using Shmembee.Application.Reconciliation;
using Shmembee.Application.Synchronization;

namespace Shmembee.Core.Tests;

public sealed class BaselineTombstoneMatcherTests
{
    [Fact]
    public void UniqueBaselinePathPreservesHistoricalIdentity()
    {
        var matcher = new BaselineTombstoneMatcher(
        [
            Track("old-id", @"D:\Music\Old.mp3", "Music/Old.mp3")
        ]);

        bool matched = matcher.TryConsume(
            @"music\old.mp3",
            out SynchronizationTrack? baselineTrack);

        Assert.True(matched);
        Assert.NotNull(baselineTrack);
        Assert.Equal("old-id", baselineTrack.TrackId);
        Assert.Equal(@"D:\Music\Old.mp3", baselineTrack.MusicBeeUrl);
    }

    [Fact]
    public void BaselineOccurrenceCannotBeConsumedTwice()
    {
        var matcher = new BaselineTombstoneMatcher(
        [
            Track("old-id", @"D:\Music\Old.mp3", "Music/Old.mp3")
        ]);

        Assert.True(matcher.TryConsume("Music/Old.mp3", out _));
        Assert.False(matcher.TryConsume("Music/Old.mp3", out _));
    }

    [Fact]
    public void DuplicateBaselineOccurrencesAreConsumedInOrder()
    {
        var matcher = new BaselineTombstoneMatcher(
        [
            Track("old-id", @"D:\Music\Old.mp3", "Music/Old.mp3"),
            Track("old-id", @"D:\Music\Old.mp3", "Music/Old.mp3")
        ]);

        Assert.True(matcher.TryConsume("Music/Old.mp3", out _));
        Assert.True(matcher.TryConsume("Music/Old.mp3", out _));
        Assert.False(matcher.TryConsume("Music/Old.mp3", out _));
    }

    [Fact]
    public void AmbiguousHistoricalIdentityIsNotGuessed()
    {
        var matcher = new BaselineTombstoneMatcher(
        [
            Track("first-id", @"D:\Music\First.mp3", "Music/Same.mp3"),
            Track("second-id", @"D:\Music\Second.mp3", "Music/Same.mp3")
        ]);

        Assert.False(matcher.TryConsume("Music/Same.mp3", out _));
    }

    [Fact]
    public void ChangedReplacementPathRemainsIndependentFromOldTombstone()
    {
        var matcher = new BaselineTombstoneMatcher(
        [
            Track("old-id", @"D:\Old\OldName.mp3", "Music/OldName.mp3")
        ]);

        Assert.False(matcher.TryConsume(
            "Music/New Artist/Completely Different.flac",
            out _));
        Assert.True(matcher.TryConsume(
            "Music/OldName.mp3",
            out SynchronizationTrack? oldTrack));
        Assert.Equal("old-id", oldTrack!.TrackId);
    }

    private static SynchronizationTrack Track(
        string id,
        string musicBeeUrl,
        string phonePath) =>
        new(id, musicBeeUrl, phonePath);
}
