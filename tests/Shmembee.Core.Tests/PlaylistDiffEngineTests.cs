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
    public void CompareSurfacesPhonePathMigrationWithoutMembershipDifference()
    {
        PlaylistDiff diff = Compare(
            new[]
            {
                new PlaylistSideEntry(
                    Track("a"),
                    "musicbee-a",
                    musicBeeValue: "musicbee-a",
                    phoneValue: "Music/New/01 - Song.mp3",
                    phonePathProof: PhonePathProof.Proven)
            },
            new[]
            {
                new PlaylistSideEntry(
                    Track("a"),
                    "Music/Old/01 - Song.mp3",
                    musicBeeValue: "musicbee-a",
                    phoneValue: "Music/New/01 - Song.mp3",
                    phonePathProof: PhonePathProof.Proven)
            });

        Assert.True(diff.MembershipEqual);
        Assert.Equal(PlaylistDifferenceKind.PhonePath, diff.Kind);
        PlaylistOccurrence occurrence = Assert.Single(diff.Occurrences);
        Assert.Equal(
            "Music/New/01 - Song.mp3",
            occurrence.PhoneEntry!.ValueFor(PlaylistSide.Phone));
        PlaylistBuildResult built = PlaylistResultBuilder.TakeCompleteSide(
            diff,
            PlaylistSide.MusicBee,
            PlaylistSide.MusicBee);
        Assert.Equal(
            new[] { "Music/New/01 - Song.mp3" },
            built.Entries.Select(entry => entry.ValueFor(PlaylistSide.Phone)));
    }

    [Fact]
    public void CompareTreatsNormalizedEquivalentPhonePathsAsIdentical()
    {
        PlaylistDiff diff = Compare(
            new[]
            {
                new PlaylistSideEntry(
                    Track("a"),
                    "musicbee-a",
                    musicBeeValue: "musicbee-a",
                    phoneValue: "Music/Artist/Song.mp3",
                    phonePathProof: PhonePathProof.Proven)
            },
            new[]
            {
                new PlaylistSideEntry(
                    Track("a"),
                    @".\Music\Artist\Song.mp3",
                    musicBeeValue: "musicbee-a",
                    phoneValue: "Music/Artist/Song.mp3",
                    phonePathProof: PhonePathProof.Proven)
            });

        Assert.Equal(PlaylistDifferenceKind.Identical, diff.Kind);
    }

    [Fact]
    public void CompareSurfacesPhonePathMigrationBeforeOrderDifference()
    {
        PlaylistDiff diff = Compare(
            new[]
            {
                new PlaylistSideEntry(
                    Track("a"),
                    "musicbee-a",
                    musicBeeValue: "musicbee-a",
                    phoneValue: "Music/New/A.mp3",
                    phonePathProof: PhonePathProof.Proven),
                new PlaylistSideEntry(
                    Track("b"),
                    "musicbee-b",
                    musicBeeValue: "musicbee-b",
                    phoneValue: "Music/B.mp3",
                    phonePathProof: PhonePathProof.Proven)
            },
            new[]
            {
                new PlaylistSideEntry(
                    Track("b"),
                    "Music/B.mp3",
                    musicBeeValue: "musicbee-b",
                    phoneValue: "Music/B.mp3",
                    phonePathProof: PhonePathProof.Proven),
                new PlaylistSideEntry(
                    Track("a"),
                    "Music/Old/A.mp3",
                    musicBeeValue: "musicbee-a",
                    phoneValue: "Music/New/A.mp3",
                    phonePathProof: PhonePathProof.Proven)
            });

        Assert.Equal(PlaylistDifferenceKind.PhonePath, diff.Kind);
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
    public void CustomChoicesWithoutOrderAppendNewMemberships()
    {
        PlaylistDiff diff = Compare(
            new[] { Entry("existing-a"), Entry("existing-b"), Entry("musicbee-new") },
            new[] { Entry("existing-a"), Entry("phone-new"), Entry("existing-b") });

        PlaylistBuildResult result = PlaylistResultBuilder.BuildCustom(
            diff,
            diff.Occurrences.Select(occurrence => new PlaylistOccurrenceDecision(
                occurrence.Key,
                OccurrenceChoice.Include)),
            orderSide: null);

        Assert.False(result.IsBlocked);
        Assert.Equal(
            new[] { "existing-a", "existing-b", "musicbee-new", "phone-new" },
            result.Entries.Select(entry => entry.Track.Value));
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
    public void UnresolvedPhoneOccurrenceCanBeExcludedButNotRetained()
    {
        var unresolved = new PlaylistSideEntry(
            Track("unresolved-phone:music/missing.mp3"),
            "Music/Missing.mp3",
            phoneValue: "Music/Missing.mp3",
            phonePathProof: PhonePathProof.Proven,
            musicBeeValueUnavailable: true,
            unavailableReason: "The phone entry has no MusicBee match.");
        PlaylistDiff diff = Compare(
            Array.Empty<PlaylistSideEntry>(),
            new[] { unresolved });
        PlaylistOccurrence occurrence = Assert.Single(diff.Occurrences);

        PlaylistBuildResult excluded = PlaylistResultBuilder.BuildCustom(
            diff,
            new[]
            {
                new PlaylistOccurrenceDecision(
                    occurrence.Key,
                    OccurrenceChoice.Exclude)
            },
            PlaylistSide.Phone);
        PlaylistBuildResult retained = PlaylistResultBuilder.BuildCustom(
            diff,
            new[]
            {
                new PlaylistOccurrenceDecision(
                    occurrence.Key,
                    OccurrenceChoice.Include)
            },
            PlaylistSide.Phone);

        Assert.False(excluded.IsBlocked);
        Assert.Empty(excluded.Entries);
        Assert.True(retained.IsBlocked);
        Assert.Contains("no MusicBee match", retained.BlockedReasons.Single());
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
