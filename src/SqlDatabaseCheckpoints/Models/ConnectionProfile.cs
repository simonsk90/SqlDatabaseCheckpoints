namespace SqlDatabaseCheckpoints.Models;

public class ConnectionProfile
{
    public required string Name { get; set; }
    public required string Server { get; set; }
    public required string Database { get; set; }
    public bool UseIntegratedSecurity { get; set; } = true;
    public string? UserId { get; set; }
    public string? Password { get; set; }
}

public class ConnectionProfilesStore
{
    public List<ConnectionProfile> Connections { get; set; } = [];
    public string? LastSelectedConnectionName { get; set; }
    public bool AutoCheckpointBeforeRollback { get; set; } = true;
}
