using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
            if (reference == null)
            {
                throw new ArgumentNullException(nameof(reference));
            }

            var tracks = library?.ToList()
                ?? throw new ArgumentNullException(nameof(library));
            string phoneKey = TrackPathNormalizer.NormalizePhonePath(reference.Path);

            string approvedUrl;
            if (approvedMappings != null
                && approvedMappings.TryGetValue(phoneKey, out approvedUrl))
            {
                LibraryTrack? approved = tracks.FirstOrDefault(
                    track => WindowsPathEquals(track.Url, approvedUrl));
                if (approved != null)
                {
                    return ResolutionResult.Matched(
                        approved,
                        MatchConfidence.ApprovedMapping,
                        "Previously approved mapping");
                }
            }

            List<LibraryTrack> exact = tracks
                .Where(track => WindowsPathEquals(track.Url, reference.Path))
                .ToList();
            ResolutionResult? exactResult = FromCandidates(
                exact,
                MatchConfidence.ExactCanonicalUrl,
                "Exact canonical MusicBee URL");
            if (exactResult != null)
            {
                return exactResult;
            }

            List<LibraryTrack> expectedPhonePaths = tracks
                .Where(track => track.PhoneAliases.Any(
                    alias => string.Equals(
                        TrackPathNormalizer.NormalizePhonePath(alias),
                        phoneKey,
                        StringComparison.OrdinalIgnoreCase)))
                .ToList();
            ResolutionResult? phoneResult = FromCandidates(
                expectedPhonePaths,
                MatchConfidence.ExpectedPhonePath,
                "Expected or previously observed phone path");
            if (phoneResult != null)
            {
                return phoneResult;
            }

            List<LibraryTrack> uniqueSuffix = tracks
                .Where(track =>
                {
                    string desktop = track.Url.Replace('\\', '/');
                    return desktop.EndsWith(
                        "/" + phoneKey,
                        StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
            ResolutionResult? suffixResult = FromCandidates(
                uniqueSuffix,
                MatchConfidence.UniquePathSuffix,
                "Unique normalized path suffix");
            if (suffixResult != null)
            {
                return suffixResult;
            }

            string fileNameKey = TrackPathNormalizer.GetFileNameKey(reference.Path);
            List<LibraryTrack> fileNameMatches = tracks
                .Where(track => string.Equals(
                    TrackPathNormalizer.GetFileNameKey(track.Url),
                    fileNameKey,
                    StringComparison.Ordinal))
                .ToList();
            ResolutionResult? fileNameResult = FromCandidates(
                fileNameMatches,
                MatchConfidence.UniqueFileName,
                "Unique filename");
            if (fileNameResult != null)
            {
                return fileNameResult;
            }

            if (!string.IsNullOrWhiteSpace(reference.Artist)
                && !string.IsNullOrWhiteSpace(reference.Title))
            {
                List<LibraryTrack> metadataMatches = tracks
                    .Where(track =>
                        string.Equals(
                            track.Artist,
                            reference.Artist,
                            StringComparison.OrdinalIgnoreCase)
                        && string.Equals(
                            track.Title,
                            reference.Title,
                            StringComparison.OrdinalIgnoreCase)
                        && DurationMatches(track.DurationSeconds, reference.DurationSeconds))
                    .ToList();
                ResolutionResult? metadataResult = FromCandidates(
                    metadataMatches,
                    MatchConfidence.StrongMetadata,
                    "Artist, title, and compatible duration");
                if (metadataResult != null)
                {
                    return metadataResult;
                }
            }

            return ResolutionResult.Unmatched("No confident library match");
        }

        private ResolutionResult? FromCandidates(
            List<LibraryTrack> candidates,
            MatchConfidence confidence,
            string reason)
        {
            if (candidates.Count == 0)
            {
                return null;
            }

            return candidates.Count == 1 || !requireUniqueMatches
                ? ResolutionResult.Matched(candidates[0], confidence, reason)
                : ResolutionResult.Ambiguous(candidates, confidence, reason);
        }

        private static bool WindowsPathEquals(string first, string second) =>
            string.Equals(
                TrackPathNormalizer.NormalizeWindowsPath(first),
                TrackPathNormalizer.NormalizeWindowsPath(second),
                StringComparison.Ordinal);

        private static bool DurationMatches(int? first, int? second) =>
            !first.HasValue
            || !second.HasValue
            || Math.Abs(first.Value - second.Value) <= 2;
    }

    public sealed class TrackReference
    {
        public TrackReference(
            string path,
            string? artist = null,
            string? title = null,
            int? durationSeconds = null)
        {
            Path = path;
            Artist = artist;
            Title = title;
            DurationSeconds = durationSeconds;
        }

        public string Path { get; }

        public string? Artist { get; }

        public string? Title { get; }

        public int? DurationSeconds { get; }
    }

    public sealed class LibraryTrack
    {
        public LibraryTrack(
            string id,
            string url,
            string? artist = null,
            string? title = null,
            int? durationSeconds = null,
            IEnumerable<string>? phoneAliases = null)
        {
            Id = id;
            Url = url;
            Artist = artist;
            Title = title;
            DurationSeconds = durationSeconds;
            PhoneAliases = new ReadOnlyCollection<string>(
                (phoneAliases ?? Enumerable.Empty<string>()).ToList());
        }

        public string Id { get; }

        public string Url { get; }

        public string? Artist { get; }

        public string? Title { get; }

        public int? DurationSeconds { get; }

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
        StrongMetadata = 50
    }
}
