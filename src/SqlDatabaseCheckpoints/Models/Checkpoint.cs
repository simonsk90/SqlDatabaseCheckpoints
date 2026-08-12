namespace SqlDatabaseCheckpoints.Models;

public class Checkpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string DatabaseName { get; set; }
    public required string ServerName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// CDC LSN (binary 10 bytes) encoded as 20-char uppercase hex string.
    /// This is the value of sys.fn_cdc_get_max_lsn() at checkpoint creation time.
    /// </summary>
    public required string CdcLsnHex { get; set; }

    public Guid? ParentId { get; set; }

    /// <summary>
    /// True if this checkpoint was auto-created as a safety backup before a rollback.
    /// </summary>
    public bool IsAutoSafety { get; set; }

    public string? Notes { get; set; }
}
