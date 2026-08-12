using Microsoft.Data.SqlClient;
using SqlDatabaseCheckpoints.Configuration;

namespace SqlDatabaseCheckpoints.Database;

public class SqlServerConnectionFactory
{
    private readonly DatabaseConfiguration _config;
    private string? _userId;
    private string? _password;

    public SqlServerConnectionFactory(DatabaseConfiguration config)
    {
        _config = config;
    }

    public void SetCredentials(string userId, string password)
    {
        _userId = userId;
        _password = password;
    }

    public void ClearCredentials()
    {
        _userId = null;
        _password = null;
    }

    public SqlConnection CreateConnection() =>
        new(_config.BuildConnectionString(_userId, _password));

    public async Task<SqlConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        var conn = CreateConnection();
        await conn.OpenAsync(ct);
        return conn;
    }
}
