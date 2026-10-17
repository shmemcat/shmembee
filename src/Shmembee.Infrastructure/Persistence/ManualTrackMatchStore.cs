using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using Shmembee.Core.Paths;

namespace Shmembee.Infrastructure.Persistence
{
    public sealed class ManualTrackMatchStore
    {
        private readonly string path;

        public ManualTrackMatchStore(string path)
        {
            this.path = Path.GetFullPath(path);
        }

        public IReadOnlyDictionary<string, string> Load()
        {
            var matches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path))
            {
                return matches;
            }
            using (FileStream stream = File.OpenRead(path))
            {
                var serializer = new DataContractJsonSerializer(typeof(Dictionary<string, string>));
                var saved = (Dictionary<string, string>)serializer.ReadObject(stream)!;
                foreach (KeyValuePair<string, string> match in saved)
                {
                    if (!string.IsNullOrWhiteSpace(match.Key) && !string.IsNullOrWhiteSpace(match.Value))
                    {
                        matches[TrackPathNormalizer.NormalizePhonePath(match.Key)] = match.Value;
                    }
                }
            }
            return matches;
        }

        public void Save(string phonePath, string musicBeeUrl)
        {
            if (string.IsNullOrWhiteSpace(musicBeeUrl))
            {
                throw new ArgumentException("Select a MusicBee track to match.", nameof(musicBeeUrl));
            }
            var matches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> match in Load())
            {
                matches[match.Key] = match.Value;
            }
            matches[TrackPathNormalizer.NormalizePhonePath(phonePath)] = musicBeeUrl;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporaryPath = path + ".tmp";
            try
            {
                using (FileStream stream = File.Create(temporaryPath))
                {
                    new DataContractJsonSerializer(typeof(Dictionary<string, string>)).WriteObject(stream, matches);
                    stream.Flush();
                }
                if (File.Exists(path))
                {
                    File.Replace(temporaryPath, path, null);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }
}
