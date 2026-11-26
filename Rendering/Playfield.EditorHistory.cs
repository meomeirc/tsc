using Microsoft.Xna.Framework;
using TSC.Ares;

namespace TSC.Rendering;

public sealed partial class Playfield
{
    private sealed record EditorTrackSnapshot(ChartTrack Track, List<ChartEvent> Events);

    private readonly record struct EditorEventSnapshot(
        ChartEvent Event,
        int Tick,
        int Track,
        int Attr,
        int Duration,
        int RawType,
        int TypeIndex,
        string? Kind,
        NoteType NoteType,
        int EventIndex,
        int LinePair,
        float Value,
        int Instrument,
        int Velocity,
        int Pan,
        int? TwinPairId,
        bool LoopReconstructed,
        string? LoopPhase,
        int? NoteGroupId,
        int? NoteGroupInstanceId,
        int? NoteGroupMemberId);

    private sealed class EditorChartSnapshot
    {
        // Selection-only history entries deliberately leave chart data empty. They
        // are frequent (mouse selection and filtering) and must not copy the chart.
        public AresChart? SourceChart { get; init; }
        public List<EditorTrackSnapshot> Tracks { get; init; } = new();
        public List<EditorEventSnapshot> Events { get; init; } = new();
        public string? Name { get; init; }
        public string? Artist { get; init; }
        public string? Series { get; init; }
        public string? Mapper { get; init; }
        public string? Description { get; init; }
        public string? Element { get; init; }
        public string? Version { get; init; }
        public string? ScPasscode { get; init; }
        public string? Difficulty { get; init; }
        public double PerfectPlusWindowMs { get; init; }
        public double PerfectWindowMs { get; init; }
        public double GoodWindowMs { get; init; }
        public int? Level { get; init; }
        public string? MusicPath { get; init; }
        public string? PreviewPath { get; init; }
        public int? PreviewStartMilliseconds { get; init; }
        public int? PreviewEndMilliseconds { get; init; }
        public string? AlbumPath { get; init; }
        public string? AlbumBigPath { get; init; }
        public double MetadataBpm { get; init; }
        public double InitialTempo { get; init; }
        public int TicksPerMeasure { get; init; }
        public int PlayTick { get; init; }
        public double PlayTime { get; init; }
        public int AudioOriginTick { get; init; }
        public int InitialGearAttr { get; init; }
        public uint RawMagic { get; init; }
        public int AresTrackCount { get; init; }
        public bool MainLoopGenerated { get; init; }
        public int MainLoopGeneratedEvents { get; init; }
        public int MainLoopLateGeneratedEvents { get; init; }
        public int MainLoopBridgeGeneratedEvents { get; init; }
        public int? MainLoopEndTargetTick { get; init; }
        public bool LateCallbackDetected { get; init; }
        public int? LateCallbackRawSeed { get; init; }
        public int? LateCallbackTargetSeed { get; init; }
        public int LateCallbackPeriod { get; init; }
        public int LateCallbackTargetStep { get; init; }
        public bool MainLoopDetected { get; init; }
        public int? MainLoopSourceTick { get; init; }
        public int? MainLoopTargetTick { get; init; }
        public byte[]? AresHeaderTemplate { get; init; }
        public byte[]? LoadedAresHeaderTemplate { get; init; }
        public List<string> InstrumentNames { get; init; } = new();
        public List<uint> InstrumentNameHuffmanFrequencies { get; init; } = new();
        public List<int> InstrumentNameReferences { get; init; } = new();
        public List<AresInstrumentDefinition> Instruments { get; init; } = new();
        public List<NoteGroupDefinition> NoteGroups { get; init; } = new();
        public List<NoteGroupInstance> NoteGroupInstances { get; init; } = new();
        public bool SelectionOnly { get; init; }
        // Ordinary note edits use event deltas instead of cloning the full chart.
        // Only the touched event states and affected track identities are retained.
        public bool EventDeltaOnly { get; init; }
        public List<int> DeltaTrackIndices { get; init; } = new();
        public List<(int TrackIndex, ChartTrack? Track)> DeltaTrackStates { get; init; } = new();
        public HashSet<ChartEvent> DeltaPresentEvents { get; init; } = new();
        public bool DeltaRebuildTwinPairing { get; set; }
        public bool DeltaRebuildLinkGraph { get; set; }
        // A panel-note placement is a single-event add/remove. Store that delta
        // instead of cloning the entire panel track for every placement.
        public bool PanelEventOnly { get; init; }
        public int PanelTrackIndex { get; init; } = -1;
        public bool PanelTrackShouldExist { get; init; }
        public ChartTrack? PanelTrack { get; init; }
        public ChartEvent? PanelEvent { get; init; }
        public bool PanelEventPresent { get; init; }
        public ChartEvent? SelectedEvent { get; set; }
        public int? SelectedBeatTick { get; set; }
        public int? SelectedBeatTrack { get; set; }
        public ChartEvent? SelectedTempoEvent { get; set; }
        public ChartEvent? SelectedLinkRoot { get; set; }
        public bool SelectedHoldEnd { get; set; }
        public List<ChartEvent> MultiSelectedEvents { get; set; } = new();
        public List<(int Tick, int Track)> SelectedGridCells { get; set; } = new();
        public (int Tick, int Track)? PanelsSelectedEmptyCell { get; set; }
        public int? SelectReferenceLineTick { get; set; }
        public int? SelectReferenceTickOverride { get; set; }
    }

    private sealed record EditorClipboardEntry(
        int TickOffset,
        int TrackOffset,
        int CanonicalTrackOffset,
        int OriginalTrack,
        int Attr,
        int Duration,
        int Instrument,
        int Velocity,
        int Pan,
        NoteType NoteType,
        int ChainGroup,
        int OriginalLinePair,
        bool IsSuperFlick,
        string? OriginalTrackSide,
        string? OriginalTrackBank,
        int? NoteGroupId,
        int? NoteGroupInstanceId,
        int? NoteGroupMemberId,
        ChartEvent SourceEvent);

    // The clipboard owns snapshots of group definitions and instance metadata so
    // COPY/CUT remains a group operation even after the source instance is deleted
    // or the library definition is subsequently changed.
    private sealed record EditorClipboardGroupSource(
        NoteGroupDefinition Definition,
        NoteGroupInstance Instance,
        int StartTickOffset);

    private sealed record EditorClipboardGroupPastePlan(
        EditorClipboardGroupSource Source,
        int[] EntryIndexes,
        bool ReuseSourceDefinition,
        List<NoteGroupTrackMapping> TrackMap,
        List<NoteGroupMember> FittedMembers,
        int FittedStartTrack,
        string FittedSignature,
        int StartTick);

    private readonly List<EditorChartSnapshot> _editorUndoStack = new();
    private readonly List<EditorChartSnapshot> _editorRedoStack = new();
    // Keep one saved baseline per chart object so switching difficulties does not
    // lose the original comparison point for a chart that already has edits.
    private readonly Dictionary<AresChart, EditorChartSnapshot> _editorSavedChartBaselines = new();
    private const int EditorHistoryLimit = 100;
    private EditorChartSnapshot? _editorDragUndoSnapshot;
    private EditorChartSnapshot? _editorPendingRectangleUndoSnapshot;
    // A click mutation may be synchronized immediately, then committed again on release.
    // Remember that the pending snapshot already propagated its group changes.
    private bool _editorPendingRectangleGroupSynchronizationApplied;
    private EditorChartSnapshot? _editorSelectionPendingUndoSnapshot;
    private readonly HashSet<ChartEvent> _editorMultiSelectedEvents = new();
    // Grid positions track note/range selections plus explicit empty-cell and SELECT gestures.
    // SELECT's X=SELECTED filter uses the represented track coordinates.
    private readonly HashSet<(int Tick, int Track)> _editorSelectedGridCells = new();
    private bool _editorAutoMergeLinks = true;
    // Panels can select one empty animation cell independently of note selection.
    // The cyan SELECT reference line is only active while the SELECT menu is open.
    private (int Tick, int Track)? _editorPanelsSelectedEmptyCell;
    private int? _editorSelectReferenceLineTick;
    // SELECT has its own horizontal-line gesture. It selects track columns and a
    // reference tick without invoking note selection or the orange marquee.
    private bool _editorSelectLineDragging;
    private Vector2 _editorSelectLineStart;
    private int _editorSelectLineStartTrack = -1;
    private readonly List<EditorClipboardEntry> _editorClipboard = new();
    private readonly List<EditorClipboardGroupSource> _editorClipboardGroupSources = new();
    private NoteGroupInstance? _editorClipboardGroupSourceInstance;
    private bool _editorClipboardIsNoteGroupInstance;
    private int _editorClipboardAnchorTickOffset;
    private int _editorClipboardAnchorTrackOffset;
    private bool _editorSelectedHoldEnd;
    private bool _editorRectSelectionArmed;
    private bool _editorRectSelecting;
    private Vector2 _editorRectStart;
    // Last pointer position on the lane; the rectangle anchors itself to chart
    // ticks/tracks so its start remains fixed while the playhead/view scrolls.
    private Vector2 _editorRectCurrent;
    private double _editorRectStartTick;
    private int _editorRectStartTrack = -1;
    private double _editorRectCurrentTick;
    private int _editorRectCurrentTrack = -1;
    private float _editorRectStartLaneX;
    private float _editorRectCurrentLaneX;
    private string _editorRectStartSide = "left";
    private string _editorRectStartBank = "Forward";
    private string _editorRectCurrentSide = "left";
    private string _editorRectCurrentBank = "Forward";
    private bool _editorDragMovedChainAcrossBanks;
    private readonly List<ChartEvent> _editorDragChainNodes = new();

    private static EditorEventSnapshot CaptureEditorEventSnapshot(ChartEvent ev)
        => new(
            ev,
            ev.Tick,
            ev.Track,
            ev.Attr,
            ev.Duration,
            ev.RawType,
            ev.TypeIndex,
            ev.Kind,
            ev.NoteType,
            ev.EventIndex,
            ev.LinePair,
            ev.Value,
            ev.Instrument,
            ev.Velocity,
            ev.Pan,
            ev.TwinPairId,
            ev.LoopReconstructed,
            ev.LoopPhase,
            ev.NoteGroupId,
            ev.NoteGroupInstanceId,
            ev.NoteGroupMemberId);

    private static void RestoreEditorEventSnapshot(EditorEventSnapshot state)
    {
        state.Event.Tick = state.Tick;
        state.Event.Track = state.Track;
        state.Event.Attr = state.Attr;
        state.Event.Duration = state.Duration;
        state.Event.RawType = state.RawType;
        state.Event.TypeIndex = state.TypeIndex;
        state.Event.Kind = state.Kind;
        state.Event.NoteType = state.NoteType;
        state.Event.EventIndex = state.EventIndex;
        state.Event.LinePair = state.LinePair;
        state.Event.Value = state.Value;
        state.Event.Instrument = state.Instrument;
        state.Event.Velocity = state.Velocity;
        state.Event.Pan = state.Pan;
        state.Event.TwinPairId = state.TwinPairId;
        state.Event.LoopReconstructed = state.LoopReconstructed;
        state.Event.LoopPhase = state.LoopPhase;
        state.Event.NoteGroupId = state.NoteGroupId;
        state.Event.NoteGroupInstanceId = state.NoteGroupInstanceId;
        state.Event.NoteGroupMemberId = state.NoteGroupMemberId;
    }

    private bool IsEditorEventInTrackLists(ChartEvent ev)
        => _chart?.Tracks.Any(track => track.Events.Contains(ev)) == true;

    private EditorChartSnapshot? CaptureEditorEventDeltaSnapshot(
        IEnumerable<ChartEvent> affectedEvents,
        IEnumerable<int> affectedTrackIndices,
        bool rebuildTwinPairing,
        bool rebuildLinkGraph,
        EditorChartSnapshot? selectionState = null)
    {
        var chart = _chart;
        if (chart is null)
            return null;

        var trackIndices = new HashSet<int>(affectedTrackIndices);
        var eventStates = new List<EditorEventSnapshot>();
        var presentEvents = new HashSet<ChartEvent>();
        var seenEvents = new HashSet<ChartEvent>();
        foreach (var ev in affectedEvents)
        {
            if (ev is null || !seenEvents.Add(ev))
                continue;
            eventStates.Add(CaptureEditorEventSnapshot(ev));
            if (chart.Tracks.Any(track => track.Events.Contains(ev)))
                presentEvents.Add(ev);
            trackIndices.Add(ev.Track);
            foreach (var containingTrack in chart.Tracks)
                if (containingTrack.Events.Contains(ev))
                    trackIndices.Add(containingTrack.TrackIndex);
        }

        var sortedTrackIndices = trackIndices.OrderBy(index => index).ToList();
        var trackStates = new List<(int TrackIndex, ChartTrack? Track)>(sortedTrackIndices.Count);
        foreach (var trackIndex in sortedTrackIndices)
            trackStates.Add((trackIndex, chart.FindTrack(trackIndex)));

        var selection = selectionState;
        return new EditorChartSnapshot
        {
            SourceChart = chart,
            EventDeltaOnly = true,
            Events = eventStates,
            DeltaTrackIndices = sortedTrackIndices,
            DeltaTrackStates = trackStates,
            DeltaPresentEvents = presentEvents,
            DeltaRebuildTwinPairing = rebuildTwinPairing,
            DeltaRebuildLinkGraph = rebuildLinkGraph,
            SelectedEvent = selection is null ? TryGetEditorEvent(_selectedEventKey) : selection.SelectedEvent,
            SelectedBeatTick = selection is null ? _selectedBeatTick : selection.SelectedBeatTick,
            SelectedBeatTrack = selection is null ? _selectedBeatTrack : selection.SelectedBeatTrack,
            SelectedTempoEvent = selection is null ? _editorSelectedTempoEvent : selection.SelectedTempoEvent,
            SelectedLinkRoot = selection is null ? _editorSelectedLinkRoot : selection.SelectedLinkRoot,
            SelectedHoldEnd = selection is null ? _editorSelectedHoldEnd : selection.SelectedHoldEnd,
            MultiSelectedEvents = selection is null ? _editorMultiSelectedEvents.ToList() : selection.MultiSelectedEvents.ToList(),
            SelectedGridCells = selection is null ? _editorSelectedGridCells.ToList() : selection.SelectedGridCells.ToList(),
            PanelsSelectedEmptyCell = selection is null ? _editorPanelsSelectedEmptyCell : selection.PanelsSelectedEmptyCell,
            SelectReferenceLineTick = selection is null ? _editorSelectReferenceLineTick : selection.SelectReferenceLineTick,
            SelectReferenceTickOverride = selection is null ? _editorSelectReferenceTickOverride : selection.SelectReferenceTickOverride
        };
    }

    private void RegisterEditorEventDeltaAdditions(EditorChartSnapshot? snapshot, params ChartEvent[] addedEvents)
    {
        if (snapshot is null || !snapshot.EventDeltaOnly || _chart is null)
            return;

        var addedSet = new HashSet<ChartEvent>();
        var addedTicks = new HashSet<int>();
        snapshot.DeltaRebuildTwinPairing |= addedEvents.Any(EditorEventNeedsTwinPairing);
        snapshot.DeltaRebuildLinkGraph |= addedEvents.Any(EditorEventNeedsLinkGraph);
        foreach (var ev in addedEvents)
        {
            if (snapshot.Events.Any(state => ReferenceEquals(state.Event, ev)))
                continue;
            EnsureEditorAddedEventIndexIsUnique(ev);
            addedSet.Add(ev);
            addedTicks.Add(ev.Tick);
            // The event does not exist in the pre-edit state. Keep its post-create
            // fields as the redo payload, but leave it absent from DeltaPresentEvents.
            snapshot.Events.Add(CaptureEditorEventSnapshot(ev));
            EnsureEditorDeltaTrack(snapshot, ev.Track, trackDidNotExistBefore: true);
        }

        if (snapshot.DeltaRebuildTwinPairing && addedTicks.Count > 0)
        {
            var existingPeers = _chart.Events.Where(ev => !addedSet.Contains(ev) &&
                addedTicks.Contains(ev.Tick) && EditorEventNeedsTwinPairing(ev)).ToArray();
            RegisterEditorEventDeltaCurrentEvents(snapshot, existingPeers);
        }
    }

    private void EnsureEditorDeltaTrack(EditorChartSnapshot snapshot, int trackIndex, bool trackDidNotExistBefore = false)
    {
        if (snapshot.DeltaTrackIndices.Contains(trackIndex))
            return;
        snapshot.DeltaTrackIndices.Add(trackIndex);
        snapshot.DeltaTrackIndices.Sort();
        snapshot.DeltaTrackStates.Add((trackIndex, trackDidNotExistBefore ? null : _chart?.FindTrack(trackIndex)));
        snapshot.DeltaTrackStates.Sort((a, b) => a.TrackIndex.CompareTo(b.TrackIndex));
    }

    private void RegisterEditorEventDeltaCurrentEvents(EditorChartSnapshot? snapshot, IEnumerable<ChartEvent> events)
    {
        if (snapshot is null || !snapshot.EventDeltaOnly || _chart is null)
            return;

        foreach (var ev in events)
        {
            if (snapshot.Events.Any(state => ReferenceEquals(state.Event, ev)))
                continue;
            snapshot.Events.Add(CaptureEditorEventSnapshot(ev));
            if (IsEditorEventInTrackLists(ev))
                snapshot.DeltaPresentEvents.Add(ev);
            EnsureEditorDeltaTrack(snapshot, ev.Track);
            if (_chart is not null)
                foreach (var containingTrack in _chart.Tracks)
                    if (containingTrack.Events.Contains(ev))
                        EnsureEditorDeltaTrack(snapshot, containingTrack.TrackIndex);
        }
    }

    private EditorChartSnapshot? CaptureCurrentEditorEventDeltaSnapshot(EditorChartSnapshot previous)
    {
        if (_chart is null || !previous.EventDeltaOnly || !ReferenceEquals(_chart, previous.SourceChart))
            return null;

        var eventStates = new List<EditorEventSnapshot>(previous.Events.Count);
        var presentEvents = new HashSet<ChartEvent>();
        foreach (var previousState in previous.Events)
        {
            var ev = previousState.Event;
            eventStates.Add(CaptureEditorEventSnapshot(ev));
            if (IsEditorEventInTrackLists(ev))
                presentEvents.Add(ev);
        }

        var trackStates = new List<(int TrackIndex, ChartTrack? Track)>(previous.DeltaTrackIndices.Count);
        foreach (var trackIndex in previous.DeltaTrackIndices)
            trackStates.Add((trackIndex, _chart.FindTrack(trackIndex)));

        return new EditorChartSnapshot
        {
            SourceChart = _chart,
            EventDeltaOnly = true,
            Events = eventStates,
            DeltaTrackIndices = previous.DeltaTrackIndices.ToList(),
            DeltaTrackStates = trackStates,
            DeltaPresentEvents = presentEvents,
            DeltaRebuildTwinPairing = previous.DeltaRebuildTwinPairing,
            DeltaRebuildLinkGraph = previous.DeltaRebuildLinkGraph,
            SelectedEvent = TryGetEditorEvent(_selectedEventKey),
            SelectedBeatTick = _selectedBeatTick,
            SelectedBeatTrack = _selectedBeatTrack,
            SelectedTempoEvent = _editorSelectedTempoEvent,
            SelectedLinkRoot = _editorSelectedLinkRoot,
            SelectedHoldEnd = _editorSelectedHoldEnd,
            MultiSelectedEvents = _editorMultiSelectedEvents.ToList(),
            SelectedGridCells = _editorSelectedGridCells.ToList(),
            PanelsSelectedEmptyCell = _editorPanelsSelectedEmptyCell,
            SelectReferenceLineTick = _editorSelectReferenceLineTick,
            SelectReferenceTickOverride = _editorSelectReferenceTickOverride
        };
    }

    private static bool EditorEventNeedsTwinPairing(ChartEvent ev)
        => ev.TwinPairId.HasValue || ev.NoteType is NoteType.SingleLong or NoteType.Vibrato or NoteType.LinkHead or NoteType.LinkTail;

    private static bool EditorEventNeedsLinkGraph(ChartEvent ev)
        => ev.NoteType is NoteType.LinkHead or NoteType.LinkTail;

    private EditorChartSnapshot? CaptureEditorNoteEditSnapshot(
        IEnumerable<ChartEvent> affectedEvents,
        IEnumerable<int> affectedTrackIndices,
        bool rebuildTwinPairing = false,
        bool rebuildLinkGraph = false,
        EditorChartSnapshot? selectionState = null)
    {
        var events = affectedEvents.Distinct().ToList();
        if (_editorNoteGroupEditMode || events.Any(ev =>
                ev.NoteGroupId.HasValue || ev.NoteGroupInstanceId.HasValue || ev.NoteGroupMemberId.HasValue))
            return CaptureEditorChartSnapshot();

        // Several callers conservatively request graph/twin rebuilding before
        // they know which note type will result. The commit path derives the final
        // requirement from both the captured state and the post-edit events, so a
        // plain tap should not trigger a chart-wide twin/link scan.
        rebuildTwinPairing = events.Any(EditorEventNeedsTwinPairing);
        rebuildLinkGraph = events.Any(EditorEventNeedsLinkGraph);
        if (rebuildTwinPairing && _chart is not null)
        {
            // ReconstructTwinNotes assigns pair IDs in a chart-wide sequence.
            // A new/removed early pair can renumber every later pair, so preserve
            // every twin-capable event when this uncommon path is actually needed.
            events = events.Concat(_chart.Events.Where(EditorEventNeedsTwinPairing))
                .Distinct().ToList();
        }
        return CaptureEditorEventDeltaSnapshot(events, affectedTrackIndices,
            rebuildTwinPairing, rebuildLinkGraph, selectionState);
    }

    private static bool IsEditorSuperFlickAttribute(int attr)
        => attr is 20 or 40 or 60 or 70 or 80 or 90;

    private bool ShouldRecalculateEditorLastPlayableTick(IEnumerable<EditorEventSnapshot> states)
    {
        foreach (var state in states)
        {
            if (state.NoteType is NoteType.SingleLong or NoteType.Vibrato or NoteType.LinkHead or NoteType.LinkTail ||
                state.Event.NoteType is NoteType.SingleLong or NoteType.Vibrato or NoteType.LinkHead or NoteType.LinkTail)
                return true;

            // A point note edit below the current endpoint cannot change the chart's
            // last playable tick. Recompute only when an old or new point touches or
            // extends beyond that endpoint (including the empty-chart case).
            if (state.Tick >= _lastPlayableTick || state.Event.Tick >= _lastPlayableTick)
                return true;
        }
        return false;
    }

    private void NormalizeEditorTrackEventIndicesForDelta(
        EditorChartSnapshot snapshot,
        IReadOnlyCollection<int> affectedTrackIndices)
    {
        if (_chart is null || !snapshot.EventDeltaOnly)
            return;

        // ARES may expose one logical lane through multiple physical ChartTrack
        // containers. Normalize ordinals across the union, not only FindTrack()'s
        // first result, or hidden events in a secondary container can retain a
        // colliding render key and later produce ghost copies during a drag.
        var affectedLaneSet = affectedTrackIndices.ToHashSet();
        var laneEvents = new Dictionary<int, List<ChartEvent>>();
        var seenReferencesByLane = new Dictionary<int, HashSet<ChartEvent>>();
        foreach (var track in _chart.Tracks)
        {
            if (!affectedLaneSet.Contains(track.TrackIndex))
                continue;
            if (!laneEvents.TryGetValue(track.TrackIndex, out var events))
                laneEvents[track.TrackIndex] = events = new List<ChartEvent>();
            if (!seenReferencesByLane.TryGetValue(track.TrackIndex, out var seen))
                seenReferencesByLane[track.TrackIndex] = seen = new HashSet<ChartEvent>();
            foreach (var ev in track.Events)
                if (seen.Add(ev))
                    events.Add(ev);
        }

        var duplicateEvents = new List<ChartEvent>();
        foreach (var events in laneEvents.Values)
        {
            var used = new HashSet<int>();
            foreach (var ev in events)
                if (!used.Add(ev.EventIndex))
                    duplicateEvents.Add(ev);
        }

        if (duplicateEvents.Count == 0)
            return;

        // Capture malformed peers before repairing them so Undo restores the
        // original event data, while the persistent cleanup is retained on redo.
        RegisterEditorEventDeltaCurrentEvents(snapshot, duplicateEvents.Distinct());

        foreach (var events in laneEvents.Values)
        {
            if (events.Count < 2)
                continue;
            var used = new HashSet<int>();
            foreach (var ev in events)
            {
                if (used.Add(ev.EventIndex))
                    continue;

                long candidate = (long)events.Max(item => item.EventIndex) + 1L;
                if (candidate > int.MaxValue)
                    candidate = 0;
                while (candidate <= int.MaxValue && used.Contains((int)candidate))
                    candidate++;
                if (candidate > int.MaxValue)
                    return;

                ev.EventIndex = (int)candidate;
                used.Add(ev.EventIndex);
            }
        }

        // EventIndex repair is chart-data hygiene, not the user's placement/move.
        // Keep the repaired identity through Undo/Redo instead of reintroducing a
        // malformed duplicate ordinal from the old snapshot.
        foreach (var ev in duplicateEvents.Distinct())
        {
            var snapshotIndex = snapshot.Events.FindIndex(state => ReferenceEquals(state.Event, ev));
            if (snapshotIndex >= 0)
                snapshot.Events[snapshotIndex] = snapshot.Events[snapshotIndex] with
                {
                    EventIndex = ev.EventIndex
                };
        }
    }

    private void RefreshAfterEditorNoteSnapshot(EditorChartSnapshot? snapshot,
        bool rebuildTwinPairing = false, bool rebuildLinkGraph = false)
    {
        if (snapshot is { EventDeltaOnly: true })
        {
            // SuperFlick edits change the authored gear route and therefore still
            // need a GearTimeline/projection refresh. Keep that uncommon path on
            // the full refresh; ordinary notes and links use the lightweight path.
            if (snapshot.Events.Any(state => IsEditorSuperFlickAttribute(state.Attr) ||
                                             state.Event.IsSuperFlick))
            {
                RefreshAfterEditorMutation();
                return;
            }

            // A hidden/non-note event can still occupy an EventIndex in the same
            // track. Repair duplicate per-track ordinals before rebuilding render
            // indexes; otherwise ChartEventKey collisions can make a visible note
            // overwrite another event's cache mapping during placement or drag.
            var initialAffectedTrackIndices = snapshot.Events
                .SelectMany(state => new[] { state.Track, state.Event.Track })
                .Distinct()
                .ToArray();
            NormalizeEditorTrackEventIndicesForDelta(snapshot, initialAffectedTrackIndices);

            // Recompute dependency flags from both sides of the edit. This avoids
            // rebuilding twins/links for ordinary taps even when an older caller
            // conservatively requested those operations before it changed note type.
            var shouldRebuildTwinPairing = snapshot.Events.Any(state =>
                state.TwinPairId.HasValue || state.Event.TwinPairId.HasValue ||
                state.NoteType is NoteType.SingleLong or NoteType.Vibrato or NoteType.LinkHead or NoteType.LinkTail ||
                state.Event.NoteType is NoteType.SingleLong or NoteType.Vibrato or NoteType.LinkHead or NoteType.LinkTail);
            var shouldRebuildLinkGraph = snapshot.Events.Any(state =>
                state.NoteType is NoteType.LinkHead or NoteType.LinkTail ||
                state.Event.NoteType is NoteType.LinkHead or NoteType.LinkTail);

            // If a plain tap was converted into a duration note, collect the peer
            // states immediately before twin reconstruction so Undo can restore
            // their old pair IDs without a full-chart snapshot.
            if (shouldRebuildTwinPairing && _chart is not null)
            {
                var captured = snapshot.Events.Select(state => state.Event).ToHashSet();
                var peers = _chart.Events.Where(ev => !captured.Contains(ev) &&
                    EditorEventNeedsTwinPairing(ev)).ToArray();
                RegisterEditorEventDeltaCurrentEvents(snapshot, peers);
            }
            snapshot.DeltaRebuildTwinPairing = shouldRebuildTwinPairing;
            snapshot.DeltaRebuildLinkGraph = shouldRebuildLinkGraph;

            // Refresh every event carried by this delta, not only the subset whose
            // basic ordering fields changed. Render/selection indexes also cache
            // derived values by event key, and changes such as insertion, attribute,
            // duration, or same-track edits can otherwise be missed when their
            // snapshot fields compare equal after an earlier live-drag cache update.
            // Delta snapshots contain only the touched notes (plus any twin peers
            // whose pairing may be rebuilt), so this remains proportional to the edit.
            var changedEvents = snapshot.Events
                .Select(state => state.Event)
                .Distinct()
                .ToArray();
            var affectedTrackIndices = snapshot.Events
                .SelectMany(state => new[] { state.Track, state.Event.Track })
                .Distinct()
                .ToArray();
            var recalculateLastPlayableTick = ShouldRecalculateEditorLastPlayableTick(snapshot.Events);
            var incrementalPointRenderUpdate = !shouldRebuildLinkGraph && snapshot.Events.Count <= 8 &&
                snapshot.Events.All(state => state.NoteType == NoteType.Single && !IsEditorSuperFlickAttribute(state.Attr) &&
                    state.Event.NoteType == NoteType.Single && !state.Event.IsSuperFlick);
            RefreshAfterEditorNoteMutation(shouldRebuildTwinPairing,
                shouldRebuildLinkGraph, changedEvents, affectedTrackIndices,
                recalculateLastPlayableTick, incrementalPointRenderUpdate);
            return;
        }

        // Group edits and chart-wide operations retain the original full refresh.
        // They may update shared metadata/routes outside the touched note objects.
        RefreshAfterEditorMutation();
    }

    private List<ChartEvent> GetEditorDeleteAffectedEvents(IEnumerable<ChartEvent> selectedEvents)
    {
        var affected = new HashSet<ChartEvent>(selectedEvents);
        if (_editorNoteGroupEditMode)
            return affected.ToList();

        foreach (var ev in affected.ToArray())
        {
            var root = GetEditorLinkRoot(ev);
            if (root is null || root.NoteType != NoteType.LinkHead)
                continue;
            foreach (var node in GetEditorLinkChainNodes(root))
                affected.Add(node);
        }
        return affected.ToList();
    }

    private EditorChartSnapshot? CaptureEditorChartSnapshot(bool selectionOnly = false)
    {
        if (_chart is null)
            return null;

        if (selectionOnly)
        {
            return new EditorChartSnapshot
            {
                SourceChart = _chart,
                SelectionOnly = true,
                SelectedEvent = TryGetEditorEvent(_selectedEventKey),
                SelectedBeatTick = _selectedBeatTick,
                SelectedBeatTrack = _selectedBeatTrack,
                SelectedTempoEvent = _editorSelectedTempoEvent,
                SelectedLinkRoot = _editorSelectedLinkRoot,
                SelectedHoldEnd = _editorSelectedHoldEnd,
                MultiSelectedEvents = _editorMultiSelectedEvents.ToList(),
                SelectedGridCells = _editorSelectedGridCells.ToList(),
                PanelsSelectedEmptyCell = _editorPanelsSelectedEmptyCell,
                SelectReferenceLineTick = _editorSelectReferenceLineTick,
                SelectReferenceTickOverride = _editorSelectReferenceTickOverride
            };
        }

        var tracks = new List<EditorTrackSnapshot>(_chart.Tracks.Count);
        var events = new List<EditorEventSnapshot>(_chart.Events.Count);
        var seenEvents = new HashSet<ChartEvent>();
        foreach (var track in _chart.Tracks)
        {
            // Build track and value snapshots together. Keep reference de-duplication
            // for safety, but avoid a second LINQ pass and one heap object per event.
            var trackEvents = track.Events.ToList();
            tracks.Add(new EditorTrackSnapshot(track, trackEvents));
            foreach (var ev in trackEvents)
            {
                if (!seenEvents.Add(ev))
                    continue;
                events.Add(new EditorEventSnapshot(
                    ev,
                    ev.Tick,
                    ev.Track,
                    ev.Attr,
                    ev.Duration,
                    ev.RawType,
                    ev.TypeIndex,
                    ev.Kind,
                    ev.NoteType,
                    ev.EventIndex,
                    ev.LinePair,
                    ev.Value,
                    ev.Instrument,
                    ev.Velocity,
                    ev.Pan,
                    ev.TwinPairId,
                    ev.LoopReconstructed,
                    ev.LoopPhase,
                    ev.NoteGroupId,
                    ev.NoteGroupInstanceId,
                    ev.NoteGroupMemberId));
            }
        }
        return new EditorChartSnapshot
        {
            SourceChart = _chart,
            Tracks = tracks,
            Events = events,
            Name = _chart.Name,
            Artist = _chart.Artist,
            Series = _chart.Series,
            Mapper = _chart.Mapper,
            Description = _chart.Description,
            Element = _chart.Element,
            Version = _chart.Version,
            ScPasscode = _chart.ScPasscode,
            Difficulty = _chart.Difficulty,
            PerfectPlusWindowMs = _chart.PerfectPlusWindowMs,
            PerfectWindowMs = _chart.PerfectWindowMs,
            GoodWindowMs = _chart.GoodWindowMs,
            Level = _chart.Level,
            MusicPath = _chart.MusicPath,
            PreviewPath = _chart.PreviewPath,
            PreviewStartMilliseconds = _chart.PreviewStartMilliseconds,
            PreviewEndMilliseconds = _chart.PreviewEndMilliseconds,
            AlbumPath = _chart.AlbumPath,
            AlbumBigPath = _chart.AlbumBigPath,
            MetadataBpm = _chart.MetadataBpm,
            InitialTempo = _chart.InitialTempo,
            TicksPerMeasure = _chart.TicksPerMeasure,
            PlayTick = _chart.PlayTick,
            PlayTime = _chart.PlayTime,
            AudioOriginTick = _chart.AudioOriginTick,
            InitialGearAttr = _chart.InitialGearAttr,
            RawMagic = _chart.RawMagic,
            AresTrackCount = _chart.AresTrackCount,
            MainLoopGenerated = _chart.MainLoopGenerated,
            MainLoopGeneratedEvents = _chart.MainLoopGeneratedEvents,
            MainLoopLateGeneratedEvents = _chart.MainLoopLateGeneratedEvents,
            MainLoopBridgeGeneratedEvents = _chart.MainLoopBridgeGeneratedEvents,
            MainLoopEndTargetTick = _chart.MainLoopEndTargetTick,
            LateCallbackDetected = _chart.LateCallbackDetected,
            LateCallbackRawSeed = _chart.LateCallbackRawSeed,
            LateCallbackTargetSeed = _chart.LateCallbackTargetSeed,
            LateCallbackPeriod = _chart.LateCallbackPeriod,
            LateCallbackTargetStep = _chart.LateCallbackTargetStep,
            MainLoopDetected = _chart.MainLoopDetected,
            MainLoopSourceTick = _chart.MainLoopSourceTick,
            MainLoopTargetTick = _chart.MainLoopTargetTick,
            AresHeaderTemplate = _chart.AresHeaderTemplate?.ToArray(),
            LoadedAresHeaderTemplate = _loadedAresHeaderTemplate?.ToArray(),
            InstrumentNames = _chart.InstrumentNames.ToList(),
            InstrumentNameHuffmanFrequencies = _chart.InstrumentNameHuffmanFrequencies.ToList(),
            InstrumentNameReferences = _chart.InstrumentNameReferences.ToList(),
            Instruments = _chart.Instruments.ToList(),
            NoteGroups = CloneEditorNoteGroups(_chart.NoteGroups),
            NoteGroupInstances = CloneEditorNoteGroupInstances(_chart.NoteGroupInstances),
            SelectionOnly = false,
            SelectedEvent = TryGetEditorEvent(_selectedEventKey),
            SelectedBeatTick = _selectedBeatTick,
            SelectedBeatTrack = _selectedBeatTrack,
            SelectedTempoEvent = _editorSelectedTempoEvent,
            SelectedLinkRoot = _editorSelectedLinkRoot,
            SelectedHoldEnd = _editorSelectedHoldEnd,
            MultiSelectedEvents = _editorMultiSelectedEvents.ToList(),
            SelectedGridCells = _editorSelectedGridCells.ToList(),
            PanelsSelectedEmptyCell = _editorPanelsSelectedEmptyCell,
            SelectReferenceLineTick = _editorSelectReferenceLineTick,
            SelectReferenceTickOverride = _editorSelectReferenceTickOverride
        };
    }

    private EditorChartSnapshot? CaptureEditorPanelEventSnapshot(
        int trackIndex, ChartTrack track, ChartEvent panelEvent,
        bool trackShouldExist, bool eventPresent)
    {
        if (_chart is null || trackIndex is < 17 or > 20)
            return null;

        return new EditorChartSnapshot
        {
            SourceChart = _chart,
            PanelEventOnly = true,
            PanelTrackIndex = trackIndex,
            PanelTrackShouldExist = trackShouldExist,
            PanelTrack = track,
            PanelEvent = panelEvent,
            PanelEventPresent = eventPresent,
            SelectedEvent = TryGetEditorEvent(_selectedEventKey),
            SelectedBeatTick = _selectedBeatTick,
            SelectedBeatTrack = _selectedBeatTrack,
            SelectedTempoEvent = _editorSelectedTempoEvent,
            SelectedLinkRoot = _editorSelectedLinkRoot,
            SelectedHoldEnd = _editorSelectedHoldEnd,
            MultiSelectedEvents = _editorMultiSelectedEvents.ToList(),
            SelectedGridCells = _editorSelectedGridCells.ToList(),
            PanelsSelectedEmptyCell = _editorPanelsSelectedEmptyCell,
            SelectReferenceLineTick = _editorSelectReferenceLineTick,
            SelectReferenceTickOverride = _editorSelectReferenceTickOverride
        };
    }

    private EditorChartSnapshot? CaptureCurrentEditorPanelEventSnapshot(EditorChartSnapshot snapshot)
    {
        if (_chart is null || !ReferenceEquals(_chart, snapshot.SourceChart) ||
            snapshot.PanelTrack is null || snapshot.PanelEvent is null)
            return null;

        var currentTrack = _chart.FindTrack(snapshot.PanelTrackIndex);
        var trackShouldExist = ReferenceEquals(currentTrack, snapshot.PanelTrack);
        var eventPresent = trackShouldExist && currentTrack!.Events.Any(ev => ReferenceEquals(ev, snapshot.PanelEvent));
        return CaptureEditorPanelEventSnapshot(snapshot.PanelTrackIndex, snapshot.PanelTrack,
            snapshot.PanelEvent, trackShouldExist, eventPresent);
    }

    private ChartEvent? TryGetEditorEvent(ChartEventKey? key)
    {
        if (!key.HasValue)
            return null;
        if (_eventsByKey.TryGetValue(key.Value, out var gameplayEvent))
            return gameplayEvent;
        return _editorPanelEventsByKey.TryGetValue(key.Value, out var panelEvent) ? panelEvent : null;
    }

    private bool IsEditorEventInCurrentChart(ChartEvent? ev)
    {
        if (ev is null || _chart is null)
            return false;
        return ReferenceEquals(TryGetEditorEvent(KeyOf(ev)), ev);
    }

    private static List<NoteGroupDefinition> CloneEditorNoteGroups(IEnumerable<NoteGroupDefinition> groups)
        => groups.Select(group => new NoteGroupDefinition
        {
            Id = group.Id,
            Name = group.Name,
            Members = group.Members.ToList()
        }).ToList();

    private static List<NoteGroupInstance> CloneEditorNoteGroupInstances(IEnumerable<NoteGroupInstance> instances)
        => instances.Select(instance => instance with
        {
            TrackMap = instance.TrackMap?.ToList()
        }).ToList();

    private static bool EditorNoteGroupInstancesEqual(
        IReadOnlyList<NoteGroupInstance> left,
        IReadOnlyList<NoteGroupInstance> right)
    {
        if (left.Count != right.Count)
            return false;
        for (var i = 0; i < left.Count; i++)
        {
            var a = left[i];
            var b = right[i];
            if (a.GroupId != b.GroupId || a.InstanceId != b.InstanceId ||
                a.StartTick != b.StartTick || a.StartTrack != b.StartTrack || a.IsMirrored != b.IsMirrored)
                return false;
            if (a.TrackMap is null || b.TrackMap is null)
            {
                if (a.TrackMap is not null || b.TrackMap is not null)
                    return false;
            }
            else if (!a.TrackMap.SequenceEqual(b.TrackMap))
            {
                return false;
            }
        }
        return true;
    }

    private static bool EditorEventMatchesSnapshot(ChartEvent ev, EditorEventSnapshot state)
        => ev.Tick == state.Tick && ev.Track == state.Track && ev.Attr == state.Attr &&
           ev.Duration == state.Duration && ev.RawType == state.RawType &&
           ev.TypeIndex == state.TypeIndex && ev.Kind == state.Kind && ev.NoteType == state.NoteType &&
           ev.EventIndex == state.EventIndex && ev.LinePair == state.LinePair &&
           ev.Value == state.Value && ev.Instrument == state.Instrument &&
           ev.Velocity == state.Velocity && ev.Pan == state.Pan && ev.TwinPairId == state.TwinPairId &&
           ev.LoopReconstructed == state.LoopReconstructed && ev.LoopPhase == state.LoopPhase &&
           ev.NoteGroupId == state.NoteGroupId && ev.NoteGroupInstanceId == state.NoteGroupInstanceId &&
           ev.NoteGroupMemberId == state.NoteGroupMemberId;

    private static bool EditorByteArraysEqual(byte[]? left, byte[]? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null)
            return false;
        return left.AsSpan().SequenceEqual(right);
    }

    private static bool EditorNoteGroupsEqual(
        IReadOnlyList<NoteGroupDefinition> left,
        IReadOnlyList<NoteGroupDefinition> right)
    {
        if (left.Count != right.Count)
            return false;
        for (var i = 0; i < left.Count; i++)
        {
            var a = left[i];
            var b = right[i];
            if (a.Id != b.Id || a.Name != b.Name || !a.Members.SequenceEqual(b.Members))
                return false;
        }
        return true;
    }

    private static bool EditorDifficultyIdentityMatches(string? current, string? saved)
        => string.Equals(
            GetEditorDifficultyCode(current),
            GetEditorDifficultyCode(saved),
            StringComparison.OrdinalIgnoreCase);

    private bool EditorChartMatchesSnapshot(EditorChartSnapshot snapshot, AresChart? chartOverride = null)
    {
        var chart = chartOverride ?? _chart;
        if (snapshot.SelectionOnly)
            return chart is not null && ReferenceEquals(chart, snapshot.SourceChart);

        if (snapshot.PanelEventOnly)
        {
            if (chart is null || !ReferenceEquals(chart, snapshot.SourceChart) ||
                snapshot.PanelTrack is null || snapshot.PanelEvent is null)
                return false;
            var currentTrack = chart.FindTrack(snapshot.PanelTrackIndex);
            var trackExists = ReferenceEquals(currentTrack, snapshot.PanelTrack);
            if (trackExists != snapshot.PanelTrackShouldExist)
                return false;
            var eventPresent = trackExists && currentTrack!.Events.Any(ev => ReferenceEquals(ev, snapshot.PanelEvent));
            return eventPresent == snapshot.PanelEventPresent;
        }

        if (chart is null || !ReferenceEquals(chart, snapshot.SourceChart) ||
            !string.Equals(chart.Name, snapshot.Name, StringComparison.Ordinal) ||
            !string.Equals(chart.Artist, snapshot.Artist, StringComparison.Ordinal) ||
            !string.Equals(chart.Series, snapshot.Series, StringComparison.Ordinal) ||
            !string.Equals(chart.Mapper, snapshot.Mapper, StringComparison.Ordinal) ||
            !string.Equals(chart.Description, snapshot.Description, StringComparison.Ordinal) ||
            !string.Equals(chart.Element, snapshot.Element, StringComparison.Ordinal) ||
            !string.Equals(chart.Version, snapshot.Version, StringComparison.Ordinal) ||
            !string.Equals(chart.ScPasscode, snapshot.ScPasscode, StringComparison.Ordinal) ||
            !EditorDifficultyIdentityMatches(chart.Difficulty, snapshot.Difficulty) ||
            chart.PerfectPlusWindowMs != snapshot.PerfectPlusWindowMs ||
            chart.PerfectWindowMs != snapshot.PerfectWindowMs ||
            chart.GoodWindowMs != snapshot.GoodWindowMs ||
            chart.Level != snapshot.Level ||
            !string.Equals(chart.MusicPath, snapshot.MusicPath, StringComparison.Ordinal) ||
            !string.Equals(chart.PreviewPath, snapshot.PreviewPath, StringComparison.Ordinal) ||
            chart.PreviewStartMilliseconds != snapshot.PreviewStartMilliseconds ||
            chart.PreviewEndMilliseconds != snapshot.PreviewEndMilliseconds ||
            !string.Equals(chart.AlbumPath, snapshot.AlbumPath, StringComparison.Ordinal) ||
            !string.Equals(chart.AlbumBigPath, snapshot.AlbumBigPath, StringComparison.Ordinal) ||
            chart.MetadataBpm != snapshot.MetadataBpm || chart.InitialTempo != snapshot.InitialTempo ||
            chart.TicksPerMeasure != snapshot.TicksPerMeasure ||
            chart.PlayTick != snapshot.PlayTick || chart.PlayTime != snapshot.PlayTime ||
            chart.AudioOriginTick != snapshot.AudioOriginTick || chart.InitialGearAttr != snapshot.InitialGearAttr ||
            chart.RawMagic != snapshot.RawMagic || chart.AresTrackCount != snapshot.AresTrackCount ||
            chart.MainLoopGenerated != snapshot.MainLoopGenerated ||
            chart.MainLoopGeneratedEvents != snapshot.MainLoopGeneratedEvents ||
            chart.MainLoopLateGeneratedEvents != snapshot.MainLoopLateGeneratedEvents ||
            chart.MainLoopBridgeGeneratedEvents != snapshot.MainLoopBridgeGeneratedEvents ||
            chart.MainLoopEndTargetTick != snapshot.MainLoopEndTargetTick ||
            chart.LateCallbackDetected != snapshot.LateCallbackDetected ||
            chart.LateCallbackRawSeed != snapshot.LateCallbackRawSeed ||
            chart.LateCallbackTargetSeed != snapshot.LateCallbackTargetSeed ||
            chart.LateCallbackPeriod != snapshot.LateCallbackPeriod ||
            chart.LateCallbackTargetStep != snapshot.LateCallbackTargetStep ||
            chart.MainLoopDetected != snapshot.MainLoopDetected ||
            chart.MainLoopSourceTick != snapshot.MainLoopSourceTick ||
            chart.MainLoopTargetTick != snapshot.MainLoopTargetTick ||
            !EditorByteArraysEqual(chart.AresHeaderTemplate, snapshot.AresHeaderTemplate))
            return false;

        // Tracks below AresTrackCount are already represented by the file's fixed
        // track table. Creating an empty in-memory ChartTrack for placement and
        // leaving it empty after deletion does not change the chart that gets saved.
        // Ignore those empty placeholders while still comparing every populated
        // track and any empty track that would extend the serialized track table.
        var savedTracks = snapshot.Tracks
            .Where(track => track.Events.Count > 0 || track.Track.TrackIndex >= snapshot.AresTrackCount)
            .OrderBy(track => track.Track.TrackIndex)
            .ToList();
        var currentTracks = chart.Tracks
            .Where(track => track.Events.Count > 0 || track.TrackIndex >= chart.AresTrackCount)
            .OrderBy(track => track.TrackIndex)
            .ToList();
        if (savedTracks.Count != currentTracks.Count)
            return false;
        var savedEventStates = snapshot.Events.ToDictionary(state => state.Event);
        for (var i = 0; i < savedTracks.Count; i++)
        {
            var savedTrack = savedTracks[i];
            var currentTrack = currentTracks[i];
            if (savedTrack.Track.TrackIndex != currentTrack.TrackIndex ||
                savedTrack.Events.Count != currentTrack.Events.Count)
                return false;
            for (var j = 0; j < savedTrack.Events.Count; j++)
            {
                if (!savedEventStates.TryGetValue(savedTrack.Events[j], out var eventState) ||
                    !EditorEventMatchesSnapshot(currentTrack.Events[j], eventState))
                    return false;
            }
        }

        return chart.InstrumentNames.SequenceEqual(snapshot.InstrumentNames) &&
               chart.InstrumentNameHuffmanFrequencies.SequenceEqual(snapshot.InstrumentNameHuffmanFrequencies) &&
               chart.InstrumentNameReferences.SequenceEqual(snapshot.InstrumentNameReferences) &&
               chart.Instruments.SequenceEqual(snapshot.Instruments) &&
               EditorNoteGroupsEqual(chart.NoteGroups, snapshot.NoteGroups) &&
               EditorNoteGroupInstancesEqual(chart.NoteGroupInstances, snapshot.NoteGroupInstances);
    }

    private void EstablishEditorDirtyBaselineAfterChartLoad()
    {
        if (!_chartEditorMode || _chart is null)
        {
            _editorHasUnsavedChanges = false;
            return;
        }

        if (!_switchingEditorDifficulty)
        {
            _editorSavedChartBaselines.Clear();
            _editorHasAuxiliaryUnsavedChanges = false;
        }

        if (!_editorSavedChartBaselines.ContainsKey(_chart))
        {
            var baseline = CaptureEditorChartSnapshot();
            if (baseline is not null)
                _editorSavedChartBaselines[_chart] = baseline;
        }

        RefreshEditorDirtyState();
    }

    private void RefreshEditorDirtyState()
    {
        // A difficulty can remain edited after the user switches to another one.
        // Check every chart that has acquired a baseline, not only the active tab.
        var chartDiffersFromSavedBaseline = _chartEditorMode &&
            _editorSavedChartBaselines.Any(pair => !EditorChartMatchesSnapshot(pair.Value, pair.Key));
        _editorHasUnsavedChanges = _chartEditorMode &&
            (_editorHasAuxiliaryUnsavedChanges || chartDiffersFromSavedBaseline);
    }

    private void EditorChartBaselineSaved()
    {
        _editorSavedChartBaselines.Clear();
        if (_chartEditorMode && _chart is not null && CaptureEditorChartSnapshot() is { } baseline)
            _editorSavedChartBaselines[_chart] = baseline;
        _editorHasAuxiliaryUnsavedChanges = false;
        RefreshEditorDirtyState();
    }

    private bool EditorSelectionMatchesSnapshot(EditorChartSnapshot snapshot)
    {
        var selectedEvent = TryGetEditorEvent(_selectedEventKey);
        if (!ReferenceEquals(selectedEvent, snapshot.SelectedEvent) ||
            _selectedBeatTick != snapshot.SelectedBeatTick ||
            _selectedBeatTrack != snapshot.SelectedBeatTrack ||
            !ReferenceEquals(_editorSelectedTempoEvent, snapshot.SelectedTempoEvent) ||
            !ReferenceEquals(_editorSelectedLinkRoot, snapshot.SelectedLinkRoot) ||
            _editorSelectedHoldEnd != snapshot.SelectedHoldEnd ||
            _editorPanelsSelectedEmptyCell != snapshot.PanelsSelectedEmptyCell ||
            _editorSelectReferenceLineTick != snapshot.SelectReferenceLineTick ||
            _editorSelectReferenceTickOverride != snapshot.SelectReferenceTickOverride ||
            _editorMultiSelectedEvents.Count != snapshot.MultiSelectedEvents.Count ||
            _editorSelectedGridCells.Count != snapshot.SelectedGridCells.Count)
            return false;

        return _editorMultiSelectedEvents.SetEquals(snapshot.MultiSelectedEvents) &&
               _editorSelectedGridCells.SetEquals(snapshot.SelectedGridCells);
    }

    private void CommitEditorSelectionUndoSnapshot(EditorChartSnapshot? snapshot)
    {
        if (snapshot is not null && EditorChartMatchesSnapshot(snapshot) && !EditorSelectionMatchesSnapshot(snapshot))
            PushEditorUndoSnapshot(snapshot);
    }

    private void ClearEditorPendingRectangleUndoSnapshot()
    {
        _editorPendingRectangleUndoSnapshot = null;
        _editorPendingRectangleGroupSynchronizationApplied = false;
    }

    private bool CommitEditorUndoSnapshot(EditorChartSnapshot? snapshot, ISet<int>? preserveEmptyGroupIds = null,
        bool groupChangesAlreadySynchronized = false)
    {
        if (snapshot is null)
            return true;

        // Track lists are the authoritative mutation source. Anchor cleanup and
        // pruning read them directly; EDIT GROUP synchronization refreshes its
        // own flattened lookup. The normal edit refresh rebuilds chart-wide event
        // and render caches once, after the history snapshot is committed.

        // A lane click is initially held open as a possible marquee gesture. In
        // EDIT GROUP, synchronize its newly placed note immediately instead of
        // waiting for mouse release: otherwise the source note can appear alone
        // until a later drag happens to commit another edit. If the pointer turns
        // into a marquee, RestoreEditorChartSnapshot still rolls the whole edit
        // (including its counterpart notes and member metadata) back atomically.
        var synchronizedGroupChanges = groupChangesAlreadySynchronized;
        if (snapshot.EventDeltaOnly)
        {
            // Event deltas are used only for ungrouped note operations. They do not
            // alter reusable group definitions, instances, or member anchors.
        }
        else if (_editorNoteGroupEditMode && !groupChangesAlreadySynchronized)
        {
            if (!SynchronizeEditorNoteGroupChanges(snapshot))
                return false;
            synchronizedGroupChanges = true;
        }

        // Preserve the click-versus-marquee behavior for history. When this
        // snapshot is committed again on release, skip a second group sync.
        if (_editorRectSelectionArmed)
        {
            _editorPendingRectangleUndoSnapshot = snapshot;
            _editorPendingRectangleGroupSynchronizationApplied = synchronizedGroupChanges;
            return true;
        }

        if (!snapshot.EventDeltaOnly)
        {
            if (!_editorNoteGroupEditMode)
                RefreshEditorNoteGroupInstanceAnchors(snapshot);

            PruneEmptyEditorNoteGroupInstances(preserveEmptyGroupIds);
        }

        PushEditorUndoSnapshot(snapshot);
        return true;
    }

    private void PushEditorUndoSnapshot(EditorChartSnapshot snapshot)
    {
        if (!snapshot.SelectionOnly)
            MarkEditorDirty(recomputeSavedBaseline: false);
        _editorUndoStack.Add(snapshot);
        if (_editorUndoStack.Count > EditorHistoryLimit)
            _editorUndoStack.RemoveAt(0);
        _editorRedoStack.Clear();
    }

    private void CommitEditorPanelEventUndoSnapshot(EditorChartSnapshot? snapshot)
    {
        // Panel animation placement changes one auxiliary event only. Keep this
        // O(1) undo record instead of copying a potentially large panel track.
        if (snapshot is not null)
            PushEditorUndoSnapshot(snapshot);
    }

    private void RestoreEditorChartSnapshot(EditorChartSnapshot snapshot)
    {
        if (_chart is null || !ReferenceEquals(_chart, snapshot.SourceChart))
            return;

        if (snapshot.SelectionOnly)
        {
            RestoreEditorSelectionSnapshot(snapshot);
            return;
        }

        if (snapshot.PanelEventOnly)
        {
            if (snapshot.PanelTrack is null || snapshot.PanelEvent is null)
                return;

            var currentTrack = _chart.FindTrack(snapshot.PanelTrackIndex);
            if (snapshot.PanelTrackShouldExist)
            {
                if (currentTrack is not null && !ReferenceEquals(currentTrack, snapshot.PanelTrack))
                    _chart.Tracks.Remove(currentTrack);
                if (!_chart.Tracks.Contains(snapshot.PanelTrack))
                {
                    var insertAt = _chart.Tracks.FindIndex(track => track.TrackIndex > snapshot.PanelTrackIndex);
                    if (insertAt < 0) _chart.Tracks.Add(snapshot.PanelTrack);
                    else _chart.Tracks.Insert(insertAt, snapshot.PanelTrack);
                }

                var eventIndex = snapshot.PanelTrack.Events.FindIndex(ev => ReferenceEquals(ev, snapshot.PanelEvent));
                if (snapshot.PanelEventPresent && eventIndex < 0)
                    InsertEditorPanelEventIntoTrack(snapshot.PanelTrack, snapshot.PanelEvent);
                else if (!snapshot.PanelEventPresent && eventIndex >= 0)
                    snapshot.PanelTrack.Events.RemoveAt(eventIndex);
            }
            else if (currentTrack is not null)
            {
                // The pre-placement state had no track at this index. Remove
                // whichever instance currently occupies it before rebuilding caches.
                _chart.Tracks.Remove(currentTrack);
            }

            if (snapshot.PanelEventPresent)
            {
                if (!_chart.Events.Any(ev => ReferenceEquals(ev, snapshot.PanelEvent)))
                    _chart.Events.Add(snapshot.PanelEvent);
            }
            else
            {
                _chart.Events.RemoveAll(ev => ReferenceEquals(ev, snapshot.PanelEvent));
            }

            _selectedEventKey = snapshot.SelectedEvent is not null ? KeyOf(snapshot.SelectedEvent) : null;
            _selectedBeatTick = snapshot.SelectedBeatTick;
            _selectedBeatTrack = snapshot.SelectedBeatTrack;
            _editorSelectedTempoEvent = snapshot.SelectedTempoEvent;
            _editorSelectedLinkRoot = snapshot.SelectedLinkRoot;
            _editorSelectedHoldEnd = snapshot.SelectedHoldEnd;
            ClearEditorMultiSelection();
            foreach (var selected in snapshot.MultiSelectedEvents)
                AddEditorMultiSelectedEvent(selected);
            _editorSelectedGridCells.Clear();
            foreach (var cell in snapshot.SelectedGridCells)
                _editorSelectedGridCells.Add(cell);
            _editorPanelsSelectedEmptyCell = snapshot.PanelsSelectedEmptyCell;
            _editorSelectReferenceLineTick = snapshot.SelectReferenceLineTick;
            _editorSelectReferenceTickOverride = snapshot.SelectReferenceTickOverride;
            ClearEditorPendingRectangleUndoSnapshot();
            _editorSelectionPendingUndoSnapshot = null;
            _editorRectSelectionArmed = false;
            _editorRectSelecting = false;
            _editorDraggedEvent = null;
            _editorDragUndoSnapshot = null;
            _editorDragIsHoldEnd = false;
            _editorDragChanged = false;
            RefreshAfterEditorMutation();
            StoreActiveEditorDifficulty();
            return;
        }

        if (snapshot.EventDeltaOnly)
        {
            RestoreEditorEventDeltaSnapshot(snapshot);
            return;
        }

        foreach (var state in snapshot.Events)
        {
            state.Event.Tick = state.Tick;
            state.Event.Track = state.Track;
            state.Event.Attr = state.Attr;
            state.Event.Duration = state.Duration;
            state.Event.RawType = state.RawType;
            state.Event.TypeIndex = state.TypeIndex;
            state.Event.Kind = state.Kind;
            state.Event.NoteType = state.NoteType;
            state.Event.EventIndex = state.EventIndex;
            state.Event.LinePair = state.LinePair;
            state.Event.Value = state.Value;
            state.Event.Instrument = state.Instrument;
            state.Event.Velocity = state.Velocity;
            state.Event.Pan = state.Pan;
            state.Event.TwinPairId = state.TwinPairId;
            state.Event.LoopReconstructed = state.LoopReconstructed;
            state.Event.LoopPhase = state.LoopPhase;
            state.Event.NoteGroupId = state.NoteGroupId;
            state.Event.NoteGroupInstanceId = state.NoteGroupInstanceId;
            state.Event.NoteGroupMemberId = state.NoteGroupMemberId;
        }

        _chart.PreviewStartMilliseconds = snapshot.PreviewStartMilliseconds;
        _chart.PreviewEndMilliseconds = snapshot.PreviewEndMilliseconds;
        _editorPreviewRangeInitialized = _chart.HasPreviewRange;
        _chart.MetadataBpm = snapshot.MetadataBpm;
        _chart.InitialTempo = snapshot.InitialTempo;
        _chart.PlayTick = snapshot.PlayTick;
        _chart.PlayTime = snapshot.PlayTime;
        _chart.AudioOriginTick = snapshot.AudioOriginTick;
        _chart.RawMagic = snapshot.RawMagic;
        _chart.AresTrackCount = snapshot.AresTrackCount;
        _chart.AresHeaderTemplate = snapshot.AresHeaderTemplate?.ToArray();
        _loadedAresHeaderTemplate = snapshot.LoadedAresHeaderTemplate?.ToArray();
        _chart.InstrumentNames.Clear();
        _chart.InstrumentNames.AddRange(snapshot.InstrumentNames);
        _chart.InstrumentNameHuffmanFrequencies.Clear();
        _chart.InstrumentNameHuffmanFrequencies.AddRange(snapshot.InstrumentNameHuffmanFrequencies);
        _chart.InstrumentNameReferences.Clear();
        _chart.InstrumentNameReferences.AddRange(snapshot.InstrumentNameReferences);
        _chart.Instruments.Clear();
        _chart.Instruments.AddRange(snapshot.Instruments);
        _chart.NoteGroups.Clear();
        _chart.NoteGroups.AddRange(CloneEditorNoteGroups(snapshot.NoteGroups));
        _chart.NoteGroupInstances.Clear();
        _chart.NoteGroupInstances.AddRange(snapshot.NoteGroupInstances);
        foreach (var currentTrack in _chart.Tracks)
            currentTrack.Events.Clear();
        _chart.Tracks.Clear();
        foreach (var trackState in snapshot.Tracks)
        {
            trackState.Track.Events.Clear();
            trackState.Track.Events.AddRange(trackState.Events);
            _chart.Tracks.Add(trackState.Track);
        }

        _selectedEventKey = snapshot.SelectedEvent is not null ? KeyOf(snapshot.SelectedEvent) : null;
        _selectedBeatTick = snapshot.SelectedBeatTick;
        _selectedBeatTrack = snapshot.SelectedBeatTrack;
        _editorSelectedTempoEvent = snapshot.SelectedTempoEvent;
        _editorSelectedLinkRoot = snapshot.SelectedLinkRoot;
        _editorSelectedHoldEnd = snapshot.SelectedHoldEnd;
        ClearEditorMultiSelection();
        foreach (var selected in snapshot.MultiSelectedEvents)
            AddEditorMultiSelectedEvent(selected);
        _editorSelectedGridCells.Clear();
        foreach (var cell in snapshot.SelectedGridCells) _editorSelectedGridCells.Add(cell);
        _editorPanelsSelectedEmptyCell = snapshot.PanelsSelectedEmptyCell;
        _editorSelectReferenceLineTick = snapshot.SelectReferenceLineTick;
        _editorSelectReferenceTickOverride = snapshot.SelectReferenceTickOverride;
        ClearEditorPendingRectangleUndoSnapshot();
        _editorSelectionPendingUndoSnapshot = null;
        _editorRectSelectionArmed = false;
        _editorRectSelecting = false;
        _editorDraggedEvent = null;
        _editorDragUndoSnapshot = null;
        _editorDragIsHoldEnd = false;
        _editorDragChanged = false;
        RefreshAfterEditorMutation();
        SyncSharedEditorMetadataToDifficultyStates();
        StoreActiveEditorDifficulty();
    }

    private static void CopyEditorSelectionState(
        EditorChartSnapshot target, EditorChartSnapshot selection)
    {
        target.SelectedEvent = selection.SelectedEvent;
        target.SelectedBeatTick = selection.SelectedBeatTick;
        target.SelectedBeatTrack = selection.SelectedBeatTrack;
        target.SelectedTempoEvent = selection.SelectedTempoEvent;
        target.SelectedLinkRoot = selection.SelectedLinkRoot;
        target.SelectedHoldEnd = selection.SelectedHoldEnd;
        target.MultiSelectedEvents = selection.MultiSelectedEvents.ToList();
        target.SelectedGridCells = selection.SelectedGridCells.ToList();
        target.PanelsSelectedEmptyCell = selection.PanelsSelectedEmptyCell;
        target.SelectReferenceLineTick = selection.SelectReferenceLineTick;
        target.SelectReferenceTickOverride = selection.SelectReferenceTickOverride;
    }

    private void RestoreEditorEventDeltaSnapshot(EditorChartSnapshot snapshot)
    {
        if (_chart is null)
            return;

        var requiresFullChartRefresh = snapshot.Events.Any(state =>
            IsEditorSuperFlickAttribute(state.Attr) || state.Event.IsSuperFlick);
        var recalculateLastPlayableTick = ShouldRecalculateEditorLastPlayableTick(snapshot.Events);
        var eventsNeedingListRestore = snapshot.Events.Where(state =>
                state.Tick != state.Event.Tick || state.Track != state.Event.Track ||
                state.EventIndex != state.Event.EventIndex || state.Kind != state.Event.Kind ||
                state.RawType != state.Event.RawType ||
                snapshot.DeltaPresentEvents.Contains(state.Event) !=
                    _chart.Tracks.Any(track => track.Events.Contains(state.Event)))
            .Select(state => state.Event).Distinct().ToArray();
        var affectedEvents = eventsNeedingListRestore.ToHashSet();
        // Track snapshots may cover every possible destination lane during a drag.
        // List membership restoration still touches only events that actually
        // moved/appeared/disappeared, but the subsequent cache refresh must include
        // every snapshotted event because non-ordering fields (or the live-drag
        // cache's temporary key) can also have changed.
        var eventsToRefresh = snapshot.Events
            .Select(state => state.Event)
            .Distinct()
            .ToArray();
        var affectedTrackIndices = snapshot.Events
            .SelectMany(state => new[] { state.Track, state.Event.Track })
            .Distinct().ToArray();
        foreach (var trackIndex in affectedTrackIndices)
            _chart.FindTrack(trackIndex)?.Events.RemoveAll(affectedEvents.Contains);

        foreach (var trackState in snapshot.DeltaTrackStates)
        {
            var currentTrack = _chart.FindTrack(trackState.TrackIndex);
            if (trackState.Track is null)
            {
                // This track did not exist before the edit. Remove an empty track
                // created by the edit, but never discard unrelated remaining events.
                if (currentTrack is not null && currentTrack.Events.Count == 0)
                    _chart.Tracks.Remove(currentTrack);
                continue;
            }

            var savedTrack = trackState.Track;
            if (currentTrack is not null && !ReferenceEquals(currentTrack, savedTrack))
            {
                // Preserve non-delta events if a track object was replaced while
                // applying the edit, then restore the original track identity.
                foreach (var ev in currentTrack.Events)
                    if (!affectedEvents.Contains(ev) && !savedTrack.Events.Contains(ev))
                        savedTrack.Events.Add(ev);
                _chart.Tracks.Remove(currentTrack);
            }
            if (!_chart.Tracks.Contains(savedTrack))
            {
                var insertAt = _chart.Tracks.FindIndex(track => track.TrackIndex > savedTrack.TrackIndex);
                if (insertAt < 0) _chart.Tracks.Add(savedTrack);
                else _chart.Tracks.Insert(insertAt, savedTrack);
            }
        }

        foreach (var state in snapshot.Events)
        {
            RestoreEditorEventSnapshot(state);
            if (!snapshot.DeltaPresentEvents.Contains(state.Event))
                continue;

            var targetTrack = _chart.FindTrack(state.Track);
            if (targetTrack is null)
            {
                targetTrack = new ChartTrack { TrackIndex = state.Track };
                var insertAt = _chart.Tracks.FindIndex(track => track.TrackIndex > state.Track);
                if (insertAt < 0) _chart.Tracks.Add(targetTrack);
                else _chart.Tracks.Insert(insertAt, targetTrack);
            }
            if (!targetTrack.Events.Contains(state.Event))
                targetTrack.Events.Add(state.Event);
        }

        _selectedEventKey = snapshot.SelectedEvent is not null ? KeyOf(snapshot.SelectedEvent) : null;
        _selectedBeatTick = snapshot.SelectedBeatTick;
        _selectedBeatTrack = snapshot.SelectedBeatTrack;
        _editorSelectedTempoEvent = snapshot.SelectedTempoEvent;
        _editorSelectedLinkRoot = snapshot.SelectedLinkRoot;
        _editorSelectedHoldEnd = snapshot.SelectedHoldEnd;
        ClearEditorMultiSelection();
        foreach (var selected in snapshot.MultiSelectedEvents)
            AddEditorMultiSelectedEvent(selected);
        _editorSelectedGridCells.Clear();
        foreach (var cell in snapshot.SelectedGridCells)
            _editorSelectedGridCells.Add(cell);
        _editorPanelsSelectedEmptyCell = snapshot.PanelsSelectedEmptyCell;
        _editorSelectReferenceLineTick = snapshot.SelectReferenceLineTick;
        _editorSelectReferenceTickOverride = snapshot.SelectReferenceTickOverride;
        ClearEditorPendingRectangleUndoSnapshot();
        _editorSelectionPendingUndoSnapshot = null;
        _editorRectSelectionArmed = false;
        _editorRectSelecting = false;
        _editorDraggedEvent = null;
        _editorDragUndoSnapshot = null;
        _editorDragIsHoldEnd = false;
        _editorDragChanged = false;
        if (requiresFullChartRefresh)
            RefreshAfterEditorMutation();
        else
        {
            var incrementalPointRenderUpdate = snapshot.Events.Count <= 8 && snapshot.Events.All(state =>
                state.NoteType == NoteType.Single && !IsEditorSuperFlickAttribute(state.Attr) &&
                state.Event.NoteType == NoteType.Single && !state.Event.IsSuperFlick);
            RefreshAfterEditorNoteMutation(snapshot.DeltaRebuildTwinPairing,
                snapshot.DeltaRebuildLinkGraph, eventsToRefresh, affectedTrackIndices,
                recalculateLastPlayableTick, incrementalPointRenderUpdate);
        }
        SyncSharedEditorMetadataToDifficultyStates();
        StoreActiveEditorDifficulty();
    }

    private void RestoreEditorSelectionSnapshot(EditorChartSnapshot snapshot)
    {
        _selectedEventKey = IsEditorEventInCurrentChart(snapshot.SelectedEvent)
            ? KeyOf(snapshot.SelectedEvent!)
            : null;
        _selectedBeatTick = snapshot.SelectedBeatTick;
        _selectedBeatTrack = snapshot.SelectedBeatTrack;
        _editorSelectedTempoEvent = IsEditorEventInCurrentChart(snapshot.SelectedTempoEvent)
            ? snapshot.SelectedTempoEvent
            : null;
        _editorSelectedLinkRoot = IsEditorEventInCurrentChart(snapshot.SelectedLinkRoot)
            ? snapshot.SelectedLinkRoot
            : null;
        _editorSelectedHoldEnd = snapshot.SelectedHoldEnd;

        ClearEditorMultiSelection();
        foreach (var selected in snapshot.MultiSelectedEvents)
        {
            if (IsEditorEventInCurrentChart(selected))
                AddEditorMultiSelectedEvent(selected);
        }

        _editorSelectedGridCells.Clear();
        foreach (var cell in snapshot.SelectedGridCells)
            _editorSelectedGridCells.Add(cell);
        _editorPanelsSelectedEmptyCell = snapshot.PanelsSelectedEmptyCell;
        _editorSelectReferenceLineTick = snapshot.SelectReferenceLineTick;
        _editorSelectReferenceTickOverride = snapshot.SelectReferenceTickOverride;

        ClearEditorPendingRectangleUndoSnapshot();
        _editorSelectionPendingUndoSnapshot = null;
        _editorRectSelectionArmed = false;
        _editorRectSelecting = false;
        _editorDraggedEvent = null;
        _editorDragUndoSnapshot = null;
        _editorDragIsHoldEnd = false;
        _editorDragChanged = false;
        _editorHoverCacheValid = false;
        RefreshEditorNoteGroupLibrarySelectionFromNotes();
    }

    private void UndoEditorChange()
    {
        if (_chart is null || _editorUndoStack.Count == 0)
        {
            _statusMessage = "Nothing to undo.";
            return;
        }

        var previous = _editorUndoStack[^1];
        var current = previous.PanelEventOnly
            ? CaptureCurrentEditorPanelEventSnapshot(previous)
            : previous.EventDeltaOnly
                ? CaptureCurrentEditorEventDeltaSnapshot(previous)
                : CaptureEditorChartSnapshot(selectionOnly: previous.SelectionOnly);
        _editorUndoStack.RemoveAt(_editorUndoStack.Count - 1);
        if (current is not null)
            _editorRedoStack.Add(current);
        RestoreEditorChartSnapshot(previous);
        if (!previous.SelectionOnly)
            MarkEditorDirty();
        _statusMessage = "Undo complete.";
    }

    private void RedoEditorChange()
    {
        if (_chart is null || _editorRedoStack.Count == 0)
        {
            _statusMessage = "Nothing to redo.";
            return;
        }

        var next = _editorRedoStack[^1];
        var current = next.PanelEventOnly
            ? CaptureCurrentEditorPanelEventSnapshot(next)
            : next.EventDeltaOnly
                ? CaptureCurrentEditorEventDeltaSnapshot(next)
                : CaptureEditorChartSnapshot(selectionOnly: next.SelectionOnly);
        _editorRedoStack.RemoveAt(_editorRedoStack.Count - 1);
        if (current is not null)
            _editorUndoStack.Add(current);
        RestoreEditorChartSnapshot(next);
        if (!next.SelectionOnly)
            MarkEditorDirty();
        _statusMessage = "Redo complete.";
    }

    private List<ChartEvent> GetSelectedEditorNotes(bool expandLinkFamilies, bool orderByTrack = true)
    {
        if (_chart is null)
            return new List<ChartEvent>();

        var selected = _editorMultiSelectedEvents
            .Where(ev => IsEditorEventInCurrentChart(ev) && ev.Kind == "Note" && ev.RawType == 1)
            .ToHashSet();
        if (selected.Count == 0 && _selectedEventKey.HasValue &&
            TryGetEditorEvent(_selectedEventKey) is { } single &&
            single.Kind == "Note" && single.RawType == 1)
        {
            selected.Add(single);
        }

        ExpandEditorNoteGroupSelection(selected);

        if (expandLinkFamilies)
        {
            foreach (var note in selected.ToList())
            {
                var root = GetEditorLinkRoot(note);
                if (root is null || root.NoteType != NoteType.LinkHead)
                    continue;
                foreach (var node in GetEditorLinkChainNodes(root))
                    selected.Add(node);
            }
        }

        // Link tools build their route from tick groups and note semantics. Do
        // not pre-sort those selections by lane: track numbers are identifiers,
        // not a path direction. Other editor actions keep the legacy stable order.
        return orderByTrack
            ? selected.OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track).ThenBy(ev => ev.EventIndex).ToList()
            : selected.OrderBy(ev => ev.Tick).ToList();
    }

    private void CopySelectedEditorPanelNotes()
    {
        var notes = GetSelectedEditorNotes(expandLinkFamilies: false)
            .Where(ev => ev.Kind == "Note" && ev.RawType == 1 &&
                        ev.Track is >= 17 and <= 20 && ev.Attr is >= 101 and <= 108)
            .Distinct()
            .OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track).ThenBy(ev => ev.EventIndex)
            .ToList();
        if (notes.Count == 0)
        {
            _statusMessage = "Select one or more animation notes to copy.";
            return;
        }

        // Panel animation cues are independent timeline events, not gameplay
        // note-group members or Link chains. Copy their exact four-track layout
        // and durations without passing them through gameplay group expansion.
        var anchorTick = notes.Min(ev => ev.Tick);
        var anchorTrack = notes.Min(ev => ev.Track);
        var selectedAnchor = _selectedEventKey.HasValue
            ? notes.FirstOrDefault(ev => KeyOf(ev) == _selectedEventKey.Value)
            : null;
        selectedAnchor ??= notes[0];
        _editorClipboardAnchorTickOffset = selectedAnchor.Tick - anchorTick;
        _editorClipboardAnchorTrackOffset = selectedAnchor.Track - anchorTrack;

        _editorClipboard.Clear();
        _editorClipboardGroupSources.Clear();
        _editorClipboardGroupSourceInstance = null;
        _editorClipboardIsNoteGroupInstance = false;
        foreach (var note in notes)
        {
            _editorClipboard.Add(new EditorClipboardEntry(
                note.Tick - anchorTick,
                note.Track - anchorTrack,
                note.Track - anchorTrack,
                note.Track,
                note.Attr,
                Math.Max(1, note.Duration),
                note.Instrument,
                note.Velocity,
                note.Pan,
                note.NoteType,
                -1,
                -1,
                false,
                null,
                null,
                null,
                null,
                null,
                note));
        }

        _statusMessage = $"Copied {notes.Count} animation note(s).";
    }

    private void CopySelectedEditorNotes()
    {
        if (_editorPanelsViewEnabled)
        {
            CopySelectedEditorPanelNotes();
            return;
        }

        // Group instances are copied as complete logical patterns, even when the
        // current selection contains only one member or several different groups.
        // The definition/instance snapshots also make CUT independent of whether
        // pruning removes the original library entry after the notes are deleted.
        var notes = GetSelectedEditorNotes(expandLinkFamilies: false);
        if (notes.Count == 0)
        {
            _statusMessage = "Select one or more notes to copy.";
            return;
        }

        var groupSources = new List<EditorClipboardGroupSource>();
        var capturedGroupInstances = new HashSet<(int GroupId, int InstanceId)>();
        bool TryCaptureGroupInstances(IEnumerable<ChartEvent> candidates, out string? error)
        {
            error = null;
            if (_chart is null)
                return true;

            var keys = candidates
                .Where(ev => ev.NoteGroupId.HasValue && ev.NoteGroupInstanceId.HasValue)
                .Select(ev => (GroupId: ev.NoteGroupId!.Value, InstanceId: ev.NoteGroupInstanceId!.Value))
                .Distinct()
                .ToList();
            foreach (var key in keys)
            {
                if (!capturedGroupInstances.Add(key))
                    continue;

                var definition = _chart.NoteGroups.FirstOrDefault(group => group.Id == key.GroupId);
                var instance = _chart.NoteGroupInstances.FirstOrDefault(candidate =>
                    candidate.GroupId == key.GroupId && candidate.InstanceId == key.InstanceId);
                var instanceNotes = _chart.Events.Where(ev => ev.Kind == "Note" && ev.RawType == 1 &&
                    ev.NoteGroupId == key.GroupId && ev.NoteGroupInstanceId == key.InstanceId &&
                    ev.Track is >= 0 and <= 15).ToList();
                var copiedIds = instanceNotes.Where(ev => ev.NoteGroupMemberId.HasValue)
                    .Select(ev => ev.NoteGroupMemberId!.Value).ToHashSet();
                var definedIds = definition?.Members.Select(member => member.Id).ToHashSet();
                if (definition is null || instance is null || definedIds is null ||
                    instanceNotes.Any(ev => !ev.NoteGroupMemberId.HasValue) ||
                    instanceNotes.Count != definition.Members.Count || copiedIds.Count != instanceNotes.Count ||
                    !copiedIds.SetEquals(definedIds))
                {
                    error = $"Copy cancelled: group G{key.GroupId} instance {key.InstanceId} is incomplete.";
                    return false;
                }

                notes.AddRange(instanceNotes);
                var definitionSnapshot = new NoteGroupDefinition
                {
                    Id = definition.Id,
                    Name = definition.Name,
                    Members = definition.Members.ToList()
                };
                var instanceSnapshot = instance with { TrackMap = instance.TrackMap?.ToList() };
                groupSources.Add(new EditorClipboardGroupSource(definitionSnapshot, instanceSnapshot, 0));
            }
            return true;
        }

        if (!TryCaptureGroupInstances(notes, out var groupCaptureError))
        {
            _statusMessage = groupCaptureError;
            return;
        }
        if (notes.Any(ev =>
                ev.NoteGroupId.HasValue != ev.NoteGroupInstanceId.HasValue ||
                (ev.NoteGroupId.HasValue && !ev.NoteGroupMemberId.HasValue) ||
                (ev.NoteGroupMemberId.HasValue && !ev.NoteGroupId.HasValue)))
        {
            _statusMessage = "Copy cancelled: one or more notes have incomplete group membership metadata.";
            return;
        }

        // Close over complete Link families and complete group instances. A
        // chain can connect members from separate groups, and adding that whole
        // group can reveal another chain connected to a different instance.
        // Repeat until both relationships stop expanding so the clipboard never
        // contains a partial group or a partial Link family.
        while (true)
        {
            var noteCountBeforeExpansion = notes.Count;
            foreach (var note in notes.ToList())
            {
                var root = GetEditorLinkRoot(note);
                if (root is null || root.NoteType != NoteType.LinkHead)
                    continue;
                foreach (var node in GetEditorLinkChainNodes(root))
                    if (!notes.Contains(node))
                        notes.Add(node);
            }
            if (!TryCaptureGroupInstances(notes, out groupCaptureError))
            {
                _statusMessage = groupCaptureError;
                return;
            }
            if (notes.Count == noteCountBeforeExpansion)
                break;
        }

        notes = notes.Distinct()
            .OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track).ThenBy(ev => ev.EventIndex).ToList();
        var anchorTick = notes.Min(ev => ev.Tick);
        var anchorTrack = notes.Min(ev => ev.Track);
        var selectedAnchor = _selectedEventKey.HasValue
            ? notes.FirstOrDefault(ev => KeyOf(ev) == _selectedEventKey.Value)
            : null;
        selectedAnchor ??= notes[0];
        _editorClipboardAnchorTickOffset = selectedAnchor.Tick - anchorTick;
        _editorClipboardAnchorTrackOffset = selectedAnchor.Track - anchorTrack;

        // Group start ticks are stored relative to the same bottom-note anchor as
        // the copied pattern, so all instances keep their relative arrangement
        // when a multi-group selection is pasted elsewhere.
        groupSources = groupSources.Select(source => source with
        {
            StartTickOffset = source.Instance.StartTick - anchorTick
        }).ToList();

        var chainGroups = new Dictionary<ChartEvent, int>();
        var nextGroup = 0;
        foreach (var note in notes)
        {
            var root = GetEditorLinkRoot(note);
            if (root is null || root.NoteType != NoteType.LinkHead)
                continue;
            if (!chainGroups.ContainsKey(root))
                chainGroups[root] = nextGroup++;
        }

        _editorClipboard.Clear();
        _editorClipboardGroupSources.Clear();
        _editorClipboardGroupSources.AddRange(groupSources);
        _editorClipboardGroupSourceInstance = groupSources.Count == 1 ? groupSources[0].Instance : null;
        _editorClipboardIsNoteGroupInstance = groupSources.Count > 0;
        foreach (var note in notes)
        {
            var root = GetEditorLinkRoot(note);
            var chainGroup = root is not null && chainGroups.TryGetValue(root, out var linkGroupIndex)
                ? linkGroupIndex : -1;
            var originalPosition = GearState.Position(note.Track);
            var groupSource = note.NoteGroupId is int groupId && note.NoteGroupInstanceId is int instanceId
                ? groupSources.FirstOrDefault(source => source.Instance.GroupId == groupId &&
                    source.Instance.InstanceId == instanceId)
                : null;
            var canonicalTrackOffset = groupSource is not null
                ? GetCanonicalEditorNoteGroupTrack(groupSource.Instance, note.Track) - groupSource.Instance.StartTrack
                : note.Track - anchorTrack;
            _editorClipboard.Add(new EditorClipboardEntry(
                note.Tick - anchorTick,
                note.Track - anchorTrack,
                canonicalTrackOffset,
                note.Track,
                note.Attr,
                Math.Max(1, note.Duration),
                note.Instrument,
                note.Velocity,
                note.Pan,
                note.NoteType,
                chainGroup,
                note.LinePair,
                note.IsSuperFlick,
                originalPosition?.Side,
                originalPosition?.Bank,
                note.NoteGroupId,
                note.NoteGroupInstanceId,
                note.NoteGroupMemberId,
                note));
        }

        _statusMessage = groupSources.Count switch
        {
            1 => $"Copied Group G{groupSources[0].Instance.GroupId} instance {groupSources[0].Instance.InstanceId} ({notes.Count} note(s)).",
            > 1 => $"Copied {groupSources.Count} group instance(s) and {notes.Count} note(s).",
            _ => $"Copied {notes.Count} note(s)."
        };
    }

    private void CutSelectedEditorNotes()
    {
        if (_chart is null)
            return;
        var notes = GetSelectedEditorNotes(expandLinkFamilies: true);
        if (notes.Count == 0)
        {
            _statusMessage = "Select one or more notes to cut.";
            return;
        }

        CopySelectedEditorNotes();
        var removed = notes.ToHashSet();
        var affectedBeforeCut = GetEditorDeleteAffectedEvents(removed);
        var snapshot = CaptureEditorNoteEditSnapshot(affectedBeforeCut,
            affectedBeforeCut.Select(ev => ev.Track), rebuildTwinPairing: true, rebuildLinkGraph: true);
        var replacement = DeleteEditorEvents(removed);
        CommitEditorUndoSnapshot(snapshot);
        ClearEditorMultiSelection();
        _selectedEventKey = replacement is not null ? KeyOf(replacement) : null;
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorSelectedHoldEnd = false;
        RefreshAfterEditorNoteSnapshot(snapshot, rebuildTwinPairing: true, rebuildLinkGraph: true);
        _editorSelectedLinkRoot = replacement is not null ? GetEditorLinkRoot(replacement) : null;
        _statusMessage = $"Cut {notes.Count} note(s).";
    }

    private int GetEditorClipboardGroupPatternEndTick(int snap)
    {
        if (_editorClipboard.Count == 0 || _editorClipboardGroupSources.Count == 0)
            return 0;

        // The clipboard anchor is shared by every copied group instance. Derive
        // it from the captured instance metadata instead of live chart events so
        // CUT and cross-difficulty paste use the same immutable source pattern.
        var source = _editorClipboardGroupSources[0];
        var clipboardAnchorTick = (long)source.Instance.StartTick - source.StartTickOffset;
        var bottomOffset = _editorClipboard.Max(entry =>
            (long)entry.TickOffset +
            (entry.NoteType is NoteType.SingleLong or NoteType.Vibrato or NoteType.LinkHead or NoteType.LinkTail
                ? Math.Max(1, entry.Duration) : 0));
        var endTick = clipboardAnchorTick + bottomOffset + Math.Max(1, snap);
        return (int)Math.Clamp(endTick, 0L, int.MaxValue);
    }

    private void PasteEditorNotes(bool fitToBank = false)
    {
        if (_chart is null || _tempo is null || _editorClipboard.Count == 0)
        {
            _statusMessage = _editorClipboard.Count == 0 ? "Clipboard is empty." : "No chart is open.";
            return;
        }
        // Both PASTE and PASTE FIT preserve captured group instances. PASTE FIT
        // may need a new group definition when its lane mapping collapses two or
        // more canonical lanes; it must never silently flatten those notes.
        var hasGroupClipboardData = _editorClipboardGroupSources.Count > 0;
        var preserveGroupClipboard = hasGroupClipboardData && IsEditorClipboardCompleteNoteGroupInstance();
        if (hasGroupClipboardData && !preserveGroupClipboard)
        {
            _statusMessage = "Group paste cancelled: one or more copied group instances are incomplete.";
            return;
        }
        // The specialized exact-size placement path aligns a single, pure group
        // copy to the destination bank. Multi-group selections use the ordinary
        // clipboard positioning, then receive independent per-instance maps.
        var createGroupInstance = !fitToBank && preserveGroupClipboard &&
            _editorClipboardGroupSources.Count == 1 &&
            _editorClipboard.Count == _editorClipboardGroupSources[0].Definition.Members.Count;

        var routeTimeline = new GearTimeline(_chart);
        var clipboardEntries = _editorClipboard.ToList();
        var clipboardContainsPanelEvents = clipboardEntries.Any(entry =>
            entry.OriginalTrack is >= 17 and <= 20 || entry.Attr is >= 101 and <= 108);
        var clipboardIsPanelOnly = clipboardEntries.All(entry =>
            entry.OriginalTrack is >= 17 and <= 20 && entry.Attr is >= 101 and <= 108);
        var isPanelAnimationPaste = _editorPanelsViewEnabled && clipboardIsPanelOnly;
        if (_editorPanelsViewEnabled && !clipboardIsPanelOnly)
        {
            _statusMessage = "Paste cancelled: Panels view accepts only animation events with attrs 101–108. Copy them in Panels view first.";
            return;
        }
        if (!_editorPanelsViewEnabled && clipboardContainsPanelEvents)
        {
            _statusMessage = "Paste cancelled: animation events with attrs 101–108 can only be pasted in Panels view.";
            return;
        }

        // PASTE FIT applies only to gameplay notes. Panel animations continue to
        // use their fixed 17–20 track mapping.
        var fitGameplayToBank = fitToBank && !_editorPanelsViewEnabled;
        var snap = Math.Max(1, (int)Math.Round(GetEditorSnapTicks(), MidpointRounding.AwayFromZero));
        var selected = _selectedEventKey.HasValue
            ? _chart.Events.FirstOrDefault(ev => KeyOf(ev) == _selectedEventKey.Value)
            : null;
        var currentTick = (int)Math.Round(_tempo.SecondsToTick(_playSeconds) / (double)snap, MidpointRounding.AwayFromZero) * snap;
        var selectedClipboardGroupInstance = preserveGroupClipboard && selected is not null &&
            selected.NoteGroupId is int selectedGroupId && selected.NoteGroupInstanceId is int selectedInstanceId &&
            _editorClipboardGroupSources.Any(source => source.Instance.GroupId == selectedGroupId &&
                source.Instance.InstanceId == selectedInstanceId);
        var defaultAnchorTick = selectedClipboardGroupInstance
            ? GetEditorClipboardGroupPatternEndTick(snap)
            : selected is not null ? selected.Tick + snap : currentTick;
        var panelDestination = _editorPanelsViewEnabled ? _editorPanelsSelectedEmptyCell : null;
        var targetAnchorTick = panelDestination?.Tick ?? _selectedBeatTick ?? defaultAnchorTick;
        var targetAnchorTrack = panelDestination?.Track ?? _selectedBeatTrack ?? selected?.Track ?? _editorTrack;
        // A pasted group's bottom anchor is its earliest note tick. The target
        // cell is where that bottom-most note belongs; later notes retain their
        // original offsets above it. Do not use the last note/end of a hold here,
        // or the whole pattern is shifted by its height and no longer aligns like
        // PASTE FIT. Keep the stored group origin so member offsets stay intact.
        var groupBottomTickOffset = preserveGroupClipboard
            ? clipboardEntries.Min(entry => entry.TickOffset)
            : _editorClipboardAnchorTickOffset;
        var baseTickLong = (long)targetAnchorTick - groupBottomTickOffset;
        if (baseTickLong < 0 || baseTickLong > int.MaxValue)
        {
            _statusMessage = preserveGroupClipboard
                ? "Group paste cancelled: the pattern's bottom note would be before tick 0."
                : "Paste cancelled: the pasted tick is outside the chart's supported range.";
            return;
        }
        var baseTick = (int)baseTickLong;
        bool AllowedTargetTrack(int track) => _editorPanelsViewEnabled
            ? track is >= 17 and <= 20
            : track is >= 0 and <= 15;

        var targetTracks = new int[clipboardEntries.Count];
        var fitDroppedSuperFlicks = 0;
        if (createGroupInstance)
        {
            var sourceGroupId = clipboardEntries[0].NoteGroupId!.Value;
            var sourceInstance = _editorClipboardGroupSourceInstance;
            var destinationPosition = GearState.Position(targetAnchorTrack);
            if (sourceInstance is null || destinationPosition is null ||
                sourceInstance.GroupId != sourceGroupId)
            {
                _statusMessage = "Group paste cancelled: the source instance or destination bank is unavailable.";
                return;
            }

            // Use PASTE FIT's bank choice and lane alignment, but never scale the
            // pattern. Canonical tracks are mapped to the corresponding side of
            // the destination bank. A one-sided pattern is edge-aligned while
            // preserving every lane gap; a two-sided pattern keeps each side's
            // local lane. Any lane that cannot be represented exactly is rejected.
            var destinationLaneCount = destinationPosition.Value.Bank == "Center" ? 2 : 3;
            var bankStart = destinationPosition.Value.Bank switch
            {
                "Center" => 12,
                "Backward" => 6,
                _ => 0
            };
            var canonicalTracks = clipboardEntries
                .Select(entry => GetCanonicalEditorNoteGroupTrack(sourceInstance, entry.OriginalTrack))
                .ToArray();
            var canonicalPositions = canonicalTracks.Select(GearState.Position).ToArray();
            if (canonicalPositions.Any(position => position is null))
            {
                _statusMessage = "Group paste cancelled: the source pattern contains an invalid canonical lane.";
                return;
            }

            var sourceHasLeftSide = canonicalPositions.Any(position =>
                string.Equals(position!.Value.Side, "left", StringComparison.OrdinalIgnoreCase));
            var sourceHasRightSide = canonicalPositions.Any(position =>
                string.Equals(position!.Value.Side, "right", StringComparison.OrdinalIgnoreCase));
            var sourceSpansBothSides = sourceHasLeftSide && sourceHasRightSide;
            var laneMap = new Dictionary<int, int>();
            if (sourceSpansBothSides)
            {
                foreach (var canonicalTrack in canonicalTracks.Distinct())
                {
                    var position = GearState.Position(canonicalTrack);
                    if (position is null || position.Value.LocalPos >= destinationLaneCount)
                    {
                        _statusMessage = "Group paste cancelled: the pattern needs a lane that does not exist in the destination bank. It will not be auto-fitted.";
                        return;
                    }
                    laneMap[canonicalTrack] = EditorTrackForBankPosition(
                        position.Value.Side, destinationPosition.Value.Bank, position.Value.LocalPos);
                }
            }
            else
            {
                var sourcePosition = canonicalPositions.FirstOrDefault(position => position is not null);
                if (sourcePosition is null)
                {
                    _statusMessage = "Group paste cancelled: the source pattern has no valid bank side.";
                    return;
                }

                var sourceSide = sourcePosition.Value.Side;
                var canonicalMin = canonicalTracks.Min();
                var canonicalMax = canonicalTracks.Max();
                var sourceWidth = canonicalMax - canonicalMin + 1;
                if (sourceWidth > destinationLaneCount)
                {
                    _statusMessage = "Group paste cancelled: the pattern is wider than this bank side. Group paste does not auto-fit; choose a wider bank.";
                    return;
                }

                var fitStart = bankStart +
                    (string.Equals(sourceSide, "right", StringComparison.OrdinalIgnoreCase)
                        ? destinationLaneCount : 0);
                foreach (var canonicalTrack in canonicalTracks.Distinct())
                    laneMap[canonicalTrack] = fitStart + canonicalTrack - canonicalMin;
            }

            if (laneMap.Count == 0 || laneMap.Values.Any(track => track is < 0 or > 15) ||
                laneMap.Values.Distinct().Count() != laneMap.Count)
            {
                _statusMessage = "Group paste cancelled: the destination bank cannot preserve the pattern's lane layout.";
                return;
            }

            // The group's canonical origin stays unchanged. Per-instance mapping
            // captures the bank alignment, so synchronized edits can still map
            // each canonical member to the equivalent lane in every instance.
            var destinationInstanceTemplate = new NoteGroupInstance(
                sourceGroupId, -1, baseTick, sourceInstance.StartTrack,
                sourceInstance.IsMirrored, laneMap.OrderBy(pair => pair.Key)
                    .Select(pair => new NoteGroupTrackMapping(pair.Key, pair.Value)).ToList());

            for (var i = 0; i < clipboardEntries.Count; i++)
            {
                targetTracks[i] = GetPhysicalEditorNoteGroupTrack(
                    destinationInstanceTemplate, canonicalTracks[i]);
                if (targetTracks[i] is < 0 or > 15 || !AllowedTargetTrack(targetTracks[i]))
                {
                    _statusMessage = "Group paste cancelled: a mapped note would leave the gameplay lanes.";
                    return;
                }
            }
        }
        else if (!fitGameplayToBank)
        {
            var baseTrack = targetAnchorTrack - _editorClipboardAnchorTrackOffset;
            for (var i = 0; i < clipboardEntries.Count; i++)
                targetTracks[i] = baseTrack + clipboardEntries[i].TrackOffset;
        }
        else
        {
            var targetPosition = GearState.Position(targetAnchorTrack);
            if (targetPosition is null)
            {
                _statusMessage = "PASTE FIT needs a target track in the 1K, 2K, or 3K bank.";
                return;
            }

            var sourceWidth = Math.Max(1, clipboardEntries.Max(entry => entry.TrackOffset) + 1);
            var halfBankWidth = targetPosition.Value.Bank == "Center" ? 2 : 3;
            var bankStart = targetPosition.Value.Bank switch
            {
                "Center" => 12,
                "Backward" => 6,
                _ => 0
            };
            var bankWidth = halfBankWidth * 2;
            var sourcePositions = clipboardEntries
                .Select(entry => GearState.Position(entry.OriginalTrack))
                .ToArray();
            var sourceHasLeftSide = sourcePositions.Any(position =>
                position is not null && string.Equals(position.Value.Side, "left", StringComparison.OrdinalIgnoreCase));
            var sourceHasRightSide = sourcePositions.Any(position =>
                position is not null && string.Equals(position.Value.Side, "right", StringComparison.OrdinalIgnoreCase));
            var sourceSpansBothSides = sourceHasLeftSide && sourceHasRightSide;

            if (sourceSpansBothSides)
            {
                // A two-sided pattern keeps its original left/right layout. Map
                // each source lane within its own physical side, then fit that
                // lane coordinate to the matching side of the target bank. Do
                // not shift the whole pattern toward the clicked lane: doing so
                // could move the source's right-side notes off the right bank.
                for (var i = 0; i < clipboardEntries.Count; i++)
                {
                    var sourcePosition = sourcePositions[i];
                    if (sourcePosition is null)
                    {
                        _statusMessage = "PASTE FIT needs valid source tracks in the 1K, 2K, or 3K bank.";
                        return;
                    }

                    var sourceLaneCount = sourcePosition.Value.Bank == "Center" ? 2 : 3;
                    var mappedLocal = ScaleEditorBankLaneForPasteFit(
                        sourcePosition.Value.LocalPos, sourceLaneCount, halfBankWidth,
                        sourcePosition.Value.Side);
                    var rawTarget = EditorTrackForBankPosition(
                        sourcePosition.Value.Side, targetPosition.Value.Bank, mappedLocal);
                    if (rawTarget < 0)
                    {
                        _statusMessage = "PASTE FIT could not preserve the source pattern's left/right layout.";
                        return;
                    }
                    targetTracks[i] = GetEditorAllowedTrackForNoteType(rawTarget, clipboardEntries[i].NoteType);
                }
            }
            else
            {
                // A one-sided pattern that fits within one side of the target
                // bank should follow the side the user actually selected. Its
                // source side is irrelevant in that case (for example, a compact
                // R2K pattern can be pasted into L3K). Only retain the source-side
                // preference when the pattern is wider than one target side and
                // therefore needs PASTE FIT's existing lane compression.
                var sourceSide = sourcePositions.FirstOrDefault(position => position is not null)?.Side;
                if (sourceSide is null)
                {
                    _statusMessage = "PASTE FIT needs valid source tracks in the 1K, 2K, or 3K bank.";
                    return;
                }

                var fitWidth = halfBankWidth;
                var canFitOnSelectedSide = sourceWidth <= fitWidth;
                var destinationSide = canFitOnSelectedSide ? targetPosition.Value.Side : sourceSide;
                var fitStart = bankStart + (string.Equals(destinationSide, "right", StringComparison.OrdinalIgnoreCase)
                    ? halfBankWidth : 0);
                var baseTrack = fitStart;
                for (var i = 0; i < clipboardEntries.Count; i++)
                {
                    var offset = Math.Max(0, clipboardEntries[i].TrackOffset);
                    int mappedOffset;
                    if (sourceWidth <= fitWidth)
                        mappedOffset = offset;
                    else if (sourceWidth <= 1 || fitWidth <= 1)
                        mappedOffset = 0;
                    else
                        mappedOffset = ScaleEditorBankLaneForPasteFit(
                            offset, sourceWidth, fitWidth, sourceSide);
                    var rawTarget = baseTrack + Math.Clamp(mappedOffset, 0, fitWidth - 1);
                    targetTracks[i] = GetEditorAllowedTrackForNoteType(rawTarget, clipboardEntries[i].NoteType);
                }
            }
        }

        if (targetTracks.Where((track, index) => !AllowedTargetTrack(track)).Any())
        {
            _statusMessage = _editorPanelsViewEnabled
                ? "Paste cancelled: copied animation events must fit entirely in tracks 17–20."
                : "Paste cancelled: gameplay notes must fit entirely in tracks 0–15.";
            return;
        }

        // A fitted SuperFlick cannot keep its authored direction on another
        // track. Drop that entry, rather than rejecting the rest of the pattern.
        // Ordinary PASTE keeps its previous cross-bank/side safety check.
        bool MovesSuperFlickAcrossBanks()
        {
            for (var i = 0; i < clipboardEntries.Count; i++)
            {
                var entry = clipboardEntries[i];
                if (!entry.IsSuperFlick)
                    continue;
                var destination = GearState.Position(targetTracks[i]);
                if (destination is null || entry.OriginalTrackSide is null || entry.OriginalTrackBank is null ||
                    !string.Equals(destination.Value.Side, entry.OriginalTrackSide, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(destination.Value.Bank, entry.OriginalTrackBank, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        if (fitGameplayToBank)
        {
            var keptEntries = new List<EditorClipboardEntry>(clipboardEntries.Count);
            var keptTracks = new List<int>(targetTracks.Length);
            for (var i = 0; i < clipboardEntries.Count; i++)
            {
                var entry = clipboardEntries[i];
                if (entry.IsSuperFlick && targetTracks[i] != entry.OriginalTrack)
                {
                    fitDroppedSuperFlicks++;
                    continue;
                }
                keptEntries.Add(entry);
                keptTracks.Add(targetTracks[i]);
            }
            clipboardEntries = keptEntries;
            targetTracks = keptTracks.ToArray();
            if (clipboardEntries.Count == 0)
            {
                _statusMessage = fitDroppedSuperFlicks > 0
                    ? "PASTE FIT skipped: all SuperFlicks needed a different track and were removed."
                    : "PASTE FIT had no notes that could be placed.";
                return;
            }
            if (fitDroppedSuperFlicks > 0)
                _statusMessage = $"PASTE FIT will skip {fitDroppedSuperFlicks} SuperFlick(s) that would change tracks.";
        }
        else if (MovesSuperFlickAcrossBanks())
        {
            _statusMessage = "Paste cancelled: SuperFlicks cannot be pasted to a different bank or side.";
            return;
        }

        bool HasInternalGridCollision()
        {
            return clipboardEntries.Select((entry, index) => (entry.TickOffset, Track: targetTracks[index]))
                .GroupBy(cell => cell)
                .Any(group => group.Count() > 1);
        }

        bool HasInternalSuperFlickBankCollision()
        {
            var occupied = new HashSet<(int Tick, string Side, string Bank)>();
            for (var i = 0; i < clipboardEntries.Count; i++)
            {
                if (!clipboardEntries[i].IsSuperFlick)
                    continue;
                var position = GearState.Position(targetTracks[i]);
                if (position is null)
                    return true;
                var key = (clipboardEntries[i].TickOffset, position.Value.Side.ToUpperInvariant(), position.Value.Bank.ToUpperInvariant());
                if (!occupied.Add(key))
                    return true;
            }
            return false;
        }

        if (HasInternalSuperFlickBankCollision() && !fitGameplayToBank)
        {
            _statusMessage = "Paste cancelled: the copied pattern has multiple SuperFlicks in the same physical bank at one tick.";
            return;
        }

        bool FindCollisionFreeTickBase()
        {
            // PASTE FIT resolves collisions at the actual tick by note priority
            // after track fitting. Still move the whole paste when it would run
            // into the body of a hold whose head is on an earlier tick.
            if (HasInternalGridCollision() && !fitGameplayToBank)
            {
                _statusMessage = "Paste cancelled: the copied pattern contains overlapping notes on one tick and track.";
                return false;
            }

            for (var attempt = 0; attempt < 256; attempt++)
            {
                var collision = clipboardEntries.Select((entry, index) => (entry, index)).Any(item =>
                {
                    var targetTick = (long)baseTick + item.entry.TickOffset;
                    if (targetTick < 0 || targetTick > int.MaxValue)
                        return true;
                    var targetTrack = targetTracks[item.index];
                    if (_editorPanelsViewEnabled)
                        return FindEditorPanelsNoteStartingAtGridCell((int)targetTick, targetTrack) is not null;
                    if (!fitGameplayToBank && item.entry.IsSuperFlick &&
                        HasSuperFlickBankCollision((int)targetTick, targetTrack))
                        return true;

                    var occupied = FindEditorNoteAtGridCell((int)targetTick, targetTrack);
                    // Exact tick collisions are resolved by PASTE FIT's priority
                    // pass; only a hold body crossing another tick still blocks.
                    if (fitGameplayToBank && occupied?.Tick == (int)targetTick)
                        return false;
                    return occupied is not null;
                });
                if (!collision)
                    return true;
                if (createGroupInstance)
                {
                    _statusMessage = "Group paste cancelled: the anchored pattern overlaps an existing note or hold. Choose another tick.";
                    return false;
                }
                if (baseTick > int.MaxValue - snap)
                    break;
                baseTick += snap;
            }

            _statusMessage = "Paste cancelled: no collision-free space was found within the next 256 grid steps.";
            return false;
        }

        var groupPairs = new Dictionary<int, int>();
        var groupRanges = new Dictionary<int, (string Family, int StartTick, int EndTick,
            bool HasLinkTailAtStart, int? TerminalTailTick)>();
        void AllocateLinkPairs()
        {
            groupPairs.Clear();
            groupRanges.Clear();
            var groupFirsts = Enumerable.Range(0, clipboardEntries.Count)
                .Where(index => clipboardEntries[index].ChainGroup >= 0)
                .GroupBy(index => clipboardEntries[index].ChainGroup)
                .OrderBy(group => group.Min(index => clipboardEntries[index].TickOffset));

            foreach (var group in groupFirsts)
            {
                var firstIndex = group.OrderBy(index => clipboardEntries[index].TickOffset).First();
                var firstTrack = targetTracks[firstIndex];
                var family = LinkBuilder.Family(firstTrack);
                if (family is null || group.Any(index => !string.Equals(LinkBuilder.Family(targetTracks[index]), family, StringComparison.Ordinal)))
                    continue;

                var startTickLong = (long)baseTick + group.Min(index => clipboardEntries[index].TickOffset);
                var endTickLong = group.Max(index => (long)baseTick + clipboardEntries[index].TickOffset + Math.Max(1, clipboardEntries[index].Duration));
                if (startTickLong > int.MaxValue || endTickLong > int.MaxValue)
                    continue;
                var startTick = (int)startTickLong;
                var endTick = (int)endTickLong;
                var hasLinkTailAtStart = group.Any(index =>
                    clipboardEntries[index].NoteType == NoteType.LinkTail &&
                    (long)baseTick + clipboardEntries[index].TickOffset == startTickLong);
                var terminalEntry = group
                    .OrderBy(index => clipboardEntries[index].TickOffset)
                    .ThenBy(index => clipboardEntries[index].NoteType == NoteType.LinkTail ? 1 : 0)
                    .Last();
                int? terminalTailTick = clipboardEntries[terminalEntry].NoteType == NoteType.LinkTail
                    ? (int)(baseTick + clipboardEntries[terminalEntry].TickOffset)
                    : null;
                var pair = new[] { 1, 3, 5, 7 }.FirstOrDefault(candidate =>
                    !IsLinkPairOccupied(_chart, family, candidate, startTick, endTick,
                        hasLinkTailAtStart: hasLinkTailAtStart) &&
                    !groupPairs.Any(existing => existing.Value == candidate &&
                        groupRanges.TryGetValue(existing.Key, out var range) &&
                        EditorLinkIntervalsConflict(
                            routeTimeline,
                            range.Family, range.StartTick, range.EndTick, range.HasLinkTailAtStart,
                            family, startTick, endTick, hasLinkTailAtStart,
                            range.TerminalTailTick, terminalTailTick)));
                if (pair != 0)
                {
                    groupPairs[group.Key] = pair;
                    groupRanges[group.Key] = (family, startTick, endTick, hasLinkTailAtStart, terminalTailTick);
                }
            }
        }

        // Allocate chain pairs first so an orphaned copied LinkTail (which must
        // become an ordinary note) is also moved off a restricted 1K outer lane.
        // Tracks 17–20 are panel-animation lanes, not gameplay lanes in
        // GearState. They must not pass through the 1K LinkTail lane validator:
        // that validator treats every non-gameplay track as an invalid outer
        // lane and rejects otherwise valid panel pastes.
        for (var pass = 0; !isPanelAnimationPaste && pass < 3; pass++)
        {
            if (!FindCollisionFreeTickBase())
                return;
            AllocateLinkPairs();
            var adjustedTrack = false;
            for (var i = 0; i < clipboardEntries.Count; i++)
            {
                var entry = clipboardEntries[i];
                var isChain = entry.ChainGroup >= 0 && groupPairs.ContainsKey(entry.ChainGroup);
                var effectiveType = isChain
                    ? entry.NoteType
                    : entry.NoteType is NoteType.LinkHead or NoteType.LinkTail
                        ? entry.Duration > 6 ? NoteType.SingleLong : NoteType.Single
                        : entry.NoteType;
                if (IsEditorTrackAllowedForNoteType(targetTracks[i], effectiveType))
                    continue;
                if (!fitGameplayToBank)
                {
                    _statusMessage = createGroupInstance
                        ? "Group paste cancelled: this exact pattern places a note on a restricted 1K lane. Group paste does not compress or rearrange notes."
                        : "Paste cancelled: in 1K, L1/L3/R1/R3 are reserved for link tails. Use PASTE FIT to fit notes into legal lanes.";
                    return;
                }
                var allowedTrack = GetEditorAllowedTrackForNoteType(targetTracks[i], effectiveType);
                if (allowedTrack == targetTracks[i])
                {
                    _statusMessage = "PASTE FIT cancelled: one or more notes cannot be fitted into a legal lane.";
                    return;
                }
                targetTracks[i] = allowedTrack;
                adjustedTrack = true;
            }
            if (!adjustedTrack)
                break;
            if (pass == 2)
            {
                _statusMessage = "PASTE FIT cancelled: track restrictions did not settle to a valid mapping.";
                return;
            }
        }

        // Re-check all final constraints after any ordinary-note lane corrections.
        if (!FindCollisionFreeTickBase())
            return;
        AllocateLinkPairs();
        if (preserveGroupClipboard)
        {
            var unallocatedChain = clipboardEntries
                .Where(entry => entry.ChainGroup >= 0)
                .Select(entry => entry.ChainGroup)
                .Distinct()
                .FirstOrDefault(chainGroup => !groupPairs.ContainsKey(chainGroup), -1);
            if (unallocatedChain >= 0)
            {
                _statusMessage = "Group paste cancelled: one or more Link chains cannot be preserved at the destination. Choose another tick.";
                return;
            }
        }
        for (var i = 0; i < clipboardEntries.Count; i++)
        {
            var entry = clipboardEntries[i];
            var isChain = entry.ChainGroup >= 0 && groupPairs.ContainsKey(entry.ChainGroup);
            var effectiveType = isChain
                ? entry.NoteType
                : entry.NoteType is NoteType.LinkHead or NoteType.LinkTail
                    ? entry.Duration > 6 ? NoteType.SingleLong : NoteType.Single
                    : entry.NoteType;
            if (!isPanelAnimationPaste && !IsEditorTrackAllowedForNoteType(targetTracks[i], effectiveType))
            {
                _statusMessage = fitGameplayToBank
                    ? "PASTE FIT cancelled: a note would land on a restricted 1K outer lane."
                    : "Paste cancelled: a note would land on a restricted 1K outer lane.";
                return;
            }
        }

        var fitCollisionRemovals = new HashSet<ChartEvent>();
        var fitDroppedConflicts = 0;
        if (fitGameplayToBank)
        {
            int PriorityOfEntry(EditorClipboardEntry entry, bool isChain)
            {
                if (isChain)
                    return 0; // Link chains are the most important object.
                var type = entry.NoteType is NoteType.LinkHead or NoteType.LinkTail
                    ? entry.Duration > 6 ? NoteType.SingleLong : NoteType.Single
                    : entry.NoteType;
                if (type == NoteType.Vibrato)
                    return 1;
                if (type == NoteType.SingleLong)
                    return 2;
                if (entry.IsSuperFlick || entry.Attr is 10 or 50)
                    return 3;
                return 4;
            }

            int PriorityOfExisting(ChartEvent ev)
            {
                if (ev.NoteType is NoteType.LinkHead or NoteType.LinkTail)
                {
                    var root = GetEditorLinkRoot(ev);
                    if (root is not null && root.NoteType == NoteType.LinkHead && GetEditorLinkChainNodes(root).Count > 1)
                        return 0;
                }
                if (ev.NoteType == NoteType.Vibrato)
                    return 1;
                if (ev.NoteType == NoteType.SingleLong ||
                    ((ev.NoteType is NoteType.LinkHead or NoteType.LinkTail) && ev.Duration > 6))
                    return 2;
                if (ev.IsSuperFlick || ev.IsFlick)
                    return 3;
                return 4;
            }

            string EntityKeyFor(int index)
                => clipboardEntries[index].ChainGroup >= 0 && groupPairs.ContainsKey(clipboardEntries[index].ChainGroup)
                    ? $"chain:{clipboardEntries[index].ChainGroup}"
                    : $"note:{index}";

            var entityMembers = Enumerable.Range(0, clipboardEntries.Count)
                .GroupBy(EntityKeyFor)
                .ToDictionary(group => group.Key, group => group.ToList());
            var entityPriority = entityMembers.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Min(index => PriorityOfEntry(clipboardEntries[index],
                    clipboardEntries[index].ChainGroup >= 0 && groupPairs.ContainsKey(clipboardEntries[index].ChainGroup))));
            var entityFirstIndex = entityMembers.ToDictionary(pair => pair.Key, pair => pair.Value.Min());
            var rejectedEntities = new HashSet<string>();

            // Existing notes win ties. Reject an entire pasted chain if any of its
            // points collides with an equally or more important existing note.
            foreach (var pair in entityMembers)
            {
                var priority = entityPriority[pair.Key];
                foreach (var index in pair.Value)
                {
                    var targetTickLong = (long)baseTick + clipboardEntries[index].TickOffset;
                    if (targetTickLong < 0 || targetTickLong > int.MaxValue)
                    {
                        rejectedEntities.Add(pair.Key);
                        break;
                    }
                    var targetTick = (int)targetTickLong;
                    var targetTrack = targetTracks[index];
                    if (clipboardEntries[index].IsSuperFlick && HasSuperFlickBankCollision(targetTick, targetTrack))
                    {
                        rejectedEntities.Add(pair.Key);
                        break;
                    }
                    var existing = _chart.Events.Where(ev => ev.Kind == "Note" && ev.RawType == 1 &&
                        ev.Tick == targetTick && ev.Track == targetTrack);
                    if (existing.Any(ev => PriorityOfExisting(ev) <= priority))
                    {
                        rejectedEntities.Add(pair.Key);
                        break;
                    }
                }
            }

            // Resolve collisions within the pasted pattern by keeping higher
            // priority notes first. Chains are considered as a whole, so a losing
            // point never leaves a half-pasted chain behind.
            var occupiedPasteCells = new HashSet<(long Tick, int Track)>();
            var occupiedPasteSuperFlickBanks = new HashSet<(long Tick, string Side, string Bank)>();
            var acceptedEntities = new HashSet<string>();
            foreach (var pair in entityMembers
                .Where(pair => !rejectedEntities.Contains(pair.Key))
                .OrderBy(pair => entityPriority[pair.Key])
                .ThenBy(pair => entityFirstIndex[pair.Key]))
            {
                var cells = pair.Value.Select(index => (
                    Tick: (long)baseTick + clipboardEntries[index].TickOffset,
                    Track: targetTracks[index])).Distinct().ToList();
                var superFlickCells = new List<(long Tick, string Side, string Bank)>();
                var invalidSuperFlickCell = false;
                foreach (var index in pair.Value.Where(index => clipboardEntries[index].IsSuperFlick))
                {
                    var position = GearState.Position(targetTracks[index]);
                    var tick = (long)baseTick + clipboardEntries[index].TickOffset;
                    if (position is null || tick < 0 || tick > int.MaxValue)
                    {
                        invalidSuperFlickCell = true;
                        break;
                    }
                    superFlickCells.Add((tick, position.Value.Side.ToUpperInvariant(), position.Value.Bank.ToUpperInvariant()));
                }
                if (invalidSuperFlickCell || cells.Any(occupiedPasteCells.Contains) ||
                    superFlickCells.Any(occupiedPasteSuperFlickBanks.Contains))
                {
                    rejectedEntities.Add(pair.Key);
                    continue;
                }
                acceptedEntities.Add(pair.Key);
                foreach (var cell in cells)
                    occupiedPasteCells.Add(cell);
                foreach (var cell in superFlickCells)
                    occupiedPasteSuperFlickBanks.Add(cell);
            }

            fitDroppedConflicts = clipboardEntries.Count - entityMembers
                .Where(pair => acceptedEntities.Contains(pair.Key))
                .Sum(pair => pair.Value.Count);
            var keptIndexes = Enumerable.Range(0, clipboardEntries.Count)
                .Where(index => acceptedEntities.Contains(EntityKeyFor(index)))
                .ToList();

            // Now that the winning entries are known, remove only lower-priority
            // existing notes at the exact same tick and track. DeleteEditorEvents
            // also repairs any affected links. Its changes are included in the same
            // undo snapshot as the paste itself.
            foreach (var index in keptIndexes)
            {
                var targetTickLong = (long)baseTick + clipboardEntries[index].TickOffset;
                if (targetTickLong < 0 || targetTickLong > int.MaxValue)
                    continue;
                var targetTick = (int)targetTickLong;
                var targetTrack = targetTracks[index];
                var priority = entityPriority[EntityKeyFor(index)];
                foreach (var existing in _chart.Events.Where(ev => ev.Kind == "Note" && ev.RawType == 1 &&
                    ev.Tick == targetTick && ev.Track == targetTrack).ToList())
                {
                    if (PriorityOfExisting(existing) > priority)
                        fitCollisionRemovals.Add(existing);
                }
            }

            clipboardEntries = keptIndexes.Select(index => clipboardEntries[index]).ToList();
            targetTracks = keptIndexes.Select(index => targetTracks[index]).ToArray();
            if (clipboardEntries.Count == 0)
            {
                _statusMessage = fitDroppedConflicts > 0
                    ? "PASTE FIT skipped: all pasted notes conflicted with equal or higher-priority notes."
                    : "PASTE FIT had no notes that could be placed.";
                return;
            }
        }

        if (preserveGroupClipboard)
        {
            foreach (var source in _editorClipboardGroupSources)
            {
                var groupEntries = clipboardEntries.Where(entry =>
                    entry.NoteGroupId == source.Instance.GroupId &&
                    entry.NoteGroupInstanceId == source.Instance.InstanceId).ToList();
                var pastedMemberIds = groupEntries.Where(entry => entry.NoteGroupMemberId.HasValue)
                    .Select(entry => entry.NoteGroupMemberId!.Value).ToHashSet();
                var expectedMemberIds = source.Definition.Members.Select(member => member.Id).ToHashSet();
                if (groupEntries.Count != source.Definition.Members.Count ||
                    pastedMemberIds.Count != groupEntries.Count || !pastedMemberIds.SetEquals(expectedMemberIds))
                {
                    _statusMessage = fitGameplayToBank
                        ? "PASTE FIT cancelled: a grouped pattern member would be skipped. Group instances must remain complete."
                        : "Group paste cancelled: a copied group instance would be incomplete.";
                    return;
                }
            }
        }

        var affectedBeforePaste = GetEditorDeleteAffectedEvents(fitCollisionRemovals);
        var targetTrackIndices = targetTracks.Concat(affectedBeforePaste.Select(ev => ev.Track)).Distinct().ToArray();
        var pasteNeedsTwinPairing = clipboardEntries.Any(entry => entry.NoteType is
            NoteType.SingleLong or NoteType.Vibrato or NoteType.LinkHead or NoteType.LinkTail) ||
            affectedBeforePaste.Any(EditorEventNeedsTwinPairing);
        var pasteNeedsLinkGraph = clipboardEntries.Any(entry => entry.NoteType is NoteType.LinkHead or NoteType.LinkTail) ||
            affectedBeforePaste.Any(EditorEventNeedsLinkGraph);
        var snapshot = preserveGroupClipboard
            ? CaptureEditorChartSnapshot()
            : CaptureEditorNoteEditSnapshot(affectedBeforePaste, targetTrackIndices,
                rebuildTwinPairing: pasteNeedsTwinPairing, rebuildLinkGraph: pasteNeedsLinkGraph);
        if (snapshot is null)
        {
            _statusMessage = "Paste cancelled: an undo snapshot could not be created.";
            return;
        }
        if (fitCollisionRemovals.Count > 0)
            DeleteEditorEvents(fitCollisionRemovals);

        var pasted = new List<ChartEvent>();
        for (var i = 0; i < clipboardEntries.Count; i++)
        {
            var entry = clipboardEntries[i];
            var trackIndex = targetTracks[i];
            var tickLong = (long)baseTick + entry.TickOffset;
            if (tickLong < 0 || tickLong > int.MaxValue)
            {
                _statusMessage = "Paste cancelled: the pasted tick is outside the chart's supported range.";
                return;
            }
            var tick = (int)tickLong;
            var pair = 0;
            var isChain = entry.ChainGroup >= 0 && groupPairs.TryGetValue(entry.ChainGroup, out pair);
            NoteType noteType;
            if (isChain)
                noteType = entry.NoteType;
            else if (entry.NoteType is NoteType.LinkHead or NoteType.LinkTail)
                noteType = entry.Duration > 6 ? NoteType.SingleLong : NoteType.Single;
            else
                noteType = entry.NoteType;
            var attr = isChain
                ? (noteType == NoteType.LinkHead ? pair : pair + 1)
                : entry.Attr;
            var linePair = isChain ? pair : -1;
            var track = _chart.FindTrack(trackIndex);
            if (track is null)
            {
                track = new ChartTrack { TrackIndex = trackIndex };
                _chart.Tracks.Add(track);
            }

            var ev = new ChartEvent
            {
                Tick = tick,
                Track = trackIndex,
                Attr = attr,
                RawType = 1,
                TypeIndex = -1,
                Duration = entry.Duration,
                NoteType = noteType,
                EventIndex = track.Events.Count == 0 ? 0 : track.Events.Max(candidate => candidate.EventIndex) + 1,
                LinePair = linePair,
                Kind = "Note",
                Instrument = entry.Instrument,
                Velocity = entry.Velocity,
                Pan = entry.Pan
            };
            track.Events.Add(ev);
            pasted.Add(ev);
        }

        if (pasted.Count == 0)
        {
            _statusMessage = "Paste cancelled: no valid notes could be placed.";
            return;
        }

        var pastedGroupInstances = new List<(int GroupId, int InstanceId)>();
        if (preserveGroupClipboard && !TryApplyEditorClipboardGroupPasteMetadata(
                clipboardEntries, targetTracks, pasted, baseTick,
                out pastedGroupInstances, out var groupPasteError))
        {
            RestoreEditorChartSnapshot(snapshot);
            _statusMessage = groupPasteError ?? "Group paste cancelled: group metadata could not be reconstructed.";
            return;
        }

        if (pastedGroupInstances.Count > 0)
        {
            _editorNoteGroupPlacementArmed = false;
            _editorSelectedNoteGroupId = pastedGroupInstances[0].GroupId;
            _editorNoteGroupEditMode = false;
            _editorEditingNoteGroupId = null;
            _editorEditingNoteGroupInstanceId = null;
        }

        RegisterEditorEventDeltaAdditions(snapshot, pasted.ToArray());
        CommitEditorUndoSnapshot(snapshot);
        RefreshAfterEditorNoteSnapshot(snapshot, pasteNeedsTwinPairing, pasteNeedsLinkGraph);
        ClearEditorMultiSelection();
        _editorSelectedGridCells.Clear();
        _editorPanelsSelectedEmptyCell = null;
        foreach (var ev in pasted)
            AddEditorMultiSelectedEvent(ev);
        _selectedEventKey = KeyOf(pasted[0]);
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorTrack = pasted[0].Track;
        _editorSelectedHoldEnd = false;
        _editorSelectedLinkRoot = GetEditorLinkRoot(pasted[0]);
        _statusMessage = pastedGroupInstances.Count switch
        {
            1 => $"Pasted Group G{pastedGroupInstances[0].GroupId} as instance {GetEditorNoteGroupDisplayInstanceNumber(pastedGroupInstances[0].GroupId, pastedGroupInstances[0].InstanceId)} with {pasted.Count} note(s).",
            > 1 => $"Pasted {pasted.Count} note(s) across {pastedGroupInstances.Count} group instance(s)." +
                   (fitGameplayToBank ? " Group layouts were preserved or rebuilt to match the fitted tracks." : string.Empty),
            _ when fitGameplayToBank => $"Pasted and fitted {pasted.Count} note(s) into the destination bank." +
                (fitDroppedConflicts > 0 ? $" Skipped {fitDroppedConflicts} lower-priority/conflicting note(s)." : string.Empty) +
                (fitCollisionRemovals.Count > 0 ? $" Replaced {fitCollisionRemovals.Count} lower-priority note(s)." : string.Empty) +
                (fitDroppedSuperFlicks > 0 ? $" Removed {fitDroppedSuperFlicks} SuperFlick(s) that needed another track." : string.Empty),
            _ => $"Pasted {pasted.Count} note(s)."
        };
    }

    private bool TryApplyEditorClipboardGroupPasteMetadata(
        IReadOnlyList<EditorClipboardEntry> entries,
        IReadOnlyList<int> targetTracks,
        IReadOnlyList<ChartEvent> pasted,
        int baseTick,
        out List<(int GroupId, int InstanceId)> createdInstances,
        out string? error)
    {
        createdInstances = new List<(int GroupId, int InstanceId)>();
        error = null;
        if (_chart is null || entries.Count != targetTracks.Count || entries.Count != pasted.Count)
        {
            error = "Group paste cancelled: the pasted note mapping is incomplete.";
            return false;
        }

        var plans = new List<EditorClipboardGroupPastePlan>(_editorClipboardGroupSources.Count);
        foreach (var source in _editorClipboardGroupSources)
        {
            var entryIndexes = Enumerable.Range(0, entries.Count).Where(index =>
                entries[index].NoteGroupId == source.Instance.GroupId &&
                entries[index].NoteGroupInstanceId == source.Instance.InstanceId).ToArray();
            var entriesByMember = entryIndexes
                .Where(index => entries[index].NoteGroupMemberId.HasValue)
                .GroupBy(index => entries[index].NoteGroupMemberId!.Value)
                .ToDictionary(group => group.Key, group => group.ToList());
            var expectedMembers = source.Definition.Members.ToDictionary(member => member.Id);
            if (entryIndexes.Length != expectedMembers.Count || entriesByMember.Count != expectedMembers.Count ||
                expectedMembers.Keys.Any(memberId => !entriesByMember.TryGetValue(memberId, out var matches) || matches.Count != 1))
            {
                error = "Group paste cancelled: a copied instance no longer contains every group member.";
                return false;
            }

            var canonicalToTarget = new Dictionary<int, int>();
            var canonicalLaneMappingConsistent = true;
            var rebuiltSourceMembers = new List<NoteGroupMember>(expectedMembers.Count);
            var fittedMembers = new List<NoteGroupMember>(expectedMembers.Count);
            foreach (var sourceMember in source.Definition.Members.OrderBy(member => member.Id))
            {
                var index = entriesByMember[sourceMember.Id][0];
                var entry = entries[index];
                var pastedEvent = pasted[index];
                var sourceCanonicalTrack = GetCanonicalEditorNoteGroupTrack(source.Instance, entry.OriginalTrack);
                var canonicalAttr = GetCanonicalEditorNoteGroupAttr(source.Instance, entry.Attr);
                var memberTickOffset = entry.TickOffset - source.StartTickOffset;
                var member = new NoteGroupMember(
                    sourceMember.Id,
                    memberTickOffset,
                    sourceCanonicalTrack - source.Instance.StartTrack,
                    canonicalAttr,
                    entry.Duration,
                    entry.Instrument,
                    entry.Velocity,
                    entry.Pan,
                    pastedEvent.NoteType,
                    sourceMember.ChainGroup,
                    sourceMember.OriginalLinePair);
                rebuiltSourceMembers.Add(member);

                var targetTrack = targetTracks[index];
                var targetCanonicalTrack = source.Instance.IsMirrored
                    ? MirrorEditorTrackX(targetTrack)
                    : targetTrack;
                var targetUnmirroredTrack = targetCanonicalTrack;
                if (canonicalToTarget.TryGetValue(sourceCanonicalTrack, out var existingTarget))
                {
                    if (existingTarget != targetUnmirroredTrack)
                        canonicalLaneMappingConsistent = false;
                }
                else
                {
                    canonicalToTarget[sourceCanonicalTrack] = targetUnmirroredTrack;
                }
                fittedMembers.Add(new NoteGroupMember(
                    sourceMember.Id,
                    memberTickOffset,
                    targetCanonicalTrack,
                    canonicalAttr,
                    entry.Duration,
                    entry.Instrument,
                    entry.Velocity,
                    entry.Pan,
                    pastedEvent.NoteType,
                    sourceMember.ChainGroup,
                    sourceMember.OriginalLinePair));
            }

            var mappedTargetsAreUnique = canonicalLaneMappingConsistent &&
                canonicalToTarget.Values.Distinct().Count() == canonicalToTarget.Count;
            var mappedOffsets = canonicalToTarget.Select(pair => pair.Value - pair.Key).Distinct().Count();
            var mappingPreservesPatternSize = canonicalLaneMappingConsistent && mappedOffsets <= 1;
            var rebuiltMatchesClipboardDefinition = rebuiltSourceMembers.SequenceEqual(
                source.Definition.Members.OrderBy(member => member.Id));
            var currentDefinition = _chart.NoteGroups.FirstOrDefault(group => group.Id == source.Definition.Id);
            var currentDefinitionMatchesClipboard = currentDefinition is null ||
                (currentDefinition.Name == source.Definition.Name &&
                 currentDefinition.Members.OrderBy(member => member.Id)
                     .SequenceEqual(source.Definition.Members.OrderBy(member => member.Id)));
            var reuseSourceDefinition = mappedTargetsAreUnique && mappingPreservesPatternSize &&
                                       rebuiltMatchesClipboardDefinition && currentDefinitionMatchesClipboard;
            var trackMap = canonicalToTarget.OrderBy(pair => pair.Key)
                .Select(pair => new NoteGroupTrackMapping(pair.Key, pair.Value)).ToList();
            // Fitted group definitions store lane offsets relative to their own
            // start track, just like groups created from a normal chart selection.
            // Normalize the mapped canonical lanes so a later library placement can
            // shift the whole fitted pattern from its new origin.
            var fittedStartTrack = fittedMembers.Select(member => member.TrackOffset).DefaultIfEmpty(0).Min();
            var normalizedFittedMembers = fittedMembers.Select(member =>
                member with { TrackOffset = member.TrackOffset - fittedStartTrack }).ToList();
            var fittedSignature = string.Join(";", normalizedFittedMembers.OrderBy(member => member.Id).Select(member =>
                $"{member.Id},{member.TickOffset},{member.TrackOffset},{member.Attr},{member.Duration},{member.Instrument},{member.Velocity},{member.Pan},{(int)member.NoteType},{member.ChainGroup},{member.OriginalLinePair}"));
            var startTickLong = (long)baseTick + source.StartTickOffset;
            if (startTickLong < int.MinValue || startTickLong > int.MaxValue)
            {
                error = "Group paste cancelled: a copied instance anchor is outside the supported tick range.";
                return false;
            }

            plans.Add(new EditorClipboardGroupPastePlan(
                source,
                entryIndexes,
                reuseSourceDefinition,
                trackMap,
                normalizedFittedMembers,
                fittedStartTrack,
                fittedSignature,
                (int)startTickLong));
        }

        if (plans.Count == 0)
            return true;

        var nextGroupIdLong = Math.Max(
            _chart.NoteGroups.Select(group => group.Id).DefaultIfEmpty(0).Max(),
            _editorClipboardGroupSources.Select(source => source.Definition.Id).DefaultIfEmpty(0).Max()) + 1L;
        var nextInstanceIds = new Dictionary<int, int>();
        var fittedGroupIds = new Dictionary<(int SourceGroupId, string Signature), int>();
        foreach (var plan in plans)
        {
            var sourceGroupId = plan.Source.Definition.Id;
            int targetGroupId;
            int targetStartTrack;
            bool targetIsMirrored;
            List<NoteGroupTrackMapping>? targetTrackMap;

            if (plan.ReuseSourceDefinition)
            {
                targetGroupId = sourceGroupId;
                if (!_chart.NoteGroups.Any(group => group.Id == targetGroupId))
                {
                    _chart.NoteGroups.Add(new NoteGroupDefinition
                    {
                        Id = targetGroupId,
                        Name = plan.Source.Definition.Name,
                        Members = plan.Source.Definition.Members.ToList()
                    });
                }
                targetStartTrack = plan.Source.Instance.StartTrack;
                targetIsMirrored = plan.Source.Instance.IsMirrored;
                targetTrackMap = plan.TrackMap;
            }
            else
            {
                var fittedKey = (sourceGroupId, plan.FittedSignature);
                if (!fittedGroupIds.TryGetValue(fittedKey, out targetGroupId))
                {
                    if (nextGroupIdLong > int.MaxValue)
                    {
                        error = "Group paste cancelled: no group IDs remain available.";
                        return false;
                    }
                    targetGroupId = (int)nextGroupIdLong++;
                    fittedGroupIds[fittedKey] = targetGroupId;
                    _chart.NoteGroups.Add(new NoteGroupDefinition
                    {
                        Id = targetGroupId,
                        Name = plan.Source.Definition.Name,
                        Members = plan.FittedMembers.ToList()
                    });
                }
                targetStartTrack = plan.FittedStartTrack;
                targetIsMirrored = plan.Source.Instance.IsMirrored;
                targetTrackMap = null;
            }

            if (!nextInstanceIds.TryGetValue(targetGroupId, out var nextInstanceId))
            {
                nextInstanceId = _chart.NoteGroupInstances.Where(instance => instance.GroupId == targetGroupId)
                    .Select(instance => instance.InstanceId).DefaultIfEmpty(0).Max();
            }
            if (nextInstanceId == int.MaxValue)
            {
                error = $"Group paste cancelled: group G{targetGroupId} has no instance IDs remaining.";
                return false;
            }
            nextInstanceId++;
            nextInstanceIds[targetGroupId] = nextInstanceId;
            _chart.NoteGroupInstances.Add(new NoteGroupInstance(
                targetGroupId, nextInstanceId, plan.StartTick, targetStartTrack,
                targetIsMirrored, targetTrackMap));

            foreach (var index in plan.EntryIndexes)
            {
                pasted[index].NoteGroupId = targetGroupId;
                pasted[index].NoteGroupInstanceId = nextInstanceId;
                pasted[index].NoteGroupMemberId = entries[index].NoteGroupMemberId;
            }
            createdInstances.Add((targetGroupId, nextInstanceId));
        }

        return true;
    }

    // Remove event references only from their source/destination lanes. Scanning all
    // track lists for a one-note edit turns a small operation into a chart-wide pass.
    // originalTrackIndices covers live drags where an event's Track field has already
    // changed but the reference remains in its original track until commit.
    private void RemoveEditorEventsFromTrackLists(IEnumerable<ChartEvent> events,
        IEnumerable<int>? originalTrackIndices = null)
    {
        if (_chart is null)
            return;

        var eventSet = events as HashSet<ChartEvent> ?? events.ToHashSet();
        if (eventSet.Count == 0)
            return;

        var trackIndices = eventSet.Select(ev => ev.Track)
            .Concat(originalTrackIndices ?? Array.Empty<int>())
            .ToHashSet();
        // ARES can contain more than one physical ChartTrack container with the
        // same logical TrackIndex. FindTrack() returns only the first one; removing
        // through it leaves a stale copy in a duplicate container, which can later
        // be re-indexed as a second render entry. Remove from every container for
        // the source/destination lanes instead.
        foreach (var track in _chart.Tracks)
            if (trackIndices.Contains(track.TrackIndex))
                track.Events.RemoveAll(eventSet.Contains);
    }

    // Remove selected notes while preserving every unbroken run of a link chain.
    // A run downstream of a deleted point becomes its own chain when it has two
    // or more points; isolated points become ordinary notes/holds.
    private ChartEvent? DeleteEditorEvents(HashSet<ChartEvent> removals)
    {
        if (_chart is null || removals.Count == 0)
            return null;

        var chainRoots = new HashSet<ChartEvent>();
        foreach (var ev in removals)
        {
            var root = GetEditorLinkRoot(ev);
            if (root is not null && root.NoteType == NoteType.LinkHead)
                chainRoots.Add(root);
        }

        var chainPlans = chainRoots
            .Select(root => (Root: root, Nodes: GetEditorLinkChainNodes(root)))
            .Where(plan => plan.Nodes.Count > 1 && plan.Nodes.Any(removals.Contains))
            .ToList();

        foreach (var plan in chainPlans)
        {
            var nodes = plan.Nodes;
            for (var i = 0; i + 1 < nodes.Count; i++)
            {
                if (!removals.Contains(nodes[i]) && removals.Contains(nodes[i + 1]) && nodes[i + 1].Tick > nodes[i].Tick)
                    nodes[i].Duration = nodes[i + 1].Tick - nodes[i].Tick;
            }
        }

        RemoveEditorEventsFromTrackLists(removals);

        ChartEvent? replacement = null;
        foreach (var plan in chainPlans)
        {
            var nodes = plan.Nodes;
            var pair = plan.Root.LinePair;
            if (pair is not (1 or 3 or 5 or 7))
                pair = plan.Root.Attr is 1 or 3 or 5 or 7 ? plan.Root.Attr : -1;

            var i = 0;
            while (i < nodes.Count)
            {
                while (i < nodes.Count && removals.Contains(nodes[i]))
                    i++;
                if (i >= nodes.Count)
                    break;

                var runStart = i;
                while (i < nodes.Count && !removals.Contains(nodes[i]))
                    i++;
                var run = nodes.Skip(runStart).Take(i - runStart).ToList();
                if (run.Count == 0)
                    continue;

                var startsAtOriginalHead = runStart == 0 && run[0].NoteType == NoteType.LinkHead;
                if (run.Count >= 2 && pair is 1 or 3 or 5 or 7)
                {
                    var root = run[0];
                    root.NoteType = NoteType.LinkHead;
                    root.Attr = pair;
                    root.LinePair = pair;
                    for (var n = 1; n < run.Count; n++)
                    {
                        run[n].NoteType = NoteType.LinkTail;
                        run[n].Attr = pair + 1;
                        run[n].LinePair = pair;
                    }
                    for (var n = 0; n + 1 < run.Count; n++)
                    {
                        if (run[n + 1].Tick > run[n].Tick)
                            run[n].Duration = run[n + 1].Tick - run[n].Tick;
                    }
                    if (!startsAtOriginalHead && replacement is null)
                        replacement = root;
                }
                else if (run.Count == 1)
                {
                    ConvertEditorLinkPointToStandaloneNote(run[0]);
                    if (replacement is null && runStart > 0)
                        replacement = run[0];
                }
                else
                {
                    foreach (var point in run)
                        ConvertEditorLinkPointToStandaloneNote(point);
                }
            }
        }

        return replacement;
    }
}
