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
            int? durationSeconds,
            string? albumArtist = null,
            string? album = null,
            int? discNumber = null,
            int? trackNumber = null)
        {
            Url = url;
            Artist = artist;
            Title = title;
            DurationSeconds = durationSeconds;
            AlbumArtist = albumArtist;
            Album = album;
            DiscNumber = discNumber;
            TrackNumber = trackNumber;
        }

        public string Url { get; }

        public string? Artist { get; }

        public string? Title { get; }

        public int? DurationSeconds { get; }

        public string? AlbumArtist { get; }

        public string? Album { get; }

        public int? DiscNumber { get; }

        public int? TrackNumber { get; }
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
