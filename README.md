# SqlDatabaseCheckpoints

Git-like checkpoints and rollback for a local Microsoft SQL Server database, designed for local development workflows.

## What it does

- **Create checkpoints** of your database state — near-instant, no full data copy
- **Roll back** to any prior checkpoint atomically
- **Roll forward** to a later checkpoint
- Works while your application stays connected and running — no connection killing, no SINGLE_USER mode, no database rename

## Requirements

- .NET 9.0+
- SQL Server 2016+ (2022 recommended)
- SQL Server Agent (for automatic CDC capture; optional — `sp_cdc_scan` is used as fallback)
- `db_owner` or `sysadmin` permission to enable CDC

## Quick start

```bash
# Build
dotnet build

# Run (interactive connection picker)
dotnet run --project src/SqlDatabaseCheckpoints

# Optional direct launch with CLI overrides
dotnet run --project src/SqlDatabaseCheckpoints -- --server "localhost,1433" --database MyDb --user sa --password YourPassword
```

Saved connections (including SQL passwords) are stored locally at:

`~/.sql-database-checkpoints/connections.json`

First time: choose or create a connection, press `i` to initialize CDC on that database, then `c` to create your first checkpoint.

## CLI

```
SqlCheckpoints

Database: MyDb   Server: localhost

╭───┬─────────┬─────────────────────┬──────────────┬───────╮
│ # │ Name    │ Created             │ LSN          │ Flags │
├───┼─────────┼─────────────────────┼──────────────┼───────┤
│ 1 │ A       │ 2026-08-11 13:10:00 │ 0x0000002B…  │       │
│ 2 │ B       │ 2026-08-11 13:20:00 │ 0x0000002C…  │       │
│ 3 │ C       │ 2026-08-11 13:30:00 │ 0x0000002D…  │       │
╰───┴─────────┴─────────────────────┴──────────────┴───────╯

Commands: c=create  r=rollback(confirm)  R=rollback(no confirm)  d=delete  i=init  s=connection  o=options  q=quit
>
```

## Architecture

Uses **SQL Server Change Data Capture (CDC)** as the underlying mechanism. See [docs/DESIGN.md](docs/DESIGN.md) for the full technical design.

### Why CDC?

| Mechanism | Before-images | Multiple changes | Transaction grouping | Verdict |
|-----------|:---:|:---:|:---:|:---|
| Change Tracking | ❌ | ❌ (collapsed) | ❌ | Not viable |
| **CDC** | **✅** | **✅** | **✅ (`__$start_lsn`)** | **✅ Chosen** |
| Temporal tables | ✅ | ⚠️ | ⚠️ (timestamp) | Supplemental |
| `fn_dblog` | ✅ | ✅ | ✅ | ❌ Undocumented |

### How a checkpoint works

A checkpoint records only:
- The current `sys.fn_cdc_get_max_lsn()` value — a lightweight binary marker
- IDENTITY column seeds for all tracked tables

No data is copied. Checkpoint creation is O(1).

### How rollback works

To roll back from checkpoint C to checkpoint A:
1. Query CDC change tables for all changes between LSN_A and LSN_C
2. Apply inverse operations in reverse chronological order:
   - INSERT → DELETE
   - DELETE → re-INSERT (with `IDENTITY_INSERT ON` to preserve original ID)
   - UPDATE → restore before-image
3. Reseed IDENTITY columns to checkpoint A values
4. All changes applied atomically in a single transaction

### Metadata storage

Stored in `SqlCheckpoints` schema within the target database:

```sql
SqlCheckpoints.CheckpointEntry     -- checkpoint records
SqlCheckpoints.TableIdentitySnapshot  -- IDENTITY seeds per checkpoint
SqlCheckpoints.TruncateLog         -- TRUNCATE detection log
```

No credentials are persisted.

## Integration tests

```bash
# Set environment variables for SQL Server connection
export SQLCHECKPOINTS_TEST_SERVER="localhost,1433"
export SQLCHECKPOINTS_TEST_USER="SA"
export SQLCHECKPOINTS_TEST_PASSWORD="YourPassword"

dotnet test tests/SqlDatabaseCheckpoints.IntegrationTests/
```

Each test creates and drops its own isolated database. Requires `sysadmin` or equivalent to create databases and enable CDC.

### Test coverage

| # | Test | Scenario |
|---|------|----------|
| 1 | Insert rollback | A→B→C, rollback to A |
| 2 | Rollback to intermediate | Rollback to B (not A) |
| 3 | UPDATE rollback | Multiple updates, restore original value |
| 4 | DELETE rollback | Deleted row restored with original IDENTITY |
| 5 | Multiple changes to same row | 3 updates to same row between checkpoints |
| 6 | Forward rollback | A→C: applying changes forward |
| 7 | Auto-safety checkpoint | Safety checkpoint created before rollback |
| 8 | Open connection survives | Persistent connection usable after rollback |
| 9 | IDENTITY preserved | Restored deleted row has original ID value |
| 10 | Rollback plan accuracy | Correct INSERT/UPDATE/DELETE counts |

## Known limitations

| Limitation | Status |
|------------|--------|
| TRUNCATE TABLE not captured by CDC | Detected via DDL trigger (where supported); rollback blocked with clear error |
| DDL changes (ALTER/DROP/CREATE TABLE) | Not auto-reversed; schema snapshot diffed at rollback; rollback blocked if schema differs |
| Tables without primary keys | CDC captures data; UPDATE reversal unreliable; documented, not silently broken |
| `image`/`text`/`ntext` before-images | CDC returns NULL for op=3 on these legacy types; use `varbinary(max)` instead |
| CDC retention expiry | Checked before rollback; error if checkpoint LSN has been cleaned up |
| SQL Server Agent required for auto-capture | `sp_cdc_scan` used as fallback; works without Agent |
| TRUNCATE_TABLE DDL event (Linux) | Not available on SQL Server for Linux; TRUNCATE detection skipped gracefully |
