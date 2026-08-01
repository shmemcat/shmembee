using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using Shmembee.Application.Synchronization;

namespace Shmembee.Infrastructure.Playlists
{
    public sealed class FileSystemPhonePlaylistWriter : IPhonePlaylistWriter
    {
        private readonly string playlistDirectory;
        private readonly string backupDirectory;
        private readonly DeterministicM3uWriter writer;
        private readonly M3uPlaylistParser parser;

        public FileSystemPhonePlaylistWriter(
            string playlistDirectory,
            string backupDirectory,
            DeterministicM3uWriter? writer = null,
            M3uPlaylistParser? parser = null)
        {
            this.playlistDirectory = Path.GetFullPath(playlistDirectory);
            this.backupDirectory = Path.GetFullPath(backupDirectory);
            this.writer = writer ?? new DeterministicM3uWriter();
            this.parser = parser ?? new M3uPlaylistParser();
        }

        public PlaylistState Read(string backingName)
        {
            string path = GetPlaylistPath(backingName);
            if (!File.Exists(path))
            {
                return State(exists: false, Array.Empty<string>());
            }

            using (FileStream stream = File.OpenRead(path))
            {
                ParsedPlaylist parsed = parser.Parse(stream, backingName, backingName);
                return State(
                    exists: true,
                    parsed.Entries.Select(entry => entry.NormalizedPhonePath).ToList());
            }
        }

        public PlaylistBackup Backup(string backingName, Guid operationId)
        {
            string source = GetPlaylistPath(backingName);
            string operationDirectory = Path.Combine(
                backupDirectory,
                operationId.ToString("D"));
            Directory.CreateDirectory(operationDirectory);
            string backupPath = Path.Combine(operationDirectory, backingName);
            if (!File.Exists(source))
            {
                return new PlaylistBackup(backingName, backupPath, existed: false);
            }

            File.Copy(source, backupPath, overwrite: true);
            return new PlaylistBackup(backingName, backupPath, existed: true);
        }

        public void Replace(
            string backingName,
            IReadOnlyList<string> phonePaths,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(playlistDirectory);
            string destination = GetPlaylistPath(backingName);
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, writer.Write(phonePaths));
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(destination))
                {
                    File.Replace(temporary, destination, null);
                }
                else
                {
                    File.Move(temporary, destination);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        public void Restore(PlaylistBackup backup)
        {
            string destination = GetPlaylistPath(backup.BackingName);
            if (!backup.Existed)
            {
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }

                return;
            }

            Directory.CreateDirectory(playlistDirectory);
            File.Copy(backup.BackupLocation, destination, overwrite: true);
        }

        private string GetPlaylistPath(string backingName)
        {
            string path = Path.GetFullPath(Path.Combine(playlistDirectory, backingName));
            string expectedPrefix = playlistDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "The backing name escapes the playlist directory.",
                    nameof(backingName));
            }

            return path;
        }

        private static PlaylistState State(
            bool exists,
            IReadOnlyList<string> entries) =>
            new PlaylistState(
                exists,
                PlaylistChecksum.Compute(entries),
                new ReadOnlyCollection<string>(entries.ToList()));
    }
}
