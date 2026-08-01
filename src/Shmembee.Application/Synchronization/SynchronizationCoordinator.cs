using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Shmembee.Application.Synchronization
{
    public sealed class SynchronizationCoordinator
    {
        private readonly IMusicBeePlaylistWriter musicBee;
        private readonly IPhonePlaylistWriter phone;
        private readonly ISynchronizationHistory history;

        public SynchronizationCoordinator(
            IMusicBeePlaylistWriter musicBee,
            IPhonePlaylistWriter phone,
            ISynchronizationHistory history)
        {
            this.musicBee = musicBee ?? throw new ArgumentNullException(nameof(musicBee));
            this.phone = phone ?? throw new ArgumentNullException(nameof(phone));
            this.history = history ?? throw new ArgumentNullException(nameof(history));
        }

        public SynchronizationApplyResult Apply(
            SynchronizationPlan plan,
            CancellationToken cancellationToken)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            cancellationToken.ThrowIfCancellationRequested();
            PlaylistState currentMusicBee = musicBee.Read(plan.MusicBeePlaylistUrl);
            PlaylistState currentPhone = phone.Read(plan.PhoneBackingName);
            if (!string.Equals(
                currentMusicBee.Checksum,
                plan.ExpectedMusicBeeChecksum,
                StringComparison.Ordinal)
                || currentPhone.Exists != plan.ExpectedPhoneExists
                || !string.Equals(
                    currentPhone.Checksum,
                    plan.ExpectedPhoneChecksum,
                    StringComparison.Ordinal))
            {
                return SynchronizationApplyResult.Stale(
                    currentMusicBee.Checksum,
                    currentPhone.Checksum);
            }

            PlaylistBackup? phoneBackup = null;
            IReadOnlyList<string> previousMusicBeeEntries = currentMusicBee.Entries.ToList();
            bool musicBeeChanged = false;
            bool phoneChanged = false;

            try
            {
                history.Started(plan);
                phoneBackup = phone.Backup(
                    plan.PhoneBackingName,
                    plan.OperationId);
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<string> proposedMusicBee = plan.Tracks
                    .Select(track => track.MusicBeeUrl)
                    .ToList();
                if (!musicBee.Replace(plan.MusicBeePlaylistUrl, proposedMusicBee))
                {
                    throw new InvalidOperationException(
                        "MusicBee rejected the proposed playlist.");
                }

                musicBeeChanged = true;
                cancellationToken.ThrowIfCancellationRequested();
                phoneChanged = true;
                phone.Replace(
                    plan.PhoneBackingName,
                    plan.Tracks.Select(track => track.PhonePath).ToList(),
                    cancellationToken);

                PlaylistState verifiedMusicBee = musicBee.Read(plan.MusicBeePlaylistUrl);
                PlaylistState verifiedPhone = phone.Read(plan.PhoneBackingName);
                string expectedMusicBee = PlaylistChecksum.Compute(proposedMusicBee);
                string expectedPhone = PlaylistChecksum.Compute(
                    plan.Tracks.Select(track => track.PhonePath));
                if (!string.Equals(
                    verifiedMusicBee.Checksum,
                    expectedMusicBee,
                    StringComparison.Ordinal)
                    || !string.Equals(
                        verifiedPhone.Checksum,
                        expectedPhone,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Post-write verification did not match the synchronization plan.");
                }

                history.Completed(plan, verifiedMusicBee, verifiedPhone);
                return SynchronizationApplyResult.Succeeded(
                    verifiedMusicBee,
                    verifiedPhone);
            }
            catch (Exception exception)
            {
                string rollbackError = Rollback(
                    plan,
                    previousMusicBeeEntries,
                    phoneBackup,
                    musicBeeChanged,
                    phoneChanged);
                string details = exception.Message;
                if (!string.IsNullOrEmpty(rollbackError))
                {
                    details += " Rollback error: " + rollbackError;
                }

                history.Failed(plan, details);
                return exception is OperationCanceledException
                    ? SynchronizationApplyResult.Cancelled(details)
                    : SynchronizationApplyResult.Failed(details);
            }
        }

        private string Rollback(
            SynchronizationPlan plan,
            IReadOnlyList<string> previousMusicBeeEntries,
            PlaylistBackup? phoneBackup,
            bool musicBeeChanged,
            bool phoneChanged)
        {
            var failures = new List<string>();
            if (phoneChanged && phoneBackup != null)
            {
                try
                {
                    phone.Restore(phoneBackup);
                    PlaylistState restoredPhone = phone.Read(plan.PhoneBackingName);
                    if (restoredPhone.Exists != phoneBackup.Existed
                        || (phoneBackup.Existed
                            && !string.Equals(
                                restoredPhone.Checksum,
                                plan.ExpectedPhoneChecksum,
                                StringComparison.Ordinal)))
                    {
                        failures.Add("Phone rollback verification failed.");
                    }
                }
                catch (Exception exception)
                {
                    failures.Add("Phone: " + exception.Message);
                }
            }

            if (musicBeeChanged)
            {
                try
                {
                    if (!musicBee.Replace(
                        plan.MusicBeePlaylistUrl,
                        previousMusicBeeEntries))
                    {
                        failures.Add("MusicBee rejected rollback.");
                    }
                    else
                    {
                        PlaylistState restoredMusicBee = musicBee.Read(
                            plan.MusicBeePlaylistUrl);
                        if (!string.Equals(
                            restoredMusicBee.Checksum,
                            plan.ExpectedMusicBeeChecksum,
                            StringComparison.Ordinal))
                        {
                            failures.Add("MusicBee rollback verification failed.");
                        }
                    }
                }
                catch (Exception exception)
                {
                    failures.Add("MusicBee: " + exception.Message);
                }
            }

            return string.Join(" ", failures);
        }
    }

    public sealed class SynchronizationApplyResult
    {
        private SynchronizationApplyResult(
            SynchronizationApplyStatus status,
            string details,
            PlaylistState? musicBeeState,
            PlaylistState? phoneState)
        {
            Status = status;
            Details = details;
            MusicBeeState = musicBeeState;
            PhoneState = phoneState;
        }

        public SynchronizationApplyStatus Status { get; }

        public string Details { get; }

        public PlaylistState? MusicBeeState { get; }

        public PlaylistState? PhoneState { get; }

        public static SynchronizationApplyResult Succeeded(
            PlaylistState musicBee,
            PlaylistState phone) =>
            new SynchronizationApplyResult(
                SynchronizationApplyStatus.Succeeded,
                "Synchronization verified.",
                musicBee,
                phone);

        public static SynchronizationApplyResult Stale(
            string musicBeeChecksum,
            string phoneChecksum) =>
            new SynchronizationApplyResult(
                SynchronizationApplyStatus.Stale,
                "Inputs changed before apply. MusicBee="
                    + musicBeeChecksum
                    + "; Phone="
                    + phoneChecksum,
                null,
                null);

        public static SynchronizationApplyResult Failed(string details) =>
            new SynchronizationApplyResult(
                SynchronizationApplyStatus.Failed,
                details,
                null,
                null);

        public static SynchronizationApplyResult Cancelled(string details) =>
            new SynchronizationApplyResult(
                SynchronizationApplyStatus.Cancelled,
                details,
                null,
                null);
    }

    public enum SynchronizationApplyStatus
    {
        Succeeded,
        Stale,
        Cancelled,
        Failed
    }
}
