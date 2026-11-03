using System.IO.Compression;
using System.Text.Json;

namespace TSC.Ares;

/// <summary>
/// Stores editor note-group data as inert ordinary note records on reserved
/// tracks 31-40. The current diagnostic encoding restores the original pair-packed
/// representation (two payload bytes per event) but distributes records across
/// ten tracks. This isolates per-track limits from global event-type limits.
/// Earlier v4 byte-packed records on tracks 31-63 remain readable and are
/// rewritten using this pair-packed ten-track layout on save.
/// </summary>
internal static class NoteGroupMetadataCodec
{
    public const int TrackIndex = 31;
    public const int FirstMetadataTrack = TrackIndex;
    public const int MetadataTrackCount = 10;
    public const int LastMetadataTrack = FirstMetadataTrack + MetadataTrackCount - 1;
    // Keep reading the TSC-455 distributed v4 layout (tracks 31-63) so charts
    // already converted with that build migrate cleanly to the ten-track form.
    private const int LastReadableMetadataTrack = 63;
    public const int RequiredTrackCount = LastMetadataTrack + 1;

    // Legacy v1-v3 encoding: two payload bytes per event, producing up to
    // 65,536 distinct velocity/pan combinations in the global ARES type table.
    private const int LegacyHeaderAttribute = 240;
    private const int LegacyDataAttribute = 241;
    private const int LegacyFooterAttribute = 242;

    // Current v4 encoding: one byte per event, stored in velocity with pan=0.
    // Separate attributes make the format self-identifying without relying on
    // a payload byte or an event-type-table index.
    private const int HeaderAttribute = 243;
    private const int DataAttribute = 244;
    private const int FooterAttribute = 245;
    private const int LegacyMetadataVersion = 1;
    private const int MetadataVersion = 4;

    private sealed class Payload
    {
        public Payload() { }

        public int Version { get; set; }
        public List<NoteGroupDefinition> Groups { get; set; } = new();
        public List<InstanceData> Instances { get; set; } = new();
        public List<Binding> Bindings { get; set; } = new();
    }

    // Keep the two legacy fields readable so existing charts retain their
    // mirrored state. New metadata writes only the unified IsMirrored field.
    private sealed class InstanceData
    {
        public InstanceData() { }

        public InstanceData(NoteGroupInstance instance)
        {
            GroupId = instance.GroupId;
            InstanceId = instance.InstanceId;
            StartTick = instance.StartTick;
            StartTrack = instance.StartTrack;
            IsMirrored = instance.IsMirrored;
            TrackMap = instance.TrackMap?.Select(mapping => new TrackMappingData
            {
                CanonicalTrack = mapping.CanonicalTrack,
                Track = mapping.Track
            }).ToList();
        }

        public int GroupId { get; set; }
        public int InstanceId { get; set; }
        public int StartTick { get; set; }
        public int StartTrack { get; set; }
        public bool IsMirrored { get; set; }
        public List<TrackMappingData>? TrackMap { get; set; }

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public bool? MirrorX { get; set; }

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public bool? MirrorBank { get; set; }

        public NoteGroupInstance ToModel()
            => new(GroupId, InstanceId, StartTick, StartTrack,
                IsMirrored || MirrorX == true || MirrorBank == true,
                TrackMap?.Select(mapping => new NoteGroupTrackMapping(
                    mapping.CanonicalTrack, mapping.Track)).ToList());
    }

    private sealed class TrackMappingData
    {
        public int CanonicalTrack { get; set; }
        public int Track { get; set; }
    }

    private sealed class Binding
    {
        public Binding() { }

        public int Track { get; set; }
        public int EventIndex { get; set; }
        public int GroupId { get; set; }
        public int InstanceId { get; set; }
        public int MemberId { get; set; }
    }

    public static bool IsMetadataEvent(ChartEvent ev)
    {
        if (ev.RawType != 1)
            return false;

        // Pair-packed records originally lived exclusively on track 31. The
        // diagnostic writer now distributes that same encoding over tracks 31-40.
        if (ev.Track is >= FirstMetadataTrack and <= LastMetadataTrack && ev.Attr is
            LegacyHeaderAttribute or LegacyDataAttribute or LegacyFooterAttribute)
            return true;

        // Continue recognizing the compact v4 byte-packed layout from TSC-455/458.
        return ev.Track is >= FirstMetadataTrack and <= LastReadableMetadataTrack &&
            ev.Attr is HeaderAttribute or DataAttribute or FooterAttribute;
    }

    public static List<ChartEvent> CreateMetadataEvents(
        AresChart chart,
        bool forAresExport,
        bool muteNativeVolumeTracks = false)
    {
        ArgumentNullException.ThrowIfNull(chart);
        if (chart.NoteGroups.Count == 0)
            return new List<ChartEvent>();

        var bindings = new List<Binding>();
        foreach (var gameplayTrack in chart.Tracks.Where(track => track.TrackIndex is >= 0 and <= 15))
        {
            var serialized = gameplayTrack.Events
                .Where(ev => !ev.LoopReconstructed && (!forAresExport || ev.RawType != 30) && !IsMetadataEvent(ev))
                .OrderBy(ev => ev.Tick)
                .ThenBy(ev => ev.EventIndex)
                .ToList();

            // Local Test replaces all existing Volume automation with one tick-zero
            // silence event. Apply that exact transformation before assigning the
            // note-group bindings' per-track event indices; otherwise the added or
            // removed Volume records shift those indices and the reserved metadata
            // fails its own round-trip validation.
            if (muteNativeVolumeTracks && gameplayTrack.TrackIndex < 32)
                VolumeEventTools.ReplaceTrackVolumeWithSilence(serialized, gameplayTrack.TrackIndex);

            for (var index = 0; index < serialized.Count; index++)
            {
                var ev = serialized[index];
                if (ev.NoteGroupId is not int groupId ||
                    ev.NoteGroupInstanceId is not int instanceId ||
                    ev.NoteGroupMemberId is not int memberId)
                    continue;
                bindings.Add(new Binding
                {
                    Track = gameplayTrack.TrackIndex,
                    EventIndex = index,
                    GroupId = groupId,
                    InstanceId = instanceId,
                    MemberId = memberId
                });
            }
        }

        // Keep definitions even when no copy is currently placed. This is needed
        // by the future Group Library, where a library entry must outlive its instances.
        var payload = new Payload
        {
            Version = MetadataVersion,
            Groups = chart.NoteGroups.OrderBy(group => group.Id)
                .Select(CloneGroup).ToList(),
            Instances = chart.NoteGroupInstances.OrderBy(instance => instance.GroupId)
                .ThenBy(instance => instance.InstanceId).Select(instance => new InstanceData(instance)).ToList(),
            Bindings = bindings
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        using var compressedStream = new MemoryStream();
        using (var deflate = new DeflateStream(compressedStream, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(json);
        var compressed = compressedStream.ToArray();
        if (compressed.Length > ushort.MaxValue)
            throw new InvalidDataException(
                $"Note-group metadata is too large to store in an ARES chart ({compressed.Length} bytes; maximum {ushort.MaxValue}).");

        var dataEventCount = (compressed.Length + 1) / 2;
        var result = new List<ChartEvent>(dataEventCount + 2);

        // Restore the original two-byte encoding: each data event stores one
        // payload byte in Velocity and the next in Pan. Distribute complete
        // event pairs as evenly as possible over tracks 31-40. This retains the
        // original global event-type variety while reducing the event count on
        // each individual track (the diagnostic purpose of this build).
        var dataTrackCount = MetadataTrackCount;
        var eventsPerTrack = (dataEventCount + dataTrackCount - 1) / dataTrackCount;
        var eventIndices = new int[dataTrackCount];
        for (var trackOffset = 0; trackOffset < dataTrackCount; trackOffset++)
        {
            var metadataTrack = FirstMetadataTrack + trackOffset;
            var existingTrack = chart.FindTrack(metadataTrack);
            eventIndices[trackOffset] = existingTrack is null || existingTrack.Events.Count == 0
                ? 0
                : existingTrack.Events.Where(ev => !IsMetadataEvent(ev))
                    .Select(ev => ev.EventIndex).DefaultIfEmpty(-1).Max() + 1;
        }

        result.Add(CreateRecord(FirstMetadataTrack, LegacyHeaderAttribute,
            eventIndices[0]++, compressed.Length & 0xFF, (compressed.Length >> 8) & 0xFF));

        var dataEventOffset = 0;
        for (var trackOffset = 0; trackOffset < dataTrackCount && dataEventOffset < dataEventCount; trackOffset++)
        {
            var metadataTrack = FirstMetadataTrack + trackOffset;
            var eventsOnTrack = Math.Min(eventsPerTrack, dataEventCount - dataEventOffset);
            for (var eventOffset = 0; eventOffset < eventsOnTrack; eventOffset++)
            {
                var byteOffset = dataEventOffset * 2;
                var velocity = compressed[byteOffset];
                var pan = byteOffset + 1 < compressed.Length ? compressed[byteOffset + 1] : 0;
                result.Add(CreateRecord(metadataTrack, LegacyDataAttribute, eventIndices[trackOffset]++,
                    velocity, pan));
                dataEventOffset++;
            }
        }

        var checksum = Crc16(compressed);
        result.Add(CreateRecord(LastMetadataTrack, LegacyFooterAttribute,
            eventIndices[dataTrackCount - 1], checksum & 0xFF, (checksum >> 8) & 0xFF));
        return result;
    }

    public static void Reconstruct(AresChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var metadata = chart.Tracks
            .Where(track => track.TrackIndex is >= FirstMetadataTrack and <= LastReadableMetadataTrack)
            .SelectMany(track => track.Events)
            .Where(IsMetadataEvent)
            .OrderBy(ev => ev.Track)
            .ThenBy(ev => ev.EventIndex)
            .ToList();
        if (metadata.Count == 0)
            return;

        try
        {
            var isCurrentEncoding = metadata.Any(ev => ev.Attr is HeaderAttribute or DataAttribute or FooterAttribute);
            var headerAttribute = isCurrentEncoding ? HeaderAttribute : LegacyHeaderAttribute;
            var dataAttribute = isCurrentEncoding ? DataAttribute : LegacyDataAttribute;
            var footerAttribute = isCurrentEncoding ? FooterAttribute : LegacyFooterAttribute;
            var headers = metadata.Where(ev => ev.Attr == headerAttribute).ToList();
            var footers = metadata.Where(ev => ev.Attr == footerAttribute).ToList();
            if (headers.Count != 1 || footers.Count != 1 ||
                metadata.Any(ev => ev.Tick != 0 || ev.Instrument != 0 || ev.Duration != 0))
                return;

            // Header must be first and footer last in the ordered metadata stream.
            // Pair-packed metadata may be on track 31 only (legacy files) or span
            // tracks 31-40 (this diagnostic build); v4 may span tracks 31-63.
            if (metadata[0] != headers[0] || metadata[^1] != footers[0])
                return;

            var header = headers[0];
            var footer = footers[0];
            var compressedLength = header.Velocity | (header.Pan << 8);
            if (compressedLength <= 0 || compressedLength > ushort.MaxValue)
                return;

            var dataEvents = metadata.Where(ev => ev.Attr == dataAttribute).ToList();
            var compressed = new List<byte>(compressedLength);
            if (isCurrentEncoding)
            {
                // Current v4 encoding: exactly one byte in Velocity per data event.
                // Pan is deliberately fixed to zero, so only 256 data event types
                // can be produced regardless of payload size.
                if (dataEvents.Any(ev => ev.Pan != 0) || dataEvents.Count != compressedLength)
                    return;
                compressed.AddRange(dataEvents.Select(ev => (byte)ev.Velocity));
            }
            else
            {
                // Pair-packed encoding used by legacy v1-v3 and this diagnostic
                // layout: two bytes (Velocity/Pan) per data event. Sorting by
                // track then EventIndex rebuilds the original contiguous stream.
                foreach (var chunk in dataEvents)
                {
                    compressed.Add((byte)chunk.Velocity);
                    compressed.Add((byte)chunk.Pan);
                }
                if (compressed.Count < compressedLength)
                    return;
                if (compressed.Count > compressedLength)
                    compressed.RemoveRange(compressedLength, compressed.Count - compressedLength);
            }

            var expectedCrc = footer.Velocity | (footer.Pan << 8);
            if (Crc16(compressed.ToArray()) != expectedCrc)
                return;

            using var input = new MemoryStream(compressed.ToArray());
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);
            var payload = JsonSerializer.Deserialize<Payload>(output.ToArray());
            if (payload is null ||
                (isCurrentEncoding && payload.Version != MetadataVersion) ||
                (!isCurrentEncoding && payload.Version is not (LegacyMetadataVersion or 2 or 3 or MetadataVersion)) ||
                payload.Groups is null || payload.Instances is null || payload.Bindings is null ||
                payload.Groups.Any(group => group is null || group.Id <= 0 || group.Members is null) ||
                payload.Instances.Any(instance => instance is null))
                return;

            var groups = payload.Groups;
            if (groups.Select(group => group.Id).Distinct().Count() != groups.Count ||
                groups.Any(group => group.Members.Any(member => member is null || member.Id <= 0)) ||
                groups.Any(group => group.Members.Select(member => member.Id).Distinct().Count() != group.Members.Count))
                return;

            var groupIds = groups.Select(group => group.Id).ToHashSet();
            var instances = payload.Instances.Select(instance => instance.ToModel()).ToList();
            if (instances.Any(instance => instance.GroupId <= 0 || !groupIds.Contains(instance.GroupId) ||
                    instance.InstanceId <= 0 || instance.StartTick < 0 || instance.StartTrack is < 0 or > 15) ||
                instances.Select(instance => (instance.GroupId, instance.InstanceId)).Distinct().Count() != instances.Count)
                return;

            // A TrackMap is a partial one-to-one mapping from canonical pattern
            // lanes to unmirrored physical lanes. Keep it optional for legacy and
            // same-bank instances, and reject ambiguous/corrupt mappings.
            foreach (var instance in instances)
            {
                var mappings = instance.TrackMap;
                if (mappings is null)
                    continue;
                if (mappings.Any(mapping => mapping is null ||
                        mapping.CanonicalTrack is < 0 or > 15 || mapping.Track is < 0 or > 15) ||
                    mappings.Select(mapping => mapping.CanonicalTrack).Distinct().Count() != mappings.Count ||
                    mappings.Select(mapping => mapping.Track).Distinct().Count() != mappings.Count)
                    return;
            }

            var membersByGroup = groups.ToDictionary(
                group => group.Id,
                group => group.Members.Select(member => member.Id).ToHashSet());
            var instanceKeys = instances.Select(instance => (instance.GroupId, instance.InstanceId)).ToHashSet();
            var resolvedBindings = new List<(Binding Binding, ChartEvent Target)>();
            var boundEvents = new HashSet<(int Track, int EventIndex)>();
            foreach (var binding in payload.Bindings)
            {
                if (binding is null || binding.Track is < 0 or > 15 || binding.EventIndex < 0 ||
                    binding.GroupId <= 0 || binding.InstanceId <= 0 || binding.MemberId <= 0 ||
                    !instanceKeys.Contains((binding.GroupId, binding.InstanceId)) ||
                    !membersByGroup.TryGetValue(binding.GroupId, out var memberIds) || !memberIds.Contains(binding.MemberId) ||
                    !boundEvents.Add((binding.Track, binding.EventIndex)))
                    return;
                var target = chart.FindTrack(binding.Track)?.Events
                    .FirstOrDefault(ev => ev.EventIndex == binding.EventIndex && ev.RawType == 1);
                if (target is null)
                    return;
                resolvedBindings.Add((binding, target));
            }

            chart.NoteGroups.Clear();
            chart.NoteGroups.AddRange(groups.Select(CloneGroup));
            chart.NoteGroupInstances.Clear();
            chart.NoteGroupInstances.AddRange(instances);
            foreach (var (binding, target) in resolvedBindings)
            {
                target.NoteGroupId = binding.GroupId;
                target.NoteGroupInstanceId = binding.InstanceId;
                target.NoteGroupMemberId = binding.MemberId;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
            ArgumentException or NotSupportedException or InvalidOperationException or OverflowException)
        {
            // Invalid editor metadata must never prevent the actual chart from loading.
            chart.NoteGroups.Clear();
            chart.NoteGroupInstances.Clear();
        }
        finally
        {
            // Reserved records are never exposed as gameplay/editor notes, even
            // when an old encoding is migrated or a metadata payload is damaged.
            foreach (var track in chart.Tracks)
                track.Events.RemoveAll(IsMetadataEvent);
            chart.Events.RemoveAll(IsMetadataEvent);
        }
    }

    private static ChartEvent CreateRecord(int track, int attr, int eventIndex,
        int velocity, int pan)
        => new()
        {
            Tick = 0,
            Track = track,
            Attr = attr,
            RawType = 1,
            TypeIndex = -1,
            Duration = 0,
            NoteType = NoteType.Single,
            EventIndex = eventIndex,
            Kind = "NoteGroupMetadata",
            Instrument = 0,
            Velocity = velocity,
            Pan = pan
        };

    private static NoteGroupDefinition CloneGroup(NoteGroupDefinition group)
        => new()
        {
            Id = group.Id,
            Name = group.Name,
            Members = (group.Members ?? new List<NoteGroupMember>()).ToList()
        };

    private static int Crc16(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFF;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
        }
        return crc & 0xFFFF;
    }
}
