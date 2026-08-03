using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Shmembee.Core.Paths
{
    public static class PhoneFileNameParser
    {
        private static readonly Regex NumberedPrefix = new Regex(
            @"^(?:(?<disc>\d+)-(?<track>\d+)|(?<track>\d+))(?:(?:\s+-\s+)|\s+)(?<title>.+)$",
            RegexOptions.CultureInvariant);
        private static readonly Regex EmbeddedNumberedPrefix = new Regex(
            @"^.+?-(?<track>\d+)\s+-\s+(?<title>.+)$",
            RegexOptions.CultureInvariant);
        private static readonly Regex MissingTrackNumberPrefix = new Regex(
            @"^(?<disc>\d+)-\s+-\s+(?<title>.+)$",
            RegexOptions.CultureInvariant);

        public static PhoneFileNameMetadata Parse(
            string fileName,
            string? artist = null)
        {
            if (fileName == null)
            {
                throw new ArgumentNullException(nameof(fileName));
            }

            string title = Path.GetFileNameWithoutExtension(fileName)
                .Normalize(NormalizationForm.FormC)
                .Trim();
            Match match = NumberedPrefix.Match(title);
            if (!match.Success)
            {
                match = EmbeddedNumberedPrefix.Match(title);
            }

            bool missingTrackNumber = false;
            if (!match.Success)
            {
                match = MissingTrackNumberPrefix.Match(title);
                missingTrackNumber = match.Success;
            }

            int? discNumber = null;
            int? trackNumber = null;
            if (match.Success)
            {
                if (!missingTrackNumber
                    && int.TryParse(match.Groups["disc"].Value, out int disc))
                {
                    discNumber = disc;
                }

                if (int.TryParse(match.Groups["track"].Value, out int track))
                {
                    trackNumber = track;
                }

                title = match.Groups["title"].Value;
            }
            else
            {
                title = Regex.Replace(
                    title,
                    @"^\s*(?:#\s*)?-\s*",
                    string.Empty,
                    RegexOptions.CultureInvariant);
            }

            string artistPrefix = (artist ?? string.Empty).Trim();
            if (artistPrefix.Length > 0
                && title.StartsWith(
                    artistPrefix + " - ",
                    StringComparison.OrdinalIgnoreCase))
            {
                title = title.Substring(artistPrefix.Length + 3);
            }
            else if (artistPrefix.Length > 0)
            {
                int separator = title.IndexOf(
                    " - ",
                    StringComparison.Ordinal);
                if (separator > 0
                    && string.Equals(
                        NormalizePrefix(title.Substring(0, separator)),
                        NormalizePrefix(artistPrefix),
                        StringComparison.Ordinal))
                {
                    title = title.Substring(separator + 3);
                }
            }

            return new PhoneFileNameMetadata(
                title.Normalize(NormalizationForm.FormC).Trim(),
                discNumber,
                trackNumber);
        }

        private static string NormalizePrefix(string value)
        {
            string canonical = value.Normalize(NormalizationForm.FormD);
            var normalized = new StringBuilder(canonical.Length);
            foreach (char character in canonical)
            {
                if (char.IsLetterOrDigit(character))
                {
                    normalized.Append(char.ToUpperInvariant(character));
                }
            }

            return normalized.ToString();
        }
    }

    public sealed class PhoneFileNameMetadata
    {
        public PhoneFileNameMetadata(
            string title,
            int? discNumber,
            int? trackNumber)
        {
            Title = title;
            DiscNumber = discNumber;
            TrackNumber = trackNumber;
        }

        public string Title { get; }

        public int? DiscNumber { get; }

        public int? TrackNumber { get; }
    }
}
