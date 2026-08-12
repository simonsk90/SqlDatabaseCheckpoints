using System.Text.Json;
using SqlDatabaseCheckpoints.Models;

namespace SqlDatabaseCheckpoints.Services;

public class ConnectionProfilesService
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public ConnectionProfilesService()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folder = Path.Combine(home, ".sql-database-checkpoints");
        _filePath = Path.Combine(folder, "connections.json");
    }

    public async Task<IReadOnlyList<ConnectionProfile>> GetProfilesAsync(CancellationToken ct = default)
    {
        var store = await LoadStoreAsync(ct);
        return store.Connections
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string?> GetLastSelectedConnectionNameAsync(CancellationToken ct = default)
    {
        var store = await LoadStoreAsync(ct);
        return store.LastSelectedConnectionName;
    }

    public async Task SaveProfileAsync(ConnectionProfile profile, bool setAsLastSelected, CancellationToken ct = default)
    {
        var store = await LoadStoreAsync(ct);
        var existingIndex = store.Connections.FindIndex(c =>
            string.Equals(c.Name, profile.Name, StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
            store.Connections[existingIndex] = profile;
        else
            store.Connections.Add(profile);

        if (setAsLastSelected)
            store.LastSelectedConnectionName = profile.Name;

        await SaveStoreAsync(store, ct);
    }

    public async Task RenameProfileAsync(string oldName, ConnectionProfile newProfile, bool setAsLastSelected, CancellationToken ct = default)
    {
        var store = await LoadStoreAsync(ct);
        store.Connections.RemoveAll(c => string.Equals(c.Name, oldName, StringComparison.OrdinalIgnoreCase));
        store.Connections.RemoveAll(c => string.Equals(c.Name, newProfile.Name, StringComparison.OrdinalIgnoreCase));
        store.Connections.Add(newProfile);

        if (setAsLastSelected || string.Equals(store.LastSelectedConnectionName, oldName, StringComparison.OrdinalIgnoreCase))
            store.LastSelectedConnectionName = newProfile.Name;

        await SaveStoreAsync(store, ct);
    }

    public async Task DeleteProfileAsync(string name, CancellationToken ct = default)
    {
        var store = await LoadStoreAsync(ct);
        store.Connections.RemoveAll(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        if (string.Equals(store.LastSelectedConnectionName, name, StringComparison.OrdinalIgnoreCase))
            store.LastSelectedConnectionName = null;

        await SaveStoreAsync(store, ct);
    }

    public async Task SetLastSelectedConnectionNameAsync(string? name, CancellationToken ct = default)
    {
        var store = await LoadStoreAsync(ct);
        store.LastSelectedConnectionName = name;
        await SaveStoreAsync(store, ct);
    }

    public async Task<bool> GetAutoCheckpointBeforeRollbackAsync(CancellationToken ct = default)
    {
        var store = await LoadStoreAsync(ct);
        return store.AutoCheckpointBeforeRollback;
    }

    public async Task SetAutoCheckpointBeforeRollbackAsync(bool enabled, CancellationToken ct = default)
    {
        var store = await LoadStoreAsync(ct);
        store.AutoCheckpointBeforeRollback = enabled;
        await SaveStoreAsync(store, ct);
    }

    private async Task<ConnectionProfilesStore> LoadStoreAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
            return new ConnectionProfilesStore();

        var json = await File.ReadAllTextAsync(_filePath, ct);
        if (string.IsNullOrWhiteSpace(json))
            return new ConnectionProfilesStore();

        return JsonSerializer.Deserialize<ConnectionProfilesStore>(json, _jsonOptions)
            ?? new ConnectionProfilesStore();
    }

    private async Task SaveStoreAsync(ConnectionProfilesStore store, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);

        store.Connections = store.Connections
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var json = JsonSerializer.Serialize(store, _jsonOptions);
        await File.WriteAllTextAsync(_filePath, json, ct);
    }
}
