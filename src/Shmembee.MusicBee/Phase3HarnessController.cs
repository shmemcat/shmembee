using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Shmembee.Application.Ports;
using Shmembee.Application.Synchronization;
using Shmembee.Core.Paths;
using Shmembee.Core.Playlists;
using Shmembee.Core.Reconciliation;
using Shmembee.Core.Resolution;
using Shmembee.Infrastructure.Persistence;
using Shmembee.Infrastructure.Playlists;
using Shmembee.Windows;

namespace MusicBeePlugin
{
    internal sealed class Phase3HarnessController
    {
        public const string PlaylistName = "Shmembee Phase 3 Test";
        public const string PhoneBackingName = "Shmembee Phase 3 Test.m3u";
        public static bool RealApplyEnabled => true;
        private const string PlaylistId = "phase3-disposable-test";

        private readonly MusicBeeLibraryReader libraryReader;
        private readonly MusicBeePlaylistWriter musicBeeWriter;
        private readonly TransportPhonePlaylistWriter phoneWriter;
        private readonly AcceptedBaselineStore baselineStore;
        private readonly SynchronizationHistoryStore history;

        public Phase3HarnessController(
            Plugin.MusicBeeApiInterface api,
            string storagePath)
        {
            libraryReader = new MusicBeeLibraryReader(api);
            musicBeeWriter = new MusicBeePlaylistWriter(api);
            var transport = new WpdSidecarPlaylistTransport(
                System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Plugins",
                    "Shmembee.WpdSidecar",
                    "Shmembee.WpdSidecar.exe"),
                "MLE S24U",
                "Internal storage",
                "gmmp/playlists",
                TimeSpan.FromMinutes(5));
            phoneWriter = new TransportPhonePlaylistWriter(
                transport,
                System.IO.Path.Combine(storagePath, "backups"));
            string databasePath = System.IO.Path.Combine(storagePath, "shmembee.db");
            baselineStore = new AcceptedBaselineStore(databasePath);
            history = new SynchronizationHistoryStore(databasePath);
        }

        public HarnessPreview Refresh()
        {
            MusicPlaylist playlist = libraryReader
                .ReadPlaylists()
                .SingleOrDefault(item => string.Equals(
                    item.Name,
                    PlaylistName,
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    "MusicBee playlist was not found: " + PlaylistName);
            PlaylistState musicBeeState = musicBeeWriter.Read(playlist.Url);
            PlaylistState phoneState = phoneWriter.Read(PhoneBackingName);
            if (!phoneState.Exists)
            {
                throw new InvalidOperationException(
                    "Phone playlist was not found: " + PhoneBackingName);
            }

            AcceptedBaseline? baseline = baselineStore.Load(PlaylistId);
            IReadOnlyList<LibraryTrack> library = libraryReader
                .ReadLibrary()
                .Select(track => new LibraryTrack(
                    track.Url,
                    track.Url,
                    track.Artist,
                    track.Title,
                    track.DurationSeconds))
                .ToList();
            library = library
                .Concat(musicBeeState.Entries
                    .Where(url => !library.Any(track => string.Equals(
                        track.Url,
                        url,
                        StringComparison.OrdinalIgnoreCase)))
                    .Select(url => new LibraryTrack(url, url)))
                .ToList();
            var resolver = new TrackResolver();
            IReadOnlyDictionary<string, string>? approvedMappings = baseline == null
                ? null
                : baseline.Tracks
                    .GroupBy(track => TrackPathNormalizer.NormalizePhonePath(
                        track.PhonePath))
                    .Where(group => group
                        .Select(track => track.MusicBeeUrl)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count() == 1)
                    .ToDictionary(
                        group => group.Key,
                        group => group.First().MusicBeeUrl,
                        StringComparer.OrdinalIgnoreCase);
            var resolvedPhone = new List<ResolvedHarnessTrack>();
            foreach (string phonePath in phoneState.Entries)
            {
                ResolutionResult result = resolver.Resolve(
                    CreatePhoneReference(phonePath),
                    library,
                    approvedMappings);
                if (result.Status != ResolutionStatus.Matched || result.Match == null)
                {
                    throw new InvalidOperationException(
                        "Phone track could not be resolved safely: "
                            + phonePath
                            + " ("
                            + result.Status
                            + "). MusicBee supplied "
                            + library.Count
                            + " candidate library URLs; filename candidates: "
                            + DescribeFileNameCandidates(phonePath, library));
                }

                resolvedPhone.Add(new ResolvedHarnessTrack(
                    result.Match.Id,
                    result.Match.Url,
                    phonePath));
            }

            IReadOnlyList<ResolvedHarnessTrack> musicBeeTracks =
                PairMusicBeeOccurrences(
                    musicBeeState.Entries,
                    resolvedPhone,
                    baseline);
            ReconciliationResult? reconciliation = null;
            IReadOnlyList<ResolvedHarnessTrack> proposed =
                Array.Empty<ResolvedHarnessTrack>();
            if (baseline != null)
            {
                Guid playlistId = StablePlaylistGuid();
                PlaylistSnapshot commonBase = Snapshot(
                    playlistId,
                    baseline.Tracks.Select(track => track.TrackId));
                PlaylistSnapshot musicBeeSnapshot = Snapshot(
                    playlistId,
                    musicBeeTracks.Select(track => track.TrackId));
                PlaylistSnapshot phoneSnapshot = Snapshot(
                    playlistId,
                    resolvedPhone.Select(track => track.TrackId));
                reconciliation = new ThreeWayPlaylistReconciler().Reconcile(
                    commonBase,
                    musicBeeSnapshot,
                    phoneSnapshot);
                if (!reconciliation.RequiresReview)
                {
                    proposed = SelectProposal(
                        reconciliation.Outcome,
                        musicBeeTracks,
                        resolvedPhone);
                }
            }

            return new HarnessPreview(
                playlist.Url,
                musicBeeState,
                phoneState,
                musicBeeTracks,
                resolvedPhone,
                baseline,
                reconciliation,
                proposed);
        }

        public void EstablishBaseline(HarnessPreview preview)
        {
            HarnessPreview current = Refresh();
            if (current.Baseline != null)
            {
                throw new InvalidOperationException(
                    "An accepted baseline already exists.");
            }

            if (!string.Equals(
                    current.MusicBeeState.Checksum,
                    preview.MusicBeeState.Checksum,
                    StringComparison.Ordinal)
                || !string.Equals(
                    current.PhoneState.Checksum,
                    preview.PhoneState.Checksum,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Inputs changed before baseline acceptance. Refresh and review again.");
            }

            if (!SequencesEqual(current.MusicBeeTracks, current.PhoneTracks))
            {
                throw new InvalidOperationException(
                    "The initial MusicBee and phone sequences must match exactly.");
            }

            SynchronizationPlan plan = CreatePlan(
                current,
                current.MusicBeeTracks,
                current.MusicBeeState.Checksum,
                current.PhoneState.Checksum);
            history.Started(plan);
            history.Completed(plan, current.MusicBeeState, current.PhoneState);
        }

        public SynchronizationApplyResult Apply(
            HarnessPreview preview,
            CancellationToken cancellationToken)
        {
            if (!RealApplyEnabled)
            {
                throw new InvalidOperationException(
                    "Real apply is disabled until Test-WpdPlaylistTransport.ps1 "
                        + "passes on the target device and the probe result is reviewed.");
            }

            if (preview.Reconciliation == null)
            {
                throw new InvalidOperationException(
                    "Establish the common baseline before applying.");
            }

            if (preview.Reconciliation.RequiresReview)
            {
                throw new InvalidOperationException(
                    "Concurrent conflicting changes are blocked by this harness.");
            }

            IReadOnlyList<ResolvedHarnessTrack> proposed = preview.ProposedTracks;
            if (proposed.Any(track => string.IsNullOrWhiteSpace(track.PhonePath)))
            {
                throw new InvalidOperationException(
                    "At least one proposed MusicBee track has no proven phone path.");
            }

            SynchronizationPlan plan = CreatePlan(
                preview,
                proposed,
                preview.MusicBeeState.Checksum,
                preview.PhoneState.Checksum);
            return new SynchronizationCoordinator(
                musicBeeWriter,
                phoneWriter,
                history).Apply(plan, cancellationToken);
        }

        private static SynchronizationPlan CreatePlan(
            HarnessPreview preview,
            IReadOnlyList<ResolvedHarnessTrack> tracks,
            string expectedMusicBeeChecksum,
            string expectedPhoneChecksum) =>
            new SynchronizationPlan(
                Guid.NewGuid(),
                PlaylistId,
                PlaylistName,
                preview.MusicBeePlaylistUrl,
                PhoneBackingName,
                expectedPhoneExists: true,
                expectedMusicBeeChecksum,
                expectedPhoneChecksum,
                tracks.Select(track => new SynchronizationTrack(
                    track.TrackId,
                    track.MusicBeeUrl,
                    track.PhonePath)));

        private static IReadOnlyList<ResolvedHarnessTrack> SelectProposal(
            ReconciliationOutcome outcome,
            IReadOnlyList<ResolvedHarnessTrack> musicBeeTracks,
            IReadOnlyList<ResolvedHarnessTrack> phoneTracks)
        {
            switch (outcome)
            {
                case ReconciliationOutcome.PhoneOnly:
                    return phoneTracks;
                case ReconciliationOutcome.MusicBeeOnly:
                    return musicBeeTracks;
                case ReconciliationOutcome.SameChange:
                    return phoneTracks;
                case ReconciliationOutcome.Unchanged:
                    return phoneTracks;
                default:
                    throw new InvalidOperationException(
                        "The reconciliation outcome cannot be applied.");
            }
        }

        private static IReadOnlyList<ResolvedHarnessTrack> PairMusicBeeOccurrences(
            IReadOnlyList<string> musicBeeUrls,
            IReadOnlyList<ResolvedHarnessTrack> phoneTracks,
            AcceptedBaseline? baseline)
        {
            var available = new Dictionary<string, Queue<string>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (ResolvedHarnessTrack track in phoneTracks.Concat(
                baseline?.Tracks.Select(track => new ResolvedHarnessTrack(
                    track.TrackId,
                    track.MusicBeeUrl,
                    track.PhonePath))
                ?? Enumerable.Empty<ResolvedHarnessTrack>()))
            {
                if (!available.TryGetValue(track.MusicBeeUrl, out Queue<string> paths))
                {
                    paths = new Queue<string>();
                    available.Add(track.MusicBeeUrl, paths);
                }

                paths.Enqueue(track.PhonePath);
            }

            var result = new List<ResolvedHarnessTrack>();
            foreach (string musicBeeUrl in musicBeeUrls)
            {
                string phonePath = available.TryGetValue(
                        musicBeeUrl,
                        out Queue<string> paths)
                    && paths.Count > 0
                    ? paths.Dequeue()
                    : string.Empty;
                result.Add(new ResolvedHarnessTrack(
                    musicBeeUrl,
                    musicBeeUrl,
                    phonePath));
            }

            return result;
        }

        private static bool SequencesEqual(
            IReadOnlyList<ResolvedHarnessTrack> first,
            IReadOnlyList<ResolvedHarnessTrack> second) =>
            first.Select(track => track.TrackId)
                .SequenceEqual(second.Select(track => track.TrackId));

        private static string DescribeFileNameCandidates(
            string phonePath,
            IReadOnlyList<LibraryTrack> library)
        {
            string phoneFileName = System.IO.Path.GetFileName(
                phonePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            List<string> candidates = library
                .Where(track => string.Equals(
                    System.IO.Path.GetFileName(track.Url),
                    phoneFileName,
                    StringComparison.OrdinalIgnoreCase))
                .Select(track => track.Url)
                .Take(5)
                .ToList();
            return candidates.Count == 0
                ? "(none)"
                : string.Join(" | ", candidates);
        }

        private static TrackReference CreatePhoneReference(string phonePath)
        {
            string[] segments = phonePath
                .Replace('\\', '/')
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            int musicIndex = Array.FindIndex(
                segments,
                segment => string.Equals(
                    segment,
                    "Music",
                    StringComparison.OrdinalIgnoreCase));
            string? artist = musicIndex >= 0 && musicIndex + 1 < segments.Length
                ? segments[musicIndex + 1]
                : null;
            string fileName = segments.Length == 0
                ? phonePath
                : segments[segments.Length - 1];
            string title = System.IO.Path.GetFileNameWithoutExtension(fileName);
            title = Regex.Replace(
                title,
                @"^\s*\d+\s*[-_.]\s*\d+\s*[-_.]\s*",
                string.Empty,
                RegexOptions.CultureInvariant);
            return new TrackReference(phonePath, artist, title);
        }

        private static PlaylistSnapshot Snapshot(
            Guid playlistId,
            IEnumerable<string> trackIds) =>
            new PlaylistSnapshot(
                playlistId,
                PlaylistName,
                PhoneBackingName,
                trackIds.Select(trackId => new PlaylistEntry(
                    Guid.NewGuid(),
                    new TrackIdentity(trackId),
                    trackId)),
                DateTimeOffset.UtcNow);

        private static Guid StablePlaylistGuid() =>
            new Guid("76abdf2b-6a74-4fac-b7eb-7e8839191f7f");
    }

    internal sealed class HarnessPreview
    {
        public HarnessPreview(
            string musicBeePlaylistUrl,
            PlaylistState musicBeeState,
            PlaylistState phoneState,
            IReadOnlyList<ResolvedHarnessTrack> musicBeeTracks,
            IReadOnlyList<ResolvedHarnessTrack> phoneTracks,
            AcceptedBaseline? baseline,
            ReconciliationResult? reconciliation,
            IReadOnlyList<ResolvedHarnessTrack> proposedTracks)
        {
            MusicBeePlaylistUrl = musicBeePlaylistUrl;
            MusicBeeState = musicBeeState;
            PhoneState = phoneState;
            MusicBeeTracks = musicBeeTracks;
            PhoneTracks = phoneTracks;
            Baseline = baseline;
            Reconciliation = reconciliation;
            ProposedTracks = proposedTracks;
        }

        public string MusicBeePlaylistUrl { get; }

        public PlaylistState MusicBeeState { get; }

        public PlaylistState PhoneState { get; }

        public IReadOnlyList<ResolvedHarnessTrack> MusicBeeTracks { get; }

        public IReadOnlyList<ResolvedHarnessTrack> PhoneTracks { get; }

        public AcceptedBaseline? Baseline { get; }

        public ReconciliationResult? Reconciliation { get; }

        public IReadOnlyList<ResolvedHarnessTrack> ProposedTracks { get; }
    }

    internal sealed class ResolvedHarnessTrack
    {
        public ResolvedHarnessTrack(
            string trackId,
            string musicBeeUrl,
            string phonePath)
        {
            TrackId = trackId;
            MusicBeeUrl = musicBeeUrl;
            PhonePath = phonePath;
        }

        public string TrackId { get; }

        public string MusicBeeUrl { get; }

        public string PhonePath { get; }
    }
}
