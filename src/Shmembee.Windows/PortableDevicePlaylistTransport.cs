using System;
using System.IO;
using Shmembee.Application.Ports;

namespace Shmembee.Windows
{
    /// <summary>
    /// Boundary for the MLE S24U playlist folder. The current implementation
    /// accepts a staged directory populated by the MTP capture scripts; native
    /// WPD transfer can replace this class without changing transaction policy.
    /// </summary>
    public sealed class PortableDevicePlaylistTransport : IPlaylistFileTransport
    {
        private readonly string stagingDirectory;

        public PortableDevicePlaylistTransport(string stagingDirectory)
        {
            this.stagingDirectory = Path.GetFullPath(
                stagingDirectory
                    ?? throw new ArgumentNullException(nameof(stagingDirectory)));
        }

        public byte[]? Read(string backingName)
        {
            string path = GetPath(backingName);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        public void Replace(string backingName, byte[] content)
        {
            Directory.CreateDirectory(stagingDirectory);
            string destination = GetPath(backingName);
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, content);
                if (File.Exists(destination))
                {
                    File.Replace(temporary, destination, null);
                }
                else
                {
                    File.Move(temporary, destination);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        public void Delete(string backingName)
        {
            string path = GetPath(backingName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private string GetPath(string backingName)
        {
            string path = Path.GetFullPath(Path.Combine(stagingDirectory, backingName));
            string prefix = stagingDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "The backing name escapes the staging directory.",
                    nameof(backingName));
            }

            return path;
        }
    }
}
