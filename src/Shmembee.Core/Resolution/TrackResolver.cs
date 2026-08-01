using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using Shmembee.Core.Paths;

namespace Shmembee.Core.Resolution
{
    public sealed class TrackResolver
    {
        private readonly bool requireUniqueMatches;

        public TrackResolver(bool requireUniqueMatches = true)
        {
            this.requireUniqueMatches = requireUniqueMatches;
        }

        public ResolutionResult Resolve(
            TrackReference reference,
            IEnumerable<LibraryTrack> library,
            IReadOnlyDictionary<string, string>? approvedMappings = null)
        {
            return CreateIndex(library).Resolve(reference, approvedMappings);
        }

        public TrackResolverIndex CreateIndex(IEnumerable<LibraryTrack> library)
        {
            return new TrackResolverIndex(
                library,
                requireUniqueMatches);
        }

        internal static ResolutionResult? FromCandidates(
            List<LibraryTrack> candidates,
            MatchConfidence confidence,
            string reason,
            bool requireUniqueMatches)
        {
            candidates = candidates
                .GroupBy(
                    track => TrackPathNormalizer.NormalizeWindowsPath(track.Url),
                    StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            return candidates.Count == 1 || !requireUniqueMatches
                ? ResolutionResult.Matched(candidates[0], confidence, reason)
                : ResolutionResult.Ambiguous(candidates, confidence, reason);
        }

        internal static bool DurationMatches(int? first, int? second) =>
            !first.HasValue
            || !second.HasValue
            || Math.Abs(first.Value - second.Value) <= 2;

        internal static bool OptionalNumberMatches(int? library, int? reference) =>
            !reference.HasValue
            || library == reference;

        internal static bool MetadataEquals(string? first, string? second)
        {
            if (string.IsNullOrWhiteSpace(first)
                || string.IsNullOrWhiteSpace(second))
            {
                return false;
            }

            return string.Equals(
                NormalizeMetadata(first!),
                NormalizeMetadata(second!),
                StringComparison.Ordinal);
        }

        internal static bool ExactMetadataEquals(string? first, string? second) =>
            !string.IsNullOrWhiteSpace(first)
            && !string.IsNullOrWhiteSpace(second)
            && string.Equals(
                first!.Trim(),
                second!.Trim(),
                StringComparison.OrdinalIgnoreCase);

        internal static bool OptionalMetadataMatches(
            string? libraryValue,
            string? referenceValue) =>
            string.IsNullOrWhiteSpace(referenceValue)
            || MetadataEquals(libraryValue, referenceValue);

        internal static string NormalizeMetadata(string value)
        {
            var normalized = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                if (char.IsLetterOrDigit(character))
                {
                    normalized.Append(char.ToUpperInvariant(character));
                }
            }

            return normalized.ToString();
        }
    }

    public sealed class TrackResolverIndex
    {
        private readonly bool requireUniqueMatches;
        private readonly IReadOnlyDictionary<string, List<LibraryTrack>> urls;
        private readonly IReadOnlyDictionary<string, List<LibraryTrack>> phonePaths;
        private readonly IReadOnlyDictionary<string, List<LibraryTrack>> suffixes;
        private readonly IReadOnlyDictionary<string, List<LibraryTrack>> fileNames;
        private readonly IReadOnlyDictionary<string, List<LibraryTrack>> artistTitles;
        private readonly IReadOnlyDictionary<string, List<LibraryTrack>> titles;

        internal TrackResolverIndex(
            IEnumerable<LibraryTrack> library,
            bool requireUniqueMatches)
        {
            if (library == null)
            {
                throw new ArgumentNullException(nameof(library));
            }

            this.requireUniqueMatches = requireUniqueMatches;
            urls = new Dictionary<string, List<LibraryTrack>>(StringComparer.Ordinal);
            phonePaths = new Dictionary<string, List<LibraryTrack>>(
                StringComparer.OrdinalIgnoreCase);
            suffixes = new Dictionary<string, List<LibraryTrack>>(
                StringComparer.OrdinalIgnoreCase);
            fileNames = new Dictionary<string, List<LibraryTrack>>(
                StringComparer.Ordinal);
            artistTitles = new Dictionary<string, List<LibraryTrack>>(
                StringComparer.Ordinal);
            titles = new Dictionary<string, List<LibraryTrack>>(
                StringComparer.Ordinal);

            foreach (LibraryTrack track in library)
            {
                Add(urls, TrackPathNormalizer.NormalizeWindowsPath(track.Url), track);
                Add(
                    fileNames,
                    TrackPathNormalizer.GetFileNameKey(track.Url),
                    track);
                foreach (string suffix in EnumerateSuffixes(track.Url))
                {
                    Add(suffixes, suffix, track);
                }

                foreach (string alias in track.PhoneAliases)
                {
                    Add(
                        phonePaths,
                        TrackPathNormalizer.NormalizePhonePath(alias),
                        track);
                }

                if (!string.IsNullOrWhiteSpace(track.Title))
                {
                    string title = TrackResolver.NormalizeMetadata(track.Title!);
                    Add(titles, title, track);
                    if (!string.IsNullOrWhiteSpace(track.Artist))
                    {
                        Add(
                            artistTitles,
                            TrackResolver.NormalizeMetadata(track.Artist!)
                                + "\0"
                                + title,
                            track);
                    }
                }
            }
        }

        public ResolutionResult Resolve(
            TrackReference reference,
            IReadOnlyDictionary<string, string>? approvedMappings = null,
            IEnumerable<string>? preferredUrls = null)
        {
            return Resolve(
                reference,
                approvedMappings,
                CreatePreferredUrlKeys(preferredUrls));
        }

        public ResolutionResult Resolve(
            TrackReference reference,
            IReadOnlyDictionary<string, string>? approvedMappings,
            HashSet<string>? preferredUrlKeys)
        {
            if (reference == null)
            {
                throw new ArgumentNullException(nameof(reference));
            }

            string phoneKey = TrackPathNormalizer.NormalizePhonePath(reference.Path);
            string approvedUrl;
            if (approvedMappings != null
                && approvedMappings.TryGetValue(phoneKey, out approvedUrl))
            {
                List<LibraryTrack> approved = Lookup(
                    urls,
                    TrackPathNormalizer.NormalizeWindowsPath(approvedUrl));
                if (approved.Count > 0)
                {
                    return ResolutionResult.Matched(
                        approved[0],
                        MatchConfidence.ApprovedMapping,
                        "Previously approved mapping");
                }
            }

            ResolutionResult? result = FromCandidates(
                Lookup(
                    urls,
                    TrackPathNormalizer.NormalizeWindowsPath(reference.Path)),
                MatchConfidence.ExactCanonicalUrl,
                "Exact canonical MusicBee URL");
            if (result != null)
            {
                return result;
            }

            result = FromCandidates(
                Lookup(phonePaths, phoneKey),
                MatchConfidence.ExpectedPhonePath,
                "Expected or previously observed phone path");
            if (result != null)
            {
                return result;
            }

            result = FromCandidates(
                Lookup(suffixes, phoneKey),
                MatchConfidence.UniquePathSuffix,
                "Unique normalized path suffix");
            if (result != null)
            {
                return result;
            }

            result = FromCandidates(
                Lookup(
                    fileNames,
                    TrackPathNormalizer.GetFileNameKey(reference.Path)),
                MatchConfidence.UniqueFileName,
                "Unique filename");
            if (result != null)
            {
                return result;
            }

            if (!string.IsNullOrWhiteSpace(reference.Artist)
                && !string.IsNullOrWhiteSpace(reference.Title))
            {
                List<LibraryTrack> metadataMatches = Lookup(
                        artistTitles,
                        TrackResolver.NormalizeMetadata(reference.Artist!)
                            + "\0"
                            + TrackResolver.NormalizeMetadata(reference.Title!))
                    .Where(track => TrackResolver.DurationMatches(
                        track.DurationSeconds,
                        reference.DurationSeconds))
                    .ToList();
                result = FromCandidates(
                    metadataMatches,
                    MatchConfidence.StrongMetadata,
                    "Artist, title, and compatible duration");
                if (result != null)
                {
                    return result;
                }
            }

            if (!string.IsNullOrWhiteSpace(reference.Title))
            {
                List<LibraryTrack> phoneTemplateMatches = Lookup(
                        titles,
                        TrackResolver.NormalizeMetadata(reference.Title!))
                    .Where(track =>
                        TrackResolver.OptionalMetadataMatches(
                            track.AlbumArtist,
                            reference.AlbumArtist)
                        && TrackResolver.OptionalMetadataMatches(
                            track.Album,
                            reference.Album)
                        && TrackResolver.DurationMatches(
                            track.DurationSeconds,
                            reference.DurationSeconds))
                    .ToList();
                List<LibraryTrack> exactTitleMatches = phoneTemplateMatches
                    .Where(track => TrackResolver.ExactMetadataEquals(
                        track.Title,
                        reference.Title))
                    .ToList();
                if (exactTitleMatches.Count > 0)
                {
                    phoneTemplateMatches = exactTitleMatches;
                }

                List<LibraryTrack> exactAlbumArtistMatches =
                    phoneTemplateMatches
                        .Where(track => TrackResolver.ExactMetadataEquals(
                            track.AlbumArtist,
                            reference.AlbumArtist))
                        .ToList();
                if (exactAlbumArtistMatches.Count > 0)
                {
                    phoneTemplateMatches = exactAlbumArtistMatches;
                }

                List<LibraryTrack> exactAlbumMatches = phoneTemplateMatches
                    .Where(track => TrackResolver.ExactMetadataEquals(
                        track.Album,
                        reference.Album))
                    .ToList();
                if (exactAlbumMatches.Count > 0)
                {
                    phoneTemplateMatches = exactAlbumMatches;
                }

                List<LibraryTrack> numberedMatches = phoneTemplateMatches
                    .Where(track =>
                        TrackResolver.OptionalNumberMatches(
                            track.DiscNumber,
                            reference.DiscNumber)
                        && TrackResolver.OptionalNumberMatches(
                            track.TrackNumber,
                            reference.TrackNumber))
                    .ToList();
                if (numberedMatches.Count > 0
                    && (reference.DiscNumber.HasValue
                        || reference.TrackNumber.HasValue))
                {
                    phoneTemplateMatches = numberedMatches;
                }

                if (phoneTemplateMatches.Count > 1
                    && preferredUrlKeys != null)
                {
                    List<LibraryTrack> preferredMatches =
                        phoneTemplateMatches
                            .Where(track => preferredUrlKeys.Contains(
                                TrackPathNormalizer.NormalizeWindowsPath(
                                    track.Url)))
                            .ToList();
                    if (preferredMatches.Count == 1)
                    {
                        phoneTemplateMatches = preferredMatches;
                    }
                }

                result = FromCandidates(
                    phoneTemplateMatches,
                    MatchConfidence.PhoneTemplateMetadata,
                    "Phone title, album artist, and album metadata");
                if (result != null)
                {
                    return result;
                }
            }

            return ResolutionResult.Unmatched("No confident library match");
        }

        public static HashSet<string>? CreatePreferredUrlKeys(
            IEnumerable<string>? preferredUrls) =>
            preferredUrls == null
                ? null
                : new HashSet<string>(
                    preferredUrls.Select(
                        TrackPathNormalizer.NormalizeWindowsPath),
                    StringComparer.Ordinal);

        private ResolutionResult? FromCandidates(
            List<LibraryTrack> candidates,
            MatchConfidence confidence,
            string reason) =>
            TrackResolver.FromCandidates(
                candidates,
                confidence,
                reason,
                requireUniqueMatches);

        private static List<LibraryTrack> Lookup(
            IReadOnlyDictionary<string, List<LibraryTrack>> index,
            string key)
        {
            List<LibraryTrack> matches;
            return index.TryGetValue(key, out matches)
                ? matches
                : new List<LibraryTrack>();
        }

        private static void Add(
            IReadOnlyDictionary<string, List<LibraryTrack>> index,
            string key,
            LibraryTrack track)
        {
            var mutable = (Dictionary<string, List<LibraryTrack>>)index;
            List<LibraryTrack> matches;
            if (!mutable.TryGetValue(key, out matches))
            {
                matches = new List<LibraryTrack>();
                mutable.Add(key, matches);
            }

            matches.Add(track);
        }

        private static IEnumerable<string> EnumerateSuffixes(string url)
        {
            string normalized = url.Replace('\\', '/');
            int separator = normalized.IndexOf('/');
            while (separator >= 0 && separator + 1 < normalized.Length)
            {
                yield return TrackPathNormalizer.NormalizePhonePath(
                    normalized.Substring(separator + 1));
                separator = normalized.IndexOf('/', separator + 1);
            }
        }
    }

    public sealed class TrackReference
    {
        public TrackReference(
            string path,
            string? artist = null,
            string? title = null,
            int? durationSeconds = null,
            string? albumArtist = null,
            string? album = null,
            int? discNumber = null,
            int? trackNumber = null)
        {
            Path = path;
            Artist = artist;
            Title = title;
            DurationSeconds = durationSeconds;
            AlbumArtist = albumArtist;
            Album = album;
            DiscNumber = discNumber;
            TrackNumber = trackNumber;
        }

        public string Path { get; }

        public string? Artist { get; }

        public string? Title { get; }

        public int? DurationSeconds { get; }

        public string? AlbumArtist { get; }

        public string? Album { get; }

        public int? DiscNumber { get; }

        public int? TrackNumber { get; }
    }

    public sealed class LibraryTrack
    {
        public LibraryTrack(
            string id,
            string url,
            string? artist = null,
            string? title = null,
            int? durationSeconds = null,
            IEnumerable<string>? phoneAliases = null,
            string? albumArtist = null,
            string? album = null,
            int? discNumber = null,
            int? trackNumber = null)
        {
            Id = id;
            Url = url;
            Artist = artist;
            Title = title;
            DurationSeconds = durationSeconds;
            AlbumArtist = albumArtist;
            Album = album;
            DiscNumber = discNumber;
            TrackNumber = trackNumber;
            PhoneAliases = new ReadOnlyCollection<string>(
                (phoneAliases ?? Enumerable.Empty<string>()).ToList());
        }

        public string Id { get; }

        public string Url { get; }

        public string? Artist { get; }

        public string? Title { get; }

        public int? DurationSeconds { get; }

        public string? AlbumArtist { get; }

        public string? Album { get; }

        public int? DiscNumber { get; }

        public int? TrackNumber { get; }

        public IReadOnlyList<string> PhoneAliases { get; }
    }

    public sealed class ResolutionResult
    {
        private ResolutionResult(
            ResolutionStatus status,
            LibraryTrack? match,
            IEnumerable<LibraryTrack> candidates,
            MatchConfidence? confidence,
            string reason)
        {
            Status = status;
            Match = match;
            Candidates = new ReadOnlyCollection<LibraryTrack>(candidates.ToList());
            Confidence = confidence;
            Reason = reason;
        }

        public ResolutionStatus Status { get; }

        public LibraryTrack? Match { get; }

        public IReadOnlyList<LibraryTrack> Candidates { get; }

        public MatchConfidence? Confidence { get; }

        public string Reason { get; }

        public static ResolutionResult Matched(
            LibraryTrack track,
            MatchConfidence confidence,
            string reason) =>
            new ResolutionResult(
                ResolutionStatus.Matched,
                track,
                new[] { track },
                confidence,
                reason);

        public static ResolutionResult Ambiguous(
            IEnumerable<LibraryTrack> candidates,
            MatchConfidence confidence,
            string reason) =>
            new ResolutionResult(
                ResolutionStatus.Ambiguous,
                null,
                candidates,
                confidence,
                reason);

        public static ResolutionResult Unmatched(string reason) =>
            new ResolutionResult(
                ResolutionStatus.Unmatched,
                null,
                Enumerable.Empty<LibraryTrack>(),
                null,
                reason);
    }

    public enum ResolutionStatus
    {
        Matched,
        Ambiguous,
        Unmatched
    }

    public enum MatchConfidence
    {
        ApprovedMapping = 100,
        ExactCanonicalUrl = 90,
        ExpectedPhonePath = 80,
        UniquePathSuffix = 70,
        UniqueFileName = 60,
        StrongMetadata = 50,
        PhoneTemplateMetadata = 40
    }
}
