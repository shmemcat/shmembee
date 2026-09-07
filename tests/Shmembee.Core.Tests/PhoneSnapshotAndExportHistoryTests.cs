using System.Text;
using Microsoft.Data.Sqlite;
using Shmembee.Application.Ports;
using Shmembee.Application.Synchronization;
using Shmembee.Infrastructure.Persistence;
using Shmembee.Infrastructure.Playlists;

namespace Shmembee.Core.Tests;

public sealed class PhoneSnapshotAndExportHistoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "shmembee-snapshot-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SnapshotIsReadOnlyAndOwnsItsBytes()
    {
        var source = new Source();
        var snapshot = new PhoneReadSnapshot();
        Assert.Throws<InvalidOperationException>(() => snapshot.Read("Road.m3u"));
        snapshot.Capture(source, source, default);
        byte[] original = source.Bytes.ToArray();
        source.Bytes[0] = 0;
        byte[] copy = snapshot.Read("Road.m3u")!;
        Assert.Equal(original, copy);
        copy[0] = 0;
        snapshot.ReadPlaylistSnapshot()[0].Content[0] = 0;
        Assert.Equal(original, snapshot.Read("ROAD.M3U"));
        Assert.Null(snapshot.Read("Missing.m3u"));
        Assert.Throws<NotSupportedException>(() => snapshot.Replace("Road.m3u", []));
        Assert.Throws<NotSupportedException>(() => snapshot.Delete("Road.m3u"));
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public void CancelledCaptureCannotReusePreviousSnapshot()
    {
        var source = new Source();
        var snapshot = new PhoneReadSnapshot();
        snapshot.Capture(source, source, default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => snapshot.Capture(source, source, cancellation.Token));
        Assert.Throws<InvalidOperationException>(() => snapshot.ReadMediaPaths());
        Assert.Throws<InvalidOperationException>(() => snapshot.ReadPlaylistSnapshot());
    }

    [Fact]
    public void ExportKeepsPreviousBaselineUntilBothEndpointsMatchOnRefreshAfterRestart()
    {
        string database = Path.Combine(root, "state.db");
        PlaylistState oldMusic = State("old-pc");
        PlaylistState oldPhone = State("Music/old.mp3");
        PlaylistState newMusic = State("new-pc", "new-pc");
        PlaylistState newPhone = State("Music/new.mp3", "Music/new.mp3");
        var baselineStore = new AcceptedBaselineStore(database);
        var oldHistory = new SynchronizationHistoryStore(database);
        var oldPlan = Plan(oldMusic, oldPhone);
        oldHistory.Started(oldPlan);
        oldHistory.Completed(oldPlan, oldMusic, oldPhone);

        var exportHistory = new SynchronizationHistoryStore(database, exportsOnly: true);
        var exportPlan = Plan(newMusic, newPhone);
        exportHistory.Started(exportPlan);
        exportHistory.Completed(exportPlan, newMusic, newPhone);
        Assert.Equal(oldMusic.Checksum, baselineStore.Load("pair")!.MusicBeeChecksum);
        Assert.Equal("exported", exportHistory.Get(exportPlan.OperationId)!.Status);
        Assert.Null(exportHistory.Get(exportPlan.OperationId)!.VerifiedPhoneChecksum);

        var restarted = new SynchronizationHistoryStore(database, exportsOnly: true);
        Assert.False(restarted.ConfirmExport("pc-playlist", "Road.m3u", "pair", newMusic, oldPhone));
        Assert.False(restarted.ConfirmExport("pc-playlist", "Road.m3u", "pair", oldMusic, newPhone));
        Assert.Equal(oldPhone.Checksum, baselineStore.Load("pair")!.PhoneChecksum);
        Assert.True(restarted.ConfirmExport("pc-playlist", "Road.m3u", "new-pair-id", newMusic, newPhone));
        var confirmed = baselineStore.Load("new-pair-id")!;
        Assert.Equal(newMusic.Checksum, confirmed.MusicBeeChecksum);
        Assert.Equal(newPhone.Checksum, confirmed.PhoneChecksum);
        Assert.Equal(2, confirmed.Tracks.Count);
        Assert.Equal("completed", restarted.Get(exportPlan.OperationId)!.Status);
        Assert.Equal("new-pair-id", restarted.Get(exportPlan.OperationId)!.PlaylistId);
        Assert.False(restarted.ConfirmExport("pc-playlist", "Road.m3u", "new-pair-id", newMusic, newPhone));
    }

    private static PlaylistState State(params string[] entries) => new(true, PlaylistChecksum.Compute(entries), entries);

    private static SynchronizationPlan Plan(PlaylistState music, PlaylistState phone) => new(
        Guid.NewGuid(), "pair", "Road", "pc-playlist", "Road.m3u", true,
        music.Checksum, phone.Checksum,
        music.Entries.Zip(phone.Entries, (pc, mobile) => new SynchronizationTrack(pc, pc, mobile)));

    private sealed class Source : IPhonePlaylistSnapshotReader, IProgressivePhoneMediaPathReader
    {
        public byte[] Bytes { get; } = Encoding.UTF8.GetBytes("Music/song.mp3\n");
        public int Reads { get; private set; }
        public IReadOnlyList<PhonePlaylistContent> ReadPlaylistSnapshot()
        {
            Reads++;
            return [new PhonePlaylistContent("id", "Road.m3u", Bytes)];
        }
        public IReadOnlyList<string> ReadMediaPaths() => ReadMediaPaths(default);
        public IReadOnlyList<string> ReadMediaPaths(CancellationToken cancellationToken, IProgress<PhoneMediaTraversalProgress>? progress = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return ["Music/song.mp3"];
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
