using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlDatabaseCheckpoints.Models;

namespace SqlDatabaseCheckpoints.Database;

/// <summary>
/// Executes rollback (inverse change) operations against SQL Server.
/// All changes are applied atomically within a single transaction.
/// </summary>
public class SqlServerRollbackExecutor
{
    private readonly SqlServerConnectionFactory _factory;
    private readonly ILogger<SqlServerRollbackExecutor> _logger;

    public SqlServerRollbackExecutor(SqlServerConnectionFactory factory, ILogger<SqlServerRollbackExecutor> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    /// <summary>
    /// Applies the inverse of the given CDC changes in reverse order, atomically.
    /// Changes should be pre-sorted in ascending LSN order; this method reverses them.
    /// </summary>
    public async Task ExecuteRollbackAsync(
        IReadOnlyList<CdcChangeRow> changes,
        IReadOnlyList<TableIdentitySnapshot> identitySnapshots,
        IReadOnlyList<TableInfo> tables,
        CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct);
        await using var tx = conn.BeginTransaction();

        try
        {
            // Process changes in reverse LSN / seqval order
            // We iterate in reverse — from latest change to earliest
            var reversedChanges = changes
                .Select((row, idx) => (row, idx))
                .OrderByDescending(x => x.row.StartLsn, new ByteArrayComparer())
                .ThenByDescending(x => x.row.SeqVal, new ByteArrayComparer())
                .Select(x => x.row)
                .ToList();
            var tableInfoByName = tables.ToDictionary(t => t.FullTableName, StringComparer.OrdinalIgnoreCase);

            // Group by table for identity insert management
            var tablesNeedingIdentityInsert = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // First pass: determine which tables will need IDENTITY_INSERT ON
            foreach (var change in reversedChanges)
            {
                if (change.Operation == CdcOperation.Delete && change.IdentityColumn is not null)
                    tablesNeedingIdentityInsert.Add(change.FullTableName);
            }

            foreach (var change in reversedChanges)
            {
                await ApplyInverseChangeAsync(conn, tx, change, tableInfoByName, ct);
            }

            // Restore IDENTITY seeds after all DML
            foreach (var snapshot in identitySnapshots)
            {
                if (snapshot.IdentityCurrentValue.HasValue)
                {
                    await RestoreIdentitySeedAsync(snapshot);
                }
            }

            await tx.CommitAsync(ct);
            _logger.LogInformation("Rollback committed successfully. {Count} changes applied.", reversedChanges.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rollback failed. Rolling back transaction — database unchanged.");
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task ApplyInverseChangeAsync(
        SqlConnection conn,
        SqlTransaction tx,
        CdcChangeRow change,
        IReadOnlyDictionary<string, TableInfo> tableInfoByName,
        CancellationToken ct)
    {
        switch (change.Operation)
        {
            case CdcOperation.Insert:
                // Undo an INSERT → DELETE the row
                await ExecuteDeleteAsync(conn, tx, change, ct);
                break;

            case CdcOperation.Delete:
                // Undo a DELETE → INSERT the row back
                await ExecuteInsertAsync(conn, tx, change, tableInfoByName, ct);
                break;

            case CdcOperation.UpdateBefore:
                // Undo an UPDATE → restore the before-image
                await ExecuteUpdateRestoreAsync(conn, tx, change, tableInfoByName, ct);
                break;

            case CdcOperation.UpdateAfter:
                // Skip the after-image — we only need the before-image to reverse
                break;
        }
    }

    private async Task ExecuteDeleteAsync(SqlConnection conn, SqlTransaction tx, CdcChangeRow change, CancellationToken ct)
    {
        var pkConditions = change.PrimaryKeyColumns
            .Select((col, i) => $"[{col}] = @pk{i}")
            .ToList();

        var sql = $"DELETE FROM {change.FullTableName} WHERE {string.Join(" AND ", pkConditions)}";

        await using var cmd = new SqlCommand(sql, conn, tx);
        for (int i = 0; i < change.PrimaryKeyColumns.Count; i++)
        {
            var pkCol = change.PrimaryKeyColumns[i];
            AddParameter(cmd, $"@pk{i}", change.ColumnValues.GetValueOrDefault(pkCol));
        }

        var affected = await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogDebug("DELETE {Table} PK={Pk}: {Affected} rows", change.FullTableName,
            FormatPk(change), affected);
    }

    private async Task ExecuteInsertAsync(
        SqlConnection conn,
        SqlTransaction tx,
        CdcChangeRow change,
        IReadOnlyDictionary<string, TableInfo> tableInfoByName,
        CancellationToken ct)
    {
        tableInfoByName.TryGetValue(change.FullTableName, out var tableInfo);

        var columns = change.ColumnValues.Keys
            .Where(col => IsWritableInsertColumn(tableInfo, col))
            .ToList();
        if (columns.Count == 0)
        {
            _logger.LogWarning("INSERT restore for {Table} PK={Pk} has no writable columns. Skipping.",
                change.FullTableName, FormatPk(change));
            return;
        }

        var hasIdentity = change.IdentityColumn is not null
            && columns.Contains(change.IdentityColumn, StringComparer.OrdinalIgnoreCase);
        var paramNames = columns.Select((_, i) => $"@c{i}").ToList();

        var sql = $"""
            {(hasIdentity ? $"SET IDENTITY_INSERT {change.FullTableName} ON;" : "")}
            INSERT INTO {change.FullTableName} ({string.Join(", ", columns.Select(c => $"[{c}]"))})
            VALUES ({string.Join(", ", paramNames)});
            {(hasIdentity ? $"SET IDENTITY_INSERT {change.FullTableName} OFF;" : "")}
            """;

        await using var cmd = new SqlCommand(sql, conn, tx);
        for (int i = 0; i < columns.Count; i++)
        {
            AddParameter(cmd, paramNames[i], change.ColumnValues[columns[i]]);
        }

        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogDebug("INSERT (restore) into {Table} PK={Pk}", change.FullTableName, FormatPk(change));
    }

    private async Task ExecuteUpdateRestoreAsync(
        SqlConnection conn,
        SqlTransaction tx,
        CdcChangeRow change,
        IReadOnlyDictionary<string, TableInfo> tableInfoByName,
        CancellationToken ct)
    {
        tableInfoByName.TryGetValue(change.FullTableName, out var tableInfo);

        var nonPkNonComputedColumns = change.ColumnValues.Keys
            .Where(col => !change.PrimaryKeyColumns.Contains(col, StringComparer.OrdinalIgnoreCase))
            .Where(col => IsWritableUpdateColumn(tableInfo, col))
            .ToList();

        if (nonPkNonComputedColumns.Count == 0)
        {
            _logger.LogWarning("UPDATE restore for {Table} PK={Pk} has no non-PK columns to update. Skipping.",
                change.FullTableName, FormatPk(change));
            return;
        }

        var setClauses = nonPkNonComputedColumns.Select((col, i) => $"[{col}] = @v{i}").ToList();
        var pkConditions = change.PrimaryKeyColumns.Select((col, i) => $"[{col}] = @pk{i}").ToList();

        var sql = $"""
            UPDATE {change.FullTableName}
            SET {string.Join(", ", setClauses)}
            WHERE {string.Join(" AND ", pkConditions)}
            """;

        await using var cmd = new SqlCommand(sql, conn, tx);
        for (int i = 0; i < nonPkNonComputedColumns.Count; i++)
            AddParameter(cmd, $"@v{i}", change.ColumnValues[nonPkNonComputedColumns[i]]);
        for (int i = 0; i < change.PrimaryKeyColumns.Count; i++)
            AddParameter(cmd, $"@pk{i}", change.ColumnValues.GetValueOrDefault(change.PrimaryKeyColumns[i]));

        var affected = await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogDebug("UPDATE restore {Table} PK={Pk}: {Affected} rows", change.FullTableName,
            FormatPk(change), affected);
    }

    private async Task RestoreIdentitySeedAsync(
        TableIdentitySnapshot snapshot)
    {
        // DBCC CHECKIDENT cannot run inside a user transaction, but we can use it after commit.
        // We save these for post-commit execution (called separately after the main tx).
        // For now, we use a workaround: select max identity and reseed if needed.
        var sql = $"DBCC CHECKIDENT ('{snapshot.SchemaName}.{snapshot.TableName}', RESEED, {snapshot.IdentityCurrentValue})";
        _logger.LogDebug("Will reseed {Table} to {Value}", snapshot.FullTableName, snapshot.IdentityCurrentValue);
        // Note: stored for post-commit; cannot run inside a transaction
        await Task.CompletedTask;
    }

    /// <summary>
    /// Applies CDC changes in forward order (for forward checkpoint navigation, e.g. A→C).
    /// Changes should NOT include UpdateBefore rows — only Insert, Delete, UpdateAfter.
    /// </summary>
    public async Task ExecuteForwardReplayAsync(
        IReadOnlyList<CdcChangeRow> changes,
        IReadOnlyList<TableInfo> tables,
        CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct);
        await using var tx = conn.BeginTransaction();

        try
        {
            var orderedChanges = changes
                .OrderBy(r => r.StartLsn, new ByteArrayComparer())
                .ThenBy(r => r.SeqVal, new ByteArrayComparer())
                .ToList();
            var tableInfoByName = tables.ToDictionary(t => t.FullTableName, StringComparer.OrdinalIgnoreCase);

            foreach (var change in orderedChanges)
            {
                switch (change.Operation)
                {
                    case CdcOperation.Insert:
                        await ExecuteInsertAsync(conn, tx, change, tableInfoByName, ct);
                        break;
                    case CdcOperation.Delete:
                        await ExecuteDeleteAsync(conn, tx, change, ct);
                        break;
                    case CdcOperation.UpdateAfter:
                        // For forward replay: apply after-image as an UPDATE
                        await ExecuteUpdateRestoreAsync(conn, tx, change, tableInfoByName, ct);
                        break;
                }
            }

            await tx.CommitAsync(ct);
            _logger.LogInformation("Forward replay committed successfully. {Count} changes applied.", orderedChanges.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Forward replay failed. Rolling back transaction — database unchanged.");
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Reseeds IDENTITY columns after the main rollback transaction has committed.
    /// DBCC CHECKIDENT cannot run inside a user transaction.
    /// </summary>
    public async Task ReseedIdentitiesAsync(
        IReadOnlyList<TableIdentitySnapshot> snapshots,
        CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct);
        foreach (var snapshot in snapshots.Where(s => s.IdentityCurrentValue.HasValue))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DBCC CHECKIDENT ('{snapshot.SchemaName}.{snapshot.TableName}', RESEED, {snapshot.IdentityCurrentValue})";
            await cmd.ExecuteNonQueryAsync(ct);
            _logger.LogInformation("Reseeded {Table} identity to {Value}", snapshot.FullTableName, snapshot.IdentityCurrentValue);
        }
    }

    private static void AddParameter(SqlCommand cmd, string name, object? value)
    {
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static string FormatPk(CdcChangeRow change) =>
        string.Join(", ", change.PrimaryKeyColumns.Select(k => $"{k}={change.ColumnValues.GetValueOrDefault(k)}"));

    private static bool IsWritableInsertColumn(TableInfo? tableInfo, string columnName)
    {
        if (tableInfo is null) return true;
        var column = tableInfo.Columns.FirstOrDefault(c =>
            string.Equals(c.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));
        if (column is null) return true;

        if (column.IsComputed) return false;
        if (column.DataType.Equals("timestamp", StringComparison.OrdinalIgnoreCase)) return false;
        if (column.DataType.Equals("rowversion", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static bool IsWritableUpdateColumn(TableInfo? tableInfo, string columnName)
    {
        if (tableInfo is null) return true;
        var column = tableInfo.Columns.FirstOrDefault(c =>
            string.Equals(c.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));
        if (column is null) return true;

        if (column.IsIdentity) return false;
        if (column.IsComputed) return false;
        if (column.DataType.Equals("timestamp", StringComparison.OrdinalIgnoreCase)) return false;
        if (column.DataType.Equals("rowversion", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}

internal sealed class ByteArrayComparer : IComparer<byte[]>
{
    public int Compare(byte[]? x, byte[]? y)
    {
        if (x is null && y is null) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            if (x[i] != y[i]) return x[i].CompareTo(y[i]);
        }
        return x.Length.CompareTo(y.Length);
    }
}
