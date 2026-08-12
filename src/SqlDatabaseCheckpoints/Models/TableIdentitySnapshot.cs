namespace SqlDatabaseCheckpoints.Models;

/// <summary>
/// Snapshot of a table's IDENTITY seed value taken at a checkpoint.
/// Required because CDC does not track IDENT_CURRENT; we must restore it after rollback.
/// </summary>
public class TableIdentitySnapshot
{
    public Guid CheckpointId { get; set; }
    public required string SchemaName { get; set; }
    public required string TableName { get; set; }

    /// <summary>The IDENT_CURRENT value at checkpoint creation time. Null if table has no identity column.</summary>
    public long? IdentityCurrentValue { get; set; }

    public string FullTableName => $"[{SchemaName}].[{TableName}]";
}
