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
    }
}
