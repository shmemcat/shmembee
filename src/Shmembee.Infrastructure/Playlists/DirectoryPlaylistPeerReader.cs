using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shmembee.Application.Ports;

namespace Shmembee.Infrastructure.Playlists
{
    public sealed class DirectoryPlaylistPeerReader : IPlaylistPeerReader
    {
        private readonly string directoryPath;
        private readonly M3uPlaylistParser parser;

        public DirectoryPlaylistPeerReader(
            string directoryPath,
            M3uPlaylistParser? parser = null)
        {
            this.directoryPath = directoryPath
                ?? throw new ArgumentNullException(nameof(directoryPath));
            this.parser = parser ?? new M3uPlaylistParser();
        }

        public IReadOnlyList<PeerPlaylist> ReadPlaylists()
        {
            if (!Directory.Exists(directoryPath))
            {
                return Array.Empty<PeerPlaylist>();
            }

            return Directory
                .EnumerateFiles(directoryPath)
                .Where(path =>
                    string.Equals(
                        Path.GetExtension(path),
                        ".m3u",
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        Path.GetExtension(path),
                        ".m3u8",
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(ReadPlaylist)
                .ToList();
        }

        private PeerPlaylist ReadPlaylist(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            {
                ParsedPlaylist parsed = parser.Parse(
                    stream,
                    Path.GetFileNameWithoutExtension(path),
                    Path.GetFileName(path));
                return new PeerPlaylist(
                    parsed.DisplayName,
                    parsed.BackingName ?? Path.GetFileName(path),
                    parsed.Entries
                        .Select(entry => new PeerPlaylistEntry(
                            entry.SourceValue,
                            entry.Title,
                            entry.DurationSeconds))
                        .ToList());
            }
        }
    }
}
