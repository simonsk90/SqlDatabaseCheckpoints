namespace SqlDatabaseCheckpoints.Models;

public class TableInfo
{
    public required string SchemaName { get; set; }
    public required string TableName { get; set; }
    public required List<string> PrimaryKeyColumns { get; set; }
    public string? IdentityColumn { get; set; }
    public required List<ColumnInfo> Columns { get; set; }

    public string FullTableName => $"[{SchemaName}].[{TableName}]";
    public string CaptureInstance => $"{SchemaName}_{TableName}";
}

public class ColumnInfo
{
    public required string ColumnName { get; set; }
    public required string DataType { get; set; }
    public bool IsNullable { get; set; }
    public bool IsIdentity { get; set; }
    public bool IsComputed { get; set; }
    public int OrdinalPosition { get; set; }
}
