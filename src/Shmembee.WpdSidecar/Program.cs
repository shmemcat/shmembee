#nullable disable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Shmembee.WpdSidecar
{
    internal static class Program
    {
        private const string ProgressPrefix = "SHMEMBEE_PROGRESS\t";

        private static int Main()
        {
            OperationResponse response;
            OperationRequest request = null;
            WpdDiagnosticJournal journal = null;
            try
            {
                string input = Console.In.ReadToEnd();
                request = Deserialize<OperationRequest>(input)
                    ?? throw new InvalidOperationException("The JSON request is empty.");
                if (!string.IsNullOrWhiteSpace(request.DiagnosticsPath))
                {
                    journal = new WpdDiagnosticJournal(
                        request.DiagnosticsPath,
                        request.ActivityId,
                        request.OperationId);
                    journal.Write(
                        "sidecar.start",
                        WpdDiagnosticJournal.Data(
                            "operation", request.Operation,
                            "runtime", Environment.Version.ToString()));
                }
                Action<WpdOperations.MediaTraversalProgress> progress = null;
                if (request.ProgressProtocolVersion == 1)
                {
                    progress = value => WriteProgress(request.OperationId, value);
                }
                response = new WpdOperations(journal, progress).Execute(request);
            }
            catch (Exception exception)
            {
                var data = WpdDiagnosticJournal.Data("stage", "request");
                WpdDiagnosticJournal.AddException(data, exception);
                journal?.Write("operation.failure", data);
                response = OperationResponse.Failure(
                    request?.OperationId,
                    "request",
                    exception.Message,
                    DeepestComHResult(exception));
            }

            journal?.Write(
                "sidecar.exit",
                WpdDiagnosticJournal.Data(
                    "success", response.Success.ToString(),
                    "stage", response.Stage));
            Console.Out.Write(Serialize(response));
            return response.Success ? 0 : 1;
        }

        private static void WriteProgress(
            string operationId,
            WpdOperations.MediaTraversalProgress value)
        {
            var record = new MediaProgressRecord
            {
                Version = 1,
                OperationId = operationId,
                Stage = "snapshot-media-paths",
                ObjectsScanned = value.ObjectsScanned,
                FoldersCompleted = value.FoldersCompleted,
                FoldersPending = value.FoldersPending,
                MediaFilesFound = value.MediaFilesFound,
                ElapsedMilliseconds = value.ElapsedMilliseconds
            };
            Console.Error.WriteLine(ProgressPrefix + Serialize(record));
            Console.Error.Flush();
        }

        private static int? DeepestComHResult(Exception exception)
        {
            int? result = null;
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (current is COMException)
                {
                    result = current.HResult;
                }
            }
            return result;
        }

        private static string Serialize<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                CreateSerializer<T>().WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static T Deserialize<T>(string value)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(value)))
            {
                return (T)CreateSerializer<T>().ReadObject(stream);
            }
        }

        private static DataContractJsonSerializer CreateSerializer<T>() =>
            new DataContractJsonSerializer(
                typeof(T),
                new DataContractJsonSerializerSettings
                {
                    MaxItemsInObjectGraph = 1000000
                });
    }

    public sealed class OperationRequest
    {
        public string Operation { get; set; }
        public string OperationId { get; set; }
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

    public sealed class MediaProgressRecord
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

    public sealed class OperationResponse
    {
        public bool Success { get; set; }
        public string OperationId { get; set; }
        public string Stage { get; set; }
        public string Error { get; set; }
        public int? HResult { get; set; }
        public string DeviceId { get; set; }
        public string StorageId { get; set; }
        public string FolderId { get; set; }
        public string OriginalObjectId { get; set; }
        public string CandidateObjectId { get; set; }
        public string CandidateName { get; set; }
        public string ObjectId { get; set; }
        public string ContentBase64 { get; set; }
        public string Sha256 { get; set; }
        public int? ByteCount { get; set; }
        public bool? RenameSupported { get; set; }
        public string[] Objects { get; set; }
        public string[] MediaPaths { get; set; }
        public string[] MediaPathsBase64 { get; set; }
        public PlaylistContentResponse[] Playlists { get; set; }
        public string BackupFolderName { get; set; }
        public string[] CopiedNames { get; set; }

        public static OperationResponse Failure(
            string operationId,
            string stage,
            string error,
            int? hresult) =>
            new OperationResponse
            {
                OperationId = operationId,
                Stage = stage,
                Error = error,
                HResult = hresult,
            };
    }

    public sealed class PlaylistContentResponse
    {
        public string ObjectId { get; set; }
        public string Name { get; set; }
        public string ContentBase64 { get; set; }
        public int ByteCount { get; set; }
    }
}
