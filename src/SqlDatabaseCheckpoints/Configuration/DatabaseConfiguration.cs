namespace SqlDatabaseCheckpoints.Configuration;

public class DatabaseConfiguration
{
    public string Server { get; set; } = "localhost";
    public string Database { get; set; } = string.Empty;

    /// <summary>
    /// Integrated Security connection string (no credentials stored).
    /// For SQL auth, pass credentials via command-line args at runtime only.
    /// </summary>
    public bool UseIntegratedSecurity { get; set; } = true;

    public bool Pooling { get; set; } = true;

    /// <summary>
    /// Connection string built at runtime — never persisted.
    /// </summary>
    public string BuildConnectionString(string? userId = null, string? password = null)
    {
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = Database,
            TrustServerCertificate = true,
            ConnectTimeout = 30,
            Pooling = Pooling
        };

        if (UseIntegratedSecurity || userId is null)
            builder.IntegratedSecurity = true;
        else
        {
            builder.UserID = userId;
            builder.Password = password ?? string.Empty;
        }

        return builder.ConnectionString;
    }
}
