using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace TSC.Ares;

public sealed class AresLoader
{
    private const int HeaderSize = 28;
    private const ulong Mask64 = ulong.MaxValue;

    private readonly record struct EventTypeRecord(
        int Type, int Instrument, int Attr, int Duration, float Value, int Velocity, int Pan);

    private sealed class LogicalReader
    {
        private readonly byte[] _data;
        private int _pos;

        public LogicalReader(byte[] data) => _data = data;

        public byte U8()
        {
            Ensure(1);
            return _data[_pos++];
        }

        public ushort U16()
        {
            Ensure(2);
            var value = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(_pos, 2));
            _pos += 2;
            return value;
        }

        public uint U32()
        {
            Ensure(4);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_pos, 4));
            _pos += 4;
            return value;
        }

        public float F32() => BitConverter.Int32BitsToSingle(unchecked((int)U32()));

        public byte[] Read(int count)
        {
            Ensure(count);
            var result = _data.AsSpan(_pos, count).ToArray();
            _pos += count;
            return result;
        }

        public uint VarUInt()
        {
            var c = U8();
            var top = c >> 6;
            if (top == 0)
                return c == 0x3F ? U32() : c;
            if (top == 1)
                return ((uint)U8() << 6 | (uint)(c & 0x3F)) + 0x3Eu;
            if (top == 2)
                return ((uint)U8() << 6 | (uint)U8() << 14 | (uint)(c & 0x3F)) + 0x403Du;
            return ((uint)U8() << 6 | (uint)U8() << 14 | (uint)U8() << 22 | (uint)(c & 0x3F)) + 0x40403Cu;
        }

        private void Ensure(int count)
        {
            if (count < 0 || _pos + count > _data.Length)
                throw new EndOfStreamException("ARES logical payload ended unexpectedly.");
        }
    }

    private sealed class StreamTransform
    {
        private ulong _a;
        private ulong _b;
        private ulong _cr;
        private int _kl;

        public StreamTransform(ReadOnlySpan<byte> header)
        {
            if (header.Length != HeaderSize || !header[..4].SequenceEqual("ARES"u8))
                throw new InvalidDataException("Invalid ARES header.");

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
            var baseValue = (a ^ ((a << 23) & Mask64)) & Mask64;
            var t = (baseValue ^ (b >> 9)) & Mask64;
            t = ((t >> 17) ^ b ^ baseValue) & Mask64;
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

    private sealed class MappedReader
    {
        private readonly byte[] _payload;
        private readonly StreamTransform _transform;
        private readonly List<byte> _logicalBytes = new();
        private int _pos;
        private int _logical;
        private int _gap = 2;

        public MappedReader(byte[] payload, ReadOnlySpan<byte> header)
        {
            _payload = payload;
            _transform = new StreamTransform(header);
        }

        public int RawPosition => _pos;
        public int RawLength => _payload.Length;
        public byte[] LogicalBytes => _logicalBytes.ToArray();

        private byte RawByte()
        {
            if (_pos >= _payload.Length)
                throw new EndOfStreamException("ARES encrypted stream ended unexpectedly.");
            return _transform.Xor(_payload[_pos++]);
        }

        public byte[] Read(int count)
        {
            var output = new byte[count];
            var outPos = 0;
            var left = count;

            while (left > 0)
            {
                var inc = _logical;
                if (inc + left <= _gap)
                {
                    for (var i = 0; i < left; i++)
                    {
                        var value = RawByte();
                        output[outPos++] = value;
                        _logicalBytes.Add(value);
                    }
                    _logical += left;
                    left = 0;
                    break;
                }

                var take = Math.Max(0, _gap - inc);
                for (var i = 0; i < take; i++)
                {
                    var value = RawByte();
                    output[outPos++] = value;
                    _logicalBytes.Add(value);
                }
                _logical += take;
                left -= take;

                var discard = _gap % 8;
                for (var i = 0; i < discard; i++)
                    _ = RawByte();
                _logical += discard;
                _gap = _logical + (_logical % 29);
            }

            return output;
        }

        public byte U8() => Read(1)[0];
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Read(2));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Read(4));
        public float F32() => BitConverter.Int32BitsToSingle(unchecked((int)U32()));

        public uint VarUInt()
        {
            var c = U8();
            var top = c >> 6;
            if (top == 0)
                return c == 0x3F ? U32() : c;
            if (top == 1)
                return ((uint)U8() << 6 | (uint)(c & 0x3F)) + 0x3Eu;
            if (top == 2)
                return ((uint)U8() << 6 | (uint)U8() << 14 | (uint)(c & 0x3F)) + 0x403Du;
            return ((uint)U8() << 6 | (uint)U8() << 14 | (uint)U8() << 22 | (uint)(c & 0x3F)) + 0x40403Cu;
        }
    }

    private sealed class HuffmanNode
    {
        public ulong Frequency { get; init; }
        public int Symbol { get; init; } = -1;
        public HuffmanNode? Left { get; init; }
        public HuffmanNode? Right { get; init; }
        public bool IsLeaf => Left is null && Right is null;
    }

    private static HuffmanNode? BuildHuffmanTree(IReadOnlyList<ulong> frequencies)
    {
        if (frequencies.Count == 0)
            return null;

        var first = frequencies.Select((f, i) => new HuffmanNode { Frequency = f, Symbol = i })
            .OrderBy(n => n.Frequency).ThenBy(n => n.Symbol).ToList();
        var second = new List<HuffmanNode>();
        var firstIndex = 0;
        var secondIndex = 0;

        HuffmanNode PopLowest()
        {
            var hasFirst = firstIndex < first.Count;
            var hasSecond = secondIndex < second.Count;
            if (hasFirst && hasSecond)
            {
                if (first[firstIndex].Frequency <= second[secondIndex].Frequency)
                    return first[firstIndex++];
                return second[secondIndex++];
            }
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

    private static List<int> DecodeHuffman(HuffmanNode? root, byte[] bits, int bitLength)
    {
        if (bitLength == 0)
            return new List<int>();
        if (root is null)
            throw new InvalidDataException("ARES Huffman stream has no tree.");
        if (root.IsLeaf)
            return Enumerable.Repeat(root.Symbol, bitLength).ToList();

        var output = new List<int>();
        var node = root;
        for (var i = 0; i < bitLength; i++)
        {
            var bit = (bits[i >> 3] >> (i & 7)) & 1;
            node = bit == 0 ? node.Left! : node.Right!;
            if (node is null)
                throw new InvalidDataException("Invalid ARES Huffman path.");
            if (node.IsLeaf)
            {
                output.Add(node.Symbol);
                node = root;
            }
        }
        if (!ReferenceEquals(node, root))
            throw new InvalidDataException("ARES Huffman stream ended mid-symbol.");
        return output;
    }

    public static AresChart Load(string path)
        => LoadBytes(File.ReadAllBytes(path));

    public static AresChart LoadBytes(byte[] raw)
    {
        if (raw.Length < HeaderSize || !raw.AsSpan(0, 4).SequenceEqual("ARES"u8))
            throw new InvalidDataException("Not a supported ARES file.");

        var header = raw.AsSpan(0, HeaderSize).ToArray();
        var payload = raw.AsSpan(HeaderSize).ToArray();
        var mapped = new MappedReader(payload, header);
        ConsumeB2Structure(mapped);
        if (mapped.RawPosition != mapped.RawLength)
            throw new InvalidDataException($"ARES parser left {mapped.RawLength - mapped.RawPosition} encrypted bytes unread.");

        var chart = ParseLogical(mapped.LogicalBytes);
        // Temporary migration layer: recognize the ordinary track-30 metadata
        // emitted by the editor and restore the internal disabled-tempo event.
        // Legacy ARES kind-30 events are still accepted below for old project files.
        ReconstructDisabledTempoMetadata(chart);
        chart.AresHeaderTemplate = header;
        chart.AresTrackCount = chart.Tracks.Count;
        chart.InitialGearAttr = DetectInitialGearAttr(chart);
        chart.AudioOriginTick = DetectAudioOriginTick(chart);
        FinalizeChart(chart, reconstructAresCallbacks: true);
        return chart;
    }

    private static void ConsumeB2Structure(MappedReader r)
    {
        _ = r.U32();
        _ = r.U16();
        _ = r.F32();
        _ = r.U32();
        _ = r.F32();

        var instrumentNameCount = r.U16();
        for (var i = 0; i < instrumentNameCount; i++)
            r.Read(r.U8());

        var instrumentHuffmanCount = r.U16();
        for (var i = 0; i < instrumentHuffmanCount; i++)
            _ = r.VarUInt();

        var instrumentCount = r.U16();
        var instrumentBits = r.U32();
        r.Read((checked((int)instrumentBits) + 7) / 8);
        for (var i = 0; i < instrumentCount; i++)
        {
            _ = r.U16();
            _ = r.U8();
            _ = r.VarUInt();
        }

        var eventTypeCount = r.U16();
        for (var i = 0; i < eventTypeCount; i++)
        {
            var type = r.U8();
            switch (type)
            {
                case 1:
                    var word = r.U16();
                    if ((word & 0x8000) == 0) _ = r.U8();
                    if ((word & 0x4000) == 0) _ = r.U8();
                    if ((word & 0x2000) == 0) _ = r.U8();
                    if ((word & 0x1000) == 0) _ = r.U16();
                    break;
                case 2:
                case 4:
                    _ = r.U8();
                    break;
                case 3:
                    _ = r.F32();
                    break;
                case 5:
                    throw new NotSupportedException("ARES User event type 5 is not decoded yet.");
                default:
                    throw new InvalidDataException($"Unknown ARES event type {type}.");
            }
        }

        var eventHuffmanCount = r.U16();
        for (var i = 0; i < eventHuffmanCount; i++)
            _ = r.VarUInt();

        var trackCount = r.U8();
        for (var i = 0; i < trackCount; i++)
        {
            _ = r.U8();
            var eventCount = r.U32();
            var bitLength = r.U32();
            r.Read((checked((int)bitLength) + 7) / 8);
            for (var e = 0; e < eventCount; e++)
                _ = r.VarUInt();
        }
    }

    private static AresChart ParseLogical(byte[] logical)
    {
        var r = new LogicalReader(logical);
        var chart = new AresChart
        {
            RawMagic = r.U32(),
            TicksPerMeasure = r.U16(),
            InitialTempo = r.F32(),
            PlayTick = checked((int)r.U32()),
            PlayTime = r.F32()
        };

        var nameCount = r.U16();
        for (var i = 0; i < nameCount; i++)
        {
            var length = r.U8();
            chart.InstrumentNames.Add(Encoding.UTF8.GetString(r.Read(length)));
        }

        var instrumentHuffmanCount = r.U16();
        var instrumentFrequencies = new ulong[instrumentHuffmanCount];
        for (var i = 0; i < instrumentHuffmanCount; i++)
        {
            instrumentFrequencies[i] = r.VarUInt();
            chart.InstrumentNameHuffmanFrequencies.Add((uint)instrumentFrequencies[i]);
        }
        var instrumentRoot = BuildHuffmanTree(instrumentFrequencies);

        var instrumentCount = r.U16();
        var instrumentBits = checked((int)r.U32());
        var instrumentRefs = DecodeHuffman(instrumentRoot, r.Read((instrumentBits + 7) / 8), instrumentBits);
        chart.InstrumentNameReferences.AddRange(instrumentRefs);
        for (var i = 0; i < instrumentCount; i++)
        {
            var instrumentNumber = r.U16();
            var streamed = r.U8() != 0;
            var reference = r.VarUInt();
            chart.Instruments.Add(new AresInstrumentDefinition(instrumentNumber, streamed, reference));
        }

        var eventTypeCount = r.U16();
        var eventTypes = new EventTypeRecord[eventTypeCount];
        for (var i = 0; i < eventTypeCount; i++)
        {
            var type = r.U8();
            switch (type)
            {
                case 1:
                    var word = r.U16();
                    var velocity = (word & 0x8000) != 0 ? 127 : r.U8();
                    var pan = (word & 0x4000) != 0 ? 64 : r.U8();
                    var attr = (word & 0x2000) != 0 ? 0 : r.U8();
                    var duration = (word & 0x1000) != 0 ? 6 : r.U16();
                    eventTypes[i] = new EventTypeRecord(1, word & 0x0FFF, attr, duration, 0, velocity, pan);
                    break;
                case 2:
                case 4:
                    eventTypes[i] = new EventTypeRecord(type, 0, 0, 0, r.U8(), 127, 64);
                    break;
                case 3:
                    eventTypes[i] = new EventTypeRecord(type, 0, 0, 0, r.F32(), 127, 64);
                    break;
                case 5:
                    throw new NotSupportedException("ARES User event type 5 is not decoded yet.");
                default:
                    throw new InvalidDataException($"Unknown ARES event type {type}.");
            }
        }

        var eventHuffmanCount = r.U16();
        var eventFrequencies = new ulong[eventHuffmanCount];
        for (var i = 0; i < eventHuffmanCount; i++)
            eventFrequencies[i] = r.VarUInt();
        var eventRoot = BuildHuffmanTree(eventFrequencies);

        var trackCount = r.U8();
        for (var trackNo = 0; trackNo < trackCount; trackNo++)
        {
            var trackIndex = r.U8();
            var eventCount = checked((int)r.U32());
            var bitLength = checked((int)r.U32());
            var references = DecodeHuffman(eventRoot, r.Read((bitLength + 7) / 8), bitLength);
            if (references.Count < eventCount)
                throw new InvalidDataException($"Track {trackIndex}: decoded {references.Count} events, expected {eventCount}.");

            var track = new ChartTrack { TrackIndex = trackIndex };
            for (var eventIndex = 0; eventIndex < eventCount; eventIndex++)
            {
                var tick = checked((int)r.VarUInt());
                var typeIndex = references[eventIndex];
                if ((uint)typeIndex >= (uint)eventTypes.Length)
                    throw new InvalidDataException($"Track {trackIndex}: event type {typeIndex} is out of range.");
                var type = eventTypes[typeIndex];

                if (type.Type == 1)
                {
                    var noteType = GetNoteTypeForTrack(trackIndex, type.Attr, type.Duration);
                    track.Events.Add(new ChartEvent
                    {
                        Tick = tick,
                        Track = trackIndex,
                        Attr = type.Attr,
                        RawType = type.Type,
                        TypeIndex = typeIndex,
                        Duration = type.Duration,
                        NoteType = noteType,
                        EventIndex = eventIndex,
                        LinePair = noteType == NoteType.LinkHead ? type.Attr % 10 : noteType == NoteType.LinkTail ? type.Attr % 10 - 1 : -1,
                        Kind = "Note",
                        Instrument = type.Instrument,
                        Velocity = type.Velocity,
                        Pan = type.Pan
                    });
                }
                else if (type.Type == 3)
                {
                    chart.TempoEvents.Add((tick, type.Value));
                    track.Events.Add(new ChartEvent
                    {
                        Tick = tick,
                        Track = trackIndex,
                        RawType = type.Type,
                        TypeIndex = typeIndex,
                        EventIndex = eventIndex,
                        Kind = "Tempo",
                        Value = type.Value
                    });
                }
                else if (type.Type == 30)
                {
                    // Type 30 mirrors a tempo payload but is deliberately inert in
                    // the active tempo map. It stores the generator's ending BPM so
                    // removing an overriding type-3 event can restore it exactly.
                    track.Events.Add(new ChartEvent
                    {
                        Tick = tick,
                        Track = trackIndex,
                        RawType = 30,
                        TypeIndex = typeIndex,
                        EventIndex = eventIndex,
                        Kind = "DisabledTempo",
                        Value = type.Value
                    });
                }
                else if (type.Type == 2 || type.Type == 4)
                {
                    track.Events.Add(new ChartEvent
                    {
                        Tick = tick,
                        Track = trackIndex,
                        RawType = type.Type,
                        TypeIndex = typeIndex,
                        EventIndex = eventIndex,
                        Kind = type.Type == 2 ? "Volume" : "Beat",
                        Value = type.Value
                    });
                }
            }
            chart.Tracks.Add(track);
        }

        _ = instrumentRefs;
        return chart;
    }

    private static void ReconstructDisabledTempoMetadata(AresChart chart)
    {
        var metadataTrack = chart.FindTrack(30);
        if (metadataTrack is null)
            return;

        var candidatesByTick = metadataTrack.Events
            .Where(ev => ev.RawType == 1 && ev.Kind == "Note" && ev.Instrument == 0 &&
                ev.Duration == 1 && ev.Attr is 10 or 11 or 12)
            .GroupBy(ev => ev.Tick)
            .ToList();
        foreach (var tickGroup in candidatesByTick)
        {
            // The writer appends each backup as a consecutive [10, 11, 12]
            // triplet. Process in event order so even multiple legacy backups at
            // one tick can be converted deterministically.
            var ordered = tickGroup.OrderBy(ev => ev.EventIndex).ToList();
            for (var i = 0; i + 2 < ordered.Count;)
            {
                var low = ordered[i];
                var high = ordered[i + 1];
                var source = ordered[i + 2];
                if (low.Attr != 10 || high.Attr != 11 || source.Attr != 12 ||
                    source.Pan != 0 || source.Velocity is < 0 or > byte.MaxValue)
                {
                    i++;
                    continue;
                }

                var bits = unchecked((uint)((low.Velocity & 0xFF) << 8 | (low.Pan & 0xFF)) |
                    ((uint)((high.Velocity & 0xFF) << 8 | (high.Pan & 0xFF)) << 16));
                var bpm = BitConverter.Int32BitsToSingle(unchecked((int)bits));
                var sourceTrackIndex = source.Velocity;
                if (!float.IsFinite(bpm) || bpm <= 0f)
                {
                    // Preserve malformed or unrelated notes instead of silently
                    // interpreting them as tempo metadata.
                    i += 3;
                    continue;
                }

                var sourceTrack = chart.FindTrack(sourceTrackIndex);
                if (sourceTrack is null)
                {
                    sourceTrack = new ChartTrack { TrackIndex = sourceTrackIndex };
                    chart.Tracks.Add(sourceTrack);
                }
                var eventIndex = sourceTrack.Events.Count == 0 ? 0 : sourceTrack.Events.Max(ev => ev.EventIndex) + 1;
                sourceTrack.Events.Add(new ChartEvent
                {
                    Tick = low.Tick,
                    Track = sourceTrackIndex,
                    Attr = 0,
                    RawType = 30,
                    TypeIndex = -1,
                    EventIndex = eventIndex,
                    Kind = "DisabledTempo",
                    Value = bpm
                });

                metadataTrack.Events.Remove(low);
                metadataTrack.Events.Remove(high);
                metadataTrack.Events.Remove(source);
                i += 3;
            }
        }
    }

    internal static int DetectInitialGearAttr(AresChart chart)
    {
        var trackEvents = chart.FindTrack(16)?.Events;
        if (trackEvents is null || trackEvents.Count == 0)
            return 6;

        // Track 16 carries the start-gear marker as instrument 1. Attribute 2
        // is another valid 1K encoding, equivalent to attribute 0. Tick zero is
        // valid too; excluding it made otherwise valid ARES charts fall back to 3K.
        var candidates = trackEvents
            .Where(e => e.RawType == 1 && e.Instrument == 1 && IsStartGearAttribute(e.Attr))
            .OrderBy(e => e.Tick)
            .ThenBy(e => e.EventIndex)
            .ToList();
        if (candidates.Count == 0)
            return 6;

        // Prefer the marker near the beginning of the chart, but don't silently
        // discard it if a valid original chart places it beyond the usual window.
        var limit = Math.Max(chart.TicksPerMeasure * 2, chart.TicksPerMeasure + 96);
        return candidates.FirstOrDefault(e => e.Tick >= 0 && e.Tick <= limit)?.Attr
            ?? candidates[0].Attr;
    }

    internal static bool IsStartGearAttribute(int attr) => attr is 0 or 2 or 4 or 6;

    internal static NoteType GetNoteType(int tens, int ones, int duration)
    {
        if (tens is 2 or 4 or 6 or 7 or 8 or 9)
            return duration > 6 ? NoteType.SingleLong : NoteType.Single;
        return ones switch
        {
            1 or 3 or 5 or 7 => NoteType.LinkHead,
            2 or 4 or 6 or 8 => NoteType.LinkTail,
            9 => NoteType.Vibrato,
            _ => duration > 6 ? NoteType.SingleLong : NoteType.Single
        };
    }

    /// <summary>Resolve note types while respecting the separate panel-animation tracks.</summary>
    internal static NoteType GetNoteTypeForTrack(int track, int attr, int duration)
    {
        // Track 17-20 attrs 101-108 are panel animation cues, not link pairs.
        // Their duration is a visual hold, while the attribute selects animation.
        if (track is >= 17 and <= 20 && attr is >= 101 and <= 108)
            return duration > 6 ? NoteType.SingleLong : NoteType.Single;

        return GetNoteType(attr / 10, attr % 10, duration);
    }

    internal static int DetectAudioOriginTick(AresChart chart)
    {
        var candidate = chart.FindTrack(16)?.Events
            .Where(e => e.RawType == 1 && e.Instrument == 1 && IsStartGearAttribute(e.Attr))
            .OrderBy(e => e.Tick)
            .ThenBy(e => e.EventIndex)
            .FirstOrDefault();
        return candidate?.Tick ?? 0;
    }

    /// <summary>
    /// Track 16 also carries a dedicated end marker (instrument 2, attr 0,
    /// default duration 6). Its tick mirrors the B2 PlayTick header field.
    /// Keep that auxiliary event synchronized whenever PlayTick is explicitly
    /// edited, auto-calculated from a cleared field, or loaded from chart metadata.
    /// </summary>
    internal static bool IsPlayTickMarker(ChartEvent ev) =>
        ev.Track == 16 && ev.RawType == 1 && ev.Instrument == 2 && ev.Attr == 0 && ev.Duration == 6;

    internal static void SynchronizePlayTickMarker(AresChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        if (chart.PlayTick < 0)
            throw new InvalidDataException("Play tick cannot be negative.");

        var track = chart.FindTrack(16);
        if (track is null)
        {
            track = new ChartTrack { TrackIndex = 16 };
            chart.Tracks.Add(track);
        }

        // On native charts the terminal marker is normally the latest matching
        // instrument-2 event on track 16. Reuse it rather than creating a second
        // marker when play time changes.
        var markers = track.Events
            .Where(IsPlayTickMarker)
            .OrderByDescending(e => e.Tick)
            .ThenByDescending(e => e.EventIndex)
            .ToList();
        if (markers.Count > 0)
        {
            markers[0].Tick = chart.PlayTick;
            // The native format has one terminal marker. If an earlier edit
            // produced copies of it, collapse those duplicates while syncing.
            foreach (var duplicate in markers.Skip(1))
                track.Events.Remove(duplicate);
        }
        else
        {
            var nextEventIndex = track.Events.Count == 0 ? 0 : track.Events.Max(e => e.EventIndex) + 1;
            track.Events.Add(new ChartEvent
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
        }

        // The flattened event list is tick-sorted and also used by editor
        // selection/render caches. Refresh it after moving/inserting the marker.
        FinalizeEvents(chart);
    }

    private static void ReconstructMainLoop(AresChart chart)
    {
        if (chart.PlayTick <= 0)
            return;
        var loopTracks = chart.Tracks.Where(t => t.TrackIndex is >= 6 and <= 11).OrderBy(t => t.TrackIndex).ToList();
        if (loopTracks.Count == 0)
            return;

        string Signature(int tick) => string.Join("|", loopTracks
            .SelectMany(t => t.Events.Where(e => e.Tick == tick).Select(e => $"{t.TrackIndex}:{e.Attr}:{e.Duration}"))
            .OrderBy(s => s));

        var preTicks = loopTracks.SelectMany(t => t.Events).Where(e => e.Tick <= chart.PlayTick).Select(e => e.Tick).Distinct().OrderBy(t => t).ToList();
        var postTicks = loopTracks.SelectMany(t => t.Events).Where(e => e.Tick > chart.PlayTick).Select(e => e.Tick).Distinct().OrderBy(t => t).ToList();
        if (preTicks.Count == 0 || postTicks.Count == 0)
            return;

        (int Length, int SourceStart)? best = null;
        for (var postIndex = 0; postIndex < Math.Min(12, postTicks.Count); postIndex++)
        {
            var postSignature = Signature(postTicks[postIndex]);
            if (string.IsNullOrEmpty(postSignature))
                continue;
            for (var sourceIndex = 0; sourceIndex < preTicks.Count; sourceIndex++)
            {
                if (Signature(preTicks[sourceIndex]) != postSignature)
                    continue;
                var n = 0;
                while (postIndex + n < postTicks.Count && sourceIndex + n < preTicks.Count && Signature(postTicks[postIndex + n]) == Signature(preTicks[sourceIndex + n]))
                    n++;
                if (n >= 3 && (best is null || n > best.Value.Length))
                    best = (n, preTicks[sourceIndex]);
            }
        }

        if (best is null)
            return;

        var targetStart = preTicks.Max() + 48;
        var continuationLength = chart.PlayTick - targetStart;
        if (continuationLength <= 0)
            return;

        var sourceStart = best.Value.SourceStart;
        var sourceEnd = sourceStart + continuationLength;
        var byTrack = loopTracks.ToDictionary(t => t.TrackIndex);
        var sourceEvents = loopTracks.SelectMany(t => t.Events.Where(e => e.Tick >= sourceStart && e.Tick < sourceEnd)
            .Select(e => (Track: t, Event: e))).ToList();
        foreach (var (track, source) in sourceEvents)
        {
            track.Events.Add(new ChartEvent
            {
                Tick = source.Tick + (targetStart - sourceStart),
                Track = source.Track,
                Attr = source.Attr,
                Duration = source.Duration,
                NoteType = source.NoteType,
                EventIndex = track.Events.Count,
                LinePair = source.LinePair,
                Instrument = source.Instrument,
                Kind = source.Kind
            });
        }

        chart.MainLoopDetected = true;
        chart.MainLoopSourceTick = sourceStart;
        chart.MainLoopTargetTick = targetStart;
    }

    internal static void ReconstructTwinNotes(AresChart chart)
    {
        var notes = chart.Tracks.Where(t => t.TrackIndex is >= 0 and <= 15)
            .SelectMany(t => t.Events.Select(e => (Track: t.TrackIndex, Event: e)))
            .Where(x => x.Event.Kind == "Note")
            .GroupBy(x => x.Event.Tick);
        var pairId = 0;
        foreach (var tickGroup in notes)
        {
            var items = tickGroup.OrderBy(x => x.Track).ThenBy(x => x.Event.EventIndex).ToList();
            var paired = new HashSet<(int Track, int EventIndex)>();
            for (var i = 0; i < items.Count; i++)
            {
                var a = items[i];
                if (paired.Contains((a.Track, a.Event.EventIndex)) || !IsTwinType(a.Event.NoteType))
                    continue;
                for (var j = i + 1; j < items.Count; j++)
                {
                    var b = items[j];
                    if (a.Track == b.Track || paired.Contains((b.Track, b.Event.EventIndex)))
                        continue;
                    if (!IsComplementary(a.Event.NoteType, b.Event.NoteType))
                        continue;
                    if (((NativeSide(a.Track) - 1) / 2) != ((NativeSide(b.Track) - 1) / 2))
                        continue;
                    a.Event.TwinPairId = pairId;
                    b.Event.TwinPairId = pairId;
                    paired.Add((a.Track, a.Event.EventIndex));
                    paired.Add((b.Track, b.Event.EventIndex));
                    pairId++;
                    break;
                }
            }
        }
    }

    private static bool IsTwinType(NoteType type) => type is NoteType.SingleLong or NoteType.LinkHead or NoteType.LinkTail or NoteType.Vibrato;

    private static bool IsComplementary(NoteType a, NoteType b) =>
        (a == NoteType.SingleLong && b == NoteType.Vibrato) ||
        (a == NoteType.Vibrato && b == NoteType.SingleLong) ||
        (a == NoteType.LinkHead && b == NoteType.LinkTail) ||
        (a == NoteType.LinkTail && b == NoteType.LinkHead);

    private static int NativeSide(int track) => track switch
    {
        >= 0 and <= 2 => 1,
        >= 3 and <= 5 => 2,
        >= 6 and <= 8 => 3,
        >= 9 and <= 11 => 4,
        >= 12 and <= 13 => 5,
        >= 14 and <= 15 => 6,
        _ => 7
    };

    internal static void FinalizeChart(AresChart chart, bool reconstructAresCallbacks)
    {
        if (reconstructAresCallbacks)
        {
            CallbackReconstructor.Reconstruct(chart);
        }

        ReconstructTwinNotes(chart);
        FinalizeEvents(chart);
        LinkBuilder.Build(chart);
    }

    internal static void FinalizeEvents(AresChart chart)
    {
        chart.Events.Clear();
        foreach (var track in chart.Tracks)
        {
            track.Events.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.EventIndex.CompareTo(b.EventIndex));
            chart.Events.AddRange(track.Events);
        }
        chart.Events.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Track.CompareTo(b.Track));
    }
}
