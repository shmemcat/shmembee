using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using Shmembee.Application.Ports;

namespace Shmembee.Application.Desktop
{
    public enum PlaylistPairingStatus
    {
        Paired,
        MusicBeeOnly,
        PhoneOnly,
        Ambiguous,
        Error
    }

    public enum PlaylistPairingSource
    {
        None,
        NormalizedName,
        ExplicitCorrection
    }

    public sealed class PlaylistPairingCorrection
    {
        public PlaylistPairingCorrection(
            string musicBeePlaylistId,
            string phonePlaylistId)
        {
            MusicBeePlaylistId = Require(musicBeePlaylistId, nameof(musicBeePlaylistId));
            PhonePlaylistId = Require(phonePlaylistId, nameof(phonePlaylistId));
        }

        public string MusicBeePlaylistId { get; }

        public string PhonePlaylistId { get; }

        private static string Require(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("A non-empty playlist identity is required.", parameterName);
            }

            return value.Trim();
        }
    }

    public sealed class PlaylistCatalogRow
    {
        public PlaylistCatalogRow(
            string rowId,
            PlaylistPairingStatus status,
            PlaylistPairingSource pairingSource,
            IEnumerable<MusicPlaylist>? musicBeeCandidates,
            IEnumerable<PhonePlaylistFile>? phoneCandidates,
            string? error = null)
        {
            RowId = rowId;
            Status = status;
            PairingSource = pairingSource;
            MusicBeeCandidates = ReadOnly(musicBeeCandidates);
            PhoneCandidates = ReadOnly(phoneCandidates);
            Error = error;
        }

        public string RowId { get; }

        public PlaylistPairingStatus Status { get; }

        public PlaylistPairingSource PairingSource { get; }

        public IReadOnlyList<MusicPlaylist> MusicBeeCandidates { get; }

        public IReadOnlyList<PhonePlaylistFile> PhoneCandidates { get; }

        public string? Error { get; }

        public MusicPlaylist? MusicBeePlaylist =>
            MusicBeeCandidates.Count == 1 ? MusicBeeCandidates[0] : null;

        public PhonePlaylistFile? PhonePlaylist =>
            PhoneCandidates.Count == 1 ? PhoneCandidates[0] : null;

        public string DisplayName =>
            MusicBeePlaylist?.Name
            ?? PhonePlaylist?.DisplayName
            ?? (MusicBeeCandidates.Count == 0 ? null : MusicBeeCandidates[0].Name)
            ?? (PhoneCandidates.Count == 0 ? null : PhoneCandidates[0].DisplayName)
            ?? "Playlist catalog";

        public bool IsActionable => Status == PlaylistPairingStatus.Paired;

        private static ReadOnlyCollection<T> ReadOnly<T>(IEnumerable<T>? values) =>
            new ReadOnlyCollection<T>(
                new List<T>(values ?? Enumerable.Empty<T>()));
    }

    public sealed class PlaylistCatalog
    {
        public PlaylistCatalog(IEnumerable<PlaylistCatalogRow> rows)
        {
            Rows = new ReadOnlyCollection<PlaylistCatalogRow>(
                new List<PlaylistCatalogRow>(rows ?? throw new ArgumentNullException(nameof(rows))));
        }

        public IReadOnlyList<PlaylistCatalogRow> Rows { get; }

        public bool HasErrors => Rows.Any(row => row.Status == PlaylistPairingStatus.Error);
    }

    public sealed class PlaylistCatalogService
    {
        private readonly IMusicLibraryReader musicBee;
        private readonly IPhonePlaylistCatalogReader phone;

        public PlaylistCatalogService(
            IMusicLibraryReader musicBee,
            IPhonePlaylistCatalogReader phone)
        {
            this.musicBee = musicBee ?? throw new ArgumentNullException(nameof(musicBee));
            this.phone = phone ?? throw new ArgumentNullException(nameof(phone));
        }

        public PlaylistCatalog Build(
            IEnumerable<PlaylistPairingCorrection>? corrections = null)
        {
            var rows = new List<PlaylistCatalogRow>();
            IReadOnlyList<MusicPlaylist> musicBeePlaylists = ReadMusicBee(rows);
            IReadOnlyList<PhonePlaylistFile> phonePlaylists = ReadPhone(rows);
            var remainingMusicBee = musicBeePlaylists
                .GroupBy(item => item.Url, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            var remainingPhone = phonePlaylists
                .GroupBy(item => item.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();

            ApplyCorrections(
                corrections,
                remainingMusicBee,
                remainingPhone,
                rows);
            ApplyNormalizedNamePairing(remainingMusicBee, remainingPhone, rows);

            foreach (MusicPlaylist playlist in remainingMusicBee)
            {
                rows.Add(Row(
                    PlaylistPairingStatus.MusicBeeOnly,
                    PlaylistPairingSource.None,
                    new[] { playlist },
                    Array.Empty<PhonePlaylistFile>()));
            }

            foreach (PhonePlaylistFile playlist in remainingPhone)
            {
                rows.Add(Row(
                    PlaylistPairingStatus.PhoneOnly,
                    PlaylistPairingSource.None,
                    Array.Empty<MusicPlaylist>(),
                    new[] { playlist }));
            }

            return new PlaylistCatalog(rows
                .OrderBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Status)
                .ThenBy(row => row.RowId, StringComparer.Ordinal));
        }

        public static string NormalizePlaylistName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string withoutExtension = GetLeafPlaylistName(value);
            string extension = Path.GetExtension(withoutExtension);
            if (string.Equals(extension, ".m3u", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                withoutExtension = withoutExtension.Substring(
                    0,
                    withoutExtension.Length - extension.Length);
            }

            var result = new StringBuilder();
            bool pendingSpace = false;
            foreach (char character in withoutExtension.Normalize(NormalizationForm.FormKC))
            {
                if (char.IsWhiteSpace(character))
                {
                    pendingSpace = result.Length > 0;
                    continue;
                }

                if (pendingSpace)
                {
                    result.Append(' ');
                    pendingSpace = false;
                }

                result.Append(char.ToUpperInvariant(character));
            }

            return result.ToString();
        }

        public static string GetLeafPlaylistName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string trimmed = value.Trim();
            int separator = Math.Max(
                trimmed.LastIndexOf('/'),
                trimmed.LastIndexOf('\\'));
            return separator < 0
                ? trimmed
                : trimmed.Substring(separator + 1).Trim();
        }

        public static string CreatePhoneBackingName(string musicBeePlaylistName)
        {
            string leafName = GetLeafPlaylistName(musicBeePlaylistName);
            if (leafName.Length == 0)
            {
                throw new ArgumentException(
                    "A MusicBee playlist leaf name is required.",
                    nameof(musicBeePlaylistName));
            }

            string extension = Path.GetExtension(leafName);
            return string.Equals(extension, ".m3u", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".m3u8", StringComparison.OrdinalIgnoreCase)
                ? leafName
                : leafName + ".m3u";
        }

        private IReadOnlyList<MusicPlaylist> ReadMusicBee(List<PlaylistCatalogRow> rows)
        {
            try
            {
                return musicBee.ReadPlaylists() ?? Array.Empty<MusicPlaylist>();
            }
            catch (Exception exception)
            {
                rows.Add(ErrorRow("musicbee", "MusicBee playlist listing failed: " + exception.Message));
                return Array.Empty<MusicPlaylist>();
            }
        }

        private IReadOnlyList<PhonePlaylistFile> ReadPhone(List<PlaylistCatalogRow> rows)
        {
            try
            {
                return phone.ListPlaylists() ?? Array.Empty<PhonePlaylistFile>();
            }
            catch (Exception exception)
            {
                rows.Add(ErrorRow("phone", "Phone playlist listing failed: " + exception.Message));
                return Array.Empty<PhonePlaylistFile>();
            }
        }

        private static void ApplyCorrections(
            IEnumerable<PlaylistPairingCorrection>? corrections,
            List<MusicPlaylist> musicBee,
            List<PhonePlaylistFile> phone,
            List<PlaylistCatalogRow> rows)
        {
            var usedMusicBee = new HashSet<string>(StringComparer.Ordinal);
            var usedPhone = new HashSet<string>(StringComparer.Ordinal);
            foreach (PlaylistPairingCorrection correction in
                corrections ?? Enumerable.Empty<PlaylistPairingCorrection>())
            {
                if (correction == null
                    || !usedMusicBee.Add(correction.MusicBeePlaylistId)
                    || !usedPhone.Add(correction.PhonePlaylistId))
                {
                    continue;
                }

                MusicPlaylist? musicBeeMatch = musicBee.FirstOrDefault(
                    item => string.Equals(
                        item.Url,
                        correction.MusicBeePlaylistId,
                        StringComparison.Ordinal));
                PhonePlaylistFile? phoneMatch = phone.FirstOrDefault(
                    item => string.Equals(
                        item.Id,
                        correction.PhonePlaylistId,
                        StringComparison.Ordinal));
                if (musicBeeMatch == null || phoneMatch == null)
                {
                    continue;
                }

                rows.Add(Row(
                    PlaylistPairingStatus.Paired,
                    PlaylistPairingSource.ExplicitCorrection,
                    new[] { musicBeeMatch },
                    new[] { phoneMatch }));
                musicBee.Remove(musicBeeMatch);
                phone.Remove(phoneMatch);
            }
        }

        private static void ApplyNormalizedNamePairing(
            List<MusicPlaylist> musicBee,
            List<PhonePlaylistFile> phone,
            List<PlaylistCatalogRow> rows)
        {
            var musicGroups = musicBee
                .GroupBy(item => NormalizePlaylistName(item.Name))
                .Where(group => group.Key.Length > 0)
                .ToDictionary(group => group.Key, group => group.ToList());
            var phoneGroups = phone
                .GroupBy(item => NormalizePlaylistName(item.DisplayName))
                .Where(group => group.Key.Length > 0)
                .ToDictionary(group => group.Key, group => group.ToList());

            foreach (string name in musicGroups.Keys.Intersect(
                phoneGroups.Keys,
                StringComparer.Ordinal))
            {
                List<MusicPlaylist> musicMatches = musicGroups[name];
                List<PhonePlaylistFile> phoneMatches = phoneGroups[name];
                PlaylistPairingStatus status =
                    musicMatches.Count == 1 && phoneMatches.Count == 1
                        ? PlaylistPairingStatus.Paired
                        : PlaylistPairingStatus.Ambiguous;
                rows.Add(Row(
                    status,
                    PlaylistPairingSource.NormalizedName,
                    musicMatches,
                    phoneMatches));
                foreach (MusicPlaylist item in musicMatches)
                {
                    musicBee.Remove(item);
                }

                foreach (PhonePlaylistFile item in phoneMatches)
                {
                    phone.Remove(item);
                }
            }
        }

        private static PlaylistCatalogRow Row(
            PlaylistPairingStatus status,
            PlaylistPairingSource source,
            IEnumerable<MusicPlaylist> musicBee,
            IEnumerable<PhonePlaylistFile> phone)
        {
            MusicPlaylist[] musicBeeValues = musicBee.ToArray();
            PhonePlaylistFile[] phoneValues = phone.ToArray();
            string rowId = string.Join(
                "|",
                musicBeeValues.Select(item => "mb:" + item.Url)
                    .Concat(phoneValues.Select(item => "phone:" + item.Id)));
            return new PlaylistCatalogRow(
                rowId,
                status,
                source,
                musicBeeValues,
                phoneValues);
        }

        private static PlaylistCatalogRow ErrorRow(string endpoint, string error) =>
            new PlaylistCatalogRow(
                "error:" + endpoint,
                PlaylistPairingStatus.Error,
                PlaylistPairingSource.None,
                Array.Empty<MusicPlaylist>(),
                Array.Empty<PhonePlaylistFile>(),
                error);
    }
}
