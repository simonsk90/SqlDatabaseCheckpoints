using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlDatabaseCheckpoints.Configuration;
using SqlDatabaseCheckpoints.Database;
using SqlDatabaseCheckpoints.Services;
using SqlDatabaseCheckpoints.UI;

namespace SqlDatabaseCheckpoints;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCheckpointServices(
        this IServiceCollection services,
        DatabaseConfiguration config)
    {
        services.AddSingleton(config);
        services.AddSingleton<SqlServerConnectionFactory>();
        services.AddSingleton<SqlServerChangeReader>();
        services.AddSingleton<SqlServerRollbackExecutor>();
        services.AddSingleton<ChangeTrackingService>();
        services.AddSingleton<MetadataService>();
        services.AddSingleton<ConnectionProfilesService>();
        services.AddSingleton<CheckpointService>();
        services.AddSingleton<RollbackService>();
        services.AddSingleton<SqlServerService>();
        services.AddSingleton<CheckpointTui>();

        services.AddLogging(b => b
            .AddConsole()
            .SetMinimumLevel(LogLevel.Warning)); // quiet by default in TUI mode

        return services;
    }
}
