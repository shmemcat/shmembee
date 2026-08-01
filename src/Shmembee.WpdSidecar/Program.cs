#nullable disable
using System;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;

namespace Shmembee.WpdSidecar
{
    internal static class Program
    {
        private static int Main()
        {
            var json = new JavaScriptSerializer
            {
                MaxJsonLength = 64 * 1024 * 1024
            };
            OperationResponse response;
            try
            {
                string input = Console.In.ReadToEnd();
                OperationRequest request = json.Deserialize<OperationRequest>(input)
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

            Console.Out.Write(json.Serialize(response));
            return response.Success ? 0 : 1;
        }
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
        public PlaylistContentResponse[] Playlists { get; set; }

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
