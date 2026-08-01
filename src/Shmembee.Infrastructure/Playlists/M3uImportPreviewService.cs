using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Shmembee.Core.Resolution;

namespace Shmembee.Infrastructure.Playlists
{
    public sealed class M3uImportPreviewService
    {
        private readonly M3uPlaylistParser parser;
        private readonly TrackResolver resolver;

        public M3uImportPreviewService()
            : this(new M3uPlaylistParser(), new TrackResolver())
        {
        }

        public M3uImportPreviewService(
            M3uPlaylistParser parser,
            TrackResolver resolver)
        {
            this.parser = parser ?? throw new ArgumentNullException(nameof(parser));
            this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        }

        public M3uImportPreview Preview(
            string filePath,
            IEnumerable<LibraryTrack> library,
            IReadOnlyDictionary<string, string>? approvedMappings = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException(
                    "An M3U or M3U8 file path is required.",
                    nameof(filePath));
            }

            string extension = Path.GetExtension(filePath);
            if (!string.Equals(extension, ".m3u", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Only M3U and M3U8 files can be previewed.",
                    nameof(filePath));
            }

            List<LibraryTrack> indexedLibrary = library?.ToList()
                ?? throw new ArgumentNullException(nameof(library));
            ParsedPlaylist parsed;
            using (FileStream stream = File.OpenRead(filePath))
            {
                parsed = parser.Parse(
                    stream,
                    Path.GetFileNameWithoutExtension(filePath),
                    Path.GetFileName(filePath));
            }

            var entries = parsed.Entries
                .Select(entry => new M3uImportPreviewEntry(
                    entry,
                    resolver.Resolve(
                        new TrackReference(
                            entry.NormalizedPhonePath,
                            title: entry.Title,
                            durationSeconds: entry.DurationSeconds),
                        indexedLibrary,
                        approvedMappings)))
                .ToList();
            return new M3uImportPreview(parsed, entries);
        }
    }

    public sealed class M3uImportPreview
    {
        public M3uImportPreview(
            ParsedPlaylist playlist,
            IEnumerable<M3uImportPreviewEntry> entries)
        {
            Playlist = playlist ?? throw new ArgumentNullException(nameof(playlist));
            Entries = new ReadOnlyCollection<M3uImportPreviewEntry>(
                new List<M3uImportPreviewEntry>(entries));
        }

        public ParsedPlaylist Playlist { get; }

        public IReadOnlyList<M3uImportPreviewEntry> Entries { get; }

        public int MatchedCount => Entries.Count(
            entry => entry.Resolution.Status == ResolutionStatus.Matched);

        public int AmbiguousCount => Entries.Count(
            entry => entry.Resolution.Status == ResolutionStatus.Ambiguous);

        public int UnmatchedCount => Entries.Count(
            entry => entry.Resolution.Status == ResolutionStatus.Unmatched);

        public int DuplicateCount => Entries
            .GroupBy(entry => entry.Parsed.NormalizedPhonePath, StringComparer.OrdinalIgnoreCase)
            .Sum(group => Math.Max(0, group.Count() - 1));
    }

    public sealed class M3uImportPreviewEntry
    {
        public M3uImportPreviewEntry(
            ParsedPlaylistEntry parsed,
            ResolutionResult resolution)
        {
            Parsed = parsed ?? throw new ArgumentNullException(nameof(parsed));
            Resolution = resolution ?? throw new ArgumentNullException(nameof(resolution));
        }

        public ParsedPlaylistEntry Parsed { get; }

        public ResolutionResult Resolution { get; }
    }
}
