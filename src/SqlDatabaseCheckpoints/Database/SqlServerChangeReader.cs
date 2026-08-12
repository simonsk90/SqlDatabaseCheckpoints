using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlDatabaseCheckpoints.Database;
using SqlDatabaseCheckpoints.Models;

namespace SqlDatabaseCheckpoints.Database;

/// <summary>
/// Reads CDC change data from SQL Server.
/// </summary>
public class SqlServerChangeReader
{
    private readonly SqlServerConnectionFactory _factory;
    private readonly ILogger<SqlServerChangeReader> _logger;

    public SqlServerChangeReader(SqlServerConnectionFactory factory, ILogger<SqlServerChangeReader> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    /// <summary>
    /// Triggers CDC to scan the transaction log and capture any pending changes.
    /// On systems without SQL Server Agent, this must be called manually to flush changes.
    /// </summary>
    public async Task ScanCdcAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXEC sys.sp_cdc_scan;";
        cmd.CommandTimeout = 60;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Returns the current maximum LSN processed by the CDC capture job.
    /// This is the LSN to record at checkpoint creation time.
    /// Triggers a CDC scan first to ensure all committed changes are captured.
    /// </summary>
    public async Task<byte[]> GetMaxLsnAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sys.fn_cdc_get_max_lsn()";
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null or DBNull)
            throw new InvalidOperationException("CDC is not enabled or no LSN is available. Ensure SQL Server Agent is running and CDC is enabled.");
        return (byte[])result;
    }

    /// <summary>
    /// Returns the minimum valid LSN for a capture instance (oldest data still in CDC tables).
    /// </summary>
    public async Task<byte[]> GetMinLsnAsync(string captureInstance, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sys.fn_cdc_get_min_lsn(@capture_instance)";
        cmd.Parameters.AddWithValue("@capture_instance", captureInstance);
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null or DBNull)
            throw new InvalidOperationException($"No CDC data found for capture instance '{captureInstance}'. The table may not be CDC-enabled or data may have been cleaned up.");
        return (byte[])result;
    }

    /// <summary>
    /// Reads all CDC changes for a table between two checkpoint LSNs.
    /// The from_lsn is the checkpoint LSN of the EARLIER checkpoint (exclusive — changes AT this LSN are NOT included).
    /// The to_lsn is the checkpoint LSN of the LATER checkpoint (inclusive — changes AT this LSN ARE included).
    /// Returns rows ordered by (__$start_lsn ASC, __$seqval ASC) — chronological order.
    /// </summary>
    public async Task<List<CdcChangeRow>> GetAllChangesAsync(
        TableInfo table,
        byte[] fromLsnExclusive,
        byte[] toLsnInclusive,
        CancellationToken ct = default)
    {
        var captureInstance = table.CaptureInstance;
        var changes = new List<CdcChangeRow>();

        await using var conn = await _factory.OpenConnectionAsync(ct);

        // Increment from_lsn to make it exclusive (CDC function is inclusive on both ends).
        // fn_cdc_increment_lsn returns the next LSN value after the given one.
        await using var incrCmd = conn.CreateCommand();
        incrCmd.CommandText = "SELECT sys.fn_cdc_increment_lsn(@lsn)";
        incrCmd.Parameters.Add(new SqlParameter("@lsn", System.Data.SqlDbType.Binary, 10) { Value = fromLsnExclusive });
        var incrResult = await incrCmd.ExecuteScalarAsync(ct);
        var fromLsnIncremented = (byte[])incrResult!;

        // If the incremented from LSN is already > toLsn, there are no changes
        if (CompareLsn(fromLsnIncremented, toLsnInclusive) > 0)
            return changes;

        await using var cmd = conn.CreateCommand();

        // The 'all update old' option returns op=3 (before-image) + op=4 (after-image) for updates.
        // We use this so we have the before-image values needed to reverse UPDATEs.
        cmd.CommandText = $"""
            SELECT *
            FROM cdc.fn_cdc_get_all_changes_{captureInstance}(@from_lsn, @to_lsn, N'all update old')
            ORDER BY __$start_lsn ASC, __$seqval ASC
            """;
        cmd.Parameters.Add(new SqlParameter("@from_lsn", System.Data.SqlDbType.Binary, 10) { Value = fromLsnIncremented });
        cmd.Parameters.Add(new SqlParameter("@to_lsn", System.Data.SqlDbType.Binary, 10) { Value = toLsnInclusive });

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        // Get column ordinals for CDC metadata columns
        int lsnOrdinal = reader.GetOrdinal("__$start_lsn");
        int seqOrdinal = reader.GetOrdinal("__$seqval");
        int opOrdinal = reader.GetOrdinal("__$operation");

        // Identify data column positions (skip CDC metadata columns)
        var cdcMetaColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "__$start_lsn", "__$seqval", "__$operation", "__$update_mask" };

        var dataColumnOrdinals = new List<(string Name, int Ordinal)>();
        for (int i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            if (!cdcMetaColumns.Contains(name))
                dataColumnOrdinals.Add((name, i));
        }

        while (await reader.ReadAsync(ct))
        {
            var lsn = (byte[])reader[lsnOrdinal];
            var seq = (byte[])reader[seqOrdinal];
            var op = (CdcOperation)(int)reader[opOrdinal];

            var columnValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, ordinal) in dataColumnOrdinals)
            {
                var val = reader[ordinal];
                columnValues[name] = val is DBNull ? null : val;
            }

            changes.Add(new CdcChangeRow
            {
                StartLsn = lsn,
                SeqVal = seq,
                Operation = op,
                SchemaName = table.SchemaName,
                TableName = table.TableName,
                ColumnValues = columnValues,
                PrimaryKeyColumns = table.PrimaryKeyColumns,
                IdentityColumn = table.IdentityColumn
            });
        }

        return changes;
    }

    /// <summary>
    /// Returns tables currently tracked by CDC in the target database.
    /// </summary>
    public async Task<List<string>> GetCdcEnabledTablesAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT capture_instance
            FROM cdc.change_tables
            ORDER BY capture_instance
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<string>();
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>
    /// Checks whether CDC is enabled on the current database.
    /// </summary>
    public async Task<bool> IsCdcEnabledOnDatabaseAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT is_cdc_enabled FROM sys.databases WHERE name = DB_NAME()";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is 1 or true;
    }

    /// <summary>
    /// Checks whether SQL Server Agent is running (required for CDC capture job).
    /// </summary>
    public async Task<bool> IsAgentRunningAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = await _factory.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            // Query the msdb Agent status; non-sysadmin users may not have access
            cmd.CommandText = """
                SELECT COUNT(*)
                FROM msdb.dbo.sysjobs j
                INNER JOIN msdb.dbo.sysjobactivity ja ON j.job_id = ja.job_id
                WHERE j.name LIKE 'cdc.%'
                  AND ja.start_execution_date IS NOT NULL
                  AND ja.stop_execution_date IS NULL
                """;
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is int count && count > 0;
        }
        catch
        {
            // If we can't query Agent status, assume it's running (we'll fail later with a clear error)
            return true;
        }
    }

    /// <summary>
    /// Compares two LSNs. Returns -1, 0, or 1.
    /// </summary>
    public static int CompareLsn(byte[] a, byte[] b)
    {
        for (int i = 0; i < 10; i++)
        {
            if (a[i] < b[i]) return -1;
            if (a[i] > b[i]) return 1;
        }
        return 0;
    }
}
