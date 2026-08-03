using Microsoft.Data.Sqlite;
using Shmembee.Application.Ports;
using Shmembee.Application.Synchronization;
using Shmembee.Infrastructure.Diagnostics;
using Shmembee.Infrastructure.Persistence;
using Shmembee.Infrastructure.Playlists;
using Shmembee.Infrastructure.Settings;

#pragma warning disable CA1707
namespace Shmembee.Core.Tests;

public sealed class Phase4BackendTests : IDisposable
{
    private readonly string temporaryDirectory =
        Path.Combine(Path.GetTempPath(), "shmembee-phase4-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingSettingsUseConservativeDefaults()
    {
        DesktopSettings settings = new DesktopSettingsStore(
            Path.Combine(temporaryDirectory, "settings.json")).Load();

        Assert.Equal("MLE S24U", settings.DeviceName);
        Assert.Equal("Internal storage", settings.StorageName);
        Assert.Equal("gmmp/playlists", settings.PlaylistFolder);
        Assert.Equal("Music", settings.PhoneMediaFolder);
        Assert.Equal(
            @"D:\My Documents\Shmembee Backups",
            settings.PostSyncBackupPath);
        Assert.Empty(settings.PlaylistAssociations);
    }

    [Fact]
    public void SettingsRoundTripAndRejectUnsafeAssociations()
    {
        string path = Path.Combine(temporaryDirectory, "settings.json");
        var store = new DesktopSettingsStore(path);
        store.Save(new DesktopSettings
        {
            DeviceName = " Phone ",
            StorageName = " Storage ",
            PlaylistFolder = @"custom\playlists",
            PhoneMediaFolder = @"Audio\Synced",
            PostSyncBackupPath = @" D:\Playlist Archives ",
            PlaylistAssociations =
            [
                new("playlist-1", "Road.m3u8"),
                new("playlist-1", "Duplicate.m3u"),
                new("playlist-2", "../unsafe.m3u"),
                new("playlist-3", "not-a-playlist.txt")
            ]
        });

        DesktopSettings loaded = store.Load();
        Assert.Equal("Phone", loaded.DeviceName);
        Assert.Equal("Storage", loaded.StorageName);
        Assert.Equal("custom/playlists", loaded.PlaylistFolder);
        Assert.Equal("Audio/Synced", loaded.PhoneMediaFolder);
        Assert.Equal(@"D:\Playlist Archives", loaded.PostSyncBackupPath);
        PlaylistAssociation association = Assert.Single(loaded.PlaylistAssociations);
        Assert.Equal("playlist-1", association.PlaylistId);
        Assert.Equal("Road.m3u8", association.PhoneBackingName);
    }

    [Fact]
    public void InvalidSettingsJsonFallsBackToDefaults()
    {
        Directory.CreateDirectory(temporaryDirectory);
        string path = Path.Combine(temporaryDirectory, "settings.json");
        File.WriteAllText(path, "{ invalid");

        DesktopSettings settings = new DesktopSettingsStore(path).Load();

        Assert.Equal(DesktopSettings.DefaultDeviceName, settings.DeviceName);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("Music/./Synced")]
    [InlineData("Music:Synced")]
    [InlineData("../Music")]
    public void UnsafePhoneMediaFoldersFallBackToDefault(string folder)
    {
        string path = Path.Combine(temporaryDirectory, "settings.json");
        var store = new DesktopSettingsStore(path);
        store.Save(new DesktopSettings { PhoneMediaFolder = folder });

        DesktopSettings loaded = store.Load();

        Assert.Equal(DesktopSettings.DefaultPhoneMediaFolder, loaded.PhoneMediaFolder);
    }

    [Fact]
    public void PostSyncBackupWritesAllRawPlaylistsIntoTimestampedDirectory()
    {
        DateTimeOffset timestamp = new(2026, 8, 1, 16, 32, 45, TimeSpan.FromHours(-7));
        string root = Path.Combine(temporaryDirectory, "post-sync");
        var backup = new PostSyncPlaylistBackup(
            new SnapshotReader(
            [
                new PhonePlaylistContent("1", "Road.m3u", [1, 2, 3]),
                new PhonePlaylistContent("2", "Mix.M3U8", [4, 5])
            ]),
            root,
            () => timestamp);

        string first = backup.Create();
        string second = backup.Create();

        Assert.Equal(Path.Combine(root, "2026-08-01 16-32-45"), first);
        Assert.Equal(Path.Combine(root, "2026-08-01 16-32-45-02"), second);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(first, "Road.m3u")));
        Assert.Equal(new byte[] { 4, 5 }, File.ReadAllBytes(Path.Combine(first, "Mix.M3U8")));
    }

    [Fact]
    public void MusicBeeBackupWritesPlaylistsIntoDateAndTimeDirectories()
    {
        DateTimeOffset timestamp = new(2026, 8, 3, 11, 30, 45, TimeSpan.FromHours(-7));
        string root = Path.Combine(temporaryDirectory, "post-sync");
        var backup = new MusicBeePlaylistBackup(
            () =>
            [
                new MusicPlaylist(
                    "playlist-1",
                    "Road: Mix",
                    [@"D:\Music\Artist\First.mp3", @"D:\Music\Artist\Second.mp3"]),
                new MusicPlaylist(
                    "playlist-2",
                    "Road: Mix",
                    [@"E:\Audio\Third.flac"])
            ],
            root,
            () => timestamp);

        string first = backup.Create();
        string second = backup.Create();

        string dateDirectory = Path.Combine(root, "MusicBee Playlists", "2026-08-03");
        Assert.Equal(Path.Combine(dateDirectory, "11-30-45"), first);
        Assert.Equal(Path.Combine(dateDirectory, "11-30-45-02"), second);
        string[] files = Directory.GetFiles(first)
            .Select(Path.GetFileName)
            .OrderBy(name => name)
            .ToArray()!;
        Assert.Equal(["Road_ Mix-02.m3u", "Road_ Mix.m3u"], files);
        Assert.Equal(
            "D:\\Music\\Artist\\First.mp3\nD:\\Music\\Artist\\Second.mp3\n",
            File.ReadAllText(Path.Combine(first, "Road_ Mix.m3u")));
        Assert.Equal(
            "E:\\Audio\\Third.flac\n",
            File.ReadAllText(Path.Combine(first, "Road_ Mix-02.m3u")));
    }

    [Fact]
    public void MobileExportSessionWritesPlaylistsAndLogIntoTimestampedDirectory()
    {
        DateTimeOffset timestamp = new(2026, 8, 3, 1, 47, 0, TimeSpan.FromHours(-7));
        string root = Path.Combine(temporaryDirectory, "mobile-playlists");
        string backups = Path.Combine(temporaryDirectory, "generated-backups");
        var session = new MobilePlaylistExportSession(root, backups, () => timestamp);

        session.Writer.Replace(
            "Driving.m3u",
            ["Music/Artist/Track.mp3"],
            CancellationToken.None);
        session.Write("Playlist", "Driving exported and verified.");

        string expectedDirectory = Path.Combine(root, "2026-08-03 01-47-00");
        Assert.Equal(expectedDirectory, session.OutputDirectory);
        Assert.False(File.Exists(Path.Combine(root, "Driving.m3u")));
        Assert.Equal(
            "/storage/emulated/0/Music/Artist/Track.mp3\n",
            File.ReadAllText(Path.Combine(expectedDirectory, "Driving.m3u")));
        Assert.Equal(
            Path.Combine(expectedDirectory, "log", "log.txt"),
            session.LogPath);
        string log = File.ReadAllText(session.LogPath);
        Assert.Contains("Mobile playlist export started.", log);
        Assert.Contains("Driving exported and verified.", log);
        Assert.Contains(session.RunId.ToString("D"), log);
    }

    [Fact]
    public void MobileExportSessionUsesCollisionSuffix()
    {
        DateTimeOffset timestamp = new(2026, 8, 3, 1, 47, 0, TimeSpan.FromHours(-7));
        string root = Path.Combine(temporaryDirectory, "mobile-playlists");
        string backups = Path.Combine(temporaryDirectory, "generated-backups");

        var first = new MobilePlaylistExportSession(root, backups, () => timestamp);
        var second = new MobilePlaylistExportSession(root, backups, () => timestamp);

        Assert.EndsWith("2026-08-03 01-47-00", first.OutputDirectory);
        Assert.EndsWith("2026-08-03 01-47-00-02", second.OutputDirectory);
    }

    [Fact]
    public void DiagnosticsAggregateInjectedChecksAndPhoneProbe()
    {
        var service = new SetupDiagnosticService(
            "storage",
            Path.Combine("database", "shmembee.db"),
            "backups",
            "sidecar.exe",
            () => new SetupDiagnosticCheckResult(
                "phone",
                SetupDiagnosticStatus.Passed,
                "connected"),
            path => !string.Equals(path, "backups", StringComparison.Ordinal),
            _ => true);

        SetupDiagnosticResult result = service.Run();

        Assert.False(result.IsReady);
        Assert.Equal(5, result.Checks.Count);
        Assert.Equal(
            SetupDiagnosticStatus.Failed,
            Assert.Single(result.Checks, check => check.Name == "backup").Status);
        Assert.Equal(
            SetupDiagnosticStatus.Passed,
            Assert.Single(result.Checks, check => check.Name == "phone").Status);
    }

    [Fact]
    public void HistoryListsNewestFirstAndReturnsDetail()
    {
        string databasePath = Path.Combine(temporaryDirectory, "history.db");
        var history = new SynchronizationHistoryStore(databasePath);
        SynchronizationPlan first = Plan(Guid.NewGuid(), "first");
        SynchronizationPlan second = Plan(Guid.NewGuid(), "second");
        history.Started(first);
        history.Failed(first, "failure detail");
        history.Started(second);

        IReadOnlyList<SynchronizationHistoryListItem> items = history.List();
        SynchronizationHistoryDetail? detail = history.Get(first.OperationId);

        Assert.Equal(2, items.Count);
        Assert.Equal(second.OperationId, items[0].OperationId);
        Assert.NotNull(detail);
        Assert.Equal("failed", detail.Status);
        Assert.Equal("failure detail", detail.Details);
        Assert.Equal("musicbee-checksum", detail.ExpectedMusicBeeChecksum);
        Assert.Null(history.Get(Guid.NewGuid()));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static SynchronizationPlan Plan(Guid operationId, string playlistId) =>
        new(
            operationId,
            playlistId,
            playlistId,
            "musicbee://" + playlistId,
            playlistId + ".m3u",
            false,
            "musicbee-checksum",
            "phone-checksum",
            Array.Empty<SynchronizationTrack>());

    private sealed class SnapshotReader : IPhonePlaylistSnapshotReader
    {
        private readonly IReadOnlyList<PhonePlaylistContent> playlists;

        public SnapshotReader(IReadOnlyList<PhonePlaylistContent> playlists)
        {
            this.playlists = playlists;
        }

        public IReadOnlyList<PhonePlaylistContent> ReadPlaylistSnapshot() => playlists;
    }
}
