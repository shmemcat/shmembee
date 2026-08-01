using System;
using System.Collections.Generic;

namespace Shmembee.Application.Ports
{
    public interface IPlaylistFileTransport
    {
        byte[]? Read(string backingName);

        void Replace(string backingName, byte[] content);

        void Delete(string backingName);
    }

    public interface IPhonePlaylistCatalogReader
    {
        IReadOnlyList<PhonePlaylistFile> ListPlaylists();
    }

    public interface IPhonePlaylistSnapshotReader
    {
        IReadOnlyList<PhonePlaylistContent> ReadPlaylistSnapshot();
    }

    public sealed class PhonePlaylistContent
    {
        public PhonePlaylistContent(
            string id,
            string backingName,
            byte[] content)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("A playlist identity is required.", nameof(id));
            }

            if (string.IsNullOrWhiteSpace(backingName))
            {
                throw new ArgumentException("A backing name is required.", nameof(backingName));
            }

            Id = id.Trim();
            BackingName = backingName.Trim();
            Content = content ?? throw new ArgumentNullException(nameof(content));
        }

        public string Id { get; }

        public string BackingName { get; }

        public byte[] Content { get; }
    }

    public sealed class PhonePlaylistFile
    {
        public PhonePlaylistFile(
            string id,
            string backingName,
            string? displayName = null,
            long? byteCount = null)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("A playlist identity is required.", nameof(id));
            }

            if (string.IsNullOrWhiteSpace(backingName))
            {
                throw new ArgumentException("A backing name is required.", nameof(backingName));
            }

            Id = id.Trim();
            BackingName = backingName.Trim();
            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? System.IO.Path.GetFileNameWithoutExtension(BackingName)
                : displayName!.Trim();
            ByteCount = byteCount;
        }

        public string Id { get; }

        public string BackingName { get; }

        public string DisplayName { get; }

        public long? ByteCount { get; }
    }
}
