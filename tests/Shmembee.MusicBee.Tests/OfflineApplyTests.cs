using System.Text;
using Microsoft.Data.Sqlite;
using MusicBeePlugin;
using Shmembee.Application.Ports;
using Shmembee.Core.Reconciliation;
using Shmembee.Infrastructure.Persistence;
using Shmembee.Infrastructure.Settings;

namespace Shmembee.MusicBee.Tests;

public sealed class OfflineApplyTests : IDisposable
{
    private const string First = @"D:\Music\Artist\Album\01 - Artist - First.mp3";
    private const string Second = @"D:\Music\Artist\Album\02 - Artist - Second.mp3";
    private const string PhoneFirst = "Music/Artist/Album/01 - Artist - First.mp3";
    private const string PhoneSecond = "Music/Artist/Album/02 - Artist - Second.mp3";
    private readonly string root = Path.Combine(Path.GetTempPath(), "shmembee-offline-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string[]> playlists = new();
    private readonly FakePhone phone = new();
    private int libraryReads;
    private int writes;
    private string? failPlaylist;
    private Action? afterWrite;
    private bool duplicateTitles;
    private string? firstTitle;

    [Fact]
    public void ManualMatchOverridesVersionDifferenceAndSurvivesRestartWithoutTagWrites()
    {
        firstTitle = "First (Live)";
        const string oldPath = "Music/Artist/Old Album/99 - Artist - First.mp3";
        PlaylistSyncController controller = Controller("Road");
        phone.Files.Clear();
        phone.Files.Add(new PhonePlaylistContent("Road", "Road.m3u", Encoding.UTF8.GetBytes(oldPath + "\n")));
        var row = Assert.Single(controller.RefreshPlaylistRows());
        var musicBee = Assert.Single(row.Diff!.Occurrences, item => item.Membership == OccurrenceMembership.MusicBeeOnly);
        var unmatchedPhone = Assert.Single(row.Diff.Occurrences, item => item.Membership == OccurrenceMembership.PhoneOnly);

        controller.MatchTracksManually(row.RowId, musicBee.Key, unmatchedPhone.Key);
        int mediaReads = phone.MediaReads;
        int playlistReads = phone.PlaylistReads;
        phone.Disconnected = true;
        var updated = Assert.Single(controller.RefreshPlaylistRows(reloadPhoneSnapshot: false));
        var matched = Assert.Single(updated.Diff!.Occurrences);
        Assert.Equal(OccurrenceMembership.Both, matched.Membership);
        Assert.Equal(First, matched.PhoneEntry!.MusicBeeValue);
        Assert.Equal(mediaReads, phone.MediaReads);
        Assert.Equal(playlistReads, phone.PlaylistReads);

        playlists.Clear();
        phone.Files.Clear();
        phone.Disconnected = false;
        var restarted = Controller("Road");
        phone.Files.Clear();
        phone.Files.Add(new PhonePlaylistContent("Road", "Road.m3u", Encoding.UTF8.GetBytes(oldPath + "\n")));
        var persisted = Assert.Single(Assert.Single(restarted.RefreshPlaylistRows()).Diff!.Occurrences);
        Assert.Equal(OccurrenceMembership.Both, persisted.Membership);
        Assert.Equal(First, persisted.PhoneEntry!.MusicBeeValue);
        Assert.Equal("First (Live)", firstTitle);
        Assert.Equal(0, writes);
    }

    [Fact]
    public void ManualMatchTakesPrecedenceOverAutomaticMediaMapping()
    {
        PlaylistSyncController controller = Controller("Road");
        var row = Assert.Single(controller.RefreshPlaylistRows());
        var musicBee = Assert.Single(row.Diff!.Occurrences, item => item.Membership == OccurrenceMembership.MusicBeeOnly);
        var phoneRow = Assert.Single(row.Diff.Occurrences, item => item.Membership == OccurrenceMembership.PhoneOnly);
        controller.MatchTracksManually(row.RowId, musicBee.Key, phoneRow.Key);
        var updated = Assert.Single(controller.RefreshPlaylistRows(reloadPhoneSnapshot: false));
        var matched = Assert.Single(updated.Diff!.Occurrences);
        Assert.Equal(OccurrenceMembership.Both, matched.Membership);
        Assert.Equal(First, matched.PhoneEntry!.MusicBeeValue);
        Assert.Equal(PhoneSecond, matched.PhoneEntry.SourceValue);
    }

    [Fact]
    public void ManualMatchRejectsInvalidSelectionWithoutSaving()
    {
        PlaylistSyncController controller = Controller("Road");
        var row = Assert.Single(controller.RefreshPlaylistRows());
        string key = row.Diff!.Occurrences[0].Key;
        Assert.Throws<InvalidOperationException>(() => controller.MatchTracksManually(row.RowId, key, key));
        Assert.False(File.Exists(Path.Combine(root, "manual-track-matches.json")));
    }

    [Fact]
    public void ReplacementFromDifferentAlbumAndTrackNumberReconcilesAsOneOccurrence()
    {
        PlaylistSyncController controller = Controller("Road");
        const string oldPath = "Music/Artist/Old Album/99 - Artist - First.mp3";
        phone.Files.Clear();
        phone.Files.Add(new PhonePlaylistContent("Road", "Road.m3u", Encoding.UTF8.GetBytes(oldPath + "\n")));

        var row = Assert.Single(controller.RefreshPlaylistRows());
        var occurrence = Assert.Single(row.Diff!.Occurrences);
        Assert.Equal(OccurrenceMembership.Both, occurrence.Membership);
        Assert.Equal(First, occurrence.PhoneEntry!.MusicBeeValue);
        Assert.False(occurrence.PhoneEntry.MusicBeeValueUnavailable);
        Assert.Equal(0, writes);
    }

    [Fact]
    public void AmbiguousReplacementRemainsUnavailableWithCandidatesForReview()
    {
        duplicateTitles = true;
        PlaylistSyncController controller = Controller("Road");
        phone.Files.Clear();
        phone.Files.Add(new PhonePlaylistContent("Road", "Road.m3u",
            Encoding.UTF8.GetBytes("Music/Artist/Old Album/99 - Artist - First.mp3\n")));

        var row = Assert.Single(controller.RefreshPlaylistRows());
        var occurrence = Assert.Single(row.Diff!.Occurrences, item => item.Membership == OccurrenceMembership.PhoneOnly);
        Assert.True(occurrence.PhoneEntry!.MusicBeeValueUnavailable);
        Assert.Contains("Ambiguous", occurrence.PhoneEntry.UnavailableReason);
        Assert.Contains("review", occurrence.PhoneEntry.UnavailableReason);
        Assert.Contains(First, occurrence.PhoneEntry.UnavailableReason);
        Assert.Contains(Second, occurrence.PhoneEntry.UnavailableReason);
        Assert.Equal(0, writes);
    }

    [Fact]
    public void MusicBeeOnlyPlaylistExportsOfflineWithOrderAndDuplicatesPreserved()
    {
        PlaylistSyncController controller = Controller("Road");
        playlists["Road"] = [First, Second, First];
        phone.Files.Clear();
        var row = Assert.Single(controller.RefreshPlaylistRows());
        phone.Disconnected = true;
        var result = controller.ApplyAll([Draft(row, PlaylistLandingAction.TakeMusicBee)], default);
        Assert.Equal(1, result.SucceededCount);
        string export = Assert.Single(Directory.GetFiles(Path.Combine(root, "exports"), "*.m3u", SearchOption.AllDirectories));
        Assert.Equal(new[] { PhoneFirst, PhoneSecond, PhoneFirst }.Select(path => "/storage/emulated/0/" + path), File.ReadAllLines(export));
        Assert.Equal(0, writes);
    }

    [Fact]
    public void PhoneOnlyPlaylistImportsIntoMusicBeeOfflineAndUpdatesCachedRow()
    {
        PlaylistSyncController controller = Controller("Road");
        playlists.Clear();
        var row = Assert.Single(controller.RefreshPlaylistRows());
        phone.Disconnected = true;
        var result = controller.ApplyAll([Draft(row, PlaylistLandingAction.TakePhone)], default);
        Assert.Equal(1, result.SucceededCount);
        Assert.Equal([Second], playlists["Road"]);
        var applied = Assert.Single(controller.LoadedRows);
        Assert.Equal("Road", applied.MusicBeePlaylistId);
        Assert.Equal("Road", applied.MusicBeeName);
        Assert.Null(applied.Diff);
    }

    [Fact]
    public void MusicBeeBackupFailurePreventsAllMutations()
    {
        PlaylistSyncController controller = Controller("Road");
        var row = Assert.Single(controller.RefreshPlaylistRows());
        phone.Disconnected = true;
        File.WriteAllText(Path.Combine(root, "backups", "MusicBee Playlists"), "blocks backup directory creation");
        var result = controller.ApplyAll([Draft(row, PlaylistLandingAction.TakePhone)], default);
        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(1, result.NotStartedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, writes);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "exports"), "*.m3u", SearchOption.AllDirectories));
    }

    [Fact]
    public void DuplicateRequestsAreRejectedBeforeWriting()
    {
        PlaylistSyncController controller = Controller("Road");
        var row = Assert.Single(controller.RefreshPlaylistRows());
        phone.Disconnected = true;
        var draft = Draft(row, PlaylistLandingAction.TakePhone);
        var result = controller.ApplyAll([draft, draft], default);
        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(2, result.NotStartedCount);
        Assert.Equal(0, writes);
    }

    [Fact]
    public void ApplySeveralBatchesAfterDisconnectDoesNotReadPhoneOrLibraryAgain()
    {
        PlaylistSyncController controller = Controller("Road", "Mix");
        var rows = controller.RefreshPlaylistRows();
        Assert.All(rows, row => Assert.NotNull(row.Diff));
        phone.Disconnected = true;
        var first = controller.ApplyAll([Draft(rows[0], PlaylistLandingAction.TakePhone)], default);
        var second = controller.ApplyAll([Draft(rows[1], PlaylistLandingAction.TakeMusicBee)], default);
        Assert.Equal(1, first.SucceededCount);
        Assert.Equal(1, second.SucceededCount);
        Assert.Equal(0, first.FailedCount + second.FailedCount);
        Assert.Equal(1, phone.MediaReads);
        Assert.Equal(1, phone.PlaylistReads);
        Assert.Equal(1, libraryReads);
        Assert.Equal(1, writes); // Taking the existing MusicBee order needs no MusicBee rewrite.
        Assert.All(controller.LoadedRows, row => Assert.Null(row.Diff));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "exports"), "*.m3u", SearchOption.AllDirectories).Length);
        Assert.Single(Directory.GetDirectories(Path.Combine(root, "backups", "Backups", "Mobile Playlist Backups")));
        var before = Directory.GetFiles(Path.Combine(root, "backups", "MusicBee Playlists"), rows[0].DisplayName + ".m3u", SearchOption.AllDirectories);
        Assert.Contains(before, path => File.ReadAllText(path).Contains(First, StringComparison.Ordinal));
        Assert.Null(new AcceptedBaselineStore(Path.Combine(root, "shmembee.db")).Load(rows[0].RowId));
        Assert.All(controller.ReadHistory(), item => Assert.Equal("exported", item.Status));
        var retry = controller.ApplyAll([Draft(rows[0], PlaylistLandingAction.TakePhone)], default);
        Assert.Equal(0, retry.SucceededCount);
        Assert.Equal(1, retry.NotStartedCount);
    }

    [Fact]
    public void FailedMusicBeeMutationRollsBackOnlyThatPlaylistAndKeepsOtherExports()
    {
        PlaylistSyncController controller = Controller("A", "B", "C");
        var rows = controller.RefreshPlaylistRows();
        phone.Disconnected = true;
        failPlaylist = "B";
        var result = controller.ApplyAll(rows.Select(row => Draft(row, PlaylistLandingAction.TakePhone)).ToList(), default);
        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal([Second], playlists["A"]);
        Assert.Equal([First], playlists["B"]);
        Assert.Equal([Second], playlists["C"]);
        var exports = Directory.GetFiles(Path.Combine(root, "exports"), "*.m3u", SearchOption.AllDirectories);
        Assert.Equal(2, exports.Length);
        Assert.DoesNotContain(exports, file => Path.GetFileName(file) == "B.m3u");
        Assert.Equal(1, phone.MediaReads);
    }

    [Fact]
    public void CancellationDuringMutationRestoresMusicBeeAndLeavesNoExport()
    {
        PlaylistSyncController controller = Controller("A", "B");
        var rows = controller.RefreshPlaylistRows();
        phone.Disconnected = true;
        using var cancellation = new CancellationTokenSource();
        afterWrite = cancellation.Cancel;
        var result = controller.ApplyAll(rows.Select(row => Draft(row, PlaylistLandingAction.TakePhone)).ToList(), cancellation.Token);
        Assert.True(result.WasCancelled);
        Assert.Equal(1, result.NotStartedCount);
        Assert.Equal(0, result.SucceededCount);
        Assert.Equal([First], playlists["A"]);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "exports"), "*.m3u", SearchOption.AllDirectories));
    }

    [Fact]
    public void PhoneOnlyDeletionProducesManualInstructionsWithoutDeviceAccess()
    {
        PlaylistSyncController controller = Controller("Road");
        phone.Files.Add(new PhonePlaylistContent("extra", "Extra.m3u", Encoding.UTF8.GetBytes(PhoneSecond + "\n")));
        var rows = controller.RefreshPlaylistRows();
        phone.Disconnected = true;
        var extra = Assert.Single(rows, row => row.PhoneName == "Extra");
        var result = controller.ApplyAll([Draft(extra, PlaylistLandingAction.TakeMusicBee)], default);
        Assert.Equal(1, result.SucceededCount);
        string manifest = Assert.Single(Directory.GetFiles(Path.Combine(root, "exports"), "TRANSFER.txt", SearchOption.AllDirectories));
        Assert.Contains("DELETE MANUALLY from the phone playlist folder: Extra.m3u", File.ReadAllText(manifest), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "exports"), "*.m3u", SearchOption.AllDirectories));
        Assert.Equal(0, writes);
    }

    [Fact]
    public void ExplicitRefreshConfirmsManualCopyEvenWhenPhoneObjectIdChanges()
    {
        PlaylistSyncController controller = Controller("Road");
        var row = Assert.Single(controller.RefreshPlaylistRows());
        var result = controller.ApplyAll([Draft(row, PlaylistLandingAction.TakeMusicBee)], default);
        Assert.Equal(1, result.SucceededCount);
        string export = Assert.Single(Directory.GetFiles(Path.Combine(root, "exports"), "*.m3u", SearchOption.AllDirectories));
        phone.Files.Clear();
        phone.Files.Add(new PhonePlaylistContent("new-object-id", "Road.m3u", File.ReadAllBytes(export)));
        var refreshed = Assert.Single(controller.RefreshPlaylistRows());
        Assert.NotEqual(row.RowId, refreshed.RowId);
        Assert.NotNull(new AcceptedBaselineStore(Path.Combine(root, "shmembee.db")).Load(refreshed.RowId));
        Assert.Equal("completed", Assert.Single(controller.ReadHistory()).Status);
    }

    [Fact]
    public void FailedRefreshInvalidatesEarlierReviews()
    {
        PlaylistSyncController controller = Controller("Road");
        var row = Assert.Single(controller.RefreshPlaylistRows());
        phone.Disconnected = true;
        Assert.Throws<IOException>(() => controller.RefreshPlaylistRows());
        var result = controller.ApplyAll([Draft(row, PlaylistLandingAction.TakePhone)], default);
        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(0, writes);
    }

    private PlaylistSyncController Controller(params string[] names)
    {
        Directory.CreateDirectory(root);
        new DesktopSettingsStore(Path.Combine(root, "settings.json")).Save(new DesktopSettings
        {
            GeneratedPlaylistPath = Path.Combine(root, "exports"),
            PostSyncBackupPath = Path.Combine(root, "backups"),
            DatabasePath = Path.Combine(root, "shmembee.db"),
            BackupPath = Path.Combine(root, "unused")
        });
        foreach (string name in names)
        {
            playlists.Add(name, [First]);
            phone.Files.Add(new PhonePlaylistContent(name, name + ".m3u", Encoding.UTF8.GetBytes(PhoneSecond + "\n")));
        }
        IEnumerator<string>? iterator = null;
        var api = new Plugin.MusicBeeApiInterface
        {
            Playlist_QueryPlaylists = () => { iterator = playlists.Keys.ToList().GetEnumerator(); return true; },
            Playlist_QueryGetNextPlaylist = () => iterator!.MoveNext() ? iterator.Current : string.Empty,
            Playlist_GetName = url => url,
            Playlist_QueryFilesEx = (string url, out string[] files) => { files = playlists[url].ToArray(); return true; },
            Playlist_SetFiles = (url, files) =>
            {
                writes++;
                playlists[url] = files.ToArray();
                afterWrite?.Invoke();
                if (url == failPlaylist) { failPlaylist = null; return false; }
                return true;
            },
            Playlist_CreatePlaylist = (_, name, files) => { playlists.Add(name, files.ToArray()); return name; },
            Playlist_DeletePlaylist = url => playlists.Remove(url),
            Library_QueryFilesEx = (string _, out string[] files) => { libraryReads++; files = [First, Second]; return true; },
            Library_GetFileTag = (url, type) => type switch
            {
                Plugin.MetaDataType.Artist or Plugin.MetaDataType.AlbumArtist => "Artist",
                Plugin.MetaDataType.Album => "Album",
                Plugin.MetaDataType.TrackTitle => url == First ? firstTitle ?? "First" : duplicateTitles ? "First" : "Second",
                Plugin.MetaDataType.TrackNo => url == First ? "1" : "2",
                _ => string.Empty
            }
        };
        return new PlaylistSyncController(api, root, phone, phone);
    }

    private static PlaylistReviewDraft Draft(HarnessPlaylistRow row, PlaylistLandingAction action)
    {
        var draft = PlaylistReviewDraft.Create(row);
        draft.Action = action;
        draft.OrderSide = action == PlaylistLandingAction.TakePhone ? PlaylistSide.Phone : PlaylistSide.MusicBee;
        draft.IsConfirmed = true;
        return draft;
    }

    private sealed class FakePhone : IPhonePlaylistSnapshotReader, IProgressivePhoneMediaPathReader
    {
        public bool Disconnected { get; set; }
        public int MediaReads { get; private set; }
        public int PlaylistReads { get; private set; }
        public List<PhonePlaylistContent> Files { get; } = [];
        public IReadOnlyList<PhonePlaylistContent> ReadPlaylistSnapshot()
        {
            if (Disconnected) { throw new IOException("Phone disconnected"); }
            PlaylistReads++;
            return Files;
        }
        public IReadOnlyList<string> ReadMediaPaths() => ReadMediaPaths(default);
        public IReadOnlyList<string> ReadMediaPaths(CancellationToken token, IProgress<PhoneMediaTraversalProgress>? progress = null)
        {
            if (Disconnected) { throw new IOException("Phone disconnected"); }
            token.ThrowIfCancellationRequested();
            MediaReads++;
            return [PhoneFirst, PhoneSecond];
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) { Directory.Delete(root, true); }
    }
}
