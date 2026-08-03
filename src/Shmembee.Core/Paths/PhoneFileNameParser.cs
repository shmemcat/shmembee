using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Shmembee.Core.Paths
{
    public static class PhoneFileNameParser
    {
        private static readonly Regex NumberedPrefix = new Regex(
            @"^(?:(?<disc>\d+)-(?<track>\d+)|(?<track>\d+))(?:(?:\s+-\s+)|\s+)(?<title>.+)$",
            RegexOptions.CultureInvariant);

        public static PhoneFileNameMetadata Parse(
            string fileName,
            string? artist = null)
        {
            if (fileName == null)
            {
                throw new ArgumentNullException(nameof(fileName));
            }

            string title = Path.GetFileNameWithoutExtension(fileName);
            Match match = NumberedPrefix.Match(title);
            int? discNumber = null;
            int? trackNumber = null;
            if (match.Success)
            {
                if (int.TryParse(match.Groups["disc"].Value, out int disc))
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
                    @"^\s*#\s*-\s*",
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

            return new PhoneFileNameMetadata(title, discNumber, trackNumber);
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
