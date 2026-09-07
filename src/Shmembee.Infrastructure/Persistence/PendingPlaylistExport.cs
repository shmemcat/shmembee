using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using Shmembee.Application.Synchronization;

namespace Shmembee.Infrastructure.Persistence
{
    [DataContract]
    internal sealed class PendingPlaylistExport
    {
        [DataMember] public Guid OperationId { get; set; }
        [DataMember] public string Name { get; set; } = string.Empty;
        [DataMember] public string MusicBeeUrl { get; set; } = string.Empty;
        [DataMember] public string PhoneBackingName { get; set; } = string.Empty;
        [DataMember] public string MusicBeeChecksum { get; set; } = string.Empty;
        [DataMember] public string PhoneChecksum { get; set; } = string.Empty;
        [DataMember] public List<PendingExportTrack> Tracks { get; set; } = new List<PendingExportTrack>();

        public static PendingPlaylistExport From(
            SynchronizationPlan plan, PlaylistState musicBee, PlaylistState phone) =>
            new PendingPlaylistExport
            {
                OperationId = plan.OperationId,
                Name = plan.PlaylistDisplayName,
                MusicBeeUrl = plan.MusicBeePlaylistUrl,
                PhoneBackingName = plan.PhoneBackingName,
                MusicBeeChecksum = musicBee.Checksum,
                PhoneChecksum = phone.Checksum,
                Tracks = plan.Tracks.Select(track => new PendingExportTrack
                {
                    Id = track.TrackId,
                    MusicBeeUrl = track.MusicBeeUrl,
                    PhonePath = track.PhonePath
                }).ToList()
            };

        public SynchronizationPlan ToPlan(string playlistId) => new SynchronizationPlan(
            OperationId, playlistId, Name, MusicBeeUrl, PhoneBackingName, true,
            MusicBeeChecksum, PhoneChecksum,
            Tracks.Select(track => new SynchronizationTrack(track.Id, track.MusicBeeUrl, track.PhonePath)));
    }

    [DataContract]
    internal sealed class PendingExportTrack
    {
        [DataMember] public string Id { get; set; } = string.Empty;
        [DataMember] public string MusicBeeUrl { get; set; } = string.Empty;
        [DataMember] public string PhonePath { get; set; } = string.Empty;
    }
}
