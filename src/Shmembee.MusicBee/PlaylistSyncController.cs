using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Shmembee.Application.Desktop;
using Shmembee.Application.Ports;
using Shmembee.Application.Reconciliation;
using Shmembee.Application.Synchronization;
using Shmembee.Core.Paths;
using Shmembee.Core.Playlists;
using Shmembee.Core.Reconciliation;
using Shmembee.Core.Resolution;
using Shmembee.Infrastructure.Persistence;
using Shmembee.Infrastructure.Playlists;
using Shmembee.Infrastructure.Settings;
using Shmembee.Windows;

namespace MusicBeePlugin
{
    internal enum HarnessPlaylistVisualState
    {
        Unchanged,
        Changed,
        OneSided,
        OrderOnly,
        Attention
    }

    internal sealed class HarnessPlaylistRow
    {
        public HarnessPlaylistRow(
            string rowId,
            string displayName,
            string? musicBeeName,
            string? phoneName,
            string musicBeeChecksum,
            string phoneChecksum,
            string statusText,
            HarnessPlaylistVisualState visualState,
            PlaylistDiff? diff,
            PlaylistPairContext? pairContext = null,
            string? musicBeePlaylistId = null,
            string? phoneBackingName = null)
        {
            RowId = rowId;
            DisplayName = displayName;
            MusicBeeName = musicBeeName;
            PhoneName = phoneName;
            MusicBeeChecksum = musicBeeChecksum;
            PhoneChecksum = phoneChecksum;
            StatusText = statusText;
            VisualState = visualState;
            Diff = diff;
            PairContext = pairContext;
            MusicBeePlaylistId = musicBeePlaylistId;
            PhoneBackingName = phoneBackingName;
        }

        public string RowId { get; }
        public string DisplayName { get; }
        public string? MusicBeeName { get; }
        public string? PhoneName { get; }
        public string MusicBeeChecksum { get; }
        public string PhoneChecksum { get; }
        public string StatusText { get; }
        public HarnessPlaylistVisualState VisualState { get; }
        public PlaylistDiff? Diff { get; }
        public PlaylistPairContext? PairContext { get; }
        public string? MusicBeePlaylistId { get; }
        public string? PhoneBackingName { get; }
        public bool IsPaired => PairContext != null;
    }

    internal sealed class HarnessBatchApplyResult
    {
        public HarnessBatchApplyResult(
            int succeededCount,
            int failedCount,
            string summary,
            IEnumerable<string> succeededRowIds,
            bool wasCancelled = false,
            int rolledBackCount = 0,
            int notStartedCount = 0)
        {
            SucceededCount = succeededCount;
            FailedCount = failedCount;
            Summary = summary;
            SucceededRowIds = succeededRowIds.ToList();
            WasCancelled = wasCancelled;
            RolledBackCount = rolledBackCount;
            NotStartedCount = notStartedCount;
        }

        public int SucceededCount { get; }
        public int FailedCount { get; }
        public string Summary { get; }
        public IReadOnlyList<string> SucceededRowIds { get; }
        public bool WasCancelled { get; }
        public int RolledBackCount { get; }
        public int NotStartedCount { get; }
    }

    internal sealed class HarnessOperationProgress
    {
        public HarnessOperationProgress(int percentage, string status)
        {
            Percentage = percentage;
            Status = status;
        }

        public int Percentage { get; }
        public string Status { get; }
    }

    internal sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> report;

        public InlineProgress(Action<T> report)
        {
            this.report = report ?? throw new ArgumentNullException(nameof(report));
        }

        public void Report(T value) => report(value);
    }

    internal sealed class PlaylistSyncController
    {
        public const string PlaylistName = "Shmembee Phase 3 Test";
        public const string PhoneBackingName = "Shmembee Phase 3 Test.m3u";
        public static bool RealApplyEnabled => true;
        private const string LegacyPlaylistId = "phase3-disposable-test";

        private readonly MusicBeeLibraryReader libraryReader;
        private readonly MusicBeePlaylistWriter directMusicBeeWriter;
        private readonly IMusicBeePlaylistWriter musicBeeWriter;
        private readonly TransportPhonePlaylistWriter phoneWriter;
        private readonly IPhonePlaylistCatalogReader phoneCatalogReader;
        private readonly IPhonePlaylistSnapshotReader? phoneSnapshotReader;
        private readonly IPhoneMediaPathReader? phoneMediaPathReader;
        private readonly IPhonePlaylistBackupTransport phoneBackupTransport;
        private readonly PostSyncPlaylistBackup? postSyncBackup;
        private readonly AcceptedBaselineStore baselineStore;
        private readonly SynchronizationHistoryStore history;
        private readonly string playlistId;
        private readonly string playlistName;
        private readonly string phoneBackingName;
        private readonly DesktopSettings settings;
        private Control? uiDispatcher;

        public PlaylistSyncController(
            Plugin.MusicBeeApiInterface api,
            string storagePath)
        {
            libraryReader = new MusicBeeLibraryReader(api);
            directMusicBeeWriter = new MusicBeePlaylistWriter(api);
            musicBeeWriter = new DispatchedMusicBeePlaylistWriter(
                directMusicBeeWriter,
                InvokeOnMusicBeeThread,
                InvokeOnMusicBeeThread);
            var settingsStore = new DesktopSettingsStore(
                System.IO.Path.Combine(storagePath, "settings.json"));
            settings = settingsStore.Load();
            PlaylistAssociation? association = settings.PlaylistAssociations.FirstOrDefault();
            playlistId = association?.PlaylistId ?? LegacyPlaylistId;
            playlistName = association?.MusicBeePlaylistName ?? PlaylistName;
            phoneBackingName = association?.PhoneBackingName ?? PhoneBackingName;
            var transport = new WpdSidecarPlaylistTransport(
                System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Plugins",
                    "Shmembee.WpdSidecar",
                    "Shmembee.WpdSidecar.exe"),
                settings.DeviceName,
                settings.StorageName,
                settings.PlaylistFolder,
                TimeSpan.FromSeconds(settings.TimeoutSeconds),
                mediaFolderPath: settings.PhoneMediaFolder,
                diagnosticsPath: System.IO.Path.Combine(
                    storagePath,
                    "diagnostics"));
            phoneCatalogReader = transport;
            phoneSnapshotReader = transport;
            phoneMediaPathReader = transport;
            phoneBackupTransport = transport;
            postSyncBackup = string.IsNullOrWhiteSpace(settings.PostSyncBackupPath)
                ? null
                : new PostSyncPlaylistBackup(
                    transport,
                    settings.PostSyncBackupPath);
            phoneWriter = new TransportPhonePlaylistWriter(
                transport,
                string.IsNullOrWhiteSpace(settings.BackupPath)
                    ? System.IO.Path.Combine(storagePath, "backups")
                    : settings.BackupPath);
            string databasePath = string.IsNullOrWhiteSpace(settings.DatabasePath)
                ? System.IO.Path.Combine(storagePath, "shmembee.db")
                : settings.DatabasePath;
            baselineStore = new AcceptedBaselineStore(databasePath);
            history = new SynchronizationHistoryStore(databasePath);
        }

        public string ConfiguredPlaylistName => playlistName;

        public string ConfiguredPhoneBackingName => phoneBackingName;

        public DesktopSettings Settings => settings;

        public IReadOnlyList<SynchronizationHistoryListItem> ReadHistory(int limit = 100) =>
            history.List(limit);

        public IReadOnlyList<PlaylistCatalogViewRow> ReadPlaylistCatalog()
        {
            IReadOnlyList<MusicPlaylist> playlists = InvokeOnMusicBeeThread(
                () => libraryReader.ReadPlaylists());
            var service = new PlaylistCatalogService(
                new SnapshotMusicLibraryReader(playlists),
                phoneCatalogReader);
            IReadOnlyList<PlaylistPairingCorrection> corrections = settings
                .PlaylistAssociations
                .Where(item => item.IsExplicitCorrection)
                .Select(item => item.ToPairingCorrection())
                .ToList();
            return service.Build(corrections).Rows
                .Select(BuildCatalogViewRow)
                .ToList();
        }

        public PlaylistDetailDiff LoadPlaylistDetail(PlaylistPairContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            MusicPlaylist playlist = InvokeOnMusicBeeThread(
                    () => libraryReader.ReadPlaylists())
                .SingleOrDefault(item => string.Equals(
                    item.Url,
                    context.MusicBeePlaylistId,
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    "MusicBee playlist was not found: " + context.MusicBeePlaylistId);
            PlaylistState musicBeeState = musicBeeWriter.Read(playlist.Url);
            PlaylistState phoneState = phoneWriter.Read(context.PhoneBackingName);
            if (!phoneState.Exists)
            {
                throw new InvalidOperationException(
                    "Phone playlist was not found: " + context.PhoneBackingName);
            }

            IReadOnlyList<LibraryTrack> library = ReadResolutionLibrary(musicBeeState);
            AcceptedBaseline? baseline = baselineStore.Load(context.PairId);
            IReadOnlyDictionary<string, string> mediaPaths =
                ResolvePhoneMediaPaths(
                    library,
                    musicBeeState.Entries,
                    out HashSet<string> observedMediaPaths);
            IReadOnlyList<ResolvedHarnessTrack> phoneTracks = ResolvePhoneTracks(
                phoneState,
                new TrackResolver().CreateIndex(library),
                baseline,
                musicBeeState.Entries,
                settings.PhoneMediaFolder,
                mediaPaths,
                observedMediaPaths);
            IReadOnlyList<ResolvedHarnessTrack> musicBeeTracks = PairMusicBeeOccurrences(
                musicBeeState.Entries,
                phoneTracks,
                baseline,
                mediaPaths);
            PlaylistDiff diff = PlaylistDiffEngine.Compare(
                musicBeeTracks.Select(ToMusicBeeDiffEntry),
                phoneTracks.Select(ToPhoneDiffEntry));
            return new PlaylistDetailDiff(
                context,
                playlist.Name,
                musicBeeState,
                phoneState,
                diff);
        }

        public ReviewedPlanResult FinalizeReviewedResult(
            PlaylistDetailDiff detail,
            ReviewedPlaylistDraft draft)
        {
            if (detail == null)
            {
                throw new ArgumentNullException(nameof(detail));
            }

            PlaylistState currentMusicBee = musicBeeWriter.Read(
                detail.Context.MusicBeePlaylistId);
            PlaylistState currentPhone = phoneWriter.Read(
                detail.Context.PhoneBackingName);
            return ReviewedPlaylistPlanningService.Finalize(
                detail.Diff,
                draft,
                detail.Context.Pair,
                currentMusicBee.Checksum,
                currentPhone.Checksum);
        }

        public SynchronizationApplyResult ApplyReviewedResult(
            PlaylistDetailDiff detail,
            ReviewedPlaylistDraft draft,
            CancellationToken cancellationToken) =>
            ApplyReviewedResult(
                detail,
                draft,
                cancellationToken,
                true);

        private SynchronizationApplyResult ApplyReviewedResult(
            PlaylistDetailDiff detail,
            ReviewedPlaylistDraft draft,
            CancellationToken cancellationToken,
            bool managePhoneBackup)
        {
            ReviewedPlanResult finalized = FinalizeReviewedResult(detail, draft);
            if (!finalized.IsReady || finalized.Plan == null)
            {
                return SynchronizationApplyResult.Failed(
                    finalized.Freshness == ReviewedDraftFreshness.StaleChecksums
                        ? "Inputs changed before apply. Refresh and review again."
                        : string.Join(" ", finalized.BlockedReasons));
            }

            if (finalized.Plan.MusicBeeEntries.Count == 0
                && finalized.Plan.PhoneEntries.Count == 0)
            {
                return SynchronizationApplyResult.Failed(
                    "An empty reviewed result is not a delete operation.");
            }

            if (finalized.Plan.PhoneEntries.Any(string.IsNullOrWhiteSpace))
            {
                return SynchronizationApplyResult.Failed(
                    "At least one selected track has no proven phone path.");
            }

            IReadOnlyList<LibraryTrack> currentLibrary = AddMissingPlaylistTracks(
                ToResolutionLibrary(InvokeOnMusicBeeThread(
                    () => libraryReader.ReadLibrary())),
                finalized.Plan.MusicBeeEntries);
            IReadOnlyDictionary<string, string> currentMediaPaths =
                ResolvePhoneMediaPaths(
                    new TrackResolver().CreateIndex(currentLibrary),
                    finalized.Plan.MusicBeeEntries,
                    cancellationToken,
                    progress: null,
                    out HashSet<string> observedMediaPaths);
            AcceptedBaseline? acceptedBaseline = baselineStore.Load(
                detail.Context.PairId);
            for (int index = 0; index < finalized.Plan.MusicBeeEntries.Count; index++)
            {
                string musicBeeUrl = finalized.Plan.MusicBeeEntries[index];
                string selectedPhonePath = TrackPathNormalizer.NormalizePhonePath(
                    finalized.Plan.PhoneEntries[index]);
                bool resolverProvedPath = currentMediaPaths.TryGetValue(
                        musicBeeUrl,
                        out string currentPhonePath)
                    && string.Equals(
                        selectedPhonePath,
                        TrackPathNormalizer.NormalizePhonePath(currentPhonePath),
                        StringComparison.OrdinalIgnoreCase);
                bool resolverFoundDifferentPath =
                    currentMediaPaths.ContainsKey(musicBeeUrl)
                    && !resolverProvedPath;
                bool baselineProvedIdentity = acceptedBaseline != null
                    && BaselineUniquelyMaps(
                        acceptedBaseline,
                        selectedPhonePath,
                        musicBeeUrl);
                if (!resolverProvedPath
                    && !(!resolverFoundDifferentPath
                        && baselineProvedIdentity
                        && observedMediaPaths.Contains(selectedPhonePath)))
                {
                    return SynchronizationApplyResult.Failed(
                        "Phone media changed after review for "
                            + musicBeeUrl
                            + ". Refresh before applying; no playlist was written.");
                }
            }

            SynchronizationPlan plan = new SynchronizationPlan(
                Guid.NewGuid(),
                detail.Context.PairId,
                detail.PlaylistName,
                detail.Context.MusicBeePlaylistId,
                detail.Context.PhoneBackingName,
                expectedPhoneExists: true,
                finalized.Plan.ExpectedMusicBeeChecksum,
                finalized.Plan.ExpectedPhoneChecksum,
                finalized.Plan.MusicBeeEntries.Zip(
                    finalized.Plan.PhoneEntries,
                    (musicBeeUrl, phonePath) => new SynchronizationTrack(
                        musicBeeUrl,
                        musicBeeUrl,
                        phonePath)));
            return managePhoneBackup
                ? ApplyPhoneChangingPlan(plan, cancellationToken)
                : Coordinator().Apply(plan, cancellationToken);
        }

        public IReadOnlyList<PlaylistApplyAllResult> ApplyAll(
            IEnumerable<ReviewedPlaylistApplyRequest> requests,
            CancellationToken cancellationToken)
        {
            List<ReviewedPlaylistApplyRequest> selected = (requests
                ?? throw new ArgumentNullException(nameof(requests)))
                .Where(request => request.IsChecked)
                .ToList();
            var results = new List<PlaylistApplyAllResult>();
            var finalized = new List<Tuple<
                ReviewedPlaylistApplyRequest,
                ReviewedPlanResult>>();
            foreach (ReviewedPlaylistApplyRequest request in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReviewedPlanResult planResult;
                try
                {
                    planResult = FinalizeReviewedResult(request.Detail, request.Draft);
                }
                catch (Exception exception)
                {
                    results.Add(new PlaylistApplyAllResult(
                        request.Detail.Context,
                        SynchronizationApplyResult.Failed(exception.Message)));
                    continue;
                }

                if (!planResult.IsReady || planResult.Plan == null)
                {
                    results.Add(new PlaylistApplyAllResult(
                        request.Detail.Context,
                        SynchronizationApplyResult.Failed(
                            planResult.Freshness
                                == ReviewedDraftFreshness.StaleChecksums
                                ? "Inputs changed before apply. Refresh and review again."
                                : string.Join(" ", planResult.BlockedReasons))));
                    continue;
                }

                finalized.Add(Tuple.Create(request, planResult));
            }

            if (results.Count > 0)
            {
                const string preflightFailure =
                    "Batch preflight failed, so no backup or sync writes were made.";
                return selected.Select(request =>
                {
                    PlaylistApplyAllResult? existing = results.FirstOrDefault(item =>
                        string.Equals(
                            item.Context.PairId,
                            request.Detail.Context.PairId,
                            StringComparison.Ordinal));
                    return existing ?? new PlaylistApplyAllResult(
                        request.Detail.Context,
                        SynchronizationApplyResult.Failed(preflightFailure));
                }).ToList();
            }

            PhonePlaylistBackupResult backup;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                backup = phoneBackupTransport.CreatePlaylistBackup();
            }
            catch (OperationCanceledException)
            {
                return selected.Select(request => new PlaylistApplyAllResult(
                    request.Detail.Context,
                    SynchronizationApplyResult.Cancelled(
                        "Cancelled before the phone backup was created; no sync writes "
                            + "were made."))).ToList();
            }
            catch (Exception exception)
            {
                return selected.Select(request => new PlaylistApplyAllResult(
                    request.Detail.Context,
                    SynchronizationApplyResult.Failed(
                        "The all-playlist phone backup could not be created, so no sync "
                            + "writes were made: " + exception.Message))).ToList();
            }

            string location = DescribePhoneBackupLocation(backup.Handle);
            bool completeSuccess = true;
            foreach (Tuple<ReviewedPlaylistApplyRequest, ReviewedPlanResult> item in finalized)
            {
                SynchronizationApplyResult result;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result = ApplyReviewedResult(
                        item.Item1.Detail,
                        item.Item1.Draft,
                        cancellationToken,
                        managePhoneBackup: false);
                }
                catch (OperationCanceledException)
                {
                    result = SynchronizationApplyResult.Cancelled(
                        "The batch was cancelled. Phone backup retained at "
                            + location + ".");
                }
                catch (Exception exception)
                {
                    result = SynchronizationApplyResult.Failed(
                        "The sync failed unexpectedly: " + exception.Message
                            + " Phone backup retained at " + location + ".");
                }

                completeSuccess &= result.Status == SynchronizationApplyStatus.Succeeded;
                results.Add(new PlaylistApplyAllResult(
                    item.Item1.Detail.Context,
                    result));
                if (result.Status == SynchronizationApplyStatus.Cancelled)
                {
                    break;
                }
            }

            string postSyncDetails = string.Empty;
            if (completeSuccess)
            {
                completeSuccess = TryCreatePostSyncBackup(
                    out postSyncDetails,
                    out string postSyncError);
                if (!completeSuccess)
                {
                    postSyncDetails = " " + postSyncError;
                }
            }

            if (completeSuccess)
            {
                try
                {
                    phoneBackupTransport.DeletePlaylistBackup(backup.Handle);
                }
                catch (Exception exception)
                {
                    completeSuccess = false;
                    postSyncDetails += " Temporary phone backup cleanup failed: "
                        + exception.Message + ".";
                }
            }

            if (!completeSuccess)
            {
                string retained = postSyncDetails
                    + " Phone backup retained at " + location + ".";
                results = results.Select(item => new PlaylistApplyAllResult(
                    item.Context,
                    item.Result.Status == SynchronizationApplyStatus.Succeeded
                        ? SynchronizationApplyResult.Failed(
                            item.Result.Details + retained)
                        : AppendRetainedBackup(item.Result, location)))
                    .ToList();
            }

            return results;
        }

        public SynchronizationLifecycleResult CreatePhonePlaylist(
            string backingName,
            string expectedMissingChecksum,
            IReadOnlyList<string> provenPhonePaths,
            CancellationToken cancellationToken) =>
            CreatePhonePlaylist(
                backingName,
                expectedMissingChecksum,
                provenPhonePaths,
                cancellationToken,
                true);

        private SynchronizationLifecycleResult CreatePhonePlaylist(
            string backingName,
            string expectedMissingChecksum,
            IReadOnlyList<string> provenPhonePaths,
            CancellationToken cancellationToken,
            bool managePhoneBackup)
        {
            if (provenPhonePaths.Any(string.IsNullOrWhiteSpace))
            {
                return SynchronizationLifecycleResult.Failed(
                    "Phone creation requires a proven path for every track.");
            }

            Func<SynchronizationLifecycleResult> operation = () =>
                Coordinator().CreatePhone(
                backingName,
                expectedMissingChecksum,
                provenPhonePaths,
                cancellationToken);
            return managePhoneBackup
                ? ExecutePhoneChangingLifecycle(cancellationToken, operation)
                : operation();
        }

        public SynchronizationLifecycleResult CreateMusicBeePlaylist(
            string name,
            IReadOnlyList<string> musicBeeUrls,
            CancellationToken cancellationToken) =>
            Coordinator().CreateMusicBee(name, musicBeeUrls, cancellationToken);

        public SynchronizationLifecycleResult DeletePhonePlaylist(
            string backingName,
            string expectedChecksum,
            CancellationToken cancellationToken) =>
            DeletePhonePlaylist(
                backingName,
                expectedChecksum,
                cancellationToken,
                true);

        private SynchronizationLifecycleResult DeletePhonePlaylist(
            string backingName,
            string expectedChecksum,
            CancellationToken cancellationToken,
            bool managePhoneBackup)
        {
            Func<SynchronizationLifecycleResult> operation = () =>
                Coordinator().DeletePhone(
                    backingName,
                    expectedChecksum,
                    cancellationToken);
            return managePhoneBackup
                ? ExecutePhoneChangingLifecycle(cancellationToken, operation)
                : operation();
        }

        public SynchronizationLifecycleResult DeleteMusicBeePlaylist(
            string playlistUrl,
            string playlistName,
            string expectedChecksum,
            CancellationToken cancellationToken) =>
            Coordinator().DeleteMusicBee(
                playlistUrl,
                playlistName,
                expectedChecksum,
                cancellationToken);

        private SynchronizationCoordinator Coordinator() =>
            new SynchronizationCoordinator(musicBeeWriter, phoneWriter, history);

        private SynchronizationApplyResult ApplyPhoneChangingPlan(
            SynchronizationPlan plan,
            CancellationToken cancellationToken)
        {
            PhonePlaylistBackupResult backup;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                backup = phoneBackupTransport.CreatePlaylistBackup();
            }
            catch (OperationCanceledException)
            {
                return SynchronizationApplyResult.Cancelled(
                    "Cancelled before the phone backup was created; no sync writes were made.");
            }
            catch (Exception exception)
            {
                return SynchronizationApplyResult.Failed(
                    "The all-playlist phone backup could not be created, so no sync writes "
                        + "were made: " + exception.Message);
            }

            string location = DescribePhoneBackupLocation(backup.Handle);
            SynchronizationApplyResult result;
            try
            {
                result = Coordinator().Apply(plan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return SynchronizationApplyResult.Cancelled(
                    "The operation was cancelled. Phone backup retained at " + location + ".");
            }
            catch (Exception exception)
            {
                return SynchronizationApplyResult.Failed(
                    "The sync failed unexpectedly: " + exception.Message
                        + " Phone backup retained at " + location + ".");
            }

            if (result.Status != SynchronizationApplyStatus.Succeeded)
            {
                return AppendRetainedBackup(result, location);
            }

            string postSyncDetails;
            if (!TryCreatePostSyncBackup(out postSyncDetails, out string postSyncError))
            {
                return SynchronizationApplyResult.Failed(
                    result.Details + " " + postSyncError
                        + " Phone backup retained at " + location + ".");
            }

            try
            {
                phoneBackupTransport.DeletePlaylistBackup(backup.Handle);
                return SynchronizationApplyResult.Succeeded(
                    result.MusicBeeState!,
                    result.PhoneState!,
                    result.Details + postSyncDetails
                        + " Temporary phone backup deleted after complete success.");
            }
            catch (Exception exception)
            {
                return SynchronizationApplyResult.Failed(
                    result.Details + postSyncDetails
                        + " The sync writes succeeded, but the temporary phone backup could "
                        + "not be deleted: " + exception.Message
                        + " Backup retained at " + location + ".");
            }
        }

        private SynchronizationLifecycleResult ExecutePhoneChangingLifecycle(
            CancellationToken cancellationToken,
            Func<SynchronizationLifecycleResult> operation)
        {
            PhonePlaylistBackupResult backup;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                backup = phoneBackupTransport.CreatePlaylistBackup();
            }
            catch (OperationCanceledException)
            {
                return SynchronizationLifecycleResult.Cancelled(
                    "Cancelled before the phone backup was created; no sync writes were made.");
            }
            catch (Exception exception)
            {
                return SynchronizationLifecycleResult.Failed(
                    "The all-playlist phone backup could not be created, so no sync writes "
                        + "were made: " + exception.Message);
            }

            string location = DescribePhoneBackupLocation(backup.Handle);
            SynchronizationLifecycleResult result;
            try
            {
                result = operation();
            }
            catch (OperationCanceledException)
            {
                return SynchronizationLifecycleResult.Cancelled(
                    "The operation was cancelled. Phone backup retained at " + location + ".");
            }
            catch (Exception exception)
            {
                return SynchronizationLifecycleResult.Failed(
                    "The operation failed unexpectedly: " + exception.Message
                        + " Phone backup retained at " + location + ".");
            }

            if (result.Status != SynchronizationApplyStatus.Succeeded)
            {
                string retained = result.Details
                    + " Phone backup retained at " + location + ".";
                return result.Status == SynchronizationApplyStatus.Cancelled
                    ? SynchronizationLifecycleResult.Cancelled(retained)
                    : result.Status == SynchronizationApplyStatus.Stale
                        ? SynchronizationLifecycleResult.Stale(retained)
                        : SynchronizationLifecycleResult.Failed(retained);
            }

            string postSyncDetails;
            if (!TryCreatePostSyncBackup(out postSyncDetails, out string postSyncError))
            {
                return SynchronizationLifecycleResult.Failed(
                    result.Details + " " + postSyncError
                        + " Phone backup retained at " + location + ".");
            }

            try
            {
                phoneBackupTransport.DeletePlaylistBackup(backup.Handle);
                return SynchronizationLifecycleResult.Succeeded(
                    result.Details + postSyncDetails
                        + " Temporary phone backup deleted after complete success.",
                    result.MusicBeeState,
                    result.PhoneState,
                    result.CreatedMusicBeePlaylistUrl);
            }
            catch (Exception exception)
            {
                return SynchronizationLifecycleResult.Failed(
                    result.Details + postSyncDetails
                        + " The phone change succeeded, but the temporary phone backup could "
                        + "not be deleted: " + exception.Message
                        + " Backup retained at " + location + ".");
            }
        }

        private bool TryCreatePostSyncBackup(
            out string details,
            out string error)
        {
            details = string.Empty;
            error = string.Empty;
            if (postSyncBackup == null)
            {
                return true;
            }

            try
            {
                details = " Post-sync M3U backup: " + postSyncBackup.Create() + ".";
                return true;
            }
            catch (Exception exception)
            {
                error = "The configured post-sync M3U backup failed: "
                    + exception.Message + ".";
                return false;
            }
        }

        private string DescribePhoneBackupLocation(PhonePlaylistBackupHandle handle) =>
            settings.PlaylistFolder.TrimEnd('\\', '/')
                + "\\backup\\" + handle.BackupFolderName;

        private static SynchronizationApplyResult AppendRetainedBackup(
            SynchronizationApplyResult result,
            string location)
        {
            string details = result.Details + " Phone backup retained at " + location + ".";
            switch (result.Status)
            {
                case SynchronizationApplyStatus.Cancelled:
                    return SynchronizationApplyResult.Cancelled(details);
                case SynchronizationApplyStatus.Stale:
                    return SynchronizationApplyResult.Failed(details);
                case SynchronizationApplyStatus.CommitPending:
                    return SynchronizationApplyResult.CommitPending(
                        details,
                        result.MusicBeeState!,
                        result.PhoneState!);
                default:
                    return SynchronizationApplyResult.Failed(details);
            }
        }

        private PlaylistCatalogViewRow BuildCatalogViewRow(PlaylistCatalogRow row)
        {
            PlaylistDifferenceKind? difference = null;
            string? detailError = row.Error;
            if (row.IsActionable
                && row.MusicBeePlaylist != null
                && row.PhonePlaylist != null)
            {
                try
                {
                    difference = LoadPlaylistDetail(new PlaylistPairContext(
                        row.RowId,
                        row.MusicBeePlaylist.Url,
                        row.PhonePlaylist.Id,
                        row.PhonePlaylist.BackingName)).Diff.Kind;
                }
                catch (Exception exception)
                {
                    detailError = exception.Message;
                }
            }

            return new PlaylistCatalogViewRow(row, difference, detailError);
        }

        private IReadOnlyList<LibraryTrack> ReadResolutionLibrary(
            PlaylistState musicBeeState)
        {
            return AddMissingPlaylistTracks(
                ToResolutionLibrary(InvokeOnMusicBeeThread(
                    () => libraryReader.ReadLibrary())),
                musicBeeState.Entries);
        }

        private static IReadOnlyList<LibraryTrack> ToResolutionLibrary(
            IEnumerable<MusicLibraryTrack> tracks) =>
            tracks.Select(track => new LibraryTrack(
                    track.Url,
                    track.Url,
                    track.Artist,
                    track.Title,
                    track.DurationSeconds,
                    albumArtist: track.AlbumArtist,
                    album: track.Album,
                    discNumber: track.DiscNumber,
                    trackNumber: track.TrackNumber))
                .ToList();

        private IReadOnlyList<LibraryTrack> AddMissingPlaylistTracks(
            IReadOnlyList<LibraryTrack> library,
            IEnumerable<string> playlistEntries)
        {
            return AddMissingPlaylistTracks(
                library,
                new HashSet<string>(
                    library.Select(track => track.Url),
                    StringComparer.OrdinalIgnoreCase),
                playlistEntries);
        }

        private IReadOnlyList<LibraryTrack> AddMissingPlaylistTracks(
            IReadOnlyList<LibraryTrack> library,
            ISet<string> libraryUrls,
            IEnumerable<string> playlistEntries)
        {
            List<string> missing = playlistEntries
                .Where(url => !libraryUrls.Contains(url))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (missing.Count == 0)
            {
                return library;
            }

            var result = new List<LibraryTrack>(library.Count + missing.Count);
            result.AddRange(library);
            foreach (string url in missing)
            {
                MusicLibraryTrack track = InvokeOnMusicBeeThread(
                    () => libraryReader.ReadTrack(url));
                result.Add(new LibraryTrack(
                    track.Url,
                    track.Url,
                    track.Artist,
                    track.Title,
                    track.DurationSeconds,
                    albumArtist: track.AlbumArtist,
                    album: track.Album,
                    discNumber: track.DiscNumber,
                    trackNumber: track.TrackNumber));
            }

            return result;
        }

        private IReadOnlyList<ResolvedHarnessTrack> ResolvePhoneTracks(
            PlaylistState phoneState,
            IReadOnlyList<LibraryTrack> library,
            AcceptedBaseline? baseline,
            IEnumerable<string>? preferredUrls = null,
            string mediaFolder = DesktopSettings.DefaultPhoneMediaFolder,
            IReadOnlyDictionary<string, string>? mediaPaths = null)
        {
            return ResolvePhoneTracks(
                phoneState,
                new TrackResolver().CreateIndex(library),
                baseline,
                preferredUrls,
                mediaFolder,
                mediaPaths);
        }

        private IReadOnlyList<ResolvedHarnessTrack> ResolvePhoneTracks(
            PlaylistState phoneState,
            TrackResolverIndex resolver,
            AcceptedBaseline? baseline,
            IEnumerable<string>? preferredUrls = null,
            string mediaFolder = DesktopSettings.DefaultPhoneMediaFolder,
            IReadOnlyDictionary<string, string>? mediaPaths = null,
            ISet<string>? observedMediaPaths = null)
        {
            var knownMappings = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            if (mediaPaths != null)
            {
                foreach (IGrouping<string, KeyValuePair<string, string>> group
                    in mediaPaths.GroupBy(
                        item => TrackPathNormalizer.NormalizePhonePath(item.Value),
                        StringComparer.OrdinalIgnoreCase))
                {
                    List<string> urls = group
                        .Select(item => item.Key)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (urls.Count == 1)
                    {
                        knownMappings[group.Key] = urls[0];
                    }
                }
            }

            if (baseline != null)
            {
                foreach (KeyValuePair<string, string> item in baseline.Tracks
                    .GroupBy(track => TrackPathNormalizer.NormalizePhonePath(
                        track.PhonePath))
                    .Where(group => group
                        .Select(track => track.MusicBeeUrl)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count() == 1)
                    .ToDictionary(
                        group => group.Key,
                        group => group.First().MusicBeeUrl,
                        StringComparer.OrdinalIgnoreCase))
                {
                    knownMappings[item.Key] = item.Value;
                }
            }

            HashSet<string>? preferredUrlKeys =
                TrackResolverIndex.CreatePreferredUrlKeys(preferredUrls);
            IReadOnlyDictionary<string, string>? approvedMappings =
                knownMappings.Count == 0 ? null : knownMappings;
            var resolved = new List<ResolvedHarnessTrack>();
            for (int index = 0; index < phoneState.Entries.Count; index++)
            {
                string path = phoneState.Entries[index];
                PlaylistEntryMetadata? metadata =
                    index < phoneState.EntryMetadata.Count
                        ? phoneState.EntryMetadata[index]
                        : null;
                if (metadata != null
                    && !string.Equals(
                        TrackPathNormalizer.NormalizePhonePath(metadata.Path),
                        TrackPathNormalizer.NormalizePhonePath(path),
                        StringComparison.OrdinalIgnoreCase))
                {
                    metadata = null;
                }

                TrackReference reference = CreatePhoneReference(
                    path,
                    mediaFolder);
                ResolutionResult result = resolver.Resolve(
                    reference,
                    approvedMappings,
                    preferredUrlKeys);
                if (result.Status != ResolutionStatus.Matched
                    && !string.IsNullOrWhiteSpace(metadata?.Title))
                {
                    TrackReference extendedReference = CreatePhoneReference(
                        path,
                        mediaFolder,
                        title: metadata!.Title!.Trim(),
                        durationSeconds: metadata.DurationSeconds);
                    ResolutionResult extendedResult = resolver.Resolve(
                        extendedReference,
                        approvedMappings,
                        preferredUrlKeys);
                    if (IsBetterResolution(extendedResult, result))
                    {
                        result = extendedResult;
                        reference = extendedReference;
                    }

                    string extractedTitle = ExtractExtendedInfoTitle(
                        metadata.Title);
                    if (result.Status != ResolutionStatus.Matched
                        && !string.Equals(
                            extractedTitle,
                            extendedReference.Title,
                            StringComparison.Ordinal))
                    {
                        TrackReference extractedReference = CreatePhoneReference(
                            path,
                            mediaFolder,
                            title: extractedTitle,
                            durationSeconds: metadata.DurationSeconds);
                        ResolutionResult extractedResult = resolver.Resolve(
                            extractedReference,
                            approvedMappings,
                            preferredUrlKeys);
                        if (IsBetterResolution(extractedResult, result))
                        {
                            result = extractedResult;
                            reference = extractedReference;
                        }
                    }
                }

                if (result.Status != ResolutionStatus.Matched || result.Match == null)
                {
                    bool sourceFileExists = observedMediaPaths != null
                        && observedMediaPaths.Contains(
                            TrackPathNormalizer.NormalizePhonePath(path));
                    string reason = "Phone track could not be resolved safely ("
                        + result.Status
                        + ": "
                        + result.Reason
                        + ")."
                        + DescribeResolutionCandidates(result)
                        + DescribePhoneReference(reference)
                        + (sourceFileExists
                            ? " Its existing path was found in the live phone-media scan."
                            : " Exclude it to remove this stale playlist entry.");
                    resolved.Add(sourceFileExists
                        ? new ResolvedHarnessTrack(
                            path,
                            path,
                            path,
                            isResolved: false,
                            unavailableReason: reason,
                            sourcePhonePath: path,
                            phonePathIsCurrent: true)
                        : ResolvedHarnessTrack.UnresolvedPhone(path, reason));
                    continue;
                }

                string currentMediaPath = string.Empty;
                bool resolvedMediaPath = mediaPaths != null
                    && mediaPaths.TryGetValue(
                        result.Match.Url,
                        out currentMediaPath);
                bool sourcePathIsCurrent = observedMediaPaths != null
                    && observedMediaPaths.Contains(
                        TrackPathNormalizer.NormalizePhonePath(path));
                resolved.Add(new ResolvedHarnessTrack(
                    result.Match.Id,
                    result.Match.Url,
                    resolvedMediaPath
                        ? currentMediaPath
                        : path,
                    sourcePhonePath: path,
                    phonePathIsCurrent: resolvedMediaPath || sourcePathIsCurrent));
            }

            return resolved;
        }

        private static bool BaselineUniquelyMaps(
            AcceptedBaseline baseline,
            string phonePath,
            string musicBeeUrl)
        {
            string normalizedPath =
                TrackPathNormalizer.NormalizePhonePath(phonePath);
            List<string> mappedUrls = baseline.Tracks
                .Where(track => string.Equals(
                    TrackPathNormalizer.NormalizePhonePath(track.PhonePath),
                    normalizedPath,
                    StringComparison.OrdinalIgnoreCase))
                .Select(track => track.MusicBeeUrl)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return mappedUrls.Count == 1
                && string.Equals(
                    mappedUrls[0],
                    musicBeeUrl,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBetterResolution(
            ResolutionResult candidate,
            ResolutionResult current)
        {
            return ResolutionRank(candidate.Status) > ResolutionRank(current.Status);
        }

        private static int ResolutionRank(ResolutionStatus status)
        {
            switch (status)
            {
                case ResolutionStatus.Matched:
                    return 2;
                case ResolutionStatus.Ambiguous:
                    return 1;
                default:
                    return 0;
            }
        }

        private IReadOnlyDictionary<string, string> ResolvePhoneMediaPaths(
            IReadOnlyList<LibraryTrack> library,
            IEnumerable<string>? preferredUrls,
            out HashSet<string> observedMediaPaths)
        {
            return ResolvePhoneMediaPaths(
                new TrackResolver().CreateIndex(library),
                preferredUrls,
                cancellationToken: default,
                progress: null,
                out observedMediaPaths);
        }

        private IReadOnlyDictionary<string, string> ResolvePhoneMediaPaths(
            TrackResolverIndex resolver,
            IEnumerable<string>? preferredUrls = null,
            CancellationToken cancellationToken = default,
            IProgress<PhoneMediaTraversalProgress>? progress = null) =>
            ResolvePhoneMediaPaths(
                resolver,
                preferredUrls,
                cancellationToken,
                progress,
                out _);

        private IReadOnlyDictionary<string, string> ResolvePhoneMediaPaths(
            TrackResolverIndex resolver,
            IEnumerable<string>? preferredUrls,
            CancellationToken cancellationToken,
            IProgress<PhoneMediaTraversalProgress>? progress,
            out HashSet<string> observedMediaPaths)
        {
            observedMediaPaths = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            if (phoneMediaPathReader == null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            HashSet<string>? preferredUrlKeys =
                TrackResolverIndex.CreatePreferredUrlKeys(preferredUrls);
            var matches = new Dictionary<string, HashSet<string>>(
                StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<string> mediaPaths =
                phoneMediaPathReader is IProgressivePhoneMediaPathReader progressive
                    ? progressive.ReadMediaPaths(cancellationToken, progress)
                    : phoneMediaPathReader.ReadMediaPaths();
            foreach (string path in mediaPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                observedMediaPaths.Add(
                    TrackPathNormalizer.NormalizePhonePath(path));
                ResolutionResult result = resolver.Resolve(
                    CreatePhoneReference(path, settings.PhoneMediaFolder),
                    approvedMappings: null,
                    preferredUrlKeys);
                if (result.Status != ResolutionStatus.Matched || result.Match == null)
                {
                    continue;
                }

                if (!matches.TryGetValue(result.Match.Url, out HashSet<string> paths))
                {
                    paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    matches.Add(result.Match.Url, paths);
                }

                paths.Add(TrackPathNormalizer.NormalizePhonePath(path));
            }

            return matches
                .Where(item => item.Value.Count == 1)
                .ToDictionary(
                    item => item.Key,
                    item => item.Value.Single(),
                    StringComparer.OrdinalIgnoreCase);
        }

        private static PlaylistSideEntry ToMusicBeeDiffEntry(
            ResolvedHarnessTrack track) =>
            new PlaylistSideEntry(
                new TrackIdentity(track.TrackId),
                track.MusicBeeUrl,
                musicBeeValue: track.MusicBeeUrl,
                phoneValue: string.IsNullOrWhiteSpace(track.PhonePath)
                    ? null
                    : track.PhonePath,
                phonePathProof: string.IsNullOrWhiteSpace(track.PhonePath)
                    ? PhonePathProof.Unknown
                    : PhonePathProof.Proven);

        private static PlaylistSideEntry ToPhoneDiffEntry(
            ResolvedHarnessTrack track) =>
            new PlaylistSideEntry(
                new TrackIdentity(track.TrackId),
                track.SourcePhonePath,
                musicBeeValue: track.IsResolved ? track.MusicBeeUrl : null,
                phoneValue: track.PhonePathIsCurrent ? track.PhonePath : null,
                phonePathProof: track.PhonePathIsCurrent
                    ? PhonePathProof.Proven
                    : PhonePathProof.Unknown,
                musicBeeValueUnavailable: !track.IsResolved,
                unavailableReason: track.UnavailableReason
                    ?? (track.PhonePathIsCurrent
                        ? null
                        : "The playlist entry resolved to a track, but no unique current phone media path was discovered."));

        public void AttachUiDispatcher(Control dispatcher)
        {
            uiDispatcher = dispatcher
                ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public IReadOnlyList<HarnessPlaylistRow> RefreshPlaylistRows(
            IProgress<HarnessOperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, 0, "Starting");
            IReadOnlyList<MusicPlaylist> playlists = InvokeOnMusicBeeThread(
                () => libraryReader.ReadPlaylists());
            ReportProgress(progress, 10, "MusicBee playlists read");
            IReadOnlyList<LibraryTrack> library = ToResolutionLibrary(
                InvokeOnMusicBeeThread(() => libraryReader.ReadLibrary()));
            ReportProgress(progress, 25, "MusicBee library read");
            library = AddMissingPlaylistTracks(
                library,
                playlists.SelectMany(item => item.TrackUrls));
            var libraryUrls = new HashSet<string>(
                library.Select(track => track.Url),
                StringComparer.OrdinalIgnoreCase);
            TrackResolverIndex resolverIndex = new TrackResolver().CreateIndex(library);
            var traversalProgress = new InlineProgress<PhoneMediaTraversalProgress>(update =>
                ReportProgress(
                    progress,
                    25,
                    "Scanning phone media — "
                        + update.ObjectsScanned.ToString("N0")
                        + " objects, "
                        + update.FoldersCompleted.ToString("N0")
                        + " folders scanned, "
                        + update.FoldersPending.ToString("N0")
                        + " pending, "
                        + update.MediaFilesFound.ToString("N0")
                        + " files found"));
            IReadOnlyDictionary<string, string> mediaPaths =
                ResolvePhoneMediaPaths(
                    resolverIndex,
                    playlists.SelectMany(item => item.TrackUrls),
                    cancellationToken,
                    traversalProgress,
                    out HashSet<string> observedMediaPaths);
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, 45, "Phone media paths resolved");
            IReadOnlyList<PhonePlaylistContent>? phoneSnapshot =
                phoneSnapshotReader?.ReadPlaylistSnapshot();
            ReportProgress(progress, 65, "Phone playlists read");
            IPhonePlaylistCatalogReader catalogReader = phoneSnapshot == null
                ? phoneCatalogReader
                : new SnapshotPhonePlaylistCatalogReader(phoneSnapshot);
            var service = new PlaylistCatalogService(
                new SnapshotMusicLibraryReader(playlists),
                catalogReader);
            IReadOnlyList<PlaylistPairingCorrection> corrections = settings
                .PlaylistAssociations
                .Where(item => item.IsExplicitCorrection)
                .Select(item => item.ToPairingCorrection())
                .ToList();
            IReadOnlyList<PlaylistCatalogRow> catalogRows = service
                .Build(corrections)
                .Rows;
            var phoneStates = new Dictionary<string, PlaylistState>(
                StringComparer.OrdinalIgnoreCase);
            if (phoneSnapshot != null)
            {
                foreach (PhonePlaylistContent playlist in phoneSnapshot)
                {
                    phoneStates[playlist.BackingName] = phoneWriter.Parse(
                        playlist.BackingName,
                        playlist.Content);
                }
            }
            var rows = new List<HarnessPlaylistRow>();
            for (int index = 0; index < catalogRows.Count; index++)
            {
                PlaylistCatalogRow catalog = catalogRows[index];
                rows.Add(BuildHarnessPlaylistRow(
                    new PlaylistCatalogViewRow(catalog, null, catalog.Error),
                    library,
                    libraryUrls,
                    resolverIndex,
                    mediaPaths,
                    phoneStates,
                    observedMediaPaths));
                ReportProgress(
                    progress,
                    65 + ((index + 1) * 35 / Math.Max(1, catalogRows.Count)),
                    "Comparing playlists "
                        + (index + 1)
                        + " of "
                        + catalogRows.Count);
            }

            ReportProgress(progress, 100, "Playlist comparison complete");
            return rows;
        }

        private static void ReportProgress(
            IProgress<HarnessOperationProgress>? progress,
            int percentage,
            string status) =>
            progress?.Report(new HarnessOperationProgress(percentage, status));

        private HarnessPlaylistRow BuildHarnessPlaylistRow(
            PlaylistCatalogViewRow view,
            IReadOnlyList<LibraryTrack> resolutionLibrary,
            ISet<string> libraryUrls,
            TrackResolverIndex resolverIndex,
            IReadOnlyDictionary<string, string> mediaPaths,
            IDictionary<string, PlaylistState> phoneStates,
            ISet<string> observedMediaPaths)
        {
            PlaylistCatalogRow catalog = view.CatalogRow;
            MusicPlaylist? musicBee = catalog.MusicBeePlaylist;
            PhonePlaylistFile? phone = catalog.PhonePlaylist;
            string? generatedBackingName = musicBee == null
                ? null
                : PlaylistCatalogService.CreatePhoneBackingName(musicBee.Name);
            try
            {
                if (catalog.Status == PlaylistPairingStatus.Paired
                    && musicBee != null
                    && phone != null)
                {
                    var context = new PlaylistPairContext(
                        catalog.RowId,
                        musicBee.Url,
                        phone.Id,
                        phone.BackingName);
                    PlaylistState musicBeeState = StateFromPlaylist(musicBee);
                    PlaylistState phoneState = ReadPhoneState(
                        phone.BackingName,
                        phoneStates);
                    if (!phoneState.Exists)
                    {
                        throw new InvalidOperationException(
                            "Phone playlist was not found: " + phone.BackingName);
                    }

                    AcceptedBaseline? baseline = baselineStore.Load(context.PairId);
                    IReadOnlyList<LibraryTrack> effectiveLibrary =
                        AddMissingPlaylistTracks(
                            resolutionLibrary,
                            libraryUrls,
                            musicBeeState.Entries);
                    TrackResolverIndex effectiveResolver =
                        effectiveLibrary.Count == resolutionLibrary.Count
                            ? resolverIndex
                            : new TrackResolver().CreateIndex(effectiveLibrary);
                    IReadOnlyList<ResolvedHarnessTrack> phoneTracks = ResolvePhoneTracks(
                        phoneState,
                        effectiveResolver,
                        baseline,
                        musicBeeState.Entries,
                        settings.PhoneMediaFolder,
                        mediaPaths,
                        observedMediaPaths);
                    IReadOnlyList<ResolvedHarnessTrack> musicBeeTracks =
                        PairMusicBeeOccurrences(
                            musicBeeState.Entries,
                            phoneTracks,
                            baseline,
                            mediaPaths);
                    PlaylistDiff diff = PlaylistDiffEngine.Compare(
                        musicBeeTracks.Select(ToMusicBeeDiffEntry),
                        phoneTracks.Select(ToPhoneDiffEntry));
                    return RowFromDiff(
                        catalog,
                        musicBeeState,
                        phoneState,
                        diff,
                        context,
                        musicBee.Url,
                        phone.BackingName);
                }

                if (catalog.Status == PlaylistPairingStatus.MusicBeeOnly
                    && musicBee != null)
                {
                    PlaylistState musicBeeState = StateFromPlaylist(musicBee);
                    var phoneState = new PlaylistState(
                        exists: false,
                        PlaylistChecksum.Compute(Array.Empty<string>()),
                        Array.Empty<string>());
                    IReadOnlyList<ResolvedHarnessTrack> tracks = PairMusicBeeOccurrences(
                        musicBeeState.Entries,
                        Array.Empty<ResolvedHarnessTrack>(),
                        baselineStore.Load(catalog.RowId),
                        mediaPaths);
                    PlaylistDiff diff = PlaylistDiffEngine.Compare(
                        tracks.Select(ToMusicBeeDiffEntry),
                        Array.Empty<PlaylistSideEntry>());
                    return RowFromDiff(
                        catalog,
                        musicBeeState,
                        phoneState,
                        diff,
                        null,
                        musicBee.Url,
                        generatedBackingName);
                }

                if (catalog.Status == PlaylistPairingStatus.PhoneOnly
                    && phone != null)
                {
                    PlaylistState phoneState = ReadPhoneState(
                        phone.BackingName,
                        phoneStates);
                    var missingMusicBee = new PlaylistState(
                        false,
                        PlaylistChecksum.Compute(Array.Empty<string>()),
                        Array.Empty<string>());
                    IReadOnlyList<ResolvedHarnessTrack> tracks = ResolvePhoneTracks(
                        phoneState,
                        resolverIndex,
                        baselineStore.Load(catalog.RowId),
                        mediaFolder: settings.PhoneMediaFolder,
                        mediaPaths: mediaPaths,
                        observedMediaPaths: observedMediaPaths);
                    PlaylistDiff diff = PlaylistDiffEngine.Compare(
                        Array.Empty<PlaylistSideEntry>(),
                        tracks.Select(ToPhoneDiffEntry));
                    return RowFromDiff(
                        catalog,
                        missingMusicBee,
                        phoneState,
                        diff,
                        null,
                        null,
                        phone.BackingName);
                }

                return AttentionRow(catalog, view.DetailError ?? DescribeCatalogAttention(catalog));
            }
            catch (Exception exception)
            {
                return AttentionRow(catalog, exception.Message);
            }
        }

        private static PlaylistState StateFromPlaylist(MusicPlaylist playlist) =>
            new PlaylistState(
                exists: true,
                PlaylistChecksum.Compute(playlist.TrackUrls),
                playlist.TrackUrls);

        private PlaylistState ReadPhoneState(
            string backingName,
            IDictionary<string, PlaylistState> states)
        {
            PlaylistState state;
            if (!states.TryGetValue(backingName, out state))
            {
                state = phoneWriter.Read(backingName);
                states.Add(backingName, state);
            }

            return state;
        }

        private static HarnessPlaylistRow RowFromDiff(
            PlaylistCatalogRow catalog,
            PlaylistState musicBeeState,
            PlaylistState phoneState,
            PlaylistDiff diff,
            PlaylistPairContext? context,
            string? musicBeePlaylistId,
            string? phoneBackingName)
        {
            bool oneSided = catalog.Status == PlaylistPairingStatus.MusicBeeOnly
                || catalog.Status == PlaylistPairingStatus.PhoneOnly;
            HarnessPlaylistVisualState state = oneSided
                ? HarnessPlaylistVisualState.OneSided
                : diff.Kind == PlaylistDifferenceKind.Membership
                    ? HarnessPlaylistVisualState.Changed
                    : diff.Kind == PlaylistDifferenceKind.PhonePath
                        ? HarnessPlaylistVisualState.Changed
                    : diff.Kind == PlaylistDifferenceKind.OrderOnly
                        ? HarnessPlaylistVisualState.OrderOnly
                        : HarnessPlaylistVisualState.Unchanged;
            string status = oneSided
                ? "ONE SIDE ONLY"
                : diff.Kind == PlaylistDifferenceKind.Membership
                    ? "TRACKS DIFFER"
                    : diff.Kind == PlaylistDifferenceKind.PhonePath
                        ? "PHONE PATHS CHANGED"
                    : diff.Kind == PlaylistDifferenceKind.OrderOnly
                        ? "ORDER DIFFERS"
                        : "UP TO DATE";
            return new HarnessPlaylistRow(
                catalog.RowId,
                catalog.DisplayName,
                catalog.MusicBeePlaylist?.Name,
                catalog.PhonePlaylist?.DisplayName,
                musicBeeState.Checksum,
                phoneState.Checksum,
                status,
                state,
                diff,
                context,
                musicBeePlaylistId,
                phoneBackingName);
        }

        private static HarnessPlaylistRow AttentionRow(
            PlaylistCatalogRow catalog,
            string details) =>
            new HarnessPlaylistRow(
                catalog.RowId,
                catalog.DisplayName,
                catalog.MusicBeePlaylist?.Name,
                catalog.PhonePlaylist?.DisplayName,
                string.Empty,
                string.Empty,
                "ATTENTION: " + details,
                HarnessPlaylistVisualState.Attention,
                null);

        private static string DescribeCatalogAttention(PlaylistCatalogRow row) =>
            row.Status == PlaylistPairingStatus.Ambiguous
                ? "Multiple playlists have the same normalized name."
                : row.Error ?? "The playlist row cannot be reviewed.";

        public HarnessBatchApplyResult ApplyAll(
            IReadOnlyList<PlaylistReviewDraft> drafts,
            CancellationToken cancellationToken,
            IProgress<int>? progress = null)
        {
            var succeeded = new List<string>();
            var errors = new List<string>();
            var warnings = new List<string>();
            bool wasCancelled = false;
            bool phoneConnectionLost = false;
            int rolledBackCount = 0;
            int processedCount = 0;
            progress?.Report(0);
            IReadOnlyDictionary<string, HarnessPlaylistRow> currentRows =
                RefreshPlaylistRows().ToDictionary(item => item.RowId, StringComparer.Ordinal);
            var preflightErrors = new List<string>();
            foreach (PlaylistReviewDraft draft in drafts)
            {
                if (!draft.IsConfirmed || draft.IsStale)
                {
                    preflightErrors.Add("• " + draft.RowId
                        + ": Only fresh confirmed reviews can be applied.");
                    continue;
                }

                if (!currentRows.TryGetValue(draft.RowId, out HarnessPlaylistRow row)
                    || row.Diff == null)
                {
                    preflightErrors.Add("• " + draft.RowId
                        + ": The playlist row is no longer available for review.");
                    continue;
                }

                if (!string.Equals(
                        row.MusicBeeChecksum,
                        draft.MusicBeeChecksum,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        row.PhoneChecksum,
                        draft.PhoneChecksum,
                        StringComparison.Ordinal))
                {
                    preflightErrors.Add("• " + row.DisplayName
                        + ": The playlist changed after it was reviewed.");
                }
            }

            if (preflightErrors.Count > 0)
            {
                return new HarnessBatchApplyResult(
                    0,
                    preflightErrors.Count,
                    "Preflight failed, so no backup or sync writes were made."
                        + Environment.NewLine + Environment.NewLine
                        + string.Join(Environment.NewLine, preflightErrors),
                    Array.Empty<string>(),
                    notStartedCount: drafts.Count);
            }

            bool phoneChanging = drafts.Any(draft =>
                IsPhoneChanging(currentRows[draft.RowId], draft));
            PhonePlaylistBackupResult? batchBackup = null;
            string? batchBackupLocation = null;
            if (phoneChanging)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    batchBackup = phoneBackupTransport.CreatePlaylistBackup();
                    batchBackupLocation = DescribePhoneBackupLocation(batchBackup.Handle);
                }
                catch (OperationCanceledException)
                {
                    return new HarnessBatchApplyResult(
                        0,
                        0,
                        "Cancelled before the phone backup was created; no sync writes were made.",
                        Array.Empty<string>(),
                        wasCancelled: true,
                        notStartedCount: drafts.Count);
                }
                catch (Exception exception)
                {
                    return new HarnessBatchApplyResult(
                        0,
                        1,
                        "The all-playlist phone backup could not be created, so no sync "
                            + "writes were made: " + DescribeApplyFailure(exception.Message),
                        Array.Empty<string>(),
                        notStartedCount: drafts.Count);
                }
            }

            for (int index = 0; index < drafts.Count; index++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    wasCancelled = true;
                    break;
                }

                PlaylistReviewDraft draft = drafts[index];
                try
                {
                    if (!draft.IsConfirmed || draft.IsStale)
                    {
                        throw new InvalidOperationException(
                            "Only fresh confirmed reviews can be applied.");
                    }

                    if (!currentRows.TryGetValue(draft.RowId, out HarnessPlaylistRow row)
                        || row.Diff == null)
                    {
                        throw new InvalidOperationException(
                            "The playlist row is no longer available for review.");
                    }

                    if (!string.Equals(row.MusicBeeChecksum, draft.MusicBeeChecksum, StringComparison.Ordinal)
                        || !string.Equals(row.PhoneChecksum, draft.PhoneChecksum, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The playlist changed after it was reviewed.");
                    }

                    SynchronizationApplyStatus status;
                    string details;
                    if (row.IsPaired)
                    {
                        PlaylistDetailDiff detail = LoadPlaylistDetail(row.PairContext!);
                        var reviewed = new ReviewedPlaylistDraft(
                            row.PairContext!.Pair,
                            draft.MusicBeeChecksum,
                            draft.PhoneChecksum,
                            draft.OrderSide,
                            draft.Action == PlaylistLandingAction.TakeMusicBee
                                ? PlaylistSide.MusicBee
                                : draft.Action == PlaylistLandingAction.TakePhone
                                    ? PlaylistSide.Phone
                                    : (PlaylistSide?)null,
                            draft.DecisionsFor(row.Diff.Occurrences));
                        SynchronizationApplyResult result = ApplyReviewedResult(
                            detail,
                            reviewed,
                            cancellationToken,
                            managePhoneBackup: !phoneChanging);
                        status = result.Status;
                        details = result.Details;
                    }
                    else
                    {
                        SynchronizationLifecycleResult result =
                            ApplyOneSided(
                                row,
                                draft,
                                cancellationToken,
                                managePhoneBackup: !phoneChanging);
                        status = result.Status;
                        details = result.Details;
                    }

                    if (status != SynchronizationApplyStatus.Succeeded)
                    {
                        if (status == SynchronizationApplyStatus.Cancelled)
                        {
                            wasCancelled = true;
                            rolledBackCount++;
                            break;
                        }

                        throw new InvalidOperationException(details);
                    }

                    succeeded.Add(draft.RowId);
                    if (details.StartsWith("WARNING:", StringComparison.Ordinal))
                    {
                        warnings.Add(draft.RowId + ": " + details);
                    }
                }
                catch (Exception exception) when (!(exception is OperationCanceledException))
                {
                    string playlistName = currentRows.TryGetValue(
                        draft.RowId,
                        out HarnessPlaylistRow? failedRow)
                            ? failedRow.DisplayName
                            : draft.RowId;
                    errors.Add(
                        "• " + playlistName + ": "
                        + DescribeApplyFailure(exception.Message));
                    if (IsHungWpdDeviceFailure(exception.Message))
                    {
                        phoneConnectionLost = true;
                    }
                }
                finally
                {
                    processedCount++;
                    progress?.Report((index + 1) * 100 / drafts.Count);
                }

                if (phoneConnectionLost)
                {
                    break;
                }
            }

            string summary = succeeded.Count
                + " playlist change(s) applied successfully.";
            int notStartedCount = drafts.Count - processedCount;
            if (wasCancelled)
            {
                int completedPercentage = drafts.Count == 0
                    ? 0
                    : processedCount * 100 / drafts.Count;
                summary = "Cancelled at " + completedPercentage + "% — "
                    + succeeded.Count + " playlist(s) applied";
                if (rolledBackCount > 0)
                {
                    summary += ", " + rolledBackCount + " rolled back";
                }

                summary += ", " + notStartedCount + " not started.";
            }
            else if (phoneConnectionLost)
            {
                summary += Environment.NewLine + Environment.NewLine
                    + "The phone stopped responding, so Shmembee stopped the sync "
                    + "to avoid causing more failures.";
                if (notStartedCount > 0)
                {
                    summary += Environment.NewLine + notStartedCount
                        + " playlist change(s) were not started.";
                }

                summary += Environment.NewLine
                    + "Reconnect and unlock the phone, wait for Windows to recognize it, "
                    + "then refresh and review the playlists before trying again.";
            }
            if (errors.Count > 0)
            {
                summary += Environment.NewLine + Environment.NewLine + errors.Count
                    + " playlist change(s) need attention:"
                    + Environment.NewLine + string.Join(Environment.NewLine, errors);
            }
            if (warnings.Count > 0)
            {
                summary += Environment.NewLine + Environment.NewLine + warnings.Count
                    + " warning(s):" + Environment.NewLine
                    + string.Join(Environment.NewLine, warnings);
            }

            bool completeSuccess = !wasCancelled
                && !phoneConnectionLost
                && errors.Count == 0
                && processedCount == drafts.Count;
            if (completeSuccess
                && succeeded.Count > 0
                && postSyncBackup != null)
            {
                try
                {
                    string backupPath = postSyncBackup.Create();
                    summary += Environment.NewLine
                        + "Post-sync M3U backup: " + backupPath;
                }
                catch (Exception exception)
                {
                    completeSuccess = false;
                    errors.Add("• Post-sync backup: "
                        + DescribeApplyFailure(exception.Message));
                    summary += Environment.NewLine + Environment.NewLine
                        + "The playlist changes succeeded, but the safety backup "
                        + "could not be created:" + Environment.NewLine
                        + "• " + DescribeApplyFailure(exception.Message);
                }
            }

            if (batchBackup != null)
            {
                if (completeSuccess)
                {
                    try
                    {
                        phoneBackupTransport.DeletePlaylistBackup(batchBackup.Handle);
                        summary += Environment.NewLine
                            + "Temporary all-playlist phone backup deleted after complete success.";
                    }
                    catch (Exception exception)
                    {
                        completeSuccess = false;
                        errors.Add("• Phone backup cleanup: "
                            + DescribeApplyFailure(exception.Message));
                        summary += Environment.NewLine + Environment.NewLine
                            + "The sync writes succeeded, but the temporary phone backup "
                            + "could not be deleted: "
                            + DescribeApplyFailure(exception.Message)
                            + Environment.NewLine + "Backup retained at "
                            + batchBackupLocation + ".";
                    }
                }
                else
                {
                    summary += Environment.NewLine + Environment.NewLine
                        + "Phone backup retained at " + batchBackupLocation + ".";
                }
            }

            return new HarnessBatchApplyResult(
                succeeded.Count,
                errors.Count,
                summary,
                succeeded,
                wasCancelled,
                rolledBackCount,
                notStartedCount);
        }

        private static bool IsHungWpdDeviceFailure(string details) =>
            !string.IsNullOrEmpty(details)
            && (details.IndexOf("0x802A0006", StringComparison.OrdinalIgnoreCase) >= 0
                || details.IndexOf("-2144731130", StringComparison.Ordinal) >= 0
                || details.IndexOf(
                    "E_WPD_DEVICE_IS_HUNG",
                    StringComparison.OrdinalIgnoreCase) >= 0);

        private static string DescribeApplyFailure(string details)
        {
            if (string.IsNullOrWhiteSpace(details))
            {
                return "The change could not be applied.";
            }

            if (IsHungWpdDeviceFailure(details))
            {
                return "The phone stopped responding during an MTP file operation "
                    + "(WPD 0x802A0006). The operation was stopped; its final phone "
                    + "state could not be confirmed.";
            }

            if (details.IndexOf("0x80042009", StringComparison.OrdinalIgnoreCase) >= 0
                || details.IndexOf("-2147213303", StringComparison.Ordinal) >= 0)
            {
                return "The phone invalidated an MTP file reference, usually after a "
                    + "disconnect or media refresh (0x80042009). Refresh the playlists "
                    + "before retrying.";
            }

            string readable = Regex.Replace(
                details,
                @"WPD sidecar operation [0-9a-f]{32} failed(?: at ([a-z0-9-]+))?",
                match => string.IsNullOrEmpty(match.Groups[1].Value)
                    ? "A phone file operation failed"
                    : "A phone file operation failed during "
                        + match.Groups[1].Value.Replace('-', ' '),
                RegexOptions.IgnoreCase);
            readable = Regex.Replace(readable, @"\s*\[[^\]]+\]\s*", " ");
            readable = Regex.Replace(readable, @"\s+", " ").Trim();
            return readable;
        }

        private SynchronizationLifecycleResult ApplyOneSided(
            HarnessPlaylistRow row,
            PlaylistReviewDraft draft,
            CancellationToken cancellationToken,
            bool managePhoneBackup = true)
        {
            if (row.MusicBeePlaylistId != null)
            {
                if (draft.Action == PlaylistLandingAction.TakePhone)
                {
                    return DeleteMusicBeePlaylist(
                        row.MusicBeePlaylistId,
                        row.MusicBeeName ?? row.DisplayName,
                        draft.MusicBeeChecksum,
                        cancellationToken);
                }

                IReadOnlyList<PlaylistOccurrence> ordered =
                    OrderOccurrences(row.Diff!, PlaylistSide.MusicBee);
                List<PlaylistSideEntry> available = ordered
                    .Where(item => item.MusicBeeEntry?.ValueFor(PlaylistSide.Phone) != null)
                    .Select(item => item.MusicBeeEntry!)
                    .ToList();
                List<PlaylistOccurrence> skipped = ordered
                    .Where(item => item.MusicBeeEntry?.ValueFor(PlaylistSide.Phone) == null)
                    .ToList();
                if (available.Count == 0)
                {
                    return SynchronizationLifecycleResult.Failed(
                        "No tracks in this MusicBee playlist could be matched "
                            + "to existing files in the configured phone media folder.");
                }

                SynchronizationLifecycleResult created = CreatePhonePlaylist(
                    row.PhoneBackingName!,
                    draft.PhoneChecksum,
                    available.Select(item =>
                        item.ValueFor(PlaylistSide.Phone) ?? string.Empty).ToList(),
                    cancellationToken,
                    managePhoneBackup);
                if (created.Status != SynchronizationApplyStatus.Succeeded
                    || skipped.Count == 0)
                {
                    return created;
                }

                return SynchronizationLifecycleResult.Succeeded(
                    "WARNING: Phone playlist created and verified, but "
                        + skipped.Count
                        + " unresolved track occurrence(s) were skipped: "
                        + string.Join(
                            "; ",
                            skipped.Select(item =>
                                item.MusicBeeEntry?.MusicBeeValue
                                    ?? item.Track.Value)),
                    created.MusicBeeState,
                    created.PhoneState,
                    created.CreatedMusicBeePlaylistUrl);
            }

            if (draft.Action == PlaylistLandingAction.TakeMusicBee)
            {
                return DeletePhonePlaylist(
                    row.PhoneBackingName!,
                    draft.PhoneChecksum,
                    cancellationToken,
                    managePhoneBackup);
            }

            PlaylistBuildResult phoneBuilt = PlaylistResultBuilder.TakeCompleteSide(
                row.Diff!,
                PlaylistSide.Phone,
                PlaylistSide.Phone);
            return phoneBuilt.IsBlocked
                ? SynchronizationLifecycleResult.Failed(
                    string.Join(Environment.NewLine, phoneBuilt.BlockedReasons))
                : CreateMusicBeePlaylist(
                    row.PhoneName ?? row.DisplayName,
                    phoneBuilt.Entries.Select(item =>
                        item.ValueFor(PlaylistSide.MusicBee) ?? string.Empty).ToList(),
                    cancellationToken);
        }

        private static bool IsPhoneChanging(
            HarnessPlaylistRow row,
            PlaylistReviewDraft draft)
        {
            if (row.IsPaired)
            {
                return true;
            }

            if (row.MusicBeePlaylistId != null)
            {
                return draft.Action != PlaylistLandingAction.TakePhone;
            }

            return draft.Action == PlaylistLandingAction.TakeMusicBee;
        }

        private static IReadOnlyList<PlaylistOccurrence> OrderOccurrences(
            PlaylistDiff diff,
            PlaylistSide side)
        {
            IReadOnlyList<string> order = side == PlaylistSide.MusicBee
                ? diff.MusicBeeOrder
                : diff.PhoneOrder;
            var byKey = diff.Occurrences.ToDictionary(
                item => item.Key,
                StringComparer.Ordinal);
            return order
                .Where(byKey.ContainsKey)
                .Select(key => byKey[key])
                .ToList();
        }

        private static PlaylistSideEntry ToDiffMusicBeeEntry(ResolvedHarnessTrack track) =>
            new PlaylistSideEntry(
                new TrackIdentity(track.TrackId),
                track.MusicBeeUrl,
                musicBeeValue: track.MusicBeeUrl,
                phoneValue: string.IsNullOrWhiteSpace(track.PhonePath)
                    ? null
                    : track.PhonePath,
                phonePathProof: string.IsNullOrWhiteSpace(track.PhonePath)
                    ? PhonePathProof.Unknown
                    : PhonePathProof.Proven);

        private static PlaylistSideEntry ToDiffPhoneEntry(ResolvedHarnessTrack track) =>
            new PlaylistSideEntry(
                new TrackIdentity(track.TrackId),
                track.PhonePath,
                musicBeeValue: track.MusicBeeUrl,
                phoneValue: track.PhonePath,
                phonePathProof: PhonePathProof.Proven);

        public HarnessPreview Refresh()
        {
            MusicPlaylist playlist = InvokeOnMusicBeeThread(
                    () => libraryReader.ReadPlaylists())
                .SingleOrDefault(item => string.Equals(
                    item.Name,
                    playlistName,
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    "MusicBee playlist was not found: " + playlistName);
            PlaylistState musicBeeState = musicBeeWriter.Read(playlist.Url);
            PlaylistState phoneState = phoneWriter.Read(phoneBackingName);
            if (!phoneState.Exists)
            {
                throw new InvalidOperationException(
                    "Phone playlist was not found: " + phoneBackingName);
            }

            AcceptedBaseline? baseline = baselineStore.Load(playlistId);
            IReadOnlyList<LibraryTrack> library = InvokeOnMusicBeeThread(
                    () => libraryReader.ReadLibrary())
                .Select(track => new LibraryTrack(
                    track.Url,
                    track.Url,
                    track.Artist,
                    track.Title,
                    track.DurationSeconds,
                    albumArtist: track.AlbumArtist,
                    album: track.Album,
                    discNumber: track.DiscNumber,
                    trackNumber: track.TrackNumber))
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
                            + ")."
                            + DescribeResolutionCandidates(result)
                            + " MusicBee supplied "
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
            return ApplyPhoneChangingPlan(plan, cancellationToken);
        }

        private SynchronizationPlan CreatePlan(
            HarnessPreview preview,
            IReadOnlyList<ResolvedHarnessTrack> tracks,
            string expectedMusicBeeChecksum,
            string expectedPhoneChecksum) =>
            new SynchronizationPlan(
                Guid.NewGuid(),
                playlistId,
                playlistName,
                preview.MusicBeePlaylistUrl,
                phoneBackingName,
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
            AcceptedBaseline? baseline,
            IReadOnlyDictionary<string, string>? mediaPaths = null)
        {
            var available = new Dictionary<string, Queue<string>>(
                StringComparer.OrdinalIgnoreCase);
            if (mediaPaths == null)
            {
                foreach (ResolvedHarnessTrack track in phoneTracks
                    .Where(track => track.IsResolved)
                    .Concat(
                    baseline?.Tracks.Select(track => new ResolvedHarnessTrack(
                        track.TrackId,
                        track.MusicBeeUrl,
                        track.PhonePath))
                    ?? Enumerable.Empty<ResolvedHarnessTrack>()))
                {
                    if (!available.TryGetValue(
                        track.MusicBeeUrl,
                        out Queue<string> paths))
                    {
                        paths = new Queue<string>();
                        available.Add(track.MusicBeeUrl, paths);
                    }

                    paths.Enqueue(track.PhonePath);
                }
            }

            var result = new List<ResolvedHarnessTrack>();
            IReadOnlyDictionary<string, string> reusableMediaPaths = mediaPaths == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : mediaPaths;
            foreach (string musicBeeUrl in musicBeeUrls)
            {
                string phonePath = reusableMediaPaths.TryGetValue(
                        musicBeeUrl,
                        out string mediaPath)
                    ? mediaPath
                    : available.TryGetValue(
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

        private static string DescribeResolutionCandidates(
            ResolutionResult result)
        {
            if (result.Candidates.Count == 0)
            {
                return string.Empty;
            }

            const int candidateLimit = 5;
            List<string> candidates = result.Candidates
                .Take(candidateLimit)
                .Select(candidate => candidate.Url)
                .ToList();
            return " Candidates: "
                + string.Join(" | ", candidates)
                + (result.Candidates.Count > candidateLimit
                    ? " | ... ("
                        + (result.Candidates.Count - candidateLimit)
                        + " more)"
                    : string.Empty)
                + ".";
        }

        private static string DescribePhoneReference(TrackReference reference)
        {
            var parts = new List<string>();
            AddReferencePart(parts, "title", reference.Title);
            AddReferencePart(parts, "album artist", reference.AlbumArtist);
            AddReferencePart(parts, "album", reference.Album);
            if (reference.DiscNumber.HasValue)
            {
                parts.Add("disc='" + reference.DiscNumber.Value + "'");
            }

            if (reference.TrackNumber.HasValue)
            {
                parts.Add("track='" + reference.TrackNumber.Value + "'");
            }

            if (reference.DurationSeconds.HasValue)
            {
                parts.Add("duration='" + reference.DurationSeconds.Value + "s'");
            }

            return parts.Count == 0
                ? string.Empty
                : " Parsed phone metadata: " + string.Join(", ", parts) + ".";
        }

        private static void AddReferencePart(
            ICollection<string> parts,
            string name,
            string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add(name + "='" + value!.Trim() + "'");
            }
        }

        private static string ExtractExtendedInfoTitle(string value)
        {
            string title = value.Trim();
            int separator = title.IndexOf(" - ", StringComparison.Ordinal);
            return separator < 0 || separator + 3 >= title.Length
                ? title
                : title.Substring(separator + 3).Trim();
        }

        private static TrackReference CreatePhoneReference(
            string phonePath,
            string mediaFolder = DesktopSettings.DefaultPhoneMediaFolder,
            string? title = null,
            int? durationSeconds = null)
        {
            string[] segments = phonePath
                .Replace('\\', '/')
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            string[] rootSegments = (mediaFolder ?? string.Empty)
                .Replace('\\', '/')
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            int rootIndex = FindSegmentSequence(segments, rootSegments);
            int contentIndex = rootIndex < 0 ? -1 : rootIndex + rootSegments.Length;
            string? artist = contentIndex >= 0 && contentIndex < segments.Length
                ? segments[contentIndex]
                : null;
            string? album = contentIndex >= 0 && contentIndex + 1 < segments.Length
                ? segments[contentIndex + 1]
                : null;
            string fileName = segments.Length == 0
                ? phonePath
                : segments[segments.Length - 1];
            PhoneFileNameMetadata fileMetadata =
                PhoneFileNameParser.Parse(fileName, artist);

            return new TrackReference(
                phonePath,
                artist: artist,
                title: string.IsNullOrWhiteSpace(title)
                    ? fileMetadata.Title
                    : title,
                durationSeconds: durationSeconds,
                albumArtist: artist,
                album: album,
                discNumber: fileMetadata.DiscNumber,
                trackNumber: fileMetadata.TrackNumber);
        }

        private static int FindSegmentSequence(
            IReadOnlyList<string> segments,
            IReadOnlyList<string> expected)
        {
            if (expected.Count == 0 || expected.Count > segments.Count)
            {
                return -1;
            }

            for (int start = 0; start <= segments.Count - expected.Count; start++)
            {
                bool matches = true;
                for (int index = 0; index < expected.Count; index++)
                {
                    if (!string.Equals(
                        segments[start + index],
                        expected[index],
                        StringComparison.OrdinalIgnoreCase))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    return start;
                }
            }

            return -1;
        }

        private PlaylistSnapshot Snapshot(
            Guid playlistId,
            IEnumerable<string> trackIds) =>
            new PlaylistSnapshot(
                playlistId,
                playlistName,
                phoneBackingName,
                trackIds.Select(trackId => new PlaylistEntry(
                    Guid.NewGuid(),
                    new TrackIdentity(trackId),
                    trackId)),
                DateTimeOffset.UtcNow);

        private Guid StablePlaylistGuid()
        {
            byte[] bytes = System.Security.Cryptography.MD5.Create()
                .ComputeHash(System.Text.Encoding.UTF8.GetBytes(playlistId));
            return new Guid(bytes);
        }

        private T InvokeOnMusicBeeThread<T>(Func<T> action)
        {
            Control? dispatcher = uiDispatcher;
            if (dispatcher == null || dispatcher.IsDisposed)
            {
                return action();
            }

            if (!dispatcher.InvokeRequired)
            {
                return action();
            }

            return (T)dispatcher.Invoke(action);
        }

        private sealed class DispatchedMusicBeePlaylistWriter : IMusicBeePlaylistWriter
        {
            private readonly MusicBeePlaylistWriter inner;
            private readonly Func<Func<PlaylistState>, PlaylistState> invokeRead;
            private readonly Func<Func<bool>, bool> invokeWrite;

            public DispatchedMusicBeePlaylistWriter(
                MusicBeePlaylistWriter inner,
                Func<Func<PlaylistState>, PlaylistState> invokeRead,
                Func<Func<bool>, bool> invokeWrite)
            {
                this.inner = inner;
                this.invokeRead = invokeRead;
                this.invokeWrite = invokeWrite;
            }

            public PlaylistState Read(string playlistUrl) =>
                invokeRead(() => inner.Read(playlistUrl));

            public bool Replace(string playlistUrl, IReadOnlyList<string> entries)
            {
                return invokeWrite(() => inner.Replace(playlistUrl, entries));
            }

            public string Create(string playlistName, IReadOnlyList<string> entries)
            {
                string result = string.Empty;
                invokeWrite(() =>
                {
                    result = inner.Create(playlistName, entries);
                    return !string.IsNullOrWhiteSpace(result);
                });
                return result;
            }

            public bool Delete(string playlistUrl)
            {
                return invokeWrite(() => inner.Delete(playlistUrl));
            }
        }

        private sealed class SnapshotMusicLibraryReader : IMusicLibraryReader
        {
            private readonly IReadOnlyList<MusicPlaylist> playlists;

            public SnapshotMusicLibraryReader(
                IReadOnlyList<MusicPlaylist> playlists)
            {
                this.playlists = playlists;
            }

            public IReadOnlyList<MusicLibraryTrack> ReadLibrary() =>
                Array.Empty<MusicLibraryTrack>();

            public IReadOnlyList<MusicPlaylist> ReadPlaylists() =>
                playlists;

            public MusicLibraryTrack ReadTrack(string url) =>
                new MusicLibraryTrack(url, null, null, null);
        }

        private sealed class SnapshotPhonePlaylistCatalogReader :
            IPhonePlaylistCatalogReader
        {
            private readonly IReadOnlyList<PhonePlaylistFile> playlists;

            public SnapshotPhonePlaylistCatalogReader(
                IEnumerable<PhonePlaylistContent> playlists)
            {
                this.playlists = playlists
                    .Select(item => new PhonePlaylistFile(
                        item.Id,
                        item.BackingName,
                        byteCount: item.Content.Length))
                    .ToList();
            }

            public IReadOnlyList<PhonePlaylistFile> ListPlaylists() =>
                playlists;
        }
    }

    internal sealed class PlaylistPairContext
    {
        public PlaylistPairContext(
            string pairId,
            string musicBeePlaylistId,
            string phonePlaylistId,
            string phoneBackingName)
        {
            PairId = pairId;
            MusicBeePlaylistId = musicBeePlaylistId;
            PhonePlaylistId = phonePlaylistId;
            PhoneBackingName = phoneBackingName;
            Pair = new PlaylistPairKey(musicBeePlaylistId, phonePlaylistId);
        }

        public string PairId { get; }

        public string MusicBeePlaylistId { get; }

        public string PhonePlaylistId { get; }

        public string PhoneBackingName { get; }

        public PlaylistPairKey Pair { get; }
    }

    internal sealed class PlaylistCatalogViewRow
    {
        public PlaylistCatalogViewRow(
            PlaylistCatalogRow catalogRow,
            PlaylistDifferenceKind? difference,
            string? detailError)
        {
            CatalogRow = catalogRow;
            Difference = difference;
            DetailError = detailError;
        }

        public PlaylistCatalogRow CatalogRow { get; }

        public PlaylistDifferenceKind? Difference { get; }

        public string? DetailError { get; }

        public bool MembershipMatches =>
            Difference == PlaylistDifferenceKind.Identical
            || Difference == PlaylistDifferenceKind.PhonePath
            || Difference == PlaylistDifferenceKind.OrderOnly;

        public bool OrderMatches => Difference == PlaylistDifferenceKind.Identical;
    }

    internal sealed class PlaylistDetailDiff
    {
        public PlaylistDetailDiff(
            PlaylistPairContext context,
            string playlistName,
            PlaylistState musicBeeState,
            PlaylistState phoneState,
            PlaylistDiff diff)
        {
            Context = context;
            PlaylistName = playlistName;
            MusicBeeState = musicBeeState;
            PhoneState = phoneState;
            Diff = diff;
        }

        public PlaylistPairContext Context { get; }

        public string PlaylistName { get; }

        public PlaylistState MusicBeeState { get; }

        public PlaylistState PhoneState { get; }

        public PlaylistDiff Diff { get; }
    }

    internal sealed class ReviewedPlaylistApplyRequest
    {
        public ReviewedPlaylistApplyRequest(
            bool isChecked,
            PlaylistDetailDiff detail,
            ReviewedPlaylistDraft draft)
        {
            IsChecked = isChecked;
            Detail = detail;
            Draft = draft;
        }

        public bool IsChecked { get; }

        public PlaylistDetailDiff Detail { get; }

        public ReviewedPlaylistDraft Draft { get; }
    }

    internal sealed class PlaylistApplyAllResult
    {
        public PlaylistApplyAllResult(
            PlaylistPairContext context,
            SynchronizationApplyResult result)
        {
            Context = context;
            Result = result;
        }

        public PlaylistPairContext Context { get; }

        public SynchronizationApplyResult Result { get; }
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
            string phonePath,
            bool isResolved = true,
            string? unavailableReason = null,
            string? sourcePhonePath = null,
            bool phonePathIsCurrent = true)
        {
            TrackId = trackId;
            MusicBeeUrl = musicBeeUrl;
            PhonePath = phonePath;
            SourcePhonePath = sourcePhonePath ?? phonePath;
            PhonePathIsCurrent = phonePathIsCurrent;
            IsResolved = isResolved;
            UnavailableReason = unavailableReason;
        }

        public string TrackId { get; }

        public string MusicBeeUrl { get; }

        public string PhonePath { get; }

        public string SourcePhonePath { get; }

        public bool PhonePathIsCurrent { get; }

        public bool IsResolved { get; }

        public string? UnavailableReason { get; }

        public static ResolvedHarnessTrack UnresolvedPhone(
            string phonePath,
            string reason) =>
            new ResolvedHarnessTrack(
                "unresolved-phone:" + TrackPathNormalizer.NormalizePhonePath(phonePath),
                phonePath,
                phonePath,
                isResolved: false,
                unavailableReason: reason,
                sourcePhonePath: phonePath,
                phonePathIsCurrent: false);
    }
}
