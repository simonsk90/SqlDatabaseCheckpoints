namespace SqlDatabaseCheckpoints.Models;

public class RollbackPlan
{
    public required Checkpoint TargetCheckpoint { get; set; }
    public required Checkpoint FromCheckpoint { get; set; }

    public int InsertsToUndo { get; set; }
    public int UpdatesToReverse { get; set; }
    public int DeletesToRestore { get; set; }

    public int TotalChanges => InsertsToUndo + UpdatesToReverse + DeletesToRestore;

    /// <summary>Warnings about schema drift, unsupported columns, etc.</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>Blocking errors that prevent rollback.</summary>
    public List<string> Blockers { get; set; } = [];

    public bool CanProceed => Blockers.Count == 0;
}
