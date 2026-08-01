using System;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Shmembee.Core.Playlists;

namespace Shmembee.Infrastructure.Persistence
{
    public sealed class PlaylistSnapshotStore
    {
        private readonly string connectionString;

        public PlaylistSnapshotStore(string databasePath)
        {
            connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();
        }

        public void Save(
            string peerId,
            string peerKind,
            PlaylistSnapshot snapshot,
            int revision,
            string source,
            string checksum)
        {
            new DatabaseMigrator(
                new SqliteConnectionStringBuilder(connectionString).DataSource)
                .ApplyPending();

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (SqliteCommand foreignKeys = connection.CreateCommand())
                {
                    foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
                    foreignKeys.ExecuteNonQuery();
                }

                using (SqliteTransaction transaction = connection.BeginTransaction())
                {
                    UpsertPeer(connection, transaction, peerId, peerKind);
                    UpsertPlaylist(connection, transaction, peerId, snapshot);
                    InsertSnapshot(
                        connection,
                        transaction,
                        snapshot,
                        revision,
                        source,
                        checksum);
                    transaction.Commit();
                }
            }
        }

        private static void UpsertPeer(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string peerId,
            string peerKind)
        {
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO peers(id, kind, display_name, created_utc)
VALUES ($id, $kind, $displayName, $createdUtc)
ON CONFLICT(id) DO UPDATE SET
    kind = excluded.kind,
    display_name = excluded.display_name;";
                command.Parameters.AddWithValue("$id", peerId);
                command.Parameters.AddWithValue("$kind", peerKind);
                command.Parameters.AddWithValue("$displayName", peerId);
                command.Parameters.AddWithValue("$createdUtc", UtcNow());
                command.ExecuteNonQuery();
            }
        }

        private static void UpsertPlaylist(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string peerId,
            PlaylistSnapshot snapshot)
        {
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO playlists(id, peer_id, display_name, backing_name, created_utc)
VALUES ($id, $peerId, $displayName, $backingName, $createdUtc)
ON CONFLICT(id) DO UPDATE SET
    display_name = excluded.display_name,
    backing_name = excluded.backing_name;";
                command.Parameters.AddWithValue("$id", snapshot.PlaylistId.ToString("D"));
                command.Parameters.AddWithValue("$peerId", peerId);
                command.Parameters.AddWithValue("$displayName", snapshot.DisplayName);
                command.Parameters.AddWithValue(
                    "$backingName",
                    (object?)snapshot.BackingName ?? DBNull.Value);
                command.Parameters.AddWithValue("$createdUtc", UtcNow());
                command.ExecuteNonQuery();
            }
        }

        private static void InsertSnapshot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            PlaylistSnapshot snapshot,
            int revision,
            string source,
            string checksum)
        {
            string snapshotId = Guid.NewGuid().ToString("D");
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO playlist_snapshots(
    id, playlist_id, revision, source, checksum, captured_utc)
VALUES ($id, $playlistId, $revision, $source, $checksum, $capturedUtc);";
                command.Parameters.AddWithValue("$id", snapshotId);
                command.Parameters.AddWithValue(
                    "$playlistId",
                    snapshot.PlaylistId.ToString("D"));
                command.Parameters.AddWithValue("$revision", revision);
                command.Parameters.AddWithValue("$source", source);
                command.Parameters.AddWithValue("$checksum", checksum);
                command.Parameters.AddWithValue(
                    "$capturedUtc",
                    snapshot.CapturedUtc.ToString("O", CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
            }

            for (int position = 0; position < snapshot.Entries.Count; position++)
            {
                PlaylistEntry entry = snapshot.Entries[position];
                EnsureTrack(connection, transaction, entry.Track);
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO playlist_snapshot_entries(
    snapshot_id, position, occurrence_id, track_id, source_value)
VALUES ($snapshotId, $position, $occurrenceId, $trackId, $sourceValue);";
                    command.Parameters.AddWithValue("$snapshotId", snapshotId);
                    command.Parameters.AddWithValue("$position", position);
                    command.Parameters.AddWithValue(
                        "$occurrenceId",
                        entry.OccurrenceId.ToString("D"));
                    command.Parameters.AddWithValue("$trackId", entry.Track.Value);
                    command.Parameters.AddWithValue("$sourceValue", entry.SourceValue);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void EnsureTrack(
            SqliteConnection connection,
            SqliteTransaction transaction,
            TrackIdentity track)
        {
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR IGNORE INTO tracks(id, created_utc)
VALUES ($id, $createdUtc);";
                command.Parameters.AddWithValue("$id", track.Value);
                command.Parameters.AddWithValue("$createdUtc", UtcNow());
                command.ExecuteNonQuery();
            }
        }

        private static string UtcNow() =>
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    }
}
