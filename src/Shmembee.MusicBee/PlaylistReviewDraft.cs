using System;
using System.Collections.Generic;
using System.Linq;
using Shmembee.Core.Reconciliation;
using Shmembee.Infrastructure.Persistence;

namespace MusicBeePlugin
{
    internal enum PlaylistLandingAction
    {
        None,
        TakeMusicBee,
        TakePhone,
        Custom
    }

    internal sealed class PlaylistReviewDraft
    {
        private PlaylistReviewDraft(HarnessPlaylistRow row)
        {
            RowId = row.RowId;
            MusicBeeChecksum = row.MusicBeeChecksum;
            PhoneChecksum = row.PhoneChecksum;
            MusicBeeOccurrenceKeys = new HashSet<string>(
                row.Diff?.Occurrences
                    .Where(item => item.DefaultChoice == OccurrenceChoice.MusicBee)
                    .Select(item => item.Key)
                ?? Enumerable.Empty<string>(),
                StringComparer.Ordinal);
            PhoneOccurrenceKeys = new HashSet<string>(
                row.Diff?.Occurrences
                    .Where(item => item.DefaultChoice == OccurrenceChoice.Phone)
                    .Select(item => item.Key)
                ?? Enumerable.Empty<string>(),
                StringComparer.Ordinal);
        }

        private PlaylistReviewDraft(PersistedPlaylistReviewDraft persisted)
        {
            RowId = persisted.RowId;
            MusicBeeChecksum = persisted.MusicBeeChecksum;
            PhoneChecksum = persisted.PhoneChecksum;
            MusicBeeOccurrenceKeys = new HashSet<string>(
                persisted.IncludedOccurrenceKeys,
                StringComparer.Ordinal);
            PhoneOccurrenceKeys = new HashSet<string>(
                persisted.PhoneOccurrenceKeys ?? Enumerable.Empty<string>(),
                StringComparer.Ordinal);
            Action = Enum.TryParse(
                persisted.Action,
                ignoreCase: false,
                out PlaylistLandingAction action)
                ? action
                : PlaylistLandingAction.None;
            IsConfirmed = persisted.IsConfirmed;
            OrderSide = Enum.TryParse(
                persisted.OrderSide,
                ignoreCase: false,
                out PlaylistSide orderSide)
                && (Action != PlaylistLandingAction.Custom || IsConfirmed)
                ? orderSide
                : (PlaylistSide?)null;
            IsDeletion = persisted.IsDeletion;
        }

        public string RowId { get; }

        public string MusicBeeChecksum { get; }

        public string PhoneChecksum { get; }

        public PlaylistLandingAction Action { get; set; }

        public HashSet<string> MusicBeeOccurrenceKeys { get; }

        public HashSet<string> PhoneOccurrenceKeys { get; }

        public OccurrenceChoice ChoiceFor(PlaylistOccurrence occurrence)
        {
            if (MusicBeeOccurrenceKeys.Contains(occurrence.Key))
            {
                return OccurrenceChoice.MusicBee;
            }

            if (PhoneOccurrenceKeys.Contains(occurrence.Key))
            {
                return OccurrenceChoice.Phone;
            }

            return OccurrenceChoice.Exclude;
        }

        public void SetChoice(string occurrenceKey, OccurrenceChoice choice)
        {
            MusicBeeOccurrenceKeys.Remove(occurrenceKey);
            PhoneOccurrenceKeys.Remove(occurrenceKey);
            if (choice == OccurrenceChoice.MusicBee)
            {
                MusicBeeOccurrenceKeys.Add(occurrenceKey);
            }
            else if (choice == OccurrenceChoice.Phone)
            {
                PhoneOccurrenceKeys.Add(occurrenceKey);
            }
        }

        public IEnumerable<PlaylistOccurrenceDecision> DecisionsFor(
            IEnumerable<PlaylistOccurrence> occurrences) =>
            occurrences.Select(item => new PlaylistOccurrenceDecision(
                item.Key,
                ChoiceFor(item)));

        public PlaylistSide? OrderSide { get; set; }

        public bool IsConfirmed { get; set; }

        public bool IsStale { get; set; }

        public bool IsDeletion { get; set; }

        public static PlaylistReviewDraft Create(HarnessPlaylistRow row) =>
            new PlaylistReviewDraft(row);

        public static PlaylistReviewDraft? FromPersisted(
            PersistedPlaylistReviewDraft persisted)
        {
            if (persisted == null
                || string.IsNullOrWhiteSpace(persisted.RowId)
                || string.IsNullOrWhiteSpace(persisted.MusicBeeChecksum)
                || string.IsNullOrWhiteSpace(persisted.PhoneChecksum))
            {
                return null;
            }

            return new PlaylistReviewDraft(persisted);
        }

        public PersistedPlaylistReviewDraft ToPersisted() =>
            new PersistedPlaylistReviewDraft
            {
                RowId = RowId,
                MusicBeePlaylistId = RowId,
                PhonePlaylistId = RowId,
                MusicBeeChecksum = MusicBeeChecksum,
                PhoneChecksum = PhoneChecksum,
                Action = Action.ToString(),
                IncludedOccurrenceKeys = MusicBeeOccurrenceKeys.ToList(),
                PhoneOccurrenceKeys = PhoneOccurrenceKeys.ToList(),
                OrderSide = OrderSide?.ToString() ?? string.Empty,
                IsConfirmed = IsConfirmed,
                IsDeletion = IsDeletion
            };
    }
}
