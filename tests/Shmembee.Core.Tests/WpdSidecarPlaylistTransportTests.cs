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
        IWpdSidecarProcessRunner runner) =>
        new(
            "sidecar.exe",
            "MLE S24U",
            "Internal storage",
            "gmmp/playlists",
            TimeSpan.FromSeconds(1),
            runner);

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
            Assert.Equal(TimeSpan.FromSeconds(1), timeout);
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
}
