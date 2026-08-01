using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Shmembee.Core.Playlists
{
    public sealed class PlaylistSnapshot
    {
        public PlaylistSnapshot(
            Guid playlistId,
            string displayName,
            string? backingName,
            IEnumerable<PlaylistEntry> entries,
            DateTimeOffset capturedUtc)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                throw new ArgumentException(
                    "A playlist display name is required.",
                    nameof(displayName));
            }

            PlaylistId = playlistId;
            DisplayName = displayName;
            BackingName = backingName;
            CapturedUtc = capturedUtc;
            Entries = new ReadOnlyCollection<PlaylistEntry>(
                entries?.ToList()
                    ?? throw new ArgumentNullException(nameof(entries)));
        }

        public Guid PlaylistId { get; }

        public string DisplayName { get; }

        public string? BackingName { get; }

        public DateTimeOffset CapturedUtc { get; }

        public IReadOnlyList<PlaylistEntry> Entries { get; }

        public IReadOnlyList<TrackIdentity> TrackSequence =>
            new ReadOnlyCollection<TrackIdentity>(
                Entries.Select(entry => entry.Track).ToList());
    }

    public sealed class PlaylistEntry
    {
        public PlaylistEntry(
            Guid occurrenceId,
            TrackIdentity track,
            string sourceValue)
        {
            if (string.IsNullOrWhiteSpace(sourceValue))
            {
                throw new ArgumentException(
                    "A source value is required.",
                    nameof(sourceValue));
            }

            OccurrenceId = occurrenceId;
            Track = track ?? throw new ArgumentNullException(nameof(track));
            SourceValue = sourceValue;
        }

        public Guid OccurrenceId { get; }

        public TrackIdentity Track { get; }

        public string SourceValue { get; }
    }

    public sealed class TrackIdentity : IEquatable<TrackIdentity>
    {
        public TrackIdentity(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "A track identity value is required.",
                    nameof(value));
            }

            Value = value;
        }

        public string Value { get; }

        public bool Equals(TrackIdentity? other) =>
            other != null
            && string.Equals(Value, other.Value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as TrackIdentity);

        public override int GetHashCode() =>
            StringComparer.Ordinal.GetHashCode(Value);

        public override string ToString() => Value;
    }
}
