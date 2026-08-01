using System.Collections.Generic;

namespace Shmembee.Application.Ports
{
    public interface IPlaylistPeerReader
    {
        IReadOnlyList<PeerPlaylist> ReadPlaylists();
    }

    public sealed class PeerPlaylist
    {
        public PeerPlaylist(
            string displayName,
            string backingName,
            IReadOnlyList<PeerPlaylistEntry> entries)
        {
            DisplayName = displayName;
            BackingName = backingName;
            Entries = entries;
        }

        public string DisplayName { get; }

        public string BackingName { get; }

        public IReadOnlyList<PeerPlaylistEntry> Entries { get; }
    }

    public sealed class PeerPlaylistEntry
    {
        public PeerPlaylistEntry(
            string path,
            string? title,
            int? durationSeconds)
        {
            Path = path;
            Title = title;
            DurationSeconds = durationSeconds;
        }

        public string Path { get; }

        public string? Title { get; }

        public int? DurationSeconds { get; }
    }
}
