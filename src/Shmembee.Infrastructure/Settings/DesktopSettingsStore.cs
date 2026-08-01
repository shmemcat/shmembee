using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using Shmembee.Application.Desktop;

namespace Shmembee.Infrastructure.Settings
{
    [DataContract]
    public sealed class DesktopSettings
    {
        public const string DefaultDeviceName = "MLE S24U";
        public const string DefaultStorageName = "Internal storage";
        public const string DefaultPlaylistFolder = "gmmp/playlists";

        public DesktopSettings()
        {
            DeviceName = DefaultDeviceName;
            StorageName = DefaultStorageName;
            PlaylistFolder = DefaultPlaylistFolder;
            TimeoutSeconds = 300;
            DatabasePath = string.Empty;
            BackupPath = string.Empty;
            PlaylistAssociations = new List<PlaylistAssociation>();
        }

        [DataMember(Order = 1)]
        public string DeviceName { get; set; }

        [DataMember(Order = 2)]
        public string StorageName { get; set; }

        [DataMember(Order = 3)]
        public string PlaylistFolder { get; set; }

        [DataMember(Order = 4)]
        public int TimeoutSeconds { get; set; }

        [DataMember(Order = 5)]
        public string DatabasePath { get; set; }

        [DataMember(Order = 6)]
        public string BackupPath { get; set; }

        [DataMember(Order = 7)]
        public bool PlaylistSyncWarningAcknowledged { get; set; }

        [DataMember(Order = 8)]
        public List<PlaylistAssociation> PlaylistAssociations { get; set; }
    }

    [DataContract]
    public sealed class PlaylistAssociation
    {
        public PlaylistAssociation()
        {
            PlaylistId = string.Empty;
            PhoneBackingName = string.Empty;
        }

        public PlaylistAssociation(
            string playlistId,
            string phoneBackingName,
            string? musicBeePlaylistName = null,
            string? musicBeePlaylistId = null,
            string? phonePlaylistId = null,
            bool isExplicitCorrection = false)
        {
            PlaylistId = playlistId;
            PhoneBackingName = phoneBackingName;
            MusicBeePlaylistName = musicBeePlaylistName ?? playlistId;
            MusicBeePlaylistId = musicBeePlaylistId ?? playlistId;
            PhonePlaylistId = phonePlaylistId ?? phoneBackingName;
            IsExplicitCorrection = isExplicitCorrection;
        }

        [DataMember(Order = 1)]
        public string PlaylistId { get; set; }

        [DataMember(Order = 2)]
        public string PhoneBackingName { get; set; }

        [DataMember(Order = 3)]
        public string MusicBeePlaylistName { get; set; } = string.Empty;

        [DataMember(Order = 4, EmitDefaultValue = false)]
        public string MusicBeePlaylistId { get; set; } = string.Empty;

        [DataMember(Order = 5, EmitDefaultValue = false)]
        public string PhonePlaylistId { get; set; } = string.Empty;

        [DataMember(Order = 6, EmitDefaultValue = false)]
        public bool IsExplicitCorrection { get; set; }

        public PlaylistPairingCorrection ToPairingCorrection() =>
            new PlaylistPairingCorrection(
                string.IsNullOrWhiteSpace(MusicBeePlaylistId)
                    ? PlaylistId
                    : MusicBeePlaylistId,
                string.IsNullOrWhiteSpace(PhonePlaylistId)
                    ? PhoneBackingName
                    : PhonePlaylistId);

        public static PlaylistAssociation FromExplicitCorrection(
            string musicBeePlaylistId,
            string musicBeePlaylistName,
            string phonePlaylistId,
            string phoneBackingName) =>
            new PlaylistAssociation(
                musicBeePlaylistId,
                phoneBackingName,
                musicBeePlaylistName,
                musicBeePlaylistId,
                phonePlaylistId,
                isExplicitCorrection: true);
    }

    public sealed class DesktopSettingsStore
    {
        private readonly string settingsPath;

        public DesktopSettingsStore(string settingsPath)
        {
            if (string.IsNullOrWhiteSpace(settingsPath))
            {
                throw new ArgumentException("A settings path is required.", nameof(settingsPath));
            }

            this.settingsPath = Path.GetFullPath(settingsPath);
        }

        public DesktopSettings Load()
        {
            if (!File.Exists(settingsPath))
            {
                return new DesktopSettings();
            }

            try
            {
                using (FileStream stream = File.OpenRead(settingsPath))
                {
                    var serializer = new DataContractJsonSerializer(typeof(DesktopSettings));
                    var settings = serializer.ReadObject(stream) as DesktopSettings;
                    return settings == null ? new DesktopSettings() : Validate(settings);
                }
            }
            catch (Exception exception) when (
                exception is IOException
                || exception is SerializationException
                || exception is InvalidDataContractException
                || exception is UnauthorizedAccessException)
            {
                return LoadLegacyJson();
            }
        }

        public void Save(DesktopSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            DesktopSettings validated = Validate(settings);
            string? directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = settingsPath + ".tmp";
            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                {
                    var serializer = new DataContractJsonSerializer(typeof(DesktopSettings));
                    serializer.WriteObject(stream, validated);
                    stream.Flush();
                }

                if (File.Exists(settingsPath))
                {
                    File.Replace(temporaryPath, settingsPath, null);
                }
                else
                {
                    File.Move(temporaryPath, settingsPath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        private static DesktopSettings Validate(DesktopSettings settings)
        {
            var validated = new DesktopSettings
            {
                DeviceName = OptionalOrDefault(
                    settings.DeviceName,
                    DesktopSettings.DefaultDeviceName),
                StorageName = OptionalOrDefault(
                    settings.StorageName,
                    DesktopSettings.DefaultStorageName),
                PlaylistFolder = ValidFolderOrDefault(settings.PlaylistFolder),
                TimeoutSeconds = settings.TimeoutSeconds < 5
                    || settings.TimeoutSeconds > 1800
                    ? 300
                    : settings.TimeoutSeconds,
                DatabasePath = settings.DatabasePath?.Trim() ?? string.Empty,
                BackupPath = settings.BackupPath?.Trim() ?? string.Empty,
                PlaylistSyncWarningAcknowledged =
                    settings.PlaylistSyncWarningAcknowledged,
                PlaylistAssociations = ValidateAssociations(settings.PlaylistAssociations)
            };
            return validated;
        }

        private DesktopSettings LoadLegacy()
        {
            try
            {
                using (FileStream stream = File.OpenRead(settingsPath))
                {
                    var serializer = new DataContractJsonSerializer(
                        typeof(LegacyDesktopSettings));
                    var legacy = serializer.ReadObject(stream) as LegacyDesktopSettings;
                    if (legacy == null)
                    {
                        return new DesktopSettings();
                    }

                    return Validate(new DesktopSettings
                    {
                        DeviceName = legacy.DeviceName ?? DesktopSettings.DefaultDeviceName,
                        StorageName = legacy.StorageName ?? DesktopSettings.DefaultStorageName,
                        PlaylistFolder =
                            legacy.PlaylistFolder ?? DesktopSettings.DefaultPlaylistFolder,
                        TimeoutSeconds = legacy.TimeoutSeconds,
                        DatabasePath = legacy.DatabasePath ?? string.Empty,
                        BackupPath = legacy.BackupPath ?? string.Empty,
                        PlaylistSyncWarningAcknowledged =
                            legacy.PlaylistSyncWarningAcknowledged,
                        PlaylistAssociations = (legacy.PlaylistAssociations
                                ?? new List<LegacyPlaylistAssociation>())
                            .Select(item => new PlaylistAssociation(
                                item.PlaylistId ?? string.Empty,
                                item.PhoneBackingName ?? string.Empty,
                                item.MusicBeePlaylistName))
                            .ToList()
                    });
                }
            }
            catch (Exception exception) when (
                exception is IOException
                || exception is SerializationException
                || exception is InvalidDataContractException
                || exception is UnauthorizedAccessException)
            {
                return new DesktopSettings();
            }
        }

        private DesktopSettings LoadLegacyJson()
        {
            try
            {
                string json = File.ReadAllText(settingsPath);
                var settings = new DesktopSettings
                {
                    DeviceName = JsonString(json, "DeviceName")
                        ?? DesktopSettings.DefaultDeviceName,
                    StorageName = JsonString(json, "StorageName")
                        ?? DesktopSettings.DefaultStorageName,
                    PlaylistFolder = JsonString(json, "PlaylistFolder")
                        ?? DesktopSettings.DefaultPlaylistFolder,
                    DatabasePath = JsonString(json, "DatabasePath") ?? string.Empty,
                    BackupPath = JsonString(json, "BackupPath") ?? string.Empty
                };
                string? timeout = JsonNumber(json, "TimeoutSeconds");
                settings.TimeoutSeconds = int.TryParse(timeout, out int seconds)
                    ? seconds
                    : 300;
                MatchCollection associations = Regex.Matches(
                    json,
                    "\\{[^{}]*\\\"PlaylistId\\\"\\s*:\\s*\\\"(?<id>[^\\\"]+)\\\""
                        + "[^{}]*\\\"PhoneBackingName\\\"\\s*:\\s*\\\"(?<phone>[^\\\"]+)\\\""
                        + "[^{}]*\\\"MusicBeePlaylistName\\\"\\s*:\\s*\\\"(?<name>[^\\\"]*)\\\""
                        + "[^{}]*\\}",
                    RegexOptions.CultureInvariant);
                foreach (Match match in associations)
                {
                    settings.PlaylistAssociations.Add(new PlaylistAssociation(
                        match.Groups["id"].Value,
                        match.Groups["phone"].Value,
                        match.Groups["name"].Value));
                }

                return Validate(settings);
            }
            catch (Exception exception) when (
                exception is IOException || exception is UnauthorizedAccessException)
            {
                return new DesktopSettings();
            }
        }

        private static string? JsonString(string json, string property)
        {
            Match match = Regex.Match(
                json,
                "\\\"" + Regex.Escape(property)
                    + "\\\"\\s*:\\s*\\\"(?<value>[^\\\"]*)\\\"",
                RegexOptions.CultureInvariant);
            return match.Success ? match.Groups["value"].Value : null;
        }

        private static string? JsonNumber(string json, string property)
        {
            Match match = Regex.Match(
                json,
                "\\\"" + Regex.Escape(property)
                    + "\\\"\\s*:\\s*(?<value>-?\\d+)",
                RegexOptions.CultureInvariant);
            return match.Success ? match.Groups["value"].Value : null;
        }

        [DataContract]
        private sealed class LegacyDesktopSettings
        {
            [DataMember(Order = 1, EmitDefaultValue = false)]
            public string? DeviceName { get; set; }

            [DataMember(Order = 2, EmitDefaultValue = false)]
            public string? StorageName { get; set; }

            [DataMember(Order = 3, EmitDefaultValue = false)]
            public string? PlaylistFolder { get; set; }

            [DataMember(Order = 4)]
            public int TimeoutSeconds { get; set; }

            [DataMember(Order = 5, EmitDefaultValue = false)]
            public string? DatabasePath { get; set; }

            [DataMember(Order = 6, EmitDefaultValue = false)]
            public string? BackupPath { get; set; }

            [DataMember(Order = 7)]
            public bool PlaylistSyncWarningAcknowledged { get; set; }

            [DataMember(Order = 8, EmitDefaultValue = false)]
            public List<LegacyPlaylistAssociation>? PlaylistAssociations { get; set; }
        }

        [DataContract]
        private sealed class LegacyPlaylistAssociation
        {
            [DataMember(Order = 1, EmitDefaultValue = false)]
            public string? PlaylistId { get; set; }

            [DataMember(Order = 2, EmitDefaultValue = false)]
            public string? PhoneBackingName { get; set; }

            [DataMember(Order = 3, EmitDefaultValue = false)]
            public string? MusicBeePlaylistName { get; set; }
        }

        private static List<PlaylistAssociation> ValidateAssociations(
            IEnumerable<PlaylistAssociation>? associations)
        {
            var result = new List<PlaylistAssociation>();
            var playlistIds = new HashSet<string>(StringComparer.Ordinal);
            if (associations == null)
            {
                return result;
            }

            foreach (PlaylistAssociation? association in associations)
            {
                if (association == null
                    || string.IsNullOrWhiteSpace(association.PlaylistId)
                    || !IsSafeBackingName(association.PhoneBackingName))
                {
                    continue;
                }

                string playlistId = association.PlaylistId.Trim();
                if (playlistIds.Add(playlistId))
                {
                    result.Add(new PlaylistAssociation(
                        playlistId,
                        association.PhoneBackingName.Trim(),
                        string.IsNullOrWhiteSpace(association.MusicBeePlaylistName)
                            ? playlistId
                            : association.MusicBeePlaylistName.Trim(),
                        string.IsNullOrWhiteSpace(association.MusicBeePlaylistId)
                            ? playlistId
                            : association.MusicBeePlaylistId.Trim(),
                        string.IsNullOrWhiteSpace(association.PhonePlaylistId)
                            ? association.PhoneBackingName.Trim()
                            : association.PhonePlaylistId.Trim(),
                        association.IsExplicitCorrection));
                }
            }

            return result;
        }

        private static string OptionalOrDefault(string? value, string defaultValue) =>
            string.IsNullOrWhiteSpace(value) ? defaultValue : value!.Trim();

        private static string ValidFolderOrDefault(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return DesktopSettings.DefaultPlaylistFolder;
            }

            string folder = value!.Trim().Replace('\\', '/').Trim('/');
            if (folder.Length == 0
                || folder.StartsWith("../", StringComparison.Ordinal)
                || folder.EndsWith("/..", StringComparison.Ordinal)
                || folder.IndexOf("/../", StringComparison.Ordinal) >= 0
                || Path.IsPathRooted(folder))
            {
                return DesktopSettings.DefaultPlaylistFolder;
            }

            return folder;
        }

        private static bool IsSafeBackingName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string name = value!.Trim();
            string extension = Path.GetExtension(name);
            return string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)
                && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                && (string.Equals(extension, ".m3u", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".m3u8", StringComparison.OrdinalIgnoreCase));
        }
    }
}
