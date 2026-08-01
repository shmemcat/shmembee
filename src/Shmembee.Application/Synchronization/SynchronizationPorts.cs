using System;
using System.Collections.Generic;
using System.Threading;

namespace Shmembee.Application.Synchronization
{
    public interface IMusicBeePlaylistWriter
    {
        PlaylistState Read(string playlistUrl);

        bool Replace(string playlistUrl, IReadOnlyList<string> canonicalUrls);
    }

    public interface IPhonePlaylistWriter
    {
        PlaylistState Read(string backingName);

        PlaylistBackup Backup(string backingName, Guid operationId);

        void Replace(
            string backingName,
            IReadOnlyList<string> phonePaths,
            CancellationToken cancellationToken);

        void Restore(PlaylistBackup backup);
    }

    public interface ISynchronizationHistory
    {
        void Started(SynchronizationPlan plan);

        void Completed(
            SynchronizationPlan plan,
            PlaylistState musicBeeResult,
            PlaylistState phoneResult);

        void Failed(SynchronizationPlan plan, string details);
    }

    public sealed class PlaylistState
    {
        public PlaylistState(
            bool exists,
            string checksum,
            IReadOnlyList<string> entries)
        {
            Exists = exists;
            Checksum = checksum;
            Entries = entries;
        }

        public bool Exists { get; }

        public string Checksum { get; }

        public IReadOnlyList<string> Entries { get; }
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
