#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shmembee.Application.Ports;

namespace Shmembee.Windows
{
    public sealed class WpdSidecarPlaylistTransport :
        IPlaylistFileTransport,
        IPhonePlaylistCatalogReader,
        IPhonePlaylistSnapshotReader,
        IProgressivePhoneMediaPathReader,
        IPhonePlaylistBackupTransport
    {
        private readonly string sidecarPath;
        private readonly string deviceName;
        private readonly string storageName;
        private readonly string folderPath;
        private readonly string mediaFolderPath;
        private readonly TimeSpan timeout;
        private readonly IWpdSidecarProcessRunner processRunner;
        private readonly string diagnosticsPath;
        private readonly string activityId = Guid.NewGuid().ToString("N");

        public WpdSidecarPlaylistTransport(
            string sidecarPath,
            string deviceName,
            string storageName,
            string folderPath,
            TimeSpan? timeout = null,
            IWpdSidecarProcessRunner processRunner = null,
            string mediaFolderPath = null,
            string diagnosticsPath = null)
        {
            this.sidecarPath = Require(sidecarPath, nameof(sidecarPath));
            this.deviceName = Require(deviceName, nameof(deviceName));
            this.storageName = Require(storageName, nameof(storageName));
            this.folderPath = Require(folderPath, nameof(folderPath));
            this.mediaFolderPath = string.IsNullOrWhiteSpace(mediaFolderPath)
                ? null
                : mediaFolderPath.Trim();
            this.timeout = timeout ?? TimeSpan.FromSeconds(30);
            if (this.timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            this.processRunner = processRunner ?? new WpdSidecarProcessRunner();
            this.diagnosticsPath = WpdDiagnosticJournal.ResolvePath(diagnosticsPath);
        }

        public WpdSidecarResponse Probe()
        {
            return Invoke("probe", null, null, false);
        }

        public IReadOnlyList<PhonePlaylistFile> ListPlaylists()
        {
            return Probe().EnumeratePlaylists();
        }

        public IReadOnlyList<PhonePlaylistContent> ReadPlaylistSnapshot()
        {
            WpdSidecarResponse response = Invoke(
                "snapshot-playlists",
                null,
                null,
                false);
            return response.DecodePlaylistSnapshot();
        }

        public IReadOnlyList<string> ReadMediaPaths()
        {
            return ReadMediaPaths(CancellationToken.None);
        }

        public IReadOnlyList<string> ReadMediaPaths(
            CancellationToken cancellationToken,
            IProgress<PhoneMediaTraversalProgress> progress = null)
        {
            if (mediaFolderPath == null)
            {
                throw new InvalidOperationException(
                    "A media folder is required to read phone media paths.");
            }

            WpdSidecarResponse response = Invoke(
                "snapshot-media-paths",
                null,
                null,
                false,
                mediaFolderPath,
                progress: progress,
                cancellationToken: cancellationToken);
            return response.DecodeMediaPaths();
        }

        public byte[] Read(string backingName)
        {
            ValidateBackingName(backingName);
            WpdSidecarResponse response = Invoke("read", backingName, null, true);
            if (!response.Success)
            {
                return null;
            }

            if (string.IsNullOrEmpty(response.ContentBase64))
            {
                return Array.Empty<byte>();
            }

            try
            {
                return Convert.FromBase64String(response.ContentBase64);
            }
            catch (FormatException exception)
            {
                throw new IOException(
                    "The WPD sidecar returned invalid base64 content.",
                    exception);
            }
        }

        public void Replace(string backingName, byte[] content)
        {
            ValidateBackingName(backingName);
            if (content == null)
            {
                throw new ArgumentNullException(nameof(content));
            }

            Invoke(
                "replace",
                backingName,
                Convert.ToBase64String(content),
                false);
        }

        public void Delete(string backingName)
        {
            ValidateBackingName(backingName);
            Invoke("delete", backingName, null, false);
        }

        public PhonePlaylistBackupResult CreatePlaylistBackup()
        {
            WpdSidecarResponse response = Invoke(
                "create-playlist-backup",
                null,
                null,
                false);
            if (string.IsNullOrWhiteSpace(response.BackupFolderName))
            {
                throw new IOException(
                    "The WPD sidecar returned no backup folder name.");
            }

            string[] copiedNames = response.CopiedNames ?? Array.Empty<string>();
            ValidateBackupHandle(response.BackupFolderName, copiedNames);
            return new PhonePlaylistBackupResult(
                new PhonePlaylistBackupHandle(
                    response.BackupFolderName,
                    copiedNames),
                copiedNames.Length);
        }

        public void DeletePlaylistBackup(PhonePlaylistBackupHandle handle)
        {
            if (handle == null)
            {
                throw new ArgumentNullException(nameof(handle));
            }

            string[] copiedNames = handle.CopiedBackingNames.ToArray();
            ValidateBackupHandle(handle.BackupFolderName, copiedNames);
            Invoke(
                "delete-playlist-backup",
                null,
                null,
                false,
                null,
                handle.BackupFolderName,
                copiedNames);
        }

        private WpdSidecarResponse Invoke(
            string operation,
            string backingName,
            string contentBase64,
            bool allowNotFound,
            string requestedFolder = null,
            string backupFolderName = null,
            string[] copiedNames = null,
            IProgress<PhoneMediaTraversalProgress> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            string operationId = Guid.NewGuid().ToString("N");
            var journal = new WpdDiagnosticJournal(
                diagnosticsPath,
                activityId,
                operationId);
            var request = new WpdSidecarRequest
            {
                ProtocolVersion = 1,
                OperationId = operationId,
                Operation = operation,
                Device = deviceName,
                Storage = storageName,
                Folder = requestedFolder ?? folderPath,
                Name = backingName,
                ContentBase64 = contentBase64,
                BackupFolderName = backupFolderName,
                CopiedNames = copiedNames,
                ActivityId = activityId,
                DiagnosticsPath = diagnosticsPath
            };
            if (progress != null || cancellationToken.CanBeCanceled)
            {
                request.ProgressProtocolVersion = 1;
            }
            journal.Write(
                "parent",
                "operation.start",
                WpdDiagnosticJournal.Data(
                    "operation", operation,
                    "device", deviceName,
                    "storage", storageName,
                    "folder", requestedFolder ?? folderPath,
                    "hasName", (backingName != null).ToString(CultureInfo.InvariantCulture),
                    "contentBytes", contentBase64 == null
                        ? null
                        : ((contentBase64.Length / 4) * 3).ToString(CultureInfo.InvariantCulture)));
            string requestJson = Serialize(request);
            WpdSidecarProcessResult result;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var streamingRunner = processRunner as IWpdSidecarStreamingProcessRunner;
                result = streamingRunner == null
                    ? RunLegacy(
                        sidecarPath,
                        requestJson,
                        timeout,
                        cancellationToken)
                    : streamingRunner.Run(
                        sidecarPath,
                        requestJson,
                        timeout,
                        record =>
                        {
                            if (progress != null
                                && record != null
                                && record.Version == 1
                                && string.Equals(
                                    record.OperationId,
                                    operationId,
                                    StringComparison.Ordinal)
                                && string.Equals(
                                    record.Stage,
                                    "snapshot-media-paths",
                                    StringComparison.Ordinal))
                            {
                                progress.Report(new PhoneMediaTraversalProgress(
                                    record.ObjectsScanned,
                                    record.FoldersCompleted,
                                    record.FoldersPending,
                                    record.MediaFilesFound));
                            }
                        },
                        cancellationToken);
            }
            catch (TimeoutException exception)
            {
                stopwatch.Stop();
                var data = WpdDiagnosticJournal.Data(
                    "operation", operation,
                    "elapsedMs", stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture),
                    "classification", "timeout-kill");
                foreach (System.Collections.DictionaryEntry item in exception.Data)
                {
                    data["process." + item.Key] = Convert.ToString(
                        item.Value,
                        CultureInfo.InvariantCulture);
                }
                WpdDiagnosticJournal.AddException(data, exception);
                journal.Write("parent", "process.timeout", data);
                throw new IOException(
                    "WPD sidecar operation "
                        + operationId
                        + " timed out after "
                        + timeout
                        + ".",
                    exception);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                || exception is System.ComponentModel.Win32Exception)
            {
                stopwatch.Stop();
                var data = WpdDiagnosticJournal.Data(
                    "operation", operation,
                    "elapsedMs", stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
                WpdDiagnosticJournal.AddException(data, exception);
                journal.Write("parent", "process.start-failure", data);
                throw new IOException(
                    "WPD sidecar could not be started: " + sidecarPath,
                    exception);
            }
            stopwatch.Stop();
            journal.Write(
                "parent",
                "process.exit",
                WpdDiagnosticJournal.Data(
                    "operation", operation,
                    "pid", result.ProcessId?.ToString(CultureInfo.InvariantCulture),
                    "exitCode", result.ExitCode.ToString(CultureInfo.InvariantCulture),
                    "elapsedMs", result.ElapsedMilliseconds?.ToString(
                        CultureInfo.InvariantCulture)));

            WpdSidecarResponse response = null;
            if (!string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                try
                {
                    response = Deserialize<WpdSidecarResponse>(
                        result.StandardOutput);
                }
                catch (InvalidOperationException exception)
                {
                    throw ProtocolFailure(
                        operationId,
                        "The helper returned malformed JSON.",
                        result,
                        exception);
                }
            }

            if (response == null)
            {
                throw ProtocolFailure(
                    operationId,
                    "The helper returned no response.",
                    result,
                    null);
            }

            if (!string.Equals(
                response.OperationId,
                operationId,
                StringComparison.Ordinal))
            {
                throw ProtocolFailure(
                    operationId,
                    "The helper response operation ID did not match the request.",
                    result,
                    null);
            }

            if (result.ExitCode != 0 || !response.Success)
            {
                if (allowNotFound
                    && string.Equals(
                        response.Stage,
                        "resolve-object",
                        StringComparison.Ordinal)
                    && response.Error != null
                    && response.Error.StartsWith(
                        "No exact WPD object named ",
                        StringComparison.Ordinal))
                {
                    return response;
                }

                string message = "WPD sidecar operation "
                    + operationId
                    + " failed";
                if (!string.IsNullOrWhiteSpace(response.Stage))
                {
                    message += " at " + response.Stage;
                }

                if (response.HResult.HasValue)
                {
                    message += " (" + response.HResult + ")";
                }

                if (!string.IsNullOrWhiteSpace(response.Error))
                {
                    message += ": " + response.Error;
                }

                throw new IOException(message + DiagnosticSuffix(response, result));
            }

            journal.Write(
                "parent",
                "operation.complete",
                WpdDiagnosticJournal.Data(
                    "operation", operation,
                    "stage", response.Stage));
            return response;
        }

        private WpdSidecarProcessResult RunLegacy(
            string executablePath,
            string standardInput,
            TimeSpan operationTimeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return processRunner.Run(executablePath, standardInput, operationTimeout);
        }

        private static IOException ProtocolFailure(
            string operationId,
            string message,
            WpdSidecarProcessResult result,
            Exception innerException)
        {
            string fullMessage = "WPD sidecar operation "
                + operationId
                + " failed: "
                + message
                + DiagnosticSuffix(null, result);
            return innerException == null
                ? new IOException(fullMessage)
                : new IOException(fullMessage, innerException);
        }

        private static string DiagnosticSuffix(
            WpdSidecarResponse response,
            WpdSidecarProcessResult result)
        {
            var details = new StringBuilder();
            if (response != null)
            {
                Append(details, "originalObjectId", response.OriginalObjectId);
                Append(details, "candidateObjectId", response.CandidateObjectId);
                Append(details, "candidateName", response.CandidateName);
            }

            Append(details, "stderr", result.StandardError);
            return details.Length == 0 ? string.Empty : " [" + details + "]";
        }

        private static void Append(StringBuilder details, string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (details.Length > 0)
            {
                details.Append("; ");
            }

            details.Append(name).Append('=').Append(value.Trim());
        }

        private static void ValidateBackingName(string backingName)
        {
            Require(backingName, nameof(backingName));
            if (!string.Equals(
                Path.GetFileName(backingName),
                backingName,
                StringComparison.Ordinal)
                || backingName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException(
                    "A conservative backing filename is required.",
                    nameof(backingName));
            }
        }

        private static void ValidateBackupHandle(
            string backupFolderName,
            IEnumerable<string> copiedNames)
        {
            Require(backupFolderName, nameof(backupFolderName));
            if (!string.Equals(
                Path.GetFileName(backupFolderName),
                backupFolderName,
                StringComparison.Ordinal)
                || !backupFolderName.StartsWith(
                    "shmembee-",
                    StringComparison.Ordinal)
                || backupFolderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException(
                    "A Shmembee backup folder name is required.",
                    nameof(backupFolderName));
            }

            if (copiedNames == null)
            {
                throw new ArgumentNullException(nameof(copiedNames));
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in copiedNames)
            {
                ValidateBackingName(name);
                string extension = Path.GetExtension(name);
                if ((!string.Equals(extension, ".m3u", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(
                            extension,
                            ".m3u8",
                            StringComparison.OrdinalIgnoreCase))
                    || !seen.Add(name))
                {
                    throw new ArgumentException(
                        "Backup copied names must be unique M3U filenames.",
                        nameof(copiedNames));
                }
            }
        }

        private static string Require(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "A non-empty value is required.",
                    parameterName);
            }

            return value;
        }

        private static string Serialize<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static T Deserialize<T>(string value)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(value)))
            {
                var settings = new DataContractJsonSerializerSettings
                {
                    MaxItemsInObjectGraph = 1000000
                };
                return (T)new DataContractJsonSerializer(
                    typeof(T),
                    settings).ReadObject(stream);
            }
        }
    }

    public interface IWpdSidecarProcessRunner
    {
        WpdSidecarProcessResult Run(
            string executablePath,
            string standardInput,
            TimeSpan timeout);
    }

    public interface IWpdSidecarStreamingProcessRunner : IWpdSidecarProcessRunner
    {
        WpdSidecarProcessResult Run(
            string executablePath,
            string standardInput,
            TimeSpan timeout,
            Action<WpdSidecarProgressRecord> progress,
            CancellationToken cancellationToken);
    }

    public sealed class WpdSidecarProcessRunner : IWpdSidecarStreamingProcessRunner
    {
        private const int MaximumOutputCharacters = 64 * 1024 * 1024;
        private const string ProgressPrefix = "SHMEMBEE_PROGRESS\t";

        public WpdSidecarProcessResult Run(
            string executablePath,
            string standardInput,
            TimeSpan timeout)
        {
            return Run(
                executablePath,
                standardInput,
                timeout,
                null,
                CancellationToken.None);
        }

        public WpdSidecarProcessResult Run(
            string executablePath,
            string standardInput,
            TimeSpan timeout,
            Action<WpdSidecarProgressRecord> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var elapsed = Stopwatch.StartNew();
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                process.Start();
                process.StandardInput.Write(standardInput);
                process.StandardInput.Close();
#pragma warning disable CA2016 // net48 has no cancellation-aware StreamReader overload.
                var outputTask = process.StandardOutput.ReadToEndAsync();
#pragma warning restore CA2016
                var errorTask = ReadErrorAsync(process.StandardError, progress);
                int timeoutMilliseconds = timeout.TotalMilliseconds >= int.MaxValue
                    ? int.MaxValue
                    : (int)Math.Ceiling(timeout.TotalMilliseconds);
                int waitSliceMilliseconds = 100;
                int waitedMilliseconds = 0;
                while (!process.WaitForExit(Math.Min(
                    waitSliceMilliseconds,
                    timeoutMilliseconds - waitedMilliseconds)))
                {
                    waitedMilliseconds += Math.Min(
                        waitSliceMilliseconds,
                        timeoutMilliseconds - waitedMilliseconds);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        KillAndWait(process);
                        outputTask.GetAwaiter().GetResult();
                        errorTask.GetAwaiter().GetResult();
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    if (waitedMilliseconds < timeoutMilliseconds)
                    {
                        continue;
                    }

                    int processId = process.Id;
                    var postKillElapsed = Stopwatch.StartNew();
                    bool killSucceeded = KillAndWait(process);
                    postKillElapsed.Stop();
                    var timeoutException = new TimeoutException();
                    timeoutException.Data["pid"] = processId;
                    timeoutException.Data["killAttempted"] = true;
                    timeoutException.Data["killSucceeded"] = killSucceeded;
                    timeoutException.Data["postKillWaitMs"] =
                        postKillElapsed.ElapsedMilliseconds;
                    throw timeoutException;
                }

                string output = outputTask.GetAwaiter().GetResult();
                string error = errorTask.GetAwaiter().GetResult();
                elapsed.Stop();
                if (output.Length > MaximumOutputCharacters
                    || error.Length > MaximumOutputCharacters)
                {
                    throw new IOException(
                        "The WPD sidecar exceeded the output size limit.");
                }

                return new WpdSidecarProcessResult(
                    process.ExitCode,
                    output,
                    error,
                    process.Id,
                    elapsed.ElapsedMilliseconds);
            }
        }

        private static bool KillAndWait(Process process)
        {
            bool killed = false;
            try
            {
                process.Kill();
                killed = true;
            }
            catch (InvalidOperationException)
            {
                // The process exited between detection and Kill.
            }
            process.WaitForExit();
            return killed;
        }

        private static async Task<string> ReadErrorAsync(
            StreamReader reader,
            Action<WpdSidecarProgressRecord> progress)
        {
            var error = new StringBuilder();
            string line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                WpdSidecarProgressRecord record;
                if (line.StartsWith(ProgressPrefix, StringComparison.Ordinal)
                    && TryDeserializeProgress(
                        line.Substring(ProgressPrefix.Length),
                        out record))
                {
                    progress?.Invoke(record);
                    continue;
                }

                if (error.Length + line.Length + Environment.NewLine.Length
                    > MaximumOutputCharacters)
                {
                    throw new IOException(
                        "The WPD sidecar exceeded the output size limit.");
                }
                error.AppendLine(line);
            }
            return error.ToString();
        }

        private static bool TryDeserializeProgress(
            string json,
            out WpdSidecarProgressRecord record)
        {
            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    record = (WpdSidecarProgressRecord)new DataContractJsonSerializer(
                        typeof(WpdSidecarProgressRecord)).ReadObject(stream);
                    return record != null;
                }
            }
            catch (Exception exception) when (
                exception is SerializationException
                || exception is InvalidOperationException)
            {
                record = null;
                return false;
            }
        }
    }

    public sealed class WpdSidecarProcessResult
    {
        public WpdSidecarProcessResult(
            int exitCode,
            string standardOutput,
            string standardError,
            int? processId = null,
            long? elapsedMilliseconds = null)
        {
            ExitCode = exitCode;
            StandardOutput = standardOutput ?? string.Empty;
            StandardError = standardError ?? string.Empty;
            ProcessId = processId;
            ElapsedMilliseconds = elapsedMilliseconds;
        }

        public int ExitCode { get; }

        public string StandardOutput { get; }

        public string StandardError { get; }
        public int? ProcessId { get; }
        public long? ElapsedMilliseconds { get; }
    }

    public sealed class WpdSidecarRequest
    {
        public int ProtocolVersion { get; set; }
        public string OperationId { get; set; }
        public string Operation { get; set; }
        public string Device { get; set; }
        public string Storage { get; set; }
        public string Folder { get; set; }
        public string Name { get; set; }
        public string ContentBase64 { get; set; }
        public string BackupFolderName { get; set; }
        public string[] CopiedNames { get; set; }
        public string ActivityId { get; set; }
        public string DiagnosticsPath { get; set; }
        public int? ProgressProtocolVersion { get; set; }
    }

    public sealed class WpdSidecarProgressRecord
    {
        public int Version { get; set; }
        public string OperationId { get; set; }
        public string Stage { get; set; }
        public int ObjectsScanned { get; set; }
        public int FoldersCompleted { get; set; }
        public int FoldersPending { get; set; }
        public int MediaFilesFound { get; set; }
        public long ElapsedMilliseconds { get; set; }
    }

    public sealed class WpdSidecarResponse
    {
        private static readonly string[] SupportedMediaExtensions =
        {
            ".aac", ".aif", ".aiff", ".alac", ".ape", ".dsf", ".flac",
            ".m4a", ".m4b", ".mp3", ".mp4", ".mpc", ".ogg", ".opus",
            ".wav", ".wma", ".wv"
        };
        public int ProtocolVersion { get; set; }
        public string OperationId { get; set; }
        public bool Success { get; set; }
        public bool Found { get; set; }
        public string ContentBase64 { get; set; }
        public string Error { get; set; }
        public string Stage { get; set; }
        public int? HResult { get; set; }
        public string OriginalObjectId { get; set; }
        public string CandidateObjectId { get; set; }
        public string CandidateName { get; set; }
        public string DeviceId { get; set; }
        public string StorageId { get; set; }
        public string FolderId { get; set; }
        public string ObjectId { get; set; }
        public string Sha256 { get; set; }
        public int? ByteCount { get; set; }
        public bool? RenameSupported { get; set; }
        public string[] Objects { get; set; }
        public string[] MediaPaths { get; set; }
        public string[] MediaPathsBase64 { get; set; }
        public WpdSidecarPlaylistContent[] Playlists { get; set; }
        public string BackupFolderName { get; set; }
        public string[] CopiedNames { get; set; }

        public IReadOnlyList<PhonePlaylistFile> EnumeratePlaylists()
        {
            var result = new List<PhonePlaylistFile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Objects == null)
            {
                return result;
            }

            foreach (string value in Objects)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                int separator = value.IndexOf('|');
                if (separator <= 0 || separator == value.Length - 1)
                {
                    continue;
                }

                string objectId = value.Substring(0, separator).Trim();
                string name = value.Substring(separator + 1);
                name = name.Trim();
                string extension = Path.GetExtension(name);
                if (objectId.Length > 0
                    && string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)
                    && (string.Equals(extension, ".m3u", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(extension, ".m3u8", StringComparison.OrdinalIgnoreCase))
                    && seen.Add(name))
                {
                    result.Add(new PhonePlaylistFile(objectId, name));
                }
            }

            return result;
        }

        public IReadOnlyList<string> EnumeratePlaylistNames()
        {
            var result = new List<string>();
            foreach (PhonePlaylistFile playlist in EnumeratePlaylists())
            {
                result.Add(playlist.BackingName);
            }

            return result;
        }

        public IReadOnlyList<PhonePlaylistContent> DecodePlaylistSnapshot()
        {
            var result = new List<PhonePlaylistContent>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (WpdSidecarPlaylistContent playlist in
                Playlists ?? Array.Empty<WpdSidecarPlaylistContent>())
            {
                if (playlist == null
                    || string.IsNullOrWhiteSpace(playlist.ObjectId)
                    || string.IsNullOrWhiteSpace(playlist.Name)
                    || !seen.Add(playlist.Name))
                {
                    continue;
                }

                string extension = Path.GetExtension(playlist.Name);
                if (!string.Equals(
                        Path.GetFileName(playlist.Name),
                        playlist.Name,
                        StringComparison.Ordinal)
                    || (!string.Equals(extension, ".m3u", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(
                            extension,
                            ".m3u8",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                try
                {
                    result.Add(new PhonePlaylistContent(
                        playlist.ObjectId,
                        playlist.Name,
                        Convert.FromBase64String(playlist.ContentBase64 ?? string.Empty)));
                }
                catch (FormatException exception)
                {
                    throw new IOException(
                        "The WPD sidecar returned invalid playlist content.",
                        exception);
                }
            }

            return result;
        }

        public IReadOnlyList<string> DecodeMediaPaths()
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IEnumerable<string> values = MediaPathsBase64 != null
                ? MediaPathsBase64.Select(DecodeUtf8Base64)
                : MediaPaths ?? Array.Empty<string>();
            foreach (string value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                string normalized = value.Trim().Replace('\\', '/').Trim('/');
                string extension = Path.GetExtension(normalized);
                if (normalized.Length == 0
                    || normalized == "."
                    || normalized == ".."
                    || normalized.StartsWith("../", StringComparison.Ordinal)
                    || normalized.Contains("/../")
                    || Path.IsPathRooted(normalized)
                    || ContainsColon(normalized)
                    || !IsSupportedMediaExtension(extension)
                    || !seen.Add(normalized))
                {
                    continue;
                }

                result.Add(normalized);
            }

            return result;
        }

        private static string DecodeUtf8Base64(string value)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(value ?? string.Empty));
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException(
                    "The WPD sidecar returned an invalid encoded media path.",
                    exception);
            }
        }

        private static bool IsSupportedMediaExtension(string extension) =>
            SupportedMediaExtensions.Contains(
                extension,
                StringComparer.OrdinalIgnoreCase);

        private static bool ContainsColon(string value)
        {
            foreach (char character in value)
            {
                if (character == ':')
                {
                    return true;
                }
            }

            return false;
        }
    }

    public sealed class WpdSidecarPlaylistContent
    {
        public string ObjectId { get; set; }
        public string Name { get; set; }
        public string ContentBase64 { get; set; }
        public int ByteCount { get; set; }
    }
}
