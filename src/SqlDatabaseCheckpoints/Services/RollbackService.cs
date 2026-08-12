using Microsoft.Extensions.Logging;
using SqlDatabaseCheckpoints.Database;
using SqlDatabaseCheckpoints.Models;

namespace SqlDatabaseCheckpoints.Services;

/// <summary>
/// Orchestrates the rollback of a database to a prior checkpoint.
/// </summary>
public class RollbackService
{
    private readonly SqlServerChangeReader _changeReader;
    private readonly SqlServerRollbackExecutor _executor;
    private readonly ChangeTrackingService _changeTrackingService;
    private readonly MetadataService _metadataService;
    private readonly CheckpointService _checkpointService;
    private readonly ILogger<RollbackService> _logger;

    public RollbackService(
        SqlServerChangeReader changeReader,
        SqlServerRollbackExecutor executor,
        ChangeTrackingService changeTrackingService,
        MetadataService metadataService,
        CheckpointService checkpointService,
        ILogger<RollbackService> logger)
    {
        _changeReader = changeReader;
        _executor = executor;
        _changeTrackingService = changeTrackingService;
        _metadataService = metadataService;
        _checkpointService = checkpointService;
        _logger = logger;
    }

    /// <summary>
    /// Builds a rollback plan by examining what CDC data exists between the two checkpoints.
    /// Does NOT modify the database.
    /// </summary>
    public async Task<RollbackPlan> BuildRollbackPlanAsync(
        Checkpoint fromCheckpoint,
        Checkpoint targetCheckpoint,
        CancellationToken ct = default)
    {
        var plan = new RollbackPlan
        {
            FromCheckpoint = fromCheckpoint,
            TargetCheckpoint = targetCheckpoint
        };

        var targetLsn = Convert.FromHexString(targetCheckpoint.CdcLsnHex);
        var fromLsn = Convert.FromHexString(fromCheckpoint.CdcLsnHex);

        // Check direction: from > target means rolling back (forward rollback also supported)
        var lsnComparison = SqlServerChangeReader.CompareLsn(fromLsn, targetLsn);
        if (lsnComparison == 0)
        {
            plan.Blockers.Add("Source and target checkpoints are at the same LSN. Nothing to roll back.");
            return plan;
        }

        // Determine LSN range (always use lower as from, upper as to for CDC query)
        var (lsnFrom, lsnTo) = lsnComparison < 0
            ? (fromLsn, targetLsn)   // forward (from A to B)
            : (targetLsn, fromLsn);  // backward (from C to A)

        var trackedTables = await _changeReader.GetCdcEnabledTablesAsync(ct);

        foreach (var captureInstance in trackedTables)
        {
            // Skip our own metadata tables
            if (captureInstance.StartsWith("SqlCheckpoints_", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = captureInstance.Split('_', 2);
            if (parts.Length != 2) continue;

            var (schema, table) = (parts[0], parts[1]);

            // Validate that CDC data for target checkpoint hasn't been cleaned up
            try
            {
                var minLsn = await _changeReader.GetMinLsnAsync(captureInstance, ct);
                if (SqlServerChangeReader.CompareLsn(minLsn, lsnFrom) > 0)
                {
                    plan.Blockers.Add(
                        $"CDC data for table [{schema}].[{table}] has been cleaned up beyond the target checkpoint LSN. " +
                        $"Rollback to checkpoint '{targetCheckpoint.Name}' is impossible. " +
                        "Increase CDC retention or delete old checkpoints.");
                    continue;
                }
            }
            catch (Exception ex)
            {
                plan.Warnings.Add($"Could not verify CDC min LSN for [{schema}].[{table}]: {ex.Message}");
                continue;
            }

            var tableInfo = await _changeTrackingService.GetTableInfoAsync(schema, table, ct);
            List<CdcChangeRow> changes;
            try
            {
                changes = await _changeReader.GetAllChangesAsync(tableInfo, lsnFrom, lsnTo, ct);
            }
            catch (Exception ex)
            {
                plan.Warnings.Add($"Could not read CDC changes for [{schema}].[{table}]: {ex.Message}");
                continue;
            }

            foreach (var change in changes)
            {
                switch (change.Operation)
                {
                    case CdcOperation.Insert:
                        plan.InsertsToUndo++;
                        break;
                    case CdcOperation.Delete:
                        plan.DeletesToRestore++;
                        break;
                    case CdcOperation.UpdateBefore:
                        plan.UpdatesToReverse++;
                        break;
                }
            }
        }

        return plan;
    }

    /// <summary>
    /// Executes the rollback atomically.
    /// Creates an auto-safety checkpoint first, then applies inverse changes.
    /// </summary>
    public async Task ExecuteRollbackAsync(
        Checkpoint fromCheckpoint,
        Checkpoint targetCheckpoint,
        string serverName,
        string databaseName,
        CancellationToken ct = default,
        bool createAutoSafetyCheckpoint = true)
    {
        _logger.LogInformation("Starting rollback from '{From}' to '{Target}'...",
            fromCheckpoint.Name, targetCheckpoint.Name);

        var fromLsnBytes = Convert.FromHexString(fromCheckpoint.CdcLsnHex);
        var targetLsnBytes = Convert.FromHexString(targetCheckpoint.CdcLsnHex);

        var lsnComparison = SqlServerChangeReader.CompareLsn(fromLsnBytes, targetLsnBytes);
        if (lsnComparison == 0)
            throw new InvalidOperationException("Source and target checkpoints are at the same LSN.");

        var (lsnFrom, lsnTo, isBackward) = lsnComparison > 0
            ? (targetLsnBytes, fromLsnBytes, true)   // rolling backward (C → A): reverse changes from A..C
            : (fromLsnBytes, targetLsnBytes, false);  // rolling forward (A → C): apply changes A..C

        if (createAutoSafetyCheckpoint)
        {
            // Create auto-safety checkpoint at current state before modifying anything.
            // When rollback starts from a synthetic "(current)" checkpoint, its Id is Guid.Empty
            // and cannot be used as a FK parent. In that case, fall back to its ParentId.
            var safetyParentId = fromCheckpoint.Id != Guid.Empty
                ? fromCheckpoint.Id
                : fromCheckpoint.ParentId;

            var safetyName = $"auto-safety-before-rollback-to-{targetCheckpoint.Name}-{DateTime.UtcNow:yyyyMMddHHmmss}";
            await _checkpointService.CreateCheckpointAsync(
                safetyName, serverName, databaseName,
                parentId: safetyParentId,
                isAutoSafety: true, ct: ct);
        }

        // Collect all changes across all tracked tables
        var trackedTables = await _changeReader.GetCdcEnabledTablesAsync(ct);
        var allChanges = new List<CdcChangeRow>();
        var allTableInfos = new List<TableInfo>();

        foreach (var captureInstance in trackedTables)
        {
            if (captureInstance.StartsWith("SqlCheckpoints_", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = captureInstance.Split('_', 2);
            if (parts.Length != 2) continue;

            var (schema, table) = (parts[0], parts[1]);
            var tableInfo = await _changeTrackingService.GetTableInfoAsync(schema, table, ct);
            allTableInfos.Add(tableInfo);

            var changes = await _changeReader.GetAllChangesAsync(tableInfo, lsnFrom, lsnTo, ct);

            if (!isBackward)
            {
                // Forward replay: apply changes in forward chronological order
                // (INSERT stays INSERT, UPDATE after-image is the target, DELETE stays DELETE)
                // For forward movement, we actually apply the changes normally — not inverted
                // This handles the case of "checkout B" when currently at A
                allChanges.AddRange(ConvertToForwardReplay(changes));
            }
            else
            {
                allChanges.AddRange(changes);
            }
        }

        // Get identity snapshots for target checkpoint
        var identitySnapshots = await _metadataService.GetIdentitySnapshotsAsync(targetCheckpoint.Id, ct);

        if (isBackward)
        {
            // Execute inverse changes in reverse order
            await _executor.ExecuteRollbackAsync(allChanges, identitySnapshots, allTableInfos, ct);
        }
        else
        {
            // Execute forward replay
            await _executor.ExecuteForwardReplayAsync(allChanges, allTableInfos, ct);
        }

        // Reseed identities after the main transaction
        await _executor.ReseedIdentitiesAsync(identitySnapshots, ct);

        // Re-anchor the target checkpoint to the current post-rollback LSN.
        // This prevents future rollback plans from scanning all the intermediate
        // rollback operations in the CDC log — which would inflate change counts.
        if (targetCheckpoint.Id != Guid.Empty)
        {
            await _changeReader.ScanCdcAsync(ct);
            var newLsn = await _changeReader.GetMaxLsnAsync(ct);
            var newLsnHex = Convert.ToHexString(newLsn);
            await _metadataService.UpdateCheckpointLsnAsync(targetCheckpoint.Id, newLsnHex, ct);
            _logger.LogInformation("Re-anchored checkpoint '{Name}' LSN to {Lsn}.", targetCheckpoint.Name, newLsnHex);
        }

        _logger.LogInformation("Rollback complete.");
    }

    /// <summary>
    /// Converts CDC changes for forward replay (A→C): apply INSERT as INSERT, DELETE as DELETE,
    /// UPDATE after-image as UPDATE (skip before-image).
    /// </summary>
    private static List<CdcChangeRow> ConvertToForwardReplay(List<CdcChangeRow> changes)
    {
        // For forward replay we keep: op=2 (INSERT), op=1 (DELETE), op=4 (UPDATE after = target state)
        // We skip op=3 (UPDATE before-image) since we're applying forward.
        return changes
            .Where(c => c.Operation != CdcOperation.UpdateBefore)
            .ToList();
    }
}
