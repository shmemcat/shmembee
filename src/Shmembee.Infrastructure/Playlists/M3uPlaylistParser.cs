using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Shmembee.Core.Paths;

namespace Shmembee.Infrastructure.Playlists
{
    public sealed class M3uPlaylistParser
    {
        private readonly bool preserveExtendedInfo;

        public M3uPlaylistParser(bool preserveExtendedInfo = true)
        {
            this.preserveExtendedInfo = preserveExtendedInfo;
        }

        public ParsedPlaylist Parse(
            Stream stream,
            string displayName,
            string? backingName)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            if (string.IsNullOrWhiteSpace(displayName))
            {
                throw new ArgumentException(
                    "A display name is required.",
                    nameof(displayName));
            }

            var entries = new List<ParsedPlaylistEntry>();
            string? pendingTitle = null;
            int? pendingDurationSeconds = null;

            using (var reader = new StreamReader(
                stream,
                new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096,
                leaveOpen: true))
            {
                string? line;
                int lineNumber = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0)
                    {
                        continue;
                    }

                    if (trimmed.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                    {
                        ParseExtendedInfo(
                            trimmed,
                            out pendingDurationSeconds,
                            out pendingTitle);
                        continue;
                    }

                    if (trimmed[0] == '#')
                    {
                        continue;
                    }

                    entries.Add(new ParsedPlaylistEntry(
                        lineNumber,
                        trimmed,
                        TrackPathNormalizer.NormalizePhonePath(trimmed),
                        preserveExtendedInfo ? pendingTitle : null,
                        preserveExtendedInfo ? pendingDurationSeconds : null));
                    pendingTitle = null;
                    pendingDurationSeconds = null;
                }
            }

            return new ParsedPlaylist(displayName, backingName, entries);
        }

        private static void ParseExtendedInfo(
            string line,
            out int? durationSeconds,
            out string? title)
        {
            string value = line.Substring("#EXTINF:".Length);
            int comma = value.IndexOf(',');
            string durationValue = comma < 0 ? value : value.Substring(0, comma);
            int parsedDuration;
            durationSeconds = int.TryParse(durationValue, out parsedDuration)
                ? parsedDuration
                : (int?)null;
            title = comma < 0 || comma == value.Length - 1
                ? null
                : value.Substring(comma + 1);
        }
    }

    public sealed class ParsedPlaylist
    {
        public ParsedPlaylist(
            string displayName,
            string? backingName,
            IEnumerable<ParsedPlaylistEntry> entries)
        {
            DisplayName = displayName;
            BackingName = backingName;
            Entries = new ReadOnlyCollection<ParsedPlaylistEntry>(
                new List<ParsedPlaylistEntry>(entries));
        }

        public string DisplayName { get; }

        public string? BackingName { get; }

        public IReadOnlyList<ParsedPlaylistEntry> Entries { get; }
    }

    public sealed class ParsedPlaylistEntry
    {
        public ParsedPlaylistEntry(
            int lineNumber,
            string sourceValue,
            string normalizedPhonePath,
            string? title,
            int? durationSeconds)
        {
            LineNumber = lineNumber;
            SourceValue = sourceValue;
            NormalizedPhonePath = normalizedPhonePath;
            Title = title;
            DurationSeconds = durationSeconds;
        }

        public int LineNumber { get; }

        public string SourceValue { get; }

        public string NormalizedPhonePath { get; }

        public string? Title { get; }

        public int? DurationSeconds { get; }
    }
}
