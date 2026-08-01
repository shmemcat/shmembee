using Shmembee.Infrastructure.Persistence;

namespace Shmembee.Core.Tests;

public sealed class ReviewedPlaylistDraftStoreTests : IDisposable
{
    private readonly string temporaryDirectory =
        Path.Combine(Path.GetTempPath(), "shmembee-drafts-" + Guid.NewGuid());

    [Fact]
    public void RoundTripsReviewedDraftDecisions()
    {
        string path = Path.Combine(temporaryDirectory, "reviewed-drafts.json");
        var store = new ReviewedPlaylistDraftStore(path);
        var draft = new PersistedPlaylistReviewDraft
        {
            RowId = "row-1",
            MusicBeePlaylistId = "musicbee-1",
            PhonePlaylistId = "phone-1",
            MusicBeeChecksum = "left",
            PhoneChecksum = "right",
            Action = "Custom",
            IncludedOccurrenceKeys = new List<string> { "track-1#0", "track-2#0" },
            OrderSide = "Phone",
            IsConfirmed = true,
            IsDeletion = false
        };

        store.Save(new[] { draft });

        PersistedPlaylistReviewDraft loaded = Assert.Single(store.Load());
        Assert.Equal("row-1", loaded.RowId);
        Assert.Equal("musicbee-1", loaded.MusicBeePlaylistId);
        Assert.Equal("phone-1", loaded.PhonePlaylistId);
        Assert.Equal("Custom", loaded.Action);
        Assert.Equal(new[] { "track-1#0", "track-2#0" }, loaded.IncludedOccurrenceKeys);
        Assert.Equal("Phone", loaded.OrderSide);
        Assert.True(loaded.IsConfirmed);
    }

    [Fact]
    public void RecognizesChangedEndpointChecksumsAsStale()
    {
        var draft = new PersistedPlaylistReviewDraft
        {
            MusicBeeChecksum = "left",
            PhoneChecksum = "right"
        };

        Assert.Equal(
            PersistedDraftFreshness.Current,
            draft.GetFreshness("left", "right"));
        Assert.Equal(
            PersistedDraftFreshness.StaleChecksums,
            draft.GetFreshness("changed", "right"));
    }

    [Fact]
    public void MalformedFileFallsBackToNoDrafts()
    {
        Directory.CreateDirectory(temporaryDirectory);
        string path = Path.Combine(temporaryDirectory, "reviewed-drafts.json");
        File.WriteAllText(path, "{ invalid");

        IReadOnlyList<PersistedPlaylistReviewDraft> drafts =
            new ReviewedPlaylistDraftStore(path).Load();

        Assert.Empty(drafts);
    }

    [Fact]
    public void DeleteRemovesSuccessfulDraftOnly()
    {
        string path = Path.Combine(temporaryDirectory, "reviewed-drafts.json");
        var store = new ReviewedPlaylistDraftStore(path);
        store.Save(new[]
        {
            Draft("succeeded"),
            Draft("failed")
        });

        store.Delete("succeeded");

        PersistedPlaylistReviewDraft remaining = Assert.Single(store.Load());
        Assert.Equal("failed", remaining.RowId);
    }

    public void Dispose()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static PersistedPlaylistReviewDraft Draft(string rowId) =>
        new PersistedPlaylistReviewDraft
        {
            RowId = rowId,
            MusicBeeChecksum = "left",
            PhoneChecksum = "right",
            Action = "TakeMusicBee",
            OrderSide = "MusicBee",
            IsConfirmed = true
        };
}
