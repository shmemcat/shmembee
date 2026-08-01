using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Shmembee.Core.Playlists;

namespace Shmembee.Core.Reconciliation
{
    public sealed class ThreeWayPlaylistReconciler
    {
        private readonly bool requireReviewForConcurrentChanges;

        public ThreeWayPlaylistReconciler(bool requireReviewForConcurrentChanges = true)
        {
            this.requireReviewForConcurrentChanges = requireReviewForConcurrentChanges;
        }

        public ReconciliationResult Reconcile(
            PlaylistSnapshot commonBase,
            PlaylistSnapshot musicBee,
            PlaylistSnapshot phone)
        {
            if (commonBase == null)
            {
                throw new ArgumentNullException(nameof(commonBase));
            }

            if (musicBee == null)
            {
                throw new ArgumentNullException(nameof(musicBee));
            }

            if (phone == null)
            {
                throw new ArgumentNullException(nameof(phone));
            }

            IReadOnlyList<TrackIdentity> baseTracks = commonBase.TrackSequence;
            IReadOnlyList<TrackIdentity> musicBeeTracks = musicBee.TrackSequence;
            IReadOnlyList<TrackIdentity> phoneTracks = phone.TrackSequence;

            bool musicBeeChanged = !SequenceEqual(baseTracks, musicBeeTracks);
            bool phoneChanged = !SequenceEqual(baseTracks, phoneTracks);

            if (!musicBeeChanged && !phoneChanged)
            {
                return ReconciliationResult.Resolved(
                    ReconciliationOutcome.Unchanged,
                    baseTracks,
                    "Neither side changed");
            }

            if (musicBeeChanged && !phoneChanged)
            {
                return ReconciliationResult.Resolved(
                    ReconciliationOutcome.MusicBeeOnly,
                    musicBeeTracks,
                    "Only MusicBee changed");
            }

            if (!musicBeeChanged && phoneChanged)
            {
                return ReconciliationResult.Resolved(
                    ReconciliationOutcome.PhoneOnly,
                    phoneTracks,
                    "Only the phone changed");
            }

            if (SequenceEqual(musicBeeTracks, phoneTracks))
            {
                return ReconciliationResult.Resolved(
                    ReconciliationOutcome.SameChange,
                    musicBeeTracks,
                    "Both sides reached the same ordered result");
            }

            return requireReviewForConcurrentChanges
                ? ReconciliationResult.Conflict(
                    SummarizeChanges(baseTracks, musicBeeTracks, phoneTracks))
                : ReconciliationResult.Resolved(
                    ReconciliationOutcome.MusicBeeOnly,
                    musicBeeTracks,
                    "Concurrent changes resolved in favor of MusicBee by policy");
        }

        private static bool SequenceEqual(
            IReadOnlyList<TrackIdentity> first,
            IReadOnlyList<TrackIdentity> second)
        {
            if (first.Count != second.Count)
            {
                return false;
            }

            for (int index = 0; index < first.Count; index++)
            {
                if (!first[index].Equals(second[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static string SummarizeChanges(
            IReadOnlyList<TrackIdentity> commonBase,
            IReadOnlyList<TrackIdentity> musicBee,
            IReadOnlyList<TrackIdentity> phone)
        {
            string baseSummary = string.Join(", ", commonBase.Select(track => track.Value));
            string musicBeeSummary = string.Join(", ", musicBee.Select(track => track.Value));
            string phoneSummary = string.Join(", ", phone.Select(track => track.Value));
            return "Both sides changed differently. Base: ["
                + baseSummary
                + "]; MusicBee: ["
                + musicBeeSummary
                + "]; Phone: ["
                + phoneSummary
                + "]";
        }
    }

    public sealed class ReconciliationResult
    {
        private ReconciliationResult(
            ReconciliationOutcome outcome,
            IEnumerable<TrackIdentity> proposedTracks,
            string summary)
        {
            Outcome = outcome;
            ProposedTracks = new ReadOnlyCollection<TrackIdentity>(
                proposedTracks.ToList());
            Summary = summary;
        }

        public ReconciliationOutcome Outcome { get; }

        public IReadOnlyList<TrackIdentity> ProposedTracks { get; }

        public string Summary { get; }

        public bool RequiresReview => Outcome == ReconciliationOutcome.Conflict;

        public static ReconciliationResult Resolved(
            ReconciliationOutcome outcome,
            IEnumerable<TrackIdentity> proposedTracks,
            string summary) =>
            new ReconciliationResult(outcome, proposedTracks, summary);

        public static ReconciliationResult Conflict(string summary) =>
            new ReconciliationResult(
                ReconciliationOutcome.Conflict,
                Enumerable.Empty<TrackIdentity>(),
                summary);
    }

    public enum ReconciliationOutcome
    {
        Unchanged,
        MusicBeeOnly,
        PhoneOnly,
        SameChange,
        Conflict
    }
}
