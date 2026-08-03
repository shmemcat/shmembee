using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Shmembee.Application.Ports;

namespace Shmembee.Infrastructure.Playlists
{
    public sealed class MusicBeePlaylistBackup
    {
        private static readonly UTF8Encoding Utf8WithoutBom = new UTF8Encoding(false);
        private static readonly char[] LineBreaks = { '\r', '\n' };
        private readonly Func<IReadOnlyList<MusicPlaylist>> readPlaylists;
        private readonly string rootDirectory;
        private readonly Func<DateTimeOffset> now;

        public MusicBeePlaylistBackup(
            Func<IReadOnlyList<MusicPlaylist>> readPlaylists,
            string backupRootDirectory,
            Func<DateTimeOffset>? now = null)
        {
            this.readPlaylists = readPlaylists
                ?? throw new ArgumentNullException(nameof(readPlaylists));
            if (string.IsNullOrWhiteSpace(backupRootDirectory))
            {
                throw new ArgumentException(
                    "A MusicBee playlist backup directory is required.",
                    nameof(backupRootDirectory));
            }

            rootDirectory = Path.Combine(
                Path.GetFullPath(backupRootDirectory),
                "MusicBee Playlists");
            this.now = now ?? (() => DateTimeOffset.Now);
        }

        public string Create()
        {
            IReadOnlyList<MusicPlaylist> playlists = readPlaylists();
            DateTimeOffset timestamp = now();
            string dateDirectory = Path.Combine(
                rootDirectory,
                timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dateDirectory);

            string time = timestamp.ToString("HH-mm-ss", CultureInfo.InvariantCulture);
            string finalDirectory = UniqueDirectory(dateDirectory, time);
            string temporaryDirectory = finalDirectory + ".tmp-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(temporaryDirectory);
            try
            {
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (MusicPlaylist playlist in playlists)
                {
                    string fileName = UniqueFileName(
                        SafeFileName(playlist.Name),
                        usedNames);
                    File.WriteAllBytes(
                        Path.Combine(temporaryDirectory, fileName),
                        WritePlaylist(playlist.TrackUrls));
                }

                Directory.Move(temporaryDirectory, finalDirectory);
                return finalDirectory;
            }
            finally
            {
                if (Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, recursive: true);
                }
            }
        }

        private static string UniqueDirectory(string parent, string name)
        {
            string candidate = Path.Combine(parent, name);
            int suffix = 2;
            while (Directory.Exists(candidate) || File.Exists(candidate))
            {
                candidate = Path.Combine(
                    parent,
                    name + "-" + suffix.ToString("00", CultureInfo.InvariantCulture));
                suffix++;
            }

            return candidate;
        }

        private static string SafeFileName(string name)
        {
            string value = string.IsNullOrWhiteSpace(name) ? "Unnamed Playlist" : name.Trim();
            foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalidCharacter, '_');
            }

            value = value.TrimEnd(' ', '.');
            return (value.Length == 0 ? "Unnamed Playlist" : value) + ".m3u";
        }

        private static string UniqueFileName(string fileName, HashSet<string> usedNames)
        {
            if (usedNames.Add(fileName))
            {
                return fileName;
            }

            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            int suffix = 2;
            string candidate;
            do
            {
                candidate = baseName + "-" + suffix.ToString("00", CultureInfo.InvariantCulture)
                    + extension;
                suffix++;
            }
            while (!usedNames.Add(candidate));

            return candidate;
        }

        private static byte[] WritePlaylist(IReadOnlyList<string> trackUrls)
        {
            if (trackUrls == null)
            {
                throw new InvalidDataException("A MusicBee playlist has no track list.");
            }

            string[] lines = trackUrls.Select(trackUrl =>
            {
                if (string.IsNullOrWhiteSpace(trackUrl)
                    || trackUrl.IndexOfAny(LineBreaks) >= 0)
                {
                    throw new InvalidDataException(
                        "A MusicBee playlist contains an invalid track path.");
                }

                return trackUrl.Trim();
            }).ToArray();
            string content = string.Join("\n", lines);
            if (content.Length > 0)
            {
                content += "\n";
            }

            return Utf8WithoutBom.GetBytes(content);
        }
    }
}
