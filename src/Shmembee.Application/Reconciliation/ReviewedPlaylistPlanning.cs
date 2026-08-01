using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Shmembee.Core.Reconciliation;

namespace Shmembee.Application.Reconciliation
{
    public enum ReviewedDraftFreshness
    {
        Current,
        DifferentPair,
        StaleChecksums
    }

    public sealed class PlaylistPairKey : IEquatable<PlaylistPairKey>
    {
        public PlaylistPairKey(string musicBeePlaylistId, string phonePlaylistId)
        {
            MusicBeePlaylistId = Require(musicBeePlaylistId, nameof(musicBeePlaylistId));
            PhonePlaylistId = Require(phonePlaylistId, nameof(phonePlaylistId));
        }

        public string MusicBeePlaylistId { get; }

        public string PhonePlaylistId { get; }

        public bool Equals(PlaylistPairKey? other) =>
            other != null
            && string.Equals(MusicBeePlaylistId, other.MusicBeePlaylistId, StringComparison.Ordinal)
            && string.Equals(PhonePlaylistId, other.PhonePlaylistId, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as PlaylistPairKey);

        public override int GetHashCode()
        {
            unchecked
            {
                return (StringComparer.Ordinal.GetHashCode(MusicBeePlaylistId) * 397)
                    ^ StringComparer.Ordinal.GetHashCode(PhonePlaylistId);
            }
        }

        private static string Require(string value, string parameterName) =>
            !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new ArgumentException("A playlist identifier is required.", parameterName);
    }

    public sealed class ReviewedPlaylistDraft
    {
        public ReviewedPlaylistDraft(
            PlaylistPairKey pair,
            string musicBeeChecksum,
            string phoneChecksum,
            PlaylistSide orderSide,
            PlaylistSide? completeMembershipSide,
            IEnumerable<PlaylistOccurrenceDecision>? decisions = null)
        {
            Pair = pair ?? throw new ArgumentNullException(nameof(pair));
            MusicBeeChecksum = Require(musicBeeChecksum, nameof(musicBeeChecksum));
            PhoneChecksum = Require(phoneChecksum, nameof(phoneChecksum));
            OrderSide = orderSide;
            CompleteMembershipSide = completeMembershipSide;
            Decisions = new ReadOnlyCollection<PlaylistOccurrenceDecision>(
                decisions?.ToList() ?? new List<PlaylistOccurrenceDecision>());
        }

        public PlaylistPairKey Pair { get; }

        public string MusicBeeChecksum { get; }

        public string PhoneChecksum { get; }

        public PlaylistSide OrderSide { get; }

        public PlaylistSide? CompleteMembershipSide { get; }

        public IReadOnlyList<PlaylistOccurrenceDecision> Decisions { get; }

        public ReviewedDraftFreshness GetFreshness(
            PlaylistPairKey pair,
            string musicBeeChecksum,
            string phoneChecksum)
        {
            if (!Pair.Equals(pair))
            {
                return ReviewedDraftFreshness.DifferentPair;
            }

            return string.Equals(MusicBeeChecksum, musicBeeChecksum, StringComparison.Ordinal)
                && string.Equals(PhoneChecksum, phoneChecksum, StringComparison.Ordinal)
                    ? ReviewedDraftFreshness.Current
                    : ReviewedDraftFreshness.StaleChecksums;
        }

        private static string Require(string value, string parameterName) =>
            !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new ArgumentException("A checksum is required.", parameterName);
    }

    public sealed class ReviewedPlaylistPlan
    {
        internal ReviewedPlaylistPlan(
            PlaylistPairKey pair,
            string expectedMusicBeeChecksum,
            string expectedPhoneChecksum,
            IEnumerable<string> musicBeeEntries,
            IEnumerable<string> phoneEntries)
        {
            Pair = pair;
            ExpectedMusicBeeChecksum = expectedMusicBeeChecksum;
            ExpectedPhoneChecksum = expectedPhoneChecksum;
            MusicBeeEntries = new ReadOnlyCollection<string>(musicBeeEntries.ToList());
            PhoneEntries = new ReadOnlyCollection<string>(phoneEntries.ToList());
        }

        public PlaylistPairKey Pair { get; }

        public string ExpectedMusicBeeChecksum { get; }

        public string ExpectedPhoneChecksum { get; }

        public IReadOnlyList<string> MusicBeeEntries { get; }

        public IReadOnlyList<string> PhoneEntries { get; }
    }

    public sealed class ReviewedPlanResult
    {
        internal ReviewedPlanResult(
            ReviewedDraftFreshness freshness,
            ReviewedPlaylistPlan? plan,
            IEnumerable<string> blockedReasons)
        {
            Freshness = freshness;
            Plan = plan;
            BlockedReasons = new ReadOnlyCollection<string>(blockedReasons.ToList());
        }

        public ReviewedDraftFreshness Freshness { get; }

        public ReviewedPlaylistPlan? Plan { get; }

        public IReadOnlyList<string> BlockedReasons { get; }

        public bool IsReady => Freshness == ReviewedDraftFreshness.Current
            && Plan != null
            && BlockedReasons.Count == 0;
    }

    public sealed class ReviewedPlaylistPlanningService
    {
        public static ReviewedPlanResult Finalize(
            PlaylistDiff diff,
            ReviewedPlaylistDraft draft,
            PlaylistPairKey currentPair,
            string currentMusicBeeChecksum,
            string currentPhoneChecksum)
        {
            if (diff == null)
            {
                throw new ArgumentNullException(nameof(diff));
            }

            if (draft == null)
            {
                throw new ArgumentNullException(nameof(draft));
            }

            ReviewedDraftFreshness freshness = draft.GetFreshness(
                currentPair,
                currentMusicBeeChecksum,
                currentPhoneChecksum);
            if (freshness != ReviewedDraftFreshness.Current)
            {
                return new ReviewedPlanResult(freshness, null, Array.Empty<string>());
            }

            PlaylistBuildResult built = draft.CompleteMembershipSide.HasValue
                ? PlaylistResultBuilder.TakeCompleteSide(
                    diff,
                    draft.CompleteMembershipSide.Value,
                    draft.OrderSide)
                : PlaylistResultBuilder.BuildCustom(diff, draft.Decisions, draft.OrderSide);
            if (built.IsBlocked)
            {
                return new ReviewedPlanResult(freshness, null, built.BlockedReasons);
            }

            var musicBeeValues = new List<string>();
            var phoneValues = new List<string>();
            var reasons = new List<string>();
            foreach (PlaylistSideEntry entry in built.Entries)
            {
                string? musicBeeValue = entry.ValueFor(PlaylistSide.MusicBee);
                string? phoneValue = entry.ValueFor(PlaylistSide.Phone);
                if (musicBeeValue == null || phoneValue == null)
                {
                    reasons.Add(entry.Track.Value + ": target-side representation is unavailable.");
                    continue;
                }

                musicBeeValues.Add(musicBeeValue);
                phoneValues.Add(phoneValue);
            }

            ReviewedPlaylistPlan? plan = reasons.Count == 0
                ? new ReviewedPlaylistPlan(
                    draft.Pair,
                    draft.MusicBeeChecksum,
                    draft.PhoneChecksum,
                    musicBeeValues,
                    phoneValues)
                : null;
            return new ReviewedPlanResult(freshness, plan, reasons);
        }
    }
}
