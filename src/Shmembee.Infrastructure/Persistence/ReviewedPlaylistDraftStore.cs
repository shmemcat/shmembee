using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Shmembee.Infrastructure.Persistence
{
    public enum PersistedDraftFreshness
    {
        Current,
        StaleChecksums
    }

    [DataContract]
    public sealed class PersistedPlaylistReviewDraft
    {
        public PersistedPlaylistReviewDraft()
        {
            RowId = string.Empty;
            MusicBeePlaylistId = string.Empty;
            PhonePlaylistId = string.Empty;
            MusicBeeChecksum = string.Empty;
            PhoneChecksum = string.Empty;
            Action = string.Empty;
            IncludedOccurrenceKeys = new List<string>();
            OrderSide = string.Empty;
        }

        [DataMember(Order = 1)]
        public string RowId { get; set; }

        [DataMember(Order = 2, EmitDefaultValue = false)]
        public string MusicBeePlaylistId { get; set; }

        [DataMember(Order = 3, EmitDefaultValue = false)]
        public string PhonePlaylistId { get; set; }

        [DataMember(Order = 4)]
        public string MusicBeeChecksum { get; set; }

        [DataMember(Order = 5)]
        public string PhoneChecksum { get; set; }

        [DataMember(Order = 6)]
        public string Action { get; set; }

        [DataMember(Order = 7)]
        public List<string> IncludedOccurrenceKeys { get; set; }

        [DataMember(Order = 8)]
        public string OrderSide { get; set; }

        [DataMember(Order = 9)]
        public bool IsConfirmed { get; set; }

        [DataMember(Order = 10)]
        public bool IsDeletion { get; set; }

        public PersistedDraftFreshness GetFreshness(
            string musicBeeChecksum,
            string phoneChecksum) =>
            string.Equals(MusicBeeChecksum, musicBeeChecksum, StringComparison.Ordinal)
            && string.Equals(PhoneChecksum, phoneChecksum, StringComparison.Ordinal)
                ? PersistedDraftFreshness.Current
                : PersistedDraftFreshness.StaleChecksums;
    }

    [DataContract]
    internal sealed class PersistedPlaylistReviewDraftDocument
    {
        public PersistedPlaylistReviewDraftDocument()
        {
            Version = 1;
            Drafts = new List<PersistedPlaylistReviewDraft>();
        }

        [DataMember(Order = 1)]
        public int Version { get; set; }

        [DataMember(Order = 2)]
        public List<PersistedPlaylistReviewDraft> Drafts { get; set; }
    }

    public sealed class ReviewedPlaylistDraftStore
    {
        private readonly string path;

        public ReviewedPlaylistDraftStore(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A reviewed-drafts path is required.", nameof(path));
            }

            this.path = Path.GetFullPath(path);
        }

        public IReadOnlyList<PersistedPlaylistReviewDraft> Load()
        {
            if (!File.Exists(path))
            {
                return Array.Empty<PersistedPlaylistReviewDraft>();
            }

            try
            {
                using (FileStream stream = File.OpenRead(path))
                {
                    var serializer = new DataContractJsonSerializer(
                        typeof(PersistedPlaylistReviewDraftDocument));
                    var document = serializer.ReadObject(stream)
                        as PersistedPlaylistReviewDraftDocument;
                    if (document == null || document.Version != 1)
                    {
                        return Array.Empty<PersistedPlaylistReviewDraft>();
                    }

                    return Validate(document.Drafts);
                }
            }
            catch (Exception exception) when (
                exception is IOException
                || exception is SerializationException
                || exception is InvalidDataContractException
                || exception is UnauthorizedAccessException)
            {
                return Array.Empty<PersistedPlaylistReviewDraft>();
            }
        }

        public void Save(IEnumerable<PersistedPlaylistReviewDraft> drafts)
        {
            if (drafts == null)
            {
                throw new ArgumentNullException(nameof(drafts));
            }

            var document = new PersistedPlaylistReviewDraftDocument
            {
                Drafts = Validate(drafts)
            };
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = path + ".tmp";
            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                {
                    var serializer = new DataContractJsonSerializer(
                        typeof(PersistedPlaylistReviewDraftDocument));
                    serializer.WriteObject(stream, document);
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

        public void Delete(string rowId)
        {
            if (string.IsNullOrWhiteSpace(rowId))
            {
                return;
            }

            Save(Load().Where(item =>
                !string.Equals(item.RowId, rowId, StringComparison.Ordinal)));
        }

        private static List<PersistedPlaylistReviewDraft> Validate(
            IEnumerable<PersistedPlaylistReviewDraft>? drafts)
        {
            var result = new Dictionary<string, PersistedPlaylistReviewDraft>(
                StringComparer.Ordinal);
            foreach (PersistedPlaylistReviewDraft draft in
                drafts ?? Enumerable.Empty<PersistedPlaylistReviewDraft>())
            {
                if (draft == null
                    || string.IsNullOrWhiteSpace(draft.RowId)
                    || string.IsNullOrWhiteSpace(draft.MusicBeeChecksum)
                    || string.IsNullOrWhiteSpace(draft.PhoneChecksum))
                {
                    continue;
                }

                result[draft.RowId] = new PersistedPlaylistReviewDraft
                {
                    RowId = draft.RowId.Trim(),
                    MusicBeePlaylistId = draft.MusicBeePlaylistId?.Trim() ?? string.Empty,
                    PhonePlaylistId = draft.PhonePlaylistId?.Trim() ?? string.Empty,
                    MusicBeeChecksum = draft.MusicBeeChecksum,
                    PhoneChecksum = draft.PhoneChecksum,
                    Action = draft.Action?.Trim() ?? string.Empty,
                    IncludedOccurrenceKeys = (draft.IncludedOccurrenceKeys
                            ?? new List<string>())
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Distinct(StringComparer.Ordinal)
                        .ToList(),
                    OrderSide = draft.OrderSide?.Trim() ?? string.Empty,
                    IsConfirmed = draft.IsConfirmed,
                    IsDeletion = draft.IsDeletion
                };
            }

            return result.Values.OrderBy(item => item.RowId, StringComparer.Ordinal).ToList();
        }
    }
}
