using Microsoft.Extensions.Logging;
using SqlDatabaseCheckpoints.Database;
using SqlDatabaseCheckpoints.Services;

namespace SqlDatabaseCheckpoints.Services;

/// <summary>
/// High-level service for initializing the checkpoint system on a database.
/// Enables CDC, installs metadata schema, and sets up TRUNCATE detection.
/// </summary>
public class SqlServerService
{
    private readonly SqlServerConnectionFactory _factory;
    private readonly ChangeTrackingService _changeTrackingService;
    private readonly MetadataService _metadataService;
    private readonly SqlServerChangeReader _changeReader;
    private readonly ILogger<SqlServerService> _logger;

    public SqlServerService(
        SqlServerConnectionFactory factory,
        ChangeTrackingService changeTrackingService,
        MetadataService metadataService,
        SqlServerChangeReader changeReader,
        ILogger<SqlServerService> logger)
    {
        _factory = factory;
        _changeTrackingService = changeTrackingService;
        _metadataService = metadataService;
        _changeReader = changeReader;
        _logger = logger;
    }

    /// <summary>
    /// Initializes the checkpoint system on the target database.
    /// Must be called once before creating the first checkpoint.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Initializing checkpoint system...");

        // 1. Ensure metadata schema exists
        await _metadataService.EnsureSchemaAsync(ct);

        // 2. Enable CDC on the database
        await _changeTrackingService.EnsureCdcEnabledOnDatabaseAsync(ct);

        // 3. Enable CDC on all user tables
        await _changeTrackingService.EnsureCdcEnabledOnAllTablesAsync(ct);

        // 4. Install TRUNCATE detection trigger
        await _changeTrackingService.EnsureTruncateDetectionAsync(ct);

        _logger.LogInformation("Checkpoint system initialized.");
    }

    /// <summary>
    /// Tests connectivity and returns basic status information.
    /// </summary>
    public async Task<(bool Connected, bool CdcEnabled, string? ErrorMessage)> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = await _factory.OpenConnectionAsync(ct);
            var cdcEnabled = await _changeReader.IsCdcEnabledOnDatabaseAsync(ct);
            return (true, cdcEnabled, null);
        }
        catch (Exception ex)
        {
            return (false, false, ex.Message);
        }
    }
}
