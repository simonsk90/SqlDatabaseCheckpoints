namespace SqlDatabaseCheckpoints.Models;

/// <summary>
/// A single change row from CDC (cdc.fn_cdc_get_all_changes_*).
/// </summary>
public class CdcChangeRow
{
    public required byte[] StartLsn { get; set; }
    public required byte[] SeqVal { get; set; }
    public CdcOperation Operation { get; set; }
    public required string SchemaName { get; set; }
    public required string TableName { get; set; }

    /// <summary>Column name → value pairs for the changed row. Nulls are represented as DBNull.Value.</summary>
    public required Dictionary<string, object?> ColumnValues { get; set; }

    /// <summary>Primary key column names for this table (cached from metadata).</summary>
    public required List<string> PrimaryKeyColumns { get; set; }

    /// <summary>Identity column name, if the table has one. Null otherwise.</summary>
    public string? IdentityColumn { get; set; }

    public string FullTableName => $"[{SchemaName}].[{TableName}]";
}

public enum CdcOperation
{
    Delete = 1,
    Insert = 2,
    UpdateBefore = 3,
    UpdateAfter = 4
}
