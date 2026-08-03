using System.Text;
using Shmembee.Application.Desktop;
using Shmembee.Application.Ports;
using Shmembee.Infrastructure.Settings;

namespace Shmembee.Core.Tests;

public sealed class PlaylistCatalogTests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "shmembee-catalog-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void AutoPairsUniqueNormalizedNamesAndKeepsOneSidedRows()
    {
        PlaylistCatalog catalog = Build(
            new[]
            {
                Music("mb-road", "  Road   Trip "),
                Music("mb-only", "Desktop only")
            },
            new[]
            {
                Phone("phone-road", "road trip.M3U8"),
                Phone("phone-only", "Phone only.m3u")
            });

        PlaylistCatalogRow paired = Assert.Single(
            catalog.Rows,
            row => row.Status == PlaylistPairingStatus.Paired);
        Assert.Equal(PlaylistPairingSource.NormalizedName, paired.PairingSource);
        Assert.Equal("mb-road", paired.MusicBeePlaylist!.Url);
        Assert.Equal("phone-road", paired.PhonePlaylist!.Id);
        Assert.Contains(catalog.Rows, row =>
            row.Status == PlaylistPairingStatus.MusicBeeOnly
            && row.MusicBeePlaylist!.Url == "mb-only");
        Assert.Contains(catalog.Rows, row =>
            row.Status == PlaylistPairingStatus.PhoneOnly
            && row.PhonePlaylist!.Id == "phone-only");
    }

    [Fact]
    public void AutoPairsMusicBeeFolderPathByLeafName()
    {
        PlaylistCatalog catalog = Build(
            new[]
            {
                Music("mb-battle", @"Dungeons & Dragons\DnD Battle"),
                Music("mb-tavern", "Dungeons & Dragons/DnD Tavern")
            },
            new[]
            {
                Phone("phone-battle", "DnD Battle.m3u"),
                Phone("phone-tavern", "DnD Tavern.m3u8")
            });

        Assert.Equal(2, catalog.Rows.Count);
        Assert.All(catalog.Rows, row =>
            Assert.Equal(PlaylistPairingStatus.Paired, row.Status));
        Assert.Contains(catalog.Rows, row =>
            row.MusicBeePlaylist!.Url == "mb-battle"
            && row.PhonePlaylist!.Id == "phone-battle");
        Assert.Contains(catalog.Rows, row =>
            row.MusicBeePlaylist!.Url == "mb-tavern"
            && row.PhonePlaylist!.Id == "phone-tavern");
    }

    [Fact]
    public void DuplicateLeafNamesAcrossMusicBeeFoldersRemainAmbiguous()
    {
        PlaylistCatalog catalog = Build(
            new[]
            {
                Music("mb-first", @"First\Mix"),
                Music("mb-second", @"Second\Mix")
            },
            new[] { Phone("phone-mix", "Mix.m3u") });

        PlaylistCatalogRow row = Assert.Single(catalog.Rows);
        Assert.Equal(PlaylistPairingStatus.Ambiguous, row.Status);
        Assert.Equal(2, row.MusicBeeCandidates.Count);
        Assert.Single(row.PhoneCandidates);
    }

    [Theory]
    [InlineData(@"Dungeons & Dragons\DnD Battle", "DnD Battle.m3u")]
    [InlineData("Dungeons & Dragons/DnD Battle", "DnD Battle.m3u")]
    [InlineData(@"Folder\Existing.m3u8", "Existing.m3u8")]
    public void PhoneBackingNameUsesMusicBeeLeafName(string name, string expected)
    {
        Assert.Equal(
            expected,
            PlaylistCatalogService.CreatePhoneBackingName(name));
    }

    [Fact]
    public void DuplicateNormalizedNamesProduceOneAmbiguousNonActionableRow()
    {
        PlaylistCatalog catalog = Build(
            new[]
            {
                Music("mb-a", "Mix"),
                Music("mb-b", " mix ")
            },
            new[] { Phone("phone-a", "MIX.m3u") });

        PlaylistCatalogRow row = Assert.Single(catalog.Rows);
        Assert.Equal(PlaylistPairingStatus.Ambiguous, row.Status);
        Assert.Equal(2, row.MusicBeeCandidates.Count);
        Assert.Single(row.PhoneCandidates);
        Assert.False(row.IsActionable);
    }

    [Fact]
    public void ExplicitCorrectionOverridesNamesAndLeavesOtherRowsIndependent()
    {
        PlaylistCatalog catalog = Build(
            new[]
            {
                Music("mb-a", "Alpha"),
                Music("mb-b", "Beta")
            },
            new[]
            {
                Phone("phone-a", "Alpha.m3u"),
                Phone("phone-b", "Gamma.m3u")
            },
            new[] { new PlaylistPairingCorrection("mb-b", "phone-b") });

        Assert.Contains(catalog.Rows, row =>
            row.Status == PlaylistPairingStatus.Paired
            && row.PairingSource == PlaylistPairingSource.ExplicitCorrection
            && row.MusicBeePlaylist!.Url == "mb-b"
            && row.PhonePlaylist!.Id == "phone-b");
        Assert.Contains(catalog.Rows, row =>
            row.Status == PlaylistPairingStatus.Paired
            && row.PairingSource == PlaylistPairingSource.NormalizedName
            && row.MusicBeePlaylist!.Url == "mb-a");
    }

    [Fact]
    public void ListingFailureIsIsolatedAsErrorRow()
    {
        var service = new PlaylistCatalogService(
            new ThrowingMusicReader(),
            new PhoneReader(new[] { Phone("phone-a", "Available.m3u") }));

        PlaylistCatalog catalog = service.Build();

        PlaylistCatalogRow error = Assert.Single(
            catalog.Rows,
            row => row.Status == PlaylistPairingStatus.Error);
        Assert.Contains("MusicBee playlist listing failed", error.Error);
        Assert.Contains(catalog.Rows, row =>
            row.Status == PlaylistPairingStatus.PhoneOnly
            && row.PhonePlaylist!.Id == "phone-a");
    }

    [Fact]
    public void LegacySettingsLoadWithStablePairingIdentities()
    {
        Directory.CreateDirectory(temporaryDirectory);
        string settingsPath = Path.Combine(temporaryDirectory, "settings.json");
        var store = new DesktopSettingsStore(settingsPath);
        var legacy = new DesktopSettings();
        legacy.PlaylistAssociations.Add(
            new PlaylistAssociation("legacy-id", "Legacy.m3u", "Legacy"));
        store.Save(legacy);
        string serialized = File.ReadAllText(settingsPath, Encoding.UTF8)
            .Replace(",\"MusicBeePlaylistId\":\"legacy-id\"", string.Empty)
            .Replace(",\"PhonePlaylistId\":\"Legacy.m3u\"", string.Empty)
            .Replace(",\"IsExplicitCorrection\":false", string.Empty);
        File.WriteAllText(settingsPath, serialized, Encoding.UTF8);

        DesktopSettings settings = store.Load();
        PlaylistAssociation association = Assert.Single(settings.PlaylistAssociations);

        Assert.Equal("legacy-id", association.MusicBeePlaylistId);
        Assert.Equal("Legacy.m3u", association.PhonePlaylistId);
        Assert.False(association.IsExplicitCorrection);
        PlaylistPairingCorrection correction = association.ToPairingCorrection();
        Assert.Equal("legacy-id", correction.MusicBeePlaylistId);
        Assert.Equal("Legacy.m3u", correction.PhonePlaylistId);
    }

    [Fact]
    public void ExplicitCorrectionRoundTripsThroughSettings()
    {
        Directory.CreateDirectory(temporaryDirectory);
        string settingsPath = Path.Combine(temporaryDirectory, "settings.json");
        var store = new DesktopSettingsStore(settingsPath);
        var settings = new DesktopSettings();
        settings.PlaylistAssociations.Add(
            PlaylistAssociation.FromExplicitCorrection(
                "mb-url",
                "Desktop name",
                "wpd-object-id",
                "Phone name.m3u8"));

        store.Save(settings);
        PlaylistAssociation association = Assert.Single(
            store.Load().PlaylistAssociations);

        Assert.Equal("mb-url", association.MusicBeePlaylistId);
        Assert.Equal("wpd-object-id", association.PhonePlaylistId);
        Assert.Equal("Phone name.m3u8", association.PhoneBackingName);
        Assert.True(association.IsExplicitCorrection);
    }

    public void Dispose()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static PlaylistCatalog Build(
        IReadOnlyList<MusicPlaylist> music,
        IReadOnlyList<PhonePlaylistFile> phone,
        IEnumerable<PlaylistPairingCorrection>? corrections = null) =>
        new PlaylistCatalogService(
            new MusicReader(music),
            new PhoneReader(phone))
        .Build(corrections);

    private static MusicPlaylist Music(string id, string name) =>
        new(id, name, Array.Empty<string>());

    private static PhonePlaylistFile Phone(string id, string backingName) =>
        new(id, backingName);

    private sealed class MusicReader : IMusicLibraryReader
    {
        private readonly IReadOnlyList<MusicPlaylist> playlists;

        public MusicReader(IReadOnlyList<MusicPlaylist> playlists)
        {
            this.playlists = playlists;
        }

        public IReadOnlyList<MusicLibraryTrack> ReadLibrary() =>
            Array.Empty<MusicLibraryTrack>();

        public IReadOnlyList<MusicPlaylist> ReadPlaylists() => playlists;

        public MusicLibraryTrack ReadTrack(string url) =>
            new(url, null, null, null);
    }

    private sealed class ThrowingMusicReader : IMusicLibraryReader
    {
        public IReadOnlyList<MusicLibraryTrack> ReadLibrary() =>
            Array.Empty<MusicLibraryTrack>();

        public IReadOnlyList<MusicPlaylist> ReadPlaylists() =>
            throw new IOException("unavailable");

        public MusicLibraryTrack ReadTrack(string url) =>
            new(url, null, null, null);
    }

    private sealed class PhoneReader : IPhonePlaylistCatalogReader
    {
        private readonly IReadOnlyList<PhonePlaylistFile> playlists;

        public PhoneReader(IReadOnlyList<PhonePlaylistFile> playlists)
        {
            this.playlists = playlists;
        }

        public IReadOnlyList<PhonePlaylistFile> ListPlaylists() => playlists;
    }
}
