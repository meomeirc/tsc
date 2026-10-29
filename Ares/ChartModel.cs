using Microsoft.Xna.Framework;

namespace TSC.Ares;

public enum NoteType
{
    Single = 0,
    SingleLong = 1,
    LinkHead = 2,
    LinkTail = 3,
    Vibrato = 4,
    End = 5
}

public enum SuperFlickFamily
{
    None,
    Outer,
    Inner
}

/// <summary>Bundle layout selected when creating a new editor chart.</summary>
public enum ChartBundleTemplate
{
    Tsc,
    Bold,
    Tsts
}

public sealed class ChartEvent
{
    public int Tick { get; set; }
    public int Track { get; set; }
    public int Attr { get; set; }
    public int RawType { get; set; }
    public int TypeIndex { get; set; }
    public int Duration { get; set; }
    public NoteType NoteType { get; set; }
    public int EventIndex { get; set; }
    public int LinePair { get; set; } = -1;
    public int? TwinPairId { get; set; }
    public bool LoopReconstructed { get; set; }
    public string? LoopPhase { get; set; }
    public string? Kind { get; set; }
    public float Value { get; set; }
    public int Instrument { get; init; }
    public int Velocity { get; init; } = 127;
    public int Pan { get; init; } = 64;

    public int EndTick => Tick + Math.Max(1, Duration);
    public bool IsHoldNote => NoteType is NoteType.SingleLong or NoteType.Vibrato;
    // Small/ordinary flick notes: 10=left, 30=up, 50=right.
    // These do not change gear/bank and are intentionally separate from SuperFlick.
    public bool IsFlick => Attr is 10 or 50;
    public int FlickDirection => IsFlick ? Attr / 10 : 0;

    // ARES SuperFlick attributes are exact path identifiers, not a general
    // direction field. Keep this explicit so unrelated/unknown attributes are
    // never treated as SuperFlicks by integer division.
    public bool IsSuperFlick => Attr is 20 or 40 or 60 or 70 or 80 or 90;

    /// <summary>Resolve the SuperFlick side from both its raw attribute and source bank.</summary>
    public string? GetSuperFlickSide()
    {
        if (!IsSuperFlick)
            return null;

        // Attr 60/70 are bank-aware path IDs:
        //   1K source: 70=left, 60=right (1K->2K)
        //   3K source: 70=left, 60=right (3K->2K)
        //   2K source: 60=left, 70=right (2K->1K)
        var sourceBank = GearState.Position(Track)?.Bank;
        if (Attr is 60 or 70)
        {
            if (sourceBank == "Center")
                return Attr == 60 ? "left" : "right";
            return Attr == 70 ? "left" : "right";
        }

        return Attr switch
        {
            20 or 80 => "left",
            40 or 90 => "right",
            _ => null
        };
    }

    /// <summary>Visual side of the authored SuperFlick strip.</summary>
    public string? GetSuperFlickRenderSide() => GetSuperFlickSide();

    /// <summary>
    /// Resolve the visual family from the authored path and source track.
    /// 20/40 are outer 1K&lt;-&gt;3K paths; 80/90 are outer 2K-&gt;3K paths.
    /// For 60/70, the track bank identifies the path segment: Forward=inner
    /// 1K-&gt;2K, Backward=inner 3K-&gt;2K, Center=outer 2K-&gt;1K. Forward and Backward
    /// use 70=left/60=right; Center uses 60=left/70=right.
    /// The route state is intentionally not used to select a different family:
    /// malformed events must still render as authored for debugging.
    /// </summary>
    public SuperFlickFamily GetSuperFlickFamily(GearState? state = null)
    {
        _ = state; // Kept for call-site compatibility; the path is attribute/track-defined.
        if (!IsSuperFlick)
            return SuperFlickFamily.None;
        return Attr switch
        {
            20 or 40 or 80 or 90 => SuperFlickFamily.Outer,
            60 or 70 => GearState.Position(Track)?.Bank == "Center"
                ? SuperFlickFamily.Outer
                : SuperFlickFamily.Inner,
            _ => SuperFlickFamily.None
        };
    }

    /// <summary>
    /// The visible strip follows the authored track's bank. Gear state is only
    /// used to decide whether the SuperFlick is valid/hittable, never to move its
    /// rendering onto another bank. This also keeps malformed placements visible.
    /// </summary>
    public string? GetSuperFlickSourceBank(GearState? state = null)
    {
        _ = state; // Kept for call-site compatibility.
        return IsSuperFlick
            ? GearState.Position(Track)?.Bank ?? (Attr is 80 or 90 ? "Center" : null)
            : null;
    }

}

public sealed class ChartTrack
{
    public int TrackIndex { get; init; }
    public List<ChartEvent> Events { get; } = new();
}

/// <summary>Raw B2 instrument-table entry preserved for ARES re-export.</summary>
public sealed record AresInstrumentDefinition(int InstrumentNumber, bool Streamed, uint Reference);

public readonly record struct LinkSegment(ChartEvent Head, ChartEvent Tail);

public sealed class AresChart
{
    // Optional song/chart metadata used by the standalone TSC format.
    public string? Name { get; set; }
    public string? Artist { get; set; }
    public string? Series { get; set; }
    public string? Mapper { get; set; }
    public string? Description { get; set; }
    public string? Element { get; set; }
    // Song-wide values stored in song.json rather than in the ARES event stream.
    public string? Version { get; set; } = "1.0.0";
    public string? ScPasscode { get; set; }
    public string? Difficulty { get; set; }
    // Per-difficulty judgement timing windows (milliseconds). These are stored
    // in song.json for TSC, TSTS, and BOLD bundles. BOLD also retains its FPK
    // metadata entry for compatibility with the editor's package metadata.
    public double PerfectPlusWindowMs { get; set; } = 25.0;
    public double PerfectWindowMs { get; set; } = 70.0;
    public double GoodWindowMs { get; set; } = 120.0;
    public int? Level { get; set; }
    public string? MusicPath { get; set; }
    public string? PreviewPath { get; set; }
    public string? AlbumPath { get; set; }
    public string? AlbumBigPath { get; set; }
    public uint RawMagic { get; set; }
    // ARES/B2 has a fixed-width track table in original-game charts. Retain the
    // source count through TSC edits so empty auxiliary tracks are not discarded.
    public int AresTrackCount { get; set; } = 64;
    // The outer 28-byte ARES header contains opaque fields and the stream keys.
    // TSC stores this as opaque round-trip metadata when a chart originated as ARES.
    public byte[]? AresHeaderTemplate { get; set; }
    public int TicksPerMeasure { get; set; } = 192;
    // Song-wide metadata BPM (song.json `bpm`), used as the base BPM for
    // metadata-driven behavior such as MMOD. This is independent of the actual
    // chart starting tempo stored in InitialTempo and the tick-0 tempo event.
    public double MetadataBpm { get; set; } = 120;
    public double InitialTempo { get; set; } = 120;
    public int PlayTick { get; set; }
    public double PlayTime { get; set; }
    public List<ChartTrack> Tracks { get; } = new();
    public List<ChartEvent> Events { get; } = new();
    public List<string> InstrumentNames { get; } = new();
    public List<uint> InstrumentNameHuffmanFrequencies { get; } = new();
    public List<int> InstrumentNameReferences { get; } = new();
    public List<AresInstrumentDefinition> Instruments { get; } = new();
    public List<LinkSegment> Links { get; } = new();
    public List<(int Tick, double Bpm)> TempoEvents { get; } = new();
    public int InitialGearAttr { get; set; }
    public int AudioOriginTick { get; set; }
    public bool MainLoopGenerated { get; set; }
    public int MainLoopGeneratedEvents { get; set; }
    public int MainLoopLateGeneratedEvents { get; set; }
    public int MainLoopBridgeGeneratedEvents { get; set; }
    public int? MainLoopEndTargetTick { get; set; }
    public bool LateCallbackDetected { get; set; }
    public int? LateCallbackRawSeed { get; set; }
    public int? LateCallbackTargetSeed { get; set; }
    public int LateCallbackPeriod { get; set; }
    public int LateCallbackTargetStep { get; set; }
    public bool MainLoopDetected { get; set; }
    public int? MainLoopSourceTick { get; set; }
    public int? MainLoopTargetTick { get; set; }

    public double TicksPerBeat => TicksPerMeasure / 4.0;

    public ChartTrack? FindTrack(int index) => Tracks.FirstOrDefault(t => t.TrackIndex == index);
}

public readonly record struct TempoSegment(int Tick, double Bpm);

public sealed class TempoMap
{
    private readonly List<TempoSegment> _segments;
    private readonly double _ticksPerBeat;
    private readonly double[] _secondsAtSegmentStart;

    public TempoMap(AresChart chart)
    {
        _ticksPerBeat = chart.TicksPerBeat;
        _segments = new List<TempoSegment> { new(0, chart.InitialTempo) };
        _segments.AddRange(chart.TempoEvents.Where(x => x.Bpm > 0).Select(x => new TempoSegment(x.Tick, x.Bpm)));
        _segments.Sort((a, b) => a.Tick.CompareTo(b.Tick));

        _secondsAtSegmentStart = new double[_segments.Count];
        var seconds = 0.0;
        for (var i = 1; i < _segments.Count; i++)
        {
            var previous = _segments[i - 1];
            var segment = _segments[i];
            var deltaTicks = Math.Max(0.0, segment.Tick - previous.Tick);
            seconds += deltaTicks / _ticksPerBeat * 60.0 / Math.Max(1e-9, previous.Bpm);
            _secondsAtSegmentStart[i] = seconds;
        }
    }

    public double InitialBpm => _segments[0].Bpm;
    public double TicksPerBeat => _ticksPerBeat;
    public IReadOnlyList<TempoSegment> Segments => _segments;

    private int SegmentIndexAtTick(double tick)
    {
        var lo = 0;
        var hi = _segments.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (_segments[mid].Tick <= tick)
                lo = mid + 1;
            else
                hi = mid;
        }
        return Math.Max(0, lo - 1);
    }

    private int SegmentIndexAtSeconds(double seconds)
    {
        var lo = 0;
        var hi = _secondsAtSegmentStart.Length;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (_secondsAtSegmentStart[mid] <= seconds)
                lo = mid + 1;
            else
                hi = mid;
        }
        return Math.Max(0, lo - 1);
    }

    public double BpmAt(double tick) => _segments[SegmentIndexAtTick(tick)].Bpm;

    public double TickToSeconds(double tick)
    {
        if (tick <= 0)
            return 0;
        var index = SegmentIndexAtTick(tick);
        var segment = _segments[index];
        return _secondsAtSegmentStart[index] +
               (tick - segment.Tick) / _ticksPerBeat * 60.0 / Math.Max(1e-9, segment.Bpm);
    }

    public double SecondsToTick(double seconds)
    {
        if (seconds <= 0)
            return 0;
        var index = SegmentIndexAtSeconds(seconds);
        var segment = _segments[index];
        return segment.Tick +
               (seconds - _secondsAtSegmentStart[index]) * segment.Bpm * _ticksPerBeat / 60.0;
    }

    // This is a spatial coordinate, not elapsed time: every chart tick occupies
    // the same distance regardless of the tempo at that tick. BPM affects how
    // quickly playback advances through ticks, but never stretches the chart space.
    public double ScrollUnitsAtTick(double tick) => tick;

    public double ScrollUnitsBetweenTicks(double fromTick, double toTick) => toTick - fromTick;
}
