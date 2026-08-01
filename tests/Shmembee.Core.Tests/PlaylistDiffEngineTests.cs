using Shmembee.Application.Reconciliation;
using Shmembee.Core.Playlists;
using Shmembee.Core.Reconciliation;

namespace Shmembee.Core.Tests;

public sealed class PlaylistDiffEngineTests
{
    [Fact]
    public void CompareTreatsDuplicateOccurrencesAsMultisetMembers()
    {
        PlaylistDiff diff = Compare(
            new[] { Entry("a"), Entry("a"), Entry("b") },
            new[] { Entry("a"), Entry("b"), Entry("b") });

        Assert.Equal(PlaylistDifferenceKind.Membership, diff.Kind);
        Assert.Equal(
            new[]
            {
                ("a", 1, OccurrenceMembership.Both),
                ("a", 2, OccurrenceMembership.MusicBeeOnly),
                ("b", 1, OccurrenceMembership.Both),
                ("b", 2, OccurrenceMembership.PhoneOnly)
            },
            diff.Occurrences
                .OrderBy(occurrence => occurrence.Track.Value)
                .ThenBy(occurrence => occurrence.Ordinal)
                .Select(occurrence => (
                    occurrence.Track.Value,
                    occurrence.Ordinal,
                    occurrence.Membership)));
    }

    [Fact]
    public void CompareClassifiesMembershipEqualReorderingAsOrderOnly()
    {
        PlaylistDiff diff = Compare(
            new[] { Entry("a"), Entry("a"), Entry("b") },
            new[] { Entry("b"), Entry("a"), Entry("a") });

        Assert.True(diff.MembershipEqual);
        Assert.True(diff.IsOrderOnly);
        Assert.Equal(PlaylistDifferenceKind.OrderOnly, diff.Kind);
    }

    [Fact]
    public void TakingCompleteSideCanUseOtherSidesOrder()
    {
        PlaylistDiff diff = Compare(
            new[] { Entry("a"), Entry("b"), Entry("c") },
            new[] { Entry("c"), Entry("a") });

        PlaylistBuildResult result = PlaylistResultBuilder.TakeCompleteSide(
            diff,
            PlaylistSide.MusicBee,
            PlaylistSide.Phone);

        Assert.False(result.IsBlocked);
        Assert.Equal(
            new[] { "c", "a", "b" },
            result.Entries.Select(entry => entry.Track.Value));
    }

    [Fact]
    public void MissingOrderSideTracksUseNearestSurvivingSourceAnchor()
    {
        PlaylistDiff diff = Compare(
            new[] { Entry("left"), Entry("anchor"), Entry("right") },
            new[] { Entry("anchor") });

        PlaylistBuildResult result = PlaylistResultBuilder.TakeCompleteSide(
            diff,
            PlaylistSide.MusicBee,
            PlaylistSide.Phone);

        Assert.Equal(
            new[] { "left", "anchor", "right" },
            result.Entries.Select(entry => entry.Track.Value));
    }

    [Fact]
    public void MissingOrderSideTracksAppendWhenNoAnchorSurvives()
    {
        PlaylistDiff diff = Compare(
            new[] { Entry("a"), Entry("b") },
            Array.Empty<PlaylistSideEntry>());

        PlaylistBuildResult result = PlaylistResultBuilder.TakeCompleteSide(
            diff,
            PlaylistSide.MusicBee,
            PlaylistSide.Phone);

        Assert.Equal(
            new[] { "a", "b" },
            result.Entries.Select(entry => entry.Track.Value));
    }

    [Fact]
    public void CustomChoicesApplyPerOccurrence()
    {
        PlaylistDiff diff = Compare(
            new[] { Entry("duplicate"), Entry("duplicate") },
            new[] { Entry("duplicate") });
        PlaylistOccurrence second = diff.Occurrences.Single(item => item.Ordinal == 2);

        PlaylistBuildResult result = PlaylistResultBuilder.BuildCustom(
            diff,
            new[]
            {
                new PlaylistOccurrenceDecision(diff.Occurrences[0].Key, OccurrenceChoice.Include),
                new PlaylistOccurrenceDecision(second.Key, OccurrenceChoice.Exclude)
            },
            PlaylistSide.MusicBee);

        Assert.Single(result.Entries);
    }

    [Fact]
    public void UnknownPhonePathBlocksMusicBeeOnlyInclusion()
    {
        PlaylistDiff diff = Compare(
            new[]
            {
                new PlaylistSideEntry(
                    Track("a"),
                    "musicbee-a",
                    musicBeeValue: "musicbee-a",
                    phonePathProof: PhonePathProof.Unknown)
            },
            Array.Empty<PlaylistSideEntry>());

        PlaylistBuildResult result = PlaylistResultBuilder.TakeCompleteSide(
            diff,
            PlaylistSide.MusicBee,
            PlaylistSide.MusicBee);

        Assert.True(result.IsBlocked);
        Assert.Contains("unknown or unproven", result.BlockedReasons.Single());
    }

    [Fact]
    public void ReviewedDraftBecomesStaleWhenEitherChecksumChanges()
    {
        var pair = new PlaylistPairKey("musicbee", "phone");
        var draft = new ReviewedPlaylistDraft(
            pair,
            "left-checksum",
            "right-checksum",
            PlaylistSide.MusicBee,
            PlaylistSide.Phone);

        Assert.Equal(
            ReviewedDraftFreshness.Current,
            draft.GetFreshness(pair, "left-checksum", "right-checksum"));
        Assert.Equal(
            ReviewedDraftFreshness.StaleChecksums,
            draft.GetFreshness(pair, "changed", "right-checksum"));
        Assert.Equal(
            ReviewedDraftFreshness.DifferentPair,
            draft.GetFreshness(
                new PlaylistPairKey("other", "phone"),
                "left-checksum",
                "right-checksum"));
    }

    [Fact]
    public void PlanningServiceCreatesFinalRepresentationsAfterFreshReview()
    {
        PlaylistDiff diff = Compare(
            new[]
            {
                new PlaylistSideEntry(
                    Track("a"),
                    "musicbee-a",
                    musicBeeValue: "musicbee-a",
                    phoneValue: "Music/A.mp3",
                    phonePathProof: PhonePathProof.Proven)
            },
            Array.Empty<PlaylistSideEntry>());
        var pair = new PlaylistPairKey("musicbee", "phone");
        var draft = new ReviewedPlaylistDraft(
            pair,
            "left",
            "right",
            PlaylistSide.MusicBee,
            PlaylistSide.MusicBee);

        ReviewedPlanResult result = ReviewedPlaylistPlanningService.Finalize(
            diff,
            draft,
            pair,
            "left",
            "right");

        Assert.True(result.IsReady);
        Assert.Equal(new[] { "musicbee-a" }, result.Plan!.MusicBeeEntries);
        Assert.Equal(new[] { "Music/A.mp3" }, result.Plan.PhoneEntries);
    }

    private static PlaylistDiff Compare(
        IEnumerable<PlaylistSideEntry> musicBee,
        IEnumerable<PlaylistSideEntry> phone) =>
        PlaylistDiffEngine.Compare(musicBee, phone);

    private static PlaylistSideEntry Entry(string identity) =>
        new(Track(identity), identity);

    private static TrackIdentity Track(string identity) => new(identity);
}
