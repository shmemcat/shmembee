using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Shmembee.Application.Ports;

namespace Shmembee.Infrastructure.Playlists
{
    // Device access is explicit. All ordinary reads use the last complete snapshot.
    public sealed class PhoneReadSnapshot : IPlaylistFileTransport,
        IPhonePlaylistSnapshotReader, IPhonePlaylistCatalogReader, IPhoneMediaPathReader
    {
        private IReadOnlyList<PhonePlaylistContent>? playlists;
        private IReadOnlyList<string>? mediaPaths;

        public void Capture(
            IPhonePlaylistSnapshotReader playlistReader,
            IProgressivePhoneMediaPathReader mediaReader,
            CancellationToken cancellationToken,
            IProgress<PhoneMediaTraversalProgress>? progress = null)
        {
            Invalidate();
            var paths = mediaReader.ReadMediaPaths(cancellationToken, progress).ToList().AsReadOnly();
            cancellationToken.ThrowIfCancellationRequested();
            var files = playlistReader.ReadPlaylistSnapshot().Select(Clone).ToList().AsReadOnly();
            cancellationToken.ThrowIfCancellationRequested();
            // Reject ambiguous names before making the snapshot available.
            _ = files.ToDictionary(file => file.BackingName, StringComparer.OrdinalIgnoreCase);
            playlists = files;
            mediaPaths = paths;
        }

        public void Invalidate()
        {
            playlists = null;
            mediaPaths = null;
        }

        public byte[]? Read(string backingName) =>
            (byte[]?)RequirePlaylists().SingleOrDefault(file => string.Equals(
                file.BackingName, backingName, StringComparison.OrdinalIgnoreCase))?.Content.Clone();

        public IReadOnlyList<PhonePlaylistContent> ReadPlaylistSnapshot() =>
            RequirePlaylists().Select(Clone).ToList().AsReadOnly();

        public IReadOnlyList<PhonePlaylistFile> ListPlaylists() =>
            RequirePlaylists().Select(file => new PhonePlaylistFile(
                file.Id, file.BackingName, byteCount: file.Content.Length)).ToList();

        public IReadOnlyList<string> ReadMediaPaths() => mediaPaths
            ?? throw new InvalidOperationException("Load or refresh the phone snapshot first.");

        public void Replace(string backingName, byte[] content) =>
            throw new NotSupportedException("The phone is read-only. Export playlists locally.");

        public void Delete(string backingName) =>
            throw new NotSupportedException("The phone is read-only. Delete phone playlists manually.");

        private IReadOnlyList<PhonePlaylistContent> RequirePlaylists() => playlists
            ?? throw new InvalidOperationException("Load or refresh the phone snapshot first.");

        private static PhonePlaylistContent Clone(PhonePlaylistContent file) =>
            new PhonePlaylistContent(file.Id, file.BackingName, (byte[])file.Content.Clone());
    }
}
