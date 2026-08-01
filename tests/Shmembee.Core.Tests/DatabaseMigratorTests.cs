using Microsoft.Data.Sqlite;
using Shmembee.Infrastructure.Persistence;

namespace Shmembee.Core.Tests;

public sealed class DatabaseMigratorTests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "shmembee-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ApplyPendingCreatesVersionedSchemaIdempotently()
    {
        string databasePath = Path.Combine(temporaryDirectory, "state.db");
        var migrator = new DatabaseMigrator(databasePath);

        Assert.Equal(1, migrator.ApplyPending());
        Assert.Equal(1, migrator.ApplyPending());

        string[] expectedTables =
        {
            "peers",
            "playlist_snapshot_entries",
            "playlist_snapshots",
            "playlists",
            "schema_migrations",
            "sync_operations",
            "track_aliases",
            "tracks"
        };

        var actualTables = new List<string>();
        using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
SELECT name
FROM sqlite_master
WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
ORDER BY name;";

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                actualTables.Add(reader.GetString(0));
            }
        }

        Assert.Equal(expectedTables, actualTables);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
}
