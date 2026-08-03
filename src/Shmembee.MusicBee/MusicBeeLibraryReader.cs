using System;
using System.Collections.Generic;
using System.Linq;
using Shmembee.Application.Ports;

namespace MusicBeePlugin
{
    internal sealed class MusicBeeLibraryReader : IMusicLibraryReader
    {
        private readonly Plugin.MusicBeeApiInterface api;

        public MusicBeeLibraryReader(Plugin.MusicBeeApiInterface api)
        {
            this.api = api;
        }

        public IReadOnlyList<MusicLibraryTrack> ReadLibrary()
        {
            string[] files;
            if (api.Library_QueryFilesEx == null
                || !api.Library_QueryFilesEx("domain=Library", out files))
            {
                return Array.Empty<MusicLibraryTrack>();
            }

            return files
                .Select(file => new MusicLibraryTrack(
                    file,
                    GetTag(file, Plugin.MetaDataType.Artist),
                    GetTag(file, Plugin.MetaDataType.TrackTitle),
                    ParseDuration(GetProperty(file, Plugin.FilePropertyType.Duration)),
                    GetTag(file, Plugin.MetaDataType.AlbumArtist),
                    GetTag(file, Plugin.MetaDataType.Album),
                    ParseNumber(GetTag(file, Plugin.MetaDataType.DiscNo)),
                    ParseNumber(GetTag(file, Plugin.MetaDataType.TrackNo))))
                .ToList();
        }

        public MusicLibraryTrack ReadTrack(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new ArgumentException(
                    "A MusicBee track URL is required.",
                    nameof(url));
            }

            return new MusicLibraryTrack(
                url,
                GetTag(url, Plugin.MetaDataType.Artist),
                GetTag(url, Plugin.MetaDataType.TrackTitle),
                ParseDuration(GetProperty(url, Plugin.FilePropertyType.Duration)),
                GetTag(url, Plugin.MetaDataType.AlbumArtist),
                GetTag(url, Plugin.MetaDataType.Album),
                ParseNumber(GetTag(url, Plugin.MetaDataType.DiscNo)),
                ParseNumber(GetTag(url, Plugin.MetaDataType.TrackNo)));
        }

        public IReadOnlyList<MusicPlaylist> ReadPlaylists()
        {
            if (api.Playlist_QueryPlaylists == null
                || api.Playlist_QueryGetNextPlaylist == null
                || !api.Playlist_QueryPlaylists())
            {
                return Array.Empty<MusicPlaylist>();
            }

            var playlists = new List<MusicPlaylist>();
            string playlistUrl;
            while (!string.IsNullOrEmpty(
                playlistUrl = api.Playlist_QueryGetNextPlaylist()))
            {
                string[] files;
                if (api.Playlist_QueryFilesEx == null
                    || !api.Playlist_QueryFilesEx(playlistUrl, out files))
                {
                    files = Array.Empty<string>();
                }

                string name = api.Playlist_GetName == null
                    ? playlistUrl
                    : api.Playlist_GetName(playlistUrl);
                playlists.Add(new MusicPlaylist(playlistUrl, name, files));
            }

            return playlists;
        }

        private string? GetTag(string file, Plugin.MetaDataType type) =>
            api.Library_GetFileTag == null
                ? null
                : api.Library_GetFileTag(file, type);

        private string? GetProperty(string file, Plugin.FilePropertyType type) =>
            api.Library_GetFileProperty == null
                ? null
                : api.Library_GetFileProperty(file, type);

        private static int? ParseDuration(string? value)
        {
            int duration;
            return int.TryParse(value, out duration) ? duration : (int?)null;
        }

        private static int? ParseNumber(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string firstPart = value!.Split('/')[0].Trim();
            int number;
            return int.TryParse(firstPart, out number) ? number : (int?)null;
        }
    }
}
