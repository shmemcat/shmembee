using System;
using System.Collections.Generic;
using System.Linq;
using Shmembee.Application.Synchronization;
using Shmembee.Core.Paths;

namespace Shmembee.Application.Reconciliation
{
    public sealed class BaselineTombstoneMatcher
    {
        private readonly Dictionary<string, Queue<SynchronizationTrack>> matches;

        public BaselineTombstoneMatcher(IEnumerable<SynchronizationTrack> baselineTracks)
        {
            if (baselineTracks == null)
            {
                throw new ArgumentNullException(nameof(baselineTracks));
            }

            matches = baselineTracks
                .GroupBy(
                    track => TrackPathNormalizer.NormalizePhonePath(track.PhonePath),
                    StringComparer.OrdinalIgnoreCase)
                .Where(group => group
                    .Select(track => track.TrackId)
                    .Distinct(StringComparer.Ordinal)
                    .Count() == 1
                    && group
                        .Select(track => track.MusicBeeUrl)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count() == 1)
                .ToDictionary(
                    group => group.Key,
                    group => new Queue<SynchronizationTrack>(group),
                    StringComparer.OrdinalIgnoreCase);
        }

        public bool TryConsume(
            string phonePath,
            out SynchronizationTrack? baselineTrack)
        {
            string normalizedPath =
                TrackPathNormalizer.NormalizePhonePath(phonePath);
            if (matches.TryGetValue(normalizedPath, out Queue<SynchronizationTrack> queue)
                && queue.Count > 0)
            {
                baselineTrack = queue.Dequeue();
                return true;
            }

            baselineTrack = null;
            return false;
        }
    }
}
