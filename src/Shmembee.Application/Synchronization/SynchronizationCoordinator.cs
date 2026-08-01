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
                PlaylistState beforeMusicBeeWrite = musicBee.Read(
                    plan.MusicBeePlaylistUrl);
                if (!StateMatches(
                    beforeMusicBeeWrite,
                    expectedExists: true,
                    plan.ExpectedMusicBeeChecksum))
                {
                    TryRecordFailure(plan, "MusicBee changed before its write.");
                    return SynchronizationApplyResult.Stale(
                        beforeMusicBeeWrite.Checksum,
                        currentPhone.Checksum);
                }

                IReadOnlyList<string> proposedMusicBee = plan.Tracks
                    .Select(track => track.MusicBeeUrl)
                    .ToList();
                musicBeeChanged = true;
                if (!musicBee.Replace(plan.MusicBeePlaylistUrl, proposedMusicBee))
                {
                    throw new InvalidOperationException(
                        "MusicBee rejected the proposed playlist.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                PlaylistState beforePhoneWrite = phone.Read(plan.PhoneBackingName);
                if (!StateMatches(
                    beforePhoneWrite,
                    plan.ExpectedPhoneExists,
                    plan.ExpectedPhoneChecksum))
                {
                    throw new StaleDuringApplyException(
                        beforeMusicBeeWrite.Checksum,
                        beforePhoneWrite.Checksum);
                }

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
                    || !verifiedPhone.Exists
                    || !string.Equals(
                        verifiedPhone.Checksum,
                        expectedPhone,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Post-write verification did not match the synchronization plan.");
                }

                try
                {
                    history.Completed(plan, verifiedMusicBee, verifiedPhone);
                }
                catch (Exception exception)
                {
                    string details = "Both playlists were written and verified, but "
                        + "the accepted baseline could not be committed: "
                        + exception.Message;
                    TryRecordCommitPending(plan, details);
                    return SynchronizationApplyResult.CommitPending(
                        details,
                        verifiedMusicBee,
                        verifiedPhone);
                }

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

                TryRecordFailure(plan, details);
                if (exception is StaleDuringApplyException stale)
                {
                    return SynchronizationApplyResult.Stale(
                        stale.MusicBeeChecksum,
                        stale.PhoneChecksum);
                }

                return exception is OperationCanceledException
                    ? SynchronizationApplyResult.Cancelled(details)
                    : SynchronizationApplyResult.Failed(details);
            }
        }

        public SynchronizationLifecycleResult CreatePhone(
            string backingName,
            string expectedMissingChecksum,
            IReadOnlyList<string> phonePaths,
            CancellationToken cancellationToken)
        {
            if (phonePaths == null)
            {
                throw new ArgumentNullException(nameof(phonePaths));
            }

            PlaylistState current = phone.Read(backingName);
            if (current.Exists
                || !string.Equals(
                    current.Checksum,
                    expectedMissingChecksum,
                    StringComparison.Ordinal))
            {
                return SynchronizationLifecycleResult.Stale(
                    "The phone playlist changed before creation.");
            }

            PlaylistBackup backup = phone.Backup(backingName, Guid.NewGuid());
            try
            {
                phone.Replace(backingName, phonePaths, cancellationToken);
                PlaylistState verified = phone.Read(backingName);
                string expected = PlaylistChecksum.Compute(phonePaths);
                if (!verified.Exists
                    || !string.Equals(verified.Checksum, expected, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Phone playlist creation verification failed.");
                }

                return SynchronizationLifecycleResult.Succeeded(
                    "Phone playlist created and verified.",
                    null,
                    verified);
            }
            catch (Exception exception)
            {
                return LifecycleFailure(exception, () => phone.Restore(backup));
            }
        }

        public SynchronizationLifecycleResult CreateMusicBee(
            string playlistName,
            IReadOnlyList<string> musicBeeUrls,
            CancellationToken cancellationToken)
        {
            if (musicBeeUrls == null)
            {
                throw new ArgumentNullException(nameof(musicBeeUrls));
            }

            string? createdUrl = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                createdUrl = musicBee.Create(playlistName, musicBeeUrls);
                PlaylistState verified = musicBee.Read(createdUrl);
                string expected = PlaylistChecksum.Compute(musicBeeUrls);
                if (!verified.Exists
                    || !string.Equals(verified.Checksum, expected, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "MusicBee playlist creation verification failed.");
                }

                return SynchronizationLifecycleResult.Succeeded(
                    "MusicBee playlist created and verified.",
                    verified,
                    null,
                    createdUrl);
            }
            catch (Exception exception)
            {
                return LifecycleFailure(
                    exception,
                    () =>
                    {
                        if (createdUrl != null && !musicBee.Delete(createdUrl))
                        {
                            throw new InvalidOperationException(
                                "MusicBee rejected creation rollback.");
                        }
                    });
            }
        }

        public SynchronizationLifecycleResult DeletePhone(
            string backingName,
            string expectedChecksum,
            CancellationToken cancellationToken)
        {
            PlaylistState current = phone.Read(backingName);
            if (!current.Exists
                || !string.Equals(current.Checksum, expectedChecksum, StringComparison.Ordinal))
            {
                return SynchronizationLifecycleResult.Stale(
                    "The phone playlist changed before deletion.");
            }

            PlaylistBackup backup = phone.Backup(backingName, Guid.NewGuid());
            try
            {
                phone.Delete(backingName, cancellationToken);
                if (phone.Read(backingName).Exists)
                {
                    throw new InvalidOperationException(
                        "Phone playlist deletion verification failed.");
                }

                return SynchronizationLifecycleResult.Succeeded(
                    "Phone playlist deleted and verified.");
            }
            catch (Exception exception)
            {
                return LifecycleFailure(exception, () => phone.Restore(backup));
            }
        }

        public SynchronizationLifecycleResult DeleteMusicBee(
            string playlistUrl,
            string playlistName,
            string expectedChecksum,
            CancellationToken cancellationToken)
        {
            PlaylistState current = musicBee.Read(playlistUrl);
            if (!current.Exists
                || !string.Equals(current.Checksum, expectedChecksum, StringComparison.Ordinal))
            {
                return SynchronizationLifecycleResult.Stale(
                    "The MusicBee playlist changed before deletion.");
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!musicBee.Delete(playlistUrl))
                {
                    throw new InvalidOperationException(
                        "MusicBee rejected playlist deletion.");
                }

                return SynchronizationLifecycleResult.Succeeded(
                    "MusicBee playlist deleted.");
            }
            catch (Exception exception)
            {
                return LifecycleFailure(
                    exception,
                    () =>
                    {
                        string recreated = musicBee.Create(
                            playlistName,
                            current.Entries);
                        PlaylistState verified = musicBee.Read(recreated);
                        if (!string.Equals(
                            verified.Checksum,
                            current.Checksum,
                            StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                "MusicBee recreation verification failed.");
                        }
                    });
            }
        }

        private static SynchronizationLifecycleResult LifecycleFailure(
            Exception exception,
            Action rollback)
        {
            string details = exception.Message;
            try
            {
                rollback();
            }
            catch (Exception rollbackException)
            {
                details += " Rollback error: " + rollbackException.Message;
            }

            return exception is OperationCanceledException
                ? SynchronizationLifecycleResult.Cancelled(details)
                : SynchronizationLifecycleResult.Failed(details);
        }

        private void TryRecordFailure(SynchronizationPlan plan, string details)
        {
            try
            {
                history.Failed(plan, details);
            }
            catch (Exception)
            {
                // Preserve the primary endpoint result if local history also fails.
            }
        }

        private void TryRecordCommitPending(
            SynchronizationPlan plan,
            string details)
        {
            try
            {
                history.CommitPending(plan, details);
            }
            catch (Exception)
            {
                // Verified endpoints must not be rolled back for local history failure.
            }
        }

        private static bool StateMatches(
            PlaylistState state,
            bool expectedExists,
            string expectedChecksum) =>
            state.Exists == expectedExists
            && string.Equals(
                state.Checksum,
                expectedChecksum,
                StringComparison.Ordinal);

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

        public static SynchronizationApplyResult CommitPending(
            string details,
            PlaylistState musicBee,
            PlaylistState phone) =>
            new SynchronizationApplyResult(
                SynchronizationApplyStatus.CommitPending,
                details,
                musicBee,
                phone);

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
        CommitPending,
        Stale,
        Cancelled,
        Failed
    }

    public sealed class SynchronizationLifecycleResult
    {
        private SynchronizationLifecycleResult(
            SynchronizationApplyStatus status,
            string details,
            PlaylistState? musicBeeState,
            PlaylistState? phoneState,
            string? createdMusicBeePlaylistUrl)
        {
            Status = status;
            Details = details;
            MusicBeeState = musicBeeState;
            PhoneState = phoneState;
            CreatedMusicBeePlaylistUrl = createdMusicBeePlaylistUrl;
        }

        public SynchronizationApplyStatus Status { get; }

        public string Details { get; }

        public PlaylistState? MusicBeeState { get; }

        public PlaylistState? PhoneState { get; }

        public string? CreatedMusicBeePlaylistUrl { get; }

        public static SynchronizationLifecycleResult Succeeded(
            string details,
            PlaylistState? musicBeeState = null,
            PlaylistState? phoneState = null,
            string? createdMusicBeePlaylistUrl = null) =>
            new SynchronizationLifecycleResult(
                SynchronizationApplyStatus.Succeeded,
                details,
                musicBeeState,
                phoneState,
                createdMusicBeePlaylistUrl);

        public static SynchronizationLifecycleResult Stale(string details) =>
            new SynchronizationLifecycleResult(
                SynchronizationApplyStatus.Stale,
                details,
                null,
                null,
                null);

        public static SynchronizationLifecycleResult Failed(string details) =>
            new SynchronizationLifecycleResult(
                SynchronizationApplyStatus.Failed,
                details,
                null,
                null,
                null);

        public static SynchronizationLifecycleResult Cancelled(string details) =>
            new SynchronizationLifecycleResult(
                SynchronizationApplyStatus.Cancelled,
                details,
                null,
                null,
                null);
    }

    internal sealed class StaleDuringApplyException : Exception
    {
        public StaleDuringApplyException(
            string musicBeeChecksum,
            string phoneChecksum)
            : base("An endpoint changed during apply.")
        {
            MusicBeeChecksum = musicBeeChecksum;
            PhoneChecksum = phoneChecksum;
        }

        public string MusicBeeChecksum { get; }

        public string PhoneChecksum { get; }
    }
}
