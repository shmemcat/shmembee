using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Shmembee.Application.Synchronization
{
    public sealed class SynchronizationPlan
    {
        public SynchronizationPlan(
            Guid operationId,
            string playlistId,
            string playlistDisplayName,
            string musicBeePlaylistUrl,
            string phoneBackingName,
            bool expectedPhoneExists,
            string expectedMusicBeeChecksum,
            string expectedPhoneChecksum,
            IEnumerable<SynchronizationTrack> tracks)
        {
            OperationId = operationId;
            PlaylistId = Require(playlistId, nameof(playlistId));
            PlaylistDisplayName = Require(
                playlistDisplayName,
                nameof(playlistDisplayName));
            MusicBeePlaylistUrl = Require(
                musicBeePlaylistUrl,
                nameof(musicBeePlaylistUrl));
            PhoneBackingName = Require(phoneBackingName, nameof(phoneBackingName));
            ExpectedPhoneExists = expectedPhoneExists;
            ExpectedMusicBeeChecksum = Require(
                expectedMusicBeeChecksum,
                nameof(expectedMusicBeeChecksum));
            ExpectedPhoneChecksum = Require(
                expectedPhoneChecksum,
                nameof(expectedPhoneChecksum));
            Tracks = new ReadOnlyCollection<SynchronizationTrack>(
                tracks?.ToList() ?? throw new ArgumentNullException(nameof(tracks)));
        }

        public Guid OperationId { get; }

        public string PlaylistId { get; }

        public string PlaylistDisplayName { get; }

        public string MusicBeePlaylistUrl { get; }

        public string PhoneBackingName { get; }

        public bool ExpectedPhoneExists { get; }

        public string ExpectedMusicBeeChecksum { get; }

        public string ExpectedPhoneChecksum { get; }

        public IReadOnlyList<SynchronizationTrack> Tracks { get; }

        private static string Require(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "A non-empty value is required.",
                    parameterName);
            }

            return value;
        }
    }

    public sealed class SynchronizationTrack
    {
        public SynchronizationTrack(
            string trackId,
            string musicBeeUrl,
            string phonePath)
        {
            TrackId = trackId;
            MusicBeeUrl = musicBeeUrl;
            PhonePath = phonePath;
        }

        public string TrackId { get; }

        public string MusicBeeUrl { get; }

        public string PhonePath { get; }
    }
}
