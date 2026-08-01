using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using Shmembee.Application.Ports;
using Shmembee.Application.Synchronization;

namespace Shmembee.Infrastructure.Playlists
{
    public sealed class TransportPhonePlaylistWriter : IPhonePlaylistWriter
    {
        private readonly IPlaylistFileTransport transport;
        private readonly string backupDirectory;
        private readonly DeterministicM3uWriter writer;
        private readonly M3uPlaylistParser parser;

        public TransportPhonePlaylistWriter(
            IPlaylistFileTransport transport,
            string backupDirectory,
            DeterministicM3uWriter? writer = null,
            M3uPlaylistParser? parser = null)
        {
            this.transport = transport
                ?? throw new ArgumentNullException(nameof(transport));
            this.backupDirectory = Path.GetFullPath(backupDirectory);
            this.writer = writer ?? new DeterministicM3uWriter();
            this.parser = parser ?? new M3uPlaylistParser();
        }

        public PlaylistState Read(string backingName)
        {
            byte[]? content = transport.Read(backingName);
            if (content == null)
            {
                return State(exists: false, Array.Empty<string>());
            }

            return Parse(backingName, content);
        }

        public PlaylistState Parse(string backingName, byte[] content)
        {
            if (content == null)
            {
                throw new ArgumentNullException(nameof(content));
            }

            using (var stream = new MemoryStream(content, writable: false))
            {
                ParsedPlaylist parsed = parser.Parse(stream, backingName, backingName);
                return State(
                    exists: true,
                    parsed.Entries.Select(entry => entry.NormalizedPhonePath).ToList());
            }
        }

        public PlaylistBackup Backup(string backingName, Guid operationId)
        {
            byte[]? content = transport.Read(backingName);
            string directory = Path.Combine(
                backupDirectory,
                operationId.ToString("D"));
            Directory.CreateDirectory(directory);
            string backupPath = Path.Combine(directory, backingName);
            if (content == null)
            {
                return new PlaylistBackup(backingName, backupPath, existed: false);
            }

            File.WriteAllBytes(backupPath, content);
            return new PlaylistBackup(backingName, backupPath, existed: true);
        }

        public void Replace(
            string backingName,
            IReadOnlyList<string> phonePaths,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] content = writer.Write(phonePaths);
            cancellationToken.ThrowIfCancellationRequested();
            transport.Replace(backingName, content);
        }

        public void Delete(string backingName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            transport.Delete(backingName);
        }

        public void Restore(PlaylistBackup backup)
        {
            if (!backup.Existed)
            {
                transport.Delete(backup.BackingName);
                return;
            }

            transport.Replace(
                backup.BackingName,
                File.ReadAllBytes(backup.BackupLocation));
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
