using System.Text;
using System.Text.Json;
using Shmembee.Application.Synchronization;
using Shmembee.Core.Paths;
using Shmembee.Infrastructure.Playlists;

namespace Shmembee.Core.Tests;

public sealed class CrossRepositoryCompatibilityTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private static string FixtureRoot =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "contract-fixtures");

    [Fact]
    public void FixtureSetIsCompleteAndInternallyVersioned()
    {
        FixtureManifest manifest = ReadJson<FixtureManifest>("manifest.json");

        Assert.Equal(1, manifest.FixtureManifestVersion);
        Assert.Equal(
            new[]
            {
                "m3u-parser-v1",
                "phone-path-v1",
                "semantic-checksum-v1",
                "m3u-writer-v1",
                "canonical-gonemad-profile-v1",
                "playlist-operations-v1",
            },
            manifest.Contracts.Select(contract => contract.Id));

        foreach (ContractReference contract in manifest.Contracts)
        {
            Assert.False(string.IsNullOrWhiteSpace(contract.Rationale));
            Assert.True(File.Exists(FixturePath(contract.Manifest)), contract.Manifest);
            using JsonDocument document = ReadDocument(contract.Manifest);
            Assert.Equal(contract.Id, document.RootElement.GetProperty("contract").GetString());
        }
    }

    [Fact]
    public void ParserMatchesSharedSuccessfulCases()
    {
        ParserManifest manifest = ReadJson<ParserManifest>("parser/m3u-parser-v1.json");

        foreach (ParserCase testCase in manifest.Cases.Where(testCase => testCase.ErrorCode is null))
        {
            using FileStream stream = File.OpenRead(FixturePath(testCase.Input));
            ParsedPlaylist actual = new M3uPlaylistParser(
                testCase.PreserveExtendedInfo ?? true).Parse(
                    stream,
                    testCase.Name,
                    Path.GetFileName(testCase.Input));

            Assert.Collection(
                actual.Entries,
                testCase.Records.Select<ParserRecord, Action<ParsedPlaylistEntry>>(
                    expected => entry =>
                    {
                        Assert.Equal(expected.Line, entry.LineNumber);
                        Assert.Equal(expected.Source, entry.SourceValue);
                        Assert.Equal(expected.Normalized, entry.NormalizedPhonePath);
                        Assert.Equal(expected.Duration, entry.DurationSeconds);
                        Assert.Equal(expected.Title, entry.Title);
                    }).ToArray());
        }
    }

    [Fact]
    public void ParserRejectsMalformedEncoding()
    {
        ParserCase testCase = ReadJson<ParserManifest>("parser/m3u-parser-v1.json")
            .Cases.Single(testCase => testCase.ErrorCode == "malformed-encoding");

        using FileStream stream = File.OpenRead(FixturePath(testCase.Input));
        Assert.Throws<DecoderFallbackException>(
            () => new M3uPlaylistParser().Parse(stream, testCase.Name, testCase.Input));
    }

    [Fact]
    public void DesktopParserDocumentsNormalizedEmptyDivergence()
    {
        ParserCase testCase = ReadJson<ParserManifest>("parser/m3u-parser-v1.json")
            .Cases.Single(testCase => testCase.ErrorCode == "normalized-path-empty");

        using FileStream stream = File.OpenRead(FixturePath(testCase.Input));
        ParsedPlaylist playlist = new M3uPlaylistParser().Parse(
            stream,
            testCase.Name,
            testCase.Input);

        ParsedPlaylistEntry entry = Assert.Single(playlist.Entries);
        Assert.Equal(testCase.ErrorLine, entry.LineNumber);
        Assert.Equal(string.Empty, entry.NormalizedPhonePath);
    }

    [Fact]
    public void PhonePathNormalizerMatchesSharedCases()
    {
        NormalizationManifest manifest =
            ReadJson<NormalizationManifest>("normalization/phone-path-v1.json");

        foreach (NormalizationCase testCase in manifest.Cases)
        {
            if (testCase.Expected is null)
            {
                Assert.Throws<ArgumentException>(
                    () => TrackPathNormalizer.NormalizePhonePath(testCase.Input!));
            }
            else
            {
                Assert.Equal(
                    testCase.Expected,
                    TrackPathNormalizer.NormalizePhonePath(testCase.Input!));
            }
        }
    }

    [Fact]
    public void SemanticChecksumMatchesSharedVectors()
    {
        ChecksumManifest manifest =
            ReadJson<ChecksumManifest>("checksums/semantic-checksum-v1.json");

        foreach (ChecksumCase testCase in manifest.Cases)
        {
            Assert.Equal(testCase.Sha256, PlaylistChecksum.Compute(testCase.Paths));
        }
    }

    [Fact]
    public void WriterMatchesSharedBytesExactly()
    {
        WriterManifest manifest = ReadJson<WriterManifest>("writer/m3u-writer-v1.json");

        foreach (WriterCase testCase in manifest.Cases)
        {
            byte[] expected = File.ReadAllBytes(FixturePath(testCase.Output));
            byte[] actual = new DeterministicM3uWriter(
                testCase.IncludeTrailingNewline,
                testCase.Prefix).Write(testCase.Paths);

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void WriterMatchesSharedRejections()
    {
        WriterManifest manifest = ReadJson<WriterManifest>("writer/m3u-writer-v1.json");

        foreach (WriterRejection rejection in manifest.Rejections)
        {
            Assert.Contains(rejection.ErrorCode, new[] { "blank-path", "line-break" });
            Assert.Throws<ArgumentException>(
                () => new DeterministicM3uWriter().Write(new[] { rejection.Path }));
        }
    }

    [Fact]
    public void AndroidOnlyContractsRemainValidFixtureData()
    {
        CanonicalProfileManifest profile = ReadJson<CanonicalProfileManifest>(
            "gonemad/canonical-gonemad-profile-v1.json");
        OperationsManifest operations = ReadJson<OperationsManifest>(
            "operations/playlist-operations-v1.json");

        Assert.Equal("canonical-gonemad-profile-v1", profile.Contract);
        Assert.NotEmpty(profile.Cases);
        foreach (CanonicalProfileCase testCase in profile.Cases)
        {
            Assert.True(File.Exists(FixturePath(testCase.Input)), testCase.Input);
            Assert.Equal(testCase.Writable, testCase.ReasonCodes.Count == 0);
            Assert.All(testCase.ReasonCodes, code => Assert.False(string.IsNullOrWhiteSpace(code)));
        }

        Assert.Equal("playlist-operations-v1", operations.Contract);
        Assert.NotEmpty(operations.Cases);
        foreach (OperationCase testCase in operations.Cases)
        {
            Assert.Contains(testCase.Policy, new[] { "SENSITIVE", "INSENSITIVE" });
            Assert.All(testCase.Indexes, index => Assert.InRange(index, 0, testCase.Paths.Count - 1));
            if (testCase.AddVolumes is not null)
            {
                Assert.Equal(testCase.Add.Count, testCase.AddVolumes.Count);
            }

            Assert.NotNull(testCase.Remove);
        }
    }

    private static T ReadJson<T>(string relativePath)
    {
        using FileStream stream = File.OpenRead(FixturePath(relativePath));
        return JsonSerializer.Deserialize<T>(stream, JsonOptions)
            ?? throw new InvalidDataException($"Fixture '{relativePath}' was empty.");
    }

    private static JsonDocument ReadDocument(string relativePath) =>
        JsonDocument.Parse(File.ReadAllBytes(FixturePath(relativePath)));

    private static string FixturePath(string relativePath) =>
        Path.Combine(FixtureRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private sealed record FixtureManifest(
        int FixtureManifestVersion,
        IReadOnlyList<ContractReference> Contracts);

    private sealed record ContractReference(string Id, string Manifest, string Rationale);

    private sealed record ParserManifest(string Contract, IReadOnlyList<ParserCase> Cases);

    private sealed record ParserCase(
        string Name,
        string Input,
        bool? PreserveExtendedInfo,
        IReadOnlyList<ParserRecord> Records,
        string? ErrorCode,
        int? ErrorLine);

    private sealed record ParserRecord(
        int Line,
        string Source,
        string Normalized,
        int? Duration,
        string? Title);

    private sealed record NormalizationManifest(
        string Contract,
        IReadOnlyList<NormalizationCase> Cases);

    private sealed record NormalizationCase(string Name, string? Input, string? Expected);

    private sealed record ChecksumManifest(string Contract, IReadOnlyList<ChecksumCase> Cases);

    private sealed record ChecksumCase(
        string Name,
        IReadOnlyList<string> Paths,
        string Sha256);

    private sealed record WriterManifest(
        string Contract,
        IReadOnlyList<WriterCase> Cases,
        IReadOnlyList<WriterRejection> Rejections);

    private sealed record WriterCase(
        string Name,
        IReadOnlyList<string> Paths,
        bool IncludeTrailingNewline,
        string? Prefix,
        string Output);

    private sealed record WriterRejection(string Name, string Path, string ErrorCode);

    private sealed record CanonicalProfileManifest(
        string Contract,
        IReadOnlyList<CanonicalProfileCase> Cases);

    private sealed record CanonicalProfileCase(
        string Name,
        string Input,
        bool Writable,
        IReadOnlyList<string> ReasonCodes);

    private sealed record OperationsManifest(
        string Contract,
        IReadOnlyList<OperationCase> Cases);

    private sealed record OperationCase(
        string Name,
        string Policy,
        IReadOnlyList<string> Paths,
        IReadOnlyList<int> Indexes,
        IReadOnlyList<string> Add,
        IReadOnlyList<string>? AddVolumes,
        IReadOnlyList<string> Remove);
}
