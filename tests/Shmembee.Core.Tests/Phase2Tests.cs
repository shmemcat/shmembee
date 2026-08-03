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

    [Theory]
    [InlineData("1-01 - Trinkets.mp3", "Trinkets", 1, 1)]
    [InlineData("1-01 Trinkets.mp3", "Trinkets", 1, 1)]
    [InlineData("17 - Twinz.mp3", "Twinz", null, 17)]
    [InlineData("1- - Wicked Game.mp3", "Wicked Game", null, null)]
    public void PhoneFileNameParserSupportsCurrentAndLegacyTemplates(
        string fileName,
        string expectedTitle,
        int? expectedDisc,
        int? expectedTrack)
    {
        PhoneFileNameMetadata parsed = PhoneFileNameParser.Parse(fileName);

        Assert.Equal(expectedTitle, parsed.Title);
        Assert.Equal(expectedDisc, parsed.DiscNumber);
        Assert.Equal(expectedTrack, parsed.TrackNumber);
    }

    [Theory]
    [InlineData("02 - EVERGLOW - DUN DUN.mp3", "EVERGLOW", "DUN DUN", 2)]
    [InlineData("Chris Isaak - Wicked Game.mp3", "Chris Isaak", "Wicked Game", null)]
    [InlineData("- Chris Isaak - Wicked Game.mp3", "Chris Isaak", "Wicked Game", null)]
    [InlineData("09 - Boney M. - Rasputin.mp3", "Boney M", "Rasputin", 9)]
    [InlineData("23 - N.W.A. - Express Yourself.mp3", "N.W.A", "Express Yourself", 23)]
    [InlineData(
        "04 - Dale Earnhardt Jr. Jr. - Dancefloor.mp3",
        "Dale Earnhardt Jr. Jr",
        "Dancefloor",
        4)]
    [InlineData("The 1st Mini Album-02 - DUN DUN.mp3", "EVERGLOW 에버글로우", "DUN DUN", 2)]
    public void PhoneFileNameParserSupportsArtistBasedPhoneTemplate(
        string fileName,
        string artist,
        string expectedTitle,
        int? expectedTrack)
    {
        PhoneFileNameMetadata parsed = PhoneFileNameParser.Parse(
            fileName,
            artist);

        Assert.Equal(expectedTitle, parsed.Title);
        Assert.Null(parsed.DiscNumber);
        Assert.Equal(expectedTrack, parsed.TrackNumber);
    }

    [Fact]
    public void ResolverMatchesRealPostReorganizationEverglowPath()
    {
        PhoneFileNameMetadata parsed = PhoneFileNameParser.Parse(
            "The 1st Mini Album-02 - DUN DUN.mp3",
            "EVERGLOW 에버글로우");
        var expected = new LibraryTrack(
            "everglow",
            @"D:\Music\EVERGLOW 에버글로우\REMINISCENCE\02 EVERGLOW 에버글로우 - DUN DUN.mp3",
            artist: "EVERGLOW 에버글로우",
            title: "DUN DUN",
            album: "REMINISCENCE",
            trackNumber: 2);

        ResolutionResult result = new TrackResolver().Resolve(
            new TrackReference(
                "Music/EVERGLOW 에버글로우/REMINISCENCE/The 1st Mini Album-02 - DUN DUN.mp3",
                artist: "EVERGLOW 에버글로우",
                title: parsed.Title,
                album: "REMINISCENCE",
                trackNumber: parsed.TrackNumber),
            new[] { expected });

        Assert.Equal(ResolutionStatus.Matched, result.Status);
        Assert.Same(expected, result.Match);
    }

    [Fact]
    public void ResolverMatchesCompilationFolderByAlbumArtist()
    {
        var expected = new LibraryTrack(
            "princess-and-frog",
            @"D:\Music\Randy Newman\The Princess and the Frog\13 Randy Newman - The Frog Hunters - Gator Down.mp3",
            artist: "Randy Newman",
            title: "The Frog Hunters - Gator Down",
            durationSeconds: 215,
            albumArtist: "Various Artists",
            album: "The Princess and the Frog",
            trackNumber: 13);

        ResolutionResult result = new TrackResolver().Resolve(
            new TrackReference(
                "Music/Various Artists/The Princess and the Frog/13 - The Frog Hunters - Gator Down.mp3",
                artist: "Various Artists",
                title: "The Frog Hunters - Gator Down",
                durationSeconds: 215,
                albumArtist: "Various Artists",
                album: "The Princess and the Frog",
                trackNumber: 13),
            new[]
            {
                expected,
                new LibraryTrack(
                    "wrong-track",
                    @"D:\Music\Other\The Princess and the Frog\14 Other.mp3",
                    artist: "Other",
                    title: "The Frog Hunters - Gator Down",
                    durationSeconds: 215,
                    albumArtist: "Various Artists",
                    album: "The Princess and the Frog",
                    trackNumber: 14)
            });

        Assert.Equal(ResolutionStatus.Matched, result.Status);
        Assert.Same(expected, result.Match);
    }

    [Fact]
    public void ResolverUsesAlbumTrackAndDurationWhenFolderArtistIsNotTaggedArtist()
    {
        var expected = new LibraryTrack(
            "beauty-and-beast",
            @"D:\Music\Celine Dion & Peabo Bryson\Beauty And The Beast\18 Beauty And The Beast.mp3",
            artist: "Celine Dion & Peabo Bryson",
            title: "Beauty And The Beast",
            durationSeconds: 247,
            albumArtist: "Alan Menken",
            album: "Beauty And The Beast",
            trackNumber: 18);

        ResolutionResult result = new TrackResolver().Resolve(
            new TrackReference(
                "Music/Various Artists/Beauty And The Beast/18 - Beauty And The Beast.mp3",
                artist: "Various Artists",
                title: "Beauty And The Beast",
                durationSeconds: 247,
                albumArtist: "Various Artists",
                album: "Beauty And The Beast",
                trackNumber: 18),
            new[]
            {
                expected,
                new LibraryTrack(
                    "different-recording",
                    @"D:\Music\Various Artists\Beauty And The Beast\10 Beauty And The Beast.mp3",
                    artist: "Various Artists",
                    title: "Beauty And The Beast",
                    durationSeconds: 167,
                    albumArtist: "Various Artists",
                    album: "Beauty And The Beast",
                    trackNumber: 10)
            });

        Assert.Equal(ResolutionStatus.Matched, result.Status);
        Assert.Same(expected, result.Match);
    }

    [Fact]
    public void ResolverMatchesPhoneTitleWhenLibraryTitleIncludesArtistPrefix()
    {
        var expected = new LibraryTrack(
            "frog-hunters",
            @"D:\Music\Randy Newman\The Princess and the Frog\13 Randy Newman - The Frog Hunters - Gator Down.mp3",
            artist: "Randy Newman",
            title: "Randy Newman - The Frog Hunters - Gator Down",
            durationSeconds: 364,
            albumArtist: "Various Artists",
            album: "The Princess and the Frog",
            trackNumber: null);

        ResolutionResult result = new TrackResolver().Resolve(
            new TrackReference(
                "Music/Randy Newman/The Princess and the Frog/13 - The Frog Hunters - Gator Down.mp3",
                artist: "Randy Newman",
                title: "The Frog Hunters - Gator Down",
                durationSeconds: 364,
                albumArtist: "Randy Newman",
                album: "The Princess and the Frog",
                trackNumber: 13),
            new[] { expected });

        Assert.Equal(ResolutionStatus.Matched, result.Status);
        Assert.Same(expected, result.Match);
    }

    [Fact]
    public void ResolverUsesPhoneTrackNumberWhenLibraryTrackNumberIsMissing()
    {
        var expected = new LibraryTrack(
            "missing-number",
            @"D:\Music\Alan Menken\Hercules\1-02 - Go the Distance.mp3",
            artist: "Michael Bolton",
            title: "Go the Distance",
            durationSeconds: 282,
            album: "Hercules",
            trackNumber: null);

        ResolutionResult result = new TrackResolver().Resolve(
            new TrackReference(
                "Music/Alan Menken/Hercules/1-02 - Go the Distance.mp3",
                artist: "Alan Menken",
                title: "Go the Distance",
                durationSeconds: 282,
                albumArtist: "Alan Menken",
                album: "Hercules",
                discNumber: 1,
                trackNumber: 2),
            new[] { expected });

        Assert.Equal(ResolutionStatus.Matched, result.Status);
        Assert.Same(expected, result.Match);
    }

    [Fact]
    public void ResolverMatchesCanonicallyEquivalentUnicodeMetadata()
    {
        var expected = new LibraryTrack(
            "accented",
            @"D:\Music\Beyoncé\Album\01 - Song.mp3",
            artist: "Beyoncé",
            title: "Café",
            album: "Album",
            trackNumber: 1);

        ResolutionResult result = new TrackResolver().Resolve(
            new TrackReference(
                "Music/Beyoncé/Album/01 - Café.mp3",
                artist: "Beyoncé",
                title: "Café",
                album: "Album",
                trackNumber: 1),
            new[] { expected });

        Assert.Equal(ResolutionStatus.Matched, result.Status);
        Assert.Same(expected, result.Match);
    }

    [Fact]
    public void ResolverRejectsExplicitTrackNumberMismatch()
    {
        ResolutionResult result = new TrackResolver().Resolve(
            new TrackReference(
                "Music/Artist/Album/02 - Song.mp3",
                artist: "Artist",
                title: "Song",
                album: "Album",
                trackNumber: 2),
            new[]
            {
                new LibraryTrack(
                    "wrong-track",
                    @"D:\Music\Artist\Album\03 - Song.mp3",
                    artist: "Artist",
                    title: "Song",
                    album: "Album",
                    trackNumber: 3)
            });

        Assert.Equal(ResolutionStatus.Unmatched, result.Status);
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
    public void ResolverMatchesPhoneTemplateMetadataUsingAlbumArtistAndAlbum()
    {
        var expected = new LibraryTrack(
            "expected",
            @"D:\Music\Featured Artist\Album\08 Featured Artist - Song.mp3",
            artist: "Featured Artist",
            title: "Song",
            albumArtist: "Various Artists",
            album: "Album");

        ResolutionResult result = new TrackResolver().Resolve(
            new TrackReference(
                "Music/Various Artists/Album/1-08 - Song.mp3",
                title: "Song",
                albumArtist: "Various Artists",
                album: "Album"),
            new[]
            {
                expected,
                new LibraryTrack(
                    "other",
                    @"D:\Music\Other\Other Album\08 Other - Song.mp3",
                    artist: "Other",
                    title: "Song",
                    albumArtist: "Other",
                    album: "Other Album")
            });

        Assert.Equal(ResolutionStatus.Matched, result.Status);
        Assert.Equal(MatchConfidence.PhoneTemplateMetadata, result.Confidence);
        Assert.Same(expected, result.Match);
    }

    [Fact]
    public void ResolverBlocksAmbiguousPhoneTemplateMetadata()
    {
        ResolutionResult result = new TrackResolver().Resolve(
            new TrackReference(
                "Music/Artist/Album/# - Song.mp3",
                title: "Song",
                albumArtist: "Artist",
                album: "Album"),
            new[]
            {
                new LibraryTrack(
                    "first",
                    @"D:\First.mp3",
                    title: "Song",
                    albumArtist: "Artist",
                    album: "Album"),
                new LibraryTrack(
                    "second",
                    @"D:\Second.mp3",
                    title: "Song",
                    albumArtist: "Artist",
                    album: "Album")
            });

        Assert.Equal(ResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Theory]
    [InlineData(
        "Do I Wanna Know ",
        "Do I Wanna Know?",
        "AM",
        "AM",
        "Arctic Monkeys",
        "Arctic Monkeys")]
    [InlineData(
        "Somethinggreater",
        "Somethinggreater",
        "Day Night",
        "Day/Night",
        "Parcels",
        "Parcels")]
    [InlineData(
        "Better Place (From TROLLS Band Together)",
        "Better Place (From TROLLS Band Together)",
        "Better Place (From TROLLS Band Together)",
        "Better Place (From TROLLS Band Together)",
        "nsync",
        "*NSYNC")]
    public void ResolverMatchesFilesystemSafePhoneMetadata(
        string phoneTitle,
        string libraryTitle,
        string phoneAlbum,
        string libraryAlbum,
        string phoneAlbumArtist,
        string libraryAlbumArtist)
    {
        var expected = new LibraryTrack(
            "expected",
            @"D:\Music\Track.mp3",
            title: libraryTitle,
            albumArtist: libraryAlbumArtist,
            album: libraryAlbum);

        ResolutionResult result = new TrackResolver().Resolve(
            new TrackReference(
                "Music/phone/path.mp3",
                title: phoneTitle,
                albumArtist: phoneAlbumArtist,
                album: phoneAlbum),
            new[] { expected });

        Assert.Equal(ResolutionStatus.Matched, result.Status);
        Assert.Equal(MatchConfidence.PhoneTemplateMetadata, result.Confidence);
        Assert.Same(expected, result.Match);
    }

    [Fact]
    public void ResolverUsesDiscAndTrackNumbersToDisambiguateMetadata()
    {
        var expected = new LibraryTrack(
            "expected",
            @"D:\Music\Disc 1\01 Song.mp3",
            title: "Song",
            albumArtist: "Artist",
            album: "Album",
            discNumber: 1,
            trackNumber: 1);

        ResolutionResult result = new TrackResolver().Resolve(
            new TrackReference(
                "Music/Artist/Album/1-01 - Song.mp3",
                title: "Song",
                albumArtist: "Artist",
                album: "Album",
                discNumber: 1,
                trackNumber: 1),
            new[]
            {
                expected,
                new LibraryTrack(
                    "other",
                    @"D:\Music\Disc 2\01 Song.mp3",
                    title: "Song",
                    albumArtist: "Artist",
                    album: "Album",
                    discNumber: 2,
                    trackNumber: 1)
            });

        Assert.Equal(ResolutionStatus.Matched, result.Status);
        Assert.Same(expected, result.Match);
    }

    [Fact]
    public void ResolverIndexCanBeReusedAcrossReferences()
    {
        var first = new LibraryTrack(
            "first",
            @"D:\Music\Artist\Album\First.mp3",
            phoneAliases: new[] { "Music/Artist/Album/First.mp3" });
        var second = new LibraryTrack(
            "second",
            @"D:\Music\Artist\Album\Second.mp3");
        TrackResolverIndex index = new TrackResolver().CreateIndex(
            new[] { first, second });

        ResolutionResult alias = index.Resolve(
            new TrackReference("Music/Artist/Album/First.mp3"));
        ResolutionResult suffix = index.Resolve(
            new TrackReference("Artist/Album/Second.mp3"));

        Assert.Same(first, alias.Match);
        Assert.Equal(MatchConfidence.ExpectedPhonePath, alias.Confidence);
        Assert.Same(second, suffix.Match);
        Assert.Equal(MatchConfidence.UniquePathSuffix, suffix.Confidence);
    }

    [Fact]
    public void ResolverTreatsDuplicateLibraryRowsForSameFileAsOneCandidate()
    {
        var first = new LibraryTrack(
            "first",
            @"D:\Music\Artist\Album\Song.mp3",
            title: "Song",
            albumArtist: "Artist",
            album: "Album");

        ResolutionResult result = new TrackResolver().Resolve(
            new TrackReference(
                "Music/Artist/Album/# - Song.mp3",
                title: "Song",
                albumArtist: "Artist",
                album: "Album"),
            new[]
            {
                first,
                new LibraryTrack(
                    "duplicate",
                    @"d:/music/artist/album/song.mp3",
                    title: "Song",
                    albumArtist: "Artist",
                    album: "Album")
            });

        Assert.Equal(ResolutionStatus.Matched, result.Status);
        Assert.Same(first, result.Match);
    }

    [Fact]
    public void ResolverPrefersExactTitleWithinPunctuationNormalizedCandidates()
    {
        var exact = new LibraryTrack(
            "exact",
            @"D:\Music\Escapism single.mp3",
            title: "Escapism.",
            albumArtist: "RAYE",
            album: "Escapism. The Thrill Is Gone",
            trackNumber: 1);

        ResolutionResult result = new TrackResolver().Resolve(
            new TrackReference(
                "Music/RAYE/Escapism. The Thrill Is Gone/01 - Escapism..mp3",
                title: "Escapism.",
                albumArtist: "RAYE",
                album: "Escapism. The Thrill Is Gone",
                trackNumber: 1),
            new[]
            {
                exact,
                new LibraryTrack(
                    "punctuation-variant",
                    @"D:\Music\Escapism album.mp3",
                    title: "Escapism",
                    albumArtist: "RAYE",
                    album: "Escapism. The Thrill Is Gone",
                    trackNumber: 1)
            });

        Assert.Equal(ResolutionStatus.Matched, result.Status);
        Assert.Same(exact, result.Match);
    }

    [Fact]
    public void ResolverPrefersExactAlbumWithinNormalizedCandidates()
    {
        var exact = new LibraryTrack(
            "exact",
            @"D:\Music\High School Musical 2\01 Song.mp3",
            title: "What Time Is It",
            albumArtist: "Various Artists",
            album: "High School Musical 2",
            trackNumber: 1);

        ResolutionResult result = new TrackResolver().Resolve(
            new TrackReference(
                "Music/Various Artists/High School Musical 2/01 - What Time Is It.mp3",
                title: "What Time Is It",
                albumArtist: "Various Artists",
                album: "High School Musical 2",
                trackNumber: 1),
            new[]
            {
                exact,
                new LibraryTrack(
                    "punctuation-variant",
                    @"D:\Music\High School Musical II\01 Song.mp3",
                    title: "What Time Is It",
                    albumArtist: "Various Artists",
                    album: "High-School Musical 2",
                    trackNumber: 1)
            });

        Assert.Equal(ResolutionStatus.Matched, result.Status);
        Assert.Same(exact, result.Match);
    }

    [Fact]
    public void ResolverUsesPairedPlaylistMembershipToBreakMetadataTie()
    {
        var expected = new LibraryTrack(
            "expected",
            @"D:\Music\Playlist version.mp3",
            title: "G.D.S.",
            albumArtist: "DIR EN GREY",
            album: "朔-saku-",
            discNumber: 1,
            trackNumber: 3);
        var duplicate = new LibraryTrack(
            "duplicate",
            @"D:\Music\Other version.mp3",
            title: "G.D.S.",
            albumArtist: "DIR EN GREY",
            album: "朔-saku-",
            discNumber: 1,
            trackNumber: 3);
        TrackResolverIndex resolver = new TrackResolver().CreateIndex(
            new[] { expected, duplicate });

        ResolutionResult result = resolver.Resolve(
            new TrackReference(
                "Music/DIR EN GREY/朔-saku-/1-03 - G.D.S..mp3",
                title: "G.D.S.",
                albumArtist: "DIR EN GREY",
                album: "朔-saku-",
                discNumber: 1,
                trackNumber: 3),
            preferredUrls: new[] { expected.Url });

        Assert.Equal(ResolutionStatus.Matched, result.Status);
        Assert.Same(expected, result.Match);
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
