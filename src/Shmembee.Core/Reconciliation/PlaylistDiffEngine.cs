using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Shmembee.Core.Playlists;

namespace Shmembee.Core.Reconciliation
{
    public enum PlaylistSide
    {
        MusicBee,
        Phone
    }

    public enum PhonePathProof
    {
        NotRequired,
        Proven,
        Unknown
    }

    public enum PlaylistDifferenceKind
    {
        Identical,
        OrderOnly,
        Membership
    }

    public enum OccurrenceMembership
    {
        Both,
        MusicBeeOnly,
        PhoneOnly
    }

    public enum OccurrenceChoice
    {
        MusicBee,
        Phone,
        Include,
        Exclude
    }

    public sealed class PlaylistSideEntry
    {
        public PlaylistSideEntry(
            TrackIdentity track,
            string sourceValue,
            string? musicBeeValue = null,
            string? phoneValue = null,
            PhonePathProof phonePathProof = PhonePathProof.NotRequired,
            bool musicBeeValueUnavailable = false,
            string? unavailableReason = null)
        {
            Track = track ?? throw new ArgumentNullException(nameof(track));
            SourceValue = !string.IsNullOrWhiteSpace(sourceValue)
                ? sourceValue
                : throw new ArgumentException("A source value is required.", nameof(sourceValue));
            MusicBeeValue = musicBeeValue;
            PhoneValue = phoneValue;
            PhonePathProof = phonePathProof;
            MusicBeeValueUnavailable = musicBeeValueUnavailable;
            UnavailableReason = unavailableReason;
        }

        public TrackIdentity Track { get; }

        public string SourceValue { get; }

        public string? MusicBeeValue { get; }

        public string? PhoneValue { get; }

        public PhonePathProof PhonePathProof { get; }

        public bool MusicBeeValueUnavailable { get; }

        public string? UnavailableReason { get; }

        public string? ValueFor(PlaylistSide side) =>
            side == PlaylistSide.MusicBee
                ? MusicBeeValueUnavailable ? null : MusicBeeValue ?? SourceValue
                : PhoneValue ?? (PhonePathProof == PhonePathProof.Unknown ? null : SourceValue);
    }

    public sealed class PlaylistOccurrence
    {
        internal PlaylistOccurrence(
            string key,
            TrackIdentity track,
            int ordinal,
            int? musicBeeIndex,
            int? phoneIndex,
            PlaylistSideEntry? musicBeeEntry,
            PlaylistSideEntry? phoneEntry)
        {
            Key = key;
            Track = track;
            Ordinal = ordinal;
            MusicBeeIndex = musicBeeIndex;
            PhoneIndex = phoneIndex;
            MusicBeeEntry = musicBeeEntry;
            PhoneEntry = phoneEntry;
        }

        public string Key { get; }

        public TrackIdentity Track { get; }

        public int Ordinal { get; }

        public int? MusicBeeIndex { get; }

        public int? PhoneIndex { get; }

        public PlaylistSideEntry? MusicBeeEntry { get; }

        public PlaylistSideEntry? PhoneEntry { get; }

        public OccurrenceMembership Membership =>
            MusicBeeEntry != null && PhoneEntry != null
                ? OccurrenceMembership.Both
                : MusicBeeEntry != null
                    ? OccurrenceMembership.MusicBeeOnly
                    : OccurrenceMembership.PhoneOnly;

        public bool IsChoiceBlocked(OccurrenceChoice choice, out string? reason)
        {
            PlaylistSideEntry? selected = choice == OccurrenceChoice.MusicBee
                ? MusicBeeEntry
                : choice == OccurrenceChoice.Phone
                    ? PhoneEntry
                    : choice == OccurrenceChoice.Include
                        ? MusicBeeEntry ?? PhoneEntry
                        : null;
            if (selected == null || choice == OccurrenceChoice.Exclude)
            {
                reason = null;
                return false;
            }

            if (selected.ValueFor(PlaylistSide.MusicBee) == null)
            {
                reason = selected.UnavailableReason
                    ?? "This phone occurrence could not be matched to a MusicBee track. "
                    + "Exclude it to remove the stale playlist entry.";
                return true;
            }

            if (selected.ValueFor(PlaylistSide.Phone) == null)
            {
                reason = selected.UnavailableReason
                    ?? "The phone path is unknown or unproven for this MusicBee occurrence.";
                return true;
            }

            reason = null;
            return false;
        }
    }

    public sealed class PlaylistDiff
    {
        internal PlaylistDiff(
            PlaylistDifferenceKind kind,
            IEnumerable<PlaylistOccurrence> occurrences,
            IEnumerable<string> musicBeeOrder,
            IEnumerable<string> phoneOrder)
        {
            Kind = kind;
            Occurrences = new ReadOnlyCollection<PlaylistOccurrence>(occurrences.ToList());
            MusicBeeOrder = new ReadOnlyCollection<string>(musicBeeOrder.ToList());
            PhoneOrder = new ReadOnlyCollection<string>(phoneOrder.ToList());
        }

        public PlaylistDifferenceKind Kind { get; }

        public bool MembershipEqual => Kind != PlaylistDifferenceKind.Membership;

        public bool IsOrderOnly => Kind == PlaylistDifferenceKind.OrderOnly;

        public IReadOnlyList<PlaylistOccurrence> Occurrences { get; }

        public IReadOnlyList<string> MusicBeeOrder { get; }

        public IReadOnlyList<string> PhoneOrder { get; }
    }

    public sealed class PlaylistDiffEngine
    {
        public static PlaylistDiff Compare(
            IEnumerable<PlaylistSideEntry> musicBee,
            IEnumerable<PlaylistSideEntry> phone)
        {
            List<IndexedEntry> left = Index(musicBee, nameof(musicBee));
            List<IndexedEntry> right = Index(phone, nameof(phone));
            var leftByKey = left.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
            var rightByKey = right.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
            var keys = new HashSet<string>(leftByKey.Keys, StringComparer.Ordinal);
            keys.UnionWith(rightByKey.Keys);

            List<PlaylistOccurrence> occurrences = keys
                .Select(key =>
                {
                    leftByKey.TryGetValue(key, out IndexedEntry? leftEntry);
                    rightByKey.TryGetValue(key, out IndexedEntry? rightEntry);
                    IndexedEntry entry = leftEntry ?? rightEntry!;
                    return new PlaylistOccurrence(
                        key,
                        entry.Entry.Track,
                        entry.Ordinal,
                        leftEntry?.Index,
                        rightEntry?.Index,
                        leftEntry?.Entry,
                        rightEntry?.Entry);
                })
                .OrderBy(entry => entry.MusicBeeIndex ?? int.MaxValue)
                .ThenBy(entry => entry.PhoneIndex ?? int.MaxValue)
                .ToList();

            IReadOnlyList<string> leftOrder = left.Select(entry => entry.Key).ToList();
            IReadOnlyList<string> rightOrder = right.Select(entry => entry.Key).ToList();
            bool membershipEqual = keys.Count == left.Count && left.Count == right.Count;
            PlaylistDifferenceKind kind = !membershipEqual
                ? PlaylistDifferenceKind.Membership
                : leftOrder.SequenceEqual(rightOrder, StringComparer.Ordinal)
                    ? PlaylistDifferenceKind.Identical
                    : PlaylistDifferenceKind.OrderOnly;
            return new PlaylistDiff(kind, occurrences, leftOrder, rightOrder);
        }

        private static List<IndexedEntry> Index(
            IEnumerable<PlaylistSideEntry> entries,
            string parameterName)
        {
            if (entries == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            var ordinals = new Dictionary<TrackIdentity, int>();
            var indexed = new List<IndexedEntry>();
            int index = 0;
            foreach (PlaylistSideEntry entry in entries)
            {
                if (entry == null)
                {
                    throw new ArgumentException("Playlist entries cannot contain null.", parameterName);
                }

                ordinals.TryGetValue(entry.Track, out int ordinal);
                ordinal++;
                ordinals[entry.Track] = ordinal;
                indexed.Add(new IndexedEntry(
                    entry.Track.Value + "\u001f" + ordinal,
                    ordinal,
                    index,
                    entry));
                index++;
            }

            return indexed;
        }

        private sealed class IndexedEntry
        {
            public IndexedEntry(string key, int ordinal, int index, PlaylistSideEntry entry)
            {
                Key = key;
                Ordinal = ordinal;
                Index = index;
                Entry = entry;
            }

            public string Key { get; }

            public int Ordinal { get; }

            public int Index { get; }

            public PlaylistSideEntry Entry { get; }
        }
    }
}
