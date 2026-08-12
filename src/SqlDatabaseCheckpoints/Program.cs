using Microsoft.Extensions.DependencyInjection;
using SqlDatabaseCheckpoints;
using SqlDatabaseCheckpoints.Configuration;
using SqlDatabaseCheckpoints.Database;
using SqlDatabaseCheckpoints.UI;

// Optional args:
//   --server <server>
//   --database <db>
//   --user <u>
//   --password <p>
string? server = null;
string? database = null;
string? userId = null;
string? password = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--server" when i + 1 < args.Length: server = args[++i]; break;
        case "--database" when i + 1 < args.Length: database = args[++i]; break;
        case "--user" when i + 1 < args.Length: userId = args[++i]; break;
        case "--password" when i + 1 < args.Length: password = args[++i]; break;
    }
}

// ─── Build DI container ───────────────────────────────────────────────────────
var config = new DatabaseConfiguration
{
    Server = server ?? "localhost",
    Database = database ?? string.Empty,
    UseIntegratedSecurity = userId is null
};

var services = new ServiceCollection()
    .AddCheckpointServices(config)
    .BuildServiceProvider();

var connectionFactory = services.GetRequiredService<SqlServerConnectionFactory>();
if (userId is not null)
    connectionFactory.SetCredentials(userId, password ?? string.Empty);
else
    connectionFactory.ClearCredentials();

// ─── Run TUI ──────────────────────────────────────────────────────────────────
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = false; cts.Cancel(); };

var tui = services.GetRequiredService<CheckpointTui>();
await tui.RunAsync(cts.Token);

return 0;
