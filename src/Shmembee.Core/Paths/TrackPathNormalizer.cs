using System;
using System.Collections.Generic;
using System.Text;

namespace Shmembee.Core.Paths
{
    public static class TrackPathNormalizer
    {
        private const string AndroidStorageRoot = "/storage/emulated/0/";
        private static readonly char[] PathSeparators = { '/' };

        public static string NormalizePhonePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A track path is required.", nameof(path));
            }

            string normalized = path.Trim()
                .Replace('\\', '/')
                .Normalize(NormalizationForm.FormC);
            while (normalized.StartsWith("./", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(2);
            }

            if (normalized.StartsWith(AndroidStorageRoot, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(AndroidStorageRoot.Length);
            }

            normalized = CollapseSegments(normalized);
            return normalized.TrimStart('/');
        }

        public static string NormalizeWindowsPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A track path is required.", nameof(path));
            }

            string normalized = path.Trim()
                .Replace('/', '\\')
                .Normalize(NormalizationForm.FormC);
            while (normalized.Contains("\\\\"))
            {
                normalized = normalized.Replace("\\\\", "\\");
            }

            return normalized.TrimEnd('\\').ToUpperInvariant();
        }

        public static string GetFileNameKey(string path)
        {
            string normalized = path.Trim()
                .Replace('\\', '/')
                .Normalize(NormalizationForm.FormC);
            int separator = normalized.LastIndexOf('/');
            return (separator < 0 ? normalized : normalized.Substring(separator + 1))
                .ToUpperInvariant();
        }

        private static string CollapseSegments(string path)
        {
            var segments = new List<string>();
            foreach (string segment in path.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment == ".")
                {
                    continue;
                }

                if (segment == "..")
                {
                    if (segments.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }

                    continue;
                }

                segments.Add(segment);
            }

            return string.Join("/", segments);
        }
    }
}
