using System.Text;
using Shmembee.Application.Desktop;
using Shmembee.Core.Reconciliation;
using Shmembee.Core.Resolution;
using Shmembee.Infrastructure.Playlists;

namespace Shmembee.Core.Tests;

public sealed class DailyWorkflowAndM3uImportPreviewTests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "shmembee-workflow-preview-tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(ReconciliationOutcome.Unchanged, PlaylistWorkflowStatus.Unchanged)]
    [InlineData(ReconciliationOutcome.MusicBeeOnly, PlaylistWorkflowStatus.MusicBeeChanged)]
    [InlineData(ReconciliationOutcome.PhoneOnly, PlaylistWorkflowStatus.PhoneChanged)]
    [InlineData(ReconciliationOutcome.SameChange, PlaylistWorkflowStatus.SameChange)]
    [InlineData(ReconciliationOutcome.Conflict, PlaylistWorkflowStatus.Conflict)]
    public void WorkflowStatusMapsReconciliationOutcome(
        ReconciliationOutcome outcome,
        PlaylistWorkflowStatus expected)
    {
        Assert.Equal(
            expected,
            PlaylistWorkflowSummary.FromOutcome(outcome, requiresReview: false));
    }

    [Theory]
    [InlineData(ReconciliationOutcome.Unchanged)]
    [InlineData(ReconciliationOutcome.MusicBeeOnly)]
    [InlineData(ReconciliationOutcome.PhoneOnly)]
    [InlineData(ReconciliationOutcome.SameChange)]
    [InlineData(ReconciliationOutcome.Conflict)]
    public void WorkflowStatusMapsAnyReviewRequirementToConflict(
        ReconciliationOutcome outcome)
    {
        Assert.Equal(
            PlaylistWorkflowStatus.Conflict,
            PlaylistWorkflowSummary.FromOutcome(outcome, requiresReview: true));
    }

    [Theory]
    [InlineData(PlaylistWorkflowStatus.MusicBeeChanged, 0, true)]
    [InlineData(PlaylistWorkflowStatus.PhoneChanged, 0, true)]
    [InlineData(PlaylistWorkflowStatus.SameChange, 0, true)]
    [InlineData(PlaylistWorkflowStatus.Unchanged, 0, false)]
    [InlineData(PlaylistWorkflowStatus.Conflict, 0, false)]
    [InlineData(PlaylistWorkflowStatus.MissingBaseline, 0, false)]
    [InlineData(PlaylistWorkflowStatus.MissingEndpoint, 0, false)]
    [InlineData(PlaylistWorkflowStatus.SetupError, 0, false)]
    [InlineData(PlaylistWorkflowStatus.MusicBeeChanged, 1, false)]
    [InlineData(PlaylistWorkflowStatus.UnresolvedTracks, 1, false)]
    public void WorkflowSummaryAllowsOnlyResolvedActionableChanges(
        PlaylistWorkflowStatus status,
        int unresolvedTrackCount,
        bool expected)
    {
        var summary = new PlaylistWorkflowSummary(
            "playlist",
            "Fixture",
            "Fixture.m3u",
            status,
            musicBeeTrackCount: 2,
            phoneTrackCount: 2,
            unresolvedTrackCount,
            lastAcceptedUtc: null);

        Assert.Equal(expected, summary.CanApply);
    }

    [Fact]
    public void PreviewReportsMatchedAndUnmatchedEntries()
    {
        string playlistPath = WritePlaylist(
            "Fixture.m3u8",
            "Music/Artist/Matched.mp3",
            "Music/Artist/Missing.mp3");
        var library = new[]
        {
            new LibraryTrack("matched", @"D:\Library\Music\Artist\Matched.mp3")
        };

        M3uImportPreview preview = new M3uImportPreviewService().Preview(
            playlistPath,
            library);

        Assert.Equal(2, preview.Entries.Count);
        Assert.Equal(1, preview.MatchedCount);
        Assert.Equal(0, preview.AmbiguousCount);
        Assert.Equal(1, preview.UnmatchedCount);
        Assert.Equal(ResolutionStatus.Matched, preview.Entries[0].Resolution.Status);
        Assert.Same(library[0], preview.Entries[0].Resolution.Match);
        Assert.Equal(ResolutionStatus.Unmatched, preview.Entries[1].Resolution.Status);
    }

    [Fact]
    public void PreviewPreservesAndCountsDuplicatePaths()
    {
        string playlistPath = WritePlaylist(
            "Duplicates.m3u",
            "Music/Artist/Track.mp3",
            "music/artist/track.mp3",
            "Music/Artist/Other.mp3",
            "Music/Artist/Track.mp3");

        M3uImportPreview preview = new M3uImportPreviewService().Preview(
            playlistPath,
            Array.Empty<LibraryTrack>());

        Assert.Equal(4, preview.Entries.Count);
        Assert.Equal(2, preview.DuplicateCount);
        Assert.Equal(
            new[]
            {
                "Music/Artist/Track.mp3",
                "music/artist/track.mp3",
                "Music/Artist/Other.mp3",
                "Music/Artist/Track.mp3"
            },
            preview.Entries.Select(entry => entry.Parsed.NormalizedPhonePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private string WritePlaylist(string fileName, params string[] entries)
    {
        Directory.CreateDirectory(temporaryDirectory);
        string path = Path.Combine(temporaryDirectory, fileName);
        File.WriteAllText(
            path,
            string.Join("\n", entries) + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
