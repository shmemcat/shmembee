using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Shmembee.Application.Synchronization;

namespace Shmembee.Infrastructure.Persistence
{
    public sealed class AcceptedBaselineStore
    {
        private readonly string databasePath;
        private readonly string connectionString;

        public AcceptedBaselineStore(string databasePath)
        {
            this.databasePath = databasePath;
            connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                ForeignKeys = true
            }.ToString();
        }

        public AcceptedBaseline? Load(string playlistId)
        {
            new DatabaseMigrator(databasePath).ApplyPending();
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (SqliteTransaction transaction = connection.BeginTransaction())
                {
                    string? musicBeeChecksum = null;
                    string? phoneChecksum = null;
                    using (SqliteCommand command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"
SELECT musicbee_checksum, phone_checksum
FROM accepted_sync_baselines
WHERE playlist_id = $playlistId;";
                        command.Parameters.AddWithValue("$playlistId", playlistId);
                        using (SqliteDataReader reader = command.ExecuteReader())
                        {
                            if (!reader.Read())
                            {
                                return null;
                            }

                            musicBeeChecksum = reader.GetString(0);
                            phoneChecksum = reader.GetString(1);
                        }
                    }

                    var tracks = new List<SynchronizationTrack>();
                    using (SqliteCommand command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"
SELECT track_id, musicbee_url, phone_path
FROM accepted_sync_baseline_entries
WHERE playlist_id = $playlistId
ORDER BY position;";
                        command.Parameters.AddWithValue("$playlistId", playlistId);
                        using (SqliteDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                tracks.Add(new SynchronizationTrack(
                                    reader.GetString(0),
                                    reader.GetString(1),
                                    reader.GetString(2)));
                            }
                        }
                    }

                    var baseline = new AcceptedBaseline(
                        musicBeeChecksum,
                        phoneChecksum,
                        tracks);
                    transaction.Commit();
                    return baseline;
                }
            }
        }
    }

    public sealed class AcceptedBaseline
    {
        public AcceptedBaseline(
            string musicBeeChecksum,
            string phoneChecksum,
            IReadOnlyList<SynchronizationTrack> tracks)
        {
            MusicBeeChecksum = musicBeeChecksum;
            PhoneChecksum = phoneChecksum;
            Tracks = tracks;
        }

        public string MusicBeeChecksum { get; }

        public string PhoneChecksum { get; }

        public IReadOnlyList<SynchronizationTrack> Tracks { get; }
    }
}
