using System;
using System.Collections.Generic;
using System.Threading;

namespace Shmembee.Application.Synchronization
{
    public interface IMusicBeePlaylistWriter
    {
        PlaylistState Read(string playlistUrl);

        bool Replace(string playlistUrl, IReadOnlyList<string> canonicalUrls);

        string Create(string playlistName, IReadOnlyList<string> canonicalUrls);

        bool Delete(string playlistUrl);
    }

    public interface IPhonePlaylistWriter
    {
        PlaylistState Read(string backingName);

        PlaylistBackup Backup(string backingName, Guid operationId);

        void Replace(
            string backingName,
            IReadOnlyList<string> phonePaths,
            CancellationToken cancellationToken);

        void Delete(string backingName, CancellationToken cancellationToken);

        void Restore(PlaylistBackup backup);
    }

    public interface ISynchronizationHistory
    {
        void Started(SynchronizationPlan plan);

        void Completed(
            SynchronizationPlan plan,
            PlaylistState musicBeeResult,
            PlaylistState phoneResult);

        void CommitPending(SynchronizationPlan plan, string details);

        void Failed(SynchronizationPlan plan, string details);
    }

    public sealed class PlaylistState
    {
        public PlaylistState(
            bool exists,
            string checksum,
            IReadOnlyList<string> entries,
            IReadOnlyList<PlaylistEntryMetadata>? entryMetadata = null)
        {
            Exists = exists;
            Checksum = checksum;
            Entries = entries;
            EntryMetadata = entryMetadata
                ?? Array.Empty<PlaylistEntryMetadata>();
        }

        public bool Exists { get; }

        public string Checksum { get; }

        public IReadOnlyList<string> Entries { get; }

        public IReadOnlyList<PlaylistEntryMetadata> EntryMetadata { get; }
    }

    public sealed class PlaylistEntryMetadata
    {
        public PlaylistEntryMetadata(
            string path,
            string? title = null,
            int? durationSeconds = null)
        {
            Path = path;
            Title = title;
            DurationSeconds = durationSeconds;
        }

        public string Path { get; }

        public string? Title { get; }

        public int? DurationSeconds { get; }
    }

    public sealed class PlaylistBackup
    {
        public PlaylistBackup(
            string backingName,
            string backupLocation,
            bool existed)
        {
            BackingName = backingName;
            BackupLocation = backupLocation;
            Existed = existed;
        }

        public string BackingName { get; }

        public string BackupLocation { get; }

        public bool Existed { get; }
    }
}
