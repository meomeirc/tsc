using System.Buffers.Binary;
using System.Text;

namespace TSC.Ares;

/// <summary>
/// Writes the supported ARES/B2 chart subset consumed by <see cref="AresLoader"/>.
/// Event-type records are rebuilt from the current chart so editor additions,
/// deletions, and attribute/duration changes are reflected in the output.
/// </summary>
public static class AresWriter
{
    private const int HeaderSize = 28;
    private const uint VarUIntTop1 = 0x3E;
    private const uint VarUIntTop2 = 0x403D;
    private const uint VarUIntTop3 = 0x40403C;
    private const uint VarUIntMax = VarUIntTop3 + 0x3FFFFFFF;

    private readonly record struct EventTypeKey(
        int RawType,
        int Instrument,
        int Attr,
        int Duration,
        int Velocity,
        int Pan,
        int ByteValue,
        int FloatBits);

    private readonly record struct EventTypeDefinition(
        int RawType,
        int Instrument,
        int Attr,
        int Duration,
        int Velocity,
        int Pan,
        int ByteValue,
        float FloatValue)
    {
        public EventTypeKey Key => new(
            RawType, Instrument, Attr, Duration, Velocity, Pan, ByteValue,
            RawType == 3 ? BitConverter.SingleToInt32Bits(FloatValue) : 0);
    }

    private sealed class TrackDefinition
    {
        public required int TrackIndex { get; init; }
        public required List<(ChartEvent Event, int TypeIndex)> Events { get; init; }
        public List<int> Symbols { get; } = new();
    }

    private sealed class HuffmanNode
    {
        public required ulong Frequency { get; init; }
        public int Symbol { get; init; } = -1;
        public HuffmanNode? Left { get; init; }
        public HuffmanNode? Right { get; init; }
        public bool IsLeaf => Left is null && Right is null;
    }

    /// <summary>
    /// Saves an ARES/B2 chart using project metadata defaults. Generated callback
    /// copies are omitted because the loader reconstructs them from source events.
    /// </summary>
    public static void Save(string path, AresChart chart, byte[]? sourceHeader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var output = BuildBytes(chart, sourceHeader);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllBytes(fullPath, output);
    }

    /// <summary>Builds and validates an ARES file in memory.</summary>
    public static byte[] BuildBytes(AresChart chart, byte[]? sourceHeader = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        _ = sourceHeader; // Source headers are intentionally not preserved anymore.
        AresMetadataDefaults.Apply(chart);

        var definitions = new List<EventTypeDefinition>();
        var indices = new Dictionary<EventTypeKey, int>();
        var frequencies = new List<uint>();
        var tracks = new List<TrackDefinition>();
        foreach (var chartTrack in chart.Tracks)
        {
            if (chartTrack.TrackIndex is < 0 or > byte.MaxValue)
                throw new InvalidDataException($"Track index {chartTrack.TrackIndex} cannot be stored in ARES.");
        }
        var chartTracksByIndex = chart.Tracks.GroupBy(t => t.TrackIndex)
            .ToDictionary(g => g.Key, g => g.First());
        var highestChartTrackCount = chartTracksByIndex.Keys
            .Select(index => checked(index + 1)).DefaultIfEmpty(0).Max();
        var disabledTempoBackups = chart.Tracks.SelectMany(track => track.Events)
            .Where(ev => !ev.LoopReconstructed && ev.RawType == 30)
            .OrderBy(ev => ev.Tick)
            .ThenBy(ev => ev.Track)
            .ThenBy(ev => ev.EventIndex)
            .ToList();
        var outputTrackCount = Math.Max(Math.Max(1, chart.AresTrackCount), highestChartTrackCount);
        // Legacy type-30 tempo backups are temporarily converted into ordinary
        // kind-1 metadata notes on track 30, which is reserved for editor data.
        if (disabledTempoBackups.Count > 0)
            outputTrackCount = Math.Max(outputTrackCount, 31);
        if (outputTrackCount > byte.MaxValue)
            throw new InvalidDataException($"ARES supports at most 255 track records; requested {outputTrackCount}.");

        // Original-game ARES files keep empty records in the track table (notably
        // auxiliary tracks 12+). Emit the full table instead of compacting it to
        // only the tracks that currently contain events.
        for (var trackIndex = 0; trackIndex < outputTrackCount; trackIndex++)
        {
            chartTracksByIndex.TryGetValue(trackIndex, out var chartTrack);
            var trackEvents = chartTrack?.Events
                .Where(e => !e.LoopReconstructed && e.RawType != 30)
                .OrderBy(e => e.Tick)
                .ThenBy(e => e.EventIndex)
                .ToList() ?? new List<ChartEvent>();

            if (trackIndex == 30 && disabledTempoBackups.Count > 0)
            {
                var nextEventIndex = trackEvents.Count == 0 ? 0 : trackEvents.Max(e => e.EventIndex) + 1;
                foreach (var disabledTempo in disabledTempoBackups)
                {
                    if (disabledTempo.Track is < 0 or > byte.MaxValue)
                        throw new InvalidDataException(
                            $"Disabled tempo source track {disabledTempo.Track} at tick {disabledTempo.Tick} cannot be stored in metadata.");
                    var bpm = (float)disabledTempo.Value;
                    if (!float.IsFinite(bpm) || bpm <= 0f)
                        throw new InvalidDataException(
                            $"Invalid disabled tempo payload {disabledTempo.Value} on track {disabledTempo.Track}, tick {disabledTempo.Tick}.");
                    var bpmBits = unchecked((uint)BitConverter.SingleToInt32Bits(bpm));
                    var low = (int)(bpmBits & 0xFFFF);
                    var high = (int)((bpmBits >> 16) & 0xFFFF);

                    // ARES kind 30 is rejected by the original game. Store the
                    // disabled BPM as three short ordinary notes instead:
                    // attrs 10/11 carry low/high 16-bit halves; attr 12 stores
                    // the source track in Velocity so editor round-trips retain it.
                    trackEvents.Add(CreateDisabledTempoMetadataNote(
                        disabledTempo.Tick, nextEventIndex++, 10, low, 0));
                    trackEvents.Add(CreateDisabledTempoMetadataNote(
                        disabledTempo.Tick, nextEventIndex++, 11, high, 0));
                    trackEvents.Add(CreateDisabledTempoMetadataNote(
                        disabledTempo.Tick, nextEventIndex++, 12, disabledTempo.Track, 0, valueIsByte: true));
                }
                trackEvents = trackEvents
                    .OrderBy(e => e.Tick)
                    .ThenBy(e => e.EventIndex)
                    .ToList();
            }

            if (trackIndex == 16)
            {
                trackEvents = SynchronizeStartGearEvent(chart, trackEvents);
                trackEvents = SynchronizePlayTickMarker(chart, trackEvents);
                // Synchronizing either endpoint can move it forward or backward.
                // Keep the serialized auxiliary track in tick order afterward.
                trackEvents.Sort((a, b) => a.Tick != b.Tick
                    ? a.Tick.CompareTo(b.Tick)
                    : a.EventIndex.CompareTo(b.EventIndex));
            }

            var track = new TrackDefinition
            {
                TrackIndex = trackIndex,
                Events = new List<(ChartEvent Event, int TypeIndex)>(trackEvents.Count)
            };

            foreach (var ev in trackEvents)
            {
                if (ev.Tick < 0)
                    throw new InvalidDataException($"Negative tick {ev.Tick} on track {ev.Track} cannot be stored in ARES.");

                var definition = CreateEventType(ev);
                if (!indices.TryGetValue(definition.Key, out var typeIndex))
                {
                    typeIndex = definitions.Count;
                    if (typeIndex >= ushort.MaxValue)
                        throw new InvalidDataException("ARES supports at most 65535 distinct event types.");
                    definitions.Add(definition);
                    indices.Add(definition.Key, typeIndex);
                    frequencies.Add(0);
                }

                frequencies[typeIndex] = checked(frequencies[typeIndex] + 1);
                track.Events.Add((ev, typeIndex));
                track.Symbols.Add(typeIndex);
            }
            tracks.Add(track);
        }

        if (tracks.Count > byte.MaxValue)
            throw new InvalidDataException("ARES supports at most 255 tracks.");
        if (definitions.Any(definition => definition.RawType == 30))
            throw new InvalidDataException("ARES export must not contain unsupported legacy event kind 30.");

        var maxTick = tracks.SelectMany(t => t.Events).Select(e => e.Event.Tick).DefaultIfEmpty(0).Max();
        if (maxTick > (int)VarUIntMax)
            throw new InvalidDataException($"Tick {maxTick} exceeds the ARES variable-integer range.");

        var mapped = new MappedPayloadWriter();
        WriteB2(mapped, chart, definitions, frequencies, tracks);
        var plainPayload = mapped.Build();
        var header = CreateHeader(AresMetadataDefaults.GetHeaderTemplateBytes());
        var encryptedPayload = Encrypt(plainPayload, header);
        var output = new byte[header.Length + encryptedPayload.Length];
        Buffer.BlockCopy(header, 0, output, 0, header.Length);
        Buffer.BlockCopy(encryptedPayload, 0, output, header.Length, encryptedPayload.Length);

        // Validate the generated binary with the same parser used by the app
        // before touching the output path. PlayTick is a B2 header field; its
        // parallel track-16 ending marker and the start-gear/audio-origin marker
        // must both remain synchronized with their metadata values.
        var validatedChart = AresLoader.LoadBytes(output);
        if (validatedChart.PlayTick != chart.PlayTick)
            throw new InvalidDataException(
                $"ARES export validation failed: play tick changed from {chart.PlayTick} to {validatedChart.PlayTick}.");
        var validatedTimelineDuration = new TempoMap(validatedChart).TickToSeconds(validatedChart.PlayTick);
        if (Math.Abs(validatedChart.PlayTime - validatedTimelineDuration) > 0.002)
            throw new InvalidDataException(
                $"ARES export validation failed: duration header {validatedChart.PlayTime:0.######} s does not match " +
                $"the decoded tempo timeline {validatedTimelineDuration:0.######} s at play tick {validatedChart.PlayTick}.");
        if (validatedChart.AudioOriginTick != chart.AudioOriginTick)
            throw new InvalidDataException(
                $"ARES export validation failed: audio origin tick changed from {chart.AudioOriginTick} to {validatedChart.AudioOriginTick}. " +
                "The track-16 start marker did not round-trip at the requested tick.");
        if (validatedChart.InitialGearAttr != chart.InitialGearAttr)
            throw new InvalidDataException(
                $"ARES export validation failed: start gear changed from attr {chart.InitialGearAttr} to attr {validatedChart.InitialGearAttr}.");
        if (validatedChart.FindTrack(16)?.Events.Any(e =>
                AresLoader.IsPlayTickMarker(e) && e.Tick == chart.PlayTick) != true)
            throw new InvalidDataException(
                $"ARES export validation failed: track-16 ending marker did not match play tick {chart.PlayTick}.");

        // Legacy disabled tempos must round-trip through track-30 metadata, without
        // leaking into the active tempo map or emitting unsupported ARES kind 30.
        var expectedDisabledTempos = chart.Tracks.SelectMany(track => track.Events)
            .Where(ev => !ev.LoopReconstructed && ev.RawType == 30)
            .Select(ev => (ev.Track, ev.Tick, BpmBits: BitConverter.SingleToInt32Bits(ev.Value)))
            .OrderBy(ev => ev.Track).ThenBy(ev => ev.Tick).ThenBy(ev => ev.BpmBits)
            .ToArray();
        var actualDisabledTempos = validatedChart.Tracks.SelectMany(track => track.Events)
            .Where(ev => ev.RawType == 30)
            .Select(ev => (ev.Track, ev.Tick, BpmBits: BitConverter.SingleToInt32Bits(ev.Value)))
            .OrderBy(ev => ev.Track).ThenBy(ev => ev.Tick).ThenBy(ev => ev.BpmBits)
            .ToArray();
        if (!expectedDisabledTempos.SequenceEqual(actualDisabledTempos))
            throw new InvalidDataException(
                $"ARES export validation failed: track-30 disabled-tempo metadata did not round-trip intact " +
                $"(expected {expectedDisabledTempos.Length}, got {actualDisabledTempos.Length}).");
        return output;
    }

    private static List<ChartEvent> SynchronizePlayTickMarker(AresChart chart, List<ChartEvent> trackEvents)
    {
        if (chart.PlayTick < 0)
            throw new InvalidDataException("Play tick cannot be negative in ARES.");

        var markerIndices = trackEvents
            .Select((ev, index) => (Event: ev, Index: index))
            .Where(pair => AresLoader.IsPlayTickMarker(pair.Event))
            .OrderByDescending(pair => pair.Event.Tick)
            .ThenByDescending(pair => pair.Event.EventIndex)
            .Select(pair => pair.Index)
            .ToList();
        if (markerIndices.Count > 0)
        {
            var markerIndex = markerIndices[0];
            trackEvents[markerIndex] = CopyWithTick(trackEvents[markerIndex], chart.PlayTick);
            foreach (var duplicateIndex in markerIndices.Skip(1).OrderByDescending(index => index))
                trackEvents.RemoveAt(duplicateIndex);
            return trackEvents;
        }

        var nextEventIndex = trackEvents.Count == 0 ? 0 : trackEvents.Max(e => e.EventIndex) + 1;
        trackEvents.Add(new ChartEvent
        {
            Tick = chart.PlayTick,
            Track = 16,
            Attr = 0,
            RawType = 1,
            TypeIndex = -1,
            Duration = 6,
            NoteType = NoteType.Single,
            EventIndex = nextEventIndex,
            Kind = "Note",
            Instrument = 2,
            Velocity = 127,
            Pan = 64
        });
        return trackEvents;
    }

    private static ChartEvent CopyWithTick(ChartEvent ev, int tick) => new()
    {
        Tick = tick,
        Track = ev.Track,
        Attr = ev.Attr,
        RawType = ev.RawType,
        TypeIndex = ev.TypeIndex,
        Duration = ev.Duration,
        NoteType = ev.NoteType,
        EventIndex = ev.EventIndex,
        LinePair = ev.LinePair,
        TwinPairId = ev.TwinPairId,
        LoopReconstructed = ev.LoopReconstructed,
        LoopPhase = ev.LoopPhase,
        Kind = ev.Kind,
        Value = ev.Value,
        Instrument = ev.Instrument,
        Velocity = ev.Velocity,
        Pan = ev.Pan
    };

    private static List<ChartEvent> SynchronizeStartGearEvent(AresChart chart, List<ChartEvent> trackEvents)
    {
        if (!AresLoader.IsStartGearAttribute(chart.InitialGearAttr))
            throw new InvalidDataException($"Start gear attribute {chart.InitialGearAttr} is invalid; expected 0, 2, 4, or 6.");
        if (chart.AudioOriginTick < 0)
            throw new InvalidDataException("Audio origin tick cannot be negative in ARES.");

        // Native ARES stores the audio-origin tick and start gear together in the
        // early instrument-1 marker on auxiliary track 16. Both its tick and attr
        // must be synchronized when the editor metadata changes. Copy the event
        // so export never mutates the live chart model. Attr 2 aliases 1K.
        var markerIndex = trackEvents.FindIndex(e =>
            e.RawType == 1 && e.Instrument == 1 && AresLoader.IsStartGearAttribute(e.Attr));
        if (markerIndex >= 0)
        {
            trackEvents[markerIndex] = CopyWithStartGear(
                trackEvents[markerIndex], chart.AudioOriginTick, chart.InitialGearAttr);
            return trackEvents;
        }

        // TSC-only/new charts may have metadata without the original auxiliary
        // marker. Synthesize it at the requested audio-origin tick.
        var nextEventIndex = trackEvents.Count == 0 ? 0 : trackEvents.Max(e => e.EventIndex) + 1;
        trackEvents.Add(new ChartEvent
        {
            Tick = chart.AudioOriginTick,
            Track = 16,
            Attr = chart.InitialGearAttr,
            RawType = 1,
            Duration = 6,
            NoteType = NoteType.Single,
            EventIndex = nextEventIndex,
            Kind = "Note",
            Instrument = 1,
            Velocity = 127,
            Pan = 64
        });
        return trackEvents;
    }

    private static ChartEvent CopyWithStartGear(ChartEvent ev, int tick, int attr) => new()
    {
        Tick = tick,
        Track = ev.Track,
        Attr = attr,
        RawType = ev.RawType,
        TypeIndex = ev.TypeIndex,
        Duration = ev.Duration,
        NoteType = ev.NoteType,
        EventIndex = ev.EventIndex,
        LinePair = ev.LinePair,
        TwinPairId = ev.TwinPairId,
        LoopReconstructed = ev.LoopReconstructed,
        LoopPhase = ev.LoopPhase,
        Kind = ev.Kind,
        Value = ev.Value,
        Instrument = ev.Instrument,
        Velocity = ev.Velocity,
        Pan = ev.Pan
    };

    private static ChartEvent CreateDisabledTempoMetadataNote(
        int tick,
        int eventIndex,
        int attr,
        int packedValue,
        int pan,
        bool valueIsByte = false)
    {
        var velocity = valueIsByte ? packedValue : (packedValue >> 8) & 0xFF;
        var packedPan = valueIsByte ? pan : packedValue & 0xFF;
        return new ChartEvent
        {
            Tick = tick,
            Track = 30,
            Attr = attr,
            RawType = 1,
            TypeIndex = -1,
            Duration = 1,
            NoteType = NoteType.Single,
            EventIndex = eventIndex,
            LinePair = -1,
            Kind = "Note",
            Instrument = 0,
            Velocity = velocity,
            Pan = packedPan
        };
    }

    private static EventTypeDefinition CreateEventType(ChartEvent ev)
    {
        switch (ev.RawType)
        {
            case 1:
                if (ev.Instrument is < 0 or > 4095)
                    throw new InvalidDataException($"Instrument {ev.Instrument} on track {ev.Track}, tick {ev.Tick} is outside 0..4095.");
                if (ev.Attr is < 0 or > byte.MaxValue)
                    throw new InvalidDataException($"Attribute {ev.Attr} on track {ev.Track}, tick {ev.Tick} is outside 0..255.");
                if (ev.Duration is < 0 or > ushort.MaxValue)
                    throw new InvalidDataException($"Duration {ev.Duration} on track {ev.Track}, tick {ev.Tick} is outside 0..65535.");
                if (ev.Velocity is < 0 or > byte.MaxValue || ev.Pan is < 0 or > byte.MaxValue)
                    throw new InvalidDataException($"Velocity/pan on track {ev.Track}, tick {ev.Tick} must be 0..255.");
                return new EventTypeDefinition(1, ev.Instrument, ev.Attr, ev.Duration,
                    ev.Velocity, ev.Pan, 0, 0);

            case 2:
            case 4:
                return new EventTypeDefinition(ev.RawType, 0, 0, 0, 0, 0,
                    ToByteValue(ev.Value, ev), 0);

            case 3:
                var bpm = (float)ev.Value;
                if (!float.IsFinite(bpm) || bpm <= 0)
                    throw new InvalidDataException($"Invalid tempo payload {ev.Value} on track {ev.Track}, tick {ev.Tick}.");
                return new EventTypeDefinition(ev.RawType, 0, 0, 0, 0, 0, 0, bpm);

            case 5:
                throw new NotSupportedException(
                    $"ARES User events are not supported by the current ARES reader (track {ev.Track}, tick {ev.Tick}).");

            default:
                throw new NotSupportedException(
                    $"Cannot save unsupported ARES event type {ev.RawType} (track {ev.Track}, tick {ev.Tick}).");
        }
    }

    private static int ToByteValue(float value, ChartEvent ev)
    {
        if (!float.IsFinite(value) || value < 0 || value > byte.MaxValue || MathF.Abs(value - MathF.Round(value)) > 0.0001f)
            throw new InvalidDataException($"Event value {value} on track {ev.Track}, tick {ev.Tick} must be an integer in 0..255 for ARES.");
        return (int)MathF.Round(value);
    }

    private static void WriteB2(
        MappedPayloadWriter w,
        AresChart chart,
        IReadOnlyList<EventTypeDefinition> eventTypes,
        IReadOnlyList<uint> eventFrequencies,
        IReadOnlyList<TrackDefinition> tracks)
    {
        if (chart.TicksPerMeasure is < 1 or > ushort.MaxValue)
            throw new InvalidDataException($"Ticks per measure {chart.TicksPerMeasure} is outside 1..65535.");
        if (!double.IsFinite(chart.InitialTempo) || chart.InitialTempo <= 0 || !float.IsFinite((float)chart.InitialTempo))
            throw new InvalidDataException($"Invalid initial tempo {chart.InitialTempo}.");
        if (chart.PlayTick < 0)
            throw new InvalidDataException("Play tick cannot be negative in ARES.");
        if (!double.IsFinite(chart.PlayTime) || chart.PlayTime < 0 || !float.IsFinite((float)chart.PlayTime))
            throw new InvalidDataException($"Invalid play time {chart.PlayTime}.");

        // The original game validates the ARES duration header against the time
        // obtained by decoding the active tempo timeline through PlayTick. A
        // template's old PlayTime can become stale when tempo events are edited,
        // so write the authoritative timeline duration instead of carrying the
        // template duration into the exported binary. This does not mutate the
        // editor's in-memory PlayTime value.
        var headerDuration = new TempoMap(chart).TickToSeconds(chart.PlayTick);
        if (!double.IsFinite(headerDuration) || headerDuration < 0 || !float.IsFinite((float)headerDuration))
            throw new InvalidDataException($"Invalid ARES timeline duration {headerDuration} at play tick {chart.PlayTick}.");

        var instrumentNameBytes = chart.InstrumentNames.Select(name => Encoding.UTF8.GetBytes(name)).ToList();
        if (instrumentNameBytes.Count > ushort.MaxValue)
            throw new InvalidDataException("ARES supports at most 65535 instrument names.");
        for (var i = 0; i < instrumentNameBytes.Count; i++)
        {
            if (instrumentNameBytes[i].Length > byte.MaxValue)
                throw new InvalidDataException($"Instrument name {i} exceeds the ARES 255-byte limit.");
        }
        if (chart.Instruments.Count > ushort.MaxValue)
            throw new InvalidDataException("ARES supports at most 65535 instrument table entries.");

        // The instrument-name reference Huffman stream is length-delimited by its
        // own bit count. Its decoded symbol count is not guaranteed to equal the
        // number of raw instrument-table entries (some source charts have partial
        // name mappings), so do not couple the two collection lengths here.
        // The references are validated against the name and Huffman tables below.
        var instrumentFrequencies = chart.InstrumentNameHuffmanFrequencies.Count > 0
            ? chart.InstrumentNameHuffmanFrequencies.ToArray()
            : new uint[instrumentNameBytes.Count];
        if (instrumentFrequencies.Length > ushort.MaxValue)
            throw new InvalidDataException("ARES supports at most 65535 instrument-name Huffman symbols.");
        foreach (var reference in chart.InstrumentNameReferences)
        {
            if ((uint)reference >= (uint)instrumentNameBytes.Count)
                throw new InvalidDataException($"Invalid instrument-name reference {reference}.");
            if ((uint)reference >= (uint)instrumentFrequencies.Length)
                throw new InvalidDataException($"Instrument-name reference {reference} has no Huffman symbol.");
            if (chart.InstrumentNameHuffmanFrequencies.Count == 0)
                instrumentFrequencies[reference] = checked(instrumentFrequencies[reference] + 1);
        }

        w.U32(chart.RawMagic);
        w.U16((ushort)chart.TicksPerMeasure);
        w.F32((float)chart.InitialTempo);
        w.U32((uint)chart.PlayTick);
        w.F32((float)headerDuration);

        w.U16((ushort)instrumentNameBytes.Count);
        foreach (var name in instrumentNameBytes)
        {
            w.U8((byte)name.Length);
            w.Blob(name);
        }

        w.U16((ushort)instrumentFrequencies.Length);
        foreach (var frequency in instrumentFrequencies)
            w.VarUInt(frequency);

        var instrumentRoot = BuildHuffmanTree(instrumentFrequencies);
        var instrumentCodes = BuildCodes(instrumentRoot, instrumentFrequencies.Length);
        var (instrumentBits, instrumentBitLength) = EncodeSymbols(
            chart.InstrumentNameReferences, instrumentCodes, instrumentRoot?.IsLeaf == true);

        w.U16((ushort)chart.Instruments.Count);
        w.U32((uint)instrumentBitLength);
        w.Blob(instrumentBits);
        foreach (var instrument in chart.Instruments)
        {
            if (instrument.InstrumentNumber is < 0 or > ushort.MaxValue)
                throw new InvalidDataException($"Instrument number {instrument.InstrumentNumber} is outside 0..65535.");
            w.U16((ushort)instrument.InstrumentNumber);
            w.U8(instrument.Streamed ? (byte)1 : (byte)0);
            w.VarUInt(instrument.Reference);
        }

        w.U16((ushort)eventTypes.Count);
        foreach (var item in eventTypes)
        {
            w.U8((byte)item.RawType);
            switch (item.RawType)
            {
                case 1:
                    // No optional-value flags are set, so all note fields follow.
                    w.U16((ushort)item.Instrument);
                    w.U8((byte)item.Velocity);
                    w.U8((byte)item.Pan);
                    w.U8((byte)item.Attr);
                    w.U16((ushort)item.Duration);
                    break;
                case 2:
                case 4:
                    w.U8((byte)item.ByteValue);
                    break;
                case 3:
                    w.F32(item.FloatValue);
                    break;
                case 30:
                    throw new InvalidDataException("ARES kind 30 is unsupported by the original game and must be converted before export.");
            }
        }

        w.U16((ushort)eventFrequencies.Count);
        foreach (var frequency in eventFrequencies)
            w.VarUInt(frequency);

        HuffmanNode? root = BuildHuffmanTree(eventFrequencies);
        var codes = BuildCodes(root, eventFrequencies.Count);

        w.U8((byte)tracks.Count);
        foreach (var track in tracks)
        {
            var (bits, bitLength) = EncodeSymbols(track.Symbols, codes, root?.IsLeaf == true);
            w.U8((byte)track.TrackIndex);
            w.U32((uint)track.Events.Count);
            w.U32((uint)bitLength);
            w.Blob(bits);
            foreach (var (ev, _) in track.Events)
                w.VarUInt((uint)ev.Tick);
        }
    }

    private static HuffmanNode? BuildHuffmanTree(IReadOnlyList<uint> frequencies)
    {
        if (frequencies.Count == 0)
            return null;

        var first = frequencies.Select((frequency, symbol) => new HuffmanNode
            {
                Frequency = frequency,
                Symbol = symbol
            })
            .OrderBy(node => node.Frequency)
            .ThenBy(node => node.Symbol)
            .ToList();
        var second = new List<HuffmanNode>();
        var firstIndex = 0;
        var secondIndex = 0;

        HuffmanNode PopLowest()
        {
            var hasFirst = firstIndex < first.Count;
            var hasSecond = secondIndex < second.Count;
            if (hasFirst && hasSecond)
                return first[firstIndex].Frequency <= second[secondIndex].Frequency
                    ? first[firstIndex++]
                    : second[secondIndex++];
            if (hasFirst)
                return first[firstIndex++];
            return second[secondIndex++];
        }

        while ((first.Count - firstIndex) + (second.Count - secondIndex) > 1)
        {
            var a = PopLowest();
            var b = PopLowest();
            second.Add(new HuffmanNode
            {
                Frequency = a.Frequency + b.Frequency,
                Left = a,
                Right = b
            });
        }
        return firstIndex < first.Count ? first[firstIndex] : second[secondIndex];
    }

    private static string[] BuildCodes(HuffmanNode? root, int symbolCount)
    {
        var codes = new string[symbolCount];
        if (root is null)
            return codes;

        void Visit(HuffmanNode node, string prefix)
        {
            if (node.IsLeaf)
            {
                codes[node.Symbol] = prefix.Length == 0 ? "0" : prefix;
                return;
            }
            Visit(node.Left!, prefix + "0");
            Visit(node.Right!, prefix + "1");
        }
        Visit(root, string.Empty);
        return codes;
    }

    private static (byte[] Bytes, int BitLength) EncodeSymbols(
        IReadOnlyList<int> symbols,
        IReadOnlyList<string> codes,
        bool singleSymbolTree)
    {
        var bitLengthLong = singleSymbolTree ? symbols.Count : symbols.Sum(symbol => (long)codes[symbol].Length);
        if (bitLengthLong > int.MaxValue - 7)
            throw new InvalidDataException("The ARES Huffman bit stream exceeds the supported in-memory size.");
        var bitLength = (int)bitLengthLong;
        var bytes = new byte[(bitLength + 7) / 8];
        var bitPosition = 0;
        foreach (var symbol in symbols)
        {
            if ((uint)symbol >= (uint)codes.Count)
                throw new InvalidDataException($"Invalid ARES event-type reference {symbol}.");
            var code = codes[symbol];
            if (singleSymbolTree)
            {
                // The current decoder emits the root symbol once per bit when
                // the tree contains one leaf, so one zero bit per event is needed.
                bitPosition++;
                continue;
            }
            foreach (var bit in code)
            {
                if (bit == '1')
                    bytes[bitPosition >> 3] |= (byte)(1 << (bitPosition & 7));
                bitPosition++;
            }
        }
        return (bytes, bitLength);
    }

    private static byte[] CreateHeader(byte[]? sourceHeader)
    {
        if (sourceHeader is { Length: >= HeaderSize } && sourceHeader.AsSpan(0, 4).SequenceEqual("ARES"u8))
            return sourceHeader.AsSpan(0, HeaderSize).ToArray();

        var header = new byte[HeaderSize];
        "ARES"u8.CopyTo(header);
        // The outer header's first two words are not consumed by the known B2
        // reader. Use zeroes for a fresh file and deterministic stream keys.
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(12, 8), 0x0123456789ABCDEFUL);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(20, 8), 0xFEDCBA9876543210UL);
        return header;
    }

    private static byte[] Encrypt(byte[] plain, byte[] header)
    {
        var transform = new StreamTransform(header);
        var result = new byte[plain.Length];
        for (var i = 0; i < plain.Length; i++)
            result[i] = transform.Xor(plain[i]);
        return result;
    }

    private sealed class StreamTransform
    {
        private ulong _a;
        private ulong _b;
        private ulong _cr;
        private int _kl;

        public StreamTransform(ReadOnlySpan<byte> header)
        {
            var key1 = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(12, 8));
            var key2 = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(20, 8));
            var xs0 = 0x89C05128439AF8B2UL;
            var xs1 = 0x56EA3A45EFBD85EEUL;
            for (var i = 0; i < 10; i++)
                _ = XspNext(ref xs0, ref xs1);
            key1 ^= XspNext(ref xs0, ref xs1);
            key2 ^= XspNext(ref xs0, ref xs1);
            _a = key1;
            _b = key2;
            XrsrJump();
            XrsrJump();
            _cr = XrsrNext();
        }

        public byte Xor(byte value)
        {
            var output = (byte)(value ^ ((_cr >> ((_kl & 7) * 8)) & 0xFF));
            _kl++;
            if (_kl == 8)
            {
                _cr = XrsrNext();
                _kl = 0;
            }
            return output;
        }

        private static ulong XspNext(ref ulong a, ref ulong b)
        {
            var baseValue = (a ^ (a << 23)) & ulong.MaxValue;
            var t = (baseValue ^ (b >> 9)) & ulong.MaxValue;
            t = ((t >> 17) ^ b ^ baseValue) & ulong.MaxValue;
            var oldB = b;
            a = oldB;
            b = t;
            return unchecked(t + oldB);
        }

        private static ulong Rot64(ulong x, int n) => (x << n) | (x >> (64 - n));

        private ulong XrsrNext()
        {
            var a = _a;
            var b = _b;
            var t = a ^ b;
            _a = ((a >> 9) ^ (t << 14) ^ t) | (a << 55);
            _b = Rot64(t, 36);
            return unchecked(a + b);
        }

        private void XrsrJump()
        {
            ulong a0 = 0;
            ulong a1 = 0;
            foreach (var jump in new[] { 0xBEAC0467EBA5FACBUL, 0xD86B048B86AA9922UL })
            {
                for (var bit = 0; bit < 64; bit++)
                {
                    if (((jump >> bit) & 1UL) != 0)
                    {
                        a0 ^= _a;
                        a1 ^= _b;
                    }
                    _ = XrsrNext();
                }
            }
            _a = a0;
            _b = a1;
        }
    }

    /// <summary>
    /// Produces the raw decrypted stream, including the periodic gap bytes that
    /// MappedReader skips. Each method corresponds to one MappedReader read call;
    /// retaining those call boundaries is essential to reproducing its mapping.
    /// </summary>
    private sealed class MappedPayloadWriter
    {
        private readonly List<byte[]> _chunks = new();

        public void U8(byte value) => _chunks.Add(new[] { value });

        public void U16(ushort value)
        {
            var bytes = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
            _chunks.Add(bytes);
        }

        public void U32(uint value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            _chunks.Add(bytes);
        }

        public void F32(float value) => U32(unchecked((uint)BitConverter.SingleToInt32Bits(value)));

        public void Blob(byte[] bytes)
        {
            if (bytes.Length > 0)
                _chunks.Add(bytes);
        }

        public void VarUInt(uint value)
        {
            if (value < 63)
            {
                U8((byte)value);
            }
            else if (value < VarUIntTop2)
            {
                var adjusted = value - VarUIntTop1;
                U8((byte)(0x40 | (adjusted & 0x3F)));
                U8((byte)(adjusted >> 6));
            }
            else if (value < VarUIntTop3)
            {
                var adjusted = value - VarUIntTop2;
                U8((byte)(0x80 | (adjusted & 0x3F)));
                // ARES stores continuation groups from low to high: bits 6-13,
                // then bits 14-21. The native game reader consumes this order.
                U8((byte)(adjusted >> 6));
                U8((byte)(adjusted >> 14));
            }
            else if (value <= VarUIntMax)
            {
                var adjusted = value - VarUIntTop3;
                U8((byte)(0xC0 | (adjusted & 0x3F)));
                // Match native ARES continuation-byte order (6, 14, 22).
                U8((byte)(adjusted >> 6));
                U8((byte)(adjusted >> 14));
                U8((byte)(adjusted >> 22));
            }
            else
            {
                // ARES reserves the one-byte value 0x3F as an escape followed
                // by a full little-endian UInt32. The reader already supports
                // this form; use it for values outside the compact 30-bit range
                // (notably the 0xFFFFFFFF instrument-reference sentinel).
                U8(0x3F);
                U32(value);
            }
        }

        public byte[] Build()
        {
            using var output = new MemoryStream();
            var logicalPosition = 0;
            var nextGapPosition = 2;

            foreach (var chunk in _chunks)
            {
                var offset = 0;
                var left = chunk.Length;
                while (left > 0)
                {
                    if (logicalPosition + left <= nextGapPosition)
                    {
                        output.Write(chunk, offset, left);
                        logicalPosition += left;
                        left = 0;
                        break;
                    }

                    var take = Math.Max(0, nextGapPosition - logicalPosition);
                    if (take > 0)
                    {
                        output.Write(chunk, offset, take);
                        offset += take;
                        left -= take;
                    }
                    logicalPosition += take;

                    var discard = nextGapPosition % 8;
                    for (var i = 0; i < discard; i++)
                        output.WriteByte((byte)(0xA5 ^ ((logicalPosition + i) & 0xFF)));
                    logicalPosition += discard;
                    nextGapPosition = logicalPosition + (logicalPosition % 29);
                }
            }
            return output.ToArray();
        }
    }
}
