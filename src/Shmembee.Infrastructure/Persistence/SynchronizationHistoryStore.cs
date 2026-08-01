using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Shmembee.Application.Synchronization;

namespace Shmembee.Infrastructure.Persistence
{
    public sealed class SynchronizationHistoryStore : ISynchronizationHistory
    {
        private readonly string databasePath;
        private readonly string connectionString;

        public SynchronizationHistoryStore(string databasePath)
        {
            this.databasePath = databasePath;
            connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                ForeignKeys = true
            }.ToString();
        }

        public void Started(SynchronizationPlan plan)
        {
            new DatabaseMigrator(databasePath).ApplyPending();
            Execute(
                @"
INSERT INTO sync_operations(
    id,
    playlist_id,
    started_utc,
    status,
    expected_musicbee_checksum,
    expected_phone_checksum)
VALUES (
    $id,
    $playlistId,
    $startedUtc,
    'started',
    $expectedMusicBee,
    $expectedPhone);",
                command =>
                {
                    command.Parameters.AddWithValue(
                        "$id",
                        plan.OperationId.ToString("D"));
                    command.Parameters.AddWithValue("$playlistId", plan.PlaylistId);
                    command.Parameters.AddWithValue("$startedUtc", UtcNow());
                    command.Parameters.AddWithValue(
                        "$expectedMusicBee",
                        plan.ExpectedMusicBeeChecksum);
                    command.Parameters.AddWithValue(
                        "$expectedPhone",
                        plan.ExpectedPhoneChecksum);
                });
        }

        public void Completed(
            SynchronizationPlan plan,
            PlaylistState musicBeeResult,
            PlaylistState phoneResult)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (SqliteTransaction transaction = connection.BeginTransaction())
                {
                    using (SqliteCommand command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"
UPDATE sync_operations
SET completed_utc = $completedUtc,
    status = 'completed',
    verified_musicbee_checksum = $musicBeeChecksum,
    verified_phone_checksum = $phoneChecksum
WHERE id = $id;";
                        command.Parameters.AddWithValue("$completedUtc", UtcNow());
                        command.Parameters.AddWithValue(
                            "$musicBeeChecksum",
                            musicBeeResult.Checksum);
                        command.Parameters.AddWithValue(
                            "$phoneChecksum",
                            phoneResult.Checksum);
                        command.Parameters.AddWithValue(
                            "$id",
                            plan.OperationId.ToString("D"));
                        command.ExecuteNonQuery();
                    }

                    using (SqliteCommand baselineCommand = connection.CreateCommand())
                    {
                        baselineCommand.Transaction = transaction;
                        baselineCommand.CommandText = @"
INSERT INTO accepted_sync_baselines(
    playlist_id,
    operation_id,
    accepted_utc,
    musicbee_checksum,
    phone_checksum)
VALUES (
    $playlistId,
    $operationId,
    $acceptedUtc,
    $musicBeeChecksum,
    $phoneChecksum)
ON CONFLICT(playlist_id) DO UPDATE SET
    operation_id = excluded.operation_id,
    accepted_utc = excluded.accepted_utc,
    musicbee_checksum = excluded.musicbee_checksum,
    phone_checksum = excluded.phone_checksum;";
                        baselineCommand.Parameters.AddWithValue(
                            "$playlistId",
                            plan.PlaylistId);
                        baselineCommand.Parameters.AddWithValue(
                            "$operationId",
                            plan.OperationId.ToString("D"));
                        baselineCommand.Parameters.AddWithValue("$acceptedUtc", UtcNow());
                        baselineCommand.Parameters.AddWithValue(
                            "$musicBeeChecksum",
                            musicBeeResult.Checksum);
                        baselineCommand.Parameters.AddWithValue(
                            "$phoneChecksum",
                            phoneResult.Checksum);
                        baselineCommand.ExecuteNonQuery();
                    }

                    using (SqliteCommand deleteEntries = connection.CreateCommand())
                    {
                        deleteEntries.Transaction = transaction;
                        deleteEntries.CommandText = @"
DELETE FROM accepted_sync_baseline_entries
WHERE playlist_id = $playlistId;";
                        deleteEntries.Parameters.AddWithValue(
                            "$playlistId",
                            plan.PlaylistId);
                        deleteEntries.ExecuteNonQuery();
                    }

                    for (int position = 0; position < plan.Tracks.Count; position++)
                    {
                        SynchronizationTrack track = plan.Tracks[position];
                        using (SqliteCommand entryCommand = connection.CreateCommand())
                        {
                            entryCommand.Transaction = transaction;
                            entryCommand.CommandText = @"
INSERT INTO accepted_sync_baseline_entries(
    playlist_id,
    position,
    track_id,
    musicbee_url,
    phone_path)
VALUES (
    $playlistId,
    $position,
    $trackId,
    $musicBeeUrl,
    $phonePath);";
                            entryCommand.Parameters.AddWithValue(
                                "$playlistId",
                                plan.PlaylistId);
                            entryCommand.Parameters.AddWithValue("$position", position);
                            entryCommand.Parameters.AddWithValue("$trackId", track.TrackId);
                            entryCommand.Parameters.AddWithValue(
                                "$musicBeeUrl",
                                track.MusicBeeUrl);
                            entryCommand.Parameters.AddWithValue(
                                "$phonePath",
                                track.PhonePath);
                            entryCommand.ExecuteNonQuery();
                        }
                    }

                    transaction.Commit();
                }
            }
        }

        public void Failed(SynchronizationPlan plan, string details)
        {
            SetTerminalStatus(plan, "failed", details);
        }

        public void CommitPending(SynchronizationPlan plan, string details)
        {
            SetTerminalStatus(plan, "commit_pending", details);
        }

        public IReadOnlyList<SynchronizationHistoryListItem> List(
            int limit = 100,
            int offset = 0)
        {
            if (limit <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(limit));
            }

            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            new DatabaseMigrator(databasePath).ApplyPending();
            var items = new List<SynchronizationHistoryListItem>();
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT id, playlist_id, started_utc, completed_utc, status, details
FROM sync_operations
ORDER BY started_utc DESC, id DESC
LIMIT $limit OFFSET $offset;";
                    command.Parameters.AddWithValue("$limit", limit);
                    command.Parameters.AddWithValue("$offset", offset);
                    using (SqliteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            items.Add(new SynchronizationHistoryListItem(
                                Guid.Parse(reader.GetString(0)),
                                NullableString(reader, 1),
                                ParseTimestamp(reader.GetString(2)),
                                NullableTimestamp(reader, 3),
                                reader.GetString(4),
                                NullableString(reader, 5)));
                        }
                    }
                }
            }

            return items;
        }

        public SynchronizationHistoryDetail? Get(Guid operationId)
        {
            new DatabaseMigrator(databasePath).ApplyPending();
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT id,
       playlist_id,
       started_utc,
       completed_utc,
       status,
       details,
       phone_backup_location,
       expected_musicbee_checksum,
       expected_phone_checksum,
       verified_musicbee_checksum,
       verified_phone_checksum
FROM sync_operations
WHERE id = $id;";
                    command.Parameters.AddWithValue("$id", operationId.ToString("D"));
                    using (SqliteDataReader reader = command.ExecuteReader())
                    {
                        if (!reader.Read())
                        {
                            return null;
                        }

                        return new SynchronizationHistoryDetail(
                            Guid.Parse(reader.GetString(0)),
                            NullableString(reader, 1),
                            ParseTimestamp(reader.GetString(2)),
                            NullableTimestamp(reader, 3),
                            reader.GetString(4),
                            NullableString(reader, 5),
                            NullableString(reader, 6),
                            NullableString(reader, 7),
                            NullableString(reader, 8),
                            NullableString(reader, 9),
                            NullableString(reader, 10));
                    }
                }
            }
        }

        private void SetTerminalStatus(
            SynchronizationPlan plan,
            string status,
            string details)
        {
            Execute(
                @"
UPDATE sync_operations
SET completed_utc = $completedUtc,
    status = $status,
    details = $details
WHERE id = $id;",
                command =>
                {
                    command.Parameters.AddWithValue("$completedUtc", UtcNow());
                    command.Parameters.AddWithValue("$status", status);
                    command.Parameters.AddWithValue("$details", details);
                    command.Parameters.AddWithValue(
                        "$id",
                        plan.OperationId.ToString("D"));
                });
        }

        private void Execute(string sql, Action<SqliteCommand> configure)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    configure(command);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static string UtcNow() =>
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        private static string? NullableString(SqliteDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

        private static DateTimeOffset? NullableTimestamp(
            SqliteDataReader reader,
            int ordinal) =>
            reader.IsDBNull(ordinal)
                ? (DateTimeOffset?)null
                : ParseTimestamp(reader.GetString(ordinal));

        private static DateTimeOffset ParseTimestamp(string value) =>
            DateTimeOffset.Parse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
    }

    public class SynchronizationHistoryListItem
    {
        public SynchronizationHistoryListItem(
            Guid operationId,
            string? playlistId,
            DateTimeOffset startedUtc,
            DateTimeOffset? completedUtc,
            string status,
            string? details)
        {
            OperationId = operationId;
            PlaylistId = playlistId;
            StartedUtc = startedUtc;
            CompletedUtc = completedUtc;
            Status = status;
            Details = details;
        }

        public Guid OperationId { get; }

        public string? PlaylistId { get; }

        public DateTimeOffset StartedUtc { get; }

        public DateTimeOffset? CompletedUtc { get; }

        public string Status { get; }

        public string? Details { get; }
    }

    public sealed class SynchronizationHistoryDetail : SynchronizationHistoryListItem
    {
        public SynchronizationHistoryDetail(
            Guid operationId,
            string? playlistId,
            DateTimeOffset startedUtc,
            DateTimeOffset? completedUtc,
            string status,
            string? details,
            string? phoneBackupLocation,
            string? expectedMusicBeeChecksum,
            string? expectedPhoneChecksum,
            string? verifiedMusicBeeChecksum,
            string? verifiedPhoneChecksum)
            : base(
                operationId,
                playlistId,
                startedUtc,
                completedUtc,
                status,
                details)
        {
            PhoneBackupLocation = phoneBackupLocation;
            ExpectedMusicBeeChecksum = expectedMusicBeeChecksum;
            ExpectedPhoneChecksum = expectedPhoneChecksum;
            VerifiedMusicBeeChecksum = verifiedMusicBeeChecksum;
            VerifiedPhoneChecksum = verifiedPhoneChecksum;
        }

        public string? PhoneBackupLocation { get; }

        public string? ExpectedMusicBeeChecksum { get; }

        public string? ExpectedPhoneChecksum { get; }

        public string? VerifiedMusicBeeChecksum { get; }

        public string? VerifiedPhoneChecksum { get; }
    }
}
