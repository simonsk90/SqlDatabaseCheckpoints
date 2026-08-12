using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlDatabaseCheckpoints.Database;
using SqlDatabaseCheckpoints.Models;

namespace SqlDatabaseCheckpoints.Services;

/// <summary>
/// Manages CDC enablement and table metadata queries.
/// </summary>
public class ChangeTrackingService
{
    private readonly SqlServerConnectionFactory _factory;
    private readonly ILogger<ChangeTrackingService> _logger;

    public ChangeTrackingService(SqlServerConnectionFactory factory, ILogger<ChangeTrackingService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    /// <summary>Enables CDC on the database if not already enabled.</summary>
    public async Task EnsureCdcEnabledOnDatabaseAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        // sp_cdc_enable_db is idempotent but throws if already enabled, so check first
        cmd.CommandText = """
            DECLARE @is_enabled BIT = (SELECT is_cdc_enabled FROM sys.databases WHERE name = DB_NAME());
            IF @is_enabled = 0 OR @is_enabled IS NULL
            BEGIN
                EXEC sys.sp_cdc_enable_db;
            END
            -- Verify systranschemas was created (required by sp_cdc_enable_table)
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'systranschemas' AND schema_id = SCHEMA_ID('dbo'))
            BEGIN
                -- systranschemas is missing even though CDC flag is set — this can happen if a prior
                -- enable failed. Disable and re-enable CDC to fix the state.
                EXEC sys.sp_cdc_disable_db;
                EXEC sys.sp_cdc_enable_db;
            END
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("CDC enabled on database.");
    }

    /// <summary>Enables CDC on a specific table, creating a capture instance.</summary>
    public async Task EnsureCdcEnabledOnTableAsync(string schemaName, string tableName, CancellationToken ct = default)
    {
        var captureInstance = $"{schemaName}_{tableName}";

        await using var conn = await _factory.OpenConnectionAsync(ct);
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = """
            SELECT COUNT(*)
            FROM sys.tables t
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = 'cdc' AND t.name = 'change_tables'
            AND EXISTS (
                SELECT 1 FROM cdc.change_tables WHERE capture_instance = @capture_instance
            )
            """;
        checkCmd.Parameters.AddWithValue("@capture_instance", captureInstance);
        var exists = (int)(await checkCmd.ExecuteScalarAsync(ct))! > 0;

        if (!exists)
        {
            // Guard: verify CDC is enabled at the database level before proceeding.
            // sp_cdc_enable_table requires dbo.systranschemas which is created by sp_cdc_enable_db.
            // Run sp_cdc_enable_table in the same batch as the guard to avoid context switch issues.
            await using var enableCmd = conn.CreateCommand();
            enableCmd.CommandText = $"""
                -- Ensure systranschemas exists (created by sp_cdc_enable_db)
                IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'systranschemas' AND schema_id = SCHEMA_ID('dbo'))
                BEGIN
                    IF (SELECT is_cdc_enabled FROM sys.databases WHERE name = DB_NAME()) = 1
                        EXEC sys.sp_cdc_disable_db;
                    EXEC sys.sp_cdc_enable_db;
                END;
                -- Now enable CDC on the table
                EXEC sys.sp_cdc_enable_table
                    @source_schema = '{schemaName}',
                    @source_name   = '{tableName}',
                    @role_name     = NULL,
                    @supports_net_changes = 0;
                """;
            // Note: parameters cannot be used with sp_cdc_enable_table when called in same batch
            // schemaName and tableName come from sys.tables so are not user-supplied and safe
            await enableCmd.ExecuteNonQueryAsync(ct);
            _logger.LogInformation("CDC enabled on table [{Schema}].[{Table}]", schemaName, tableName);
        }
    }

    /// <summary>Enables CDC on all user tables in the database.</summary>
    public async Task EnsureCdcEnabledOnAllTablesAsync(CancellationToken ct = default)
    {
        var tables = await GetUserTablesAsync(ct);
        foreach (var (schema, table) in tables)
            await EnsureCdcEnabledOnTableAsync(schema, table, ct);
    }

    /// <summary>Returns all user tables in the database.</summary>
    public async Task<List<(string Schema, string Table)>> GetUserTablesAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.TABLE_SCHEMA, t.TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES t
            INNER JOIN sys.objects o ON o.name = t.TABLE_NAME
            INNER JOIN sys.schemas s ON s.schema_id = o.schema_id AND s.name = t.TABLE_SCHEMA
            WHERE t.TABLE_TYPE = 'BASE TABLE'
              AND t.TABLE_SCHEMA NOT IN ('cdc', 'SqlCheckpoints')
              AND o.is_ms_shipped = 0
            ORDER BY t.TABLE_SCHEMA, t.TABLE_NAME
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<(string, string)>();
        while (await reader.ReadAsync(ct))
            result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }

    /// <summary>Returns full TableInfo including PK, identity, and column metadata.</summary>
    public async Task<TableInfo> GetTableInfoAsync(string schemaName, string tableName, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct);

        var pkColumns = await GetPrimaryKeyColumnsAsync(conn, schemaName, tableName, ct);
        var identityColumn = await GetIdentityColumnAsync(conn, schemaName, tableName, ct);
        var columns = await GetColumnsAsync(conn, schemaName, tableName, ct);

        return new TableInfo
        {
            SchemaName = schemaName,
            TableName = tableName,
            PrimaryKeyColumns = pkColumns,
            IdentityColumn = identityColumn,
            Columns = columns
        };
    }

    private static async Task<List<string>> GetPrimaryKeyColumnsAsync(
        SqlConnection conn, string schema, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.COLUMN_NAME
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            INNER JOIN INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE c
                ON c.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
                AND c.TABLE_SCHEMA = tc.TABLE_SCHEMA
                AND c.TABLE_NAME = tc.TABLE_NAME
            WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
              AND tc.TABLE_SCHEMA = @schema
              AND tc.TABLE_NAME = @table
            ORDER BY c.COLUMN_NAME
            """;
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var pks = new List<string>();
        while (await reader.ReadAsync(ct))
            pks.Add(reader.GetString(0));
        return pks;
    }

    private static async Task<string?> GetIdentityColumnAsync(
        SqlConnection conn, string schema, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.name
            FROM sys.identity_columns ic
            INNER JOIN sys.objects o ON ic.object_id = o.object_id
            INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
            INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            WHERE s.name = @schema AND o.name = @table
            """;
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DBNull or null ? null : (string)result;
    }

    private static async Task<List<ColumnInfo>> GetColumnsAsync(
        SqlConnection conn, string schema, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                c.COLUMN_NAME,
                c.DATA_TYPE,
                c.IS_NULLABLE,
                c.ORDINAL_POSITION,
                CASE WHEN ic.object_id IS NOT NULL THEN 1 ELSE 0 END AS IS_IDENTITY,
                CASE WHEN cc.object_id IS NOT NULL THEN 1 ELSE 0 END AS IS_COMPUTED
            FROM INFORMATION_SCHEMA.COLUMNS c
            INNER JOIN sys.objects o ON o.name = c.TABLE_NAME
            INNER JOIN sys.schemas s ON s.schema_id = o.schema_id AND s.name = c.TABLE_SCHEMA
            LEFT JOIN sys.identity_columns ic
                ON ic.object_id = o.object_id
                AND ic.name = c.COLUMN_NAME
            LEFT JOIN sys.computed_columns cc
                ON cc.object_id = o.object_id
                AND cc.name = c.COLUMN_NAME
            WHERE c.TABLE_SCHEMA = @schema AND c.TABLE_NAME = @table
            ORDER BY c.ORDINAL_POSITION
            """;
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var cols = new List<ColumnInfo>();
        while (await reader.ReadAsync(ct))
        {
            cols.Add(new ColumnInfo
            {
                ColumnName = reader.GetString(0),
                DataType = reader.GetString(1),
                IsNullable = reader.GetString(2) == "YES",
                OrdinalPosition = reader.GetInt32(3),
                IsIdentity = reader.GetInt32(4) == 1,
                IsComputed = reader.GetInt32(5) == 1
            });
        }
        return cols;
    }

    /// <summary>Returns the current IDENT_CURRENT for a table, or null if no identity column.</summary>
    public async Task<long?> GetIdentityCurrentAsync(string schemaName, string tableName, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct);
        await using var checkCmd = conn.CreateCommand();
        // Check if table has identity column first
        checkCmd.CommandText = """
            SELECT COUNT(*) FROM sys.identity_columns ic
            INNER JOIN sys.objects o ON ic.object_id = o.object_id
            INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE s.name = @schema AND o.name = @table
            """;
        checkCmd.Parameters.AddWithValue("@schema", schemaName);
        checkCmd.Parameters.AddWithValue("@table", tableName);
        var hasIdentity = (int)(await checkCmd.ExecuteScalarAsync(ct))! > 0;
        if (!hasIdentity) return null;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT IDENT_CURRENT('{schemaName}.{tableName}')";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DBNull or null ? null : Convert.ToInt64(result);
    }

    /// <summary>
    /// Installs a DDL trigger to detect TRUNCATE TABLE operations.
    /// Note: TRUNCATE_TABLE DDL event is not available on SQL Server for Linux.
    /// This method gracefully skips trigger creation if the event type is unsupported.
    /// </summary>
    public async Task EnsureTruncateDetectionAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct);

        // Create the TruncateLog table
        await using var schemaCmd = conn.CreateCommand();
        schemaCmd.CommandText = """
            IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'SqlCheckpoints')
                EXEC('CREATE SCHEMA SqlCheckpoints');

            IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'TruncateLog' AND schema_id = SCHEMA_ID('SqlCheckpoints'))
            BEGIN
                CREATE TABLE SqlCheckpoints.TruncateLog (
                    Id        INT IDENTITY PRIMARY KEY,
                    TableName NVARCHAR(255) NOT NULL,
                    EventTime DATETIME2     NOT NULL DEFAULT GETUTCDATE()
                );
            END
            """;
        await schemaCmd.ExecuteNonQueryAsync(ct);

        // Check if TRUNCATE_TABLE DDL event is supported (not available on SQL Server for Linux)
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = """
            SELECT COUNT(*) FROM sys.event_notification_event_types
            WHERE type_name = 'TRUNCATE_TABLE'
            """;
        var eventExists = (int)(await checkCmd.ExecuteScalarAsync(ct))! > 0;

        if (!eventExists)
        {
            _logger.LogWarning("TRUNCATE_TABLE DDL event is not supported on this SQL Server instance. TRUNCATE detection is disabled.");
            return;
        }

        // Create/replace the DDL trigger
        await using var trigCmd = conn.CreateCommand();
        trigCmd.CommandText = """
            IF EXISTS (SELECT 1 FROM sys.triggers WHERE name = 'trg_SqlCheckpoints_DetectTruncate' AND parent_class = 0)
                DROP TRIGGER trg_SqlCheckpoints_DetectTruncate ON DATABASE;
            """;
        await trigCmd.ExecuteNonQueryAsync(ct);

        await using var createTrigCmd = conn.CreateCommand();
        createTrigCmd.CommandText = """
            CREATE TRIGGER trg_SqlCheckpoints_DetectTruncate
            ON DATABASE
            FOR TRUNCATE_TABLE
            AS
            BEGIN
                SET NOCOUNT ON;
                DECLARE @tableName NVARCHAR(255) =
                    EVENTDATA().value('(/EVENT_INSTANCE/ObjectName)[1]', 'nvarchar(255)');
                INSERT INTO SqlCheckpoints.TruncateLog (TableName, EventTime)
                VALUES (@tableName, GETUTCDATE());
            END;
            """;
        await createTrigCmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("TRUNCATE detection trigger installed.");
    }

    /// <summary>Returns any TRUNCATE events recorded after a given datetime.</summary>
    public async Task<List<string>> GetTruncateEventsSinceAsync(DateTime since, CancellationToken ct = default)
    {
        try
        {
            await using var conn = await _factory.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT TableName FROM SqlCheckpoints.TruncateLog
                WHERE EventTime >= @since
                ORDER BY EventTime
                """;
            cmd.Parameters.AddWithValue("@since", since);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var result = new List<string>();
            while (await reader.ReadAsync(ct))
                result.Add(reader.GetString(0));
            return result;
        }
        catch
        {
            return [];
        }
    }
}
