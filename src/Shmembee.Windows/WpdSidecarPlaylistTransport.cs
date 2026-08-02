#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using Shmembee.Application.Ports;

namespace Shmembee.Windows
{
    public sealed class WpdSidecarPlaylistTransport :
        IPlaylistFileTransport,
        IPhonePlaylistCatalogReader,
        IPhonePlaylistSnapshotReader,
        IPhoneMediaPathReader
    {
        private readonly string sidecarPath;
        private readonly string deviceName;
        private readonly string storageName;
        private readonly string folderPath;
        private readonly string mediaFolderPath;
        private readonly TimeSpan timeout;
        private readonly IWpdSidecarProcessRunner processRunner;

        public WpdSidecarPlaylistTransport(
            string sidecarPath,
            string deviceName,
            string storageName,
            string folderPath,
            TimeSpan? timeout = null,
            IWpdSidecarProcessRunner processRunner = null,
            string mediaFolderPath = null)
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
                mediaFolderPath);
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

        private WpdSidecarResponse Invoke(
            string operation,
            string backingName,
            string contentBase64,
            bool allowNotFound,
            string requestedFolder = null)
        {
            string operationId = Guid.NewGuid().ToString("N");
            var request = new WpdSidecarRequest
            {
                ProtocolVersion = 1,
                OperationId = operationId,
                Operation = operation,
                Device = deviceName,
                Storage = storageName,
                Folder = requestedFolder ?? folderPath,
                Name = backingName,
                ContentBase64 = contentBase64
            };
            string requestJson = Serialize(request);
            WpdSidecarProcessResult result;
            try
            {
                result = processRunner.Run(sidecarPath, requestJson, timeout);
            }
            catch (TimeoutException exception)
            {
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
                throw new IOException(
                    "WPD sidecar could not be started: " + sidecarPath,
                    exception);
            }

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

            return response;
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

    public sealed class WpdSidecarProcessRunner : IWpdSidecarProcessRunner
    {
        private const int MaximumOutputCharacters = 64 * 1024 * 1024;

        public WpdSidecarProcessResult Run(
            string executablePath,
            string standardInput,
            TimeSpan timeout)
        {
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
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                int timeoutMilliseconds = timeout.TotalMilliseconds >= int.MaxValue
                    ? int.MaxValue
                    : (int)Math.Ceiling(timeout.TotalMilliseconds);
                if (!process.WaitForExit(timeoutMilliseconds))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                        // The process exited between timeout detection and Kill.
                    }

                    process.WaitForExit();
                    throw new TimeoutException();
                }

                string output = outputTask.GetAwaiter().GetResult();
                string error = errorTask.GetAwaiter().GetResult();
                if (output.Length > MaximumOutputCharacters
                    || error.Length > MaximumOutputCharacters)
                {
                    throw new IOException(
                        "The WPD sidecar exceeded the output size limit.");
                }

                return new WpdSidecarProcessResult(
                    process.ExitCode,
                    output,
                    error);
            }
        }
    }

    public sealed class WpdSidecarProcessResult
    {
        public WpdSidecarProcessResult(
            int exitCode,
            string standardOutput,
            string standardError)
        {
            ExitCode = exitCode;
            StandardOutput = standardOutput ?? string.Empty;
            StandardError = standardError ?? string.Empty;
        }

        public int ExitCode { get; }

        public string StandardOutput { get; }

        public string StandardError { get; }
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
