using System;
using System.Globalization;
using System.IO;
using System.Text;
using Shmembee.Application.Synchronization;

namespace Shmembee.Infrastructure.Playlists
{
    public sealed class MobilePlaylistExportSession
    {
        private readonly object logLock = new object();
        private readonly Func<DateTimeOffset> now;

        public MobilePlaylistExportSession(
            string rootDirectory,
            string backupDirectory,
            Func<DateTimeOffset>? now = null)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                throw new ArgumentException(
                    "A mobile playlist export directory is required.",
                    nameof(rootDirectory));
            }

            if (string.IsNullOrWhiteSpace(backupDirectory))
            {
                throw new ArgumentException(
                    "A generated-playlist backup directory is required.",
                    nameof(backupDirectory));
            }

            this.now = now ?? (() => DateTimeOffset.Now);
            string root = Path.GetFullPath(rootDirectory);
            Directory.CreateDirectory(root);
            OutputDirectory = CreateUniqueDirectory(root, this.now());
            LogDirectory = Path.Combine(OutputDirectory, "log");
            Directory.CreateDirectory(LogDirectory);
            LogPath = Path.Combine(LogDirectory, "log.txt");
            RunId = Guid.NewGuid();
            Writer = new FileSystemPhonePlaylistWriter(
                OutputDirectory,
                backupDirectory,
                new DeterministicM3uWriter(pathPrefix: "/storage/emulated/0/"));
            Write("Run", "Mobile playlist export started.");
            Write("Run", "Output directory: " + OutputDirectory);
            Write("Run", "Diagnostic log: " + LogPath);
        }

        public Guid RunId { get; }

        public string OutputDirectory { get; }

        public string LogDirectory { get; }

        public string LogPath { get; }

        public IPhonePlaylistWriter Writer { get; }

        public void Write(string stage, string message)
        {
            string timestamp = now().ToString("O", CultureInfo.InvariantCulture);
            string normalizedMessage = (message ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Replace("\n", Environment.NewLine + "    ");
            string line = timestamp
                + " [" + RunId.ToString("D") + "]"
                + " [" + (string.IsNullOrWhiteSpace(stage) ? "Diagnostic" : stage) + "] "
                + normalizedMessage
                + Environment.NewLine;
            lock (logLock)
            {
                File.AppendAllText(LogPath, line, new UTF8Encoding(false));
            }
        }

        private static string CreateUniqueDirectory(
            string rootDirectory,
            DateTimeOffset timestamp)
        {
            string name = timestamp.ToString(
                "yyyy-MM-dd HH-mm-ss",
                CultureInfo.InvariantCulture);
            string candidate = Path.Combine(rootDirectory, name);
            int suffix = 2;
            while (Directory.Exists(candidate) || File.Exists(candidate))
            {
                candidate = Path.Combine(
                    rootDirectory,
                    name + "-" + suffix.ToString("00", CultureInfo.InvariantCulture));
                suffix++;
            }

            Directory.CreateDirectory(candidate);
            return candidate;
        }
    }
}
