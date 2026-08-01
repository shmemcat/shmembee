using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Shmembee.Core.Reconciliation;

namespace Shmembee.Application.Desktop
{
    public enum PlaylistWorkflowStatus
    {
        Unchanged,
        MusicBeeChanged,
        PhoneChanged,
        SameChange,
        Conflict,
        MissingBaseline,
        UnresolvedTracks,
        MissingEndpoint,
        SetupError
    }

    public enum WorkflowWarningSeverity
    {
        Information,
        Warning,
        Error
    }

    public sealed class WorkflowWarning
    {
        public WorkflowWarning(
            string code,
            WorkflowWarningSeverity severity,
            string message)
        {
            Code = Require(code, nameof(code));
            Severity = severity;
            Message = Require(message, nameof(message));
        }

        public string Code { get; }

        public WorkflowWarningSeverity Severity { get; }

        public string Message { get; }

        private static string Require(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("A non-empty value is required.", parameterName);
            }

            return value;
        }
    }

    public sealed class PlaylistWorkflowSummary
    {
        public PlaylistWorkflowSummary(
            string playlistId,
            string displayName,
            string phoneBackingName,
            PlaylistWorkflowStatus status,
            int musicBeeTrackCount,
            int phoneTrackCount,
            int unresolvedTrackCount,
            DateTimeOffset? lastAcceptedUtc,
            IEnumerable<WorkflowWarning>? warnings = null)
        {
            PlaylistId = playlistId;
            DisplayName = displayName;
            PhoneBackingName = phoneBackingName;
            Status = status;
            MusicBeeTrackCount = musicBeeTrackCount;
            PhoneTrackCount = phoneTrackCount;
            UnresolvedTrackCount = unresolvedTrackCount;
            LastAcceptedUtc = lastAcceptedUtc;
            Warnings = new ReadOnlyCollection<WorkflowWarning>(
                new List<WorkflowWarning>(warnings ?? Enumerable.Empty<WorkflowWarning>()));
        }

        public string PlaylistId { get; }

        public string DisplayName { get; }

        public string PhoneBackingName { get; }

        public PlaylistWorkflowStatus Status { get; }

        public int MusicBeeTrackCount { get; }

        public int PhoneTrackCount { get; }

        public int UnresolvedTrackCount { get; }

        public DateTimeOffset? LastAcceptedUtc { get; }

        public IReadOnlyList<WorkflowWarning> Warnings { get; }

        public bool CanApply =>
            UnresolvedTrackCount == 0
            && Status != PlaylistWorkflowStatus.Unchanged
            && Status != PlaylistWorkflowStatus.Conflict
            && Status != PlaylistWorkflowStatus.MissingBaseline
            && Status != PlaylistWorkflowStatus.MissingEndpoint
            && Status != PlaylistWorkflowStatus.SetupError;

        public static PlaylistWorkflowStatus FromOutcome(
            ReconciliationOutcome outcome,
            bool requiresReview)
        {
            if (requiresReview)
            {
                return PlaylistWorkflowStatus.Conflict;
            }

            switch (outcome)
            {
                case ReconciliationOutcome.Unchanged:
                    return PlaylistWorkflowStatus.Unchanged;
                case ReconciliationOutcome.MusicBeeOnly:
                    return PlaylistWorkflowStatus.MusicBeeChanged;
                case ReconciliationOutcome.PhoneOnly:
                    return PlaylistWorkflowStatus.PhoneChanged;
                case ReconciliationOutcome.SameChange:
                    return PlaylistWorkflowStatus.SameChange;
                default:
                    return PlaylistWorkflowStatus.Conflict;
            }
        }
    }
}
