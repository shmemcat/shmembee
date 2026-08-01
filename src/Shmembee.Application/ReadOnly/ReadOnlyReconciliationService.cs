using System;
using System.Collections.Generic;
using System.Linq;
using Shmembee.Core.Playlists;
using Shmembee.Core.Reconciliation;
using Shmembee.Core.Resolution;

namespace Shmembee.Application.Reconciliation
{
    public sealed class ReadOnlyReconciliationService
    {
        private readonly TrackResolver resolver;
        private readonly ThreeWayPlaylistReconciler reconciler;

        public ReadOnlyReconciliationService(
            TrackResolver resolver,
            ThreeWayPlaylistReconciler reconciler)
        {
            this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            this.reconciler = reconciler
                ?? throw new ArgumentNullException(nameof(reconciler));
        }

        public ReadOnlyReconciliationResult Reconcile(
            PlaylistSnapshot commonBase,
            PlaylistSnapshot musicBee,
            IEnumerable<TrackReference> phoneEntries,
            IEnumerable<LibraryTrack> library,
            IReadOnlyDictionary<string, string>? approvedMappings = null)
        {
            List<LibraryTrack> indexedLibrary = library.ToList();
            List<TrackReference> indexedPhoneEntries = phoneEntries.ToList();
            var resolutions = indexedPhoneEntries
                .Select(reference => resolver.Resolve(
                    reference,
                    indexedLibrary,
                    approvedMappings))
                .ToList();

            if (resolutions.Any(result => result.Status != ResolutionStatus.Matched))
            {
                return ReadOnlyReconciliationResult.Blocked(resolutions);
            }

            var phoneSnapshot = new PlaylistSnapshot(
                musicBee.PlaylistId,
                musicBee.DisplayName,
                musicBee.BackingName,
                resolutions.Select((result, index) => new PlaylistEntry(
                    Guid.NewGuid(),
                    new TrackIdentity(result.Match!.Id),
                    indexedPhoneEntries[index].Path)),
                DateTimeOffset.UtcNow);

            return ReadOnlyReconciliationResult.Ready(
                resolutions,
                reconciler.Reconcile(commonBase, musicBee, phoneSnapshot));
        }
    }

    public sealed class ReadOnlyReconciliationResult
    {
        private ReadOnlyReconciliationResult(
            IReadOnlyList<ResolutionResult> resolutions,
            ReconciliationResult? reconciliation)
        {
            Resolutions = resolutions;
            Reconciliation = reconciliation;
        }

        public IReadOnlyList<ResolutionResult> Resolutions { get; }

        public ReconciliationResult? Reconciliation { get; }

        public bool IsBlocked => Reconciliation == null;

        public static ReadOnlyReconciliationResult Blocked(
            IReadOnlyList<ResolutionResult> resolutions) =>
            new ReadOnlyReconciliationResult(resolutions, null);

        public static ReadOnlyReconciliationResult Ready(
            IReadOnlyList<ResolutionResult> resolutions,
            ReconciliationResult reconciliation) =>
            new ReadOnlyReconciliationResult(resolutions, reconciliation);
    }
}
