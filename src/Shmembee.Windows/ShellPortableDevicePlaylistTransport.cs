using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Shmembee.Application.Ports;

namespace Shmembee.Windows
{
    public sealed class ShellPortableDevicePlaylistTransport : IPlaylistFileTransport
    {
        private static readonly char[] FolderSeparators = { '/', '\\' };

        private readonly string deviceName;
        private readonly string storageName;
        private readonly string[] folderSegments;
        private readonly TimeSpan timeout;
        private bool explorerOpened;

        public ShellPortableDevicePlaylistTransport(
            string deviceName,
            string storageName,
            string relativeFolderPath,
            TimeSpan? timeout = null)
        {
            this.deviceName = Require(deviceName, nameof(deviceName));
            this.storageName = Require(storageName, nameof(storageName));
            folderSegments = Require(relativeFolderPath, nameof(relativeFolderPath))
                .Split(FolderSeparators, StringSplitOptions.RemoveEmptyEntries);
            this.timeout = timeout ?? TimeSpan.FromSeconds(30);
        }

        public byte[]? Read(string backingName)
        {
            ValidateBackingName(backingName);
            dynamic folder = OpenPlaylistFolder();
            dynamic? item = FindItem(folder, backingName);
            if (item == null)
            {
                return null;
            }

            string temporaryDirectory = CreateTemporaryDirectory();
            try
            {
                dynamic shell = CreateShell();
                dynamic destination = shell.Namespace(temporaryDirectory);
                destination.CopyHere(item, 20);
                string destinationPath = Path.Combine(temporaryDirectory, backingName);
                WaitForStableFile(destinationPath, "MTP download");
                return File.ReadAllBytes(destinationPath);
            }
            finally
            {
                DeleteDirectory(temporaryDirectory);
            }
        }

        public void Replace(string backingName, byte[] content)
        {
            ValidateBackingName(backingName);
            if (content == null)
            {
                throw new ArgumentNullException(nameof(content));
            }

            dynamic folder = OpenPlaylistFolder();
            dynamic? existing = FindItem(folder, backingName);
            string candidateName = Path.GetFileNameWithoutExtension(backingName)
                + ".shmembee-"
                + Guid.NewGuid().ToString("N")
                + Path.GetExtension(backingName);
            string temporaryDirectory = CreateTemporaryDirectory();
            bool originalDeleted = false;
            try
            {
                string sourcePath = Path.Combine(temporaryDirectory, candidateName);
                File.WriteAllBytes(sourcePath, content);
                dynamic shell = CreateShell();
                dynamic sourceFolder = shell.Namespace(temporaryDirectory);
                dynamic sourceItem = sourceFolder.ParseName(candidateName);

                folder.CopyHere(sourceItem, 20);
                WaitUntil(
                    () => FindItem(folder, candidateName) != null,
                    "MTP candidate upload");
                WaitUntil(
                    () =>
                    {
                        byte[]? uploaded = Read(candidateName);
                        return uploaded != null && uploaded.SequenceEqual(content);
                    },
                    "MTP candidate verification");

                if (existing != null)
                {
                    existing.InvokeVerb("delete");
                    WaitUntil(
                        () => FindItem(folder, backingName) == null,
                        "MTP deletion before candidate promotion");
                    originalDeleted = true;
                }

                dynamic candidate = FindItem(folder, candidateName)
                    ?? throw new IOException(
                        "The verified MTP candidate disappeared before promotion.");
                candidate.Name = backingName;
                WaitUntil(
                    () => FindItem(folder, backingName) != null,
                    "MTP candidate promotion");
                WaitUntil(
                    () =>
                    {
                        byte[]? promoted = Read(backingName);
                        return promoted != null && promoted.SequenceEqual(content);
                    },
                    "MTP promoted-file verification");
            }
            catch (Exception exception)
            {
                string recovery = originalDeleted
                    ? " The verified recovery candidate may remain on the phone as "
                        + candidateName
                        + "."
                    : " The original phone playlist was not deleted.";
                throw new IOException(exception.Message + recovery, exception);
            }
            finally
            {
                DeleteDirectory(temporaryDirectory);
            }
        }

        public void Delete(string backingName)
        {
            ValidateBackingName(backingName);
            dynamic folder = OpenPlaylistFolder();
            dynamic? existing = FindItem(folder, backingName);
            if (existing == null)
            {
                return;
            }

            existing.InvokeVerb("delete");
            WaitUntil(
                () => FindItem(folder, backingName) == null,
                "MTP deletion");
        }

        private dynamic OpenPlaylistFolder()
        {
            dynamic shell = CreateShell();
            dynamic thisPc = shell.Namespace(17);
            dynamic? device = FindExactItem(thisPc, deviceName);
            if (device == null)
            {
                throw new IOException("Portable device is not connected: " + deviceName);
            }

            dynamic folder = device.GetFolder;
            dynamic? storage = FindExactItem(folder, storageName);
            if (storage == null)
            {
                throw new IOException(
                    "Portable-device storage was not found: " + storageName);
            }

            folder = storage.GetFolder;
            foreach (string segment in folderSegments)
            {
                dynamic? child = FindExactItem(folder, segment);
                if (child == null)
                {
                    throw new IOException(
                        "Portable-device folder was not found: " + segment);
                }

                folder = child.GetFolder;
            }

            OpenFolderInExplorer(folder);
            return folder;
        }

        private void OpenFolderInExplorer(dynamic folder)
        {
            if (explorerOpened)
            {
                return;
            }

            dynamic shell = CreateShell();
            dynamic windows = shell.Windows();
            for (int index = 0; index < windows.Count; index++)
            {
                dynamic window = windows.Item(index);
                try
                {
                    if (string.Equals(
                        (string)window.Document.Folder.Self.Path,
                        (string)folder.Self.Path,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        explorerOpened = true;
                        return;
                    }
                }
                catch
                {
                    // Ignore non-Explorer Shell windows.
                }
            }

            folder.Self.InvokeVerb("open");
            explorerOpened = true;
            Thread.Sleep(1000);
        }

        private static dynamic CreateShell()
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null)
            {
                throw new PlatformNotSupportedException(
                    "Windows Shell automation is unavailable.");
            }

            return Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException(
                    "Windows Shell automation could not be created.");
        }

        private static dynamic? FindExactItem(dynamic folder, string name)
        {
            foreach (dynamic item in folder.Items())
            {
                if (string.Equals(
                    (string)item.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }

            return null;
        }

        private static dynamic? FindItem(dynamic folder, string backingName)
        {
            string withoutExtension = Path.GetFileNameWithoutExtension(backingName);
            var exactMatches = new System.Collections.Generic.List<dynamic>();
            var displayMatches = new System.Collections.Generic.List<dynamic>();
            foreach (dynamic item in folder.Items())
            {
                string name = (string)item.Name;
                if (string.Equals(name, backingName, StringComparison.OrdinalIgnoreCase))
                {
                    exactMatches.Add(item);
                }
                else if (string.Equals(
                    name,
                    withoutExtension,
                    StringComparison.OrdinalIgnoreCase))
                {
                    displayMatches.Add(item);
                }
            }

            if (exactMatches.Count > 1
                || (exactMatches.Count == 0 && displayMatches.Count > 1))
            {
                throw new IOException(
                    "Multiple portable-device items match: " + backingName);
            }

            return exactMatches.Count == 1
                ? exactMatches[0]
                : displayMatches.Count == 1
                    ? displayMatches[0]
                    : null;
        }

        private void WaitUntil(Func<bool> condition, string operation)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                if (condition())
                {
                    return;
                }

                Thread.Sleep(250);
            }

            throw new IOException(operation + " did not complete within " + timeout + ".");
        }

        private void WaitForStableFile(string path, string operation)
        {
            var stopwatch = Stopwatch.StartNew();
            long previousLength = -1;
            int stableObservations = 0;
            while (stopwatch.Elapsed < timeout)
            {
                if (File.Exists(path))
                {
                    long length = new FileInfo(path).Length;
                    stableObservations = length == previousLength
                        ? stableObservations + 1
                        : 0;
                    previousLength = length;
                    if (stableObservations >= 2)
                    {
                        return;
                    }
                }

                Thread.Sleep(250);
            }

            throw new IOException(operation + " did not complete within " + timeout + ".");
        }

        private static string CreateTemporaryDirectory()
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "shmembee-mtp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Windows Shell can release temporary files shortly after returning.
            }
            catch (UnauthorizedAccessException)
            {
                // Windows Shell can release temporary files shortly after returning.
            }
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
    }
}
