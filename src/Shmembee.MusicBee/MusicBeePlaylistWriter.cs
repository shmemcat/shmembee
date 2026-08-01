using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Shmembee.Application.Synchronization;

namespace MusicBeePlugin
{
    internal sealed class MusicBeePlaylistWriter : IMusicBeePlaylistWriter
    {
        private readonly Plugin.MusicBeeApiInterface api;

        public MusicBeePlaylistWriter(Plugin.MusicBeeApiInterface api)
        {
            this.api = api;
        }

        public PlaylistState Read(string playlistUrl)
        {
            string[] files;
            if (api.Playlist_QueryFilesEx == null
                || !api.Playlist_QueryFilesEx(playlistUrl, out files))
            {
                throw new InvalidOperationException(
                    "MusicBee could not read playlist: " + playlistUrl);
            }

            return new PlaylistState(
                exists: true,
                PlaylistChecksum.Compute(files),
                new ReadOnlyCollection<string>(files.ToList()));
        }

        public bool Replace(
            string playlistUrl,
            IReadOnlyList<string> canonicalUrls)
        {
            if (api.Playlist_SetFiles == null)
            {
                return false;
            }

            return api.Playlist_SetFiles(playlistUrl, canonicalUrls.ToArray());
        }

        public string Create(
            string playlistName,
            IReadOnlyList<string> canonicalUrls)
        {
            if (string.IsNullOrWhiteSpace(playlistName))
            {
                throw new ArgumentException(
                    "A playlist name is required.",
                    nameof(playlistName));
            }

            if (api.Playlist_CreatePlaylist == null)
            {
                throw new InvalidOperationException(
                    "MusicBee does not expose playlist creation.");
            }

            string playlistUrl = api.Playlist_CreatePlaylist(
                string.Empty,
                playlistName,
                canonicalUrls.ToArray());
            if (string.IsNullOrWhiteSpace(playlistUrl))
            {
                throw new InvalidOperationException(
                    "MusicBee rejected playlist creation: " + playlistName);
            }

            return playlistUrl;
        }

        public bool Delete(string playlistUrl) =>
            api.Playlist_DeletePlaylist != null
            && api.Playlist_DeletePlaylist(playlistUrl);
    }
}
