using Shmembee.Core.Resolution;

namespace Shmembee.Core.Tests;

public sealed class ReplacementMatcherTests
{
    [Theory]
    [InlineData("Madonna", "Miles Away", "Hard Candy", 5, "Madonna", "Miles Away", "Celebration (Deluxe Version)", 28)]
    [InlineData("Dazz Band", "Let It Whip", "Keep It Live", 1, "Dazz Band", "Let It Whip", "Best of Motown", 7)]
    [InlineData("Jazztronik", "Samurai", "Samurai", 3, "Jazztronik", "Samurai", "Breaking the Beats", 4)]
    [InlineData("Charlie Puth Feat. Kehlani", "Done For Me", "Voicenotes", 1, "Charlie Puth, Kehlani", "Done for Me (feat. Kehlani)", "Voicenotes", 5)]
    [InlineData("Missy Elliott", "Lose Control (feat. Ciara & Fat Man Scoop)", "Lose Control", 1, "Missy Elliott Ciara Fatman Scoop", "Lose Control", "The Cookbook", 4)]
    [InlineData("A Taste Of Honey", "Boogie Oogie Oogie", "A Taste Of Honey", 1, "ATaste Of Honey", "Boogie Oogie Oogie", "A Taste Of Honey", 10)]
    public void MatchesReplacementAcrossReleaseAndFeaturedArtistChanges(
        string phoneArtist, string phoneTitle, string phoneAlbum, int phoneNumber,
        string artist, string title, string album, int number)
    {
        var replacement = new LibraryTrack("replacement", @"D:\Music\new-file.flac",
            artist: artist, title: title, albumArtist: artist, album: album,
            trackNumber: number, durationSeconds: 240);
        var result = new TrackResolver().Resolve(
            new TrackReference("Music/old-file.mp3", artist: phoneArtist, title: phoneTitle,
                albumArtist: phoneArtist, album: phoneAlbum, trackNumber: phoneNumber,
                durationSeconds: 241), [replacement]);

        Assert.Equal(ResolutionStatus.Matched, result.Status);
        Assert.Same(replacement, result.Match);
        Assert.Equal(MatchConfidence.ReplacementMetadata, result.Confidence);
    }

    [Fact]
    public void FlagsMultipleVersionsEvenWhenOneIsPreferredAndUniquenessIsDisabled()
    {
        var first = Track("first", "Artist", "Song");
        var second = Track("second", "Artist", "Song");
        var result = new TrackResolver(requireUniqueMatches: false).CreateIndex([first, second])
            .Resolve(Reference(), preferredUrls: [first.Url]);

        Assert.Equal(ResolutionStatus.Ambiguous, result.Status);
        Assert.Null(result.Match);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains("review", result.Reason);
    }

    [Theory]
    [InlineData("Other Artist", "Song", 240)]
    [InlineData("Artist", "Song (Live)", 240)]
    [InlineData("Artist", "Song (Remix)", 240)]
    [InlineData("Artist", "Song (feat. Guest) (Live)", 240)]
    [InlineData("Artist", "Song", 270)]
    public void DoesNotConflateDifferentArtistsVersionsOrDurations(string artist, string title, int duration)
    {
        var result = new TrackResolver().Resolve(Reference(), [Track("other", artist, title, duration)]);
        Assert.Equal(ResolutionStatus.Unmatched, result.Status);
    }

    [Fact]
    public void SharedCompilationAlbumArtistDoesNotEstablishRecordingIdentity()
    {
        var track = new LibraryTrack("other", @"D:\Music\other.flac", artist: "Other",
            title: "Song", album: "New Album", albumArtist: "Various Artists");
        var result = new TrackResolver().Resolve(
            new TrackReference("Music/old.mp3", artist: "Various Artists", title: "Song",
                albumArtist: "Various Artists", album: "Old Album"), [track]);
        Assert.Equal(ResolutionStatus.Unmatched, result.Status);
    }

    private static TrackReference Reference() => new("Music/old.mp3", artist: "Artist",
        title: "Song", album: "Old Album", trackNumber: 1, durationSeconds: 240);

    private static LibraryTrack Track(string id, string artist, string title, int duration = 240) =>
        new(id, @"D:\Music\" + id + ".flac", artist: artist, title: title,
            album: "New Album", trackNumber: 9, durationSeconds: duration);
}
