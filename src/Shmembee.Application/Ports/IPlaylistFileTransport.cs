using System;

namespace Shmembee.Application.Ports
{
    public interface IPlaylistFileTransport
    {
        byte[]? Read(string backingName);

        void Replace(string backingName, byte[] content);

        void Delete(string backingName);
    }
}
