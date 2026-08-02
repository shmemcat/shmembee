using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Shmembee.Core.Reconciliation
{
    public sealed class PlaylistOccurrenceDecision
    {
        public PlaylistOccurrenceDecision(string occurrenceKey, OccurrenceChoice choice)
        {
            OccurrenceKey = !string.IsNullOrWhiteSpace(occurrenceKey)
                ? occurrenceKey
                : throw new ArgumentException("An occurrence key is required.", nameof(occurrenceKey));
            Choice = choice;
        }

        public string OccurrenceKey { get; }

        public OccurrenceChoice Choice { get; }
    }

    public sealed class PlaylistBuildResult
    {
        internal PlaylistBuildResult(
            IEnumerable<PlaylistSideEntry> entries,
            IEnumerable<string> blockedReasons)
        {
            Entries = new ReadOnlyCollection<PlaylistSideEntry>(entries.ToList());
            BlockedReasons = new ReadOnlyCollection<string>(blockedReasons.ToList());
        }

        public IReadOnlyList<PlaylistSideEntry> Entries { get; }

        public IReadOnlyList<string> BlockedReasons { get; }

        public bool IsBlocked => BlockedReasons.Count != 0;
    }

    public sealed class PlaylistResultBuilder
    {
        public static PlaylistBuildResult TakeCompleteSide(
            PlaylistDiff diff,
            PlaylistSide membershipSide,
            PlaylistSide orderSide) =>
            Build(diff, membershipSide, orderSide, null);

        public static PlaylistBuildResult BuildCustom(
            PlaylistDiff diff,
            IEnumerable<PlaylistOccurrenceDecision> decisions,
            PlaylistSide? orderSide)
        {
            if (decisions == null)
            {
                throw new ArgumentNullException(nameof(decisions));
            }

            var choices = decisions.ToDictionary(
                decision => decision.OccurrenceKey,
                decision => decision.Choice,
                StringComparer.Ordinal);
            return Build(diff, null, orderSide, choices);
        }

        private static PlaylistBuildResult Build(
            PlaylistDiff diff,
            PlaylistSide? completeSide,
            PlaylistSide? orderSide,
            Dictionary<string, OccurrenceChoice>? choices)
        {
            if (diff == null)
            {
                throw new ArgumentNullException(nameof(diff));
            }

            var selected = new Dictionary<string, PlaylistSideEntry>(StringComparer.Ordinal);
            var reasons = new List<string>();
            foreach (PlaylistOccurrence occurrence in diff.Occurrences)
            {
                OccurrenceChoice choice = completeSide.HasValue
                    ? completeSide == PlaylistSide.MusicBee
                        ? OccurrenceChoice.MusicBee
                        : OccurrenceChoice.Phone
                    : choices != null && choices.TryGetValue(occurrence.Key, out OccurrenceChoice configured)
                        ? configured
                        : OccurrenceChoice.Exclude;

                if (occurrence.IsChoiceBlocked(choice, out string? reason))
                {
                    reasons.Add(occurrence.Key + ": " + reason);
                    continue;
                }

                PlaylistSideEntry? entry = ChooseEntry(occurrence, choice);
                if (entry != null)
                {
                    selected.Add(occurrence.Key, entry);
                }
            }

            if (reasons.Count != 0)
            {
                return new PlaylistBuildResult(Array.Empty<PlaylistSideEntry>(), reasons);
            }

            if (!orderSide.HasValue)
            {
                var combinedOrder = diff.MusicBeeOrder
                    .Concat(diff.PhoneOrder)
                    .Distinct(StringComparer.Ordinal);
                var matchingKeys = new HashSet<string>(
                    diff.Occurrences
                        .Where(item => item.Membership == OccurrenceMembership.Both)
                        .Select(item => item.Key),
                    StringComparer.Ordinal);
                List<string> appendedResultKeys = combinedOrder
                    .Where(key => selected.ContainsKey(key) && matchingKeys.Contains(key))
                    .Concat(combinedOrder.Where(key =>
                        selected.ContainsKey(key) && !matchingKeys.Contains(key)))
                    .ToList();
                return new PlaylistBuildResult(
                    appendedResultKeys.Select(key => selected[key]),
                    reasons);
            }

            IReadOnlyList<string> sourceOrder = orderSide == PlaylistSide.MusicBee
                ? diff.MusicBeeOrder
                : diff.PhoneOrder;
            IReadOnlyList<string> alternateOrder = orderSide == PlaylistSide.MusicBee
                ? diff.PhoneOrder
                : diff.MusicBeeOrder;
            List<string> resultKeys = sourceOrder.Where(selected.ContainsKey).ToList();
            foreach (string key in alternateOrder.Where(selected.ContainsKey))
            {
                if (!resultKeys.Contains(key, StringComparer.Ordinal))
                {
                    InsertByNearestAnchor(resultKeys, alternateOrder, key);
                }
            }

            return new PlaylistBuildResult(resultKeys.Select(key => selected[key]), reasons);
        }

        private static PlaylistSideEntry? ChooseEntry(
            PlaylistOccurrence occurrence,
            OccurrenceChoice choice)
        {
            switch (choice)
            {
                case OccurrenceChoice.MusicBee:
                    return occurrence.MusicBeeEntry;
                case OccurrenceChoice.Phone:
                    return occurrence.PhoneEntry;
                case OccurrenceChoice.Include:
                    return occurrence.MusicBeeEntry ?? occurrence.PhoneEntry;
                case OccurrenceChoice.Exclude:
                    return null;
                default:
                    throw new ArgumentOutOfRangeException(nameof(choice));
            }
        }

        private static void InsertByNearestAnchor(
            List<string> result,
            IReadOnlyList<string> sourceOrder,
            string key)
        {
            int sourceIndex = sourceOrder.IndexOf(key);
            for (int distance = 1; distance < sourceOrder.Count; distance++)
            {
                int previousIndex = sourceIndex - distance;
                int nextIndex = sourceIndex + distance;
                int previousResult = previousIndex >= 0
                    ? result.IndexOf(sourceOrder[previousIndex])
                    : -1;
                int nextResult = nextIndex < sourceOrder.Count
                    ? result.IndexOf(sourceOrder[nextIndex])
                    : -1;

                if (previousResult >= 0)
                {
                    result.Insert(previousResult + 1, key);
                    return;
                }

                if (nextResult >= 0)
                {
                    result.Insert(nextResult, key);
                    return;
                }
            }

            result.Add(key);
        }
    }

    internal static class ReconciliationListExtensions
    {
        public static int IndexOf<T>(this IReadOnlyList<T> values, T value)
        {
            for (int index = 0; index < values.Count; index++)
            {
                if (EqualityComparer<T>.Default.Equals(values[index], value))
                {
                    return index;
                }
            }

            return -1;
        }

        public static int IndexOf<T>(this IList<T> values, T value)
        {
            for (int index = 0; index < values.Count; index++)
            {
                if (EqualityComparer<T>.Default.Equals(values[index], value))
                {
                    return index;
                }
            }

            return -1;
        }
    }
}
