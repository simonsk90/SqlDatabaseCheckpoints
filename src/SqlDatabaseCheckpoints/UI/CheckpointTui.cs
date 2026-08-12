using Spectre.Console;
using Spectre.Console.Rendering;
using SqlDatabaseCheckpoints.Configuration;
using SqlDatabaseCheckpoints.Database;
using SqlDatabaseCheckpoints.Models;
using SqlDatabaseCheckpoints.Services;
using System.Text;

namespace SqlDatabaseCheckpoints.UI;

/// <summary>
/// Terminal UI for the checkpoint tool.
/// </summary>
public class CheckpointTui
{
    private const string CreateNewConnectionChoice = "+ Create new connection";
    private const string CancelChoice = "Cancel";

    private readonly CheckpointService _checkpointService;
    private readonly RollbackService _rollbackService;
    private readonly SqlServerService _serverService;
    private readonly ConnectionProfilesService _connectionProfilesService;
    private readonly SqlServerConnectionFactory _connectionFactory;
    private readonly DatabaseConfiguration _config;

    private string? _activeConnectionName;
    private Guid? _selectedCheckpointId;
    private bool _autoCheckpointBeforeRollback = true;

    public CheckpointTui(
        CheckpointService checkpointService,
        RollbackService rollbackService,
        SqlServerService serverService,
        ConnectionProfilesService connectionProfilesService,
        SqlServerConnectionFactory connectionFactory,
        DatabaseConfiguration config)
    {
        _checkpointService = checkpointService;
        _rollbackService = rollbackService;
        _serverService = serverService;
        _connectionProfilesService = connectionProfilesService;
        _connectionFactory = connectionFactory;
        _config = config;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        AnsiConsole.Clear();
        _autoCheckpointBeforeRollback = await _connectionProfilesService.GetAutoCheckpointBeforeRollbackAsync(ct);
        TrySetCursorVisible(false);

        try
        {
            var connected = await EnsureActiveConnectionAsync(ct);
            if (!connected)
                return;

            while (!ct.IsCancellationRequested)
            {
                AnsiConsole.Clear();

                // Load data fresh before each live display session
                var (isConnected, _, error) = await _serverService.GetStatusAsync(ct);
                List<Checkpoint> checkpoints = [];
                bool isInitialized = true;
                if (isConnected)
                    (checkpoints, isInitialized) = await LoadCheckpointsAsync(ct);

                // Seed selection to first item if unset
                GetSelectedCheckpoint(checkpoints);

                // Display main screen via Live to avoid cursor flicker on navigation
                ConsoleKey actionKey = ConsoleKey.NoName;
                char actionChar = '\0';

                await AnsiConsole.Live(BuildScreenRenderable(checkpoints, isInitialized, isConnected, error))
                    .AutoClear(false)
                    .StartAsync(async ctx =>
                    {
                        ctx.Refresh(); // paint immediately on entry

                        while (!ct.IsCancellationRequested)
                        {
                            var key = Console.ReadKey(intercept: true);

                            if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
                            {
                                MoveSelection(checkpoints, key.Key == ConsoleKey.UpArrow ? -1 : 1);
                                ctx.UpdateTarget(BuildScreenRenderable(checkpoints, isInitialized, isConnected, error));
                                continue;
                            }

                            // Any non-navigation key breaks out of live mode so interactive
                            // prompts (Spectre SelectionPrompt, text input) can render normally
                            actionKey = key.Key;
                            actionChar = key.KeyChar;
                            break;
                        }
                    });

                if (ct.IsCancellationRequested) break;

                AnsiConsole.WriteLine();

                switch (actionKey)
                {
                    case ConsoleKey.C:
                        if (isConnected) await CreateCheckpointInteractiveAsync(ct);
                        break;
                    case ConsoleKey.R:
                        if (isConnected)
                        {
                            var target = GetSelectedCheckpoint(checkpoints);
                            if (target is not null)
                                await RollbackInteractiveAsync(target, skipConfirmation: actionChar == 'R', ct);
                        }
                        break;
                    case ConsoleKey.D:
                        if (isConnected)
                        {
                            var target = GetSelectedCheckpoint(checkpoints);
                            if (target is not null)
                                await DeleteCheckpointInteractiveAsync(target, ct);
                        }
                        break;
                    case ConsoleKey.I:
                        if (isConnected) await InitializeInteractiveAsync(ct);
                        break;
                    case ConsoleKey.S:
                        await ManageConnectionsInteractiveAsync(ct);
                        break;
                    case ConsoleKey.O:
                        await ShowOptionsInteractiveAsync(ct);
                        break;
                    case ConsoleKey.Q:
                        return;
                }

                // Let the user read the outcome panel before clearing the screen
                if (actionKey != ConsoleKey.Q)
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.Markup("[dim]Press any key to continue...[/]");
                    Console.ReadKey(intercept: true);
                }
                // Loop back — data will be reloaded at top of while
            }
        }
        finally
        {
            TrySetCursorVisible(true);
        }
    }

    /// <summary>
    /// Builds the entire main screen as a single Spectre renderable for flicker-free Live updates.
    /// </summary>
    private Renderable BuildScreenRenderable(
        List<Checkpoint> checkpoints,
        bool initialized,
        bool isConnected,
        string? error)
    {
        var root = new Rows(
            BuildHeaderRenderable(),
            new Text(""),
            BuildCheckpointsRenderable(checkpoints, initialized, isConnected, error),
            new Text(""),
            new Markup("[dim]Commands: [/][bold]c[/][dim]=create  [/][bold]r[/][dim]=rollback (confirm)  [/][bold]R[/][dim]=rollback (no confirm)  [/][bold]d[/][dim]=delete  [/][bold]i[/][dim]=init  [/][bold]s[/][dim]=connection  [/][bold]o[/][dim]=options  [/][bold]q[/][dim]=quit[/]"),
            new Markup("[dim]Navigate checkpoints with ↑/↓, then press r/R/d.[/]")
        );
        return root;
    }

    private Renderable BuildHeaderRenderable()
    {
        var connectionName = _activeConnectionName ?? "(unsaved)";
        var autoCheckpointStatus = _autoCheckpointBeforeRollback ? "ON" : "OFF";
        return new Rows(
            new FigletText("SqlCheckpoints").Color(Color.SteelBlue1),
            new Markup($"[dim]Connection:[/] [bold]{Markup.Escape(connectionName)}[/]"),
            new Markup($"[dim]Database:[/] [bold]{Markup.Escape(_config.Database)}[/]   [dim]Server:[/] [bold]{Markup.Escape(_config.Server)}[/]"),
            new Markup($"[dim]Auto checkpoint before rollback:[/] [bold]{autoCheckpointStatus}[/]"),
            new Rule()
        );
    }

    private Renderable BuildCheckpointsRenderable(
        List<Checkpoint> checkpoints,
        bool initialized,
        bool isConnected,
        string? error)
    {
        if (!isConnected)
            return new Markup($"[red]Connection error:[/] {Markup.Escape(error ?? "Unknown error")}");

        if (!initialized)
            return new Markup("[yellow]Not initialized. Press [bold]i[/] to initialize CDC on this database.[/]");

        if (checkpoints.Count == 0)
            return new Markup("[dim]No checkpoints yet. Press [bold]c[/] to create one.[/]");

        var selectedCheckpoint = GetSelectedCheckpoint(checkpoints);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[grey][/]")
            .AddColumn("[grey]#[/]")
            .AddColumn("[grey]Name[/]")
            .AddColumn("[grey]Created[/]")
            .AddColumn("[grey]LSN[/]")
            .AddColumn("[grey]Flags[/]");

        for (int i = 0; i < checkpoints.Count; i++)
        {
            var cp = checkpoints[i];
            var flags = cp.IsAutoSafety ? "[yellow]auto[/]" : "";
            var isSelected = selectedCheckpoint is not null && cp.Id == selectedCheckpoint.Id;
            var marker = isSelected ? "[green]▶[/]" : " ";
            var nameCell = isSelected
                ? $"[black on deepskyblue1]{Markup.Escape(cp.Name)}[/]"
                : $"[bold]{Markup.Escape(cp.Name)}[/]";
            table.AddRow(
                marker,
                $"{i + 1}",
                nameCell,
                cp.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                cp.CdcLsnHex[..8] + "…",
                flags
            );
        }

        return table;
    }

    private async Task ShowOptionsInteractiveAsync(CancellationToken ct)
    {
        while (true)
        {
            var toggleText = _autoCheckpointBeforeRollback
                ? "Disable auto checkpoint before rollback"
                : "Enable auto checkpoint before rollback";

            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Options")
                    .AddChoices(toggleText, "Back"));

            if (selected == "Back")
                return;

            _autoCheckpointBeforeRollback = !_autoCheckpointBeforeRollback;
            await _connectionProfilesService.SetAutoCheckpointBeforeRollbackAsync(_autoCheckpointBeforeRollback, ct);
            AnsiConsole.Write(new Panel(
                $"[green]✓ Auto checkpoint before rollback is now [bold]{(_autoCheckpointBeforeRollback ? "ON" : "OFF")}[/].[/]")
                .Border(BoxBorder.Rounded).BorderColor(Color.Green));
        }
    }

    private async Task<bool> EnsureActiveConnectionAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_config.Database))
        {
            var (connected, _, _) = await _serverService.GetStatusAsync(ct);
            if (connected)
            {
                _activeConnectionName ??= "(cli)";
                return true;
            }
        }

        return await SelectConnectionInteractiveAsync("Select database connection", allowCancel: true, ct);
    }

    private async Task ManageConnectionsInteractiveAsync(CancellationToken ct)
    {
        while (true)
        {
            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Connection manager")
                    .AddChoices(
                        "Select saved connection",
                        "Create new connection",
                        "Edit saved connection",
                        "Delete saved connection",
                        "Back"));

            switch (action)
            {
                case "Select saved connection":
                    await SelectConnectionInteractiveAsync("Select connection", allowCancel: true, ct);
                    return;

                case "Create new connection":
                {
                    var profile = PromptConnectionProfile(existing: null);
                    if (profile is null)
                    {
                        AnsiConsole.Write(new Panel("[grey]Create connection cancelled.[/]")
                            .Border(BoxBorder.Rounded).BorderColor(Color.Grey));
                        break;
                    }
                    await _connectionProfilesService.SaveProfileAsync(profile, setAsLastSelected: false, ct);
                    AnsiConsole.Write(new Panel(
                        $"[green]✓ Connection '[bold]{Markup.Escape(profile.Name)}[/]' saved.[/]")
                        .Border(BoxBorder.Rounded).BorderColor(Color.Green));
                    break;
                }

                case "Edit saved connection":
                {
                    var selected = await PromptSelectProfileAsync("Edit which connection?", ct);
                    if (selected is null)
                        break;

                    var updated = PromptConnectionProfile(selected);
                    if (updated is null)
                    {
                        AnsiConsole.Write(new Panel("[grey]Edit connection cancelled.[/]")
                            .Border(BoxBorder.Rounded).BorderColor(Color.Grey));
                        break;
                    }
                    if (!string.Equals(selected.Name, updated.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        await _connectionProfilesService.RenameProfileAsync(
                            oldName: selected.Name,
                            newProfile: updated,
                            setAsLastSelected: false,
                            ct);
                    }
                    else
                    {
                        await _connectionProfilesService.SaveProfileAsync(updated, setAsLastSelected: false, ct);
                    }

                    if (string.Equals(_activeConnectionName, selected.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyConnectionProfile(updated);
                        _activeConnectionName = updated.Name;
                    }

                    AnsiConsole.Write(new Panel(
                        $"[green]✓ Connection '[bold]{Markup.Escape(updated.Name)}[/]' updated.[/]")
                        .Border(BoxBorder.Rounded).BorderColor(Color.Green));
                    break;
                }

                case "Delete saved connection":
                {
                    var selected = await PromptSelectProfileAsync("Delete which connection?", ct);
                    if (selected is null)
                        break;

                    if (!AnsiConsole.Confirm($"[red]Delete connection '[bold]{Markup.Escape(selected.Name)}[/]'?[/]", defaultValue: false))
                        break;

                    await _connectionProfilesService.DeleteProfileAsync(selected.Name, ct);
                    if (string.Equals(_activeConnectionName, selected.Name, StringComparison.OrdinalIgnoreCase))
                        _activeConnectionName = "(deleted)";
                    AnsiConsole.Write(new Panel(
                        $"[green]✓ Connection '[bold]{Markup.Escape(selected.Name)}[/]' deleted.[/]")
                        .Border(BoxBorder.Rounded).BorderColor(Color.Green));
                    break;
                }

                case "Back":
                    return;
            }
        }
    }

    private async Task<bool> SelectConnectionInteractiveAsync(string title, bool allowCancel, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var profiles = (await _connectionProfilesService.GetProfilesAsync(ct)).ToList();
            var lastSelected = await _connectionProfilesService.GetLastSelectedConnectionNameAsync(ct);

            if (profiles.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No saved connections yet.[/]");
                var createNow = AnsiConsole.Confirm("Create a new connection now?", defaultValue: true);
                if (!createNow)
                    return false;

                var newProfile = PromptConnectionProfile(existing: null);
                if (newProfile is null)
                    return false;
                await _connectionProfilesService.SaveProfileAsync(newProfile, setAsLastSelected: true, ct);
                if (await TryApplyAndConnectAsync(newProfile, saveAsLastSelected: true, ct))
                    return true;
                continue;
            }

            profiles = profiles
                .OrderByDescending(p => string.Equals(p.Name, lastSelected, StringComparison.OrdinalIgnoreCase))
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var choices = profiles.Select(p => p.Name).ToList();
            choices.Add(CreateNewConnectionChoice);
            if (allowCancel)
                choices.Add(CancelChoice);

            var selectedName = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title(title)
                    .AddChoices(choices));

            if (selectedName == CancelChoice)
                return false;

            if (selectedName == CreateNewConnectionChoice)
            {
                var newProfile = PromptConnectionProfile(existing: null);
                if (newProfile is null)
                    continue;
                await _connectionProfilesService.SaveProfileAsync(newProfile, setAsLastSelected: true, ct);
                if (await TryApplyAndConnectAsync(newProfile, saveAsLastSelected: true, ct))
                    return true;
                continue;
            }

            var selectedProfile = profiles.First(p => p.Name == selectedName);
            if (await TryApplyAndConnectAsync(selectedProfile, saveAsLastSelected: true, ct))
                return true;
        }

        return false;
    }

    private async Task<bool> TryApplyAndConnectAsync(ConnectionProfile profile, bool saveAsLastSelected, CancellationToken ct)
    {
        ApplyConnectionProfile(profile);
        var (connected, _, error) = await _serverService.GetStatusAsync(ct);
        if (!connected)
        {
            AnsiConsole.MarkupLine($"[red]Failed to connect using '{Markup.Escape(profile.Name)}':[/] {Markup.Escape(error ?? "Unknown error")}");
            return false;
        }

        _activeConnectionName = profile.Name;
        if (saveAsLastSelected)
            await _connectionProfilesService.SetLastSelectedConnectionNameAsync(profile.Name, ct);

        return true;
    }

    private void ApplyConnectionProfile(ConnectionProfile profile)
    {
        _config.Server = profile.Server;
        _config.Database = profile.Database;
        _config.UseIntegratedSecurity = profile.UseIntegratedSecurity;
        _selectedCheckpointId = null;

        if (profile.UseIntegratedSecurity)
        {
            _connectionFactory.ClearCredentials();
        }
        else
        {
            _connectionFactory.SetCredentials(profile.UserId ?? string.Empty, profile.Password ?? string.Empty);
        }
    }

    private static ConnectionProfile? PromptConnectionProfile(ConnectionProfile? existing)
    {
        var defaultName = existing?.Name ?? "Local Development";
        var defaultServer = existing?.Server ?? "localhost";
        var defaultDatabase = existing?.Database ?? string.Empty;

        var name = PromptLineWithEsc("Connection [bold]name[/]", defaultName);
        if (name is null) return null;
        var server = PromptLineWithEsc("Server", defaultServer);
        if (server is null) return null;
        var database = PromptLineWithEsc("Database", defaultDatabase);
        if (database is null) return null;

        var authChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Authentication")
                .AddChoices("Integrated Security", "SQL Login", CancelChoice)
                .UseConverter(choice => choice));
        if (authChoice == CancelChoice) return null;

        var useIntegrated = authChoice == "Integrated Security";
        var userId = existing?.UserId;
        var password = existing?.Password;

        if (!useIntegrated)
        {
            userId = PromptLineWithEsc("User", existing?.UserId ?? "sa");
            if (userId is null) return null;

            var passwordPrompt = existing is null
                ? "Password"
                : "Password (leave blank to keep current)";
            var enteredPassword = PromptLineWithEsc(
                passwordPrompt,
                defaultValue: null,
                secret: true,
                useDefaultWhenEmpty: false,
                trimInput: false);
            if (enteredPassword is null) return null;
            if (!string.IsNullOrEmpty(enteredPassword))
                password = enteredPassword;
            if (existing is null && string.IsNullOrEmpty(enteredPassword))
                password = string.Empty;
        }
        else
        {
            userId = null;
            password = null;
        }

        return new ConnectionProfile
        {
            Name = name,
            Server = server,
            Database = database,
            UseIntegratedSecurity = useIntegrated,
            UserId = userId,
            Password = password
        };
    }

    private async Task<ConnectionProfile?> PromptSelectProfileAsync(string title, CancellationToken ct)
    {
        var profiles = (await _connectionProfilesService.GetProfilesAsync(ct)).ToList();
        if (profiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No saved connections.[/]");
            return null;
        }

        var names = profiles.Select(p => p.Name).ToList();
        names.Add(CancelChoice);

        var selectedName = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title(title)
                .AddChoices(names));

        if (selectedName == CancelChoice)
            return null;

        return profiles.FirstOrDefault(p => p.Name == selectedName);
    }

    private async Task<(List<Checkpoint> Checkpoints, bool Initialized)> LoadCheckpointsAsync(CancellationToken ct)
    {
        try
        {
            var checkpoints = await _checkpointService.GetAllCheckpointsAsync(ct);
            return (checkpoints, true);
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            return ([], false);
        }
    }

    private async Task CreateCheckpointInteractiveAsync(CancellationToken ct)
    {
        var name = PromptLineWithEsc(
            "Checkpoint [bold]name[/]",
            defaultValue: null,
            useDefaultWhenEmpty: false);
        if (name is null)
        {
            AnsiConsole.Write(new Panel("[grey]Create checkpoint cancelled.[/]")
                .Border(BoxBorder.Rounded).BorderColor(Color.Grey));
            return;
        }
        if (string.IsNullOrWhiteSpace(name)) return;

        string? createdName = null;
        string? failureMessage = null;
        await AnsiConsole.Status()
            .StartAsync("Creating checkpoint...", async _ =>
            {
                try
                {
                    var cp = await _checkpointService.CreateCheckpointAsync(
                        name, _config.Server, _config.Database, ct: ct);
                    createdName = cp.Name;
                }
                catch (Exception ex)
                {
                    failureMessage = ex.Message;
                }
            });

        if (failureMessage is null)
            AnsiConsole.Write(new Panel(
                $"[green]✓ Checkpoint '[bold]{Markup.Escape(createdName!)}[/]' created.[/]")
                .Border(BoxBorder.Rounded).BorderColor(Color.Green));
        else
            AnsiConsole.Write(new Panel(
                $"[red]✗ Failed to create checkpoint:[/] {Markup.Escape(failureMessage)}")
                .Border(BoxBorder.Rounded).BorderColor(Color.Red));
    }

    private Checkpoint? GetSelectedCheckpoint(IReadOnlyList<Checkpoint> checkpoints)
    {
        if (checkpoints.Count == 0)
            return null;

        if (_selectedCheckpointId is not null)
        {
            var existing = checkpoints.FirstOrDefault(c => c.Id == _selectedCheckpointId.Value);
            if (existing is not null)
                return existing;
        }

        var fallback = checkpoints[0];
        _selectedCheckpointId = fallback.Id;
        return fallback;
    }

    private void MoveSelection(IReadOnlyList<Checkpoint> checkpoints, int delta)
    {
        if (checkpoints.Count == 0)
            return;

        var current = GetSelectedCheckpoint(checkpoints);
        var currentIndex = 0;
        if (current is not null)
        {
            for (int i = 0; i < checkpoints.Count; i++)
            {
                if (checkpoints[i].Id == current.Id)
                {
                    currentIndex = i;
                    break;
                }
            }
        }
        if (currentIndex < 0) currentIndex = 0;

        var nextIndex = currentIndex + delta;
        if (nextIndex < 0) nextIndex = checkpoints.Count - 1;
        if (nextIndex >= checkpoints.Count) nextIndex = 0;

        _selectedCheckpointId = checkpoints[nextIndex].Id;
    }

    private async Task RollbackInteractiveAsync(Checkpoint target, bool skipConfirmation, CancellationToken ct)
    {
        var latest = await _checkpointService.GetLatestCheckpointAsync(ct);
        if (latest is null) return;

        var currentLsn = await _checkpointService.GetCurrentMaxLsnAsync(ct);
        var current = new Checkpoint
        {
            Id = Guid.Empty,
            Name = "(current)",
            ServerName = latest.ServerName,
            DatabaseName = latest.DatabaseName,
            CreatedAt = DateTime.UtcNow,
            CdcLsnHex = Convert.ToHexString(currentLsn),
            ParentId = latest.Id,
            IsAutoSafety = false
        };

        RollbackPlan plan;
        await AnsiConsole.Status()
            .StartAsync("Analysing changes...", async _ =>
            {
                plan = await _rollbackService.BuildRollbackPlanAsync(current, target, ct);
                PrintRollbackPlan(plan);
            });

        plan = await _rollbackService.BuildRollbackPlanAsync(current, target, ct);

        if (!plan.CanProceed)
        {
            AnsiConsole.MarkupLine("[red]Rollback is blocked:[/]");
            foreach (var blocker in plan.Blockers)
                AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(blocker)}");
            AnsiConsole.Write(new Panel("[red]Rollback blocked — no changes made.[/]")
                .Border(BoxBorder.Rounded).BorderColor(Color.Red));
            return;
        }

        if (!skipConfirmation &&
            !AnsiConsole.Confirm($"\n[bold]Execute rollback to '[yellow]{Markup.Escape(target.Name)}[/]'?[/]", defaultValue: false))
        {
            AnsiConsole.Write(new Panel("[grey]Rollback cancelled.[/]")
                .Border(BoxBorder.Rounded).BorderColor(Color.Grey));
            return;
        }

        string? failureMessage = null;
        await AnsiConsole.Status()
            .StartAsync("Rolling back...", async _ =>
            {
                try
                {
                    await _rollbackService.ExecuteRollbackAsync(
                        current,
                        target,
                        _config.Server,
                        _config.Database,
                        ct: ct,
                        createAutoSafetyCheckpoint: _autoCheckpointBeforeRollback);
                }
                catch (Exception ex)
                {
                    failureMessage = ex.Message;
                }
            });

        if (failureMessage is null)
        {
            AnsiConsole.Write(new Panel(
                $"[green]✓ Rolled back to [bold]{Markup.Escape(target.Name)}[/][/]")
                .Border(BoxBorder.Rounded).BorderColor(Color.Green));
        }
        else
        {
            AnsiConsole.Write(new Panel(
                $"[red]✗ Rollback failed:[/] {Markup.Escape(failureMessage)}")
                .Border(BoxBorder.Rounded).BorderColor(Color.Red));
        }
    }

    private static void PrintRollbackPlan(RollbackPlan plan)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Rollback from[/] '[yellow]{Markup.Escape(plan.FromCheckpoint.Name)}[/]' → '[green]{Markup.Escape(plan.TargetCheckpoint.Name)}[/]'");
        AnsiConsole.MarkupLine($"  [dim]INSERTs to undo:[/]      {plan.InsertsToUndo}");
        AnsiConsole.MarkupLine($"  [dim]UPDATEs to reverse:[/]   {plan.UpdatesToReverse}");
        AnsiConsole.MarkupLine($"  [dim]DELETEs to restore:[/]   {plan.DeletesToRestore}");
        AnsiConsole.MarkupLine($"  [dim]Total changes:[/]        [bold]{plan.TotalChanges}[/]");

        if (plan.Warnings.Count > 0)
        {
            AnsiConsole.MarkupLine("\n[yellow]Warnings:[/]");
            foreach (var w in plan.Warnings)
                AnsiConsole.MarkupLine($"  [yellow]⚠[/] {Markup.Escape(w)}");
        }

        if (plan.Blockers.Count > 0)
        {
            AnsiConsole.MarkupLine("\n[red]Blockers:[/]");
            foreach (var b in plan.Blockers)
                AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(b)}");
        }
    }

    private async Task DeleteCheckpointInteractiveAsync(Checkpoint cp, CancellationToken ct)
    {
        if (!AnsiConsole.Confirm($"Delete checkpoint '[bold]{Markup.Escape(cp.Name)}[/]'?", defaultValue: false))
        {
            AnsiConsole.Write(new Panel("[grey]Delete cancelled.[/]")
                .Border(BoxBorder.Rounded).BorderColor(Color.Grey));
            return;
        }

        string? failureMessage = null;
        try
        {
            await _checkpointService.DeleteCheckpointAsync(cp.Id, ct);
        }
        catch (Exception ex)
        {
            failureMessage = ex.Message;
        }

        if (failureMessage is null)
            AnsiConsole.Write(new Panel(
                $"[green]✓ Checkpoint '[bold]{Markup.Escape(cp.Name)}[/]' deleted.[/]")
                .Border(BoxBorder.Rounded).BorderColor(Color.Green));
        else
            AnsiConsole.Write(new Panel(
                $"[red]✗ Delete failed:[/] {Markup.Escape(failureMessage)}")
                .Border(BoxBorder.Rounded).BorderColor(Color.Red));
    }

    private async Task InitializeInteractiveAsync(CancellationToken ct)
    {
        AnsiConsole.MarkupLine("[yellow]This will enable CDC on all tables in the database.[/]");
        if (!AnsiConsole.Confirm("Proceed?", defaultValue: false))
        {
            AnsiConsole.Write(new Panel("[grey]Initialization cancelled.[/]")
                .Border(BoxBorder.Rounded).BorderColor(Color.Grey));
            return;
        }

        string? failureMessage = null;
        await AnsiConsole.Status()
            .StartAsync("Initializing...", async _ =>
            {
                try { await _serverService.InitializeAsync(ct); }
                catch (Exception ex) { failureMessage = ex.Message; }
            });

        if (failureMessage is null)
            AnsiConsole.Write(new Panel("[green]✓ Checkpoint system initialized.[/]")
                .Border(BoxBorder.Rounded).BorderColor(Color.Green));
        else
            AnsiConsole.Write(new Panel(
                $"[red]✗ Initialization failed:[/] {Markup.Escape(failureMessage)}")
                .Border(BoxBorder.Rounded).BorderColor(Color.Red));
    }

    private static string? PromptLineWithEsc(
        string label,
        string? defaultValue = null,
        bool secret = false,
        bool useDefaultWhenEmpty = true,
        bool trimInput = true)
    {
        var previousCursorVisible = TryGetCursorVisible();
        TrySetCursorVisible(true);

        var hasDefault = !string.IsNullOrEmpty(defaultValue);
        if (hasDefault)
        {
            AnsiConsole.Markup($"{label} [dim](Esc to cancel, default: {Markup.Escape(defaultValue!)})[/]: ");
        }
        else
        {
            AnsiConsole.Markup($"{label} [dim](Esc to cancel)[/]: ");
        }

        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Escape)
            {
                Console.WriteLine();
                if (previousCursorVisible.HasValue)
                    TrySetCursorVisible(previousCursorVisible.Value);
                return null;
            }

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                var raw = buffer.ToString();
                if (previousCursorVisible.HasValue)
                    TrySetCursorVisible(previousCursorVisible.Value);
                if (useDefaultWhenEmpty && string.IsNullOrWhiteSpace(raw) && defaultValue is not null)
                    return defaultValue;
                return trimInput ? raw.Trim() : raw;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length == 0) continue;
                buffer.Length--;
                Console.Write("\b \b");
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
                Console.Write(secret ? '*' : key.KeyChar);
            }
        }
    }

    private static bool? TryGetCursorVisible()
    {
        try
        {
            return Console.CursorVisible;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static void TrySetCursorVisible(bool visible)
    {
        // ANSI fallback for terminals where Console.CursorVisible isn't supported.
        //  ?25l = hide cursor, ?25h = show cursor
        Console.Write(visible ? "\u001b[?25h" : "\u001b[?25l");
        Console.Out.Flush();

        try
        {
            Console.CursorVisible = visible;
        }
        catch (PlatformNotSupportedException)
        {
            // Some terminals/platforms do not support cursor visibility control.
        }
    }
}
