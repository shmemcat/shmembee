using System.Collections.Generic;

namespace Shmembee.Application.Ports
{
    public interface IMusicLibraryReader
    {
        IReadOnlyList<MusicLibraryTrack> ReadLibrary();

        IReadOnlyList<MusicPlaylist> ReadPlaylists();
    }

    public sealed class MusicLibraryTrack
    {
        public MusicLibraryTrack(
            string url,
            string? artist,
            string? title,
            int? durationSeconds)
        {
            Url = url;
            Artist = artist;
            Title = title;
            DurationSeconds = durationSeconds;
        }

        public string Url { get; }

        public string? Artist { get; }

        public string? Title { get; }

        public int? DurationSeconds { get; }
    }

    public sealed class MusicPlaylist
    {
        public MusicPlaylist(
            string url,
            string name,
            IReadOnlyList<string> trackUrls)
        {
            Url = url;
            Name = name;
            TrackUrls = trackUrls;
        }

        public string Url { get; }

        public string Name { get; }

        public IReadOnlyList<string> TrackUrls { get; }
    }
}
