# SqlDatabaseCheckpoints — Design Document

## 1. Which SQL Server Mechanism Will Be Used

**Change Data Capture (CDC)** is the primary mechanism.

CDC reads the SQL Server transaction log asynchronously via a SQL Server Agent capture job and writes full row data — including before-images — into system change tables (`cdc.<schema>_<table>_CT`). It is the only officially supported SQL Server mechanism that provides:

- Complete record of every individual INSERT/UPDATE/DELETE
- Full before-images (pre-change column values) for UPDATE and DELETE
- LSN-based ordering that groups changes atomically by transaction
- No disruption to existing application connections

**Change Tracking (CT) was evaluated and rejected.** CT stores only the primary key of changed rows, not column values. Before-images are entirely absent. Multiple changes to the same row between two versions are collapsed to one entry. CT cannot support rollback — Microsoft's own documentation states: "If an application requires information about all the changes that were made and the intermediate values of the changed data, using change data capture, instead of change tracking, might be appropriate."

**Transaction log (`sys.fn_dblog`) was evaluated and rejected.** While theoretically complete, `fn_dblog` is an undocumented, unsupported internal function whose output format is not guaranteed across versions. Its raw before/after images are in SQL Server's internal binary storage format, requiring reverse-engineering of null bitmaps, variable-length column layouts, sparse columns, and compression. Commercial tools (Apex SQL Log, Redgate SQL Log Rescue) exist precisely because this is enormously hard to do correctly. Not viable.

**Temporal tables were evaluated as supplemental only.** They require `ALTER TABLE … WITH (SYSTEM_VERSIONING = ON)` on every tracked table (a schema change), cannot correlate cross-table transactions by transaction ID (only by timestamp), and cannot track tables without a primary key. They are excellent for per-table point-in-time recovery if you control the schema, but are not a drop-in replacement for CDC in a general-purpose tool.

---

## 2. Why CDC Can Reconstruct Previous Database States

CDC records every DML operation as a separate row in capture tables, ordered by Log Sequence Number (LSN) — the position in the transaction log. The `__$operation` column takes values:

| Value | Meaning |
|-------|---------|
| 1 | DELETE — all columns contain the deleted row's values |
| 2 | INSERT — all columns contain the inserted row's values |
| 3 | UPDATE before-image — all columns contain the row's values **before** the update |
| 4 | UPDATE after-image — all columns contain the row's values **after** the update |

By querying `cdc.fn_cdc_get_all_changes_<capture_instance>(@from_lsn, @to_lsn, 'all update old')`, the tool retrieves every change between two LSN markers in chronological order, with full column data. Processing these changes in **reverse LSN order** and applying **inverse operations** restores the database to its earlier state.

A checkpoint is stored as:

```
Checkpoint A → LSN_A = sys.fn_cdc_get_max_lsn() at the moment of creation
Checkpoint B → LSN_B
Checkpoint C → LSN_C
```

To roll back from C to A, the tool reads all CDC rows with LSN in `(LSN_A, LSN_C]` and applies them in reverse.

---

## 3. How an INSERT Is Reversed

CDC records an INSERT as one row with `__$operation = 2`, containing the inserted values.

**Inverse:** `DELETE FROM <table> WHERE <primary_key_columns> = <pk_values>`

If the primary key is an IDENTITY column, the row is simply deleted. If the same PK was then re-inserted after the checkpoint, the tool processes changes in reverse chronological order, so the later INSERT is undone first.

---

## 4. How an UPDATE Is Reversed

CDC records an UPDATE as two consecutive rows with the same `__$seqval`:
- `__$operation = 3`: before-image (old values)
- `__$operation = 4`: after-image (new values)

When using `'all update old'`, both rows are present.

**Inverse:** Apply the before-image:
```sql
UPDATE <table>
SET col1 = <before_value_1>, col2 = <before_value_2>, ...
WHERE <pk_cols> = <pk_values>
```

The after-image (op=4) is skipped during rollback — only the before-image (op=3) is needed.

---

## 5. How a DELETE Is Reversed

CDC records a DELETE as one row with `__$operation = 1`, containing all column values of the deleted row.

**Inverse:**
```sql
-- If the table has an IDENTITY column:
SET IDENTITY_INSERT <table> ON;
INSERT INTO <table> (<all columns including identity>) VALUES (<all before-image values>);
SET IDENTITY_INSERT <table> OFF;
```

The tool detects IDENTITY columns at CDC-enable time (and re-checks at checkpoint time) by querying `sys.identity_columns`.

---

## 6. How Multiple Changes to the Same Row Are Handled

CDC preserves every individual change as a separate row, ordered by `(__$start_lsn, __$seqval)`. It does **not** collapse multiple changes to the same row the way Change Tracking does.

Example — between checkpoint A and B, row Id=1 undergoes:
1. INSERT (Id=1, Name='Skoda') → CDC: op=2, seqval=0x01
2. UPDATE (Name='Peugeot') → CDC: op=3 (before: 'Skoda'), op=4 (after: 'Peugeot'), seqval=0x02
3. UPDATE (Name='Ford') → CDC: op=3 (before: 'Peugeot'), op=4 (after: 'Ford'), seqval=0x03
4. DELETE → CDC: op=1 (row: 'Ford'), seqval=0x04

During rollback (B → A), the tool processes in **reverse `(__$start_lsn DESC, __$seqval DESC)` order**:
1. op=1 (DELETE) → INSERT back 'Ford' with `IDENTITY_INSERT ON`
2. op=3 (UPDATE before 'Peugeot') → UPDATE to 'Peugeot' (skip op=4)
3. op=3 (UPDATE before 'Skoda') → UPDATE to 'Skoda' (skip op=4)
4. op=2 (INSERT) → DELETE row Id=1

Net result: row Id=1 is gone, exactly as it was at checkpoint A (before the INSERT).

---

## 7. How Transactions Are Handled

All changes committed in the same database transaction share the same `__$start_lsn`. The tool groups changes by `__$start_lsn` and processes each transaction as an atomic unit.

During rollback, all inverse changes within a group are applied inside a single `BEGIN TRANSACTION … COMMIT`. If any inverse change fails, the entire rollback transaction is rolled back, leaving the database in its pre-rollback state.

The rollback of multiple checkpoints (e.g., C → A) is itself wrapped in one outer transaction where possible, or in sequential per-source-transaction atomic units in reverse order.

---

## 8. How Foreign Keys Are Handled

CDC provides full row data but does not provide the FK dependency graph. The tool must:

1. Query `sys.foreign_keys` and `sys.foreign_key_columns` at rollback time to build a topological sort of tables by FK dependency.
2. Apply DELETE inverse operations (i.e., re-INSERTs) in dependency order: parent tables first, then child tables.
3. Apply INSERT inverse operations (i.e., DELETEs) in reverse dependency order: child tables first, then parent tables.

If the FK graph has cycles, the tool temporarily disables FK constraints:
```sql
ALTER TABLE <table> NOCHECK CONSTRAINT ALL;
-- apply inverse changes
ALTER TABLE <table> WITH CHECK CHECK CONSTRAINT ALL;
```

---

## 9. How Schema Changes Are Handled

**CDC does not track DDL.** This is a known limitation.

The tool addresses this by:

1. **Capturing a full schema snapshot** at each checkpoint into its own metadata store:
   - Table list, column definitions, data types, nullability, defaults
   - Index definitions
   - Foreign key definitions
   - IDENTITY column seeds (`IDENT_CURRENT`)
   - Sequences (`sys.sequences`)

2. **Detecting schema drift** before rollback by comparing the current schema against the schema snapshot in the target checkpoint. If differences are found, the tool warns the user and requires explicit confirmation before proceeding.

3. **Version 1 explicitly does NOT automatically reverse schema changes** (ALTER TABLE, DROP TABLE, CREATE TABLE). DDL rollback is a roadmap item. Detected DDL drift causes the rollback to be blocked with a clear error message.

4. **TRUNCATE TABLE detection:** CDC does not capture TRUNCATE. The tool installs a DDL event trigger on the target database to detect and log TRUNCATE events:
   ```sql
   CREATE TRIGGER trg_DetectTruncate
   ON DATABASE
   FOR TRUNCATE_TABLE
   AS
   BEGIN
       INSERT INTO SqlCheckpoints.TruncateLog (TableName, EventTime)
       SELECT EVENTDATA().value('(/EVENT_INSTANCE/ObjectName)[1]', 'nvarchar(128)'),
              GETUTCDATE();
   END;
   ```
   If a TRUNCATE is detected between two checkpoints, rollback is blocked with an explicit error.

---

## 10. How Rollback Works While Application Connections Remain Active

CDC operates entirely asynchronously. The capture job reads the transaction log independently; it does not acquire table locks and does not interfere with application transactions.

The rollback itself is executed as a normal database transaction. SQL Server's standard row-level locking applies:
- The rollback transaction acquires row locks on affected rows.
- If the application holds a conflicting lock on the same row, the rollback waits (subject to `SET LOCK_TIMEOUT`).
- The application can continue reading and writing unaffected rows.

The application's connection to `Database=MyDb` remains valid throughout. No connection killing, no SINGLE_USER mode, no database rename. The application will see the data transition atomically when the rollback transaction commits.

**One genuine limitation:** If the application has an active open transaction that touches the same rows as the rollback, both will contend for locks. The rollback can be configured with a lock timeout; if it cannot acquire all locks within the timeout, it rolls back itself (database left unchanged) and the tool reports the conflict.

---

## 11. Known Limitations

| Limitation | Impact | Status |
|------------|--------|--------|
| **TRUNCATE TABLE not captured by CDC** | If app truncates a table, CDC misses it; rollback produces wrong result | Detected via DDL trigger; rollback blocked with explicit error |
| **DDL changes not auto-reversed** | Schema changes between checkpoints cannot be automatically undone | Detected via schema snapshot comparison; rollback blocked with explicit error |
| **Tables without primary keys** | CDC captures changes but UPDATE reversal requires a PK to identify the target row | Tables without PKs are flagged as "partially supported"; INSERTs/DELETEs work, UPDATEs may not |
| **LOB columns in before-images** | For `image`/`text`/`ntext` data types, CDC returns NULL for op=3 (UPDATE before-image) | Tool warns and flags these columns; for `varchar(max)`/`nvarchar(max)`/`varbinary(max)`, CDC provides the before-image only if the column was actually modified |
| **CDC capture latency** | The capture job reads the log asynchronously; checkpoint LSN is the last processed LSN, not the last committed LSN | Checkpoint creation waits for CDC to catch up (polls `sys.fn_cdc_get_max_lsn()` vs current commit LSN) |
| **CDC retention expiry** | If the CDC cleanup job runs before rollback and purges data for an old checkpoint, rollback is impossible | Tool checks `sys.fn_cdc_get_min_lsn()` before rollback; recommends setting retention to `≥ max checkpoint age` |
| **Computed columns** | CDC returns NULL for non-persisted computed columns in before-images | Non-persisted computed columns are excluded from rollback INSERT/UPDATE; they are recomputed by SQL Server |
| **Triggers** | Rollback DML may fire application triggers | Rollback should disable triggers on affected tables during execution (configurable) |
| **SQL Server Agent required** | CDC requires SQL Server Agent to be running for the capture job | Tool checks Agent status at startup and reports clearly if not running |
| **Requires ALTER permission** | Enabling CDC on a table requires `db_owner` or `sysadmin` | Tool documents required permissions |

---

## Checkpoint Data Model

```
SqlCheckpoints.Checkpoint
    Id              UNIQUEIDENTIFIER PK
    Name            NVARCHAR(255)
    DatabaseName    NVARCHAR(255)
    ServerName      NVARCHAR(255)
    CreatedAt       DATETIME2
    CdcLsn          BINARY(10)          -- sys.fn_cdc_get_max_lsn() at creation time
    ParentId        UNIQUEIDENTIFIER    -- NULL for root checkpoint
    IsAutoSafety    BIT                 -- 1 = auto-created before a rollback
    Notes           NVARCHAR(MAX)

SqlCheckpoints.TableSnapshot
    CheckpointId    UNIQUEIDENTIFIER FK
    SchemaName      NVARCHAR(128)
    TableName       NVARCHAR(128)
    IdentityCurrentValue BIGINT         -- IDENT_CURRENT at checkpoint time
    ColumnDefinitionsJson NVARCHAR(MAX) -- JSON snapshot of column metadata

SqlCheckpoints.TruncateLog
    Id              INT IDENTITY PK
    TableName       NVARCHAR(255)
    EventTime       DATETIME2
    DetectedBy      NVARCHAR(255)
```

---

## Rollback Procedure (Detailed)

```
Input: target_checkpoint (A), current_checkpoint (C)

1. Verify:
   a. CDC is enabled and Agent is running
   b. sys.fn_cdc_get_min_lsn() <= LSN(A)   [CDC data not expired]
   c. No TRUNCATE events between LSN(A) and LSN(C)
   d. Schema at LSN(A) matches current schema (warn if not)

2. Auto-safety checkpoint:
   Create a checkpoint named "auto-safety-before-rollback-to-<A>" at the current state

3. Build inverse change set:
   For each tracked table:
     changes = cdc.fn_cdc_get_all_changes_<table>(LSN(A), LSN(C), 'all update old')
     Sort changes by (__$start_lsn DESC, __$seqval DESC)

4. Display summary to user:
   - N INSERTs to undo (→ DELETE)
   - M UPDATEs to reverse
   - K DELETEs to restore (→ INSERT)
   - Require explicit [y/N] confirmation

5. Execute rollback:
   BEGIN TRANSACTION;
   SET XACT_ABORT ON;

   For each change in reverse order:
     op=2 (INSERT → undo with DELETE):
       DELETE FROM <table> WHERE <pk> = <pk_values>
     op=3 (UPDATE before-image → restore):
       UPDATE <table> SET col1=val1, ... WHERE <pk> = <pk_values>
     op=4 (UPDATE after-image → skip)
     op=1 (DELETE → restore with INSERT):
       SET IDENTITY_INSERT <table> ON (if needed)
       INSERT INTO <table> VALUES (...)
       SET IDENTITY_INSERT <table> OFF

   COMMIT;

6. Restore IDENTITY seeds:
   DBCC CHECKIDENT ('<table>', RESEED, <snapshot_value>)

7. Update tool's current checkpoint pointer to A
```

---

## LSN Handling

`sys.fn_cdc_get_max_lsn()` returns `binary(10)`. The tool stores this as a hex string (20 hex chars) in the metadata for portability.

LSN comparison: `sys.fn_cdc_compare_lsn(@lsn1, @lsn2)` returns -1, 0, or 1.

LSN arithmetic for range queries: `sys.fn_cdc_increment_lsn(@lsn)` returns the next LSN value.

---

## Performance Goals

- **Checkpoint creation:** O(1) — records a single LSN value plus a schema snapshot. Near-instant.
- **Rollback:** O(number of changes between checkpoints) — proportional to CDC rows, not database size.
- **Metadata storage:** Tiny — one LSN + schema snapshot per checkpoint. No full data copy.
- **CDC overhead:** Asynchronous log reading; minimal impact on application DML throughput.
