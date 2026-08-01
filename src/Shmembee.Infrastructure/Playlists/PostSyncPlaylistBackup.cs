using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Shmembee.Application.Ports;

namespace Shmembee.Infrastructure.Playlists
{
    public sealed class PostSyncPlaylistBackup
    {
        private readonly IPhonePlaylistSnapshotReader snapshotReader;
        private readonly string rootDirectory;
        private readonly Func<DateTimeOffset> now;

        public PostSyncPlaylistBackup(
            IPhonePlaylistSnapshotReader snapshotReader,
            string rootDirectory,
            Func<DateTimeOffset>? now = null)
        {
            this.snapshotReader = snapshotReader
                ?? throw new ArgumentNullException(nameof(snapshotReader));
            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                throw new ArgumentException(
                    "A post-sync backup directory is required.",
                    nameof(rootDirectory));
            }

            this.rootDirectory = Path.GetFullPath(rootDirectory);
            this.now = now ?? (() => DateTimeOffset.Now);
        }

        public string Create()
        {
            IReadOnlyList<PhonePlaylistContent> playlists =
                snapshotReader.ReadPlaylistSnapshot();
            Directory.CreateDirectory(rootDirectory);

            string timestamp = now().ToString(
                "yyyy-MM-dd HH-mm-ss",
                CultureInfo.InvariantCulture);
            string finalDirectory = UniqueDirectory(timestamp);
            string temporaryDirectory = finalDirectory + ".tmp-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(temporaryDirectory);
            try
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (PhonePlaylistContent playlist in playlists)
                {
                    ValidateBackingName(playlist.BackingName, names);
                    File.WriteAllBytes(
                        Path.Combine(temporaryDirectory, playlist.BackingName),
                        playlist.Content);
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

        private string UniqueDirectory(string timestamp)
        {
            string candidate = Path.Combine(rootDirectory, timestamp);
            int suffix = 2;
            while (Directory.Exists(candidate) || File.Exists(candidate))
            {
                candidate = Path.Combine(
                    rootDirectory,
                    timestamp + "-" + suffix.ToString("00", CultureInfo.InvariantCulture));
                suffix++;
            }

            return candidate;
        }

        private static void ValidateBackingName(
            string backingName,
            HashSet<string> names)
        {
            string extension = Path.GetExtension(backingName);
            if (!string.Equals(Path.GetFileName(backingName), backingName, StringComparison.Ordinal)
                || backingName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || (!string.Equals(extension, ".m3u", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(extension, ".m3u8", StringComparison.OrdinalIgnoreCase))
                || !names.Add(backingName))
            {
                throw new InvalidDataException(
                    "The phone returned an unsafe or duplicate playlist name: " + backingName);
            }
        }
    }
}
