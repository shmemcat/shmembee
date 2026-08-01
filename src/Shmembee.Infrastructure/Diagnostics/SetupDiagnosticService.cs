using System;
using System.Collections.Generic;
using System.IO;

namespace Shmembee.Infrastructure.Diagnostics
{
    public enum SetupDiagnosticStatus
    {
        Passed,
        Failed
    }

    public sealed class SetupDiagnosticCheckResult
    {
        public SetupDiagnosticCheckResult(
            string name,
            SetupDiagnosticStatus status,
            string details)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Status = status;
            Details = details ?? string.Empty;
        }

        public string Name { get; }

        public SetupDiagnosticStatus Status { get; }

        public string Details { get; }
    }

    public sealed class SetupDiagnosticResult
    {
        public SetupDiagnosticResult(IReadOnlyList<SetupDiagnosticCheckResult> checks)
        {
            Checks = checks ?? throw new ArgumentNullException(nameof(checks));
        }

        public IReadOnlyList<SetupDiagnosticCheckResult> Checks { get; }

        public bool IsReady
        {
            get
            {
                foreach (SetupDiagnosticCheckResult check in Checks)
                {
                    if (check.Status != SetupDiagnosticStatus.Passed)
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }

    public sealed class SetupDiagnosticService
    {
        private readonly string storageDirectory;
        private readonly string databasePath;
        private readonly string backupDirectory;
        private readonly string sidecarPath;
        private readonly Func<SetupDiagnosticCheckResult> phoneProbe;
        private readonly Func<string, bool> directoryCheck;
        private readonly Func<string, bool> fileCheck;

        public SetupDiagnosticService(
            string storageDirectory,
            string databasePath,
            string backupDirectory,
            string sidecarPath,
            Func<SetupDiagnosticCheckResult> phoneProbe,
            Func<string, bool>? directoryCheck = null,
            Func<string, bool>? fileCheck = null)
        {
            this.storageDirectory = Require(storageDirectory, nameof(storageDirectory));
            this.databasePath = Require(databasePath, nameof(databasePath));
            this.backupDirectory = Require(backupDirectory, nameof(backupDirectory));
            this.sidecarPath = Require(sidecarPath, nameof(sidecarPath));
            this.phoneProbe = phoneProbe
                ?? throw new ArgumentNullException(nameof(phoneProbe));
            this.directoryCheck = directoryCheck ?? CheckWritableDirectory;
            this.fileCheck = fileCheck ?? File.Exists;
        }

        public SetupDiagnosticResult Run()
        {
            var checks = new List<SetupDiagnosticCheckResult>
            {
                RunCheck(
                    "storage",
                    () => directoryCheck(storageDirectory),
                    storageDirectory),
                RunCheck(
                    "database",
                    () => CheckDatabasePath(databasePath),
                    databasePath),
                RunCheck(
                    "backup",
                    () => directoryCheck(backupDirectory),
                    backupDirectory),
                RunCheck(
                    "sidecar",
                    () => fileCheck(sidecarPath),
                    sidecarPath),
                RunPhoneProbe()
            };
            return new SetupDiagnosticResult(checks);
        }

        private SetupDiagnosticCheckResult RunPhoneProbe()
        {
            try
            {
                return phoneProbe()
                    ?? new SetupDiagnosticCheckResult(
                        "phone",
                        SetupDiagnosticStatus.Failed,
                        "The phone probe returned no result.");
            }
            catch (Exception exception)
            {
                return new SetupDiagnosticCheckResult(
                    "phone",
                    SetupDiagnosticStatus.Failed,
                    exception.Message);
            }
        }

        private static SetupDiagnosticCheckResult RunCheck(
            string name,
            Func<bool> check,
            string details)
        {
            try
            {
                return new SetupDiagnosticCheckResult(
                    name,
                    check()
                        ? SetupDiagnosticStatus.Passed
                        : SetupDiagnosticStatus.Failed,
                    details);
            }
            catch (Exception exception)
            {
                return new SetupDiagnosticCheckResult(
                    name,
                    SetupDiagnosticStatus.Failed,
                    exception.Message);
            }
        }

        private bool CheckDatabasePath(string path)
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            return !string.IsNullOrEmpty(directory) && directoryCheck(directory);
        }

        private static bool CheckWritableDirectory(string path)
        {
            Directory.CreateDirectory(path);
            string probePath = Path.Combine(
                path,
                ".shmembee-write-probe-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (File.Create(probePath))
                {
                }

                return true;
            }
            finally
            {
                if (File.Exists(probePath))
                {
                    File.Delete(probePath);
                }
            }
        }

        private static string Require(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "A non-empty path is required.",
                    parameterName);
            }

            return value;
        }
    }
}
