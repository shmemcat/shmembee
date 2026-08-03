using System.Text.Json;
using Shmembee.Windows;

#pragma warning disable CA1707
namespace Shmembee.Core.Tests;

public sealed class WpdSidecarPlaylistTransportTests
{
    [Fact]
    public void ReadSerializesProtocolAndDecodesContent()
    {
        var runner = new RecordingRunner(request =>
        {
            using JsonDocument document = JsonDocument.Parse(request);
            JsonElement root = document.RootElement;
            Assert.Equal("read", root.GetProperty("Operation").GetString());
            Assert.Equal("MLE S24U", root.GetProperty("Device").GetString());
            Assert.Equal("Internal storage", root.GetProperty("Storage").GetString());
            Assert.Equal("gmmp/playlists", root.GetProperty("Folder").GetString());
            Assert.Equal("playlist.m3u", root.GetProperty("Name").GetString());
            string operationId = root.GetProperty("OperationId").GetString()!;
            return Success(operationId, "AQID");
        });
        var transport = CreateTransport(runner);

        Assert.Equal(new byte[] { 1, 2, 3 }, transport.Read("playlist.m3u"));
        Assert.Equal(1, runner.CallCount);
    }

    [Fact]
    public void RequestCarriesCorrelatedDiagnosticsWithoutContentInJournal()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "shmembee-wpd-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            string? activityId = null;
            var runner = new RecordingRunner(request =>
            {
                using JsonDocument document = JsonDocument.Parse(request);
                JsonElement root = document.RootElement;
                activityId = root.GetProperty("ActivityId").GetString();
                Assert.False(string.IsNullOrWhiteSpace(activityId));
                Assert.Equal(
                    Path.Combine(directory, "wpd-diagnostics.jsonl"),
                    root.GetProperty("DiagnosticsPath").GetString());
                return Success(root.GetProperty("OperationId").GetString()!);
            });
            var transport = new WpdSidecarPlaylistTransport(
                "sidecar.exe",
                "MLE S24U",
                "Internal storage",
                "gmmp/playlists",
                TimeSpan.FromSeconds(1),
                runner,
                diagnosticsPath: directory);

            transport.Replace("playlist.m3u", new byte[] { 1, 2, 3 });

            string journal = File.ReadAllText(
                Path.Combine(directory, "wpd-diagnostics.jsonl"));
            Assert.Contains(activityId!, journal);
            Assert.Contains("\"operation.start\"", journal);
            Assert.DoesNotContain("AQID", journal);
            Assert.DoesNotContain("playlist.m3u", journal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ProcessResultRetainsOptionalExecutionDiagnostics()
    {
        var result = new WpdSidecarProcessResult(7, "out", "err", 123, 456);

        Assert.Equal(123, result.ProcessId);
        Assert.Equal(456, result.ElapsedMilliseconds);
    }

    [Fact]
    public void ReplaceSerializesContent()
    {
        var runner = new RecordingRunner(request =>
        {
            using JsonDocument document = JsonDocument.Parse(request);
            JsonElement root = document.RootElement;
            Assert.Equal("replace", root.GetProperty("Operation").GetString());
            Assert.Equal("AQID", root.GetProperty("ContentBase64").GetString());
            return Success(root.GetProperty("OperationId").GetString()!);
        });

        CreateTransport(runner).Replace("playlist.m3u", new byte[] { 1, 2, 3 });
    }

    [Fact]
    public void CreateBackupReturnsOpaqueCleanupHandle()
    {
        var runner = new RecordingRunner(request =>
        {
            using JsonDocument document = JsonDocument.Parse(request);
            JsonElement root = document.RootElement;
            Assert.Equal(
                "create-playlist-backup",
                root.GetProperty("Operation").GetString());
            Assert.True(root.GetProperty("Name").ValueKind == JsonValueKind.Null);
            return new WpdSidecarProcessResult(
                0,
                JsonSerializer.Serialize(new
                {
                    Success = true,
                    OperationId = root.GetProperty("OperationId").GetString(),
                    BackupFolderName = "shmembee-20260802-031000-0000000-id",
                    CopiedNames = new[] { "One.m3u", "Two.m3u8" }
                }),
                string.Empty);
        });

        Shmembee.Application.Ports.PhonePlaylistBackupResult result =
            CreateTransport(runner).CreatePlaylistBackup();

        Assert.Equal(2, result.PlaylistCount);
        Assert.Equal(
            "shmembee-20260802-031000-0000000-id",
            result.Handle.BackupFolderName);
        Assert.Equal(
            new[] { "One.m3u", "Two.m3u8" },
            result.Handle.CopiedBackingNames);
    }

    [Fact]
    public void DeleteBackupSerializesOnlyHandleContents()
    {
        var runner = new RecordingRunner(request =>
        {
            using JsonDocument document = JsonDocument.Parse(request);
            JsonElement root = document.RootElement;
            Assert.Equal(
                "delete-playlist-backup",
                root.GetProperty("Operation").GetString());
            Assert.Equal(
                "shmembee-20260802-031000-0000000-id",
                root.GetProperty("BackupFolderName").GetString());
            Assert.Equal(
                new[] { "One.m3u", "Two.m3u8" },
                root.GetProperty("CopiedNames")
                    .EnumerateArray()
                    .Select(item => item.GetString()));
            return Success(root.GetProperty("OperationId").GetString()!);
        });

        CreateTransport(runner).DeletePlaylistBackup(
            new Shmembee.Application.Ports.PhonePlaylistBackupHandle(
                "shmembee-20260802-031000-0000000-id",
                new[] { "One.m3u", "Two.m3u8" }));

        Assert.Equal(1, runner.CallCount);
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("other")]
    public void UnsafeBackupHandlesNeverStartProcess(string folderName)
    {
        var runner = new RecordingRunner(_ => throw new InvalidOperationException());
        var handle = new Shmembee.Application.Ports.PhonePlaylistBackupHandle(
            folderName,
            Array.Empty<string>());

        Assert.Throws<ArgumentException>(
            () => CreateTransport(runner).DeletePlaylistBackup(handle));
        Assert.Equal(0, runner.CallCount);
    }

    [Theory]
    [InlineData("../One.m3u")]
    [InlineData("One.txt")]
    public void UnsafeBackupCopiedNamesNeverStartCleanupProcess(string copiedName)
    {
        var runner = new RecordingRunner(_ => throw new InvalidOperationException());
        var handle = new Shmembee.Application.Ports.PhonePlaylistBackupHandle(
            "shmembee-20260802-031000-0000000-id",
            new[] { copiedName });

        Assert.Throws<ArgumentException>(
            () => CreateTransport(runner).DeletePlaylistBackup(handle));
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public void UnsafeCreateBackupResponseCannotBecomeCleanupHandle()
    {
        var runner = new RecordingRunner(request =>
        {
            using JsonDocument document = JsonDocument.Parse(request);
            return new WpdSidecarProcessResult(
                0,
                JsonSerializer.Serialize(new
                {
                    Success = true,
                    OperationId = document.RootElement
                        .GetProperty("OperationId")
                        .GetString(),
                    BackupFolderName = "some-existing-folder",
                    CopiedNames = new[] { "One.m3u" }
                }),
                string.Empty);
        });

        Assert.Throws<ArgumentException>(
            () => CreateTransport(runner).CreatePlaylistBackup());
        Assert.Equal(1, runner.CallCount);
    }

    [Fact]
    public void DuplicateCreateBackupCopiedNamesAreRejected()
    {
        var runner = new RecordingRunner(request =>
        {
            using JsonDocument document = JsonDocument.Parse(request);
            return new WpdSidecarProcessResult(
                0,
                JsonSerializer.Serialize(new
                {
                    Success = true,
                    OperationId = document.RootElement
                        .GetProperty("OperationId")
                        .GetString(),
                    BackupFolderName = "shmembee-20260802-031000-0000000-id",
                    CopiedNames = new[] { "One.m3u", "One.m3u" }
                }),
                string.Empty);
        });

        Assert.Throws<ArgumentException>(
            () => CreateTransport(runner).CreatePlaylistBackup());
        Assert.Equal(1, runner.CallCount);
    }

    [Fact]
    public void TimeoutIsBoundedAndReportedAsIoFailure()
    {
        var runner = new ThrowingRunner(new TimeoutException());
        var exception = Assert.Throws<IOException>(
            () => CreateTransport(runner).Read("playlist.m3u"));

        Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Equal(1, runner.CallCount);
    }

    [Fact]
    public void SidecarErrorPreservesStageAndRecoveryDiagnostics()
    {
        var runner = new RecordingRunner(request =>
        {
            using JsonDocument document = JsonDocument.Parse(request);
            string operationId = document.RootElement
                .GetProperty("OperationId")
                .GetString()!;
            return new WpdSidecarProcessResult(
                1,
                JsonSerializer.Serialize(new
                {
                    Success = false,
                    OperationId = operationId,
                    Stage = "promote-candidate",
                    HResult = unchecked((int)0x80004005),
                    Error = "promotion failed",
                    OriginalObjectId = "old-id",
                    CandidateObjectId = "candidate-id",
                    CandidateName = "candidate.m3u"
                }),
                "driver detail");
        });

        IOException exception = Assert.Throws<IOException>(
            () => CreateTransport(runner).Replace("playlist.m3u", new byte[] { 1 }));

        Assert.Contains("promote-candidate", exception.Message);
        Assert.Contains("old-id", exception.Message);
        Assert.Contains("candidate-id", exception.Message);
        Assert.Contains("driver detail", exception.Message);
    }

    [Fact]
    public void MismatchedOperationIdIsRejected()
    {
        var runner = new RecordingRunner(_ => Success("different-id"));

        IOException exception = Assert.Throws<IOException>(
            () => CreateTransport(runner).Delete("playlist.m3u"));

        Assert.Contains("operation ID", exception.Message);
    }

    [Fact]
    public void MissingReadReturnsNullWithoutFallbackProcess()
    {
        var runner = new RecordingRunner(request =>
        {
            using JsonDocument document = JsonDocument.Parse(request);
            string operationId = document.RootElement
                .GetProperty("OperationId")
                .GetString()!;
            return new WpdSidecarProcessResult(
                1,
                JsonSerializer.Serialize(new
                {
                    Success = false,
                    OperationId = operationId,
                    Stage = "resolve-object",
                    Error = "No exact WPD object named 'playlist.m3u' exists."
                }),
                string.Empty);
        });

        Assert.Null(CreateTransport(runner).Read("playlist.m3u"));
        Assert.Equal(1, runner.CallCount);
    }

    [Fact]
    public void ProbeRetainsMetadataAndSafelyEnumeratesPlaylistNames()
    {
        var runner = new RecordingRunner(request =>
        {
            using JsonDocument document = JsonDocument.Parse(request);
            string operationId = document.RootElement
                .GetProperty("OperationId")
                .GetString()!;
            return new WpdSidecarProcessResult(
                0,
                JsonSerializer.Serialize(new
                {
                    Success = true,
                    OperationId = operationId,
                    DeviceId = "device-id",
                    StorageId = "storage-id",
                    FolderId = "folder-id",
                    ObjectId = "object-id",
                    Sha256 = "abc",
                    ByteCount = 42,
                    RenameSupported = true,
                    Objects = new[]
                    {
                        "1|Road.m3u",
                        "2|Mix.M3U8",
                        "3|notes.txt",
                        "4|../unsafe.m3u",
                        "5|road.m3u",
                        "malformed.m3u8"
                    }
                }),
                string.Empty);
        });

        WpdSidecarResponse response = CreateTransport(runner).Probe();

        Assert.Equal("device-id", response.DeviceId);
        Assert.Equal("storage-id", response.StorageId);
        Assert.Equal("folder-id", response.FolderId);
        Assert.Equal("object-id", response.ObjectId);
        Assert.Equal("abc", response.Sha256);
        Assert.Equal(42, response.ByteCount);
        Assert.True(response.RenameSupported);
        Assert.Equal(
            new[] { "Road.m3u", "Mix.M3U8" },
            response.EnumeratePlaylistNames());
        Assert.Collection(
            response.EnumeratePlaylists(),
            playlist =>
            {
                Assert.Equal("1", playlist.Id);
                Assert.Equal("Road.m3u", playlist.BackingName);
                Assert.Equal("Road", playlist.DisplayName);
            },
            playlist =>
            {
                Assert.Equal("2", playlist.Id);
                Assert.Equal("Mix.M3U8", playlist.BackingName);
                Assert.Equal("Mix", playlist.DisplayName);
            });
    }

    [Fact]
    public void PlaylistSnapshotReadsAllContentsWithOneProcess()
    {
        var runner = new RecordingRunner(request =>
        {
            using JsonDocument document = JsonDocument.Parse(request);
            JsonElement root = document.RootElement;
            Assert.Equal(
                "snapshot-playlists",
                root.GetProperty("Operation").GetString());
            string operationId = root.GetProperty("OperationId").GetString()!;
            return new WpdSidecarProcessResult(
                0,
                JsonSerializer.Serialize(new
                {
                    Success = true,
                    OperationId = operationId,
                    Playlists = new[]
                    {
                        new { ObjectId = "one", Name = "One.m3u", ContentBase64 = "AQI=" },
                        new { ObjectId = "two", Name = "Two.m3u8", ContentBase64 = "Aw==" }
                    }
                }),
                string.Empty);
        });

        IReadOnlyList<Shmembee.Application.Ports.PhonePlaylistContent> result =
            CreateTransport(runner).ReadPlaylistSnapshot();

        Assert.Equal(1, runner.CallCount);
        Assert.Collection(
            result,
            item =>
            {
                Assert.Equal("one", item.Id);
                Assert.Equal("One.m3u", item.BackingName);
                Assert.Equal(new byte[] { 1, 2 }, item.Content);
            },
            item =>
            {
                Assert.Equal("two", item.Id);
                Assert.Equal("Two.m3u8", item.BackingName);
                Assert.Equal(new byte[] { 3 }, item.Content);
            });
    }

    [Fact]
    public void MediaPathSnapshotUsesConfiguredRootAndNormalizesResponse()
    {
        var runner = new RecordingRunner(request =>
        {
            using JsonDocument document = JsonDocument.Parse(request);
            JsonElement root = document.RootElement;
            Assert.Equal(
                "snapshot-media-paths",
                root.GetProperty("Operation").GetString());
            Assert.Equal("Music/Library", root.GetProperty("Folder").GetString());
            Assert.True(root.GetProperty("Name").ValueKind == JsonValueKind.Null);
            Assert.True(root.GetProperty("ContentBase64").ValueKind == JsonValueKind.Null);
            string operationId = root.GetProperty("OperationId").GetString()!;
            return new WpdSidecarProcessResult(
                0,
                JsonSerializer.Serialize(new
                {
                    Success = true,
                    OperationId = operationId,
                    MediaPaths = new[]
                    {
                        @"Music\Library\Artist\Track.mp3",
                        "Music/Library/Artist/Track.mp3",
                        " Music/Library/Other.flac ",
                        "Music/Library/Cover.jpg",
                        "../unsafe.mp3",
                        "C:/unsafe.mp3",
                        ""
                    }
                }),
                string.Empty);
        });

        IReadOnlyList<string> result = CreateTransport(
            runner,
            "Music/Library").ReadMediaPaths();

        Assert.Equal(1, runner.CallCount);
        Assert.Equal(
            new[]
            {
                "Music/Library/Artist/Track.mp3",
                "Music/Library/Other.flac"
            },
            result);
    }

    [Fact]
    public void MediaPathSnapshotRequiresConfiguredRootWithoutStartingProcess()
    {
        var runner = new RecordingRunner(_ => throw new InvalidOperationException());

        Assert.Throws<InvalidOperationException>(
            () => CreateTransport(runner).ReadMediaPaths());
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public void MediaPathSnapshotSupportsLargeObjectGraphs()
    {
        string[] paths = Enumerable.Range(0, 70000)
            .Select(index => $"Music/Artist/Album/{index:D5}.mp3")
            .ToArray();
        var runner = new RecordingRunner(request =>
        {
            using JsonDocument document = JsonDocument.Parse(request);
            string operationId = document.RootElement
                .GetProperty("OperationId")
                .GetString()!;
            return new WpdSidecarProcessResult(
                0,
                JsonSerializer.Serialize(new
                {
                    Success = true,
                    OperationId = operationId,
                    MediaPaths = paths
                }),
                string.Empty);
        });

        IReadOnlyList<string> result = CreateTransport(
            runner,
            "Music").ReadMediaPaths();

        Assert.Equal(paths.Length, result.Count);
        Assert.Equal(paths[0], result[0]);
        Assert.Equal(paths[^1], result[^1]);
    }

    [Fact]
    public void MediaPathSnapshotDecodesQuotedPhoneNames()
    {
        const string path = "Music/Artist/Album \"Dusk\"/01 - Song.mp3";
        var runner = new RecordingRunner(request =>
        {
            using JsonDocument document = JsonDocument.Parse(request);
            string operationId = document.RootElement
                .GetProperty("OperationId")
                .GetString()!;
            return new WpdSidecarProcessResult(
                0,
                JsonSerializer.Serialize(new
                {
                    Success = true,
                    OperationId = operationId,
                    MediaPathsBase64 = new[]
                    {
                        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(path))
                    }
                }),
                string.Empty);
        });

        string actual = Assert.Single(CreateTransport(
            runner,
            "Music").ReadMediaPaths());

        Assert.Equal(path, actual);
    }

    [Fact]
    public void MediaPathSnapshotReportsMatchingStreamingProgress()
    {
        var runner = new StreamingRunner((request, report) =>
        {
            using JsonDocument document = JsonDocument.Parse(request);
            JsonElement root = document.RootElement;
            Assert.Equal(1, root.GetProperty("ProgressProtocolVersion").GetInt32());
            string operationId = root.GetProperty("OperationId").GetString()!;
            report(new WpdSidecarProgressRecord
            {
                Version = 1,
                OperationId = "other",
                Stage = "snapshot-media-paths",
                ObjectsScanned = 1
            });
            report(new WpdSidecarProgressRecord
            {
                Version = 1,
                OperationId = operationId,
                Stage = "snapshot-media-paths",
                ObjectsScanned = 120,
                FoldersCompleted = 7,
                FoldersPending = 3,
                MediaFilesFound = 90
            });
            return new WpdSidecarProcessResult(
                0,
                JsonSerializer.Serialize(new
                {
                    Success = true,
                    OperationId = operationId,
                    MediaPaths = new[] { "Music/Track.mp3" }
                }),
                string.Empty);
        });
        var updates = new List<Shmembee.Application.Ports.PhoneMediaTraversalProgress>();
        var progress = new InlineProgress<
            Shmembee.Application.Ports.PhoneMediaTraversalProgress>(updates.Add);

        IReadOnlyList<string> result = CreateTransport(
            runner,
            "Music").ReadMediaPaths(CancellationToken.None, progress);

        Assert.Single(result);
        var update = Assert.Single(updates);
        Assert.Equal(120, update.ObjectsScanned);
        Assert.Equal(7, update.FoldersCompleted);
        Assert.Equal(3, update.FoldersPending);
        Assert.Equal(90, update.MediaFilesFound);
    }

    [Fact]
    public void MediaPathSnapshotHonorsPreCancelledToken()
    {
        var runner = new StreamingRunner((_, _) =>
            throw new InvalidOperationException("Runner should not start."));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => CreateTransport(
            runner,
            "Music").ReadMediaPaths(cancellation.Token));
        Assert.Equal(0, runner.CallCount);
    }

    [Theory]
    [InlineData("../playlist.m3u")]
    [InlineData("folder/playlist.m3u")]
    public void UnsafeBackingNamesNeverStartProcess(string name)
    {
        var runner = new RecordingRunner(_ => throw new InvalidOperationException());

        Assert.Throws<ArgumentException>(() => CreateTransport(runner).Read(name));
        Assert.Equal(0, runner.CallCount);
    }

    private static WpdSidecarPlaylistTransport CreateTransport(
        IWpdSidecarProcessRunner runner,
        string? mediaFolderPath = null) =>
        new(
            "sidecar.exe",
            "MLE S24U",
            "Internal storage",
            "gmmp/playlists",
            TimeSpan.FromSeconds(1),
            runner,
            mediaFolderPath);

    private static WpdSidecarProcessResult Success(
        string operationId,
        string? contentBase64 = null) =>
        new(
            0,
            JsonSerializer.Serialize(new
            {
                Success = true,
                OperationId = operationId,
                Stage = "complete",
                ContentBase64 = contentBase64
            }),
            string.Empty);

    private sealed class RecordingRunner(
        Func<string, WpdSidecarProcessResult> handler)
        : IWpdSidecarProcessRunner
    {
        public int CallCount { get; private set; }

        public WpdSidecarProcessResult Run(
            string executablePath,
            string standardInput,
            TimeSpan timeout)
        {
            CallCount++;
            Assert.Equal("sidecar.exe", executablePath);
            using JsonDocument document = JsonDocument.Parse(standardInput);
            TimeSpan expectedTimeout = string.Equals(
                document.RootElement.GetProperty("Operation").GetString(),
                "snapshot-media-paths",
                StringComparison.Ordinal)
                ? TimeSpan.FromHours(2)
                : TimeSpan.FromSeconds(1);
            Assert.Equal(expectedTimeout, timeout);
            return handler(standardInput);
        }
    }

    private sealed class ThrowingRunner(Exception exception)
        : IWpdSidecarProcessRunner
    {
        public int CallCount { get; private set; }

        public WpdSidecarProcessResult Run(
            string executablePath,
            string standardInput,
            TimeSpan timeout)
        {
            CallCount++;
            throw exception;
        }
    }

    private sealed class StreamingRunner(
        Func<string, Action<WpdSidecarProgressRecord>, WpdSidecarProcessResult> handler)
        : IWpdSidecarStreamingProcessRunner
    {
        public int CallCount { get; private set; }

        public WpdSidecarProcessResult Run(
            string executablePath,
            string standardInput,
            TimeSpan timeout)
        {
            return Run(
                executablePath,
                standardInput,
                timeout,
                null!,
                CancellationToken.None);
        }

        public WpdSidecarProcessResult Run(
            string executablePath,
            string standardInput,
            TimeSpan timeout,
            Action<WpdSidecarProgressRecord> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return handler(standardInput, progress);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
