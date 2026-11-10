using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaColor = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using TSC.Ares;

namespace TSC.Rendering;

public sealed partial class Playfield
{
    private const float EditorNoteGroupMenuPanelHeight = 481f;

    private sealed record EditorNoteGroupShape(string Signature,
        Dictionary<string, List<NoteGroupMember>> MembersByKey);

    private sealed record EditorNoteGroupPatternInfo(
        NoteGroupDefinition Group, string CanonicalSignature, bool IsMirroredToCanonical);

    private sealed record EditorNoteGroupSourceRemap(
        NoteGroupDefinition Source, Dictionary<int, int> MemberMap, bool MirrorToRepresentative);

    private sealed record EditorNoteGroupConsolidationCluster(
        NoteGroupDefinition Representative, List<EditorNoteGroupSourceRemap> Sources);

    private sealed record EditorNoteGroupLinkPairReservation(
        int Pair, string? Family, int StartTick, long EndTick, bool HasTailAtStart,
        int? TerminalTailTick);

    // The placement ghost has its own small chart and render caches. Reuse these
    // collections across cursor updates instead of copying the full source chart
    // and allocating a fresh cache graph for every preview refresh.
    private sealed class EditorNoteGroupPlacementPreviewRenderCacheState
    {
        public AresChart? SourceChart;
        public bool IsValid;
        public int ContentHash;
        public readonly Dictionary<(int Tick, int Track, int EventIndex), ChartEvent> LinkRootByKey = new();
        public readonly Dictionary<ChartEventKey, LinkSegment> NextLinkByHeadKey = new();
        public readonly Dictionary<ChartEventKey, ChartEvent> LinkHeadByTailKey = new();
        public readonly HashSet<ChartEventKey> AmbiguousNextLinkHeadKeys = new();
        public readonly HashSet<ChartEventKey> LinkHeadKeys = new();
        public readonly List<ChartEvent> OrderedRenderableEvents = new();
        public readonly List<double> EventSeconds = new();
        public readonly List<double> EventScrollUnits = new();
        public readonly List<double> EventEffectiveEndSeconds = new();
        public readonly List<double> EventEffectiveEndScrollUnits = new();
        public readonly List<double> LongEventEndScrollUnits = new();
        public readonly List<int> LongRenderableEventIndicesByEnd = new();
        public readonly List<int> RenderEventIndexScratch = new(128);
        public int[] RenderEventMarks = Array.Empty<int>();
        public int RenderEventMarkStamp;
        public readonly Dictionary<ChartEventKey, List<ChartEvent>> LinkChainCache = new();
        public readonly Dictionary<ChartEventKey, int> LinkChainEffectiveEndTickCache = new();
        public readonly Dictionary<ChartEvent, ChartEvent> LinkChainRootByMember = new();
        public readonly Dictionary<ChartEventKey, double> EventSecondsByKey = new();
        public readonly Dictionary<ChartEventKey, ChartEvent> EventsByKey = new();
        public readonly List<(int Tick, string Side, ChartEvent Event)> BankChangingSuperFlicks = new();
        public readonly HashSet<(int Tick, string Side)> BankChangingSuperFlickTicks = new();
        public readonly List<ChartEvent> EditorTimelineTraceSuperFlicks = new();
        public GearTimeline? EditorTimelineTraceGear;
        public TempoMap? EditorTimelineTraceTempo;
        public AresChart? EditorTimelineTraceChart;
        public double CachedRenderRangeTick = double.NaN;
        public int CachedRenderRangeFirst;
        public int CachedRenderRangeLast;
        public readonly Dictionary<(string Left, string Right), Dictionary<int, int>> SimultaneousCountsCache = new();

        public void Apply(Playfield playfield)
        {
            playfield._linkRootByKey = LinkRootByKey;
            playfield._nextLinkByHeadKey = NextLinkByHeadKey;
            playfield._linkHeadByTailKey = LinkHeadByTailKey;
            playfield._ambiguousNextLinkHeadKeys = AmbiguousNextLinkHeadKeys;
            playfield._linkHeadKeys = LinkHeadKeys;
            playfield._orderedRenderableEvents = OrderedRenderableEvents;
            playfield._eventSeconds = EventSeconds;
            playfield._eventScrollUnits = EventScrollUnits;
            playfield._eventEffectiveEndSeconds = EventEffectiveEndSeconds;
            playfield._eventEffectiveEndScrollUnits = EventEffectiveEndScrollUnits;
            playfield._longEventEndScrollUnits = LongEventEndScrollUnits;
            playfield._longRenderableEventIndicesByEnd = LongRenderableEventIndicesByEnd;
            playfield._renderEventIndexScratch = RenderEventIndexScratch;
            playfield._renderEventMarks = RenderEventMarks;
            playfield._renderEventMarkStamp = RenderEventMarkStamp;
            playfield._linkChainCache = LinkChainCache;
            playfield._linkChainEffectiveEndTickCache = LinkChainEffectiveEndTickCache;
            playfield._linkChainRootByMember = LinkChainRootByMember;
            playfield._eventSecondsByKey = EventSecondsByKey;
            playfield._eventsByKey = EventsByKey;
            playfield._bankChangingSuperFlicks = BankChangingSuperFlicks;
            playfield._bankChangingSuperFlickTicks = BankChangingSuperFlickTicks;
            playfield._editorTimelineTraceSuperFlicks = EditorTimelineTraceSuperFlicks;
            playfield._editorTimelineTraceGear = EditorTimelineTraceGear;
            playfield._editorTimelineTraceTempo = EditorTimelineTraceTempo;
            playfield._editorTimelineTraceChart = EditorTimelineTraceChart;
            playfield._cachedRenderRangeTick = CachedRenderRangeTick;
            playfield._cachedRenderRangeFirst = CachedRenderRangeFirst;
            playfield._cachedRenderRangeLast = CachedRenderRangeLast;
            playfield._simultaneousCountsCache = SimultaneousCountsCache;
        }

        public void Capture(Playfield playfield)
        {
            RenderEventMarks = playfield._renderEventMarks;
            RenderEventMarkStamp = playfield._renderEventMarkStamp;
            EditorTimelineTraceGear = playfield._editorTimelineTraceGear;
            EditorTimelineTraceTempo = playfield._editorTimelineTraceTempo;
            EditorTimelineTraceChart = playfield._editorTimelineTraceChart;
            CachedRenderRangeTick = playfield._cachedRenderRangeTick;
            CachedRenderRangeFirst = playfield._cachedRenderRangeFirst;
            CachedRenderRangeLast = playfield._cachedRenderRangeLast;
        }
    }

    private const int EditorNoteGroupVisibleRows = 5;
    private int _editorNoteGroupsScrollOffset;
    private readonly TSC.UI.SmoothScrollHandler _editorNoteGroupsScroll = new();
    private int? _editorSelectedNoteGroupId;
    // A library row can be selected without selecting a chart instance. Keep
    // that intentional selection after placement is disarmed, until a new chart
    // selection explicitly moves focus away from the group.
    private bool _editorNoteGroupLibrarySelectionExplicit;
    private bool _editorNoteGroupEditMode;
    private int? _editorEditingNoteGroupId;
    private int? _editorEditingNoteGroupInstanceId;
    private bool _editorNoteGroupPlacementArmed;
    private bool _editorSynchronizingNoteGroups;
    private readonly List<ChartEvent> _editorNoteGroupPlacementPreviewEventBuffer = new(128);
    private bool _editorNoteGroupPlacementPreviewEventsCacheValid;
    private AresChart? _editorNoteGroupPlacementPreviewEventsCacheChart;
    private int _editorNoteGroupPlacementPreviewEventsCacheGroupId = int.MinValue;
    private int _editorNoteGroupPlacementPreviewEventsCacheStartTick = int.MinValue;
    private int _editorNoteGroupPlacementPreviewEventsCacheStartTrack = int.MinValue;
    private int _editorNoteGroupPlacementPreviewEventsCacheContentHash;
    private bool _editorNoteGroupPlacementPreviewEventsCacheCanPlace;
    private AresChart? _editorNoteGroupPlacementPreviewOccupancyChart;
    private bool _editorNoteGroupPlacementPreviewOccupancyCacheValid;
    private readonly HashSet<(int Tick, int Track)> _editorNoteGroupPlacementPreviewOccupiedCells = new();
    private readonly List<(int Start, int End)>[] _editorNoteGroupPlacementPreviewOccupiedIntervals =
        Enumerable.Range(0, 16).Select(_ => new List<(int Start, int End)>()).ToArray();
    private readonly List<int>[] _editorNoteGroupPlacementPreviewOccupiedIntervalMaxEnds =
        Enumerable.Range(0, 16).Select(_ => new List<int>()).ToArray();
    private readonly HashSet<(int Tick, string Side, string Bank)> _editorNoteGroupPlacementPreviewOccupiedSuperFlickBanks = new();
    private readonly HashSet<string> _editorNoteGroupPlacementPreviewLinkFamilies = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Family, int Pair), List<ChartEvent>> _editorNoteGroupPlacementPreviewLinkPairNodes = new();
    private AresChart? _editorNoteGroupPlacementPreviewRouteTimelineChart;
    private GearTimeline? _editorNoteGroupPlacementPreviewRouteTimeline;
    private bool _editorNoteGroupPlacementPreviewSourceCacheValid;
    private AresChart? _editorNoteGroupPlacementPreviewSourceCacheChart;
    private int _editorNoteGroupPlacementPreviewSourceCacheGroupId = int.MinValue;
    private NoteGroupDefinition? _editorNoteGroupPlacementPreviewCachedGroup;
    private NoteGroupInstance? _editorNoteGroupPlacementPreviewCachedSourceInstance;
    private List<ChartEvent>? _editorNoteGroupPlacementPreviewCachedSourceEvents;
    private Dictionary<int, ChartEvent>? _editorNoteGroupPlacementPreviewCachedSourceByMember;
    private bool _editorNoteGroupPlacementPreviewSourceIsLive;
    private bool _editorNoteGroupPlacementPreviewLinkRootMapCacheValid;
    private int _editorNoteGroupPlacementPreviewLinkRootMapCacheGroupId = int.MinValue;
    private int _editorNoteGroupPlacementPreviewLinkRootMapCacheInstanceId = int.MinValue;
    private readonly Dictionary<int, int> _editorNoteGroupPlacementPreviewLinkRootByMember = new();
    private AresChart? _editorNoteGroupPlacementPreviewInstanceIdsChart;
    private bool _editorNoteGroupPlacementPreviewInstanceIdsCacheValid;
    private readonly Dictionary<int, int> _editorNoteGroupPlacementPreviewMaxInstanceIdsByGroup = new();
    private readonly HashSet<(int Tick, int Track)> _editorNoteGroupPlacementPreviewPlannedCells = new();
    private readonly HashSet<(int Tick, string Side, string Bank)> _editorNoteGroupPlacementPreviewPlannedSuperFlickBanks = new();
    private readonly Dictionary<int, string?> _editorNoteGroupPlacementPreviewPlannedChains = new();
    private readonly Dictionary<int, int> _editorNoteGroupPlacementPreviewTargetTracks = new();
    private readonly Dictionary<int, int> _editorNoteGroupPlacementPreviewTargetTicks = new();
    private readonly Dictionary<int, int> _editorNoteGroupPlacementPreviewPairMaps = new();
    private readonly Dictionary<int, Dictionary<int, EditorNoteGroupLinkPairReservation>> _editorNoteGroupPlacementPreviewPairReservations = new();
    private readonly HashSet<ChartEvent> _editorNoteGroupPlacementPreviewNoTargetChainEvents = new();
    private readonly Dictionary<int, int> _editorNoteGroupPlacementPreviewEventIndices = new();
    private AresChart? _editorNoteGroupPlacementPreviewChart;
    private readonly EditorNoteGroupPlacementPreviewRenderCacheState _editorNoteGroupPlacementPreviewRenderCaches = new();
    private readonly ChartEvent _editorNotePlacementPreviewEvent = new();
    private readonly List<ChartEvent> _editorNotePlacementPreviewEvents = new(1);

    private void InvalidateEditorNoteGroupPlacementPreviewCache()
    {
        _editorNoteGroupPlacementPreviewCacheValid = false;
        _editorNoteGroupPlacementPreviewEventsCacheValid = false;
        _editorNoteGroupPlacementPreviewEventBuffer.Clear();
        _editorNoteGroupPlacementPreviewOccupancyCacheValid = false;
        _editorNoteGroupPlacementPreviewOccupancyChart = null;
        _editorNoteGroupPlacementPreviewOccupiedCells.Clear();
        _editorNoteGroupPlacementPreviewOccupiedSuperFlickBanks.Clear();
        _editorNoteGroupPlacementPreviewLinkFamilies.Clear();
        _editorNoteGroupPlacementPreviewLinkPairNodes.Clear();
        foreach (var intervals in _editorNoteGroupPlacementPreviewOccupiedIntervals)
            intervals.Clear();
        foreach (var maxEnds in _editorNoteGroupPlacementPreviewOccupiedIntervalMaxEnds)
            maxEnds.Clear();
        _editorNoteGroupPlacementPreviewRouteTimelineChart = null;
        _editorNoteGroupPlacementPreviewRouteTimeline = null;
        _editorNoteGroupPlacementPreviewSourceCacheValid = false;
        _editorNoteGroupPlacementPreviewSourceCacheChart = null;
        _editorNoteGroupPlacementPreviewCachedGroup = null;
        _editorNoteGroupPlacementPreviewCachedSourceInstance = null;
        _editorNoteGroupPlacementPreviewCachedSourceEvents = null;
        _editorNoteGroupPlacementPreviewCachedSourceByMember = null;
        _editorNoteGroupPlacementPreviewSourceIsLive = false;
        _editorNoteGroupPlacementPreviewLinkRootMapCacheValid = false;
        _editorNoteGroupPlacementPreviewLinkRootByMember.Clear();
        _editorNoteGroupPlacementPreviewInstanceIdsCacheValid = false;
        _editorNoteGroupPlacementPreviewInstanceIdsChart = null;
        _editorNoteGroupPlacementPreviewMaxInstanceIdsByGroup.Clear();
        _editorNoteGroupPlacementPreviewPlannedCells.Clear();
        _editorNoteGroupPlacementPreviewPlannedSuperFlickBanks.Clear();
        _editorNoteGroupPlacementPreviewPlannedChains.Clear();
        _editorNoteGroupPlacementPreviewTargetTracks.Clear();
        _editorNoteGroupPlacementPreviewTargetTicks.Clear();
        _editorNoteGroupPlacementPreviewPairMaps.Clear();
        _editorNoteGroupPlacementPreviewPairReservations.Clear();
        _editorNoteGroupPlacementPreviewNoTargetChainEvents.Clear();
        _editorNoteGroupPlacementPreviewEventIndices.Clear();
        _editorNoteGroupPlacementPreviewRenderCaches.IsValid = false;
    }

    private int GetEditorNoteGroupPlacementPreviewNextInstanceId(int groupId)
    {
        if (_chart is null)
            return 0;
        if (!_editorNoteGroupPlacementPreviewInstanceIdsCacheValid ||
            !ReferenceEquals(_editorNoteGroupPlacementPreviewInstanceIdsChart, _chart))
        {
            _editorNoteGroupPlacementPreviewMaxInstanceIdsByGroup.Clear();
            foreach (var instance in _chart.NoteGroupInstances)
            {
                if (!_editorNoteGroupPlacementPreviewMaxInstanceIdsByGroup.TryGetValue(
                        instance.GroupId, out var currentMax) || instance.InstanceId > currentMax)
                    _editorNoteGroupPlacementPreviewMaxInstanceIdsByGroup[instance.GroupId] = instance.InstanceId;
            }
            _editorNoteGroupPlacementPreviewInstanceIdsChart = _chart;
            _editorNoteGroupPlacementPreviewInstanceIdsCacheValid = true;
        }
        return _editorNoteGroupPlacementPreviewMaxInstanceIdsByGroup.TryGetValue(groupId, out var maxId)
            ? maxId
            : 0;
    }

    private void EnsureEditorNoteGroupPlacementPreviewOccupancyCache()
    {
        if (_chart is null || (_editorNoteGroupPlacementPreviewOccupancyCacheValid &&
            ReferenceEquals(_editorNoteGroupPlacementPreviewOccupancyChart, _chart)))
            return;

        _editorNoteGroupPlacementPreviewOccupiedCells.Clear();
        _editorNoteGroupPlacementPreviewOccupiedSuperFlickBanks.Clear();
        _editorNoteGroupPlacementPreviewLinkFamilies.Clear();
        _editorNoteGroupPlacementPreviewLinkPairNodes.Clear();
        foreach (var intervals in _editorNoteGroupPlacementPreviewOccupiedIntervals)
            intervals.Clear();
        foreach (var maxEnds in _editorNoteGroupPlacementPreviewOccupiedIntervalMaxEnds)
            maxEnds.Clear();

        foreach (var ev in _chart.Events)
        {
            if (ev.Track is < 0 or > 15)
                continue;

            var linkFamily = LinkBuilder.Family(ev.Track);
            if (linkFamily is not null)
                _editorNoteGroupPlacementPreviewLinkFamilies.Add(linkFamily);

            if (ev.IsSuperFlick)
            {
                var position = GearState.Position(ev.Track);
                if (position is { } bank)
                    _editorNoteGroupPlacementPreviewOccupiedSuperFlickBanks.Add((
                        ev.Tick, bank.Side.ToUpperInvariant(), bank.Bank.ToUpperInvariant()));
            }

            if (ev.Kind != "Note")
                continue;

            if (linkFamily is not null &&
                (ev.NoteType is NoteType.LinkHead or NoteType.LinkTail) &&
                (ev.LinePair is 1 or 3 or 5 or 7))
            {
                var pairKey = (linkFamily, ev.LinePair);
                if (!_editorNoteGroupPlacementPreviewLinkPairNodes.TryGetValue(pairKey, out var pairNodes))
                    _editorNoteGroupPlacementPreviewLinkPairNodes[pairKey] = pairNodes = new List<ChartEvent>();
                pairNodes.Add(ev);
            }

            _editorNoteGroupPlacementPreviewOccupiedCells.Add((ev.Tick, ev.Track));
            if (ev.IsHoldNote || ev.NoteType is NoteType.LinkHead or NoteType.LinkTail)
            {
                var endTick = GetEditorNoteHitEndTick(ev);
                if (endTick > ev.Tick)
                    _editorNoteGroupPlacementPreviewOccupiedIntervals[ev.Track].Add((ev.Tick, endTick));
            }
        }

        foreach (var pairNodes in _editorNoteGroupPlacementPreviewLinkPairNodes.Values)
        {
            pairNodes.Sort(static (a, b) =>
            {
                var tickOrder = a.Tick.CompareTo(b.Tick);
                if (tickOrder != 0)
                    return tickOrder;
                var trackOrder = a.Track.CompareTo(b.Track);
                return trackOrder != 0 ? trackOrder : a.EventIndex.CompareTo(b.EventIndex);
            });
        }

        for (var track = 0; track < 16; track++)
        {
            var intervals = _editorNoteGroupPlacementPreviewOccupiedIntervals[track];
            intervals.Sort(static (a, b) => a.Start.CompareTo(b.Start));
            var maxEnds = _editorNoteGroupPlacementPreviewOccupiedIntervalMaxEnds[track];
            var maxEnd = int.MinValue;
            foreach (var interval in intervals)
            {
                maxEnd = Math.Max(maxEnd, interval.End);
                maxEnds.Add(maxEnd);
            }
        }

        _editorNoteGroupPlacementPreviewOccupancyChart = _chart;
        _editorNoteGroupPlacementPreviewOccupancyCacheValid = true;
    }

    private bool IsEditorNoteGroupPlacementPreviewCellOccupied(int tick, int track)
    {
        if (_chart is null || track is < 0 or > 15)
            return false;
        EnsureEditorNoteGroupPlacementPreviewOccupancyCache();
        if (_editorNoteGroupPlacementPreviewOccupiedCells.Contains((tick, track)))
            return true;

        var intervals = _editorNoteGroupPlacementPreviewOccupiedIntervals[track];
        var maxEnds = _editorNoteGroupPlacementPreviewOccupiedIntervalMaxEnds[track];
        var low = 0;
        var high = intervals.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (intervals[middle].Start <= tick)
                low = middle + 1;
            else
                high = middle;
        }

        var lastStartingAtOrBeforeTick = low - 1;
        return lastStartingAtOrBeforeTick >= 0 && maxEnds[lastStartingAtOrBeforeTick] >= tick;
    }

    private bool IsEditorNoteGroupPlacementPreviewSuperFlickBankOccupied(
        int tick, string side, string bank)
    {
        EnsureEditorNoteGroupPlacementPreviewOccupancyCache();
        return _editorNoteGroupPlacementPreviewOccupiedSuperFlickBanks.Contains(
            (tick, side.ToUpperInvariant(), bank.ToUpperInvariant()));
    }

    private GearTimeline GetEditorNoteGroupPlacementPreviewRouteTimeline()
    {
        if (_chart is null)
            throw new InvalidOperationException("A chart is required to build a group placement preview.");
        if (!ReferenceEquals(_editorNoteGroupPlacementPreviewRouteTimelineChart, _chart) ||
            _editorNoteGroupPlacementPreviewRouteTimeline is null)
        {
            _editorNoteGroupPlacementPreviewRouteTimeline = new GearTimeline(_chart);
            _editorNoteGroupPlacementPreviewRouteTimelineChart = _chart;
        }
        return _editorNoteGroupPlacementPreviewRouteTimeline;
    }

    // Group member coordinates are always stored in the group's canonical
    // (unmirrored) orientation. A mirrored instance is a physical view of that
    // same pattern, so convert tracks/attributes at the instance boundary.
    private static int GetCanonicalEditorNoteGroupTrack(NoteGroupInstance instance, int physicalTrack)
    {
        var unmirroredTrack = instance.IsMirrored ? MirrorEditorTrackX(physicalTrack) : physicalTrack;
        if (instance.TrackMap is { Count: > 0 })
        {
            var mapping = instance.TrackMap.FirstOrDefault(candidate => candidate.Track == unmirroredTrack);
            if (mapping is not null)
                return mapping.CanonicalTrack;
        }
        return unmirroredTrack;
    }

    private static int GetPhysicalEditorNoteGroupTrack(NoteGroupInstance instance, int canonicalTrack)
    {
        var mappedTrack = instance.TrackMap is { Count: > 0 }
            ? instance.TrackMap.FirstOrDefault(candidate => candidate.CanonicalTrack == canonicalTrack)?.Track ?? canonicalTrack
            : canonicalTrack;
        return instance.IsMirrored ? MirrorEditorTrackX(mappedTrack) : mappedTrack;
    }

    private static int GetCanonicalEditorNoteGroupAttr(NoteGroupInstance instance, int physicalAttr)
        => instance.IsMirrored ? MirrorEditorAttrX(physicalAttr) : physicalAttr;

    private static int GetPhysicalEditorNoteGroupAttr(NoteGroupInstance instance, int canonicalAttr)
        => instance.IsMirrored ? MirrorEditorAttrX(canonicalAttr) : canonicalAttr;

    private static int GetEditorNoteGroupCounterpartTrack(
        NoteGroupInstance sourceInstance, NoteGroupInstance targetInstance, int sourcePhysicalTrack)
    {
        var canonicalTrack = GetCanonicalEditorNoteGroupTrack(sourceInstance, sourcePhysicalTrack);
        var targetCanonicalTrack = targetInstance.StartTrack + canonicalTrack - sourceInstance.StartTrack;
        return GetPhysicalEditorNoteGroupTrack(targetInstance, targetCanonicalTrack);
    }

    private (int GroupId, int InstanceId, int MemberId)? _editorLastGroupNoteClick;
    private long _editorLastGroupNoteClickTimestamp;
    private Vector2 _editorLastGroupNoteClickPosition;

    private double GetEditorNoteGroupScrollRowPitch()
    {
        var unit = _projection.ScalePixels(1f);
        var rowHeight = Math.Max(1.0, 50.0 * unit);
        var rowGap = Math.Max(1.0, 5.0 * unit);
        return rowHeight + rowGap;
    }

    private double GetEditorNoteGroupScrollContentExtent()
    {
        var count = _chart?.NoteGroups.Count ?? 0;
        if (count <= 0) return 0.0;
        var unit = _projection.ScalePixels(1f);
        var rowGap = Math.Max(1.0, 5.0 * unit);
        return count * GetEditorNoteGroupScrollRowPitch() - rowGap;
    }

    private double GetEditorNoteGroupScrollViewportExtent()
    {
        var unit = _projection.ScalePixels(1f);
        var rowHeight = Math.Max(1.0, 50.0 * unit);
        var rowGap = Math.Max(1.0, 5.0 * unit);
        return EditorNoteGroupVisibleRows * rowHeight + (EditorNoteGroupVisibleRows - 1) * rowGap;
    }

    private double GetEditorNoteGroupScrollMaximum()
        => Math.Max(0.0,
            GetEditorNoteGroupScrollContentExtent() - GetEditorNoteGroupScrollViewportExtent());

    private int GetEditorNoteGroupScrollFirstRow()
    {
        var pitch = GetEditorNoteGroupScrollRowPitch();
        return Math.Clamp((int)Math.Floor(_editorNoteGroupsScroll.Position / pitch), 0,
            Math.Max(0, (_chart?.NoteGroups.Count ?? 0) - 1));
    }

    private double GetEditorNoteGroupScrollPixelRemainder()
    {
        var pitch = GetEditorNoteGroupScrollRowPitch();
        var firstRow = GetEditorNoteGroupScrollFirstRow();
        return Math.Clamp(_editorNoteGroupsScroll.Position - firstRow * pitch, 0.0, pitch);
    }

    private List<object?> BuildEditorNoteGroupMenuItems()
    {
        if (_chart is null)
            return new List<object?>();

        RefreshEditorNoteGroupLibrarySelectionFromNotes();
        var groups = _chart.NoteGroups.OrderBy(group => group.Id).ToList();
        _editorNoteGroupsScroll.SetMaximum(GetEditorNoteGroupScrollMaximum());
        _editorNoteGroupsScrollOffset = GetEditorNoteGroupScrollFirstRow();
        var visible = new List<object?>();
        foreach (var group in groups.Skip(_editorNoteGroupsScrollOffset).Take(EditorNoteGroupVisibleRows + 1))
        {
            var instances = _chart.NoteGroupInstances.Where(instance => instance.GroupId == group.Id)
                .OrderBy(instance => instance.StartTick).ThenBy(instance => instance.StartTrack)
                .ThenBy(instance => instance.InstanceId).ToList();
            var span = group.Members.Count == 0 ? 0 :
                group.Members.Max(member => (long)member.TickOffset + Math.Max(1, member.Duration)) -
                group.Members.Min(member => member.TickOffset);
            var description = group.Members.Count == 0
                ? "EMPTY PATTERN"
                : $"{group.Members.Count} NOTES  ·  {span} TICKS  ·  {group.Members.Select(member => member.TrackOffset).Distinct().Count()} TRACKS";
            if (_editorNoteGroupEditMode && _editorEditingNoteGroupId == group.Id)
                description = $"EDITING INSTANCE {GetEditorNoteGroupDisplayInstanceNumber(group.Id, _editorEditingNoteGroupInstanceId ?? 1)}  ·  {description}";
            else if (_editorNoteGroupPlacementArmed && _editorSelectedNoteGroupId == group.Id)
                description = $"SELECTED · CLICK AN EMPTY LANE TO PLACE  ·  {description}";
            var placementArmed = _editorNoteGroupPlacementArmed && _editorSelectedNoteGroupId == group.Id;
            var selected = _editorSelectedNoteGroupId == group.Id ||
                           (_editorNoteGroupEditMode && _editorEditingNoteGroupId == group.Id);
            visible.Add(new Dictionary<string, object?>
            {
                ["id"] = $"note_group_{group.Id}",
                ["label"] = string.Empty,
                ["header"] = $"GROUP {group.Id} | {instances.Count} {(instances.Count == 1 ? "INSTANCE" : "INSTANCES")}",
                ["description"] = description,
                ["action"] = "editor.note_groups.select",
                ["value"] = group.Id,
                ["selected"] = selected,
                ["primary_active"] = placementArmed,
                ["variant"] = placementArmed ? "primary" : "secondary",
                ["element_color"] = selected ? "#34D399" : null,
                ["enabled"] = true
            });
        }
        return visible;
    }

    private int GetEditorNoteGroupInstanceEndTick(int groupId, int instanceId)
    {
        if (_chart is null)
            return 0;

        var fallbackTick = _chart.NoteGroupInstances.FirstOrDefault(instance =>
            instance.GroupId == groupId && instance.InstanceId == instanceId)?.StartTick ?? 0;
        var bottomTick = _chart.Events
            .Where(ev => ev.Kind == "Note" && ev.RawType == 1 &&
                         ev.NoteGroupId == groupId && ev.NoteGroupInstanceId == instanceId)
            .Select(ev => (long)ev.Tick +
                (ev.NoteType is NoteType.SingleLong or NoteType.Vibrato or NoteType.LinkHead or NoteType.LinkTail
                    ? Math.Max(1, ev.Duration) : 0))
            .DefaultIfEmpty(fallbackTick)
            .Max();
        return (int)Math.Clamp(bottomTick, 0L, int.MaxValue);
    }

    private static NoteGroupInstance CreateEditorNoteGroupFallbackInstance(NoteGroupDefinition group)
    {
        var minTickOffset = group.Members.Count == 0 ? 0 : group.Members.Min(member => member.TickOffset);
        var minTrackOffset = group.Members.Count == 0 ? 0 : group.Members.Min(member => member.TrackOffset);
        // This is a temporary source coordinate system, never serialized. It may
        // use a negative origin so the canonical members normalize to valid lanes
        // even if the group's original anchor instance has been deleted.
        var startTick = (int)Math.Clamp(-(long)minTickOffset, int.MinValue, int.MaxValue);
        var startTrack = (int)Math.Clamp(-(long)minTrackOffset, int.MinValue, int.MaxValue);
        return new NoteGroupInstance(group.Id, 0, startTick, startTrack, false);
    }

    private bool TryGetEditorNoteGroupSource(
        int groupId, out NoteGroupDefinition group, out NoteGroupInstance sourceInstance,
        out List<ChartEvent> sourceEvents, out Dictionary<int, ChartEvent> sourceByMember)
    {
        group = null!;
        sourceInstance = null!;
        sourceEvents = new List<ChartEvent>();
        sourceByMember = new Dictionary<int, ChartEvent>();
        if (_chart is null)
            return false;

        if (_editorNoteGroupPlacementPreviewSourceCacheValid &&
            ReferenceEquals(_editorNoteGroupPlacementPreviewSourceCacheChart, _chart) &&
            _editorNoteGroupPlacementPreviewSourceCacheGroupId == groupId &&
            _editorNoteGroupPlacementPreviewCachedGroup is not null &&
            _editorNoteGroupPlacementPreviewCachedSourceInstance is not null &&
            _editorNoteGroupPlacementPreviewCachedSourceEvents is not null &&
            _editorNoteGroupPlacementPreviewCachedSourceByMember is not null)
        {
            group = _editorNoteGroupPlacementPreviewCachedGroup;
            sourceInstance = _editorNoteGroupPlacementPreviewCachedSourceInstance;
            sourceEvents = _editorNoteGroupPlacementPreviewCachedSourceEvents;
            sourceByMember = _editorNoteGroupPlacementPreviewCachedSourceByMember;
            return true;
        }

        var definition = _chart.NoteGroups.FirstOrDefault(candidate => candidate.Id == groupId);
        if (definition is null || definition.Members.Count == 0)
            return false;

        var definedMemberIds = definition.Members.Select(member => member.Id).ToHashSet();
        var candidateInstances = _chart.NoteGroupInstances.Where(instance => instance.GroupId == groupId)
            .OrderBy(instance => instance.StartTick).ThenBy(instance => instance.StartTrack)
            .ThenBy(instance => instance.InstanceId).ToList();
        foreach (var candidateInstance in candidateInstances)
        {
            var candidateEvents = _chart.Events.Where(ev => ev.Kind == "Note" && ev.RawType == 1 &&
                    ev.NoteGroupId == groupId && ev.NoteGroupInstanceId == candidateInstance.InstanceId &&
                    ev.NoteGroupMemberId.HasValue)
                .OrderBy(ev => ev.NoteGroupMemberId).ToList();
            var candidateByMember = candidateEvents.GroupBy(ev => ev.NoteGroupMemberId!.Value)
                .ToDictionary(items => items.Key, items => items.ToList());
            if (candidateEvents.Count != definedMemberIds.Count ||
                candidateByMember.Count != definedMemberIds.Count ||
                !candidateByMember.Keys.ToHashSet().SetEquals(definedMemberIds) ||
                candidateByMember.Values.Any(matches => matches.Count != 1))
                continue;

            group = definition;
            sourceInstance = candidateInstance;
            sourceEvents = candidateEvents;
            sourceByMember = candidateByMember.ToDictionary(pair => pair.Key, pair => pair.Value[0]);
            CacheEditorNoteGroupPlacementPreviewSource(groupId, group, sourceInstance,
                sourceEvents, sourceByMember, sourceIsLive: true);
            return true;
        }

        // The library definition is the canonical pattern. Older charts or an
        // interrupted edit can leave every live instance incomplete; that must
        // not make the library entry appear empty or prevent its placement. Use
        // the first surviving instance as the orientation/anchor when available,
        // otherwise synthesize a safe canonical origin for this definition.
        sourceInstance = candidateInstances.FirstOrDefault() ?? CreateEditorNoteGroupFallbackInstance(definition);
        var eventIndices = new Dictionary<int, int>();
        foreach (var member in definition.Members.OrderBy(member => member.Id))
        {
            var canonicalTrack = sourceInstance.StartTrack + member.TrackOffset;
            var physicalTrack = GetPhysicalEditorNoteGroupTrack(sourceInstance, canonicalTrack);
            var tickLong = (long)sourceInstance.StartTick + member.TickOffset;
            if (physicalTrack is < 0 or > 15 || tickLong < 0 || tickLong > int.MaxValue)
                return false;
            eventIndices.TryGetValue(physicalTrack, out var eventIndex);
            eventIndices[physicalTrack] = eventIndex + 1;
            var isLink = member.NoteType is NoteType.LinkHead or NoteType.LinkTail;
            var pair = isLink ? member.OriginalLinePair : -1;
            if (isLink && pair is not (1 or 3 or 5 or 7))
                return false;
            var sourceEvent = new ChartEvent
            {
                Tick = (int)tickLong,
                Track = physicalTrack,
                Attr = GetPhysicalEditorNoteGroupAttr(sourceInstance, member.Attr),
                RawType = 1,
                TypeIndex = -1,
                Duration = Math.Max(1, member.Duration),
                NoteType = member.NoteType,
                EventIndex = eventIndex,
                LinePair = pair,
                Kind = "Note",
                Instrument = member.Instrument,
                Velocity = member.Velocity,
                Pan = member.Pan,
                NoteGroupId = groupId,
                NoteGroupInstanceId = sourceInstance.InstanceId,
                NoteGroupMemberId = member.Id
            };
            sourceEvents.Add(sourceEvent);
            sourceByMember[member.Id] = sourceEvent;
        }

        group = definition;
        var completeFallback = sourceEvents.Count == definedMemberIds.Count &&
                               sourceByMember.Count == definedMemberIds.Count;
        if (completeFallback)
            CacheEditorNoteGroupPlacementPreviewSource(groupId, group, sourceInstance,
                sourceEvents, sourceByMember, sourceIsLive: false);
        return completeFallback;
    }

    private void CacheEditorNoteGroupPlacementPreviewSource(int groupId, NoteGroupDefinition group,
        NoteGroupInstance sourceInstance, List<ChartEvent> sourceEvents,
        Dictionary<int, ChartEvent> sourceByMember, bool sourceIsLive)
    {
        _editorNoteGroupPlacementPreviewSourceCacheChart = _chart;
        _editorNoteGroupPlacementPreviewSourceCacheGroupId = groupId;
        _editorNoteGroupPlacementPreviewCachedGroup = group;
        _editorNoteGroupPlacementPreviewCachedSourceInstance = sourceInstance;
        _editorNoteGroupPlacementPreviewCachedSourceEvents = sourceEvents;
        _editorNoteGroupPlacementPreviewCachedSourceByMember = sourceByMember;
        _editorNoteGroupPlacementPreviewSourceIsLive = sourceIsLive;
        _editorNoteGroupPlacementPreviewSourceCacheValid = _chart is not null;
        _editorNoteGroupPlacementPreviewLinkRootMapCacheValid = false;
        _editorNoteGroupPlacementPreviewLinkRootByMember.Clear();
    }

    private Dictionary<int, int> BuildEditorNoteGroupPlacementPreviewLinkRootMap(
        NoteGroupDefinition group, NoteGroupInstance sourceInstance, IReadOnlyList<ChartEvent> sourceEvents)
    {
        if (_editorNoteGroupPlacementPreviewLinkRootMapCacheValid &&
            _editorNoteGroupPlacementPreviewLinkRootMapCacheGroupId == group.Id &&
            _editorNoteGroupPlacementPreviewLinkRootMapCacheInstanceId == sourceInstance.InstanceId)
            return _editorNoteGroupPlacementPreviewLinkRootByMember;

        var rootByMember = _editorNoteGroupPlacementPreviewLinkRootByMember;
        rootByMember.Clear();
        if (sourceEvents.Count == 0)
            return rootByMember;

        if (_editorNoteGroupPlacementPreviewSourceIsLive)
        {
            // Resolve the source instance's graph once. The old preview called
            // GetEditorNoteGroupLogicalChainRootMemberId for every LinkTail, which
            // rebuilt this isolated chart repeatedly during a single hover update.
            foreach (var chain in GetEditorNoteGroupLinkChainInfos(group.Id, sourceInstance.InstanceId))
            {
                if (chain.Root.NoteGroupMemberId is not int rootMemberId)
                    continue;
                foreach (var node in chain.Nodes)
                {
                    if (node.NoteGroupMemberId is int memberId)
                        rootByMember[memberId] = rootMemberId;
                }
            }
            _editorNoteGroupPlacementPreviewLinkRootMapCacheGroupId = group.Id;
            _editorNoteGroupPlacementPreviewLinkRootMapCacheInstanceId = sourceInstance.InstanceId;
            _editorNoteGroupPlacementPreviewLinkRootMapCacheValid = true;
            return rootByMember;
        }

        // When the preview is based on the saved definition because no complete
        // live instance remains, ChainGroup is the canonical logical relationship.
        var rootsByChain = group.Members
            .Where(member => member.ChainGroup >= 0 && member.NoteType == NoteType.LinkHead)
            .GroupBy(member => member.ChainGroup)
            .Where(members => members.Count() == 1)
            .ToDictionary(members => members.Key, members => members.First().Id);
        foreach (var member in group.Members)
        {
            if (member.NoteType is not (NoteType.LinkHead or NoteType.LinkTail) ||
                member.ChainGroup < 0 || !rootsByChain.TryGetValue(member.ChainGroup, out var rootMemberId))
                continue;
            rootByMember[member.Id] = rootMemberId;
        }
        _editorNoteGroupPlacementPreviewLinkRootMapCacheGroupId = group.Id;
        _editorNoteGroupPlacementPreviewLinkRootMapCacheInstanceId = sourceInstance.InstanceId;
        _editorNoteGroupPlacementPreviewLinkRootMapCacheValid = true;
        return rootByMember;
    }

    private bool TryBuildEditorNoteGroupPlacementPreviewEvents(
        int startTick, int startTrack, out List<ChartEvent> previewEvents, out int contentHash, out bool canPlace)
    {
        previewEvents = _editorNoteGroupPlacementPreviewEventBuffer;
        contentHash = 0;
        canPlace = true;
        if (_chart is null || _editorSelectedNoteGroupId is not int groupId)
            return false;

        if (_editorNoteGroupPlacementPreviewEventsCacheValid &&
            ReferenceEquals(_editorNoteGroupPlacementPreviewEventsCacheChart, _chart) &&
            _editorNoteGroupPlacementPreviewEventsCacheGroupId == groupId &&
            _editorNoteGroupPlacementPreviewEventsCacheStartTick == startTick &&
            _editorNoteGroupPlacementPreviewEventsCacheStartTrack == startTrack)
        {
            contentHash = _editorNoteGroupPlacementPreviewEventsCacheContentHash;
            canPlace = _editorNoteGroupPlacementPreviewEventsCacheCanPlace;
            return previewEvents.Count > 0;
        }

        _editorNoteGroupPlacementPreviewEventsCacheValid = false;
        previewEvents.Clear();
        if (!TryGetEditorNoteGroupSource(groupId, out var group, out var sourceInstance,
                out var sourceEvents, out var sourceByMember))
            return false;

        var nextInstanceId = GetEditorNoteGroupPlacementPreviewNextInstanceId(groupId);
        if (nextInstanceId == int.MaxValue)
            return false;
        var targetCanonicalStartTrack = sourceInstance.IsMirrored ? MirrorEditorTrackX(startTrack) : startTrack;
        var targetInstance = new NoteGroupInstance(
            groupId, nextInstanceId + 1, startTick, targetCanonicalStartTrack, sourceInstance.IsMirrored);
        var linkRootByMember = BuildEditorNoteGroupPlacementPreviewLinkRootMap(
            group, sourceInstance, sourceEvents);
        var plannedCells = _editorNoteGroupPlacementPreviewPlannedCells;
        plannedCells.Clear();
        var plannedSuperFlickBanks = _editorNoteGroupPlacementPreviewPlannedSuperFlickBanks;
        plannedSuperFlickBanks.Clear();
        var plannedChains = _editorNoteGroupPlacementPreviewPlannedChains;
        plannedChains.Clear();
        var targetTracks = _editorNoteGroupPlacementPreviewTargetTracks;
        targetTracks.Clear();
        EnsureEditorNoteGroupPlacementPreviewOccupancyCache();
        var targetTicks = _editorNoteGroupPlacementPreviewTargetTicks;
        targetTicks.Clear();

        foreach (var member in group.Members)
        {
            var source = sourceByMember[member.Id];
            var tickLong = (long)startTick + source.Tick - sourceInstance.StartTick;
            var resolvedTrack = GetEditorNoteGroupCounterpartTrack(sourceInstance, targetInstance, source.Track);
            if (tickLong < 0 || tickLong > int.MaxValue || resolvedTrack is < 0 or > 15)
                canPlace = false;
            var trackIndex = Math.Clamp(resolvedTrack, 0, 15);
            var tick = (int)Math.Clamp(tickLong, 0L, int.MaxValue);
            if (!IsEditorTrackAllowedForNoteType(trackIndex, source.NoteType))
                canPlace = false;
            if (!plannedCells.Add((tick, trackIndex)) ||
                IsEditorNoteGroupPlacementPreviewCellOccupied(tick, trackIndex))
                canPlace = false;

            if (source.NoteType is NoteType.LinkHead or NoteType.LinkTail)
            {
                var logicalChainRoot = -1;
                var hasRoot = source.NoteGroupMemberId is int sourceMemberId &&
                    linkRootByMember.TryGetValue(sourceMemberId, out logicalChainRoot);
                var family = LinkBuilder.Family(trackIndex);
                if (!hasRoot || family is null ||
                    (plannedChains.TryGetValue(logicalChainRoot, out var chainFamily) &&
                     !string.Equals(chainFamily, family, StringComparison.Ordinal)))
                    canPlace = false;
                else
                    plannedChains[logicalChainRoot] = family;
            }

            if (source.IsSuperFlick)
            {
                var sourcePosition = GearState.Position(source.Track);
                var targetPosition = GearState.Position(trackIndex);
                if (sourcePosition is null || targetPosition is null ||
                    !string.Equals(sourcePosition.Value.Side, targetPosition.Value.Side, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(sourcePosition.Value.Bank, targetPosition.Value.Bank, StringComparison.OrdinalIgnoreCase))
                    canPlace = false;
                else
                {
                    var key = (tick, targetPosition.Value.Side.ToUpperInvariant(), targetPosition.Value.Bank.ToUpperInvariant());
                    if (!plannedSuperFlickBanks.Add(key) ||
                        IsEditorNoteGroupPlacementPreviewSuperFlickBankOccupied(
                            tick, targetPosition.Value.Side, targetPosition.Value.Bank))
                        canPlace = false;
                }
            }

            targetTracks[member.Id] = trackIndex;
            targetTicks[member.Id] = tick;
        }

        // Reserve LinkPairs with the same allocator used by real placement. This
        // keeps preview chains on the exact same physical pair/family mapping as
        // the committed instance and prevents two overlapping preview chains from
        // merging into one accidental path.
        var pairMaps = _editorNoteGroupPlacementPreviewPairMaps;
        pairMaps.Clear();
        var pairReservations = _editorNoteGroupPlacementPreviewPairReservations;
        foreach (var reservations in pairReservations.Values)
            reservations.Clear();
        var previewRouteTimeline = GetEditorNoteGroupPlacementPreviewRouteTimeline();
        var noTargetChainEvents = _editorNoteGroupPlacementPreviewNoTargetChainEvents;
        var logicalChains = sourceEvents
            .Where(ev => ev.NoteType is NoteType.LinkHead or NoteType.LinkTail)
            .GroupBy(ev => ev.NoteGroupMemberId is int memberId &&
                           linkRootByMember.TryGetValue(memberId, out var rootMemberId)
                ? rootMemberId
                : -1)
            .OrderBy(chain => chain.Key);
        foreach (var chain in logicalChains)
        {
            if (chain.Key < 0)
            {
                canPlace = false;
                continue;
            }

            var sourceChain = chain.ToList();
            var root = sourceChain.FirstOrDefault(ev => ev.NoteType == NoteType.LinkHead &&
                ev.NoteGroupMemberId == chain.Key);
            if (root is null)
            {
                canPlace = false;
                continue;
            }

            if (!TryAllocateEditorNoteGroupLinkPair(group, chain.Key, sourceChain,
                    sourceInstance, targetInstance, pairReservations, out var pair, out _,
                    routeTimelineOverride: previewRouteTimeline,
                    targetChainEventsOverride: noTargetChainEvents,
                    linkFamiliesOverride: _editorNoteGroupPlacementPreviewLinkFamilies,
                    linkPairNodesOverride: _editorNoteGroupPlacementPreviewLinkPairNodes))
            {
                canPlace = false;
                // Keep an invalid (red) preview visually connected. These synthetic
                // IDs exist only in the temporary preview chart and cannot be saved.
                pair = 9 + pairMaps.Count * 2;
            }
            pairMaps[chain.Key] = pair;
        }

        var eventIndices = _editorNoteGroupPlacementPreviewEventIndices;
        eventIndices.Clear();
        foreach (var source in sourceEvents)
        {
            var memberId = source.NoteGroupMemberId!.Value;
            var trackIndex = targetTracks[memberId];
            int? rootMemberId = (source.NoteType is NoteType.LinkHead or NoteType.LinkTail) &&
                source.NoteGroupMemberId is int sourceMemberId &&
                linkRootByMember.TryGetValue(sourceMemberId, out var resolvedRootMemberId)
                    ? resolvedRootMemberId
                    : null;
            var pair = rootMemberId is int logicalRootMemberId && pairMaps.TryGetValue(logicalRootMemberId, out var mappedPair)
                ? mappedPair
                : -1;
            if ((source.NoteType is NoteType.LinkHead or NoteType.LinkTail) && pair < 0)
                canPlace = false;
            var attr = GetPhysicalEditorNoteGroupAttr(targetInstance,
                GetCanonicalEditorNoteGroupAttr(sourceInstance, source.Attr));
            if (source.NoteType is NoteType.LinkHead or NoteType.LinkTail)
                attr = source.NoteType == NoteType.LinkHead ? pair : pair + 1;
            eventIndices.TryGetValue(trackIndex, out var eventIndex);
            eventIndices[trackIndex] = eventIndex + 1;
            previewEvents.Add(new ChartEvent
            {
                Tick = targetTicks[memberId],
                Track = trackIndex,
                Attr = attr,
                RawType = source.RawType,
                TypeIndex = -1,
                Duration = source.Duration,
                NoteType = source.NoteType,
                EventIndex = eventIndex,
                LinePair = pair,
                TwinPairId = source.TwinPairId,
                LoopReconstructed = source.LoopReconstructed,
                LoopPhase = source.LoopPhase,
                Kind = source.Kind,
                Value = source.Value,
                Instrument = source.Instrument,
                Velocity = source.Velocity,
                Pan = source.Pan
            });
        }

        var hash = new HashCode();
        foreach (var note in previewEvents)
        {
            hash.Add(note.Tick);
            hash.Add(note.Track);
            hash.Add(note.EventIndex);
            hash.Add(note.RawType);
            hash.Add(note.Attr);
            hash.Add(note.Duration);
            hash.Add(note.NoteType);
            hash.Add(note.LinePair);
            hash.Add(note.TwinPairId);
            hash.Add(note.LoopReconstructed);
            hash.Add(note.LoopPhase);
            hash.Add(note.Kind);
            hash.Add(note.Value);
            hash.Add(note.Instrument);
            hash.Add(note.Velocity);
            hash.Add(note.Pan);
        }
        contentHash = hash.ToHashCode();
        _editorNoteGroupPlacementPreviewEventsCacheChart = _chart;
        _editorNoteGroupPlacementPreviewEventsCacheGroupId = groupId;
        _editorNoteGroupPlacementPreviewEventsCacheStartTick = startTick;
        _editorNoteGroupPlacementPreviewEventsCacheStartTrack = startTrack;
        _editorNoteGroupPlacementPreviewEventsCacheContentHash = contentHash;
        _editorNoteGroupPlacementPreviewEventsCacheCanPlace = canPlace;
        _editorNoteGroupPlacementPreviewEventsCacheValid = previewEvents.Count > 0;
        return previewEvents.Count > 0;
    }

    private void DrawEditorNoteGroupPlacementPreview(double currentTick, GearState currentState)
    {
        if (_editorNoteGroupPlacementPreviewRendering || _editorInactiveBankLayerPass || !_chartEditorMode ||
            _editorPreviewMode || _gameplayMode || !_editorNoteGroupPlacementArmed || _chart is null)
            return;

        // The normal editor hover pass already performs the exact hit test used
        // by a placement click. Reuse it so the ghost and the actual click cannot
        // disagree about the snapped cell. Fall back to a fresh hit test when the
        // group was armed in the same frame that the hover cache was invalidated.
        int? pointerTick = _hoverBeatTick;
        var startTrack = _hoverTrack ?? -1;
        if (pointerTick is null || startTrack is < 0 or > 15)
            pointerTick = FindClickedQuarterBeat(_editorPointerPosition, currentTick, currentState,
                out startTrack);
        if (pointerTick is not int startTick || startTrack is < 0 or > 15)
            return;

        if (!TryBuildEditorNoteGroupPlacementPreviewEvents(startTick, startTrack,
                out var previewEvents, out var contentHash, out var canPlace))
            return;

        var cache = new HashCode();
        cache.Add("note-group-placement");
        cache.Add(_editorSelectedNoteGroupId);
        cache.Add(startTick);
        cache.Add(startTrack);
        cache.Add(contentHash);
        cache.Add(canPlace);
        cache.Add(Math.Round(currentTick, 3));
        cache.Add(_graphics.Viewport.Width);
        cache.Add(_graphics.Viewport.Height);
        cache.Add(ComputeLaneMeshCacheSignature(currentState));
        cache.Add(currentState.Left);
        cache.Add(currentState.Right);
        cache.Add(currentState.CenterLeft);
        cache.Add(currentState.CenterRight);
        cache.Add(_projection.ScrollSpeedScale);
        cache.Add(_projection.BaseScrollSpeed);
        cache.Add(_topDownViewEnabled);
        cache.Add(_compact);
        cache.Add(_constantScroll);
        cache.Add(_editorShowAttributes);
        var cacheKey = cache.ToHashCode();

        if (!_editorNoteGroupPlacementPreviewCacheValid ||
            _editorNoteGroupPlacementPreviewCacheKey != cacheKey)
        {
            RenderEditorNoteGroupPlacementPreview(previewEvents, contentHash, currentTick, currentState);
            _editorNoteGroupPlacementPreviewCacheKey = cacheKey;
            _editorNoteGroupPlacementPreviewCacheValid = true;
        }

        if (_editorNoteGroupPlacementPreviewRenderTarget is null)
            return;
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);
        _spriteBatch.Draw(_editorNoteGroupPlacementPreviewRenderTarget,
            new Rectangle(0, 0, _graphics.Viewport.Width, _graphics.Viewport.Height),
            canPlace ? new XnaColor(174, 255, 0, 160) : new XnaColor(255, 78, 88, 160));
        _spriteBatch.End();
    }

    private void DrawEditorNotePlacementPreview(double currentTick, GearState currentState)
    {
        if (!_chartEditorMode || _editorPreviewMode || _gameplayMode || _editorPanelsViewEnabled ||
            !_editorPlacementEnabled || _editorConnectorMode || _editorNoteGroupPlacementArmed ||
            _editorNoteGroupEditMode || _editorSuperFlickTool == EditorSuperFlickTool.Place ||
            !_editorMouseHoverActive || _chart is null || _tempo is null)
            return;

        // Use the same snapped grid cell and lane mapping as the ordinary hover
        // marker and placement click. The fallback handles the frame in which a
        // placement tool was just armed and the hover cache has not refreshed yet.
        int? pointerTick = _hoverBeatTick;
        var hoverTrack = _hoverTrack ?? -1;
        if (pointerTick is null || hoverTrack is < 0 or > 15)
            pointerTick = FindClickedQuarterBeat(_editorPointerPosition, currentTick, currentState,
                out hoverTrack);
        if (pointerTick is not int startTick || hoverTrack is < 0 or > 15)
            return;

        var startTrack = GetEditorHoverMarkerTrack(hoverTrack);
        if (startTrack is < 0 or > 15 || !IsEditorTrackAllowedForNoteType(startTrack, _editorNoteType))
            return;

        var previewEvent = _editorNotePlacementPreviewEvent;
        previewEvent.Tick = Math.Max(0, startTick);
        previewEvent.Track = startTrack;
        previewEvent.Attr = _editorAttr;
        previewEvent.RawType = 1;
        previewEvent.TypeIndex = -1;
        previewEvent.Duration = _editorDuration;
        previewEvent.NoteType = _editorNoteType;
        previewEvent.EventIndex = 0;
        previewEvent.LinePair = -1;
        previewEvent.TwinPairId = null;
        previewEvent.NoteGroupId = null;
        previewEvent.NoteGroupInstanceId = null;
        previewEvent.NoteGroupMemberId = null;
        previewEvent.Kind = "Note";
        previewEvent.Value = 0f;
        previewEvent.Instrument = 0;
        previewEvent.Velocity = 127;
        previewEvent.Pan = 64;

        var previewEvents = _editorNotePlacementPreviewEvents;
        if (previewEvents.Count == 0)
            previewEvents.Add(previewEvent);
        else
            previewEvents[0] = previewEvent;

        // A hovered note/marker blocks the same click that would otherwise place
        // this note. Keep the ghost visible in red to communicate that state.
        var canPlace = _editorHoverHit is null;
        var contentHashBuilder = new HashCode();
        contentHashBuilder.Add("single-note-placement");
        contentHashBuilder.Add(previewEvent.Tick);
        contentHashBuilder.Add(previewEvent.Track);
        contentHashBuilder.Add(previewEvent.Attr);
        contentHashBuilder.Add(previewEvent.Duration);
        contentHashBuilder.Add(previewEvent.NoteType);
        var contentHash = contentHashBuilder.ToHashCode();

        var cache = new HashCode();
        cache.Add("single-note-placement");
        cache.Add(previewEvent.Tick);
        cache.Add(previewEvent.Track);
        cache.Add(contentHash);
        cache.Add(canPlace);
        cache.Add(Math.Round(currentTick, 3));
        cache.Add(_graphics.Viewport.Width);
        cache.Add(_graphics.Viewport.Height);
        cache.Add(ComputeLaneMeshCacheSignature(currentState));
        cache.Add(currentState.Left);
        cache.Add(currentState.Right);
        cache.Add(currentState.CenterLeft);
        cache.Add(currentState.CenterRight);
        cache.Add(_projection.ScrollSpeedScale);
        cache.Add(_projection.BaseScrollSpeed);
        cache.Add(_topDownViewEnabled);
        cache.Add(_compact);
        cache.Add(_constantScroll);
        cache.Add(_editorShowAttributes);
        var cacheKey = cache.ToHashCode();

        if (!_editorNoteGroupPlacementPreviewCacheValid ||
            _editorNoteGroupPlacementPreviewCacheKey != cacheKey)
        {
            RenderEditorNoteGroupPlacementPreview(previewEvents, contentHash, currentTick, currentState);
            _editorNoteGroupPlacementPreviewCacheKey = cacheKey;
            _editorNoteGroupPlacementPreviewCacheValid = true;
        }

        if (_editorNoteGroupPlacementPreviewRenderTarget is null)
            return;

        // This runs before DrawSelectionOverlay, intentionally leaving the
        // normal circular hover highlight visible above the textured note ghost.
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);
        _spriteBatch.Draw(_editorNoteGroupPlacementPreviewRenderTarget,
            new Rectangle(0, 0, _graphics.Viewport.Width, _graphics.Viewport.Height),
            canPlace ? new XnaColor(174, 255, 0, 160) : new XnaColor(255, 78, 88, 160));
        _spriteBatch.End();
    }

    private void RenderEditorNoteGroupPlacementPreview(
        IReadOnlyCollection<ChartEvent> previewEvents, int contentHash,
        double currentTick, GearState currentState)
    {
        if (_chart is null || previewEvents.Count == 0)
            return;

        var sourceChart = _chart;
        var previewChart = _editorNoteGroupPlacementPreviewChart ??= new AresChart();
        var originalTargets = _graphics.GetRenderTargets();
        var originalViewport = _graphics.Viewport;
        var originalActiveBanksOnlyPass = _editorActiveBanksOnlyPass;
        var originalInactiveBankLayerPass = _editorInactiveBankLayerPass;
        var originalRenderCaches = (
            LinkRootByKey: _linkRootByKey,
            NextLinkByHeadKey: _nextLinkByHeadKey,
            LinkHeadByTailKey: _linkHeadByTailKey,
            AmbiguousNextLinkHeadKeys: _ambiguousNextLinkHeadKeys,
            LinkHeadKeys: _linkHeadKeys,
            OrderedRenderableEvents: _orderedRenderableEvents,
            EventSeconds: _eventSeconds,
            EventScrollUnits: _eventScrollUnits,
            EventEffectiveEndSeconds: _eventEffectiveEndSeconds,
            EventEffectiveEndScrollUnits: _eventEffectiveEndScrollUnits,
            LongEventEndScrollUnits: _longEventEndScrollUnits,
            LongRenderableEventIndicesByEnd: _longRenderableEventIndicesByEnd,
            RenderEventIndexScratch: _renderEventIndexScratch,
            RenderEventMarks: _renderEventMarks,
            RenderEventMarkStamp: _renderEventMarkStamp,
            LinkChainCache: _linkChainCache,
            LinkChainEffectiveEndTickCache: _linkChainEffectiveEndTickCache,
            LinkChainRootByMember: _linkChainRootByMember,
            EventSecondsByKey: _eventSecondsByKey,
            EventsByKey: _eventsByKey,
            BankChangingSuperFlicks: _bankChangingSuperFlicks,
            BankChangingSuperFlickTicks: _bankChangingSuperFlickTicks,
            EditorTimelineTraceSuperFlicks: _editorTimelineTraceSuperFlicks,
            EditorTimelineTraceGear: _editorTimelineTraceGear,
            EditorTimelineTraceTempo: _editorTimelineTraceTempo,
            EditorTimelineTraceChart: _editorTimelineTraceChart,
            CachedRenderRangeTick: _cachedRenderRangeTick,
            CachedRenderRangeFirst: _cachedRenderRangeFirst,
            CachedRenderRangeLast: _cachedRenderRangeLast,
            SimultaneousCountsCache: _simultaneousCountsCache);

        var width = Math.Max(1, originalViewport.Width);
        var height = Math.Max(1, originalViewport.Height);
        if (_editorNoteGroupPlacementPreviewRenderTarget is null ||
            _editorNoteGroupPlacementPreviewRenderTarget.Width != width ||
            _editorNoteGroupPlacementPreviewRenderTarget.Height != height)
        {
            _editorNoteGroupPlacementPreviewRenderTarget?.Dispose();
            _editorNoteGroupPlacementPreviewRenderTarget = NativeResourceDiagnostics.TrackTexture(
                new RenderTarget2D(
                    _graphics, width, height, false, SurfaceFormat.Color, DepthFormat.None, 0,
                    RenderTargetUsage.PreserveContents),
                NativeResourceCategory.Editor);
        }

        _editorNoteGroupPlacementPreviewRendering = true;
        try
        {
            // This temporary chart is kept separate from the real chart. Swapping
            // _chart avoids copying the source chart's (potentially huge) Tracks,
            // Events, and Links lists every time the hover target changes. The
            // source tempo/gear maps stay active because the preview only changes
            // note placement, not the chart's route or timing.
            _chart = previewChart;
            previewChart.InitialGearAttr = sourceChart.InitialGearAttr;
            previewChart.InitialTempo = sourceChart.InitialTempo;
            previewChart.TicksPerMeasure = sourceChart.TicksPerMeasure;
            previewChart.PlayTick = 0;

            var renderCaches = _editorNoteGroupPlacementPreviewRenderCaches;
            var rebuildPreviewData = !renderCaches.IsValid ||
                !ReferenceEquals(renderCaches.SourceChart, sourceChart) ||
                renderCaches.ContentHash != contentHash;

            if (rebuildPreviewData)
            {
                previewChart.Tracks.Clear();
                previewChart.Events.Clear();
                previewChart.Links.Clear();
                var previewTracks = new ChartTrack?[16];
                foreach (var ev in previewEvents)
                {
                    if (ev.Track is < 0 or > 15)
                        continue;
                    var track = previewTracks[ev.Track];
                    if (track is null)
                    {
                        track = new ChartTrack { TrackIndex = ev.Track };
                        previewTracks[ev.Track] = track;
                        previewChart.Tracks.Add(track);
                    }
                    track.Events.Add(ev);
                }

                AresLoader.FinalizeEvents(previewChart);
                LinkBuilder.Build(previewChart);

                // Reuse the preview-only cache collections. The real chart's cache
                // references are restored below, and the preview caches remain warm
                // for the next frame/cursor-position update.
                renderCaches.Apply(this);
                RebuildLinkAdjacencyCache();
                RebuildLinkRootMap();
                RebuildRenderEventCache();
                _simultaneousCountsCache.Clear();
                renderCaches.SourceChart = sourceChart;
                renderCaches.ContentHash = contentHash;
                renderCaches.IsValid = true;
                renderCaches.Capture(this);
            }
            else
            {
                renderCaches.Apply(this);
            }

            // A placement preview represents the complete pattern, including notes
            // in banks that are faded/hidden during the normal split pass.
            _editorActiveBanksOnlyPass = false;
            _editorInactiveBankLayerPass = false;
            _graphics.SetRenderTarget(_editorNoteGroupPlacementPreviewRenderTarget);
            _graphics.Viewport = new Viewport(0, 0, width, height);
            _graphics.Clear(XnaColor.Transparent);
            // The preview target can be refreshed for a viewport/lane-layout change
            // even when the chart tick is unchanged. Force the cached render range
            // to be recalculated against the current projection in that case.
            _cachedRenderRangeTick = double.NaN;
            _cachedRenderRangeFirst = 0;
            _cachedRenderRangeLast = 0;
            DrawNotes(currentTick, currentState);
            renderCaches.Capture(this);
        }
        finally
        {
            try
            {
                _chart = sourceChart;
                _linkRootByKey = originalRenderCaches.LinkRootByKey;
                _nextLinkByHeadKey = originalRenderCaches.NextLinkByHeadKey;
                _linkHeadByTailKey = originalRenderCaches.LinkHeadByTailKey;
                _ambiguousNextLinkHeadKeys = originalRenderCaches.AmbiguousNextLinkHeadKeys;
                _linkHeadKeys = originalRenderCaches.LinkHeadKeys;
                _orderedRenderableEvents = originalRenderCaches.OrderedRenderableEvents;
                _eventSeconds = originalRenderCaches.EventSeconds;
                _eventScrollUnits = originalRenderCaches.EventScrollUnits;
                _eventEffectiveEndSeconds = originalRenderCaches.EventEffectiveEndSeconds;
                _eventEffectiveEndScrollUnits = originalRenderCaches.EventEffectiveEndScrollUnits;
                _longEventEndScrollUnits = originalRenderCaches.LongEventEndScrollUnits;
                _longRenderableEventIndicesByEnd = originalRenderCaches.LongRenderableEventIndicesByEnd;
                _renderEventIndexScratch = originalRenderCaches.RenderEventIndexScratch;
                _renderEventMarks = originalRenderCaches.RenderEventMarks;
                _renderEventMarkStamp = originalRenderCaches.RenderEventMarkStamp;
                _linkChainCache = originalRenderCaches.LinkChainCache;
                _linkChainEffectiveEndTickCache = originalRenderCaches.LinkChainEffectiveEndTickCache;
                _linkChainRootByMember = originalRenderCaches.LinkChainRootByMember;
                _eventSecondsByKey = originalRenderCaches.EventSecondsByKey;
                _eventsByKey = originalRenderCaches.EventsByKey;
                _bankChangingSuperFlicks = originalRenderCaches.BankChangingSuperFlicks;
                _bankChangingSuperFlickTicks = originalRenderCaches.BankChangingSuperFlickTicks;
                _editorTimelineTraceSuperFlicks = originalRenderCaches.EditorTimelineTraceSuperFlicks;
                _editorTimelineTraceGear = originalRenderCaches.EditorTimelineTraceGear;
                _editorTimelineTraceTempo = originalRenderCaches.EditorTimelineTraceTempo;
                _editorTimelineTraceChart = originalRenderCaches.EditorTimelineTraceChart;
                _cachedRenderRangeTick = originalRenderCaches.CachedRenderRangeTick;
                _cachedRenderRangeFirst = originalRenderCaches.CachedRenderRangeFirst;
                _cachedRenderRangeLast = originalRenderCaches.CachedRenderRangeLast;
                _simultaneousCountsCache = originalRenderCaches.SimultaneousCountsCache;
            }
            finally
            {
                try
                {
                    if (originalTargets.Length == 0)
                        _graphics.SetRenderTarget(null);
                    else
                        _graphics.SetRenderTargets(originalTargets);
                    _graphics.Viewport = originalViewport;
                }
                finally
                {
                    _editorActiveBanksOnlyPass = originalActiveBanksOnlyPass;
                    _editorInactiveBankLayerPass = originalInactiveBankLayerPass;
                    _editorNoteGroupPlacementPreviewRendering = false;
                }
            }
        }
    }

    private (int GroupId, int InstanceId)? GetSelectedEditorNoteGroupInstance()
    {
        // A single selection is represented by the anchor key. Resolve it directly
        // through the event index instead of building and sorting a selected-note list.
        if (_editorMultiSelectedEvents.Count == 0)
        {
            if (_selectedEventKey is ChartEventKey key &&
                _eventsByKey.TryGetValue(key, out var selected) &&
                selected.Kind == "Note" && selected.RawType == 1 &&
                selected.NoteGroupId is int groupId &&
                selected.NoteGroupInstanceId is int instanceId)
                return (groupId, instanceId);
            return null;
        }

        EnsureEditorMultiSelectionGroupSummary();
        return _editorMultiSelectionGroupInstanceSummary;
    }

    private bool CanCreateEditorNoteGroupFromSelection()
        => _chart is not null && !_editorNoteGroupEditMode &&
           CanCreateEditorNoteGroupFromMultiSelection();

    private bool CanUngroupSelectedEditorNoteGroupInstance()
    {
        if (_chart is null)
            return false;

        RefreshEditorNoteGroupLibrarySelectionFromNotes();
        if (_editorSelectedNoteGroupId is not int selectedGroupId)
            return false;
        var selectedInstance = GetSelectedEditorNoteGroupInstance();
        return selectedInstance is { } key && key.GroupId == selectedGroupId &&
               _chart.NoteGroupInstances.Any(instance => instance.GroupId == key.GroupId && instance.InstanceId == key.InstanceId);
    }

    private void RefreshEditorNoteGroupLibrarySelectionFromNotes()
    {
        if (_chart is null)
        {
            _editorSelectedNoteGroupId = null;
            _editorNoteGroupLibrarySelectionExplicit = false;
            _editorNoteGroupPlacementArmed = false;
            return;
        }

        if (_editorNoteGroupEditMode && _editorEditingNoteGroupId is int editingGroupId &&
            _chart.NoteGroups.Any(group => group.Id == editingGroupId))
        {
            _editorSelectedNoteGroupId = editingGroupId;
            return;
        }

        if (_editorNoteGroupPlacementArmed)
        {
            if (_editorSelectedNoteGroupId is int armedGroupId &&
                _chart.NoteGroups.Any(group => group.Id == armedGroupId))
                return;

            _editorNoteGroupPlacementArmed = false;
            _editorSelectedNoteGroupId = null;
            _editorNoteGroupLibrarySelectionExplicit = false;
        }

        // Selecting a library row is independent of chart selection. Preserve it
        // until a chart-selection action explicitly changes focus.
        if (_editorNoteGroupLibrarySelectionExplicit &&
            _editorSelectedNoteGroupId is int explicitGroupId &&
            _chart.NoteGroups.Any(group => group.Id == explicitGroupId))
            return;

        _editorNoteGroupLibrarySelectionExplicit = false;
        int? selectedGroupId = null;
        if (_editorMultiSelectedEvents.Count > 0)
        {
            EnsureEditorMultiSelectionGroupSummary();
            selectedGroupId = _editorMultiSelectionGroupIdSummary;
        }
        else if (_selectedEventKey is ChartEventKey key &&
                 _eventsByKey.TryGetValue(key, out var selected) &&
                 selected.Kind == "Note" && selected.RawType == 1)
        {
            selectedGroupId = selected.NoteGroupId;
        }

        if (selectedGroupId is not int groupId ||
            !_chart.NoteGroups.Any(group => group.Id == groupId))
        {
            _editorSelectedNoteGroupId = null;
            return;
        }

        _editorSelectedNoteGroupId = groupId;
        _editorNoteGroupPlacementArmed = false;
        var orderedGroups = _chart.NoteGroups.OrderBy(group => group.Id).ToList();
        var index = orderedGroups.FindIndex(group => group.Id == groupId);
        if (index >= 0 && (index < _editorNoteGroupsScrollOffset ||
                           index >= _editorNoteGroupsScrollOffset + EditorNoteGroupVisibleRows))
        {
            SetEditorNoteGroupScrollPosition(Math.Clamp(
                index - EditorNoteGroupVisibleRows + 1, 0,
                Math.Max(0, orderedGroups.Count - EditorNoteGroupVisibleRows)), immediate: true);
        }
    }

    private void ClearEditorNoteGroupLibrarySelection()
    {
        if (_editorNoteGroupEditMode)
            return;

        _editorSelectedNoteGroupId = null;
        _editorNoteGroupLibrarySelectionExplicit = false;
        _editorNoteGroupPlacementArmed = false;
        InvalidateEditorNoteGroupPlacementPreviewCache();
    }

    private Dictionary<string, object?> BuildEditorNoteGroupActions()
    {
        RefreshEditorNoteGroupLibrarySelectionFromNotes();
        var hasGroups = _chart?.NoteGroups.Count > 0;
        var selectedGroupExists = _chart?.NoteGroups.Any(group => group.Id == _editorSelectedNoteGroupId) == true;
        var create = EditorUiItem("note_group_create", "CREATE", "editor.note_groups.create", null,
            enabled: CanCreateEditorNoteGroupFromSelection());
        create["variant"] = "success";
        var ungroup = EditorUiItem("note_group_ungroup", "UNGROUP", "editor.note_groups.ungroup", null,
            enabled: CanUngroupSelectedEditorNoteGroupInstance());
        var remove = EditorUiItem("note_group_remove", "REMOVE GROUP", "editor.note_groups.remove", null,
            enabled: selectedGroupExists);
        remove["variant"] = "warning";
        var deleteAll = EditorUiItem("note_group_delete_all", "DELETE ALL", "editor.note_groups.delete_all", null,
            enabled: hasGroups);
        deleteAll["variant"] = "danger";
        var consolidate = EditorUiItem("note_group_consolidate", "CONSOLIDATE GROUPS",
            "editor.note_groups.consolidate", null, enabled: hasGroups);
        consolidate["variant"] = "secondary";
        var edit = EditorUiItem("note_group_edit", "EDIT GROUP",
            "editor.note_groups.edit", null, selected: _editorNoteGroupEditMode,
            enabled: selectedGroupExists && _chart!.NoteGroupInstances.Any(instance => instance.GroupId == _editorSelectedNoteGroupId),
            toggle: true);
        return new Dictionary<string, object?>
        {
            ["create"] = create,
            ["ungroup"] = ungroup,
            ["remove"] = remove,
            ["delete_all"] = deleteAll,
            ["consolidate"] = consolidate,
            ["edit"] = edit
        };
    }

    private void SelectEditorNoteGroup(int groupId)
    {
        if (_chart is null || !_chart.NoteGroups.Any(group => group.Id == groupId))
            return;

        // Clicking the currently armed row toggles placement off, but the group
        // remains the explicit library selection (secondary success styling).
        if (_editorNoteGroupPlacementArmed && _editorSelectedNoteGroupId == groupId)
        {
            _editorNoteGroupPlacementArmed = false;
            _editorNoteGroupLibrarySelectionExplicit = true;
            InvalidateEditorNoteGroupPlacementPreviewCache();
            _statusMessage = $"Group G{groupId} remains selected; placement disarmed.";
            return;
        }

        if (_editorNoteGroupEditMode)
        {
            _editorNoteGroupEditMode = false;
            _editorEditingNoteGroupId = null;
            _editorEditingNoteGroupInstanceId = null;
            ApplyEditorNoteGroupSelectionLockToCurrentSelection();
        }

        _editorSelectedNoteGroupId = groupId;
        _editorNoteGroupLibrarySelectionExplicit = true;
        InvalidateEditorNoteGroupPlacementPreviewCache();
        _editorNoteGroupPlacementArmed = true;
        _editorPlacementEnabled = false;
        _editorConnectorMode = false;
        _editorSuperFlickTool = EditorSuperFlickTool.None;
        _editorSelectedTempoEvent = null;
        SetEditorNoteGroupScrollPosition(Math.Max(0,
            _chart.NoteGroups.OrderBy(group => group.Id).ToList().FindIndex(group => group.Id == groupId) - EditorNoteGroupVisibleRows + 1), immediate: true);
        _statusMessage = $"Group G{groupId} selected. Click an empty lane cell to place another instance; click this row again or press Cancel to stop.";
    }

    private void PlaceEditorNoteGroupInstance(int startTick, int startTrack)
    {
        if (_chart is null || _editorSelectedNoteGroupId is not int groupId)
            return;

        // Resolve logical chain roots once against a complete graph before planning
        // pair assignments. Do not rebuild the graph from partially placed notes.
        RefreshEditorNoteGroupEventGraph();
        if (!TryGetEditorNoteGroupSource(groupId, out var group, out var sourceInstance,
                out var sourceEvents, out var sourceByMember))
        {
            _statusMessage = $"Group G{groupId} cannot be placed because no complete instance matches its library definition.";
            return;
        }

        var nextInstanceId = _chart.NoteGroupInstances.Where(instance => instance.GroupId == groupId)
            .Select(instance => instance.InstanceId).DefaultIfEmpty(0).Max();
        if (nextInstanceId == int.MaxValue)
        {
            _statusMessage = $"Group G{groupId} has reached the maximum number of instances.";
            return;
        }
        var targetCanonicalStartTrack = sourceInstance.IsMirrored ? MirrorEditorTrackX(startTrack) : startTrack;
        var targetInstance = new NoteGroupInstance(
            groupId, nextInstanceId + 1, startTick, targetCanonicalStartTrack, sourceInstance.IsMirrored);

        var plannedCells = new HashSet<(int Tick, int Track)>();
        var plannedSuperFlickBanks = new HashSet<(int Tick, string Side, string Bank)>();
        var plannedChains = new Dictionary<int, string?>();
        foreach (var member in group.Members)
        {
            var source = sourceByMember[member.Id];
            var tickLong = (long)startTick + source.Tick - sourceInstance.StartTick;
                var trackIndex = GetEditorNoteGroupCounterpartTrack(sourceInstance, targetInstance, source.Track);
            if (tickLong < 0 || tickLong > int.MaxValue || trackIndex is < 0 or > 15)
            {
                _statusMessage = $"Group G{groupId} does not fit at tick {startTick}, track {startTrack}.";
                return;
            }

            var tick = (int)tickLong;
            if (!IsEditorTrackAllowedForNoteType(trackIndex, source.NoteType))
            {
                _statusMessage = $"Group G{groupId} cannot be placed here: its {source.NoteType} notes do not fit the destination lane layout.";
                return;
            }
            if (!plannedCells.Add((tick, trackIndex)))
            {
                _statusMessage = $"Group G{groupId} has overlapping notes and cannot be placed as an instance.";
                return;
            }
            if (FindEditorNoteAtGridCell(tick, trackIndex) is not null)
            {
                _statusMessage = $"Group placement blocked: tick {tick}, track {trackIndex} is occupied.";
                return;
            }

            if (source.NoteType is NoteType.LinkHead or NoteType.LinkTail)
            {
                var rootMemberId = GetEditorNoteGroupLogicalChainRootMemberId(group, source);
                var family = LinkBuilder.Family(trackIndex);
                if (rootMemberId is not int chainRootMemberId || family is null)
                {
                    _statusMessage = $"Group G{groupId} contains a Link chain without a valid logical root or destination lane family.";
                    return;
                }
                if (plannedChains.TryGetValue(chainRootMemberId, out var chainFamily) &&
                    !string.Equals(chainFamily, family, StringComparison.Ordinal))
                {
                    _statusMessage = $"Group G{groupId} cannot place a Link chain across incompatible lane families.";
                    return;
                }
                plannedChains[chainRootMemberId] = family;
            }

            if (source.IsSuperFlick)
            {
                var sourcePosition = GearState.Position(source.Track);
                var targetPosition = GearState.Position(trackIndex);
                if (sourcePosition is null || targetPosition is null ||
                    !string.Equals(sourcePosition.Value.Side, targetPosition.Value.Side, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(sourcePosition.Value.Bank, targetPosition.Value.Bank, StringComparison.OrdinalIgnoreCase))
                {
                    _statusMessage = $"Group G{groupId} cannot move a SuperFlick to a different side or bank.";
                    return;
                }
                var key = (tick, targetPosition.Value.Side.ToUpperInvariant(), targetPosition.Value.Bank.ToUpperInvariant());
                if (!plannedSuperFlickBanks.Add(key) || HasSuperFlickBankCollision(tick, trackIndex))
                {
                    _statusMessage = $"Group placement blocked: a SuperFlick already occupies that bank at tick {tick}.";
                    return;
                }
            }
        }

        var pairMaps = new Dictionary<int, Dictionary<int, EditorNoteGroupLinkPairReservation>>();
        foreach (var chain in sourceEvents.Where(ev => ev.NoteType is NoteType.LinkHead or NoteType.LinkTail)
                     .GroupBy(ev => GetEditorNoteGroupLogicalChainRootMemberId(group, ev) ?? -1))
        {
            if (chain.Key < 0)
            {
                _statusMessage = $"Group G{groupId} contains a Link chain without a stable logical root.";
                return;
            }
            if (!TryAllocateEditorNoteGroupLinkPair(group, chain.Key, chain.ToList(), sourceInstance,
                    targetInstance, pairMaps, out _, out var pairError))
            {
                _statusMessage = $"Group G{groupId} cannot be placed: {pairError}.";
                return;
            }
        }

        var snapshot = CaptureEditorChartSnapshot();
        if (snapshot is null)
        {
            _statusMessage = $"Group G{groupId} could not be placed because an undo snapshot could not be created.";
            return;
        }
        _chart.NoteGroupInstances.Add(targetInstance);
        var pasted = new List<ChartEvent>(sourceEvents.Count);
        foreach (var source in sourceEvents)
        {
            var clone = CreateEditorNoteGroupCounterpart(group, source, sourceInstance, targetInstance, pairMaps);
            if (clone is null)
            {
                RestoreEditorChartSnapshot(snapshot);
                RefreshEditorNoteGroupEventGraph();
                _statusMessage = $"Group G{groupId} could not be placed at tick {startTick}, track {startTrack}.";
                return;
            }
            pasted.Add(clone);
        }

        RefreshEditorNoteGroupEventGraph();
        CommitEditorUndoSnapshot(snapshot);
        RefreshAfterEditorMutation();
        _editorSelectedNoteGroupId = groupId;
        ClearEditorMultiSelection();
        foreach (var note in pasted)
            AddEditorMultiSelectedEvent(note);
        var anchor = pasted.OrderBy(note => note.Tick).ThenBy(note => note.Track).First();
        _selectedEventKey = KeyOf(anchor);
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorSelectedLinkRoot = GetEditorLinkRoot(anchor);
        _editorSelectedHoldEnd = false;
        _editorTrack = anchor.Track;
        _statusMessage = $"Placed Group G{groupId}, instance {GetEditorNoteGroupDisplayInstanceNumber(groupId, targetInstance.InstanceId)} ({pasted.Count} notes). Select another group or press Cancel to stop placing.";
    }

    private void CreateEditorNoteGroupFromSelection()
    {
        if (_chart is null)
            return;
        var selected = GetSelectedEditorNotes(expandLinkFamilies: true)
            .Distinct().OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track).ThenBy(ev => ev.EventIndex).ToList();
        if (selected.Count == 0 || selected.Any(ev => ev.Kind != "Note" || ev.RawType != 1 ||
                ev.Track is < 0 or > 15 || ev.NoteGroupId.HasValue ||
                ev.NoteGroupInstanceId.HasValue || ev.NoteGroupMemberId.HasValue))
        {
            _statusMessage = "CREATE needs only selected, ungrouped gameplay notes. Select the pattern first.";
            return;
        }

        var snapshot = CaptureEditorChartSnapshot();
        var groupId = _chart.NoteGroups.Select(group => group.Id).DefaultIfEmpty(0).Max() + 1;
        var startTick = selected.Min(ev => ev.Tick);
        var startTrack = selected.Min(ev => ev.Track);
        var chainIds = new Dictionary<ChartEvent, int>();
        var nextChainId = 0;
        var members = new List<NoteGroupMember>(selected.Count);
        for (var index = 0; index < selected.Count; index++)
        {
            var ev = selected[index];
            var chainRoot = GetEditorLinkRoot(ev) ?? (ev.NoteType == NoteType.LinkHead ? ev : null);
            var chainGroup = ev.NoteType is NoteType.LinkHead or NoteType.LinkTail
                ? chainRoot is not null && chainIds.TryGetValue(chainRoot, out var chainId)
                    ? chainId
                    : chainRoot is not null ? chainIds[chainRoot] = nextChainId++ : -1
                : -1;
            var memberId = index + 1;
            members.Add(new NoteGroupMember(memberId, ev.Tick - startTick, ev.Track - startTrack,
                ev.Attr, ev.Duration, ev.Instrument, ev.Velocity, ev.Pan, ev.NoteType,
                chainGroup, ev.LinePair));
            ev.NoteGroupId = groupId;
            ev.NoteGroupInstanceId = 1;
            ev.NoteGroupMemberId = memberId;
        }

        _chart.NoteGroups.Add(new NoteGroupDefinition
        {
            Id = groupId,
            Name = $"Group {groupId}",
            Members = members
        });
        _chart.NoteGroupInstances.Add(new NoteGroupInstance(groupId, 1, startTick, startTrack));
        _editorSelectedNoteGroupId = groupId;
        _editorNoteGroupLibrarySelectionExplicit = true;
        _editorNoteGroupEditMode = false;
        _editorEditingNoteGroupId = null;
        _editorEditingNoteGroupInstanceId = null;
        _editorNoteGroupPlacementArmed = false;
        ClearEditorMultiSelection();
        foreach (var ev in selected)
            AddEditorMultiSelectedEvent(ev);
        _selectedEventKey = KeyOf(selected[0]);
        CommitEditorUndoSnapshot(snapshot);
        RefreshAfterEditorMutation();
        _statusMessage = $"Created Group {groupId} from {selected.Count} note(s). Select it in NOTE GROUPS to place more instances.";
    }

    private void UngroupSelectedEditorNoteGroupInstance()
    {
        if (_chart is null || !CanUngroupSelectedEditorNoteGroupInstance())
        {
            _statusMessage = "Select notes from one instance of the selected group to ungroup that instance.";
            return;
        }

        var key = GetSelectedEditorNoteGroupInstance()!.Value;
        var displayInstanceNumber = GetEditorNoteGroupDisplayInstanceNumber(key.GroupId, key.InstanceId);
        var snapshot = CaptureEditorChartSnapshot();
        foreach (var ev in _chart.Events.Where(ev => ev.NoteGroupId == key.GroupId && ev.NoteGroupInstanceId == key.InstanceId))
        {
            ev.NoteGroupId = null;
            ev.NoteGroupInstanceId = null;
            ev.NoteGroupMemberId = null;
        }
        _chart.NoteGroupInstances.RemoveAll(instance => instance.GroupId == key.GroupId && instance.InstanceId == key.InstanceId);
        NormalizeEditorNoteGroupInstanceIds(key.GroupId);
        if (_editorEditingNoteGroupId == key.GroupId)
        {
            _editorNoteGroupEditMode = false;
            _editorEditingNoteGroupId = null;
            _editorEditingNoteGroupInstanceId = null;
        }
        CommitEditorUndoSnapshot(snapshot);
        RefreshAfterEditorMutation();
        _statusMessage = $"Ungrouped instance {displayInstanceNumber} of Group {key.GroupId}; its notes were kept.";
    }

    private void RemoveSelectedEditorNoteGroup()
    {
        if (_chart is null || _editorSelectedNoteGroupId is not int groupId)
        {
            _statusMessage = "Select a note group first.";
            return;
        }
        var group = _chart.NoteGroups.FirstOrDefault(candidate => candidate.Id == groupId);
        if (group is null)
            return;

        var snapshot = CaptureEditorChartSnapshot();
        foreach (var ev in _chart.Events.Where(ev => ev.NoteGroupId == groupId))
        {
            ev.NoteGroupId = null;
            ev.NoteGroupInstanceId = null;
            ev.NoteGroupMemberId = null;
        }
        _chart.NoteGroupInstances.RemoveAll(instance => instance.GroupId == groupId);
        _chart.NoteGroups.Remove(group);
        if (_editorEditingNoteGroupId == groupId)
        {
            _editorNoteGroupEditMode = false;
            _editorEditingNoteGroupId = null;
            _editorEditingNoteGroupInstanceId = null;
        }
        _editorSelectedNoteGroupId = null;
        _editorNoteGroupLibrarySelectionExplicit = false;
        _editorNoteGroupPlacementArmed = false;
        _editorNoteGroupsScroll.SetMaximum(GetEditorNoteGroupScrollMaximum());
        _editorNoteGroupsScrollOffset = GetEditorNoteGroupScrollFirstRow();
        CommitEditorUndoSnapshot(snapshot);
        RefreshAfterEditorMutation();
        _statusMessage = $"Removed Group {groupId}; all of its instances are now independent notes.";
    }

    private static EditorNoteGroupShape? BuildEditorNoteGroupShape(
        NoteGroupDefinition group, bool mirrored)
    {
        if (group.Members.Count == 0 ||
            group.Members.Select(member => member.Id).Distinct().Count() != group.Members.Count)
            return null;

        var minTick = group.Members.Min(member => member.TickOffset);
        var minTrack = group.Members.Min(member => member.TrackOffset);
        var maxTrack = group.Members.Max(member => member.TrackOffset);
        var baseKeys = new Dictionary<int, string>();
        foreach (var member in group.Members)
        {
            var tickOffset = member.TickOffset - minTick;
            var trackOffset = mirrored
                ? maxTrack - member.TrackOffset
                : member.TrackOffset - minTrack;
            // Link attributes contain instance-local pair numbers. Their actual
            // relationship is represented by ChainGroup, so pair numbers must not
            // prevent otherwise identical patterns from consolidating.
            var attr = member.NoteType is NoteType.LinkHead or NoteType.LinkTail
                ? 0
                : mirrored ? MirrorEditorAttrX(member.Attr) : member.Attr;
            baseKeys[member.Id] = string.Join(",", tickOffset, trackOffset, attr,
                member.Duration, member.Instrument, member.Velocity, member.Pan,
                (int)member.NoteType);
        }

        // ChainGroup numbers are local identifiers too. Canonicalize them by the
        // complete set of notes in each chain, rather than comparing their raw IDs.
        var chainIds = group.Members.Where(member => member.ChainGroup >= 0)
            .GroupBy(member => member.ChainGroup)
            .Select(chain => new
            {
                OriginalId = chain.Key,
                Signature = string.Join(";", chain.Select(member => baseKeys[member.Id])
                    .OrderBy(value => value, StringComparer.Ordinal))
            })
            .OrderBy(chain => chain.Signature, StringComparer.Ordinal)
            .ThenBy(chain => chain.OriginalId)
            .Select((chain, index) => (chain.OriginalId, CanonicalId: index))
            .ToDictionary(item => item.OriginalId, item => item.CanonicalId);

        var membersByKey = new Dictionary<string, List<NoteGroupMember>>(StringComparer.Ordinal);
        foreach (var member in group.Members)
        {
            var chainId = member.ChainGroup >= 0 && chainIds.TryGetValue(member.ChainGroup, out var canonicalId)
                ? canonicalId : -1;
            var key = $"{baseKeys[member.Id]}|{chainId}";
            if (!membersByKey.TryGetValue(key, out var matches))
                membersByKey[key] = matches = new List<NoteGroupMember>();
            matches.Add(member);
        }
        foreach (var matches in membersByKey.Values)
            matches.Sort((left, right) => left.Id.CompareTo(right.Id));

        var signature = string.Join("\n", membersByKey
            .SelectMany(pair => Enumerable.Repeat(pair.Key, pair.Value.Count))
            .OrderBy(value => value, StringComparer.Ordinal));
        return new EditorNoteGroupShape(signature, membersByKey);
    }

    private static EditorNoteGroupPatternInfo? BuildEditorNoteGroupPatternInfo(NoteGroupDefinition group)
    {
        var normal = BuildEditorNoteGroupShape(group, mirrored: false);
        var mirrored = BuildEditorNoteGroupShape(group, mirrored: true);
        if (normal is null || mirrored is null)
            return null;

        var comparison = string.CompareOrdinal(normal.Signature, mirrored.Signature);
        return new EditorNoteGroupPatternInfo(group,
            comparison <= 0 ? normal.Signature : mirrored.Signature,
            comparison > 0);
    }

    private static bool TryBuildEditorNoteGroupMemberRemap(
        NoteGroupDefinition source, NoteGroupDefinition target, bool mirrorSource,
        out Dictionary<int, int> memberMap)
    {
        memberMap = new Dictionary<int, int>();
        var sourceShape = BuildEditorNoteGroupShape(source, mirrorSource);
        var targetShape = BuildEditorNoteGroupShape(target, mirrored: false);
        if (sourceShape is null || targetShape is null ||
            !string.Equals(sourceShape.Signature, targetShape.Signature, StringComparison.Ordinal))
            return false;

        foreach (var pair in sourceShape.MembersByKey)
        {
            if (!targetShape.MembersByKey.TryGetValue(pair.Key, out var targetMembers) ||
                pair.Value.Count != targetMembers.Count)
                return false;

            var sourceMembers = pair.Value.OrderBy(member => member.Id).ToList();
            var orderedTargets = targetMembers.OrderBy(member => member.Id).ToList();
            for (var index = 0; index < sourceMembers.Count; index++)
                memberMap[sourceMembers[index].Id] = orderedTargets[index].Id;
        }

        return memberMap.Count == source.Members.Count &&
               memberMap.Values.Distinct().Count() == target.Members.Count;
    }

    private bool TryBuildConsolidatedEditorNoteGroupInstance(
        NoteGroupDefinition sourceGroup,
        NoteGroupDefinition representative,
        NoteGroupInstance sourceInstance,
        IReadOnlyDictionary<int, int> memberMap,
        bool mirrorToRepresentative,
        int representativeAnchor,
        int newInstanceId,
        out NoteGroupInstance consolidated,
        out string error)
    {
        consolidated = null!;
        error = string.Empty;
        var sourceMinTick = sourceGroup.Members.Min(member => member.TickOffset);
        var targetMinTick = representative.Members.Min(member => member.TickOffset);
        long startTickLong = (long)sourceInstance.StartTick + sourceMinTick - targetMinTick;
        var targetIsMirrored = sourceInstance.IsMirrored ^ mirrorToRepresentative;
        var targetMembers = representative.Members.ToDictionary(member => member.Id);
        var laneMap = new Dictionary<int, int>();
        long? observedStartTick = null;
        foreach (var sourceMember in sourceGroup.Members)
        {
            if (!memberMap.TryGetValue(sourceMember.Id, out var targetMemberId) ||
                !targetMembers.TryGetValue(targetMemberId, out var targetMember))
            {
                error = $"Group {sourceGroup.Id} contains a member that cannot be mapped to its consolidated definition.";
                return false;
            }

            var sourceCanonicalTrackLong = (long)sourceInstance.StartTrack + sourceMember.TrackOffset;
            if (sourceCanonicalTrackLong < int.MinValue || sourceCanonicalTrackLong > int.MaxValue)
            {
                error = $"Group {sourceGroup.Id} has a canonical lane outside the supported coordinate range.";
                return false;
            }
            var sourceCanonicalTrack = (int)sourceCanonicalTrackLong;
            var sourceEvent = _chart?.Events.FirstOrDefault(ev => ev.NoteGroupId == sourceGroup.Id &&
                ev.NoteGroupInstanceId == sourceInstance.InstanceId && ev.NoteGroupMemberId == sourceMember.Id);
            var physicalTrack = sourceEvent?.Track ?? GetPhysicalEditorNoteGroupTrack(sourceInstance, sourceCanonicalTrack);
            if (sourceEvent is not null)
            {
                var candidateStart = (long)sourceEvent.Tick - targetMember.TickOffset;
                if (candidateStart < 0 || candidateStart > int.MaxValue ||
                    (observedStartTick.HasValue && observedStartTick.Value != candidateStart))
                {
                    error = $"Group {sourceGroup.Id} has an instance whose note ticks do not match one shared pattern anchor.";
                    return false;
                }
                observedStartTick = candidateStart;
            }

            var targetCanonicalTrackLong = (long)representativeAnchor + targetMember.TrackOffset;
            if (physicalTrack is < 0 or > 15 || targetCanonicalTrackLong < int.MinValue ||
                targetCanonicalTrackLong > int.MaxValue)
            {
                error = $"Group {sourceGroup.Id} has a lane outside the supported group coordinate range.";
                return false;
            }
            var targetCanonicalTrack = (int)targetCanonicalTrackLong;

            // Rebuild the per-instance map in the representative's canonical
            // coordinates. This preserves the exact physical lanes for both
            // mirrored and bank-fitted instances after their definition changes.
            var unmirroredPhysicalTrack = targetIsMirrored
                ? MirrorEditorTrackX(physicalTrack) : physicalTrack;
            if (laneMap.TryGetValue(targetCanonicalTrack, out var existingTrack))
            {
                if (existingTrack != unmirroredPhysicalTrack)
                {
                    error = $"Group {sourceGroup.Id} has inconsistent notes on one mapped lane.";
                    return false;
                }
            }
            else
            {
                laneMap[targetCanonicalTrack] = unmirroredPhysicalTrack;
            }
        }

        if (laneMap.Values.Distinct().Count() != laneMap.Count)
        {
            error = $"Group {sourceGroup.Id} cannot be represented as a one-to-one lane mapping.";
            return false;
        }

        if (observedStartTick.HasValue)
            startTickLong = observedStartTick.Value;
        if (startTickLong < 0 || startTickLong > int.MaxValue)
        {
            error = $"Group {sourceGroup.Id} has an instance whose normalized tick anchor is outside the chart.";
            return false;
        }

        consolidated = new NoteGroupInstance(
            representative.Id, newInstanceId, (int)startTickLong, representativeAnchor,
            targetIsMirrored, laneMap.OrderBy(pair => pair.Key)
                .Select(pair => new NoteGroupTrackMapping(pair.Key, pair.Value)).ToList());
        return true;
    }

    private void ConsolidateEditorNoteGroups()
    {
        if (_chart is null || _chart.NoteGroups.Count == 0)
        {
            _statusMessage = "There are no note groups to consolidate.";
            return;
        }

        RefreshEditorNoteGroupEventGraph();
        var originalGroups = _chart.NoteGroups.OrderBy(group => group.Id).ToList();
        if (originalGroups.Select(group => group.Id).Distinct().Count() != originalGroups.Count)
        {
            _statusMessage = "Cannot consolidate groups because the chart contains duplicate group IDs.";
            return;
        }
        var groupById = originalGroups.ToDictionary(group => group.Id);
        var originalInstances = _chart.NoteGroupInstances.ToList();
        if (originalInstances.Any(instance => !groupById.ContainsKey(instance.GroupId)) ||
            originalInstances.GroupBy(instance => instance.GroupId)
                .Any(group => group.Select(instance => instance.InstanceId).Distinct().Count() != group.Count()))
        {
            _statusMessage = "Cannot consolidate groups because the chart contains invalid group-instance metadata.";
            return;
        }

        if (_chart.Events.Any(ev => !ev.NoteGroupId.HasValue &&
                (ev.NoteGroupInstanceId.HasValue || ev.NoteGroupMemberId.HasValue)))
        {
            _statusMessage = "Cannot consolidate groups because the chart contains a note with incomplete group identity.";
            return;
        }

        var groupedEvents = _chart.Events.Where(ev => ev.NoteGroupId.HasValue).ToList();
        if (groupedEvents.Any(ev => ev.Kind != "Note" || ev.RawType != 1) ||
            groupedEvents.GroupBy(ev => (ev.NoteGroupId, ev.NoteGroupInstanceId, ev.NoteGroupMemberId))
                .Any(group => group.Count() != 1))
        {
            _statusMessage = "Cannot consolidate groups because grouped note identities are duplicated or invalid.";
            return;
        }
        foreach (var ev in groupedEvents)
        {
            if (ev.NoteGroupId is not int groupId || !groupById.TryGetValue(groupId, out var definition) ||
                ev.NoteGroupInstanceId is not int instanceId || ev.NoteGroupMemberId is not int memberId ||
                !originalInstances.Any(instance => instance.GroupId == groupId && instance.InstanceId == instanceId) ||
                !definition.Members.Any(member => member.Id == memberId))
            {
                _statusMessage = "Cannot consolidate groups because one or more grouped notes have incomplete metadata.";
                return;
            }
        }

        var patternInfoById = originalGroups
            .Select(group => BuildEditorNoteGroupPatternInfo(group))
            .Where(info => info is not null)
            .Select(info => info!)
            .ToDictionary(info => info.Group.Id);
        var buckets = new List<List<NoteGroupDefinition>>();
        var consumed = new HashSet<int>();
        foreach (var group in originalGroups)
        {
            if (consumed.Contains(group.Id))
                continue;
            if (!patternInfoById.TryGetValue(group.Id, out var info))
            {
                buckets.Add(new List<NoteGroupDefinition> { group });
                consumed.Add(group.Id);
                continue;
            }

            var bucket = originalGroups.Where(candidate =>
                    patternInfoById.TryGetValue(candidate.Id, out var candidateInfo) &&
                    string.Equals(candidateInfo.CanonicalSignature, info.CanonicalSignature, StringComparison.Ordinal))
                .ToList();
            foreach (var candidate in bucket)
                consumed.Add(candidate.Id);
            buckets.Add(bucket);
        }

        var clusters = new List<EditorNoteGroupConsolidationCluster>();
        foreach (var bucket in buckets)
        {
            var representative = bucket
                .OrderByDescending(group => originalInstances.Count(instance => instance.GroupId == group.Id))
                .ThenBy(group => group.Id)
                .First();
            var representativeInfo = patternInfoById.GetValueOrDefault(representative.Id);
            var sources = new List<EditorNoteGroupSourceRemap>();
            foreach (var source in bucket.OrderBy(group => group.Id))
            {
                var mirrorToRepresentative = source.Id != representative.Id &&
                    patternInfoById[source.Id].IsMirroredToCanonical != representativeInfo!.IsMirroredToCanonical;
                Dictionary<int, int> memberMap;
                if (source.Members.Count == 0 && representative.Members.Count == 0)
                {
                    memberMap = new Dictionary<int, int>();
                }
                else if (!TryBuildEditorNoteGroupMemberRemap(source, representative,
                             mirrorToRepresentative, out memberMap))
                {
                    _statusMessage = $"Cannot consolidate Group {source.Id}: its member data cannot be mapped safely.";
                    return;
                }
                sources.Add(new EditorNoteGroupSourceRemap(source, memberMap, mirrorToRepresentative));
            }
            clusters.Add(new EditorNoteGroupConsolidationCluster(representative, sources));
        }

        // The surviving group is the one with the most instances (lowest ID on a
        // tie). Sort those survivors by their original IDs, then compact to 1..N.
        clusters = clusters.OrderBy(cluster => cluster.Representative.Id).ToList();
        var oldGroupToNewGroup = new Dictionary<int, int>();
        var sourceRemapByGroup = new Dictionary<int, EditorNoteGroupSourceRemap>();
        var newGroups = new List<NoteGroupDefinition>();
        for (var index = 0; index < clusters.Count; index++)
        {
            var cluster = clusters[index];
            var newGroupId = index + 1;
            var oldRepresentativeId = cluster.Representative.Id;
            var name = string.Equals(cluster.Representative.Name, $"Group {oldRepresentativeId}", StringComparison.OrdinalIgnoreCase)
                ? $"Group {newGroupId}" : cluster.Representative.Name;
            newGroups.Add(new NoteGroupDefinition
            {
                Id = newGroupId,
                Name = name,
                Members = cluster.Representative.Members.ToList()
            });
            foreach (var source in cluster.Sources)
            {
                oldGroupToNewGroup[source.Source.Id] = newGroupId;
                sourceRemapByGroup[source.Source.Id] = source;
            }
        }

        var mergedCount = originalGroups.Count - newGroups.Count;
        var renumberedCount = originalGroups.Count(group => oldGroupToNewGroup[group.Id] != group.Id);
        if (mergedCount == 0 && renumberedCount == 0)
        {
            _statusMessage = "No repeated or mirrored group patterns were found; group IDs are already sequential.";
            return;
        }

        var oldInstanceToNew = new Dictionary<(int GroupId, int InstanceId), int>();
        var newInstances = new List<NoteGroupInstance>();
        foreach (var cluster in clusters)
        {
            var representative = cluster.Representative;
            var representativeInstances = originalInstances.Where(instance => instance.GroupId == representative.Id)
                .OrderBy(instance => instance.InstanceId).ToList();
            var usedInstanceIds = new HashSet<int>();
            long nextInstanceId = (long)representativeInstances.Select(instance => instance.InstanceId)
                .DefaultIfEmpty(0).Max() + 1;

            int? AllocateInstanceId(int preferred)
            {
                if (preferred > 0 && usedInstanceIds.Add(preferred))
                    return preferred;
                while (nextInstanceId <= int.MaxValue && usedInstanceIds.Contains((int)nextInstanceId))
                    nextInstanceId++;
                if (nextInstanceId > int.MaxValue)
                    return null;
                var allocated = (int)nextInstanceId++;
                usedInstanceIds.Add(allocated);
                return allocated;
            }

            foreach (var instance in representativeInstances)
            {
                var assignedId = AllocateInstanceId(instance.InstanceId);
                if (assignedId is not int validInstanceId)
                {
                    _statusMessage = "Cannot consolidate groups because no unique instance IDs remain available.";
                    return;
                }
                oldInstanceToNew[(instance.GroupId, instance.InstanceId)] = validInstanceId;
                newInstances.Add(instance with { GroupId = representative.Id, InstanceId = validInstanceId });
            }

            var representativeAnchor = representativeInstances.FirstOrDefault()?.StartTrack ?? 0;
            foreach (var source in cluster.Sources.Where(source => source.Source.Id != representative.Id)
                         .OrderBy(source => source.Source.Id))
            {
                foreach (var instance in originalInstances.Where(instance => instance.GroupId == source.Source.Id)
                             .OrderBy(instance => instance.InstanceId))
                {
                    var assignedId = AllocateInstanceId(instance.InstanceId);
                    if (assignedId is not int validInstanceId)
                    {
                        _statusMessage = "Cannot consolidate groups because no unique instance IDs remain available.";
                        return;
                    }
                    if (!TryBuildConsolidatedEditorNoteGroupInstance(source.Source, representative, instance,
                            source.MemberMap, source.MirrorToRepresentative, representativeAnchor, validInstanceId,
                            out var consolidated, out var error))
                    {
                        _statusMessage = $"Cannot consolidate groups: {error}";
                        return;
                    }
                    oldInstanceToNew[(instance.GroupId, instance.InstanceId)] = validInstanceId;
                    newInstances.Add(consolidated);
                }
            }
        }

        var eventUpdates = new List<(ChartEvent Event, int GroupId, int InstanceId, int MemberId)>();
        foreach (var ev in groupedEvents)
        {
            var oldGroupId = ev.NoteGroupId!.Value;
            var oldInstanceId = ev.NoteGroupInstanceId!.Value;
            var oldMemberId = ev.NoteGroupMemberId!.Value;
            var sourceRemap = sourceRemapByGroup[oldGroupId];
            if (!sourceRemap.MemberMap.TryGetValue(oldMemberId, out var newMemberId) ||
                !oldInstanceToNew.TryGetValue((oldGroupId, oldInstanceId), out var newInstanceId))
            {
                _statusMessage = "Cannot consolidate groups because a note could not be mapped to its new instance.";
                return;
            }
            eventUpdates.Add((ev, oldGroupToNewGroup[oldGroupId], newInstanceId, newMemberId));
        }

        var snapshot = CaptureEditorChartSnapshot();
        if (snapshot is null)
            return;

        // All validation and mapping is complete before mutating chart metadata.
        foreach (var update in eventUpdates)
        {
            update.Event.NoteGroupId = update.GroupId;
            update.Event.NoteGroupInstanceId = update.InstanceId;
            update.Event.NoteGroupMemberId = update.MemberId;
        }
        _chart.NoteGroups.Clear();
        _chart.NoteGroups.AddRange(newGroups);
        _chart.NoteGroupInstances.Clear();
        _chart.NoteGroupInstances.AddRange(newInstances.Select(instance =>
            instance with { GroupId = oldGroupToNewGroup[instance.GroupId] }));

        _editorNoteGroupEditMode = false;
        _editorEditingNoteGroupId = null;
        _editorEditingNoteGroupInstanceId = null;
        _editorNoteGroupPlacementArmed = false;
        InvalidateEditorNoteGroupPlacementPreviewCache();
        // Re-derive the library selection from selected chart notes. Keeping an
        // explicit old group ID here would make Undo point at whichever compacted
        // group later inherited that number instead of the original group.
        _editorSelectedNoteGroupId = null;
        _editorNoteGroupLibrarySelectionExplicit = false;
        _editorNoteGroupsScroll.Reset();
        _editorNoteGroupsScrollOffset = 0;

        // This is a metadata-identity operation, not a note drag. Avoid the usual
        // instance-anchor refresh because all note positions are intentionally
        // unchanged while their group/member IDs are being rewritten.
        AresLoader.FinalizeEvents(_chart);
        PruneEmptyEditorNoteGroupInstances();
        PushEditorUndoSnapshot(snapshot);
        RefreshAfterEditorMutation();
        RefreshEditorNoteGroupLibrarySelectionFromNotes();
        _statusMessage = mergedCount > 0
            ? $"Consolidated {mergedCount} duplicate group(s) and renumbered {newGroups.Count} remaining group(s)."
            : renumberedCount > 0
                ? $"Renumbered {renumberedCount} group(s). No repeated patterns were found."
                : "No repeated or mirrored group patterns were found; group IDs are already sequential.";
    }

    private void DeleteAllEditorNoteGroups()
    {
        if (_chart is null || _chart.NoteGroups.Count == 0)
        {
            _statusMessage = "There are no note groups to delete.";
            return;
        }

        var count = _chart.NoteGroups.Count;
        var groupedNotes = _chart.Events.Where(ev => ev.Kind == "Note" && ev.RawType == 1 &&
            ev.NoteGroupId.HasValue && ev.NoteGroupInstanceId.HasValue).ToHashSet();
        var snapshot = CaptureEditorChartSnapshot();
        if (groupedNotes.Count > 0)
            DeleteEditorEvents(groupedNotes);
        _chart.NoteGroups.Clear();
        _chart.NoteGroupInstances.Clear();
        _editorSelectedNoteGroupId = null;
        _editorNoteGroupLibrarySelectionExplicit = false;
        _editorNoteGroupPlacementArmed = false;
        _editorNoteGroupEditMode = false;
        _editorEditingNoteGroupId = null;
        _editorEditingNoteGroupInstanceId = null;
        _editorNoteGroupsScroll.Reset();
        _editorNoteGroupsScrollOffset = 0;
        ClearEditorMultiSelection();
        _selectedEventKey = null;
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        CommitEditorUndoSnapshot(snapshot);
        RefreshAfterEditorMutation();
        _statusMessage = $"Deleted all {count} note group(s) and their grouped notes.";
    }

    private void ExitEditorNoteGroupEditMode(string? statusMessage = null)
    {
        if (!_editorNoteGroupEditMode)
            return;

        var groupId = _editorEditingNoteGroupId;
        _editorNoteGroupEditMode = false;
        _editorEditingNoteGroupId = null;
        _editorEditingNoteGroupInstanceId = null;
        _editorNoteGroupPlacementArmed = false;
        _editorNoteGroupLibrarySelectionExplicit = groupId is int selectedId &&
            _chart?.NoteGroups.Any(group => group.Id == selectedId) == true;
        ApplyEditorNoteGroupSelectionLockToCurrentSelection();
        _statusMessage = statusMessage ?? (groupId is int id
            ? $"Exited EDIT GROUP for Group {id}. Selecting one note selects its whole instance again."
            : "Exited EDIT GROUP. Selecting one note selects its whole instance again.");
    }

    private void ToggleEditorNoteGroupEditMode()
    {
        if (_chart is null || _editorSelectedNoteGroupId is not int groupId)
        {
            _statusMessage = "Select a note group first.";
            return;
        }
        _editorNoteGroupPlacementArmed = false;
        if (_editorNoteGroupEditMode && _editorEditingNoteGroupId == groupId)
        {
            ExitEditorNoteGroupEditMode();
            return;
        }

        var instances = _chart.NoteGroupInstances.Where(instance => instance.GroupId == groupId)
            .OrderBy(instance => instance.StartTick).ThenBy(instance => instance.StartTrack).ThenBy(instance => instance.InstanceId).ToList();
        if (instances.Count == 0)
        {
            _statusMessage = "This group has no placed instances to edit.";
            return;
        }

        var selectedKey = GetSelectedEditorNoteGroupInstance();
        var active = selectedKey is { } key && key.GroupId == groupId
            ? instances.FirstOrDefault(instance => instance.InstanceId == key.InstanceId)
            : null;
        active ??= instances[0];
        EnterEditorNoteGroupEditMode(groupId, active.InstanceId, null);
    }

    private bool EnterEditorNoteGroupEditMode(int groupId, int instanceId, ChartEvent? selectedNote)
    {
        if (_chart is null || !_chart.NoteGroups.Any(group => group.Id == groupId) ||
            !_chart.NoteGroupInstances.Any(instance => instance.GroupId == groupId && instance.InstanceId == instanceId))
            return false;

        _editorSelectedNoteGroupId = groupId;
        _editorNoteGroupLibrarySelectionExplicit = false;
        _editorNoteGroupPlacementArmed = false;
        _editorNoteGroupEditMode = true;
        _editorEditingNoteGroupId = groupId;
        _editorEditingNoteGroupInstanceId = instanceId;
        ClearEditorMultiSelection();
        SetEditorNoteGroupScrollPosition(Math.Max(0,
            _chart.NoteGroups.OrderBy(group => group.Id).ToList().FindIndex(group => group.Id == groupId) - EditorNoteGroupVisibleRows + 1), immediate: true);
        if (selectedNote is not null)
        {
            _editorSelectedHoldEnd = false;
            _editorSelectedLinkRoot = GetEditorLinkRoot(selectedNote);
            _selectedEventKey = KeyOf(selectedNote);
            _selectedBeatTick = null;
            _selectedBeatTrack = null;
            _editorTrack = selectedNote.Track;
        }
        else
        {
            // Do not leave a previously selected note from another group as the
            // fallback selection while entering EDIT GROUP from the library.
            _selectedEventKey = null;
            _selectedBeatTick = null;
            _selectedBeatTrack = null;
            _editorSelectedLinkRoot = null;
            _editorSelectedHoldEnd = false;
        }
        _statusMessage = $"EDIT GROUP G{groupId} · INSTANCE {GetEditorNoteGroupDisplayInstanceNumber(groupId, instanceId)} active. Select individual notes; changes will be copied to the other instances.";
        return true;
    }

    private bool TryHandleEditorNoteGroupDoubleClick(Vector2 point, ChartEvent? clicked)
    {
        // Mouse clicks pass the same resolved target used by the visible hover
        // marker. This prevents a broad secondary hitbox from entering EDIT GROUP
        // for a note other than the one the user was shown.
        if (clicked?.NoteGroupId is not int groupId ||
            clicked.NoteGroupInstanceId is not int instanceId ||
            clicked.NoteGroupMemberId is not int memberId)
        {
            _editorLastGroupNoteClick = null;
            _editorLastGroupNoteClickTimestamp = 0;
            _editorLastGroupNoteClickPosition = Vector2.Zero;
            return false;
        }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var identity = (groupId, instanceId, memberId);
        var isDoubleClick = _editorLastGroupNoteClick == identity &&
            now >= _editorLastGroupNoteClickTimestamp &&
            now - _editorLastGroupNoteClickTimestamp <= System.Diagnostics.Stopwatch.Frequency / 2 &&
            Vector2.DistanceSquared(_editorLastGroupNoteClickPosition, point) <= 144f;
        if (isDoubleClick)
        {
            _editorLastGroupNoteClick = null;
            _editorLastGroupNoteClickTimestamp = 0;
            _editorLastGroupNoteClickPosition = Vector2.Zero;
            return EnterEditorNoteGroupEditMode(groupId, instanceId, clicked);
        }

        _editorLastGroupNoteClick = identity;
        _editorLastGroupNoteClickTimestamp = now;
        _editorLastGroupNoteClickPosition = point;
        return false;
    }

    private void HandleEditorNoteGroupUiAction(string action, int value)
    {
        switch (action)
        {
            case "select": SelectEditorNoteGroup(value); break;
            case "create": CreateEditorNoteGroupFromSelection(); break;
            case "ungroup": UngroupSelectedEditorNoteGroupInstance(); break;
            case "remove": RemoveSelectedEditorNoteGroup(); break;
            case "delete_all": DeleteAllEditorNoteGroups(); break;
            case "consolidate": ConsolidateEditorNoteGroups(); break;
            case "edit": ToggleEditorNoteGroupEditMode(); break;
        }
    }

    private const int EditorNoteGroupMenuVisibleRows = EditorNoteGroupVisibleRows;

    private Rectangle GetEditorNoteGroupEntryBounds(int row, int virtualWidth, int virtualHeight)
    {
        if (row is < 0 or > EditorNoteGroupMenuVisibleRows)
            return Rectangle.Empty;
        var panel = GetEditorMenuPanelBounds(virtualWidth, virtualHeight);
        if (panel.IsEmpty) return Rectangle.Empty;
        var unit = _projection.ScalePixels(1f);
        var padding = GetEditorMenuPanelPadding(unit);
        var header = GetEditorMenuHeaderBounds(virtualWidth, virtualHeight);
        var listY = header.Bottom + GetEditorMenuHeaderContentGap(unit);
        var rowHeight = Math.Max(1, (int)MathF.Round(50f * unit));
        var rowGap = Math.Max(1, (int)MathF.Round(5f * unit));
        var scrollbarWidth = Math.Max(1, (int)MathF.Round(18f * unit));
        var rowPitch = GetEditorNoteGroupScrollRowPitch();
        var scrollRemainder = GetEditorNoteGroupScrollPixelRemainder();
        var rowY = listY + (int)Math.Round(row * rowPitch - scrollRemainder);
        var listBottom = listY + EditorNoteGroupMenuVisibleRows * rowHeight +
            (EditorNoteGroupMenuVisibleRows - 1) * rowGap;
        var visibleTop = Math.Max(listY, rowY);
        var visibleBottom = Math.Min(listBottom, rowY + rowHeight);
        if (visibleBottom <= visibleTop)
            return Rectangle.Empty;
        return new Rectangle(panel.X + padding, visibleTop,
            Math.Max(1, panel.Width - padding * 2 - scrollbarWidth), visibleBottom - visibleTop);
    }

    private Rectangle GetEditorNoteGroupActionBounds(int row, int column, int columns, int virtualWidth, int virtualHeight)
    {
        if (row is < 0 or > 2 || column < 0 || column >= columns || columns <= 0)
            return Rectangle.Empty;
        var panel = GetEditorMenuPanelBounds(virtualWidth, virtualHeight);
        if (panel.IsEmpty) return Rectangle.Empty;
        var unit = _projection.ScalePixels(1f);
        var padding = GetEditorMenuPanelPadding(unit);
        var gap = Math.Max(1, (int)MathF.Round(5f * unit));
        var buttonHeight = Math.Max(1, (int)MathF.Round(38f * unit));
        var contentWidth = panel.Width - padding * 2;
        var buttonWidth = Math.Max(1, (contentWidth - gap * (columns - 1)) / columns);
        var footerY = panel.Bottom - padding - buttonHeight * 3 - gap * 2;
        var x = panel.X + padding + column * (buttonWidth + gap);
        var y = footerY + row * (buttonHeight + gap);
        if (column == columns - 1)
            buttonWidth = Math.Max(1, panel.Right - padding - x);
        return new Rectangle(x, y, buttonWidth, buttonHeight);
    }

    private bool TryHandleEditorNoteGroupsMenuClick(Microsoft.Xna.Framework.Point pixel, int virtualWidth, int virtualHeight)
    {
        if (_editorMenuSection != EditorMenuSection.NoteGroups || _chart is null)
            return false;

        for (var row = 0; row <= EditorNoteGroupMenuVisibleRows; row++)
        {
            if (!GetEditorNoteGroupEntryBounds(row, virtualWidth, virtualHeight).Contains(pixel))
                continue;
            var visibleGroups = _chart.NoteGroups.OrderBy(group => group.Id)
                .Skip(_editorNoteGroupsScrollOffset).Take(EditorNoteGroupMenuVisibleRows + 1).ToList();
            if (row < visibleGroups.Count)
                SelectEditorNoteGroup(visibleGroups[row].Id);
            return true;
        }

        var actionRows = new (int Row, int Column, int Columns, string Action)[]
        {
            (0, 0, 3, "create"), (0, 1, 3, "edit"), (0, 2, 3, "ungroup"),
            (1, 0, 2, "remove"), (1, 1, 2, "delete_all"),
            (2, 0, 1, "consolidate")
        };
        foreach (var entry in actionRows)
        {
            if (GetEditorNoteGroupActionBounds(entry.Row, entry.Column, entry.Columns, virtualWidth, virtualHeight).Contains(pixel))
            {
                HandleEditorNoteGroupUiAction(entry.Action, 0);
                return true;
            }
        }
        return false;
    }

    private void NormalizeEditorNoteGroupInstanceIds(int groupId)
    {
        if (_chart is null)
            return;

        var ordered = _chart.NoteGroupInstances.Where(instance => instance.GroupId == groupId)
            .OrderBy(instance => instance.StartTick).ThenBy(instance => instance.StartTrack)
            .ThenBy(instance => instance.InstanceId).ToList();
        var oldEditingId = _editorEditingNoteGroupId == groupId ? _editorEditingNoteGroupInstanceId : null;
        var normalizedEditingId = (int?)null;

        // Stage IDs through unique negative values first. Directly swapping IDs
        // can merge two instances when their old IDs overlap the new sequence.
        for (var index = 0; index < ordered.Count; index++)
        {
            var instance = ordered[index];
            if (oldEditingId == instance.InstanceId)
                normalizedEditingId = index + 1;
            foreach (var ev in _chart.Events.Where(ev => ev.NoteGroupId == groupId &&
                         ev.NoteGroupInstanceId == instance.InstanceId))
                ev.NoteGroupInstanceId = -(index + 1);
        }

        for (var index = 0; index < ordered.Count; index++)
        {
            var nextId = index + 1;
            foreach (var ev in _chart.Events.Where(ev => ev.NoteGroupId == groupId &&
                         ev.NoteGroupInstanceId == -(index + 1)))
                ev.NoteGroupInstanceId = nextId;
            ordered[index] = ordered[index] with { InstanceId = nextId };
        }

        _chart.NoteGroupInstances.RemoveAll(instance => instance.GroupId == groupId);
        _chart.NoteGroupInstances.AddRange(ordered);
        if (_editorEditingNoteGroupId == groupId)
            _editorEditingNoteGroupInstanceId = normalizedEditingId;
    }

    private void PruneEmptyEditorNoteGroupInstances(ISet<int>? preserveGroupIds = null)
    {
        if (_chart is null)
            return;

        var removedGroups = new HashSet<int>();
        foreach (var instance in _chart.NoteGroupInstances.ToList())
        {
            if (preserveGroupIds?.Contains(instance.GroupId) == true)
                continue;

            var hasNotes = _chart.Events.Any(ev => ev.Kind == "Note" && ev.RawType == 1 &&
                ev.NoteGroupId == instance.GroupId && ev.NoteGroupInstanceId == instance.InstanceId);
            if (hasNotes) continue;
            _chart.NoteGroupInstances.Remove(instance);
            removedGroups.Add(instance.GroupId);
        }

        foreach (var groupId in _chart.NoteGroups.Select(group => group.Id).ToList())
            if (removedGroups.Contains(groupId) || _chart.NoteGroupInstances.Any(instance => instance.GroupId == groupId))
                NormalizeEditorNoteGroupInstanceIds(groupId);

        if (_editorNoteGroupEditMode && _editorEditingNoteGroupId is int editingGroup &&
            !_chart.NoteGroupInstances.Any(instance => instance.GroupId == editingGroup &&
                instance.InstanceId == _editorEditingNoteGroupInstanceId))
        {
            _editorNoteGroupEditMode = false;
            _editorEditingNoteGroupId = null;
            _editorEditingNoteGroupInstanceId = null;
            _statusMessage = "Exited EDIT GROUP because its active instance no longer exists.";
        }
    }

    private void ExpandEditorNoteGroupSelection(HashSet<ChartEvent> selected)
    {
        if (_chart is null || selected.Count == 0)
            return;

        var groupKeys = _editorNoteGroupExpansionKeysScratch;
        groupKeys.Clear();
        foreach (var note in selected)
        {
            if (note.NoteGroupId is not int groupId ||
                note.NoteGroupInstanceId is not int instanceId ||
                (_editorNoteGroupEditMode && _editorEditingNoteGroupId == groupId))
                continue;
            groupKeys.Add((groupId, instanceId));
        }

        if (groupKeys.Count == 0)
            return;

        EnsureEditorNoteGroupMembersCache();
        var liveSelection = ReferenceEquals(selected, _editorMultiSelectedEvents);
        foreach (var key in groupKeys)
        {
            if (!_editorNoteGroupMembersByInstance.TryGetValue(key, out var members))
                continue;
            foreach (var member in members)
            {
                if (liveSelection)
                    AddEditorMultiSelectedEvent(member);
                else
                    selected.Add(member);
            }
        }
    }

    private int GetEditorNoteGroupDisplayInstanceNumber(int groupId, int instanceId)
    {
        if (_chart is null)
            return instanceId;
        var ordered = _chart.NoteGroupInstances.Where(instance => instance.GroupId == groupId)
            .OrderBy(instance => instance.StartTick).ThenBy(instance => instance.StartTrack)
            .ThenBy(instance => instance.InstanceId).ToList();
        var index = ordered.FindIndex(instance => instance.InstanceId == instanceId);
        return index >= 0 ? index + 1 : instanceId;
    }

    private void SetEditorNoteGroupEditingInstance(ChartEvent ev)
    {
        if (_editorNoteGroupEditMode && ev.NoteGroupId == _editorEditingNoteGroupId && ev.NoteGroupInstanceId.HasValue)
            _editorEditingNoteGroupInstanceId = ev.NoteGroupInstanceId;
    }

    private void ApplyEditorNoteGroupSelectionForAnchor(ChartEvent anchor)
    {
        if (_chart is null || anchor.NoteGroupId is not int groupId || anchor.NoteGroupInstanceId is not int instanceId)
        {
            if (_editorNoteGroupEditMode && anchor.NoteGroupId != _editorEditingNoteGroupId)
            {
                _editorNoteGroupEditMode = false;
                _editorEditingNoteGroupId = null;
                _editorEditingNoteGroupInstanceId = null;
                _statusMessage = "Exited EDIT GROUP because a note outside the active group was selected.";
            }
            ClearEditorNoteGroupLibrarySelection();
            return;
        }

        // Selecting a placed instance from the chart also selects its library
        // entry. This keeps the success state and the UNGROUP action tied to the
        // instance the user is actually working with.
        _editorSelectedNoteGroupId = groupId;
        _editorNoteGroupLibrarySelectionExplicit = false;
        _editorNoteGroupPlacementArmed = false;
        InvalidateEditorNoteGroupPlacementPreviewCache();
        var orderedGroups = _chart.NoteGroups.OrderBy(group => group.Id).ToList();
        var groupIndex = orderedGroups.FindIndex(group => group.Id == groupId);
        if (groupIndex >= 0 && (groupIndex < _editorNoteGroupsScrollOffset ||
                                groupIndex >= _editorNoteGroupsScrollOffset + EditorNoteGroupVisibleRows))
            SetEditorNoteGroupScrollPosition(Math.Clamp(groupIndex - EditorNoteGroupVisibleRows + 1,
                0, Math.Max(0, orderedGroups.Count - EditorNoteGroupVisibleRows)), immediate: true);

        if (_editorNoteGroupEditMode)
        {
            if (_editorEditingNoteGroupId == groupId)
            {
                _editorEditingNoteGroupInstanceId = instanceId;
                return;
            }

            _editorNoteGroupEditMode = false;
            _editorEditingNoteGroupId = null;
            _editorEditingNoteGroupInstanceId = null;
            _statusMessage = "Exited EDIT GROUP because a note from another group was selected.";
        }

        // A normal click on a grouped note selects exactly its complete instance.
        // Replace any previous/mixed selection instead of adding to it: otherwise
        // the library can highlight this group while UNGROUP still sees notes from
        // an older instance and rejects the operation intermittently.
        ClearEditorMultiSelection();
        foreach (var member in _chart.Events.Where(candidate => candidate.Kind == "Note" && candidate.RawType == 1 &&
                     candidate.NoteGroupId == groupId && candidate.NoteGroupInstanceId == instanceId))
            AddEditorMultiSelectedEvent(member);
    }

    private void ApplyEditorNoteGroupSelectionLockToCurrentSelection()
    {
        if (_chart is null)
            return;
        var selected = _editorMultiSelectedEvents.Where(IsEditorEventInCurrentChart).ToHashSet();
        if (_selectedEventKey.HasValue &&
            _eventsByKey.TryGetValue(_selectedEventKey.Value, out var anchor) &&
            anchor.Kind == "Note" && anchor.RawType == 1)
            selected.Add(anchor);
        ExpandEditorNoteGroupSelection(selected);
        ClearEditorMultiSelection();
        foreach (var note in selected)
            AddEditorMultiSelectedEvent(note);
    }

    private void RefreshEditorNoteGroupInstanceAnchors(EditorChartSnapshot snapshot)
    {
        if (_chart is null || _editorNoteGroupEditMode)
            return;

        // Editor mutations are applied to ChartTrack.Events first. Refresh the
        // flattened lookup before resolving note identities or instance anchors.
        AresLoader.FinalizeEvents(_chart);
        var prior = snapshot.Events.Where(state => state.NoteGroupId.HasValue &&
                state.NoteGroupInstanceId.HasValue && state.NoteGroupMemberId.HasValue)
            .GroupBy(state => (GroupId: state.NoteGroupId!.Value, InstanceId: state.NoteGroupInstanceId!.Value));
        foreach (var group in prior)
        {
            var instance = _chart.NoteGroupInstances.FirstOrDefault(candidate =>
                candidate.GroupId == group.Key.GroupId && candidate.InstanceId == group.Key.InstanceId);
            if (instance is null)
                continue;

            var priorInstance = snapshot.NoteGroupInstances.FirstOrDefault(candidate =>
                candidate.GroupId == group.Key.GroupId && candidate.InstanceId == group.Key.InstanceId) ?? instance;
            var currentByMember = _chart.Events.Where(ev => ev.NoteGroupId == group.Key.GroupId &&
                    ev.NoteGroupInstanceId == group.Key.InstanceId && ev.NoteGroupMemberId.HasValue)
                .GroupBy(ev => ev.NoteGroupMemberId!.Value)
                .ToDictionary(items => items.Key, items => items.First());
            var deltas = group.Select(state => currentByMember.TryGetValue(state.NoteGroupMemberId!.Value, out var current)
                    ? (Tick: current.Tick - state.Tick,
                        Track: GetEditorNoteGroupInstanceTrackDelta(instance, priorInstance, current.Track, state.Track))
                    : ((int Tick, int Track)?)null)
                .Where(delta => delta.HasValue).Select(delta => delta!.Value).Distinct().ToList();
            if (deltas.Count != 1 || deltas[0] == (0, 0))
                continue;

            var nextTick = (long)instance.StartTick + deltas[0].Tick;
            if (nextTick < 0 || nextTick > int.MaxValue)
                continue;

            NoteGroupInstance updated;
            if (HasEditorNoteGroupTrackMap(instance, priorInstance))
            {
                var shiftedMap = (instance.TrackMap ?? priorInstance.TrackMap ?? new List<NoteGroupTrackMapping>())
                    .Select(mapping => mapping with { Track = mapping.Track + deltas[0].Track }).ToList();
                if (shiftedMap.Any(mapping => mapping.Track is < 0 or > 15))
                    continue;
                updated = instance with { StartTick = (int)nextTick, TrackMap = shiftedMap };
            }
            else
            {
                var nextTrack = instance.StartTrack + deltas[0].Track;
                if (nextTrack is < 0 or > 15)
                    continue;
                updated = instance with { StartTick = (int)nextTick, StartTrack = nextTrack };
            }

            var index = _chart.NoteGroupInstances.IndexOf(instance);
            if (index >= 0)
                _chart.NoteGroupInstances[index] = updated;
        }
    }

    private NoteGroupInstance RefreshEditorNoteGroupInstanceAnchorFromSnapshot(
        EditorChartSnapshot snapshot, int groupId, NoteGroupInstance sourceInstance)
    {
        if (_chart is null)
            return sourceInstance;

        var priorStates = snapshot.Events.Where(state => state.NoteGroupId == groupId &&
            state.NoteGroupInstanceId == sourceInstance.InstanceId && state.NoteGroupMemberId.HasValue).ToList();
        if (priorStates.Count == 0)
            return sourceInstance;
        var priorInstance = snapshot.NoteGroupInstances.FirstOrDefault(candidate =>
            candidate.GroupId == groupId && candidate.InstanceId == sourceInstance.InstanceId) ?? sourceInstance;

        // Match by the persistent logical member ID, not the ChartEvent object.
        // Mapped instances shift their physical lane map when the entire copy is
        // dragged; ordinary instances continue to shift their canonical anchor.
        var currentByMember = _chart.Events.Where(ev => ev.NoteGroupId == groupId &&
                ev.NoteGroupInstanceId == sourceInstance.InstanceId && ev.NoteGroupMemberId.HasValue)
            .GroupBy(ev => ev.NoteGroupMemberId!.Value)
            .ToDictionary(items => items.Key, items => items.First());
        var deltas = priorStates.Select(state => currentByMember.TryGetValue(state.NoteGroupMemberId!.Value, out var current)
                ? (Tick: current.Tick - state.Tick,
                    Track: GetEditorNoteGroupInstanceTrackDelta(sourceInstance, priorInstance, current.Track, state.Track))
                : ((int Tick, int Track)?)null)
            .Where(delta => delta.HasValue).Select(delta => delta!.Value).Distinct().ToList();
        if (deltas.Count != 1 || deltas[0] == (0, 0))
            return sourceInstance;

        var nextTick = (long)sourceInstance.StartTick + deltas[0].Tick;
        if (nextTick < 0 || nextTick > int.MaxValue)
            return sourceInstance;

        NoteGroupInstance updated;
        if (HasEditorNoteGroupTrackMap(sourceInstance, priorInstance))
        {
            var shiftedMap = (sourceInstance.TrackMap ?? priorInstance.TrackMap ?? new List<NoteGroupTrackMapping>())
                .Select(mapping => mapping with { Track = mapping.Track + deltas[0].Track }).ToList();
            if (shiftedMap.Any(mapping => mapping.Track is < 0 or > 15))
                return sourceInstance;
            updated = sourceInstance with { StartTick = (int)nextTick, TrackMap = shiftedMap };
        }
        else
        {
            var nextTrack = sourceInstance.StartTrack + deltas[0].Track;
            if (nextTrack is < 0 or > 15)
                return sourceInstance;
            updated = sourceInstance with { StartTick = (int)nextTick, StartTrack = nextTrack };
        }

        var index = _chart.NoteGroupInstances.IndexOf(sourceInstance);
        if (index >= 0)
            _chart.NoteGroupInstances[index] = updated;
        return updated;
    }

    private static bool HasEditorNoteGroupTrackMap(NoteGroupInstance current, NoteGroupInstance prior)
        => (current.TrackMap?.Count ?? 0) > 0 || (prior.TrackMap?.Count ?? 0) > 0;

    private static int GetEditorNoteGroupInstanceTrackDelta(
        NoteGroupInstance currentInstance, NoteGroupInstance priorInstance, int currentPhysicalTrack, int priorPhysicalTrack)
    {
        if (HasEditorNoteGroupTrackMap(currentInstance, priorInstance))
        {
            var currentUnmirrored = currentInstance.IsMirrored
                ? MirrorEditorTrackX(currentPhysicalTrack) : currentPhysicalTrack;
            var priorUnmirrored = priorInstance.IsMirrored
                ? MirrorEditorTrackX(priorPhysicalTrack) : priorPhysicalTrack;
            return currentUnmirrored - priorUnmirrored;
        }

        return GetCanonicalEditorNoteGroupTrack(currentInstance, currentPhysicalTrack) -
               GetCanonicalEditorNoteGroupTrack(priorInstance, priorPhysicalTrack);
    }

    private void RefreshEditorNoteGroupEventGraph()
    {
        if (_chart is null)
            return;
        AresLoader.FinalizeEvents(_chart);
        LinkBuilder.Build(_chart);
        RebuildLinkRootMap();
        RebuildLinkAdjacencyCache();
        InvalidateEditorSelectionRenderCache();
        InvalidateEditorNoteGroupMarkerCache();
    }

    // Build link paths from one logical group instance only. LinkBuilder normally
    // groups the whole chart by (lane family, LinkPair), but physical pair IDs are
    // allowed to differ between copies and may be reused by non-overlapping chains.
    // Keeping the graph local prevents another instance from becoming an accidental
    // predecessor/successor while group synchronization is in progress.
    private List<EditorLinkChainInfo> GetEditorNoteGroupLinkChainInfos(int groupId, int instanceId)
    {
        var result = new List<EditorLinkChainInfo>();
        if (_chart is null)
            return result;

        var isolatedChart = new AresChart();
        foreach (var sourceTrack in _chart.Tracks)
        {
            var linkEvents = sourceTrack.Events.Where(ev => ev.Kind == "Note" && ev.RawType == 1 &&
                    IsPlayableEditorTrack(ev.Track) && ev.NoteGroupId == groupId &&
                    ev.NoteGroupInstanceId == instanceId &&
                    ev.NoteType is NoteType.LinkHead or NoteType.LinkTail && ev.LinePair >= 0)
                .ToList();
            if (linkEvents.Count == 0)
                continue;

            var isolatedTrack = new ChartTrack { TrackIndex = sourceTrack.TrackIndex };
            isolatedTrack.Events.AddRange(linkEvents);
            isolatedChart.Tracks.Add(isolatedTrack);
        }

        if (isolatedChart.Tracks.Count == 0)
            return result;

        AresLoader.FinalizeEvents(isolatedChart);
        LinkBuilder.Build(isolatedChart);

        foreach (var root in isolatedChart.Events.Where(ev => ev.NoteType == NoteType.LinkHead)
                     .OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track).ThenBy(ev => ev.EventIndex))
        {
            var nodes = new List<ChartEvent> { root };
            var visited = new HashSet<ChartEvent> { root };
            var current = root;
            for (var guard = 0; guard < 256; guard++)
            {
                var outgoing = isolatedChart.Links.Where(link => ReferenceEquals(link.Head, current)).ToList();
                if (outgoing.Count == 0)
                    break;

                var nextTick = outgoing.Min(link => link.Tail.Tick);
                var candidates = outgoing.Where(link => link.Tail.Tick == nextTick).ToList();
                if (candidates.Count != 1)
                    break;

                var next = candidates[0].Tail;
                if (!visited.Add(next))
                    break;
                nodes.Add(next);
                current = next;
            }

            result.Add(new EditorLinkChainInfo(root, nodes, LinkBuilder.Family(root.Track),
                root.Tick, nodes.Max(node => (long)node.EndTick), root.LinePair));
        }

        return result;
    }

    private void PreserveEditorNoteGroupIdentityForReplacements(EditorChartSnapshot snapshot,
        int groupId, int sourceInstanceId)
    {
        if (_chart is null)
            return;

        var snapshotEvents = snapshot.Events.Select(state => state.Event).ToHashSet();
        var liveEvents = _chart.Tracks.SelectMany(track => track.Events).Distinct().ToList();
        foreach (var state in snapshot.Events.Where(state => state.NoteGroupId == groupId &&
                     state.NoteGroupInstanceId == sourceInstanceId && state.NoteGroupMemberId.HasValue))
        {
            if (liveEvents.Any(ev => ReferenceEquals(ev, state.Event)))
                continue;

            // Replacing a note at the same cell should retain its member identity.
            // Without this, the old instance is interpreted as a deletion and the
            // replacement as an unrelated addition, breaking cross-instance edits.
            var replacements = liveEvents.Where(ev => !snapshotEvents.Contains(ev) &&
                ev.Kind == "Note" && ev.RawType == 1 && ev.Tick == state.Tick && ev.Track == state.Track &&
                ev.NoteGroupId is null && ev.NoteGroupInstanceId is null && ev.NoteGroupMemberId is null).ToList();
            if (replacements.Count != 1)
                continue;

            replacements[0].NoteGroupId = groupId;
            replacements[0].NoteGroupInstanceId = sourceInstanceId;
            replacements[0].NoteGroupMemberId = state.NoteGroupMemberId;
        }
    }

    private List<ChartEvent> GetUnassignedEditorNoteGroupMembers(
        EditorChartSnapshot snapshot, int groupId, int sourceInstanceId)
    {
        if (_chart is null)
            return new List<ChartEvent>();

        var snapshotEvents = snapshot.Events.Select(state => state.Event).ToHashSet();
        var snapshotMembers = snapshot.Events.Where(state => state.NoteGroupId == groupId &&
                state.NoteGroupInstanceId == sourceInstanceId && state.NoteGroupMemberId.HasValue)
            .Select(state => state.Event).ToHashSet();
        // Existing connector targets are explicitly adopted into the edited
        // instance by AddEditorConnector before this scan. Do not rediscover them
        // through GetEditorLinkRoot here: that resolver can see the chart-wide
        // graph while sibling copies still carry the previous pair assignments.
        // New points are either already tagged for this instance or truly new
        // ungrouped events created by the current edit.
        return _chart.Events.Where(ev => ev.Kind == "Note" && ev.RawType == 1 &&
                ev.Track is >= 0 and <= 15 &&
                ((ev.NoteGroupId == groupId && ev.NoteGroupInstanceId == sourceInstanceId &&
                  !ev.NoteGroupMemberId.HasValue && !snapshotMembers.Contains(ev)) ||
                 (!snapshotEvents.Contains(ev) && ev.NoteGroupId is null &&
                  ev.NoteGroupInstanceId is null && ev.NoteGroupMemberId is null)))
            .Distinct().ToList();
    }

    private bool SynchronizeEditorNoteGroupChanges(EditorChartSnapshot snapshot)
    {
        if (_chart is null || !_editorNoteGroupEditMode || _editorSynchronizingNoteGroups ||
            _editorEditingNoteGroupId is not int groupId || _editorEditingNoteGroupInstanceId is not int sourceInstanceId)
            return true;

        PreserveEditorNoteGroupIdentityForReplacements(snapshot, groupId, sourceInstanceId);
        RefreshEditorNoteGroupEventGraph();

        var group = _chart.NoteGroups.FirstOrDefault(candidate => candidate.Id == groupId);
        var sourceInstance = _chart.NoteGroupInstances.FirstOrDefault(instance =>
            instance.GroupId == groupId && instance.InstanceId == sourceInstanceId);
        if (group is null || sourceInstance is null)
            return true;

        var sourceStates = snapshot.Events.Where(state => state.NoteGroupId == groupId &&
            state.NoteGroupInstanceId == sourceInstanceId && state.NoteGroupMemberId.HasValue).ToList();
        if (sourceStates.GroupBy(state => state.NoteGroupMemberId!.Value).Any(members => members.Count() != 1))
        {
            RestoreEditorChartSnapshot(snapshot);
            _statusMessage = $"EDIT GROUP cancelled: Group {groupId} contains duplicate member identities in its edited instance.";
            return false;
        }

        var currentSourceGroups = _chart.Events.Where(ev => ev.NoteGroupId == groupId &&
                ev.NoteGroupInstanceId == sourceInstanceId && ev.NoteGroupMemberId.HasValue)
            .GroupBy(ev => ev.NoteGroupMemberId!.Value).ToList();
        if (currentSourceGroups.Any(members => members.Count() != 1))
        {
            RestoreEditorChartSnapshot(snapshot);
            _statusMessage = $"EDIT GROUP cancelled: Group {groupId} has duplicate live notes for a member in the edited instance.";
            return false;
        }
        var currentSourceByMember = currentSourceGroups.ToDictionary(members => members.Key, members => members.Single());

        var targetInstances = _chart.NoteGroupInstances.Where(instance => instance.GroupId == groupId &&
            instance.InstanceId != sourceInstanceId).ToList();

        // Validate every destination as one batch before modifying any counterpart.
        // Checking notes one at a time would incorrectly reject a group move where
        // one note temporarily occupies another member's old cell while that member
        // is moving away in the same transaction.
        var newNotesForValidation = GetUnassignedEditorNoteGroupMembers(snapshot, groupId, sourceInstanceId);

        foreach (var targetInstance in targetInstances)
        {
            var plannedPositions = new List<(int MemberId, long Tick, int Track)>();
            foreach (var state in sourceStates)
            {
                var memberId = state.NoteGroupMemberId!.Value;
                currentSourceByMember.TryGetValue(memberId, out var current);
                if (current is null)
                    continue;

                var matches = _chart.Events.Where(candidate => candidate.NoteGroupId == groupId &&
                    candidate.NoteGroupMemberId == memberId && candidate.NoteGroupInstanceId == targetInstance.InstanceId).ToList();
                if (matches.Count > 1)
                {
                    RestoreEditorChartSnapshot(snapshot);
                    _statusMessage = $"EDIT GROUP cancelled: Group {groupId}, instance {targetInstance.InstanceId} has duplicate copies of member {memberId}.";
                    return false;
                }

                // Reconcile the destination to the source's absolute relative position
                // every time. Applying only a source-side delta leaves an already stale
                // sibling untouched whenever the source member itself did not change.
                var targetTick = (long)targetInstance.StartTick + current.Tick - sourceInstance.StartTick;
                var targetTrack = GetEditorNoteGroupCounterpartTrack(sourceInstance, targetInstance, current.Track);
                if (targetTick < 0 || targetTick > int.MaxValue || targetTrack is < 0 or > 15)
                {
                    RestoreEditorChartSnapshot(snapshot);
                    _statusMessage = $"EDIT GROUP cancelled: a matching note in Group {groupId} would leave the chart lanes or tick range.";
                    return false;
                }
                plannedPositions.Add((memberId, targetTick, targetTrack));
            }

            foreach (var newNote in newNotesForValidation)
            {
                var tick = (long)targetInstance.StartTick + newNote.Tick - sourceInstance.StartTick;
                var track = GetEditorNoteGroupCounterpartTrack(sourceInstance, targetInstance, newNote.Track);
                if (tick < 0 || tick > int.MaxValue || track is < 0 or > 15)
                {
                    RestoreEditorChartSnapshot(snapshot);
                    _statusMessage = $"EDIT GROUP cancelled: a new note cannot be placed inside every instance of Group {groupId}.";
                    return false;
                }
                plannedPositions.Add((0, tick, track));
            }

            var duplicatePosition = plannedPositions.GroupBy(position => (position.Tick, position.Track))
                .FirstOrDefault(positions => positions.Count() > 1);
            if (duplicatePosition is not null)
            {
                RestoreEditorChartSnapshot(snapshot);
                _statusMessage = $"EDIT GROUP cancelled: multiple members would overlap in instance {GetEditorNoteGroupDisplayInstanceNumber(groupId, targetInstance.InstanceId)}.";
                return false;
            }

            var plannedCells = plannedPositions.Select(position => (position.Tick, position.Track)).ToHashSet();
            var occupied = _chart.Events.FirstOrDefault(candidate => candidate.Kind == "Note" &&
                candidate.RawType == 1 && plannedCells.Contains(( (long)candidate.Tick, candidate.Track )) &&
                !(candidate.NoteGroupId == groupId && candidate.NoteGroupInstanceId == targetInstance.InstanceId));
            if (occupied is not null)
            {
                var displayInstance = GetEditorNoteGroupDisplayInstanceNumber(groupId, targetInstance.InstanceId);
                var conflictingMember = plannedPositions.FirstOrDefault(position =>
                    position.Tick == occupied.Tick && position.Track == occupied.Track);
                RestoreEditorChartSnapshot(snapshot);
                _statusMessage = $"EDIT GROUP cancelled: instance {displayInstance} already has a note at tick {occupied.Tick}, track {occupied.Track}; member {conflictingMember.MemberId} cannot be synchronized there.";
                return false;
            }
        }

        _editorSynchronizingNoteGroups = true;
        try
        {
            var pairMaps = new Dictionary<int, Dictionary<int, EditorNoteGroupLinkPairReservation>>();
            var deletedMemberIds = new HashSet<int>();

            // Adopt connector targets and other newly linked points before applying
            // existing members. The graph was refreshed once before entering this
            // transaction; do not rebuild it while sibling instances still contain
            // a mixture of old and new chain data.
            var newNotes = GetUnassignedEditorNoteGroupMembers(snapshot, groupId, sourceInstanceId);
            if (newNotes.Count > 0 && !AddEditorNoteGroupMembersToInstances(group, sourceInstance, newNotes, pairMaps,
                    out var addError))
            {
                RestoreEditorChartSnapshot(snapshot);
                _statusMessage = $"EDIT GROUP cancelled: {addError}";
                return false;
            }

            foreach (var state in sourceStates)
            {
                var memberId = state.NoteGroupMemberId!.Value;
                currentSourceByMember.TryGetValue(memberId, out var current);

                if (current is null)
                {
                    foreach (var targetInstance in targetInstances)
                    {
                        var removals = _chart.Events.Where(candidate => candidate.NoteGroupId == groupId &&
                            candidate.NoteGroupMemberId == memberId && candidate.NoteGroupInstanceId == targetInstance.InstanceId).ToHashSet();
                        if (removals.Count == 0)
                            continue;
                        // The edited source instance has already performed the link-run
                        // normalization for this deletion. Remove only the matching logical
                        // member here; transforming the target chain independently can make
                        // its intermediate graph differ while the remaining members sync.
                        foreach (var track in _chart.Tracks)
                            track.Events.RemoveAll(removals.Contains);
                    }
                    deletedMemberIds.Add(memberId);
                    continue;
                }

                foreach (var targetInstance in targetInstances)
                {
                    var target = _chart.Events.FirstOrDefault(candidate => candidate.NoteGroupId == groupId &&
                        candidate.NoteGroupMemberId == memberId && candidate.NoteGroupInstanceId == targetInstance.InstanceId);
                    if (target is null)
                    {
                        target = CreateEditorNoteGroupCounterpart(group, current, sourceInstance, targetInstance,
                            pairMaps, remappingWholeGroup: true);
                        if (target is null)
                        {
                            RestoreEditorChartSnapshot(snapshot);
                            _statusMessage = $"EDIT GROUP cancelled: could not recreate member {memberId} in instance {targetInstance.InstanceId}.";
                            return false;
                        }
                        continue;
                    }

                    if (!ApplyEditorNoteGroupMemberChange(current, target, sourceInstance, pairMaps,
                            out var syncError, remappingWholeGroup: true))
                    {
                        RestoreEditorChartSnapshot(snapshot);
                        _statusMessage = $"EDIT GROUP cancelled: {syncError}";
                        return false;
                    }
                }
                // Keep the target link graph untouched until the whole logical chain
                // has been copied. Rebuilding after each member can expose a temporary
                // split pair and make later members resolve against a partial chain.
            }

            // Deletions are mirrored only after the additions have stable member IDs.
            // This keeps mixed chain edits atomic and lets the full-chain pass below
            // rebuild every surviving path from one complete logical member set.
            group.Members.RemoveAll(member => deletedMemberIds.Contains(member.Id));

            // Reassign pair IDs and copy every point in each logical chain as one
            // operation. Do not rebuild LinkBuilder between member copying and this
            // pass: the sibling instances are intentionally in a mixed intermediate
            // state until each complete logical chain is applied.
            if (!SynchronizeEditorNoteGroupLinkChains(groupId, sourceInstance, targetInstances, out var chainSyncError))
            {
                RestoreEditorChartSnapshot(snapshot);
                _statusMessage = $"EDIT GROUP cancelled: {chainSyncError}";
                return false;
            }
            RefreshEditorNoteGroupEventGraph();

            // Recompute anchors for every copy. Member IDs let this also work when
            // an edit replaced one of the underlying ChartEvent objects.
            foreach (var instance in _chart.NoteGroupInstances
                         .Where(candidate => candidate.GroupId == groupId).ToList())
            {
                var currentInstance = _chart.NoteGroupInstances.FirstOrDefault(candidate =>
                    candidate.GroupId == groupId && candidate.InstanceId == instance.InstanceId);
                if (currentInstance is not null)
                    RefreshEditorNoteGroupInstanceAnchorFromSnapshot(snapshot, groupId, currentInstance);
            }

            sourceInstance = _chart.NoteGroupInstances.FirstOrDefault(instance =>
                instance.GroupId == groupId && instance.InstanceId == sourceInstanceId) ?? sourceInstance;
            UpdateEditorNoteGroupDefinitionFromInstance(group, sourceInstance);
            RefreshEditorNoteGroupEventGraph();
            if (!ValidateEditorNoteGroupLinkSynchronization(groupId, sourceInstanceId, out var validationError))
            {
                RestoreEditorChartSnapshot(snapshot);
                _statusMessage = $"EDIT GROUP cancelled: {validationError}";
                return false;
            }
        }
        finally
        {
            _editorSynchronizingNoteGroups = false;
        }
        return true;
    }

    private bool SynchronizeEditorNoteGroupLinkChains(int groupId, NoteGroupInstance sourceInstance,
        IReadOnlyList<NoteGroupInstance> targetInstances, out string error)
    {
        error = string.Empty;
        if (_chart is null)
            return false;

        var allInstances = targetInstances.Append(sourceInstance)
            .DistinctBy(instance => instance.InstanceId)
            .OrderBy(instance => instance.InstanceId)
            .ToList();
        var sourceChains = GetEditorNoteGroupLinkChainInfos(groupId, sourceInstance.InstanceId)
            .OrderBy(chain => chain.StartTick)
            .ThenBy(chain => chain.Root.NoteGroupMemberId)
            .ToList();

        // Every link point in every copy being reconciled is excluded from the
        // occupancy scan. Pair IDs are then reserved for all instances together,
        // including the edited source instance, instead of trusting its old ID.
        var targetChainEvents = _chart.Events.Where(ev => ev.NoteGroupId == groupId &&
                allInstances.Any(instance => instance.InstanceId == ev.NoteGroupInstanceId) &&
                ev.NoteType is NoteType.LinkHead or NoteType.LinkTail)
            .ToHashSet();
        var plans = new List<(NoteGroupInstance Instance, EditorLinkChainInfo Chain,
            List<(ChartEvent Source, ChartEvent Target)> Nodes, string Family,
            int StartTick, int EndTick, bool HasTailAtStart, int? TerminalTailTick,
            List<int> PreferredPairs)>();

        foreach (var chain in sourceChains)
        {
            if (chain.Root.NoteGroupMemberId is not int rootMemberId || chain.Nodes.Count < 2 || chain.Pair < 0)
            {
                error = $"group {groupId} contains a link chain without a valid logical root or pair ID";
                return false;
            }

            if (chain.Nodes.Any(node => node.NoteGroupId != groupId ||
                                       node.NoteGroupInstanceId != sourceInstance.InstanceId ||
                                       !node.NoteGroupMemberId.HasValue))
            {
                error = $"group {groupId}, chain member {rootMemberId} crosses group-instance boundaries";
                return false;
            }

            var duplicateSourceIds = chain.Nodes.GroupBy(node => node.NoteGroupMemberId!.Value)
                .FirstOrDefault(nodes => nodes.Count() != 1);
            if (duplicateSourceIds is not null)
            {
                error = $"group {groupId}, chain member {rootMemberId} contains duplicate logical member IDs";
                return false;
            }

            foreach (var instance in allInstances)
            {
                var mappedNodes = new List<(ChartEvent Source, ChartEvent Target)>(chain.Nodes.Count);
                foreach (var sourceNode in chain.Nodes)
                {
                    ChartEvent target;
                    if (instance.InstanceId == sourceInstance.InstanceId)
                    {
                        target = sourceNode;
                    }
                    else
                    {
                        var matches = _chart.Events.Where(candidate => candidate.NoteGroupId == groupId &&
                                candidate.NoteGroupInstanceId == instance.InstanceId &&
                                candidate.NoteGroupMemberId == sourceNode.NoteGroupMemberId)
                            .ToList();
                        if (matches.Count != 1)
                        {
                            error = $"group {groupId}, instance {instance.InstanceId} is missing or duplicates link member {sourceNode.NoteGroupMemberId}";
                            return false;
                        }
                        target = matches[0];
                    }

                    mappedNodes.Add((sourceNode, target));
                }

                var mappedFamilies = mappedNodes.Select(node => LinkBuilder.Family(
                        GetEditorNoteGroupCounterpartTrack(sourceInstance, instance, node.Source.Track)))
                    .Distinct(StringComparer.Ordinal).ToList();
                if (mappedFamilies.Count != 1 || mappedFamilies[0] is null)
                {
                    error = $"group {groupId}, instance {instance.InstanceId} maps chain member {rootMemberId} across incompatible lane families";
                    return false;
                }

                var tickOffset = (long)instance.StartTick - sourceInstance.StartTick;
                var startTickLong = (long)chain.StartTick + tickOffset;
                var endTickLong = chain.EndTick + tickOffset;
                if (startTickLong < 0 || startTickLong > int.MaxValue ||
                    endTickLong < 0 || endTickLong > int.MaxValue)
                {
                    error = $"group {groupId}, instance {instance.InstanceId} maps chain member {rootMemberId} outside the chart tick range";
                    return false;
                }

                var startTick = (int)startTickLong;
                var endTick = (int)Math.Max(endTickLong, startTickLong < int.MaxValue ? startTickLong + 1 : startTickLong);
                var hasTailAtStart = chain.Nodes.Any(node => node.NoteType == NoteType.LinkTail &&
                    (long)node.Tick + tickOffset == startTickLong);
                int? terminalTailTick = chain.Nodes.LastOrDefault() is { NoteType: NoteType.LinkTail } terminalTail
                    ? (int)(terminalTail.Tick + tickOffset)
                    : null;
                var preferredPairs = mappedNodes.Select(node => node.Target.LinePair)
                    .Where(pair => pair is 1 or 3 or 5 or 7)
                    .GroupBy(pair => pair)
                    .OrderByDescending(group => group.Count())
                    .ThenBy(group => group.Key)
                    .Select(group => group.Key)
                    .ToList();
                plans.Add((instance, chain, mappedNodes, mappedFamilies[0]!,
                    startTick, endTick, hasTailAtStart, terminalTailTick, preferredPairs));
            }
        }

        var routeTimeline = new GearTimeline(_chart);
        var assigned = new List<(int Pair, string Family, int StartTick, long EndTick,
            bool HasTailAtStart, int? TerminalTailTick)>();
        var resolvedPlans = new List<(NoteGroupInstance Instance, EditorLinkChainInfo Chain,
            List<(ChartEvent Source, ChartEvent Target)> Nodes, int Pair)>();

        foreach (var plan in plans.OrderBy(plan => plan.StartTick)
                     .ThenBy(plan => plan.Instance.InstanceId)
                     .ThenBy(plan => plan.Chain.Root.NoteGroupMemberId))
        {
            var candidates = plan.PreferredPairs.Concat(new[] { 1, 3, 5, 7 }).Distinct();
            var selectedPair = 0;
            foreach (var candidate in candidates)
            {
                if (IsLinkPairOccupied(_chart, plan.Family, candidate, plan.StartTick, plan.EndTick,
                        targetChainEvents, plan.HasTailAtStart))
                    continue;
                if (assigned.Any(existing => existing.Pair == candidate &&
                    EditorLinkIntervalsConflict(routeTimeline,
                        existing.Family, existing.StartTick, existing.EndTick, existing.HasTailAtStart,
                        plan.Family, plan.StartTick, plan.EndTick, plan.HasTailAtStart,
                        existing.TerminalTailTick, plan.TerminalTailTick)))
                    continue;

                selectedPair = candidate;
                break;
            }

            if (selectedPair == 0)
            {
                error = $"no safe LinkPair ID is available for group {groupId}, instance {plan.Instance.InstanceId}, chain member {plan.Chain.Root.NoteGroupMemberId}";
                return false;
            }

            assigned.Add((selectedPair, plan.Family, plan.StartTick, plan.EndTick,
                plan.HasTailAtStart, plan.TerminalTailTick));
            resolvedPlans.Add((plan.Instance, plan.Chain, plan.Nodes, selectedPair));
        }

        long nextTargetEventIndex = (long)_chart.Events.Select(ev => ev.EventIndex)
            .DefaultIfEmpty(-1).Max() + 1;
        foreach (var plan in resolvedPlans)
        {
            foreach (var (source, target) in plan.Nodes)
            {
                var nextTick = (long)plan.Instance.StartTick + source.Tick - sourceInstance.StartTick;
                var nextTrack = GetEditorNoteGroupCounterpartTrack(sourceInstance, plan.Instance, source.Track);
                if (nextTick < 0 || nextTick > int.MaxValue || nextTrack is < 0 or > 15)
                {
                    error = $"group {groupId}, instance {plan.Instance.InstanceId} cannot copy link member {source.NoteGroupMemberId}";
                    return false;
                }

                if (!ReferenceEquals(source, target) && target.Track != nextTrack)
                {
                    foreach (var track in _chart.Tracks)
                        track.Events.RemoveAll(candidate => ReferenceEquals(candidate, target));
                    target.Track = nextTrack;
                    GetOrCreateEditorTrack(nextTrack).Events.Add(target);
                }

                if (!ReferenceEquals(source, target))
                {
                    if (nextTargetEventIndex > int.MaxValue)
                    {
                        error = $"group {groupId}, instance {plan.Instance.InstanceId} exhausted event ordering IDs while synchronizing its link chain";
                        return false;
                    }
                    target.EventIndex = (int)nextTargetEventIndex++;
                    target.Tick = (int)nextTick;
                    target.Duration = source.Duration;
                    target.RawType = source.RawType;
                    target.TypeIndex = source.TypeIndex;
                    target.Kind = source.Kind;
                    target.NoteType = source.NoteType;
                    target.Value = source.Value;
                    target.Instrument = source.Instrument;
                    target.Velocity = source.Velocity;
                    target.Pan = source.Pan;
                    target.TwinPairId = source.TwinPairId;
                    target.LoopReconstructed = source.LoopReconstructed;
                    target.LoopPhase = source.LoopPhase;
                }

                target.LinePair = plan.Pair;
                target.Attr = source.NoteType == NoteType.LinkHead ? plan.Pair : plan.Pair + 1;
            }
        }

        AresLoader.FinalizeEvents(_chart);
        return true;
    }

    private int? GetEditorNoteGroupLogicalChainRootMemberId(NoteGroupDefinition group, ChartEvent source)
    {
        if (source.NoteGroupMemberId is not int memberId)
            return null;

        // A live tail must resolve inside its own group instance. The chart-wide
        // LinkBuilder graph can contain links from another instance when physical
        // pair IDs overlap, so never use GetEditorLinkRoot for grouped notes here.
        if (source.NoteGroupId == group.Id && source.NoteGroupInstanceId is int instanceId &&
            _chart?.Tracks.Any(track => track.Events.Any(ev => ReferenceEquals(ev, source))) == true)
        {
            if (source.NoteType == NoteType.LinkHead)
                return memberId;

            var chain = GetEditorNoteGroupLinkChainInfos(group.Id, instanceId)
                .FirstOrDefault(candidate => candidate.Nodes.Any(node => ReferenceEquals(node, source)));
            if (chain?.Root.NoteGroupMemberId is int liveRootMemberId)
                return liveRootMemberId;
            return null;
        }

        // When every live instance is incomplete, placement can be built directly
        // from the saved pattern definition and there is no live LinkBuilder graph.
        // ChainGroup is the saved logical relationship in that fallback case.
        var member = group.Members.FirstOrDefault(candidate => candidate.Id == memberId);
        if (member is null || member.ChainGroup < 0)
            return null;
        var roots = group.Members.Where(candidate => candidate.ChainGroup == member.ChainGroup &&
            candidate.NoteType == NoteType.LinkHead).ToList();
        return roots.Count == 1 ? roots[0].Id : null;
    }

    private bool TryAllocateEditorNoteGroupLinkPair(NoteGroupDefinition group, int chainRootMemberId,
        List<ChartEvent> sourceChain, NoteGroupInstance sourceInstance, NoteGroupInstance targetInstance,
        Dictionary<int, Dictionary<int, EditorNoteGroupLinkPairReservation>> pairMaps,
        out int pair, out string error, bool remappingWholeGroup = false,
        GearTimeline? routeTimelineOverride = null,
        HashSet<ChartEvent>? targetChainEventsOverride = null,
        IReadOnlyCollection<string>? linkFamiliesOverride = null,
        IReadOnlyDictionary<(string Family, int Pair), List<ChartEvent>>? linkPairNodesOverride = null)
    {
        pair = -1;
        error = string.Empty;
        if (_chart is null || sourceChain.Count < 2 || sourceChain.Any(ev =>
                ev.NoteGroupId != group.Id || ev.NoteGroupInstanceId != sourceInstance.InstanceId ||
                !ev.NoteGroupMemberId.HasValue || ev.NoteType is not (NoteType.LinkHead or NoteType.LinkTail)))
        {
            error = $"group {group.Id}, chain member {chainRootMemberId} is not a complete link chain";
            return false;
        }

        var sourceRoot = sourceChain.FirstOrDefault(ev => ev.NoteGroupMemberId == chainRootMemberId &&
            ev.NoteType == NoteType.LinkHead);
        if (sourceRoot is null)
        {
            error = $"group {group.Id}, chain member {chainRootMemberId} has no LinkHead";
            return false;
        }

        if (!pairMaps.TryGetValue(targetInstance.InstanceId, out var map))
            pairMaps[targetInstance.InstanceId] = map = new Dictionary<int, EditorNoteGroupLinkPairReservation>();
        if (map.TryGetValue(chainRootMemberId, out var existingAssignment))
        {
            pair = existingAssignment.Pair;
            return true;
        }

        // Every link chain in this destination group instance is about to be
        // reconciled from logical member relationships. Ignore its current pair
        // assignments while choosing the replacement; otherwise an old ID on a
        // different chain can block the first chain, even though both are being
        // remapped in this same transaction. The per-instance reservation map
        // below prevents the newly planned chains from colliding with each other.
        var targetChainEvents = targetChainEventsOverride ?? _chart.Events.Where(ev => ev.NoteGroupId == group.Id &&
                (remappingWholeGroup || ev.NoteGroupInstanceId == targetInstance.InstanceId) &&
                ev.NoteType is NoteType.LinkHead or NoteType.LinkTail)
            .ToHashSet();

        var targetFamilyTrack = GetEditorNoteGroupCounterpartTrack(sourceInstance, targetInstance, sourceRoot.Track);
        var targetFamily = LinkBuilder.Family(targetFamilyTrack);
        if (targetFamily is null)
        {
            error = $"group {group.Id}, instance {targetInstance.InstanceId} maps its link chain outside a link-capable bank";
            return false;
        }

        var tickOffset = (long)targetInstance.StartTick - sourceInstance.StartTick;
        var startTickLong = sourceChain.Min(ev => (long)ev.Tick + tickOffset);
        var endTickLong = sourceChain.Max(ev => (long)ev.Tick + Math.Max(1, ev.Duration) + tickOffset);
        if (startTickLong < 0 || startTickLong > int.MaxValue || endTickLong < 0 || endTickLong > int.MaxValue)
        {
            error = $"group {group.Id}, instance {targetInstance.InstanceId} has a link chain outside the supported tick range";
            return false;
        }
        var startTick = (int)startTickLong;
        var endTick = Math.Max(startTick < int.MaxValue ? startTick + 1 : startTick, (int)endTickLong);
        var hasTailAtStart = sourceChain.Any(ev => ev.NoteType == NoteType.LinkTail &&
            (long)ev.Tick + tickOffset == startTickLong);
        int? terminalTailTick = sourceChain.LastOrDefault() is { NoteType: NoteType.LinkTail } sourceTerminalTail
            ? (int)(sourceTerminalTail.Tick + tickOffset)
            : null;

        var candidatePairs = targetChainEvents
            .Where(ev => (ev.NoteType is NoteType.LinkHead or NoteType.LinkTail) &&
                         (ev.LinePair is 1 or 3 or 5 or 7))
            .GroupBy(ev => ev.LinePair)
            .OrderByDescending(candidate => candidate.Count())
            .ThenBy(candidate => candidate.Key)
            .Select(candidate => candidate.Key)
            .Concat(new[] { 1, 3, 5, 7 })
            .Distinct();

        var routeTimeline = routeTimelineOverride ?? new GearTimeline(_chart);
        foreach (var candidate in candidatePairs)
        {
            if (IsLinkPairOccupied(_chart, targetFamily, candidate, startTick, endTick,
                    targetChainEvents, hasTailAtStart, routeTimeline,
                    linkFamiliesOverride, linkPairNodesOverride))
                continue;

            // Newly allocated chains have not been cloned into the destination
            // yet. Reserve their route/time windows to avoid assigning the same
            // pair to two overlapping logical chains in this edit transaction.
            var conflictsWithPendingChain = map.Values.Any(reservation =>
                reservation.Pair == candidate &&
                EditorLinkIntervalsConflict(routeTimeline,
                    targetFamily, startTick, endTick, hasTailAtStart,
                    reservation.Family, reservation.StartTick, reservation.EndTick,
                    reservation.HasTailAtStart,
                    terminalTailTick, reservation.TerminalTailTick));
            if (conflictsWithPendingChain)
                continue;

            pair = candidate;
            map[chainRootMemberId] = new EditorNoteGroupLinkPairReservation(
                pair, targetFamily, startTick, endTick, hasTailAtStart, terminalTailTick);
            return true;
        }

        error = $"no safe LinkPair ID is available for group {group.Id}, instance {targetInstance.InstanceId}, chain member {chainRootMemberId}";
        return false;
    }

    private bool TryResolveEditorNoteGroupLinkPair(ChartEvent source,
        NoteGroupInstance sourceInstance, NoteGroupInstance targetInstance,
        Dictionary<int, Dictionary<int, EditorNoteGroupLinkPairReservation>> pairMaps,
        out int pair, out string error, bool remappingWholeGroup = false)
    {
        pair = -1;
        error = string.Empty;
        if (_chart is null || source.NoteGroupId is not int groupId ||
            source.NoteGroupInstanceId != sourceInstance.InstanceId)
        {
            error = "the source link point is not attached to the edited group instance";
            return false;
        }

        var group = _chart.NoteGroups.FirstOrDefault(candidate => candidate.Id == groupId);
        if (group is null)
        {
            error = $"group {groupId} no longer exists";
            return false;
        }

        // Synchronization resolves chains from an isolated graph for this group
        // instance. Do not rebuild the chart-wide LinkBuilder graph here: sibling
        // instances may temporarily share physical pair IDs during the transaction.
        var chainRootMemberId = GetEditorNoteGroupLogicalChainRootMemberId(group, source);
        if (chainRootMemberId is not int rootMemberId)
        {
            error = $"group {groupId} contains a link point that has no stable logical chain root";
            return false;
        }

        if (pairMaps.TryGetValue(targetInstance.InstanceId, out var existingMap) &&
            existingMap.TryGetValue(rootMemberId, out var existingAssignment))
        {
            pair = existingAssignment.Pair;
            return true;
        }

        var sourceChain = GetEditorNoteGroupLinkChainInfos(groupId, sourceInstance.InstanceId)
            .FirstOrDefault(chain => chain.Root.NoteGroupMemberId == rootMemberId)?.Nodes;
        if (sourceChain is null)
        {
            error = $"group {groupId}, chain member {rootMemberId} has no live LinkHead; its saved pattern can only be used during placement";
            return false;
        }

        return TryAllocateEditorNoteGroupLinkPair(group, rootMemberId, sourceChain,
            sourceInstance, targetInstance, pairMaps, out pair, out error, remappingWholeGroup);
    }

    private ChartEvent? CreateEditorNoteGroupCounterpart(NoteGroupDefinition group, ChartEvent source,
        NoteGroupInstance sourceInstance, NoteGroupInstance targetInstance,
        Dictionary<int, Dictionary<int, EditorNoteGroupLinkPairReservation>> pairMaps,
        bool remappingWholeGroup = false)
    {
        if (_chart is null || source.NoteGroupMemberId is not int memberId ||
            source.NoteGroupId != group.Id)
            return null;

        var tick = (long)targetInstance.StartTick + source.Tick - sourceInstance.StartTick;
        var trackIndex = GetEditorNoteGroupCounterpartTrack(sourceInstance, targetInstance, source.Track);
        if (tick < 0 || tick > int.MaxValue || trackIndex is < 0 or > 15)
            return null;

        var noteType = source.NoteType;
        var pair = -1;
        var attr = GetPhysicalEditorNoteGroupAttr(targetInstance,
            GetCanonicalEditorNoteGroupAttr(sourceInstance, source.Attr));
        if (noteType is NoteType.LinkHead or NoteType.LinkTail)
        {
            if (!TryResolveEditorNoteGroupLinkPair(source, sourceInstance, targetInstance, pairMaps,
                    out pair, out var error, remappingWholeGroup))
            {
                _statusMessage = $"Group {group.Id} could not recreate link member {memberId}: {error}.";
                return null;
            }
            attr = noteType == NoteType.LinkHead ? pair : pair + 1;
        }

        var destination = GetOrCreateEditorTrack(trackIndex);
        var clone = new ChartEvent
        {
            Tick = (int)tick,
            Track = trackIndex,
            Attr = attr,
            RawType = source.RawType,
            TypeIndex = -1,
            Duration = source.Duration,
            NoteType = noteType,
            EventIndex = destination.Events.Select(ev => ev.EventIndex).DefaultIfEmpty(-1).Max() + 1,
            LinePair = pair,
            TwinPairId = source.TwinPairId,
            LoopReconstructed = source.LoopReconstructed,
            LoopPhase = source.LoopPhase,
            Kind = source.Kind,
            Value = source.Value,
            Instrument = source.Instrument,
            Velocity = source.Velocity,
            Pan = source.Pan,
            NoteGroupId = group.Id,
            NoteGroupInstanceId = targetInstance.InstanceId,
            NoteGroupMemberId = memberId
        };
        destination.Events.Add(clone);
        AresLoader.FinalizeEvents(_chart);
        return clone;
    }

    private bool ApplyEditorNoteGroupMemberChange(ChartEvent source,
        ChartEvent target, NoteGroupInstance sourceInstance, Dictionary<int, Dictionary<int, EditorNoteGroupLinkPairReservation>> pairMaps,
        out string error, bool remappingWholeGroup = false)
    {
        error = string.Empty;
        if (_chart is null || source.NoteGroupId is not int groupId || source.NoteGroupMemberId is not int memberId ||
            target.NoteGroupInstanceId is not int targetInstanceId)
        {
            error = "a matching group member lost its identity during synchronization";
            return false;
        }
        var targetInstance = _chart.NoteGroupInstances.FirstOrDefault(instance =>
            instance.GroupId == groupId && instance.InstanceId == targetInstanceId);
        if (targetInstance is null)
        {
            error = $"group {groupId} is missing destination instance {targetInstanceId}";
            return false;
        }

        var nextTick = (long)targetInstance.StartTick + source.Tick - sourceInstance.StartTick;
        var nextTrack = GetEditorNoteGroupCounterpartTrack(sourceInstance, targetInstance, source.Track);
        if (nextTick < 0 || nextTick > int.MaxValue || nextTrack is < 0 or > 15)
        {
            error = $"group {groupId}, member {memberId} would leave the chart lanes or tick range";
            return false;
        }

        if (target.Track != nextTrack)
        {
            foreach (var track in _chart.Tracks)
                track.Events.RemoveAll(candidate => ReferenceEquals(candidate, target));
            target.Track = nextTrack;
            var destination = GetOrCreateEditorTrack(nextTrack);
            target.EventIndex = destination.Events.Select(candidate => candidate.EventIndex).DefaultIfEmpty(-1).Max() + 1;
            destination.Events.Add(target);
        }
        target.Tick = (int)nextTick;
        target.Duration = source.Duration;
        target.RawType = source.RawType;
        target.TypeIndex = source.TypeIndex;
        target.Kind = source.Kind;
        target.NoteType = source.NoteType;
        target.Value = source.Value;
        target.Instrument = source.Instrument;
        target.Velocity = source.Velocity;
        target.Pan = source.Pan;
        target.TwinPairId = source.TwinPairId;
        target.LoopReconstructed = source.LoopReconstructed;
        target.LoopPhase = source.LoopPhase;

        if (source.NoteType is NoteType.LinkHead or NoteType.LinkTail)
        {
            if (!TryResolveEditorNoteGroupLinkPair(source, sourceInstance, targetInstance, pairMaps,
                    out var pair, out error, remappingWholeGroup: true))
                return false;
            target.LinePair = pair;
            target.Attr = source.NoteType == NoteType.LinkHead ? pair : pair + 1;
        }
        else
        {
            var canonicalAttr = GetCanonicalEditorNoteGroupAttr(sourceInstance, source.Attr);
            target.Attr = GetPhysicalEditorNoteGroupAttr(targetInstance, canonicalAttr);
            target.LinePair = -1;
            target.TwinPairId = source.TwinPairId;
        }
        return true;
    }

    private bool AddEditorNoteGroupMembersToInstances(NoteGroupDefinition group, NoteGroupInstance sourceInstance,
        List<ChartEvent> newNotes, Dictionary<int, Dictionary<int, EditorNoteGroupLinkPairReservation>> pairMaps, out string error)
    {
        error = string.Empty;
        if (_chart is null)
            return false;

        var nextMemberId = group.Members.Select(member => member.Id).DefaultIfEmpty(0).Max() + 1;
        foreach (var source in newNotes.OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track).ThenBy(ev => ev.EventIndex))
        {
            if (source.NoteGroupId is int assignedGroup && assignedGroup != group.Id)
            {
                error = $"a connector target belongs to another group (G{assignedGroup})";
                return false;
            }
            source.NoteGroupId = group.Id;
            source.NoteGroupInstanceId = sourceInstance.InstanceId;
            source.NoteGroupMemberId = nextMemberId++;
        }

        var rootMemberByNode = new Dictionary<ChartEvent, int>();
        foreach (var chain in GetEditorNoteGroupLinkChainInfos(group.Id, sourceInstance.InstanceId))
        {
            if (chain.Root.NoteGroupMemberId is not int rootMemberId)
            {
                error = $"group {group.Id} contains a newly added link chain without a member identity";
                return false;
            }
            foreach (var node in chain.Nodes)
            {
                if (rootMemberByNode.TryGetValue(node, out var priorRoot) && priorRoot != rootMemberId)
                {
                    error = $"group {group.Id} contains an ambiguous link point shared by multiple chain paths";
                    return false;
                }
                rootMemberByNode[node] = rootMemberId;
            }
        }

        var nextChainGroup = group.Members.Select(member => member.ChainGroup).DefaultIfEmpty(-1).Max() + 1;
        var chainGroups = new Dictionary<int, int>();
        foreach (var source in newNotes)
        {
            var chainGroup = -1;
            if (source.NoteType is NoteType.LinkHead or NoteType.LinkTail)
            {
                if (!rootMemberByNode.TryGetValue(source, out var rootMemberId))
                {
                    error = $"new link member {source.NoteGroupMemberId} has no complete logical chain root";
                    return false;
                }

                if (!chainGroups.TryGetValue(rootMemberId, out chainGroup))
                {
                    chainGroup = group.Members.FirstOrDefault(member => member.Id == rootMemberId)?.ChainGroup ?? -1;
                    if (chainGroup < 0)
                        chainGroup = nextChainGroup++;
                    chainGroups[rootMemberId] = chainGroup;
                }
            }

            var canonicalTrack = GetCanonicalEditorNoteGroupTrack(sourceInstance, source.Track);
            var canonicalAttr = GetCanonicalEditorNoteGroupAttr(sourceInstance, source.Attr);
            group.Members.Add(new NoteGroupMember(source.NoteGroupMemberId!.Value,
                source.Tick - sourceInstance.StartTick, canonicalTrack - sourceInstance.StartTrack, canonicalAttr,
                source.Duration, source.Instrument, source.Velocity, source.Pan, source.NoteType,
                chainGroup, source.LinePair));
        }

        var targetInstances = _chart.NoteGroupInstances.Where(instance => instance.GroupId == group.Id &&
            instance.InstanceId != sourceInstance.InstanceId).ToList();
        foreach (var targetInstance in targetInstances)
        {
            var targetPairs = new Dictionary<int, int>();
            foreach (var chainRootGroup in newNotes
                         .Where(ev => ev.NoteType is NoteType.LinkHead or NoteType.LinkTail)
                         .GroupBy(ev => rootMemberByNode.TryGetValue(ev, out var rootMemberId) ? rootMemberId : -1))
            {
                if (chainRootGroup.Key < 0)
                {
                    error = $"group {group.Id} contains a newly added link member without a chain root";
                    return false;
                }
                var first = chainRootGroup.OrderBy(ev => ev.Tick)
                    .ThenBy(ev => ev.NoteType == NoteType.LinkHead ? 0 : 1).First();
                if (!TryResolveEditorNoteGroupLinkPair(first, sourceInstance, targetInstance, pairMaps,
                        out var pair, out error, remappingWholeGroup: true))
                    return false;
                targetPairs[chainRootGroup.Key] = pair;
            }

            foreach (var source in newNotes)
            {
                var tickLong = (long)targetInstance.StartTick + source.Tick - sourceInstance.StartTick;
                var trackIndex = GetEditorNoteGroupCounterpartTrack(sourceInstance, targetInstance, source.Track);
                if (tickLong < 0 || tickLong > int.MaxValue || trackIndex is < 0 or > 15)
                {
                    error = $"group {group.Id}, instance {targetInstance.InstanceId} cannot place member {source.NoteGroupMemberId}";
                    return false;
                }
                var noteType = source.NoteType;
                var pair = -1;
                if (noteType is NoteType.LinkHead or NoteType.LinkTail)
                {
                    if (!rootMemberByNode.TryGetValue(source, out var chainRootId) ||
                        !targetPairs.TryGetValue(chainRootId, out pair))
                    {
                        error = $"group {group.Id}, instance {targetInstance.InstanceId} has no allocated pair for member {source.NoteGroupMemberId}";
                        return false;
                    }
                }
                var canonicalAttr = GetCanonicalEditorNoteGroupAttr(sourceInstance, source.Attr);
                var attr = noteType == NoteType.LinkHead ? pair : noteType == NoteType.LinkTail ? pair + 1 :
                    GetPhysicalEditorNoteGroupAttr(targetInstance, canonicalAttr);
                var track = GetOrCreateEditorTrack(trackIndex);
                var clone = new ChartEvent
                {
                    Tick = (int)tickLong,
                    Track = trackIndex,
                    Attr = attr,
                    RawType = source.RawType,
                    TypeIndex = -1,
                    Duration = source.Duration,
                    NoteType = noteType,
                    EventIndex = track.Events.Select(ev => ev.EventIndex).DefaultIfEmpty(-1).Max() + 1,
                    LinePair = pair,
                    TwinPairId = source.TwinPairId,
                    LoopReconstructed = source.LoopReconstructed,
                    LoopPhase = source.LoopPhase,
                    Kind = source.Kind,
                    Value = source.Value,
                    Instrument = source.Instrument,
                    Velocity = source.Velocity,
                    Pan = source.Pan,
                    NoteGroupId = group.Id,
                    NoteGroupInstanceId = targetInstance.InstanceId,
                    NoteGroupMemberId = source.NoteGroupMemberId
                };
                track.Events.Add(clone);
            }
            AresLoader.FinalizeEvents(_chart);
        }
        return true;
    }

    private bool ValidateEditorNoteGroupLinkSynchronization(int groupId, int sourceInstanceId, out string error)
    {
        error = string.Empty;
        if (_chart is null)
            return false;

        RefreshEditorNoteGroupEventGraph();
        var instances = _chart.NoteGroupInstances.Where(instance => instance.GroupId == groupId)
            .OrderBy(instance => instance.InstanceId).ToList();
        var sourceInstance = instances.FirstOrDefault(instance => instance.InstanceId == sourceInstanceId);
        if (sourceInstance is null)
        {
            error = $"group {groupId} lost its edited instance during synchronization";
            return false;
        }

        var sourceMembers = _chart.Events.Where(ev => ev.NoteGroupId == groupId &&
                ev.NoteGroupInstanceId == sourceInstanceId && ev.NoteGroupMemberId.HasValue &&
                ev.Kind == "Note" && ev.RawType == 1)
            .GroupBy(ev => ev.NoteGroupMemberId!.Value).ToList();
        if (sourceMembers.Any(group => group.Count() != 1))
        {
            error = $"group {groupId}, edited instance {sourceInstanceId} has duplicate member IDs";
            return false;
        }
        var sourceMemberIds = sourceMembers.Select(group => group.Key).ToHashSet();
        var logicalEdgesByInstance = new Dictionary<int, HashSet<(int HeadMember, int TailMember)>>();
        var allGroupedChains = new List<EditorLinkChainInfo>();

        foreach (var instance in instances)
        {
            var instanceNotes = _chart.Events.Where(ev => ev.NoteGroupId == groupId &&
                    ev.NoteGroupInstanceId == instance.InstanceId && ev.Kind == "Note" && ev.RawType == 1)
                .ToList();
            var memberGroups = instanceNotes.Where(ev => ev.NoteGroupMemberId.HasValue)
                .GroupBy(ev => ev.NoteGroupMemberId!.Value).ToList();
            if (memberGroups.Any(group => group.Count() != 1) ||
                !memberGroups.Select(group => group.Key).ToHashSet().SetEquals(sourceMemberIds))
            {
                error = $"group {groupId}, instance {instance.InstanceId} does not have exactly the same logical members as the edited instance";
                return false;
            }
            if (instanceNotes.Any(ev => !ev.NoteGroupMemberId.HasValue))
            {
                error = $"group {groupId}, instance {instance.InstanceId} contains a note without a logical member ID";
                return false;
            }

            var localChains = GetEditorNoteGroupLinkChainInfos(groupId, instance.InstanceId);
            allGroupedChains.AddRange(localChains);
            var chainMembership = localChains.SelectMany(chain => chain.Nodes.Select((node, index) =>
                    (Node: node, Chain: chain, Index: index)))
                .GroupBy(item => item.Node)
                .ToDictionary(group => group.Key, group => group.ToList());

            var linkPoints = instanceNotes.Where(ev => ev.NoteType is NoteType.LinkHead or NoteType.LinkTail).ToList();
            foreach (var point in linkPoints)
            {
                if (!chainMembership.TryGetValue(point, out var membership) || membership.Count != 1)
                {
                    error = $"group {groupId}, instance {instance.InstanceId} has an orphaned or ambiguous {point.NoteType} at tick {point.Tick}, track {point.Track}";
                    return false;
                }
                var entry = membership[0];
                var valid = point.NoteType == NoteType.LinkHead
                    ? entry.Index == 0 && entry.Chain.Nodes.Count >= 2
                    : entry.Index > 0;
                if (!valid)
                {
                    error = $"group {groupId}, instance {instance.InstanceId} has an orphaned or ambiguous {point.NoteType} at tick {point.Tick}, track {point.Track}";
                    return false;
                }
            }

            var logicalEdges = new HashSet<(int HeadMember, int TailMember)>();
            foreach (var chain in localChains)
            {
                if (chain.Nodes.Any(node => node.NoteGroupId != groupId ||
                        node.NoteGroupInstanceId != instance.InstanceId || !node.NoteGroupMemberId.HasValue))
                {
                    error = $"group {groupId}, instance {instance.InstanceId} has a link chain that crosses group boundaries";
                    return false;
                }
                for (var index = 0; index + 1 < chain.Nodes.Count; index++)
                {
                    logicalEdges.Add((chain.Nodes[index].NoteGroupMemberId!.Value,
                        chain.Nodes[index + 1].NoteGroupMemberId!.Value));
                }
            }

            // The final chart-wide graph must not connect any point from this
            // instance to another group or another instance.
            foreach (var link in _chart.Links.Where(link =>
                         (link.Head.NoteGroupId == groupId && link.Head.NoteGroupInstanceId == instance.InstanceId) ||
                         (link.Tail.NoteGroupId == groupId && link.Tail.NoteGroupInstanceId == instance.InstanceId)))
            {
                if (link.Head.NoteGroupId != groupId || link.Tail.NoteGroupId != groupId ||
                    link.Head.NoteGroupInstanceId != instance.InstanceId ||
                    link.Tail.NoteGroupInstanceId != instance.InstanceId ||
                    !link.Head.NoteGroupMemberId.HasValue || !link.Tail.NoteGroupMemberId.HasValue)
                {
                    error = $"group {groupId}, instance {instance.InstanceId} has a link connected to a note outside its logical instance";
                    return false;
                }
            }

            logicalEdgesByInstance[instance.InstanceId] = logicalEdges;
        }

        var sourceEdges = logicalEdgesByInstance.GetValueOrDefault(sourceInstanceId) ?? new HashSet<(int, int)>();
        foreach (var instance in instances.Where(instance => instance.InstanceId != sourceInstanceId))
        {
            var targetEdges = logicalEdgesByInstance.GetValueOrDefault(instance.InstanceId) ?? new HashSet<(int, int)>();
            if (!sourceEdges.SetEquals(targetEdges))
            {
                error = $"group {groupId}, instance {instance.InstanceId} has a different logical link path from the edited instance";
                return false;
            }
        }

        // Check physical pair collisions against unrelated chart chains and against
        // other group instances. Per-instance pair IDs may differ, but any reused ID
        // must be safe for every route/time interval where it occurs.
        var routeTimeline = new GearTimeline(_chart);
        var externalChains = GetEditorLinkChainInfos()
            .Where(chain => !chain.Nodes.Any(node => node.NoteGroupId == groupId)).ToList();
        var chainsToCheck = allGroupedChains.Concat(externalChains).ToList();
        for (var index = 0; index < allGroupedChains.Count; index++)
        {
            var groupedChain = allGroupedChains[index];
            foreach (var otherChain in chainsToCheck.Where((chain, otherIndex) =>
                         !ReferenceEquals(chain, groupedChain) && chain.Pair == groupedChain.Pair))
            {
                if (EditorLinkChainsConflictInTime(routeTimeline, groupedChain, otherChain) &&
                    groupedChain.StartTick < otherChain.EndTick && otherChain.StartTick < groupedChain.EndTick)
                {
                    error = $"group {groupId} has a LinkPair collision with another reachable chain near tick {Math.Max(groupedChain.StartTick, otherChain.StartTick)}";
                    return false;
                }
                var boundaryTail = GetEditorLinkBoundaryTailConflict(groupedChain, otherChain) ??
                                   GetEditorLinkBoundaryTailConflict(otherChain, groupedChain);
                if (boundaryTail is not null &&
                    EditorLinkFamiliesCanSharePair(routeTimeline, groupedChain.Family, otherChain.Family,
                        boundaryTail.Tick, boundaryTail.Tick < int.MaxValue ? boundaryTail.Tick + 1 : boundaryTail.Tick))
                {
                    error = $"group {groupId} has an ambiguous LinkTail at a LinkPair hand-off tick {boundaryTail.Tick}";
                    return false;
                }
            }
        }
        return true;
    }

    private void UpdateEditorNoteGroupDefinitionFromInstance(NoteGroupDefinition group, NoteGroupInstance sourceInstance)
    {
        if (_chart is null)
            return;
        var events = _chart.Events.Where(ev => ev.NoteGroupId == group.Id &&
            ev.NoteGroupInstanceId == sourceInstance.InstanceId && ev.NoteGroupMemberId.HasValue &&
            ev.Kind == "Note" && ev.RawType == 1).ToList();
        var chainIds = new Dictionary<int, int>();
        var rootMemberByNode = GetEditorNoteGroupLinkChainInfos(group.Id, sourceInstance.InstanceId)
            .SelectMany(chain => chain.Nodes.Select(node => (Node: node, RootMemberId: chain.Root.NoteGroupMemberId)))
            .Where(item => item.RootMemberId.HasValue)
            .GroupBy(item => item.Node)
            .ToDictionary(grouping => grouping.Key, grouping => grouping.First().RootMemberId!.Value);
        var nextChainId = 0;
        var updated = new List<NoteGroupMember>(events.Count);
        foreach (var ev in events.OrderBy(ev => ev.NoteGroupMemberId))
        {
            var chainGroup = -1;
            if ((ev.NoteType is NoteType.LinkHead or NoteType.LinkTail) &&
                rootMemberByNode.TryGetValue(ev, out var rootMemberId))
            {
                if (!chainIds.TryGetValue(rootMemberId, out chainGroup))
                    chainIds[rootMemberId] = chainGroup = nextChainId++;
            }
            var canonicalTrack = GetCanonicalEditorNoteGroupTrack(sourceInstance, ev.Track);
            var canonicalAttr = GetCanonicalEditorNoteGroupAttr(sourceInstance, ev.Attr);
            updated.Add(new NoteGroupMember(ev.NoteGroupMemberId!.Value,
                ev.Tick - sourceInstance.StartTick, canonicalTrack - sourceInstance.StartTrack,
                canonicalAttr, ev.Duration, ev.Instrument, ev.Velocity, ev.Pan,
                ev.NoteType, chainGroup, ev.LinePair));
        }
        group.Members = updated.OrderBy(member => member.Id).ToList();
    }

}
