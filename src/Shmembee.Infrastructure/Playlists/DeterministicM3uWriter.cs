using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Shmembee.Infrastructure.Playlists
{
    public sealed class DeterministicM3uWriter
    {
        private static readonly UTF8Encoding Utf8WithoutBom = new UTF8Encoding(false);
        private static readonly char[] LineBreaks = { '\r', '\n' };
        private readonly bool includeTrailingNewline;
        private readonly string pathPrefix;

        public DeterministicM3uWriter(
            bool includeTrailingNewline = true,
            string? pathPrefix = null)
        {
            this.includeTrailingNewline = includeTrailingNewline;
            this.pathPrefix = string.IsNullOrWhiteSpace(pathPrefix)
                ? string.Empty
                : pathPrefix!.Trim().Replace('\\', '/').TrimEnd('/') + "/";
        }

        public byte[] Write(IEnumerable<string> orderedPhonePaths)
        {
            if (orderedPhonePaths == null)
            {
                throw new ArgumentNullException(nameof(orderedPhonePaths));
            }

            string content = string.Join(
                "\n",
                orderedPhonePaths.Select(NormalizeLine));
            if (content.Length > 0 && includeTrailingNewline)
            {
                content += "\n";
            }

            return Utf8WithoutBom.GetBytes(content);
        }

        private string NormalizeLine(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "Playlist paths cannot be empty.",
                    nameof(value));
            }

            if (value.IndexOfAny(LineBreaks) >= 0)
            {
                throw new ArgumentException(
                    "Playlist paths cannot contain line breaks.",
                    nameof(value));
            }

            string normalized = value.Trim().Replace('\\', '/');
            return pathPrefix.Length == 0
                || normalized.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase)
                    ? normalized
                    : pathPrefix + normalized.TrimStart('/');
        }
    }
}
