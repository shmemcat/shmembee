using System.Text;
using Microsoft.Data.Sqlite;
using Shmembee.Application.Reconciliation;
using Shmembee.Core.Paths;
using Shmembee.Core.Playlists;
using Shmembee.Core.Reconciliation;
using Shmembee.Core.Resolution;
using Shmembee.Infrastructure.Persistence;
using Shmembee.Infrastructure.Playlists;

namespace Shmembee.Core.Tests;

public sealed class Phase2Tests
{
    private static readonly string[] ReorderedTracks = { "b", "a" };
    private static readonly string[] DuplicateTracks = { "a", "a", "b" };

    [Fact]
    public void M3uParserPreservesOrderDuplicatesUnicodeAndNormalizesGoneMadPaths()
    {
        const string text = """
            #EXTM3U
            #EXTINF:98,ケビン・ペンキン - Kansas
            Music/Album/Kansas.mp3
            Music/Album/Kansas.mp3
            /storage/emulated/0/Music/言ノ葉/言ノ葉.mp3
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));

        ParsedPlaylist playlist = new M3uPlaylistParser().Parse(
            stream,
            "Fixture",
            "Fixture.m3u");

        Assert.Equal(3, playlist.Entries.Count);
        Assert.Equal("Music/Album/Kansas.mp3", playlist.Entries[0].NormalizedPhonePath);
        Assert.Equal(
            playlist.Entries[0].NormalizedPhonePath,
            playlist.Entries[1].NormalizedPhonePath);
        Assert.Equal("Music/言ノ葉/言ノ葉.mp3", playlist.Entries[2].NormalizedPhonePath);
        Assert.Equal("ケビン・ペンキン - Kansas", playlist.Entries[0].Title);
        Assert.Equal(98, playlist.Entries[0].DurationSeconds);
    }

    [Fact]
    public void ResolverUsesApprovedMappingBeforeAmbiguousFilename()
    {
        var reference = new TrackReference("Music/Phone/Song.mp3");
        var first = new LibraryTrack("first", @"D:\First\Song.mp3");
        var approved = new LibraryTrack("approved", @"D:\Second\Song.mp3");
        var mappings = new Dictionary<string, string>
        {
            ["Music/Phone/Song.mp3"] = approved.Url
        };

        ResolutionResult result = new TrackResolver().Resolve(
            reference,
            new[] { first, approved },
            mappings);

        Assert.Equal(ResolutionStatus.Matched, result.Status);
        Assert.Equal(MatchConfidence.ApprovedMapping, result.Confidence);
        Assert.Same(approved, result.Match);
    }

    [Fact]
    public void ResolverBlocksAmbiguousFilename()
    {
        ResolutionResult result = new TrackResolver().Resolve(
            new TrackReference("Music/Phone/Song.mp3"),
            new[]
            {
                new LibraryTrack("first", @"D:\First\Song.mp3"),
                new LibraryTrack("second", @"D:\Second\Song.mp3")
            });

        Assert.Equal(ResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void ThreeWayReconcilerAcceptsOneSidedAndSameChanges()
    {
        Guid playlistId = Guid.NewGuid();
        PlaylistSnapshot commonBase = Snapshot(playlistId, "a", "b");
        PlaylistSnapshot musicBeeChanged = Snapshot(playlistId, "b", "a");
        PlaylistSnapshot phoneUnchanged = Snapshot(playlistId, "a", "b");
        var reconciler = new ThreeWayPlaylistReconciler();

        ReconciliationResult oneSided = reconciler.Reconcile(
            commonBase,
            musicBeeChanged,
            phoneUnchanged);
        ReconciliationResult sameChange = reconciler.Reconcile(
            commonBase,
            musicBeeChanged,
            Snapshot(playlistId, "b", "a"));

        Assert.Equal(ReconciliationOutcome.MusicBeeOnly, oneSided.Outcome);
        Assert.Equal(ReorderedTracks, oneSided.ProposedTracks.Select(x => x.Value));
        Assert.Equal(ReconciliationOutcome.SameChange, sameChange.Outcome);
    }

    [Fact]
    public void ThreeWayReconcilerRequiresReviewForDifferentConcurrentChanges()
    {
        Guid playlistId = Guid.NewGuid();

        ReconciliationResult result = new ThreeWayPlaylistReconciler().Reconcile(
            Snapshot(playlistId, "a", "b"),
            Snapshot(playlistId, "b", "a"),
            Snapshot(playlistId, "a", "c"));

        Assert.Equal(ReconciliationOutcome.Conflict, result.Outcome);
        Assert.True(result.RequiresReview);
        Assert.Empty(result.ProposedTracks);
    }

    [Fact]
    public void SnapshotStorePersistsOrderAndDuplicateOccurrences()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "shmembee-tests",
            Guid.NewGuid().ToString("N"));
        string databasePath = Path.Combine(directory, "state.db");

        try
        {
            PlaylistSnapshot snapshot = Snapshot(Guid.NewGuid(), "a", "a", "b");
            new PlaylistSnapshotStore(databasePath).Save(
                "phone",
                "GoneMAD",
                snapshot,
                1,
                "phone",
                "checksum");

            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
SELECT position, track_id, occurrence_id
FROM playlist_snapshot_entries
ORDER BY position;";
            using SqliteDataReader reader = command.ExecuteReader();
            var rows = new List<(long Position, string TrackId, string OccurrenceId)>();
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
            }

            Assert.Equal(DuplicateTracks, rows.Select(row => row.TrackId));
            Assert.Equal(3, rows.Select(row => row.OccurrenceId).Distinct().Count());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ReadOnlyServiceBlocksUnresolvedPhoneEntries()
    {
        Guid playlistId = Guid.NewGuid();
        PlaylistSnapshot commonBase = Snapshot(playlistId, "a");
        PlaylistSnapshot musicBee = Snapshot(playlistId, "a");
        var service = new ReadOnlyReconciliationService(
            new TrackResolver(),
            new ThreeWayPlaylistReconciler());

        ReadOnlyReconciliationResult result = service.Reconcile(
            commonBase,
            musicBee,
            new[] { new TrackReference("Music/Unknown.mp3") },
            new[] { new LibraryTrack("a", @"D:\Music\Known.mp3") });

        Assert.True(result.IsBlocked);
        Assert.Equal(ResolutionStatus.Unmatched, result.Resolutions[0].Status);
    }

    [Theory]
    [InlineData("Music/Artist/Track.mp3", "Music/Artist/Track.mp3")]
    [InlineData(
        "/storage/emulated/0/Music/Artist/Track.mp3",
        "Music/Artist/Track.mp3")]
    [InlineData(
        @"\storage\emulated\0\Music\Artist\Track.mp3",
        "Music/Artist/Track.mp3")]
    public void PhonePathNormalizationProducesStableAliases(string path, string expected)
    {
        Assert.Equal(expected, TrackPathNormalizer.NormalizePhonePath(path));
    }

    private static PlaylistSnapshot Snapshot(Guid playlistId, params string[] trackIds) =>
        new(
            playlistId,
            "Fixture",
            "Fixture.m3u",
            trackIds.Select(trackId => new PlaylistEntry(
                Guid.NewGuid(),
                new TrackIdentity(trackId),
                trackId)),
            DateTimeOffset.UtcNow);
}
