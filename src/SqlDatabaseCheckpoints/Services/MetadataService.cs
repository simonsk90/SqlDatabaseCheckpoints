using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlDatabaseCheckpoints.Database;
using SqlDatabaseCheckpoints.Models;

namespace SqlDatabaseCheckpoints.Services;

/// <summary>
/// Persists checkpoint metadata to a SqlCheckpoints schema within the target database.
/// This avoids needing a separate SQLite dependency and keeps metadata close to the data.
/// </summary>
public class MetadataService
{
    private readonly SqlServerConnectionFactory _factory;
    private readonly ILogger<MetadataService> _logger;

    public MetadataService(SqlServerConnectionFactory factory, ILogger<MetadataService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    /// <summary>Creates the SqlCheckpoints metadata tables if they don't exist.</summary>
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'SqlCheckpoints')
                EXEC('CREATE SCHEMA SqlCheckpoints');

            IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'CheckpointEntry' AND schema_id = SCHEMA_ID('SqlCheckpoints'))
            BEGIN
                CREATE TABLE SqlCheckpoints.CheckpointEntry (
                    Id           UNIQUEIDENTIFIER NOT NULL PRIMARY KEY DEFAULT NEWID(),
                    Name         NVARCHAR(255)    NOT NULL,
                    DatabaseName NVARCHAR(255)    NOT NULL,
                    ServerName   NVARCHAR(255)    NOT NULL,
                    CreatedAt    DATETIME2        NOT NULL DEFAULT GETUTCDATE(),
                    CdcLsnHex    CHAR(20)         NOT NULL,
                    ParentId     UNIQUEIDENTIFIER NULL REFERENCES SqlCheckpoints.CheckpointEntry(Id),
                    IsAutoSafety BIT              NOT NULL DEFAULT 0,
                    Notes        NVARCHAR(MAX)    NULL
                );
            END

            IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'TableIdentitySnapshot' AND schema_id = SCHEMA_ID('SqlCheckpoints'))
            BEGIN
                CREATE TABLE SqlCheckpoints.TableIdentitySnapshot (
                    Id                   INT IDENTITY     PRIMARY KEY,
                    CheckpointId         UNIQUEIDENTIFIER NOT NULL REFERENCES SqlCheckpoints.CheckpointEntry(Id) ON DELETE CASCADE,
                    SchemaName           NVARCHAR(128)    NOT NULL,
                    TableName            NVARCHAR(128)    NOT NULL,
                    IdentityCurrentValue BIGINT           NULL
                );
            END
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveCheckpointAsync(Checkpoint checkpoint, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO SqlCheckpoints.CheckpointEntry (Id, Name, DatabaseName, ServerName, CreatedAt, CdcLsnHex, ParentId, IsAutoSafety, Notes)
            VALUES (@Id, @Name, @DatabaseName, @ServerName, @CreatedAt, @CdcLsnHex, @ParentId, @IsAutoSafety, @Notes)
            """;
        cmd.Parameters.AddWithValue("@Id", checkpoint.Id);
        cmd.Parameters.AddWithValue("@Name", checkpoint.Name);
        cmd.Parameters.AddWithValue("@DatabaseName", checkpoint.DatabaseName);
        cmd.Parameters.AddWithValue("@ServerName", checkpoint.ServerName);
        cmd.Parameters.AddWithValue("@CreatedAt", checkpoint.CreatedAt);
        cmd.Parameters.AddWithValue("@CdcLsnHex", checkpoint.CdcLsnHex);
        cmd.Parameters.AddWithValue("@ParentId", (object?)checkpoint.ParentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IsAutoSafety", checkpoint.IsAutoSafety);
        cmd.Parameters.AddWithValue("@Notes", (object?)checkpoint.Notes ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveIdentitySnapshotsAsync(
        IEnumerable<TableIdentitySnapshot> snapshots,
        CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct);
        foreach (var snapshot in snapshots)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO SqlCheckpoints.TableIdentitySnapshot (CheckpointId, SchemaName, TableName, IdentityCurrentValue)
                VALUES (@CheckpointId, @SchemaName, @TableName, @IdentityCurrentValue)
                """;
            cmd.Parameters.AddWithValue("@CheckpointId", snapshot.CheckpointId);
            cmd.Parameters.AddWithValue("@SchemaName", snapshot.SchemaName);
            cmd.Parameters.AddWithValue("@TableName", snapshot.TableName);
            cmd.Parameters.AddWithValue("@IdentityCurrentValue", (object?)snapshot.IdentityCurrentValue ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<List<Checkpoint>> GetAllCheckpointsAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Name, DatabaseName, ServerName, CreatedAt, CdcLsnHex, ParentId, IsAutoSafety, Notes
            FROM SqlCheckpoints.CheckpointEntry
            ORDER BY CreatedAt ASC
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<Checkpoint>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new Checkpoint
            {
                Id = reader.GetGuid(0),
                Name = reader.GetString(1),
                DatabaseName = reader.GetString(2),
                ServerName = reader.GetString(3),
                CreatedAt = reader.GetDateTime(4),
                CdcLsnHex = reader.GetString(5),
                ParentId = reader.IsDBNull(6) ? null : reader.GetGuid(6),
                IsAutoSafety = reader.GetBoolean(7),
                Notes = reader.IsDBNull(8) ? null : reader.GetString(8)
            });
        }
        return result;
    }

    public async Task<Checkpoint?> GetCheckpointByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Name, DatabaseName, ServerName, CreatedAt, CdcLsnHex, ParentId, IsAutoSafety, Notes
            FROM SqlCheckpoints.CheckpointEntry
            WHERE Id = @Id
            """;
        cmd.Parameters.AddWithValue("@Id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new Checkpoint
        {
            Id = reader.GetGuid(0),
            Name = reader.GetString(1),
            DatabaseName = reader.GetString(2),
            ServerName = reader.GetString(3),
            CreatedAt = reader.GetDateTime(4),
            CdcLsnHex = reader.GetString(5),
            ParentId = reader.IsDBNull(6) ? null : reader.GetGuid(6),
            IsAutoSafety = reader.GetBoolean(7),
            Notes = reader.IsDBNull(8) ? null : reader.GetString(8)
        };
    }

    public async Task<Checkpoint?> GetLatestCheckpointAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT TOP 1 Id, Name, DatabaseName, ServerName, CreatedAt, CdcLsnHex, ParentId, IsAutoSafety, Notes
            FROM SqlCheckpoints.CheckpointEntry
            ORDER BY CreatedAt DESC
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new Checkpoint
        {
            Id = reader.GetGuid(0),
            Name = reader.GetString(1),
            DatabaseName = reader.GetString(2),
            ServerName = reader.GetString(3),
            CreatedAt = reader.GetDateTime(4),
            CdcLsnHex = reader.GetString(5),
            ParentId = reader.IsDBNull(6) ? null : reader.GetGuid(6),
            IsAutoSafety = reader.GetBoolean(7),
            Notes = reader.IsDBNull(8) ? null : reader.GetString(8)
        };
    }

    public async Task<List<TableIdentitySnapshot>> GetIdentitySnapshotsAsync(Guid checkpointId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT CheckpointId, SchemaName, TableName, IdentityCurrentValue
            FROM SqlCheckpoints.TableIdentitySnapshot
            WHERE CheckpointId = @CheckpointId
            """;
        cmd.Parameters.AddWithValue("@CheckpointId", checkpointId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<TableIdentitySnapshot>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new TableIdentitySnapshot
            {
                CheckpointId = reader.GetGuid(0),
                SchemaName = reader.GetString(1),
                TableName = reader.GetString(2),
                IdentityCurrentValue = reader.IsDBNull(3) ? null : reader.GetInt64(3)
            });
        }
        return result;
    }

    public async Task UpdateCheckpointLsnAsync(Guid id, string newLsnHex, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE SqlCheckpoints.CheckpointEntry
            SET CdcLsnHex = @CdcLsnHex
            WHERE Id = @Id
            """;
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@CdcLsnHex", newLsnHex);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteCheckpointAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct);

        // Detach any children that reference this checkpoint as their parent
        await using var detachCmd = conn.CreateCommand();
        detachCmd.CommandText = """
            UPDATE SqlCheckpoints.CheckpointEntry
            SET ParentId = NULL
            WHERE ParentId = @Id
            """;
        detachCmd.Parameters.AddWithValue("@Id", id);
        await detachCmd.ExecuteNonQueryAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM SqlCheckpoints.CheckpointEntry WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
