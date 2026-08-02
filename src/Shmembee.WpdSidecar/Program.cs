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
        private static int Main()
        {
            OperationResponse response;
            try
            {
                string input = Console.In.ReadToEnd();
                OperationRequest request = Deserialize<OperationRequest>(input)
                    ?? throw new InvalidOperationException("The JSON request is empty.");
                response = new WpdOperations().Execute(request);
            }
            catch (Exception exception)
            {
                var com = exception as COMException;
                response = OperationResponse.Failure(
                    null,
                    "request",
                    exception.Message,
                    com?.ErrorCode);
            }

            Console.Out.Write(Serialize(response));
            return response.Success ? 0 : 1;
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
