#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using Vanara.PInvoke;
using static Vanara.PInvoke.Ole32;
using static Vanara.PInvoke.PortableDeviceApi;

namespace Shmembee.WpdSidecar
{
    internal sealed class WpdOperations
    {
        private const string DeviceRoot = "DEVICE";
        private const string BackupRootName = "backup";
        private const string BackupFolderPrefix = "shmembee-";
        private const uint DeleteNoRecursion = 0;
        private static readonly char[] FolderSeparators = { '/', '\\' };
        private static PROPERTYKEY ObjectName =
            Key(new Guid("EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C"), 4);
        private static PROPERTYKEY ObjectParentId =
            Key(new Guid("EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C"), 3);
        private static PROPERTYKEY ObjectOriginalFileName =
            Key(new Guid("EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C"), 12);
        private static PROPERTYKEY ObjectSize =
            Key(new Guid("EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C"), 11);
        private static PROPERTYKEY ObjectFormat =
            Key(new Guid("EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C"), 6);
        private static PROPERTYKEY ObjectContentType =
            Key(new Guid("EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C"), 7);
        private static PROPERTYKEY DefaultResource =
            Key(new Guid("E81E79BE-34F0-41BF-B53F-F1A06AE87842"), 0);
        private static readonly Guid GenericFile =
            new Guid("0085E0A6-8D34-45D7-BC5C-447E59C73D48");
        private static readonly Guid FolderContentType =
            new Guid("27E2E392-A111-48E0-AB0C-E17705A05F85");
        private static readonly Guid PropertiesOnlyFormat =
            new Guid("30000000-AE6C-4804-98BA-C57B46965FE7");
        private const int MaximumMediaFolderDepth = 64;
        private const int MaximumMediaObjectCount = 250000;
        private static readonly Guid UnspecifiedFormat = Guid.Empty;

        private string stage;

        public OperationResponse Execute(OperationRequest request)
        {
            var response = new OperationResponse { OperationId = request.OperationId };
            try
            {
                Validate(request);
                stage = "resolve-device";
                using (var session = WpdSession.Open(request.Device))
                {
                    response.DeviceId = session.DeviceId;
                    stage = "resolve-storage";
                    response.StorageId = ResolveExact(session, DeviceRoot, request.Storage);
                    stage = "resolve-folder";
                    response.FolderId = ResolvePath(session, response.StorageId, request.Folder);

                    switch (request.Operation.ToLowerInvariant())
                    {
                        case "probe":
                            Probe(session, response.FolderId, request.Name, response);
                            break;
                        case "snapshot-playlists":
                            SnapshotPlaylists(session, response.FolderId, response);
                            break;
                        case "snapshot-media-paths":
                            SnapshotMediaPaths(
                                session,
                                response.FolderId,
                                request.Folder,
                                response);
                            break;
                        case "read":
                            Read(session, response.FolderId, request.Name, response);
                            break;
                        case "replace":
                            Replace(session, response.FolderId, request.Name,
                                Convert.FromBase64String(request.ContentBase64), response);
                            break;
                        case "delete":
                            Delete(session, response.FolderId, request.Name, response);
                            break;
                        case "create-playlist-backup":
                            CreatePlaylistBackup(
                                session,
                                response.FolderId,
                                response);
                            break;
                        case "delete-playlist-backup":
                            DeletePlaylistBackup(
                                session,
                                response.FolderId,
                                request.BackupFolderName,
                                request.CopiedNames,
                                response);
                            break;
                        default:
                            throw new ArgumentException("Unknown operation: " + request.Operation);
                    }
                }

                response.Success = true;
                response.Stage = "complete";
            }
            catch (Exception exception)
            {
                response.Success = false;
                response.Stage = stage ?? "request";
                response.Error = exception.Message;
                response.HResult = exception is COMException ? exception.HResult : (int?)null;
            }

            return response;
        }

        private void Probe(WpdSession session, string folderId, string name,
            OperationResponse response)
        {
            stage = "enumerate-folder";
            response.Objects = session.Children(folderId)
                .Select(id => id + "|" + session.Name(id))
                .ToArray();
            if (!string.IsNullOrWhiteSpace(name))
            {
                response.OriginalObjectId = ResolveOptionalExact(session, folderId, name);
                if (response.OriginalObjectId != null)
                {
                    byte[] bytes = ReadById(session, response.OriginalObjectId);
                    response.Sha256 = Hash(bytes);
                    response.ByteCount = bytes.Length;
                }
            }
        }

        private void Read(WpdSession session, string folderId, string name,
            OperationResponse response)
        {
            stage = "resolve-object";
            response.ObjectId = ResolveExact(session, folderId, name);
            byte[] bytes = ReadById(session, response.ObjectId);
            response.ContentBase64 = Convert.ToBase64String(bytes);
            response.Sha256 = Hash(bytes);
            response.ByteCount = bytes.Length;
        }

        private void SnapshotPlaylists(
            WpdSession session,
            string folderId,
            OperationResponse response)
        {
            stage = "snapshot-playlists";
            var playlists = new List<PlaylistContentResponse>();
            foreach (string objectId in session.Children(folderId))
            {
                string name = session.Name(objectId);
                string extension = Path.GetExtension(name);
                if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)
                    || (!string.Equals(extension, ".m3u", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(
                            extension,
                            ".m3u8",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                byte[] bytes = ReadById(session, objectId);
                playlists.Add(new PlaylistContentResponse
                {
                    ObjectId = objectId,
                    Name = name,
                    ContentBase64 = Convert.ToBase64String(bytes),
                    ByteCount = bytes.Length
                });
            }

            response.Playlists = playlists.ToArray();
        }

        private void CreatePlaylistBackup(
            WpdSession session,
            string playlistFolderId,
            OperationResponse response)
        {
            stage = "snapshot-backup-source";
            var snapshot = new List<BackupFile>();
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (string objectId in session.Children(playlistFolderId))
            {
                string name = session.Name(objectId);
                if (!IsPlaylistFileName(name))
                {
                    continue;
                }

                if (!seenNames.Add(name))
                {
                    throw new InvalidOperationException(
                        "Multiple direct-child playlist objects named '"
                            + name + "' cannot be backed up safely.");
                }

                snapshot.Add(new BackupFile(name, ReadById(session, objectId)));
            }

            string backupRootId = null;
            string backupFolderId = null;
            var copiedNames = new List<string>();
            try
            {
                stage = "create-backup-root";
                backupRootId = ResolveOptionalExact(
                    session,
                    playlistFolderId,
                    BackupRootName);
                if (backupRootId == null)
                {
                    backupRootId = session.CreateFolder(
                        playlistFolderId,
                        BackupRootName);
                }
                else if (!session.Info(backupRootId).IsFolder)
                {
                    throw new InvalidOperationException(
                        "The fixed backup root exists but is not a folder.");
                }

                stage = "create-backup-folder";
                response.BackupFolderName = BackupFolderPrefix
                    + DateTime.UtcNow.ToString(
                        "yyyyMMdd-HHmmss-fffffff",
                        System.Globalization.CultureInfo.InvariantCulture)
                    + "-"
                    + Guid.NewGuid().ToString("N");
                if (ResolveOptionalExact(
                    session,
                    backupRootId,
                    response.BackupFolderName) != null)
                {
                    throw new InvalidOperationException(
                        "The generated backup folder already exists.");
                }
                backupFolderId = session.CreateFolder(
                    backupRootId,
                    response.BackupFolderName);

                foreach (BackupFile file in snapshot)
                {
                    stage = "copy-backup-file";
                    string copiedId = session.Create(
                        backupFolderId,
                        file.Name,
                        file.Bytes);
                    copiedNames.Add(file.Name);
                    stage = "verify-backup-file";
                    Verify(session, copiedId, file.Bytes);
                    string resolvedId = ResolveExact(
                        session,
                        backupFolderId,
                        file.Name);
                    if (!string.Equals(copiedId, resolvedId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Backup readback resolved a different object.");
                    }
                }

                response.CopiedNames = copiedNames.ToArray();
            }
            catch
            {
                CleanupBackup(
                    session,
                    playlistFolderId,
                    backupRootId,
                    backupFolderId,
                    copiedNames,
                    preservePrimaryFailure: true);
                throw;
            }
        }

        private void DeletePlaylistBackup(
            WpdSession session,
            string playlistFolderId,
            string backupFolderName,
            string[] copiedNames,
            OperationResponse response)
        {
            ValidateBackupHandle(backupFolderName, copiedNames);
            stage = "resolve-backup-root";
            string backupRootId = ResolveOptionalExact(
                session,
                playlistFolderId,
                BackupRootName);
            if (backupRootId == null)
            {
                return;
            }
            if (!session.Info(backupRootId).IsFolder)
            {
                throw new InvalidOperationException(
                    "The fixed backup root is not a folder.");
            }

            stage = "resolve-backup-folder";
            string backupFolderId = ResolveOptionalExact(
                session,
                backupRootId,
                backupFolderName);
            if (backupFolderId == null)
            {
                return;
            }
            if (!session.Info(backupFolderId).IsFolder)
            {
                throw new InvalidOperationException(
                    "The requested backup object is not a folder.");
            }

            CleanupBackup(
                session,
                playlistFolderId,
                backupRootId,
                backupFolderId,
                copiedNames,
                preservePrimaryFailure: false);
            response.BackupFolderName = backupFolderName;
            response.CopiedNames = copiedNames;
        }

        private void CleanupBackup(
            WpdSession session,
            string playlistFolderId,
            string backupRootId,
            string backupFolderId,
            IEnumerable<string> copiedNames,
            bool preservePrimaryFailure)
        {
            try
            {
                if (backupFolderId != null)
                {
                    foreach (string name in copiedNames)
                    {
                        stage = "delete-backup-file";
                        string objectId = ResolveOptionalExact(
                            session,
                            backupFolderId,
                            name);
                        if (objectId != null)
                        {
                            session.Delete(objectId);
                        }
                    }

                    stage = "delete-backup-folder";
                    if (!session.Children(backupFolderId).Any())
                    {
                        session.Delete(backupFolderId);
                    }
                    else if (!preservePrimaryFailure)
                    {
                        throw new InvalidOperationException(
                            "The backup folder contains unexpected objects and was preserved.");
                    }
                }

            }
            catch when (preservePrimaryFailure)
            {
                // Keep the original creation failure. Cleanup never traverses or
                // deletes anything outside the newly-created backup folder.
            }
        }

        private sealed class BackupFile
        {
            public BackupFile(string name, byte[] bytes)
            {
                Name = name;
                Bytes = bytes;
            }

            public string Name { get; }

            public byte[] Bytes { get; }
        }

        private void SnapshotMediaPaths(
            WpdSession session,
            string folderId,
            string folderPath,
            OperationResponse response)
        {
            stage = "snapshot-media-paths";
            string normalizedRoot = NormalizeRelativePath(folderPath);
            var paths = new List<string>();
            var visitedFolders = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<MediaFolder>();
            pending.Push(new MediaFolder(folderId, normalizedRoot, 0));
            int objectCount = 0;
            while (pending.Count > 0)
            {
                MediaFolder folder = pending.Pop();
                if (folder.Depth > MaximumMediaFolderDepth)
                {
                    throw new InvalidOperationException(
                        "The phone media folder exceeds the maximum supported depth of "
                            + MaximumMediaFolderDepth + ".");
                }

                if (!visitedFolders.Add(folder.ObjectId))
                {
                    throw new InvalidOperationException(
                        "A cycle was detected while enumerating WPD folder "
                            + folder.ObjectId + ".");
                }

                foreach (string objectId in session.Children(folder.ObjectId))
                {
                    objectCount++;
                    if (objectCount > MaximumMediaObjectCount)
                    {
                        throw new InvalidOperationException(
                            "The phone media folder contains more than "
                                + MaximumMediaObjectCount + " objects.");
                    }

                    WpdSession.WpdObjectInfo info = session.Info(objectId);
                    string name = info.Name;
                    if (!IsSafePathSegment(name))
                    {
                        throw new InvalidOperationException(
                            "WPD object '" + objectId
                                + "' has an unsafe name: " + name);
                    }

                    string relativePath = folder.RelativePath.Length == 0
                        ? name
                        : folder.RelativePath + "/" + name;
                    if (info.IsFolder)
                    {
                        pending.Push(new MediaFolder(
                            objectId,
                            relativePath,
                            folder.Depth + 1));
                    }
                    else
                    {
                        paths.Add(relativePath);
                    }
                }
            }
            response.MediaPathsBase64 = paths
                .Select(path => Convert.ToBase64String(Encoding.UTF8.GetBytes(path)))
                .ToArray();
        }

        private sealed class MediaFolder
        {
            public MediaFolder(string objectId, string relativePath, int depth)
            {
                ObjectId = objectId;
                RelativePath = relativePath;
                Depth = depth;
            }

            public string ObjectId { get; }
            public string RelativePath { get; }
            public int Depth { get; }
        }

        private static string NormalizeRelativePath(string path)
        {
            string[] segments = (path ?? string.Empty)
                .Split(FolderSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(segment => !IsSafePathSegment(segment)))
            {
                throw new ArgumentException("Folder contains an unsafe path segment.");
            }

            return string.Join("/", segments);
        }

        private static bool IsSafePathSegment(string value) =>
            !string.IsNullOrWhiteSpace(value)
            && value != "."
            && value != ".."
            && value.IndexOfAny(FolderSeparators) < 0
            && value.IndexOf(':') < 0;

        private static bool IsPlaylistFileName(string name)
        {
            string extension = Path.GetExtension(name);
            return IsSafePathSegment(name)
                && string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)
                && (string.Equals(extension, ".m3u", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".m3u8", StringComparison.OrdinalIgnoreCase));
        }

        private void Replace(WpdSession session, string folderId, string name,
            byte[] bytes, OperationResponse response)
        {
            bool originalDeleted = false;
            stage = "resolve-original";
            response.OriginalObjectId = ResolveOptionalExact(session, folderId, name);
            response.CandidateName = "." + name + ".shmembee-"
                + Guid.NewGuid().ToString("N") + ".candidate";

            try
            {
                stage = "create-candidate";
                response.CandidateObjectId = session.Create(
                    folderId, response.CandidateName, bytes);
                stage = "verify-candidate";
                Verify(session, response.CandidateObjectId, bytes);

                string renameProbe = response.CandidateName + ".renamed";
                stage = "probe-rename";
                session.Rename(response.CandidateObjectId, renameProbe);
                if (!string.Equals(session.Name(response.CandidateObjectId),
                        renameProbe, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Candidate rename probe did not persist.");
                }
                response.RenameSupported = true;

                if (response.OriginalObjectId != null)
                {
                    stage = "delete-original";
                    session.Delete(response.OriginalObjectId);
                    originalDeleted = true;
                }

                stage = "promote-candidate";
                session.Rename(response.CandidateObjectId, name);
                if (!string.Equals(session.Name(response.CandidateObjectId),
                        name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Promoted filename did not persist.");
                }
                Verify(session, response.CandidateObjectId, bytes);
                stage = "verify-exact-name";
                string promotedId = ResolveExact(session, folderId, name);
                if (!string.Equals(promotedId, response.CandidateObjectId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Exact-name readback resolved a different object.");
                }
            }
            catch (Exception primaryException)
            {
                if (!originalDeleted && response.CandidateObjectId != null)
                {
                    try
                    {
                        session.Delete(response.CandidateObjectId);
                        response.CandidateObjectId = null;
                        response.CandidateName = null;
                    }
                    catch (Exception cleanupException)
                    {
                        throw new InvalidOperationException(
                            "The WPD operation failed: "
                            + primaryException.Message
                            + " Candidate cleanup also failed. "
                            + "The original object was not touched. Candidate object ID: "
                            + response.CandidateObjectId
                            + ", candidate name: "
                            + response.CandidateName
                            + ". Cleanup error: "
                            + cleanupException.Message,
                            cleanupException);
                    }
                }

                // Never delete a verified recovery candidate after the original is gone.
                throw;
            }

            response.ObjectId = response.CandidateObjectId;
            response.Sha256 = Hash(bytes);
            response.ByteCount = bytes.Length;
        }

        private void Delete(WpdSession session, string folderId, string name,
            OperationResponse response)
        {
            stage = "resolve-object";
            response.OriginalObjectId = ResolveOptionalExact(session, folderId, name);
            if (response.OriginalObjectId == null)
            {
                return;
            }
            stage = "delete-object";
            session.Delete(response.OriginalObjectId);
            stage = "verify-delete";
            if (ResolveOptionalExact(session, folderId, name) != null)
            {
                throw new InvalidOperationException("Object still exists after delete.");
            }
        }

        private byte[] ReadById(WpdSession session, string objectId)
        {
            stage = "read-stream";
            return session.Read(objectId);
        }

        private void Verify(WpdSession session, string objectId, byte[] expected)
        {
            byte[] actual = ReadById(session, objectId);
            if (!actual.SequenceEqual(expected))
            {
                throw new InvalidOperationException(
                    "Object readback differs from the committed bytes.");
            }
        }

        private static string ResolvePath(WpdSession session, string parent, string path)
        {
            foreach (string segment in (path ?? string.Empty)
                .Split(FolderSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                parent = ResolveExact(session, parent, segment);
            }
            return parent;
        }

        private static string ResolveExact(WpdSession session, string parent, string name)
        {
            string id = ResolveOptionalExact(session, parent, name);
            return id ?? throw new FileNotFoundException(
                "No exact WPD object named '" + name + "' exists under " + parent + ".");
        }

        private static string ResolveOptionalExact(
            WpdSession session, string parent, string name)
        {
            string[] matches = session.Children(parent)
                .Where(id => string.Equals(
                    session.Name(id), name, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length > 1)
            {
                throw new InvalidOperationException(
                    "Multiple exact WPD objects named '" + name + "' exist under "
                    + parent + ": " + string.Join(", ", matches));
            }
            return matches.SingleOrDefault();
        }

        private static void Validate(OperationRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Operation))
                throw new ArgumentException("Operation is required.");
            if (string.IsNullOrWhiteSpace(request.Device))
                throw new ArgumentException("Device is required.");
            if (string.IsNullOrWhiteSpace(request.Storage))
                throw new ArgumentException("Storage is required.");
            if (request.Operation != "probe"
                && request.Operation != "snapshot-playlists"
                && request.Operation != "snapshot-media-paths"
                && request.Operation != "create-playlist-backup"
                && request.Operation != "delete-playlist-backup"
                && string.IsNullOrWhiteSpace(request.Name))
                throw new ArgumentException("Name is required.");
            if (request.Operation == "replace" && request.ContentBase64 == null)
                throw new ArgumentException("ContentBase64 is required.");
            if (request.Operation == "delete-playlist-backup")
                ValidateBackupHandle(
                    request.BackupFolderName,
                    request.CopiedNames);
        }

        private static void ValidateBackupHandle(
            string backupFolderName,
            string[] copiedNames)
        {
            if (string.IsNullOrWhiteSpace(backupFolderName)
                || !backupFolderName.StartsWith(
                    BackupFolderPrefix,
                    StringComparison.Ordinal)
                || !IsSafePathSegment(backupFolderName))
            {
                throw new ArgumentException(
                    "A constrained Shmembee backup folder name is required.");
            }
            if (copiedNames == null)
            {
                throw new ArgumentException("CopiedNames is required.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in copiedNames)
            {
                if (!IsPlaylistFileName(name) || !seen.Add(name))
                {
                    throw new ArgumentException(
                        "CopiedNames must contain unique direct-child M3U filenames.");
                }
            }
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "")
                    .ToLowerInvariant();
        }

        private static PROPERTYKEY Key(Guid fmtid, uint pid) =>
            new PROPERTYKEY(fmtid, pid);

        private sealed class WpdSession : IDisposable
        {
            private readonly IPortableDevice device;
            private readonly IPortableDeviceContent content;
            private readonly IPortableDeviceProperties properties;
            private readonly IPortableDeviceResources resources;

            private WpdSession(string deviceId, IPortableDevice device)
            {
                DeviceId = deviceId;
                this.device = device;
                content = device.Content();
                properties = content.Properties();
                resources = content.Transfer();
            }

            public string DeviceId { get; }

            public static WpdSession Open(string exactName)
            {
                IPortableDeviceManager manager =
                    (IPortableDeviceManager)new PortableDeviceManager();
                uint count = 0;
                manager.GetDevices(null, ref count);
                var ids = new string[count];
                manager.GetDevices(ids, ref count);
                var matches = new List<string>();
                foreach (string id in ids)
                {
                    uint length = 0;
                    manager.GetDeviceFriendlyName(id, null, ref length);
                    var chars = new StringBuilder((int)length);
                    manager.GetDeviceFriendlyName(id, chars, ref length);
                    string name = chars.ToString();
                    if (string.Equals(name, exactName, StringComparison.Ordinal))
                        matches.Add(id);
                }
                if (matches.Count != 1)
                    throw new InvalidOperationException("Expected exactly one device named '"
                        + exactName + "', found " + matches.Count + ".");

                IPortableDeviceValues values =
                    (IPortableDeviceValues)new PortableDeviceValues();
                values.SetStringValue(in WpdClientName, "Shmembee WPD Sidecar");
                values.SetUnsignedIntegerValue(in WpdClientMajor, 1);
                values.SetUnsignedIntegerValue(in WpdClientMinor, 0);
                IPortableDevice device = (IPortableDevice)new PortableDevice();
                device.Open(matches[0], values);
                return new WpdSession(matches[0], device);
            }

            private static PROPERTYKEY WpdClientName =
                Key(new Guid("204D9F0C-2292-4080-9F42-40664E70F859"), 2);
            private static PROPERTYKEY WpdClientMajor =
                Key(new Guid("204D9F0C-2292-4080-9F42-40664E70F859"), 3);
            private static PROPERTYKEY WpdClientMinor =
                Key(new Guid("204D9F0C-2292-4080-9F42-40664E70F859"), 4);

            public IEnumerable<string> Children(string parent)
            {
                IEnumPortableDeviceObjectIDs enumerator =
                    content.EnumObjects(0, parent, null);
                while (true)
                {
                    uint fetched = 0;
                    var ids = new string[1];
                    enumerator.Next(1, ids, out fetched);
                    if (fetched == 0) yield break;
                    yield return ids[0];
                }
            }

            public WpdObjectInfo Info(string objectId)
            {
                IPortableDeviceKeyCollection keys =
                    (IPortableDeviceKeyCollection)new PortableDeviceKeyCollection();
                PROPERTYKEY fileKey = ObjectOriginalFileName;
                PROPERTYKEY nameKey = ObjectName;
                PROPERTYKEY contentTypeKey = ObjectContentType;
                keys.Add(in fileKey);
                keys.Add(in nameKey);
                keys.Add(in contentTypeKey);
                IPortableDeviceValues values = properties.GetValues(objectId, keys);
                string name;
                try { name = values.GetStringValue(in fileKey); }
                catch (COMException)
                { name = values.GetStringValue(in nameKey); }
                return new WpdObjectInfo(
                    name,
                    values.GetGuidValue(in contentTypeKey) == FolderContentType);
            }

            public string Name(string objectId)
            {
                IPortableDeviceKeyCollection keys =
                    (IPortableDeviceKeyCollection)new PortableDeviceKeyCollection();
                PROPERTYKEY fileKey = ObjectOriginalFileName;
                PROPERTYKEY nameKey = ObjectName;
                keys.Add(in fileKey);
                keys.Add(in nameKey);
                IPortableDeviceValues values = properties.GetValues(objectId, keys);
                try { return values.GetStringValue(in fileKey); }
                catch (COMException)
                { return values.GetStringValue(in nameKey); }
            }

            public sealed class WpdObjectInfo
            {
                public WpdObjectInfo(string name, bool isFolder)
                {
                    Name = name;
                    IsFolder = isFolder;
                }

                public string Name { get; }
                public bool IsFolder { get; }
            }

            public byte[] Read(string objectId)
            {
                PROPERTYKEY resource = DefaultResource;
                IStream stream = resources.GetStream(
                    objectId, in resource, STGM.STGM_READ, out uint optimal);
                try
                {
                    using (var output = new MemoryStream())
                    {
                        var buffer = new byte[Math.Max(4096, Math.Min(optimal, 1024 * 1024))];
                        var readPtr = Marshal.AllocCoTaskMem(sizeof(int));
                        try
                        {
                            while (true)
                            {
                                stream.Read(buffer, buffer.Length, readPtr);
                                int read = Marshal.ReadInt32(readPtr);
                                if (read == 0) break;
                                output.Write(buffer, 0, read);
                            }
                        }
                        finally { Marshal.FreeCoTaskMem(readPtr); }
                        return output.ToArray();
                    }
                }
                finally
                {
                    Marshal.FinalReleaseComObject(stream);
                }
            }

            public string Create(string parent, string name, byte[] bytes)
            {
                IPortableDeviceValues values =
                    (IPortableDeviceValues)new PortableDeviceValues();
                values.SetStringValue(in ObjectParentId, parent);
                values.SetStringValue(in ObjectName, name);
                values.SetStringValue(in ObjectOriginalFileName, name);
                values.SetUnsignedLargeIntegerValue(in ObjectSize, (ulong)bytes.Length);
                Guid type = GenericFile;
                Guid format = UnspecifiedFormat;
                values.SetGuidValue(in ObjectContentType, in type);
                values.SetGuidValue(in ObjectFormat, in format);
                IStream stream;
                uint optimal = 0;
                string cookie = content.CreateObjectWithPropertiesAndData(
                    values, out stream, ref optimal);
                string objectId;
                try
                {
                    int offset = 0;
                    int chunkSize = (int)Math.Max(4096, Math.Min(optimal, 1024 * 1024));
                    while (offset < bytes.Length)
                    {
                        int count = Math.Min(chunkSize, bytes.Length - offset);
                        byte[] chunk = new byte[count];
                        Buffer.BlockCopy(bytes, offset, chunk, 0, count);
                        var writtenPtr = Marshal.AllocCoTaskMem(sizeof(int));
                        try
                        {
                            stream.Write(chunk, count, writtenPtr);
                            int written = Marshal.ReadInt32(writtenPtr);
                            if (written != count)
                                throw new IOException("WPD stream accepted " + written
                                    + " of " + count + " bytes.");
                            offset += written;
                        }
                        finally { Marshal.FreeCoTaskMem(writtenPtr); }
                    }
                    stream.Commit(0);
                    var dataStream = (IPortableDeviceDataStream)stream;
                    objectId = dataStream.GetObjectID();
                }
                finally
                {
                    Marshal.FinalReleaseComObject(stream);
                }
                return objectId;
            }

            public string CreateFolder(string parent, string name)
            {
                IPortableDeviceValues values =
                    (IPortableDeviceValues)new PortableDeviceValues();
                values.SetStringValue(in ObjectParentId, parent);
                values.SetStringValue(in ObjectName, name);
                Guid type = FolderContentType;
                Guid format = PropertiesOnlyFormat;
                values.SetGuidValue(in ObjectContentType, in type);
                values.SetGuidValue(in ObjectFormat, in format);
                return content.CreateObjectWithPropertiesOnly(values);
            }

            public void Rename(string objectId, string name)
            {
                IPortableDeviceValues values =
                    (IPortableDeviceValues)new PortableDeviceValues();
                values.SetStringValue(in ObjectName, name);
                values.SetStringValue(in ObjectOriginalFileName, name);
                properties.SetValues(objectId, values);
            }

            public void Delete(string objectId)
            {
                object rawCollection = Activator.CreateInstance(Type.GetTypeFromCLSID(
                    new Guid("08A99E2F-6D6D-4B80-AF5A-BAF2BCBE4CB9")));
                var ids = (INet48PropVariantCollection)rawCollection;
                IntPtr text = Marshal.StringToCoTaskMemUni(objectId);
                var value = new Net48PropVariant
                {
                    VariantType = 31, // VT_LPWSTR
                    PointerValue = text
                };
                try
                {
                    ids.Add(ref value);
                    content.Delete(
                        (DELETE_OBJECT_OPTIONS)DeleteNoRecursion,
                        (IPortableDevicePropVariantCollection)rawCollection);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(text);
                    Marshal.FinalReleaseComObject(rawCollection);
                }
            }

            public void Dispose()
            {
                try { device.Close(); }
                finally
                {
                    Marshal.FinalReleaseComObject(resources);
                    Marshal.FinalReleaseComObject(properties);
                    Marshal.FinalReleaseComObject(content);
                    Marshal.FinalReleaseComObject(device);
                }
            }
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct Net48PropVariant
        {
            [FieldOffset(0)]
            public ushort VariantType;

            [FieldOffset(8)]
            public IntPtr PointerValue;
        }

        [ComImport]
        [Guid("89B2E422-4F1B-4316-BCEF-A44AFEA83EB3")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface INet48PropVariantCollection
        {
            uint GetCount();

            void GetAt(uint index, out Net48PropVariant value);

            void Add(ref Net48PropVariant value);
        }
    }
}
