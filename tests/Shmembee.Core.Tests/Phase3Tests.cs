using System.Collections.ObjectModel;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;
using Shmembee.Application.Synchronization;
using Shmembee.Infrastructure.Persistence;
using Shmembee.Infrastructure.Playlists;

namespace Shmembee.Core.Tests;

public sealed class Phase3Tests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "shmembee-phase3-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void DeterministicWriterUsesUtf8LfAndPreservesDuplicates()
    {
        byte[] bytes = new DeterministicM3uWriter().Write(
            new[] { @"Music\言ノ葉.mp3", "Music/Duplicate.mp3", "Music/Duplicate.mp3" });

        Assert.Equal(
            "Music/言ノ葉.mp3\nMusic/Duplicate.mp3\nMusic/Duplicate.mp3\n",
            Encoding.UTF8.GetString(bytes));
        Assert.False(bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
    }

    [Fact]
    public void CoordinatorRejectsStaleInputsWithoutWriting()
    {
        var musicBee = new MemoryMusicBee("old");
        var phone = new MemoryPhone("phone");
        var history = new MemoryHistory();
        SynchronizationPlan plan = Plan("different", phone.State.Checksum);

        SynchronizationApplyResult result = new SynchronizationCoordinator(
            musicBee,
            phone,
            history).Apply(plan, CancellationToken.None);

        Assert.Equal(SynchronizationApplyStatus.Stale, result.Status);
        Assert.Equal(0, musicBee.ReplaceCount);
        Assert.Equal(0, phone.ReplaceCount);
        Assert.Equal(0, history.StartedCount);
    }

    [Fact]
    public void CoordinatorWritesVerifiesAndCommitsHistory()
    {
        var musicBee = new MemoryMusicBee("old");
        var phone = new MemoryPhone("old-phone");
        var history = new MemoryHistory();
        SynchronizationPlan plan = Plan(
            musicBee.State.Checksum,
            phone.State.Checksum);

        SynchronizationApplyResult result = new SynchronizationCoordinator(
            musicBee,
            phone,
            history).Apply(plan, CancellationToken.None);

        Assert.Equal(SynchronizationApplyStatus.Succeeded, result.Status);
        Assert.Equal(new[] { "new-url" }, musicBee.State.Entries);
        Assert.Equal(new[] { "Music/New.mp3" }, phone.State.Entries);
        Assert.Equal(1, history.CompletedCount);
        Assert.Equal(0, history.FailedCount);
    }

    [Fact]
    public void CoordinatorRollsBackBothSidesWhenPhoneWriteFails()
    {
        var musicBee = new MemoryMusicBee("old");
        var phone = new MemoryPhone("old-phone") { FailReplace = true };
        var history = new MemoryHistory();
        SynchronizationPlan plan = Plan(
            musicBee.State.Checksum,
            phone.State.Checksum);

        SynchronizationApplyResult result = new SynchronizationCoordinator(
            musicBee,
            phone,
            history).Apply(plan, CancellationToken.None);

        Assert.Equal(SynchronizationApplyStatus.Failed, result.Status);
        Assert.Equal(new[] { "old" }, musicBee.State.Entries);
        Assert.Equal(new[] { "old-phone" }, phone.State.Entries);
        Assert.Equal(1, phone.RestoreCount);
        Assert.Equal(1, history.FailedCount);
        Assert.Equal(0, history.CompletedCount);
    }

    [Fact]
    public void CoordinatorRollsBackWhenMusicBeeMutatesThenRejectsWrite()
    {
        var musicBee = new MemoryMusicBee("old") { RejectAfterMutation = true };
        var phone = new MemoryPhone("old-phone");
        var history = new MemoryHistory();
        SynchronizationPlan plan = Plan(
            musicBee.State.Checksum,
            phone.State.Checksum);

        SynchronizationApplyResult result = new SynchronizationCoordinator(
            musicBee,
            phone,
            history).Apply(plan, CancellationToken.None);

        Assert.Equal(SynchronizationApplyStatus.Failed, result.Status);
        Assert.Equal(new[] { "old" }, musicBee.State.Entries);
        Assert.Equal(2, musicBee.ReplaceCount);
        Assert.Equal(0, phone.ReplaceCount);
    }

    [Fact]
    public void CoordinatorRejectsMissingPhoneFileAfterEmptyWrite()
    {
        var musicBee = new MemoryMusicBee();
        var phone = new MemoryPhone { DropReplacement = true };
        var history = new MemoryHistory();
        SynchronizationPlan plan = new(
            Guid.NewGuid(),
            "playlist",
            "Fixture",
            "playlist-url",
            "Fixture.m3u",
            true,
            musicBee.State.Checksum,
            phone.State.Checksum,
            Array.Empty<SynchronizationTrack>());

        SynchronizationApplyResult result = new SynchronizationCoordinator(
            musicBee,
            phone,
            history).Apply(plan, CancellationToken.None);

        Assert.Equal(SynchronizationApplyStatus.Failed, result.Status);
        Assert.Equal(1, history.FailedCount);
        Assert.Equal(0, history.CompletedCount);
    }

    [Fact]
    public void CoordinatorDoesNotRollbackVerifiedWritesWhenBaselineCommitFails()
    {
        var musicBee = new MemoryMusicBee("old");
        var phone = new MemoryPhone("old-phone");
        var history = new MemoryHistory { FailCompletion = true };
        SynchronizationPlan plan = Plan(
            musicBee.State.Checksum,
            phone.State.Checksum);

        SynchronizationApplyResult result = new SynchronizationCoordinator(
            musicBee,
            phone,
            history).Apply(plan, CancellationToken.None);

        Assert.Equal(SynchronizationApplyStatus.CommitPending, result.Status);
        Assert.Equal(new[] { "new-url" }, musicBee.State.Entries);
        Assert.Equal(new[] { "Music/New.mp3" }, phone.State.Entries);
        Assert.Equal(0, phone.RestoreCount);
        Assert.Equal(1, history.CommitPendingCount);
    }

    [Fact]
    public void FileWriterBacksUpReplacesAndRestores()
    {
        string playlists = Path.Combine(temporaryDirectory, "playlists");
        string backups = Path.Combine(temporaryDirectory, "backups");
        Directory.CreateDirectory(playlists);
        string playlist = Path.Combine(playlists, "Fixture.m3u");
        File.WriteAllText(playlist, "Music/Old.mp3\n", new UTF8Encoding(false));
        var writer = new FileSystemPhonePlaylistWriter(playlists, backups);

        PlaylistBackup backup = writer.Backup("Fixture.m3u", Guid.NewGuid());
        writer.Replace(
            "Fixture.m3u",
            new[] { "Music/New.mp3" },
            CancellationToken.None);
        Assert.Equal(new[] { "Music/New.mp3" }, writer.Read("Fixture.m3u").Entries);

        writer.Restore(backup);
        Assert.Equal(new[] { "Music/Old.mp3" }, writer.Read("Fixture.m3u").Entries);
    }

    [Fact]
    public void CoordinatorRollsBackAfterCancellationBetweenWrites()
    {
        var musicBee = new MemoryMusicBee("old");
        using var source = new CancellationTokenSource();
        var phone = new MemoryPhone("old-phone")
        {
            BeforeReplace = source.Cancel
        };
        var history = new MemoryHistory();
        SynchronizationPlan plan = Plan(
            musicBee.State.Checksum,
            phone.State.Checksum);

        SynchronizationApplyResult result = new SynchronizationCoordinator(
            musicBee,
            phone,
            history).Apply(plan, source.Token);

        Assert.Equal(SynchronizationApplyStatus.Cancelled, result.Status);
        Assert.Equal(new[] { "old" }, musicBee.State.Entries);
        Assert.Equal(new[] { "old-phone" }, phone.State.Entries);
    }

    [Fact]
    public void HistoryCommitsAcceptedBaselineOnlyAfterSuccessfulCompletion()
    {
        string databasePath = Path.Combine(temporaryDirectory, "history.db");
        SynchronizationPlan plan = Plan(
            PlaylistChecksum.Compute(new[] { "old" }),
            PlaylistChecksum.Compute(new[] { "old-phone" }));
        var history = new SynchronizationHistoryStore(databasePath);

        history.Started(plan);
        Assert.Equal(0L, CountRows(databasePath, "accepted_sync_baselines"));

        history.Completed(
            plan,
            CreateState(new[] { "new-url" }),
            CreateState(new[] { "Music/New.mp3" }));

        Assert.Equal(1L, CountRows(databasePath, "accepted_sync_baselines"));
        Assert.Equal(1L, CountRows(databasePath, "accepted_sync_baseline_entries"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static SynchronizationPlan Plan(
        string expectedMusicBee,
        string expectedPhone) =>
        new(
            Guid.NewGuid(),
            "playlist",
            "Fixture",
            "playlist-url",
            "Fixture.m3u",
            true,
            expectedMusicBee,
            expectedPhone,
            new[] { new SynchronizationTrack("track", "new-url", "Music/New.mp3") });

    private sealed class MemoryMusicBee : IMusicBeePlaylistWriter
    {
        public MemoryMusicBee(params string[] entries)
        {
            State = CreateState(entries, exists: true);
        }

        public PlaylistState State { get; private set; }

        public int ReplaceCount { get; private set; }

        public bool RejectAfterMutation { get; set; }

        public PlaylistState Read(string playlistUrl) => State;

        public bool Replace(string playlistUrl, IReadOnlyList<string> canonicalUrls)
        {
            ReplaceCount++;
            State = CreateState(canonicalUrls);
            bool accepted = !RejectAfterMutation;
            RejectAfterMutation = false;
            return accepted;
        }
    }

    private sealed class MemoryPhone : IPhonePlaylistWriter
    {
        private PlaylistState backup = CreateState(
            Array.Empty<string>(),
            exists: false);

        public MemoryPhone(params string[] entries)
        {
            State = CreateState(entries, exists: true);
        }

        public PlaylistState State { get; private set; }

        public bool FailReplace { get; set; }

        public bool DropReplacement { get; set; }

        public Action? BeforeReplace { get; set; }

        public int ReplaceCount { get; private set; }

        public int RestoreCount { get; private set; }

        public PlaylistState Read(string backingName) => State;

        public PlaylistBackup Backup(string backingName, Guid operationId)
        {
            backup = State;
            return new PlaylistBackup(backingName, "memory", State.Exists);
        }

        public void Replace(
            string backingName,
            IReadOnlyList<string> phonePaths,
            CancellationToken cancellationToken)
        {
            ReplaceCount++;
            BeforeReplace?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (FailReplace)
            {
                throw new IOException("Simulated phone failure.");
            }

            State = DropReplacement
                ? CreateState(Array.Empty<string>(), exists: false)
                : CreateState(phonePaths);
        }

        public void Restore(PlaylistBackup ignored)
        {
            RestoreCount++;
            State = backup;
        }
    }

    private sealed class MemoryHistory : ISynchronizationHistory
    {
        public int StartedCount { get; private set; }

        public int CompletedCount { get; private set; }

        public int FailedCount { get; private set; }

        public int CommitPendingCount { get; private set; }

        public bool FailCompletion { get; set; }

        public void Started(SynchronizationPlan plan) => StartedCount++;

        public void Completed(
            SynchronizationPlan plan,
            PlaylistState musicBeeResult,
            PlaylistState phoneResult)
        {
            if (FailCompletion)
            {
                throw new IOException("Simulated history failure.");
            }

            CompletedCount++;
        }

        public void CommitPending(SynchronizationPlan plan, string details) =>
            CommitPendingCount++;

        public void Failed(SynchronizationPlan plan, string details) => FailedCount++;
    }

    private static PlaylistState CreateState(
        IEnumerable<string> entries,
        bool exists = true)
    {
        var list = entries.ToList();
        return new PlaylistState(
            exists,
            PlaylistChecksum.Compute(list),
            new ReadOnlyCollection<string>(list));
    }

    private static long CountRows(string databasePath, string table)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + table + ";";
        return (long)(command.ExecuteScalar() ?? 0L);
    }
}
