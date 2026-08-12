using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using SqlDatabaseCheckpoints;
using SqlDatabaseCheckpoints.Configuration;
using SqlDatabaseCheckpoints.Database;
using SqlDatabaseCheckpoints.Services;

namespace SqlDatabaseCheckpoints.IntegrationTests;

/// <summary>
/// Base class for integration tests.
/// Creates a fresh test database for each test class, drops it on dispose.
///
/// Configure the SQL Server connection via environment variables:
///   SQLCHECKPOINTS_TEST_SERVER   (default: localhost\SQLEXPRESS or localhost for containers)
///   SQLCHECKPOINTS_TEST_USER     (optional; uses integrated security if not set)
///   SQLCHECKPOINTS_TEST_PASSWORD (optional)
/// </summary>
public abstract class IntegrationTestBase : IAsyncLifetime
{
    protected static readonly string TestServer =
        Environment.GetEnvironmentVariable("SQLCHECKPOINTS_TEST_SERVER") ?? "localhost";

    private static readonly string? TestUser =
        Environment.GetEnvironmentVariable("SQLCHECKPOINTS_TEST_USER");

    private static readonly string? TestPassword =
        Environment.GetEnvironmentVariable("SQLCHECKPOINTS_TEST_PASSWORD");

    /// <summary>Unique database name per test class to allow parallel execution.</summary>
    protected string TestDatabase { get; } = $"SqlCpTest_{Guid.NewGuid():N}";

    protected ServiceProvider Services { get; private set; } = null!;
    protected CheckpointService CheckpointService => Services.GetRequiredService<CheckpointService>();
    protected RollbackService RollbackService => Services.GetRequiredService<RollbackService>();
    protected SqlServerService SqlServerService => Services.GetRequiredService<SqlServerService>();
    protected ChangeTrackingService ChangeTrackingService => Services.GetRequiredService<ChangeTrackingService>();

    public async Task InitializeAsync()
    {
        await CreateDatabaseAsync();

        var config = new DatabaseConfiguration
        {
            Server = TestServer,
            Database = TestDatabase,
            UseIntegratedSecurity = TestUser is null,
            Pooling = false  // Disable pooling for tests; each test DB is unique and dropped after
        };

        var sc = new ServiceCollection().AddCheckpointServices(config);
        Services = sc.BuildServiceProvider();

        if (TestUser is not null)
            Services.GetRequiredService<SqlServerConnectionFactory>()
                .SetCredentials(TestUser, TestPassword ?? string.Empty);
    }

    public async Task DisposeAsync()
    {
        await Services.DisposeAsync();
        await DropDatabaseAsync();
    }

    private string MasterConnectionString()
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = TestServer,
            InitialCatalog = "master",
            TrustServerCertificate = true,
            ConnectTimeout = 30,
            Pooling = false  // Tests drop/create DBs; avoid stale pooled connections
        };
        if (TestUser is not null)
        {
            b.UserID = TestUser;
            b.Password = TestPassword ?? string.Empty;
        }
        else
        {
            b.IntegratedSecurity = true;
        }
        return b.ConnectionString;
    }

    private async Task CreateDatabaseAsync()
    {
        await using var conn = new SqlConnection(MasterConnectionString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        // Must enable SQL Server Agent and CDC; requires sysadmin for CDC on a new DB
        cmd.CommandText = $"""
            CREATE DATABASE [{TestDatabase}];
            ALTER DATABASE [{TestDatabase}] SET RECOVERY SIMPLE;
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task DropDatabaseAsync()
    {
        await using var conn = new SqlConnection(MasterConnectionString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            IF EXISTS (SELECT 1 FROM sys.databases WHERE name = '{TestDatabase}')
            BEGIN
                ALTER DATABASE [{TestDatabase}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{TestDatabase}];
            END
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Executes SQL against the test database.</summary>
    protected async Task ExecAsync(string sql, CancellationToken ct = default)
    {
        var factory = Services.GetRequiredService<SqlServerConnectionFactory>();
        await using var conn = await factory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Reads a scalar from the test database.</summary>
    protected async Task<T?> ScalarAsync<T>(string sql, CancellationToken ct = default)
    {
        var factory = Services.GetRequiredService<SqlServerConnectionFactory>();
        await using var conn = await factory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DBNull or null ? default : (T)Convert.ChangeType(result, typeof(T));
    }

    /// <summary>Returns all rows of a single-column query as strings.</summary>
    protected async Task<List<string>> QueryColumnAsync(string sql, CancellationToken ct = default)
    {
        var factory = Services.GetRequiredService<SqlServerConnectionFactory>();
        await using var conn = await factory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<string>();
        while (await reader.ReadAsync(ct))
            results.Add(reader[0]?.ToString() ?? "(null)");
        return results;
    }

    /// <summary>
    /// Initializes CDC and creates the Cars test table.
    /// Cars(Id INT IDENTITY PK, Make NVARCHAR(100)).
    /// </summary>
    protected async Task SetupCarsTableAsync(CancellationToken ct = default)
    {
        await ExecAsync("""
            CREATE TABLE dbo.Cars (
                Id   INT           IDENTITY(1,1) PRIMARY KEY,
                Make NVARCHAR(100) NOT NULL
            )
            """, ct);

        await SqlServerService.InitializeAsync(ct);
    }
}
