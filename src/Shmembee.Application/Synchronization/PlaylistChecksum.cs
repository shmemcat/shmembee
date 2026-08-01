using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Shmembee.Application.Synchronization
{
    public static class PlaylistChecksum
    {
        public static string Compute(IEnumerable<string> orderedEntries)
        {
            if (orderedEntries == null)
            {
                throw new ArgumentNullException(nameof(orderedEntries));
            }

            string canonical = string.Join(
                "\n",
                orderedEntries.Select(entry => entry ?? string.Empty));
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                var builder = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash)
                {
                    builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }
    }
}
