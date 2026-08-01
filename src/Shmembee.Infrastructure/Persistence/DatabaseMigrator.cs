using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Shmembee.Infrastructure.Persistence
{
    public sealed class DatabaseMigrator
    {
        private static readonly IReadOnlyList<Migration> Migrations = new[]
        {
            new Migration(
                1,
                "initial state",
                @"
CREATE TABLE peers (
    id TEXT PRIMARY KEY NOT NULL,
    kind TEXT NOT NULL,
    display_name TEXT NOT NULL,
    created_utc TEXT NOT NULL
);

CREATE TABLE tracks (
    id TEXT PRIMARY KEY NOT NULL,
    created_utc TEXT NOT NULL
);

CREATE TABLE track_aliases (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    track_id TEXT NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
    peer_id TEXT NOT NULL REFERENCES peers(id) ON DELETE CASCADE,
    alias_kind TEXT NOT NULL,
    alias_value TEXT NOT NULL,
    provenance TEXT NOT NULL,
    approved_utc TEXT,
    UNIQUE(peer_id, alias_kind, alias_value)
);

CREATE TABLE playlists (
    id TEXT PRIMARY KEY NOT NULL,
    peer_id TEXT NOT NULL REFERENCES peers(id) ON DELETE CASCADE,
    display_name TEXT NOT NULL,
    backing_name TEXT,
    created_utc TEXT NOT NULL
);

CREATE TABLE playlist_snapshots (
    id TEXT PRIMARY KEY NOT NULL,
    playlist_id TEXT NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
    revision INTEGER NOT NULL,
    source TEXT NOT NULL,
    checksum TEXT NOT NULL,
    captured_utc TEXT NOT NULL,
    accepted_utc TEXT,
    UNIQUE(playlist_id, revision)
);

CREATE TABLE playlist_snapshot_entries (
    snapshot_id TEXT NOT NULL REFERENCES playlist_snapshots(id) ON DELETE CASCADE,
    position INTEGER NOT NULL,
    occurrence_id TEXT NOT NULL,
    track_id TEXT REFERENCES tracks(id),
    source_value TEXT NOT NULL,
    PRIMARY KEY(snapshot_id, position)
);

CREATE TABLE sync_operations (
    id TEXT PRIMARY KEY NOT NULL,
    started_utc TEXT NOT NULL,
    completed_utc TEXT,
    status TEXT NOT NULL,
    details TEXT
);

CREATE INDEX ix_track_aliases_track_id ON track_aliases(track_id);
CREATE INDEX ix_playlists_peer_id ON playlists(peer_id);
CREATE INDEX ix_playlist_snapshots_playlist_id
    ON playlist_snapshots(playlist_id, revision);
"),
            new Migration(
                2,
                "transactional synchronization history",
                @"
ALTER TABLE sync_operations ADD COLUMN playlist_id TEXT;
ALTER TABLE sync_operations ADD COLUMN phone_backup_location TEXT;
ALTER TABLE sync_operations ADD COLUMN expected_musicbee_checksum TEXT;
ALTER TABLE sync_operations ADD COLUMN expected_phone_checksum TEXT;
ALTER TABLE sync_operations ADD COLUMN verified_musicbee_checksum TEXT;
ALTER TABLE sync_operations ADD COLUMN verified_phone_checksum TEXT;
"),
            new Migration(
                3,
                "accepted synchronization baselines",
                @"
CREATE TABLE accepted_sync_baselines (
    playlist_id TEXT PRIMARY KEY NOT NULL,
    operation_id TEXT NOT NULL REFERENCES sync_operations(id),
    accepted_utc TEXT NOT NULL,
    musicbee_checksum TEXT NOT NULL,
    phone_checksum TEXT NOT NULL
);

CREATE TABLE accepted_sync_baseline_entries (
    playlist_id TEXT NOT NULL REFERENCES accepted_sync_baselines(playlist_id)
        ON DELETE CASCADE,
    position INTEGER NOT NULL,
    track_id TEXT NOT NULL,
    musicbee_url TEXT NOT NULL,
    phone_path TEXT NOT NULL,
    PRIMARY KEY(playlist_id, position)
);
")
        };

        private readonly string connectionString;

        public DatabaseMigrator(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
            {
                throw new ArgumentException(
                    "A database path is required.",
                    nameof(databasePath));
            }

            string? directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();
        }

        public int ApplyPending()
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                EnableForeignKeys(connection);
                EnsureMigrationTable(connection);

                int currentVersion = GetCurrentVersion(connection);
                foreach (Migration migration in Migrations)
                {
                    if (migration.Version <= currentVersion)
                    {
                        continue;
                    }

                    ApplyMigration(connection, migration);
                    currentVersion = migration.Version;
                }

                return currentVersion;
            }
        }

        private static void EnableForeignKeys(SqliteConnection connection)
        {
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA foreign_keys = ON;";
                command.ExecuteNonQuery();
            }
        }

        private static void EnsureMigrationTable(SqliteConnection connection)
        {
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"
CREATE TABLE IF NOT EXISTS schema_migrations (
    version INTEGER PRIMARY KEY NOT NULL,
    name TEXT NOT NULL,
    applied_utc TEXT NOT NULL
);";
                command.ExecuteNonQuery();
            }
        }

        private static int GetCurrentVersion(SqliteConnection connection)
        {
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
                return Convert.ToInt32(
                    command.ExecuteScalar(),
                    CultureInfo.InvariantCulture);
            }
        }

        private static void ApplyMigration(
            SqliteConnection connection,
            Migration migration)
        {
            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                using (SqliteCommand schemaCommand = connection.CreateCommand())
                {
                    schemaCommand.Transaction = transaction;
                    schemaCommand.CommandText = migration.Sql;
                    schemaCommand.ExecuteNonQuery();
                }

                using (SqliteCommand recordCommand = connection.CreateCommand())
                {
                    recordCommand.Transaction = transaction;
                    recordCommand.CommandText = @"
INSERT INTO schema_migrations(version, name, applied_utc)
VALUES ($version, $name, $appliedUtc);";
                    recordCommand.Parameters.AddWithValue("$version", migration.Version);
                    recordCommand.Parameters.AddWithValue("$name", migration.Name);
                    recordCommand.Parameters.AddWithValue(
                        "$appliedUtc",
                        DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    recordCommand.ExecuteNonQuery();
                }

                transaction.Commit();
            }
        }

        private sealed class Migration
        {
            public Migration(int version, string name, string sql)
            {
                Version = version;
                Name = name;
                Sql = sql;
            }

            public int Version { get; }

            public string Name { get; }

            public string Sql { get; }
        }
    }
}
