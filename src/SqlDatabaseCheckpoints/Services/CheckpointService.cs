using Microsoft.Extensions.Logging;
using SqlDatabaseCheckpoints.Database;
using SqlDatabaseCheckpoints.Models;

namespace SqlDatabaseCheckpoints.Services;

/// <summary>
/// Core service for creating checkpoints.
/// A checkpoint = LSN marker + identity snapshots + schema snapshot (future).
/// </summary>
public class CheckpointService
{
    private readonly SqlServerChangeReader _changeReader;
    private readonly ChangeTrackingService _changeTrackingService;
    private readonly MetadataService _metadataService;
    private readonly ILogger<CheckpointService> _logger;

    public CheckpointService(
        SqlServerChangeReader changeReader,
        ChangeTrackingService changeTrackingService,
        MetadataService metadataService,
        ILogger<CheckpointService> logger)
    {
        _changeReader = changeReader;
        _changeTrackingService = changeTrackingService;
        _metadataService = metadataService;
        _logger = logger;
    }

    /// <summary>
    /// Creates a checkpoint by recording the current CDC max LSN and identity seeds.
    /// This is a near-instant, lightweight operation — no data is copied.
    /// </summary>
    public async Task<Checkpoint> CreateCheckpointAsync(
        string name,
        string serverName,
        string databaseName,
        Guid? parentId = null,
        bool isAutoSafety = false,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Creating checkpoint '{Name}'...", name);

        // Flush CDC capture — on systems without SQL Server Agent, this ensures
        // all committed transactions are read from the log before we snapshot the LSN.
        await _changeReader.ScanCdcAsync(ct);

        // Capture current CDC LSN — this is the checkpoint marker
        var lsnBytes = await _changeReader.GetMaxLsnAsync(ct);
        var lsnHex = Convert.ToHexString(lsnBytes); // 20 uppercase hex chars

        var checkpoint = new Checkpoint
        {
            Name = name,
            DatabaseName = databaseName,
            ServerName = serverName,
            CreatedAt = DateTime.UtcNow,
            CdcLsnHex = lsnHex,
            ParentId = parentId,
            IsAutoSafety = isAutoSafety
        };

        await _metadataService.SaveCheckpointAsync(checkpoint, ct);

        // Capture identity seeds for all tracked tables
        var userTables = await _changeTrackingService.GetUserTablesAsync(ct);
        var identitySnapshots = new List<TableIdentitySnapshot>();
        foreach (var (schema, table) in userTables)
        {
            var current = await _changeTrackingService.GetIdentityCurrentAsync(schema, table, ct);
            identitySnapshots.Add(new TableIdentitySnapshot
            {
                CheckpointId = checkpoint.Id,
                SchemaName = schema,
                TableName = table,
                IdentityCurrentValue = current
            });
        }
        await _metadataService.SaveIdentitySnapshotsAsync(identitySnapshots, ct);

        _logger.LogInformation("Checkpoint '{Name}' created at LSN {Lsn}.", name, lsnHex);
        return checkpoint;
    }

    public Task<List<Checkpoint>> GetAllCheckpointsAsync(CancellationToken ct = default) =>
        _metadataService.GetAllCheckpointsAsync(ct);

    public Task<Checkpoint?> GetCheckpointByIdAsync(Guid id, CancellationToken ct = default) =>
        _metadataService.GetCheckpointByIdAsync(id, ct);

    public Task<Checkpoint?> GetLatestCheckpointAsync(CancellationToken ct = default) =>
        _metadataService.GetLatestCheckpointAsync(ct);

    public Task DeleteCheckpointAsync(Guid id, CancellationToken ct = default) =>
        _metadataService.DeleteCheckpointAsync(id, ct);

    /// <summary>
    /// Returns the current CDC max LSN after forcing a capture scan.
    /// Useful to represent the live database state when no new named checkpoint exists yet.
    /// </summary>
    public async Task<byte[]> GetCurrentMaxLsnAsync(CancellationToken ct = default)
    {
        await _changeReader.ScanCdcAsync(ct);
        return await _changeReader.GetMaxLsnAsync(ct);
    }
}
