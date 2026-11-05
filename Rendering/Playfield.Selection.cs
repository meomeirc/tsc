using TSC;
using TSC.Ares;
using TSC.Input;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Color = Microsoft.Xna.Framework.Color;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using System.Globalization;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Reflection;

namespace TSC.Rendering;

public sealed partial class Playfield
{
    // Mouse/chart selection and editor selection overlays.

    // Mouse hover becomes active on movement or the first chart press.
    // Touch presses still edit the chart, but never drive the hover preview.
    private bool _editorMouseHoverActive;
    private Vector2 _editorPointerPosition;
    private enum EditorHoverHitKind : byte
    {
        LinkHeadMarker,
        HoldEndMarker,
        NoteCard,
        GridCellNote,
        LinkBody
    }

    private readonly record struct EditorHoverHit(
        ChartEvent Event, EditorHoverHitKind Kind, int Tick, int Track);

    private EditorHoverHit? _editorHoverHit;
    private static readonly Color EditorHoverEmptyColor = new(255, 255, 255, 88);
    private static readonly Color EditorHoverOverNoteColor = new(255, 220, 0, 170);
    private bool _editorTimelineScrubbing;
    private bool _editorTimelineResumeAfterScrub;
    private static readonly Color EditorSelectedColor = new(255, 165, 0);
    private const float EditorSelection3DNearPlane = 0.05f;
    private static readonly Color EditorGroupSelectedColor = new(174, 255, 0);
    private static readonly Color EditorFamilyColor = Color.White;

    private static Color GetEditorSelectedNoteHighlightColor(ChartEvent note)
        => note.NoteGroupId.HasValue ? EditorGroupSelectedColor : EditorSelectedColor;
    private static readonly Color EditorEndingColor = Color.Cyan;
    // Selection and Local Test segment geometry is drawn on the real 3D lane surfaces.
    // Reuse the vertex buffer each frame rather than creating short-lived arrays.
    private static readonly (int First, int Last)[] EditorLanTestSegmentBanks =
    { (0, 2), (3, 5), (6, 8), (9, 11), (12, 13), (14, 15) };
    private readonly VertexPositionColor[] _editorSelection3DVertices = new VertexPositionColor[2048];
    // All multi-selection highlights are accumulated into one reusable 2D mesh.
    // The capacity grows only when a denser-than-before visible frame requires it.
    private VertexPositionColor[] _editorMultiSelectionMarkerVertices = new VertexPositionColor[8192];
    private int _editorMultiSelectionMarkerVertexCount;
    private const int EditorMultiSelectionMarkerSegments = 20;
    private static readonly Vector2[] EditorMultiSelectionMarkerUnitCircle =
        CreateEditorMultiSelectionMarkerUnitCircle();
    private readonly record struct EditorMultiSelectionRenderItem(
        int EventIndex, bool IsDirectlySelected, bool IsInSelectedChain, bool IsChainEnd);
    private readonly List<EditorMultiSelectionRenderItem> _editorMultiSelectionRenderItems = new(256);
    // Selection rendering resolves a visible link note back to its cached chain.
    // It never enumerates the (possibly enormous) selected set to build an overlay.
    private Dictionary<ChartEvent, ChartEvent> _linkChainRootByMember = new();
    // Whole-instance selection uses a one-time index, not a chart-wide scan for
    // every selected group member. It is rebuilt only after chart mutation.
    private readonly Dictionary<(int GroupId, int InstanceId), List<ChartEvent>> _editorNoteGroupMembersByInstance = new();
    private readonly HashSet<(int GroupId, int InstanceId)> _editorNoteGroupExpansionKeysScratch = new();
    private AresChart? _editorNoteGroupMembersCacheChart;
    private bool _editorNoteGroupMembersCacheDirty = true;
    // A group-instance link graph is expensive to rebuild because it is isolated
    // from the chart-wide graph. Cache it once for the active edit instance and
    // resolve all selected members through this map.
    private readonly Dictionary<ChartEvent, EditorLinkChainInfo> _editorSelectionGroupChainByMember = new();
    private readonly List<EditorLinkChainInfo> _editorSelectionGroupChains = new();
    private bool _editorSelectionGroupChainCacheDirty = true;
    private AresChart? _editorSelectionGroupChainCacheChart;
    private int _editorSelectionGroupChainCacheGroupId = -1;
    private int _editorSelectionGroupChainCacheInstanceId = -1;

    // The group lock and note-group library need only a compact summary of the
    // current multi-selection. Recompute it when selection changes, never while
    // rendering every frame.
    private bool _editorMultiSelectionGroupSummaryDirty = true;
    private bool _editorMultiSelectionGroupSummaryHasEligibleNote;
    private bool _editorMultiSelectionHasLogicalNote;
    private bool _editorMultiSelectionHasPlayableNote;
    private bool _editorMultiSelectionCreateGroupDirty = true;
    private bool _editorMultiSelectionCanCreateGroup;
    private int? _editorMultiSelectionGroupIdSummary;
    private (int GroupId, int InstanceId)? _editorMultiSelectionGroupInstanceSummary;
    private int? _editorMultiSelectionMinimumTick;

    private void MarkEditorMultiSelectionChanged()
    {
        _editorMultiSelectionGroupSummaryDirty = true;
        _editorMultiSelectionCreateGroupDirty = true;
    }

    private void BeginEditorMultiSelectionRenderFrame()
    {
        _editorMultiSelectionRenderItems.Clear();
        _editorEditedGroupHighlightBounds.Clear();
    }

    private void CollectEditorMultiSelectionRenderItem(
        int eventIndex, ChartEvent note, GearState currentState)
    {
        if (_editorMultiSelectedEvents.Count == 0)
            return;

        var selectedNodes = _editorMultiSelectedEvents;
        var isDirectlySelected = selectedNodes.Contains(note) ||
            (_selectedEventKey.HasValue && KeyOf(note) == _selectedEventKey.Value);
        var isInSelectedChain = false;
        var isChainEnd = false;
        if (note.NoteType is NoteType.LinkHead or NoteType.LinkTail)
        {
            isInSelectedChain = IsEditorNoteInSelectedLinkChain(
                note, selectedNodes, currentState, out isChainEnd);
        }

        if (!isDirectlySelected && !isInSelectedChain)
            return;
        if (!isInSelectedChain && !ShouldRenderInCurrentBank(note, currentState))
            return;

        _editorMultiSelectionRenderItems.Add(new EditorMultiSelectionRenderItem(
            eventIndex, isDirectlySelected, isInSelectedChain, isChainEnd));
    }

    private void ClearEditorMultiSelection()
    {
        _editorMultiSelectedEvents.Clear();
        // A clear gives us a known empty summary. Subsequent adds can update it
        // incrementally, so Select All does not need another full selection pass.
        _editorMultiSelectionGroupSummaryDirty = false;
        _editorMultiSelectionGroupSummaryHasEligibleNote = false;
        _editorMultiSelectionHasLogicalNote = false;
        _editorMultiSelectionHasPlayableNote = false;
        _editorMultiSelectionCreateGroupDirty = false;
        _editorMultiSelectionCanCreateGroup = false;
        _editorMultiSelectionGroupIdSummary = null;
        _editorMultiSelectionGroupInstanceSummary = null;
        _editorMultiSelectionMinimumTick = null;
    }

    private bool AddEditorMultiSelectedEvent(ChartEvent note)
    {
        var added = _editorMultiSelectedEvents.Add(note);
        if (!added)
            return false;

        if (!_editorMultiSelectionGroupSummaryDirty &&
            (!_editorMultiSelectionMinimumTick.HasValue || note.Tick < _editorMultiSelectionMinimumTick.Value))
            _editorMultiSelectionMinimumTick = note.Tick;
        if (_editorMultiSelectionGroupSummaryDirty && !_editorMultiSelectionCreateGroupDirty)
            _editorMultiSelectionCreateGroupDirty = true;

        if (!_editorMultiSelectionGroupSummaryDirty && note.Kind == "Note" && note.RawType == 1)
        {
            _editorMultiSelectionHasLogicalNote = true;
            if (note.Track is >= 0 and <= 15 && note.NoteType != NoteType.End)
                _editorMultiSelectionHasPlayableNote = true;
        }

        if (!_editorMultiSelectionCreateGroupDirty && note.Kind == "Note" && note.RawType == 1)
        {
            var canCreateFromNote = note.Track is >= 0 and <= 15 &&
                note.NoteGroupId is null && note.NoteGroupInstanceId is null && note.NoteGroupMemberId is null;
            if (!_editorMultiSelectionGroupSummaryHasEligibleNote)
                _editorMultiSelectionCanCreateGroup = canCreateFromNote;
            else
                _editorMultiSelectionCanCreateGroup &= canCreateFromNote;
        }
        if (_editorMultiSelectionGroupSummaryDirty || note.Kind != "Note" || note.RawType != 1)
            return true;

        var instance = note.NoteGroupId is int groupId && note.NoteGroupInstanceId is int instanceId
            ? (GroupId: groupId, InstanceId: instanceId)
            : ((int GroupId, int InstanceId)?)null;
        if (!_editorMultiSelectionGroupSummaryHasEligibleNote)
        {
            _editorMultiSelectionGroupSummaryHasEligibleNote = true;
            _editorMultiSelectionGroupIdSummary = note.NoteGroupId;
            _editorMultiSelectionGroupInstanceSummary = instance;
        }
        else
        {
            if (_editorMultiSelectionGroupIdSummary != note.NoteGroupId)
                _editorMultiSelectionGroupIdSummary = null;
            if (_editorMultiSelectionGroupInstanceSummary != instance)
                _editorMultiSelectionGroupInstanceSummary = null;
        }
        return true;
    }

    private bool RemoveEditorMultiSelectedEvent(ChartEvent note)
    {
        var removed = _editorMultiSelectedEvents.Remove(note);
        if (removed)
            MarkEditorMultiSelectionChanged();
        return removed;
    }

    private int RemoveEditorMultiSelectedEventsWhere(Predicate<ChartEvent> predicate)
    {
        var removed = _editorMultiSelectedEvents.RemoveWhere(predicate);
        if (removed > 0)
            MarkEditorMultiSelectionChanged();
        return removed;
    }

    private void EnsureEditorMultiSelectionGroupSummary()
    {
        if (!_editorMultiSelectionGroupSummaryDirty)
            return;

        _editorMultiSelectionGroupIdSummary = null;
        _editorMultiSelectionGroupInstanceSummary = null;
        _editorMultiSelectionMinimumTick = null;
        _editorMultiSelectionGroupSummaryHasEligibleNote = false;
        _editorMultiSelectionHasLogicalNote = false;
        _editorMultiSelectionHasPlayableNote = false;
        _editorMultiSelectionGroupSummaryDirty = false;
        var sameGroup = true;
        var sameInstance = true;
        var allCanCreateGroup = true;
        var firstGroupId = 0;
        var firstInstanceId = 0;
        foreach (var note in _editorMultiSelectedEvents)
        {
            if (!_editorMultiSelectionMinimumTick.HasValue || note.Tick < _editorMultiSelectionMinimumTick.Value)
                _editorMultiSelectionMinimumTick = note.Tick;

            // Match GetSelectedEditorNotes: stale, non-note and reconstructed
            // events are not part of the logical note selection.
            if (!IsEditorEventInCurrentChart(note) || note.Kind != "Note" || note.RawType != 1)
                continue;

            _editorMultiSelectionHasLogicalNote = true;
            if (note.Track is >= 0 and <= 15 && note.NoteType != NoteType.End)
                _editorMultiSelectionHasPlayableNote = true;
            if (note.Track is < 0 or > 15 || note.NoteGroupId.HasValue ||
                note.NoteGroupInstanceId.HasValue || note.NoteGroupMemberId.HasValue)
                allCanCreateGroup = false;

            var groupId = note.NoteGroupId;
            var instanceId = note.NoteGroupInstanceId;
            if (!_editorMultiSelectionGroupSummaryHasEligibleNote)
            {
                _editorMultiSelectionGroupSummaryHasEligibleNote = true;
                _editorMultiSelectionGroupIdSummary = groupId;
                if (groupId.HasValue && instanceId.HasValue)
                    _editorMultiSelectionGroupInstanceSummary = (groupId.Value, instanceId.Value);
                firstGroupId = groupId ?? 0;
                firstInstanceId = instanceId ?? 0;
                if (!groupId.HasValue || !instanceId.HasValue)
                    sameInstance = false;
                if (!groupId.HasValue)
                    sameGroup = false;
                continue;
            }

            if (_editorMultiSelectionGroupIdSummary != groupId)
            {
                _editorMultiSelectionGroupIdSummary = null;
                sameGroup = false;
            }
            if (!groupId.HasValue || !instanceId.HasValue ||
                groupId.Value != firstGroupId || instanceId.Value != firstInstanceId)
            {
                _editorMultiSelectionGroupInstanceSummary = null;
                sameInstance = false;
            }
        }

        if (!sameGroup)
            _editorMultiSelectionGroupIdSummary = null;
        if (!sameInstance)
            _editorMultiSelectionGroupInstanceSummary = null;
        _editorMultiSelectionCanCreateGroup = _editorMultiSelectionGroupSummaryHasEligibleNote && allCanCreateGroup;
        _editorMultiSelectionCreateGroupDirty = false;
    }

    private bool HasPlayableEditorSelection()
    {
        if (_editorMultiSelectedEvents.Count == 0)
        {
            if (!_selectedEventKey.HasValue || !_eventsByKey.TryGetValue(_selectedEventKey.Value, out var selected))
                return false;
            return selected.Kind == "Note" && selected.RawType == 1 &&
                   selected.Track is >= 0 and <= 15 && selected.NoteType != NoteType.End;
        }

        EnsureEditorMultiSelectionGroupSummary();
        if (_editorMultiSelectionHasPlayableNote)
            return true;
        if (_editorMultiSelectionHasLogicalNote || !_selectedEventKey.HasValue ||
            !_eventsByKey.TryGetValue(_selectedEventKey.Value, out var single))
            return false;

        return single.Kind == "Note" && single.RawType == 1 &&
               single.Track is >= 0 and <= 15 && single.NoteType != NoteType.End;
    }

    private bool CanCreateEditorNoteGroupFromMultiSelection()
    {
        if (_editorMultiSelectedEvents.Count == 0)
        {
            // Single-note selection is represented separately from the multi-set.
            if (!_selectedEventKey.HasValue || !_eventsByKey.TryGetValue(_selectedEventKey.Value, out var selected) ||
                selected.Kind != "Note" || selected.RawType != 1)
                return false;
            return selected.Track is >= 0 and <= 15 &&
                   selected.NoteGroupId is null && selected.NoteGroupInstanceId is null &&
                   selected.NoteGroupMemberId is null;
        }

        EnsureEditorMultiSelectionGroupSummary();
        return !_editorMultiSelectionCreateGroupDirty && _editorMultiSelectionCanCreateGroup;
    }

    private void InvalidateEditorSelectionRenderCache()
    {
        MarkEditorMultiSelectionChanged();
        _editorSelectionGroupChainCacheDirty = true;
        _editorNoteGroupMembersCacheDirty = true;
    }

    private void EnsureEditorNoteGroupMembersCache()
    {
        if (_chart is null)
            return;
        if (!_editorNoteGroupMembersCacheDirty &&
            ReferenceEquals(_editorNoteGroupMembersCacheChart, _chart))
            return;

        _editorNoteGroupMembersByInstance.Clear();
        foreach (var note in _chart.Events)
        {
            if (note.Kind != "Note" || note.RawType != 1 ||
                note.NoteGroupId is not int groupId ||
                note.NoteGroupInstanceId is not int instanceId)
                continue;

            var key = (groupId, instanceId);
            if (!_editorNoteGroupMembersByInstance.TryGetValue(key, out var members))
                _editorNoteGroupMembersByInstance[key] = members = new List<ChartEvent>(4);
            members.Add(note);
        }

        _editorNoteGroupMembersCacheChart = _chart;
        _editorNoteGroupMembersCacheDirty = false;
    }

    private bool IsEditorNoteInSelectedLinkChain(
        ChartEvent note,
        HashSet<ChartEvent> selectedNodes,
        GearState currentState,
        out bool isChainEnd)
    {
        isChainEnd = false;
        if (note.NoteType is not (NoteType.LinkHead or NoteType.LinkTail))
            return false;

        List<ChartEvent>? chainNodes = null;
        if (_editorNoteGroupEditMode &&
            _editorEditingNoteGroupId is int groupId &&
            _editorEditingNoteGroupInstanceId is int instanceId &&
            note.NoteGroupId == groupId && note.NoteGroupInstanceId == instanceId)
        {
            EnsureEditorSelectionGroupChainCache(groupId, instanceId);
            if (_editorSelectionGroupChainByMember.TryGetValue(note, out var localChain))
                chainNodes = localChain.Nodes;
        }
        else if (_linkChainRootByMember.TryGetValue(note, out var root) &&
                 _linkChainCache.TryGetValue(KeyOf(root), out var cachedChain))
        {
            chainNodes = cachedChain;
        }

        if (chainNodes is null || chainNodes.Count == 0)
            return false;

        isChainEnd = ReferenceEquals(note, chainNodes[^1]);
        // Link chains are short (at most four members in gameplay). Test the
        // selected hash directly instead of scanning thousands of selected notes.
        for (var i = 0; i < chainNodes.Count; i++)
        {
            var member = chainNodes[i];
            if (selectedNodes.Contains(member) && ShouldRenderInCurrentBank(member, currentState))
                return true;
        }

        return false;
    }

    private void EnsureEditorSelectionGroupChainCache(int groupId, int instanceId)
    {
        if (_chart is null)
            return;
        if (!_editorSelectionGroupChainCacheDirty &&
            ReferenceEquals(_editorSelectionGroupChainCacheChart, _chart) &&
            _editorSelectionGroupChainCacheGroupId == groupId &&
            _editorSelectionGroupChainCacheInstanceId == instanceId)
            return;

        _editorSelectionGroupChainByMember.Clear();
        _editorSelectionGroupChains.Clear();
        foreach (var chain in GetEditorNoteGroupLinkChainInfos(groupId, instanceId))
        {
            _editorSelectionGroupChains.Add(chain);
            foreach (var member in chain.Nodes)
                _editorSelectionGroupChainByMember.TryAdd(member, chain);
        }

        _editorSelectionGroupChainCacheChart = _chart;
        _editorSelectionGroupChainCacheGroupId = groupId;
        _editorSelectionGroupChainCacheInstanceId = instanceId;
        _editorSelectionGroupChainCacheDirty = false;
    }

    private bool TryGetActiveEditorGroupChain(ChartEvent note, out EditorLinkChainInfo chain)
    {
        chain = null!;
        if (!_editorNoteGroupEditMode ||
            _editorEditingNoteGroupId is not int groupId ||
            _editorEditingNoteGroupInstanceId is not int instanceId ||
            note.NoteGroupId != groupId || note.NoteGroupInstanceId != instanceId)
            return false;

        EnsureEditorSelectionGroupChainCache(groupId, instanceId);
        if (_editorSelectionGroupChainByMember.TryGetValue(note, out var cachedChain))
        {
            chain = cachedChain;
            return true;
        }
        return false;
    }

    private bool BeginEditorSelectLineDrag(Vector2 point, double currentTick, GearState currentState,
        int virtualWidth, int virtualHeight, bool panelsView)
    {
        int tick;
        int track;
        if (panelsView)
        {
            var area = GetEditorPanelsTrackAreaBounds(virtualWidth, virtualHeight);
            if (!area.Contains((int)point.X, (int)point.Y))
                return false;
            var x = Math.Clamp(point.X, area.Left, area.Right - 1);
            var y = Math.Clamp(point.Y, area.Top, area.Bottom - 1);
            tick = GetEditorPanelsSnappedTickAtY(y, currentTick, currentState);
            track = GetEditorPanelsTrackAtX(x, virtualWidth, virtualHeight);
        }
        else
        {
            var snapped = FindClickedQuarterBeat(point, currentTick, currentState, out track);
            if (snapped is not int snappedTick || track is < 0 or > 15)
                return false;
            tick = snappedTick;
        }

        _editorSelectLineDragging = true;
        _editorSelectLineStart = point;
        _editorSelectLineStartTrack = track;
        _editorSelectReferenceTickOverride = null;

        // This gesture chooses the line's track span and reference tick only. It
        // deliberately clears note selection so no note can be dragged or selected
        // through the SELECT submenu.
        _selectedEventKey = null;
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorSelectedLinkRoot = null;
        _editorSelectedHoldEnd = false;
        ClearEditorMultiSelection();
        ClearEditorNoteGroupLibrarySelection();
        _editorPanelsSelectedEmptyCell = null;
        UpdateEditorSelectLineDrag(point, tick, track, currentTick, currentState,
            virtualWidth, virtualHeight, panelsView);
        _statusMessage = "SELECT range line placed. Drag horizontally to choose tracks; move vertically to set the tick.";
        return true;
    }

    private void UpdateEditorSelectLineDrag(Vector2 point, int tick, int hoveredTrack,
        double currentTick, GearState currentState, int virtualWidth, int virtualHeight, bool panelsView)
    {
        tick = Math.Max(0, tick);
        _editorSelectReferenceLineTick = tick;
        _editorSelectReferenceTickOverride = null;

        var selectedTracks = new HashSet<int>();
        var lowX = Math.Min(_editorSelectLineStart.X, point.X);
        var highX = Math.Max(_editorSelectLineStart.X, point.X);
        if (panelsView)
        {
            var area = GetEditorPanelsTrackAreaBounds(virtualWidth, virtualHeight);
            lowX = Math.Clamp(lowX, area.Left, area.Right - 1);
            highX = Math.Clamp(highX, area.Left, area.Right - 1);
            var startTrack = GetEditorPanelsTrackAtX(lowX, virtualWidth, virtualHeight);
            var endTrack = GetEditorPanelsTrackAtX(highX, virtualWidth, virtualHeight);
            for (var track = Math.Min(startTrack, endTrack); track <= Math.Max(startTrack, endTrack); track++)
                selectedTracks.Add(track);
        }
        else
        {
            // Select columns crossed by the pointer in screen X at the reference
            // tick. Track IDs aren't a reliable left-to-right order across banks,
            // so use their projected centers and retain the clicked endpoint lanes.
            for (var candidateTrack = 0; candidateTrack < 16; candidateTrack++)
            {
                var position = GearState.Position(candidateTrack);
                if (position is null)
                    continue;
                if (_compact && !string.Equals(
                        GearTimeline.ActiveBankForSide(currentState, position.Value.Side),
                        position.Value.Bank, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!_compact && _chartEditorMode &&
                    GetEditorBankOpacity(position.Value.Side, position.Value.Bank, currentState) <= 0.001f)
                    continue;

                var projected = _projection.ProjectAtTicks(candidateTrack, tick, currentTick, currentState);
                var x = projected.Position.X;
                if (!float.IsFinite(x) || projected.Scale <= 0f)
                    continue;
                if (x >= lowX - 1f && x <= highX + 1f)
                    selectedTracks.Add(candidateTrack);
            }
            if (_editorSelectLineStartTrack is >= 0 and <= 15)
                selectedTracks.Add(_editorSelectLineStartTrack);
            if (hoveredTrack is >= 0 and <= 15)
                selectedTracks.Add(hoveredTrack);
        }

        if (selectedTracks.Count == 0 && _editorSelectLineStartTrack >= 0)
            selectedTracks.Add(_editorSelectLineStartTrack);

        _editorSelectedGridCells.Clear();
        foreach (var track in selectedTracks.OrderBy(value => value))
            _editorSelectedGridCells.Add((tick, track));
        _editorHoverCacheValid = false;
    }

    private void UpdateEditorSelectLineDragFromPointer(Vector2 point, double currentTick, GearState currentState,
        int virtualWidth, int virtualHeight, bool panelsView)
    {
        var hoveredTrack = -1;
        int lineTick;
        if (panelsView)
        {
            var area = GetEditorPanelsTrackAreaBounds(virtualWidth, virtualHeight);
            var x = Math.Clamp(point.X, area.Left, area.Right - 1);
            var y = Math.Clamp(point.Y, area.Top, area.Bottom - 1);
            lineTick = GetEditorPanelsSnappedTickAtY(y, currentTick, currentState);
            hoveredTrack = GetEditorPanelsTrackAtX(x, virtualWidth, virtualHeight);
            point = new Vector2(x, y);
        }
        else
        {
            var snapped = FindClickedQuarterBeat(point, currentTick, currentState, out hoveredTrack);
            // When the pointer crosses a gap between bank surfaces, retain the last
            // valid tick while still allowing the horizontal span to extend.
            lineTick = snapped ?? _editorSelectReferenceLineTick ?? (int)Math.Max(0, Math.Round(currentTick));
        }

        UpdateEditorSelectLineDrag(point, lineTick, hoveredTrack, currentTick, currentState,
            virtualWidth, virtualHeight, panelsView);
    }

    private void ContinueEditorSelectLineDrag(Vector2 point, double currentTick, GearState currentState,
        int virtualWidth, int virtualHeight, bool held, bool panelsView)
    {
        // The last held frame already updated the line. Do not remap a touch
        // release through the mouse cursor, which can be elsewhere on screen.
        if (held)
        {
            UpdateEditorSelectLineDragFromPointer(point, currentTick, currentState,
                virtualWidth, virtualHeight, panelsView);
            return;
        }

        _editorSelectLineDragging = false;
        _editorSelectLineStartTrack = -1;
    }

    private void HandleMouseSelection()
    {
        if (!_chartEditorMode || _chart is null || _tempo is null || _gear is null)
        {
            HandleMouseSelectionCore();
            return;
        }

        var mouse = GlobalInput.Mouse;
        var leftDown = mouse.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed;
        var leftPressed = leftDown && GlobalInput.PreviousMouse.LeftButton != Microsoft.Xna.Framework.Input.ButtonState.Pressed;
        var rightPressed = mouse.RightButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed &&
                           GlobalInput.PreviousMouse.RightButton != Microsoft.Xna.Framework.Input.ButtonState.Pressed;
        var touches = Microsoft.Xna.Framework.Input.Touch.TouchPanel.GetState();
        var touchPressed = false;
        var touchActive = false;
        for (var i = 0; i < touches.Count; i++)
        {
            if (touches[i].State is Microsoft.Xna.Framework.Input.Touch.TouchLocationState.Pressed or
                Microsoft.Xna.Framework.Input.Touch.TouchLocationState.Moved)
                touchActive = true;
            if (touches[i].State == Microsoft.Xna.Framework.Input.Touch.TouchLocationState.Pressed)
                touchPressed = true;
        }

        var selectionBefore = leftPressed || rightPressed || touchPressed
            ? CaptureEditorChartSnapshot(selectionOnly: true)
            : null;
        HandleMouseSelectionCore();

        if (selectionBefore is not null)
        {
            // An empty-lane click is armed as a potential marquee. Delay its
            // history entry until release so selection drags produce one step.
            if (_editorRectSelectionArmed || _editorRectSelecting)
                _editorSelectionPendingUndoSnapshot = selectionBefore;
            else
                CommitEditorSelectionUndoSnapshot(selectionBefore);
        }

        var pending = _editorSelectionPendingUndoSnapshot;
        if (pending is null || _editorRectSelectionArmed || _editorRectSelecting || leftDown || touchActive)
            return;

        _editorSelectionPendingUndoSnapshot = null;
        CommitEditorSelectionUndoSnapshot(pending);
    }

    private void HandleMouseSelectionCore()
    {
        if (_chart is null || _tempo is null || _gear is null)
            return;

        var mouse = GlobalInput.Mouse;
        var leftDown = mouse.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed;
        var leftPressed = leftDown && GlobalInput.PreviousMouse.LeftButton != Microsoft.Xna.Framework.Input.ButtonState.Pressed;
        var leftReleased = !leftDown && GlobalInput.PreviousMouse.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed;
        var rightPressed = mouse.RightButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed && GlobalInput.PreviousMouse.RightButton != Microsoft.Xna.Framework.Input.ButtonState.Pressed;
        var mouseMoved = mouse.X != GlobalInput.PreviousMouse.X || mouse.Y != GlobalInput.PreviousMouse.Y;

        // Touch can place/select on Pressed and continue a drag on Moved. It has no
        // hover preview; a touch drag ends when the active touch disappears.
        var touchPressed = false;
        var touchMoved = false;
        var touchActive = false;
        var touchX = 0f;
        var touchY = 0f;
        var touches = Microsoft.Xna.Framework.Input.Touch.TouchPanel.GetState();
        var touchInputThisFrame = touches.Count > 0;
        for (var i = 0; i < touches.Count; i++)
        {
            var touch = touches[i];
            if (touch.State is not (Microsoft.Xna.Framework.Input.Touch.TouchLocationState.Pressed or
                                    Microsoft.Xna.Framework.Input.Touch.TouchLocationState.Moved))
                continue;

            if (!touchActive || touch.State == Microsoft.Xna.Framework.Input.Touch.TouchLocationState.Pressed)
            {
                touchX = touch.Position.X;
                touchY = touch.Position.Y;
            }
            touchActive = true;
            if (touch.State == Microsoft.Xna.Framework.Input.Touch.TouchLocationState.Pressed)
                touchPressed = true;
            if (touch.State == Microsoft.Xna.Framework.Input.Touch.TouchLocationState.Moved)
            {
                touchMoved = true;
                if (!touchPressed)
                {
                    touchX = touch.Position.X;
                    touchY = touch.Position.Y;
                }
            }
        }

        // Also suppress the mouse-hover state on touch frames, since some platforms
        // briefly mirror the finger position through mouse input.
        if (touchInputThisFrame)
            _editorMouseHoverActive = false;
        else if (mouseMoved || leftPressed ||
                 (_chartEditorMode && (_editorNoteGroupPlacementArmed || _editorPlacementEnabled)))
            _editorMouseHoverActive = true;

        var (virtualWidth, virtualHeight) = GetVirtualResolution(_graphics.Viewport.Width, _graphics.Viewport.Height);
        Vector2 ToVirtualPoint(float x, float y) => new(
            x * virtualWidth / (float)Math.Max(1, _graphics.Viewport.Width),
            y * virtualHeight / (float)Math.Max(1, _graphics.Viewport.Height));

        var mousePoint = ToVirtualPoint(mouse.X, mouse.Y);
        if (!touchInputThisFrame)
            _editorPointerPosition = mousePoint;
        var point = touchActive ? ToVirtualPoint(touchX, touchY) : mousePoint;
        var currentTick = _tempo.SecondsToTick(_playSeconds);
        var currentState = RenderGearState(currentTick);

        // Mouse hover previews the snapped tick/track used by the active note tool.
        // It is deliberately absent during touch interaction.
        var pointerPixel = new Microsoft.Xna.Framework.Point((int)MathF.Round(point.X), (int)MathF.Round(point.Y));
        var editorLuaUiActive = _chartEditorMode && _chartEditorInterfaceRuntime.IsLoaded &&
                                _chartEditorInterfaceRuntime.LastError is null;
        if (!editorLuaUiActive && HandleEditorMusicVolumeFallbackPointer(
                pointerPixel, leftPressed, leftDown, leftReleased, virtualWidth, virtualHeight))
            return;

        // Once a gesture belongs to the chart canvas, keep pointer ownership there
        // until release. Lua buttons/sliders must not capture or interrupt note drags,
        // marquees, or the SELECT line. Timeline scrubbing is different: its Lua
        // slider owns pointer capture so it can continue receiving drag updates.
        var editorCanvasGestureActive = _chartEditorMode &&
            (_editorRectSelectionArmed || _editorRectSelecting || _editorDraggedEvent is not null ||
             _editorPanelsMarqueeArmed || _editorPanelsMarqueeSelecting || _editorPanelUsingSharedDrag ||
             _editorSelectLineDragging);
        if (editorLuaUiActive)
        {
            if (editorCanvasGestureActive)
            {
                // Clear any stale UI capture while retaining live hover feedback.
                _chartEditorInterfaceRuntime.UpdateUiPointer(point.X, point.Y, leftDown: false);
                var releasedUiAction = _chartEditorInterfaceRuntime.ConsumeUiAction();
                if (releasedUiAction is not null)
                {
                    DispatchChartEditorInterfaceAction(releasedUiAction, virtualWidth, virtualHeight);
                    return;
                }
            }
            else
            {
                UpdateChartEditorInterfacePointer(point, leftDown || touchActive);
                // A slider's release_action is emitted when pointer capture ends.
                // Consume it for mouse and touch release alike before processing
                // any chart-canvas gesture at the same position.
                var pointerReleaseAction = _chartEditorInterfaceRuntime.ConsumeUiAction();
                if (pointerReleaseAction is not null)
                {
                    DispatchChartEditorInterfaceAction(pointerReleaseAction, virtualWidth, virtualHeight);
                    return;
                }
                if (leftPressed || touchPressed)
                {
                    var hitLuaControl = _chartEditorInterfaceRuntime.HandleUiPointerPressed(point.X, point.Y);
                    var action = _chartEditorInterfaceRuntime.ConsumeUiAction();
                    if (hitLuaControl)
                    {
                        DispatchChartEditorInterfaceAction(action, virtualWidth, virtualHeight);
                        return;
                    }
                }
                else if (leftDown && _chartEditorInterfaceRuntime.HandleUiPointerDrag(point.X, point.Y))
                {
                    DispatchChartEditorInterfaceAction(_chartEditorInterfaceRuntime.ConsumeUiAction(), virtualWidth, virtualHeight);
                    return;
                }
            }
        }

        // Preview mode still exposes its two Lua toolbar controls. Let Lua get
        // first chance to process them above, then keep clicks outside the
        // toolbar from selecting or placing chart notes beneath the preview.
        if (_chartEditorMode && _editorPreviewMode)
        {
            if (!editorLuaUiActive && (leftPressed || touchPressed))
                TryHandleChartEditorToolbarClick(point, virtualWidth, virtualHeight);
            return;
        }

        // The timeline is intentionally handled by one C# press/drag/release
        // path below. The skin only draws it; a parallel Lua slider would compete
        // for pointer capture and could leave click-only scrubbing.

        if ((leftPressed || touchPressed) && _editorMetaEditingField is not null)
            CommitEditorMetaFieldOnBlur();
        var pointerOverToolbar = (editorLuaUiActive &&
                                  _chartEditorInterfaceRuntime.IsUiPointOverControl(point.X, point.Y)) ||
                                 GetEditorTimelineScrubberBounds(virtualWidth, virtualHeight).Contains(pointerPixel) ||
                                 (!editorLuaUiActive &&
                                  (IsEditorToolbarPanelContains(pointerPixel, virtualWidth, virtualHeight) ||
                                   IsEditorBankIndicatorArea(point, virtualWidth, virtualHeight)));

        // SELECT is an exclusive range-filter mode. The cyan range-line gesture is
        // handled separately; ordinary note hit-testing and the orange marquee are
        // disabled for the entire time the submenu is open.
        if (_chartEditorMode && _editorMenuSection == EditorMenuSection.Select)
        {
            _editorRectSelecting = false;
            _editorRectSelectionArmed = false;
            ClearEditorPendingRectangleUndoSnapshot();
            _editorHoverCacheValid = false;
            _hoverBeatTick = null;
            _hoverTrack = null;
            _editorHoverHit = null;
        }

        // The cyan SELECT line has its own drag path in gameplay view. Process it
        // before normal lane hit-testing so the gesture cannot select or drag notes.
        if (_chartEditorMode && !_editorPanelsViewEnabled &&
            _editorMenuSection == EditorMenuSection.Select && _editorSelectLineDragging)
        {
            ContinueEditorSelectLineDrag(point, currentTick, currentState,
                virtualWidth, virtualHeight, leftDown || touchActive, panelsView: false);
            return;
        }

        // Panels view owns chart input outside its toolbar and never routes those
        // clicks into the ordinary 0-15 gameplay lanes.
        if (_chartEditorMode && _editorPanelsViewEnabled && !_editorTimelineScrubbing &&
            HandleEditorPanelsMouseSelection(point, pointerPixel, currentTick, currentState,
                leftDown, leftPressed, rightPressed, touchActive, touchPressed, touchMoved,
                mouseMoved, touchInputThisFrame, pointerOverToolbar, virtualWidth, virtualHeight))
            return;

        var hoverModeAllowsLaneTarget = _chartEditorMode && !_editorPanelsViewEnabled &&
            !_editorPreviewMode && !_editorTimelineScrubbing && _editorDraggedEvent is null &&
            !_editorRectSelecting && !_editorRectSelectionArmed &&
            _editorMenuSection is not (EditorMenuSection.Select or EditorMenuSection.Bpm or EditorMenuSection.Stutter) &&
            !_editorLanTestSegmentStartPlacementPending && !_editorLanTestSegmentEndPlacementPending;
        if (hoverModeAllowsLaneTarget && _editorMouseHoverActive && !touchInputThisFrame && !pointerOverToolbar)
        {
            var signatureHash = new HashCode();
            signatureHash.Add(ComputeLaneMeshCacheSignature(currentState));
            signatureHash.Add(_projection.ScrollSpeedScale);
            signatureHash.Add(_projection.BaseScrollSpeed);
            signatureHash.Add(_projection.GetRenderDistanceAhead());
            signatureHash.Add(_projection.GetRenderDistanceBehind());
            signatureHash.Add(_projection.NoteStandAngleDegrees);
            signatureHash.Add(_topDownViewEnabled);
            signatureHash.Add(_compact);
            signatureHash.Add(GetEditorSnapTicks());
            signatureHash.Add(_editorNoteType);
            signatureHash.Add(_editorSuperFlickTool);
            signatureHash.Add(_editorAttr);
            signatureHash.Add(_editorMenuSection);
            signatureHash.Add(_editorPlacementEnabled);
            signatureHash.Add(_editorConnectorMode);
            signatureHash.Add(_editorNoteGroupPlacementArmed);
            signatureHash.Add(_editorNoteGroupEditMode);
            signatureHash.Add(KeybindSettings.IsDown("editor_place_inactive_bank_modifier"));
            var hoverSignature = signatureHash.ToHashCode();
            var pointerMoved = Vector2.DistanceSquared(mousePoint, _lastEditorHoverPointer) > 1f;
            var clockMoved = double.IsNaN(_lastEditorHoverCurrentTick) || Math.Abs(currentTick - _lastEditorHoverCurrentTick) > 0.001;
            if (!_editorHoverCacheValid || pointerMoved || clockMoved || hoverSignature != _lastEditorHoverSignature)
            {
                var hoverTick = FindClickedQuarterBeat(mousePoint, currentTick, currentState, out var hoverTrack);
                var resolvedHoverTrack = hoverTick.HasValue
                    ? GetEditorHoverMarkerTrack(hoverTrack)
                    : -1;
                _hoverBeatTick = hoverTick;
                _hoverTrack = hoverTick.HasValue ? hoverTrack : null;
                _editorHoverHit = ResolveEditorHoverHit(
                    mousePoint, hoverTick, resolvedHoverTrack, currentTick, currentState);
                _lastEditorHoverPointer = mousePoint;
                _lastEditorHoverCurrentTick = currentTick;
                _lastEditorHoverSignature = hoverSignature;
                _editorHoverCacheValid = true;
            }
        }
        else
        {
            _hoverBeatTick = null;
            _hoverTrack = null;
            _editorHoverHit = null;
            _lastEditorHoverCurrentTick = double.NaN;
            _editorHoverCacheValid = false;
        }

        // Mouse clicks use the target resolved for the visible hover marker. Touch
        // remains direct-hit based because touch has no persistent hover preview.
        var resolvedMouseHoverHit = !touchInputThisFrame && _editorHoverCacheValid
            ? _editorHoverHit
            : null;
        ChartEvent? ClickLinkHeadTarget() => touchInputThisFrame
            ? FindClickedLinkHeadMarker(point, currentTick, currentState)
            : resolvedMouseHoverHit is { } hit && hit.Kind == EditorHoverHitKind.LinkHeadMarker
                ? hit.Event : null;
        ChartEvent? ClickHoldEndTarget() => touchInputThisFrame
            ? FindClickedHoldEnd(point, currentTick, currentState)
            : resolvedMouseHoverHit is { } hit && hit.Kind == EditorHoverHitKind.HoldEndMarker
                ? hit.Event : null;
        ChartEvent? ClickNoteTarget() => touchInputThisFrame
            ? FindClickedNote(point, currentTick, currentState, out _)
            : resolvedMouseHoverHit is { } hit &&
              hit.Kind is EditorHoverHitKind.NoteCard or EditorHoverHitKind.GridCellNote
                ? hit.Event : null;
        ChartEvent? ClickLinkBodyTarget()
        {
            if (!touchInputThisFrame)
                return resolvedMouseHoverHit is { } hit && hit.Kind == EditorHoverHitKind.LinkBody
                    ? hit.Event : null;

            var snappedTick = FindClickedQuarterBeat(point, currentTick, currentState, out _);
            return FindClickedLinkChainBody(point, currentTick, currentState, snappedTick);
        }
        int? ClickGridTick(out int track)
        {
            if (!touchInputThisFrame && _editorHoverCacheValid)
            {
                track = _hoverTrack ?? -1;
                return _hoverBeatTick;
            }
            return FindClickedQuarterBeat(point, currentTick, currentState, out track);
        }


        // Ctrl+click deletes the note under the pointer instead of selecting,
        // dragging, or placing. Holding the DELETE FAMILY modifier as well (Shift
        // by default) removes the whole link chain or complete long note.
        if (_chartEditorMode && leftPressed && !touchInputThisFrame && !_editorPanelsViewEnabled &&
            !pointerOverToolbar && _editorMenuSection != EditorMenuSection.Select &&
            _editorMenuSection != EditorMenuSection.Bpm &&
            !_editorPreviewMode && !_editorSuperFlickPlacementPending && !_editorBpmPlacementPending &&
            !_editorStutterStartPlacementPending && !_editorStutterEndPlacementPending &&
            !_editorLanTestSegmentStartPlacementPending && !_editorLanTestSegmentEndPlacementPending &&
            !GetEditorTimelineScrubberBounds(virtualWidth, virtualHeight).Contains(pointerPixel) &&
            KeybindSettings.IsModifierBindingHeld("editor_delete_click_modifier"))
        {
            var clickedNote = ClickLinkHeadTarget();
            clickedNote ??= ClickHoldEndTarget();
            clickedNote ??= ClickNoteTarget();
            clickedNote ??= ClickLinkBodyTarget();
            if (clickedNote is not null)
            {
                DeleteEditorNoteAtPointer(clickedNote,
                    deleteFamily: KeybindSettings.IsModifierPartHeld("editor_delete_family"));
                return;
            }

            // Don't accidentally place a new note when the delete-click modifier
            // is held but the pointer lands on an empty lane cell.
            if (ClickGridTick(out _) is not null)
                return;
        }

        // Double-clicking one grouped note enters EDIT GROUP for that exact
        // group instance. Keep this on the chart-input path (not toolbar clicks),
        // so the group library remains the only way to arm an instance for placement.
        if (_chartEditorMode && leftPressed && pointerOverToolbar)
        {
            _editorLastGroupNoteClick = null;
            _editorLastGroupNoteClickTimestamp = 0;
            _editorLastGroupNoteClickPosition = Vector2.Zero;
        }
        if (_chartEditorMode && leftPressed && !touchInputThisFrame && !pointerOverToolbar &&
            _editorMenuSection is not (EditorMenuSection.Select or EditorMenuSection.Bpm) && !_editorPreviewMode &&
            !_editorSuperFlickPlacementPending && !_editorBpmPlacementPending &&
            !_editorStutterStartPlacementPending && !_editorStutterEndPlacementPending &&
            !_editorLanTestSegmentStartPlacementPending && !_editorLanTestSegmentEndPlacementPending &&
            !GetEditorTimelineScrubberBounds(virtualWidth, virtualHeight).Contains(pointerPixel) &&
            !KeybindSettings.IsDown("editor_connect_modifier") &&
            TryHandleEditorNoteGroupDoubleClick(point, resolvedMouseHoverHit?.Event))
            return;

        // The left timeline is a continuous scrub surface. Its five timestamps
        // are landmarks only; mouse and touch can seek to any vertical position.
        if (_chartEditorMode && !_editorTimelineScrubbing &&
            !_editorRectSelectionArmed && !_editorRectSelecting &&
            (leftPressed || touchPressed) &&
            GetEditorTimelineScrubberBounds(virtualWidth, virtualHeight).Contains(pointerPixel))
        {
            _editorTimelineScrubbing = true;
            _editorTimelineResumeAfterScrub = _playing;
            // Freeze both the chart clock and audio while dragging the timeline.
            // If playback was already paused, release must leave it paused.
            if (_playing)
            {
                _playing = false;
                _audioResyncPending = true;
            }
            _editorRectSelectionArmed = false;
            _editorBpmInputFocused = false;
        }

        if (_chartEditorMode && _editorTimelineScrubbing)
        {
            if (leftDown || touchActive)
            {
                var scrubBounds = GetEditorTimelineScrubberBounds(virtualWidth, virtualHeight);
                var trackTop = GetEditorTimelineTrackTop(scrubBounds);
                var trackBottom = GetEditorTimelineTrackBottom(scrubBounds);
                var ratio = Math.Clamp((trackBottom - point.Y) / Math.Max(1f, trackBottom - trackTop), 0f, 1f);
                _playSeconds = ChartDuration() * ratio;
            }
            else
            {
                _editorTimelineScrubbing = false;
                if (_editorTimelineResumeAfterScrub)
                {
                    _playing = true;
                    _audioResyncPending = true;
                }
                _editorTimelineResumeAfterScrub = false;
            }

            // The marquee's edge stays at its last lane position while the
            // scrubber moves the playhead. Re-resolve that screen point against
            // the new playhead so the chart-space selection can grow past the
            // amount of timeline visible on screen.
            var scrubbedTick = _tempo.SecondsToTick(_playSeconds);
            var scrubbedState = RenderGearState(scrubbedTick);
            if (_chartEditorMode && _editorRectSelecting)
            {
                UpdateEditorRectangleEndpoint(_editorRectCurrent, scrubbedTick, scrubbedState, updatePointer: false);
                if (!leftDown && !touchActive)
                    FinishEditorRectangleSelection(scrubbedTick, scrubbedState);
            }
            return;
        }

        // An empty-point press may become a rectangle selection once it moves.
        // Delay committing its chart change until release, so the temporary note
        // or connector target can be cancelled without creating an undo step.
        if (_chartEditorMode && _editorRectSelecting)
        {
            // Once a drag gesture has started, keep capturing the pointer even
            // when it passes over toolbar buttons. Toolbar controls only get a
            // chance to handle the initial press; hovering them must not pause a
            // chart-space selection gesture.
            UpdateEditorRectangleEndpoint(point, currentTick, currentState, updatePointer: true);
            if (!leftDown && !touchActive)
                FinishEditorRectangleSelection(currentTick, currentState);
            return;
        }
        if (_chartEditorMode && _editorRectSelectionArmed)
        {
            var rectanglePointerHeld = leftDown || touchActive;
            if (rectanglePointerHeld && Vector2.DistanceSquared(point, _editorRectStart) >= 36f)
            {
                if (_editorPendingRectangleUndoSnapshot is not null)
                    RestoreEditorChartSnapshot(_editorPendingRectangleUndoSnapshot);
                ClearEditorPendingRectangleUndoSnapshot();
                _editorRectSelectionArmed = false;
                _editorRectSelecting = true;
                _editorRectCurrent = point;
                UpdateEditorRectangleEndpoint(point, currentTick, currentState, updatePointer: false);
                _selectedEventKey = null;
                _selectedBeatTick = null;
                _selectedBeatTrack = null;
                _editorSelectedLinkRoot = null;
                _editorSelectedHoldEnd = false;
                ClearEditorMultiSelection();
                ClearEditorNoteGroupLibrarySelection();
                _editorSelectedGridCells.Clear();
                _editorPanelsSelectedEmptyCell = null;
                _editorSelectReferenceLineTick = null;
                _statusMessage = "Selecting notes and empty grid cells…";
                return;
            }
            if (!rectanglePointerHeld)
            {
                var pendingSnapshot = _editorPendingRectangleUndoSnapshot;
                var groupChangesAlreadySynchronized = _editorPendingRectangleGroupSynchronizationApplied;
                ClearEditorPendingRectangleUndoSnapshot();
                _editorRectSelectionArmed = false;
                // A click can mutate the chart and synchronize sibling group
                // instances immediately. On release, finalize the same snapshot
                // without applying its movement/link deltas a second time.
                if (pendingSnapshot is not null)
                    CommitEditorUndoSnapshot(pendingSnapshot,
                        groupChangesAlreadySynchronized: groupChangesAlreadySynchronized);
                return;
            }
        }

        if (_editorDraggedEvent is not null)
        {
            var dragHeld = _editorDragStartedByTouch ? touchActive : leftDown;
            if (dragHeld)
            {
                if (touchMoved || mouseMoved || touchPressed)
                    UpdateEditorDrag(point, currentTick, currentState);
            }
            else
            {
                FinishEditorDrag();
            }
            // Do not allow a release after dragging to also select/place a tick.
            return;
        }

        if (rightPressed && !touchPressed &&
            !(_chartEditorMode && _editorMenuSection == EditorMenuSection.Select))
        {
            _selectedEventKey = null;
            _selectedBeatTick = null;
            _selectedBeatTrack = null;
            _editorSelectedLinkRoot = null;
            _editorSelectedHoldEnd = false;
            ClearEditorMultiSelection();
            ClearEditorNoteGroupLibrarySelection();
            _editorSelectedGridCells.Clear();
            _editorPanelsSelectedEmptyCell = null;
            _editorSelectReferenceLineTick = null;
            _editorSelectReferenceTickOverride = null;
        }

        if (!(leftPressed || touchPressed))
            return;

        // Visible toolbar controls take priority over bank indicators if their
        // screen rectangles overlap. Both are drawn in the same corner regions,
        // so hit-testing must follow visual stacking order.
        if (_chartEditorMode && !editorLuaUiActive && TryHandleChartEditorToolbarClick(point, virtualWidth, virtualHeight))
            return;
        if (_chartEditorMode && _editorSuperFlickPlacementPending)
        {
            // A click on a visible bank indicator selects that target directly
            // while placement is pending, rather than changing the preview route.
            if (TryGetClickedEditorBankIndicator(point, virtualWidth, virtualHeight, out var indicatorSide, out var indicatorBank))
            {
                if (string.Equals(indicatorSide, _editorSuperFlickPendingSide, StringComparison.OrdinalIgnoreCase))
                    SelectEditorSuperFlickTarget(indicatorBank);
                else
                    _statusMessage = $"Choose a target bank on the {_editorSuperFlickPendingSide} side.";
                return;
            }

            // The actual projected lane is also a target picker. This lets the
            // user click the physical 1K/2K/3K bank instead of only the submenu.
            if (FindClickedQuarterBeat(point, currentTick, currentState, out var targetTrack) is not null)
            {
                var targetPosition = GearState.Position(targetTrack);
                if (targetPosition is not null &&
                    string.Equals(targetPosition.Value.Side, _editorSuperFlickPendingSide, StringComparison.OrdinalIgnoreCase))
                {
                    SelectEditorSuperFlickTarget(targetPosition.Value.Bank);
                    return;
                }

                _statusMessage = $"Choose a target bank on the {_editorSuperFlickPendingSide} side.";
                return;
            }

            CancelEditorSuperFlickPlacement("SuperFlick placement cancelled because no target bank was selected.");
            return;
        }
        if (_chartEditorMode && TryHandleEditorBankIndicatorClick(point, virtualWidth, virtualHeight))
            return;

        // Segment placement is a one-shot chart gesture. Handle it before the
        // exclusive SELECT/BPM/STUTTER modes so the INFO → TEST controls work
        // regardless of which editor tool was previously open.
        if (_chartEditorMode &&
            (_editorLanTestSegmentStartPlacementPending || _editorLanTestSegmentEndPlacementPending))
        {
            var chosenTick = FindClickedQuarterBeat(point, currentTick, currentState, out _);
            if (chosenTick is not int snappedTick)
            {
                _statusMessage = _editorLanTestSegmentStartPlacementPending
                    ? "Tap inside a visible lane to set the test start."
                    : "Tap inside a visible lane to set the test end.";
                return;
            }

            SetEditorLanTestSegmentPosition(_editorLanTestSegmentStartPlacementPending, snappedTick);
            return;
        }

        // A placement tool normally gets first chance at lane clicks, but an
        // outside-lane press cannot place anything. Arm the canvas gesture before
        // that tool path returns so dragging into the playfield still becomes a
        // marquee selection even with a note or connector armed.
        if (_chartEditorMode && !_editorPanelsViewEnabled && (leftPressed || touchPressed) &&
            (_editorPlacementEnabled || _editorConnectorMode || _editorNoteGroupPlacementArmed) &&
            _editorMenuSection is not (EditorMenuSection.Select or EditorMenuSection.Bpm or EditorMenuSection.Stutter) &&
            !_editorPreviewMode &&
            !GetEditorTimelineScrubberBounds(virtualWidth, virtualHeight).Contains(pointerPixel) &&
            ClickGridTick(out _) is null &&
            ClickLinkHeadTarget() is null &&
            ClickHoldEndTarget() is null &&
            ClickNoteTarget() is null &&
            ClickLinkBodyTarget() is null &&
            !_editorRectSelectionArmed && !_editorRectSelecting)
        {
            ArmEditorRectangleSelection(point, currentTick, currentState);
            // This press began outside every lane/note hit target, so the armed
            // tool has nothing to place or select. Let the canvas gesture own it
            // immediately; the next frames will promote it to a marquee if moved.
            return;
        }

        // While SELECT is open, a click/drag on the projected lane places the
        // cyan X-range line. This is deliberately separate from ordinary note
        // selection and from the orange freeform rectangle.
        if (_chartEditorMode && _editorMenuSection == EditorMenuSection.Select &&
            !pointerOverToolbar &&
            !GetEditorTimelineScrubberBounds(virtualWidth, virtualHeight).Contains(pointerPixel) &&
            BeginEditorSelectLineDrag(point, currentTick, currentState, virtualWidth, virtualHeight, panelsView: false))
            return;

        // A click that wasn't on a lane still belongs to SELECT's submenu mode.
        // This blocks direct note selection, dragging, and orange marquee creation.
        if (_chartEditorMode && _editorMenuSection == EditorMenuSection.Select && !pointerOverToolbar)
            return;

        // Toolbar clicks leave the rectangle available for SELECT's X=SELECTED
        // option. A new lane/tick click replaces it and clears the empty-cell area.
        if (_editorSelectedGridCells.Count > 0)
        {
            _editorSelectedGridCells.Clear();
            _editorPanelsSelectedEmptyCell = null;
            _editorSelectReferenceLineTick = null;
            _editorSelectReferenceTickOverride = null;
        }

        // Start/end buttons arm one-shot placement on a visible lane. Pressing the
        // same button again, or Escape, cancels the pending placement.
        if (_chartEditorMode && _editorMenuSection == EditorMenuSection.Stutter)
        {
            if (_editorStutterStartPlacementPending || _editorStutterEndPlacementPending)
            {
                var chosenTick = FindClickedQuarterBeat(point, currentTick, currentState, out _);
                if (chosenTick is not int snappedTick)
                {
                    _statusMessage = _editorStutterStartPlacementPending
                        ? "Tap inside a visible lane to set the generator start."
                        : "Tap inside a visible lane to set the generator end.";
                    return;
                }

                if (_editorStutterStartPlacementPending)
                {
                    var oldStart = _editorStutterStartTick;
                    var priorDuration = oldStart >= 0 && _editorStutterEndTick > oldStart
                        ? _editorStutterEndTick - oldStart
                        : -1;
                    _editorStutterStartTick = snappedTick;
                    _editorStutterStartPlacementPending = false;
                    var chartStartBpm = GetEditorRampStartBpmForDisplay();
                    if (double.IsFinite(chartStartBpm) && chartStartBpm >= 1.0 && chartStartBpm <= float.MaxValue)
                        _editorRampStartBpm = chartStartBpm;
                    if (priorDuration > 0 && priorDuration <= ushort.MaxValue && snappedTick <= int.MaxValue - priorDuration)
                    {
                        _editorStutterEndTick = snappedTick + priorDuration;
                    }
                    else if (_editorStutterEndTick <= snappedTick)
                    {
                        _editorStutterEndTick = -1;
                    }
                    _statusMessage = $"Generator start set to tick {snappedTick}.";
                }
                else
                {
                    if (_editorStutterStartTick < 0)
                    {
                        _statusMessage = "Set the generator start before setting its end.";
                        return;
                    }
                    if (snappedTick <= _editorStutterStartTick)
                    {
                        _statusMessage = "The generator end must be after the start. Choose a later tick.";
                        return;
                    }
                    var duration = snappedTick - _editorStutterStartTick;
                    if (duration > ushort.MaxValue || _tempo is null || _tempo.TicksPerBeat <= 0)
                    {
                        _statusMessage = "The generator end is too far from its start for the chart duration field.";
                        return;
                    }
                    _editorStutterEndTick = snappedTick;
                    _editorStutterEndPlacementPending = false;
                    _statusMessage = $"Generator end set to tick {snappedTick}.";
                }
                return;
            }

            // While the stutter editor is open, lane clicks never place ordinary
            // notes. Clicking a tempo guide belonging to a stutter opens that group.
            var stutterTempo = FindClickedEditorTempoChanger(point, currentTick, currentState);
            if (stutterTempo is not null && TryOpenEditorStutterForTempoEvent(stutterTempo))
                return;

            // A lane click outside a generator group discards the previous edit
            // range. The menu stays open, ready for PICK START / PICK END.
            _editorStutterStartTick = -1;
            _editorStutterEndTick = -1;
            _editorStutterEditingGroup = null;
            _editorSelectedTempoEvent = null;
            _editorStutterStartPlacementPending = false;
            _editorStutterEndPlacementPending = false;
            _editorHoverCacheValid = false;
            _statusMessage = stutterTempo is not null
                ? $"Tempo change at tick {stutterTempo.Tick} is not part of a generator group. The previous range was cleared; use PICK START to make a new group."
                : "Previous generator range cleared. Use PICK START to make a new group.";
            return;
        }

        // BPM mode routes lane clicks to tempo-marker selection/placement instead
        // of the currently selected note tool or rectangle-selection gesture.
        if (_chartEditorMode && _editorMenuSection == EditorMenuSection.Bpm)
        {
            HandleEditorTempoLaneClick(point, currentTick, currentState);
            return;
        }

        if (_chartEditorMode && !_editorPanelsViewEnabled &&
            (leftPressed || touchPressed) && !_editorRectSelectionArmed && !_editorRectSelecting)
            ArmEditorRectangleSelection(point, currentTick, currentState);

        if (_chartEditorMode && (_editorPlacementEnabled || _editorNoteGroupPlacementArmed))
        {
            // Existing notes take priority over placement, so a note can still be
            // dragged while a note tool is selected. Empty-lane taps place new notes.
            // A link segment's duration-end handle can sit directly underneath its
            // destination point. Test endpoints first so clicking that handle edits
            // the segment end instead of selecting/moving the destination note.
            // Link heads get first refusal when hit regions overlap. This is especially
            // important for horizontal links where the hold-end marker can sit directly
            // on top of the connected head.
            var existingLinkHead = ClickLinkHeadTarget();
            var existingHoldEnd = existingLinkHead is null ? ClickHoldEndTarget() : null;
            var existingNote = existingLinkHead ?? existingHoldEnd ?? ClickNoteTarget();
            if (existingNote is not null)
            {
                if (!_editorConnectorMode && KeybindSettings.IsDown("editor_connect_modifier"))
                {
                    ToggleEditorMultiSelection(existingNote);
                    return;
                }
                _editorDurationInputValuePending = false;
                _editorRectSelectionArmed = false;
                var preserveMultiSelection = existingHoldEnd is null &&
                    _editorMultiSelectedEvents.Count > 1 && _editorMultiSelectedEvents.Contains(existingNote);
                if (!preserveMultiSelection)
                    ClearEditorMultiSelection();
                _editorSelectedHoldEnd = existingHoldEnd is not null;
                var clickedRoot = GetEditorLinkRoot(existingNote);
                if (_editorConnectorMode)
                {
                    _editorSelectedHoldEnd = false;
                    var previouslySelected = _chart?.Events.FirstOrDefault(ev =>
                        _selectedEventKey.HasValue && KeyOf(ev) == _selectedEventKey.Value &&
                        ev.Kind == "Note" && ev.RawType == 1);
                    // Modifier-click connects to the note; a normal click selects a source.
                    var connectModifierHeld = KeybindSettings.IsModifierBindingDown("editor_connect_modifier");
                    if (connectModifierHeld && previouslySelected is not null &&
                        !ReferenceEquals(previouslySelected, existingNote))
                    {
                        AddEditorConnector(existingNote.Tick, existingNote.Track, existingNote);
                        return;
                    }

                    // Keep ordinary drag editing available while Connector is on.
                    var source = clickedRoot is not null
                        ? GetEditorLinkChainEnd(existingNote)
                        : existingNote;
                    _selectedEventKey = KeyOf(source);
                    _selectedBeatTick = null;
                    _selectedBeatTrack = null;
                    _editorTrack = source.Track;
                    _editorSelectedLinkRoot = clickedRoot;
                    _statusMessage = clickedRoot is not null
                        ? $"Link source {source.Tick}/{source.Track}; hold Connect and click target."
                        : $"Connector source {source.Tick}/{source.Track}; hold Connect and click target.";
                    if (IsEditorDraggableNote(existingNote))
                        BeginEditorDrag(existingNote, existingHoldEnd is not null, touchPressed, point, currentTick, currentState);
                    return;
                }

                _selectedEventKey = KeyOf(existingNote);
                _selectedBeatTick = null;
                _selectedBeatTrack = existingNote.Track;
                _editorTrack = existingNote.Track;
                _editorSelectedLinkRoot = clickedRoot;
                if (!_editorNoteGroupPlacementArmed)
                    ApplyEditorNoteGroupSelectionForAnchor(existingNote);
                _statusMessage = existingNote.IsSuperFlick
                    ? $"SuperFlick direction → {GetEditorSuperFlickTargetLabel(existingNote)} (attr {existingNote.Attr}), tick {existingNote.Tick}, track {existingNote.Track}."
                    : existingHoldEnd is not null
                        ? $"Selected hold end: tick {GetEditorNextLinkPoint(existingNote)?.Tick ?? existingNote.EndTick}, track {existingNote.Track}; drag to resize."
                        : $"Selected note: tick {existingNote.Tick} track {existingNote.Track}; drag to move.";
                if (IsEditorDraggableNote(existingNote))
                    BeginEditorDrag(existingNote, existingHoldEnd is not null, touchPressed, point, currentTick, currentState);
                return;
            }

            var clickedChain = ClickLinkBodyTarget();
            if (clickedChain is not null)
            {
                if (!_editorConnectorMode && KeybindSettings.IsDown("editor_connect_modifier"))
                {
                    ToggleEditorMultiSelection(clickedChain);
                    return;
                }
                // Modifier-clicking any visible part of a link chain targets the whole chain.
                // Connecting resolves it to the head; a normal click selects the chain.
                var connectModifierHeld = KeybindSettings.IsModifierBindingDown("editor_connect_modifier");
                var previouslySelectedSource = _chart?.Events.FirstOrDefault(ev =>
                    _selectedEventKey.HasValue && KeyOf(ev) == _selectedEventKey.Value &&
                    ev.Kind == "Note" && ev.RawType == 1);
                var clickedChainRoot = GetEditorLinkRoot(clickedChain) ??
                                       (clickedChain.NoteType == NoteType.LinkHead ? clickedChain : null);
                if (_editorConnectorMode && connectModifierHeld && previouslySelectedSource is not null &&
                    clickedChainRoot is not null &&
                    !ReferenceEquals(GetEditorLinkRoot(previouslySelectedSource), clickedChainRoot))
                {
                    AddEditorConnector(clickedChainRoot.Tick, clickedChainRoot.Track, clickedChainRoot);
                    return;
                }

                _editorRectSelectionArmed = false;
                ClearEditorMultiSelection();
                _editorSelectedHoldEnd = false;
                _editorSelectedLinkRoot = GetEditorLinkRoot(clickedChain) ??
                                          (clickedChain.NoteType == NoteType.LinkHead ? clickedChain : null);
                var selectionPoint = _editorConnectorMode && _editorSelectedLinkRoot is not null
                    ? GetEditorLinkChainEnd(clickedChain)
                    : clickedChain;
                _selectedEventKey = KeyOf(selectionPoint);
                _selectedBeatTick = null;
                _selectedBeatTrack = null;
                _editorTrack = selectionPoint.Track;
                ApplyEditorNoteGroupSelectionForAnchor(selectionPoint);
                _statusMessage = _editorConnectorMode
                    ? $"Link source {selectionPoint.Tick}/{selectionPoint.Track}; hold Connect and click target."
                    : "Selected link chain. Tap a point to drag it.";
                return;
            }

            if (ClickGridTick(out var placementTrack) is int placementTick)
            {
                // Clicking an outer 1K lane with a regular note tool places it on
                // that side's legal middle lane. Connector mode is kept separate so
                // its target can use L1/L3/R1/R3 as a link tail.
                if (!_editorConnectorMode && !_editorNoteGroupPlacementArmed)
                    placementTrack = GetEditorHoverMarkerTrack(placementTrack);

                // The projected quad hit test can miss a note when the pointer is
                // near its edge or inside a hold body. Check the snapped cell as a
                // final guard before either placing a note or adding a connector.
                var occupiedNote = touchInputThisFrame
                    ? FindEditorNoteAtGridCell(placementTick, placementTrack)
                    : resolvedMouseHoverHit is { Kind: EditorHoverHitKind.GridCellNote } cellHit
                        ? cellHit.Event
                        : null;
                if (occupiedNote is not null)
                {
                    if (!_editorConnectorMode && KeybindSettings.IsDown("editor_connect_modifier"))
                    {
                        ToggleEditorMultiSelection(occupiedNote);
                        return;
                    }
                    var previouslySelected = _chart?.Events.FirstOrDefault(ev =>
                        _selectedEventKey.HasValue && KeyOf(ev) == _selectedEventKey.Value &&
                        ev.Kind == "Note" && ev.RawType == 1);
                    var connectModifierHeld = KeybindSettings.IsModifierBindingDown("editor_connect_modifier");
                    if (_editorConnectorMode && connectModifierHeld && previouslySelected is not null &&
                        !ReferenceEquals(previouslySelected, occupiedNote))
                    {
                        _editorRectSelectionArmed = false;
                        AddEditorConnector(occupiedNote.Tick, occupiedNote.Track, occupiedNote);
                        return;
                    }
                    _editorRectSelectionArmed = false;
                    ClearEditorMultiSelection();
                    _editorSelectedHoldEnd = false;
                    var occupiedRoot = GetEditorLinkRoot(occupiedNote);
                    var selected = _editorConnectorMode && occupiedRoot is not null
                        ? GetEditorLinkChainEnd(occupiedNote)
                        : occupiedNote;
                    _selectedEventKey = KeyOf(selected);
                    _selectedBeatTick = null;
                    _selectedBeatTrack = null;
                    _editorTrack = selected.Track;
                    _editorSelectedLinkRoot = occupiedRoot;
                    if (!_editorNoteGroupPlacementArmed)
                        ApplyEditorNoteGroupSelectionForAnchor(selected);
                    _statusMessage = _editorConnectorMode && occupiedRoot is not null
                        ? $"Link source {selected.Tick}/{selected.Track}; hold Connect and click target."
                        : _editorConnectorMode
                            ? $"Connector source {selected.Tick}/{selected.Track}; hold Connect and click target."
                            : $"Selected existing note: tick {selected.Tick}, track {selected.Track}; no overlapping note was placed.";
                    return;
                }

                ClearEditorMultiSelection();
                _editorSelectedHoldEnd = false;
                _editorTrack = placementTrack;
                _selectedBeatTick = placementTick;
                _selectedBeatTrack = placementTrack;
                if (_editorNoteGroupPlacementArmed)
                {
                    PlaceEditorNoteGroupInstance(placementTick, placementTrack);
                    return;
                }
                if (_editorSuperFlickTool == EditorSuperFlickTool.Place)
                {
                    BeginEditorSuperFlickPlacement(placementTick, placementTrack);
                }
                else if (_editorConnectorMode)
                {
                    AddEditorConnector(placementTick, placementTrack);
                }
                else
                {
                    ClearEditorNoteGroupLibrarySelection();
                    _selectedEventKey = null;
                    _editorSelectedLinkRoot = null;
                    AddEditorNote();
                }
            }
            else
            {
                _statusMessage = _editorConnectorMode
                    ? "Select a tap, hold, or link point first, then tap a target tick."
                    : "Tap inside a lane to place the selected note.";
            }
            return;
        }

        // Prefer a head marker over any overlapping duration-end marker so a
        // horizontally connected LinkHead remains easy to grab and drag.
        var linkHeadNote = ClickLinkHeadTarget();
        var holdEndNote = linkHeadNote is null ? ClickHoldEndTarget() : null;
        var note = linkHeadNote ?? holdEndNote ?? ClickNoteTarget();
        if (note is not null)
        {
            if (_chartEditorMode && !_editorConnectorMode && KeybindSettings.IsDown("editor_connect_modifier"))
            {
                ToggleEditorMultiSelection(note);
                return;
            }
            _editorDurationInputValuePending = false;
            _editorRectSelectionArmed = false;
            var preserveMultiSelection = holdEndNote is null &&
                _editorMultiSelectedEvents.Count > 1 && _editorMultiSelectedEvents.Contains(note);
            if (!preserveMultiSelection)
                ClearEditorMultiSelection();
            _editorSelectedHoldEnd = holdEndNote is not null;
            _selectedEventKey = KeyOf(note);
            _selectedBeatTick = null;
            _selectedBeatTrack = note.Track;
            _editorTrack = note.Track;
            _editorSelectedLinkRoot = GetEditorLinkRoot(note);
            if (_chartEditorMode)
                ApplyEditorNoteGroupSelectionForAnchor(note);
            _editorPanelsSelectedEmptyCell = null;
            if (_chartEditorMode && _editorMenuSection == EditorMenuSection.Select)
            {
                _editorSelectReferenceLineTick = note.Tick;
                _editorSelectedGridCells.Clear();
                _editorSelectedGridCells.Add((note.Tick, note.Track));
                _editorSelectReferenceTickOverride = null;
            }
            else
                _editorSelectReferenceLineTick = null;
            _statusMessage = note.IsSuperFlick
                ? $"SuperFlick direction → {GetEditorSuperFlickTargetLabel(note)} (attr {note.Attr}), tick {note.Tick}, track {note.Track}."
                : holdEndNote is not null
                    ? $"Selected hold end: tick {note.EndTick}, track {note.Track}; drag to resize."
                    : $"Selected note: tick {note.Tick}  track {note.Track}  attr {note.Attr}; drag to move.";
            if (_chartEditorMode && IsEditorDraggableNote(note))
                BeginEditorDrag(note, holdEndNote is not null, touchPressed, point, currentTick, currentState);
        }
        else if (ClickLinkBodyTarget() is ChartEvent chainPoint)
        {
            if (_chartEditorMode && !_editorConnectorMode && KeybindSettings.IsDown("editor_connect_modifier"))
            {
                ToggleEditorMultiSelection(chainPoint);
                return;
            }
            _editorDurationInputValuePending = false;
            _editorRectSelectionArmed = false;
            ClearEditorMultiSelection();
            _editorSelectedHoldEnd = false;
            _selectedEventKey = KeyOf(chainPoint);
            _selectedBeatTick = null;
            _selectedBeatTrack = null;
            _editorSelectedLinkRoot = GetEditorLinkRoot(chainPoint) ??
                                      (chainPoint.NoteType == NoteType.LinkHead ? chainPoint : null);
            _editorTrack = chainPoint.Track;
            if (_chartEditorMode)
                ApplyEditorNoteGroupSelectionForAnchor(chainPoint);
            _editorPanelsSelectedEmptyCell = null;
            if (_chartEditorMode && _editorMenuSection == EditorMenuSection.Select)
            {
                _editorSelectReferenceLineTick = chainPoint.Tick;
                _editorSelectedGridCells.Clear();
                _editorSelectedGridCells.Add((chainPoint.Tick, chainPoint.Track));
                _editorSelectReferenceTickOverride = null;
            }
            else
                _editorSelectReferenceLineTick = null;
            _statusMessage = "Selected link chain. Tap a head/tail/end point to drag that point.";
        }
        else if (ClickGridTick(out var selectedTrack) is int beatTick)
        {
            _editorDurationInputValuePending = false;
            ClearEditorMultiSelection();
            ClearEditorNoteGroupLibrarySelection();
            _editorSelectedHoldEnd = false;
            _selectedEventKey = null;
            _selectedBeatTick = beatTick;
            _selectedBeatTrack = selectedTrack;
            _editorSelectedLinkRoot = null;
            _editorTrack = selectedTrack;
            _editorPanelsSelectedEmptyCell = null;
            if (_chartEditorMode && _editorMenuSection == EditorMenuSection.Select)
            {
                _editorSelectedGridCells.Clear();
                _editorSelectedGridCells.Add((beatTick, selectedTrack));
                _editorSelectReferenceLineTick = beatTick;
                _editorSelectReferenceTickOverride = null;
            }
            else
                _editorSelectReferenceLineTick = null;
            _statusMessage = $"Selected 1/4 beat: tick {beatTick}, track {selectedTrack}";
        }
    }

    private static readonly int[] EditorSnapDivisors = { 2, 3, 4, 6, 8, 12, 16, 32 };
    private static readonly int[] EditorMetronomeDivisors = { 0, 1, 2, 3, 4, 6, 8, 12, 16, 32 };

    // Stable action IDs are shared by the full-width bottom toolbar and the
    // upper-right edit-action rail. The bottom toolbar stays in one row.
    private static readonly string[] EditorActionLabels =
    {
        "PLAY", "DELETE", "UNDO", "REDO", "COPY", "CUT", "PASTE", "DESELECT",
        "INFO", "VIEW 2D/3D", "EXPAND/COLLAPSE", "GRIDS", "SAVE", "PANELS",
        "PASTE FIT", "SELECT", "SNAPS", "TEMPO LINES", "CONSTANT", "SETTINGS", "METRONOME",
        "EDITOR/PREVIEW", "AUTOPLAY", "ATTR"
    };
    private static readonly int[] EditorBottomLeftActionIds =
        { EditorActionPlay, EditorActionEditorPreview, EditorActionGameplayMode,
          EditorActionViewMode, EditorActionExpand, EditorActionBeatLines, EditorActionAttributes,
          EditorActionTempoLines, EditorActionConstantScroll, EditorActionPanels,
          EditorActionMetronome, EditorActionSnap, EditorActionMeta,
          EditorActionQuickSave, EditorActionSettings };
    // The bottom-right tab row now owns SELECT; the upper-right action stack is
    // reserved for undo/redo and contextual edit operations.
    private static readonly int[] EditorRightActionIds =
        { EditorActionUndo, EditorActionRedo, EditorActionDeselect, EditorActionDelete,
          EditorActionCopy, EditorActionCut, EditorActionPaste, EditorActionPasteFit };
    private const int EditorActionPlay = 0;
    private const int EditorActionDelete = 1;
    private const int EditorActionUndo = 2;
    private const int EditorActionRedo = 3;
    private const int EditorActionCopy = 4;
    private const int EditorActionCut = 5;
    private const int EditorActionPaste = 6;
    private const int EditorActionDeselect = 7;
    private const int EditorActionMeta = 8;
    private const int EditorActionViewMode = 9;
    private const int EditorActionExpand = 10;
    private const int EditorActionBeatLines = 11;
    private const int EditorActionQuickSave = 12;
    private const int EditorActionPanels = 13;
    private const int EditorActionPasteFit = 14;
    private const int EditorActionSelect = 15;
    private const int EditorActionSnap = 16;
    private const int EditorActionTempoLines = 17;
    private const int EditorActionConstantScroll = 18;
    private const int EditorActionSettings = 19;
    private const int EditorActionMetronome = 20;
    private const int EditorActionEditorPreview = 21;
    private const int EditorActionGameplayMode = 22;
    private const int EditorActionAttributes = 23;
    private enum EditorUnlinkMode
    {
        PreserveDuration,
        Taps,
        AllTaps
    }

    private static readonly string[] EditorToolLabels =
        { "TAP", "CONNECTOR", "FLICK LEFT", "FLICK RIGHT", "VIBRATO", "SUPERFLICK", "REPLACE SELECTION" };
    private static readonly string[] EditorSuperFlickToolLabels =
        { "TARGET 1K", "TARGET 2K", "TARGET 3K" };
    // Path validation is automatic and appears in the chart-problems panel.
    // Repair also lives there so diagnostics and cleanup stay together.
    private static readonly string[] EditorEditToolLabels =
        { "LINK Y", "LINK X", "DURATION", "UNLINK (PRESERVE)", "UNLINK (TAPS)", "UNLINK (ALL TAPS)", "MIRROR BANK", "MIRROR X", "AUTO-MERGE" };

    private string GetEditorEditToolLabel(int toolIndex)
        => toolIndex == 8 ? $"AUTO-MERGE: {(_editorAutoMergeLinks ? "ON" : "OFF")}" :
            EditorEditToolLabels[toolIndex];

    private sealed record EditorLinkChainInfo(
        ChartEvent Root,
        List<ChartEvent> Nodes,
        string? Family,
        int StartTick,
        long EndTick,
        int Pair);

    private sealed record EditorLinkPathIssue(int Tick, int Track, string Message, ChartEvent? Event);

    // Route combinations start with the active banks in the initial gear.
    // Each valid SuperFlick then registers its source/destination bank pair,
    // plus any pair of banks active in the resulting gear state. Those route
    // combinations persist for the rest of the chart instead of being limited
    // to the exact tick range in which a particular gear state is active.
    private static bool EditorLinkFamiliesCanSharePair(
        GearTimeline timeline, string? familyA, string? familyB,
        int startTick, int endTickExclusive)
    {
        var sampleEnd = endTickExclusive > startTick
            ? endTickExclusive
            : startTick < int.MaxValue ? startTick + 1 : int.MaxValue;
        return timeline.FamiliesCanSharePair(familyA, familyB, sampleEnd);
    }

    // Link IDs can be handed off at an exact duration boundary when the new
    // chain starts with a LinkHead and its next LinkTail is later. A LinkTail
    // at that same boundary is different: it can be mistaken for the ending
    // point of the previous chain, so the two chains must not share an ID.
    private static ChartEvent? GetEditorLinkBoundaryTailConflict(
        EditorLinkChainInfo endingChain, EditorLinkChainInfo startingChain)
    {
        if (endingChain.EndTick != startingChain.StartTick)
            return null;

        return startingChain.Nodes
            .Where(node => node.NoteType == NoteType.LinkTail && node.Tick == startingChain.StartTick)
            .OrderBy(node => node.Track)
            .ThenBy(node => node.EventIndex)
            .FirstOrDefault();
    }

    private static bool EditorLinkIntervalsConflict(
        GearTimeline timeline,
        string? familyA, int startA, long endA, bool hasTailAtStartA,
        string? familyB, int startB, long endB, bool hasTailAtStartB)
    {
        var overlapStart = Math.Max(startA, startB);
        var overlapEnd = Math.Min(endA, endB);
        if (overlapStart < overlapEnd &&
            EditorLinkFamiliesCanSharePair(timeline, familyA, familyB, overlapStart,
                (int)Math.Min(int.MaxValue, overlapEnd)))
        {
            return true;
        }

        if (endA == startB && hasTailAtStartB && startB < int.MaxValue &&
            EditorLinkFamiliesCanSharePair(timeline, familyA, familyB, startB, startB + 1))
        {
            return true;
        }

        return endB == startA && hasTailAtStartA && startA < int.MaxValue &&
               EditorLinkFamiliesCanSharePair(timeline, familyA, familyB, startA, startA + 1);
    }

    private static bool EditorLinkChainsConflictInTime(
        GearTimeline timeline, EditorLinkChainInfo a, EditorLinkChainInfo b)
    {
        var aHasTailAtStart = a.Nodes.Any(node => node.NoteType == NoteType.LinkTail && node.Tick == a.StartTick);
        var bHasTailAtStart = b.Nodes.Any(node => node.NoteType == NoteType.LinkTail && node.Tick == b.StartTick);
        return EditorLinkIntervalsConflict(
            timeline,
            a.Family, a.StartTick, a.EndTick, aHasTailAtStart,
            b.Family, b.StartTick, b.EndTick, bHasTailAtStart);
    }

    private bool HasSelectedEditorNotes()
    {
        if (_chart is null)
            return false;
        if (_editorMultiSelectedEvents.Count > 0)
        {
            EnsureEditorMultiSelectionGroupSummary();
            if (_editorMultiSelectionHasLogicalNote)
                return true;
        }
        return _selectedEventKey.HasValue &&
               TryGetEditorEvent(_selectedEventKey) is { } selected &&
               selected.Kind == "Note" && selected.RawType == 1;
    }

    private bool HasAnyEditorSelection()
        => _selectedEventKey.HasValue || _selectedBeatTick.HasValue || _editorSelectedTempoEvent is not null ||
           _editorStutterEditingGroup is not null || _editorMultiSelectedEvents.Count > 0 ||
           _editorSelectedGridCells.Count > 0;

    // In the 1K/Forward bank, ordinary notes (including SuperFlicks and link
    // heads) belong on the two middle tracks. The outer L1/L3/R1/R3 tracks are
    // reserved for LinkTail endpoints. Other banks keep their existing layout.
    private static bool IsEditorTrackAllowedForNoteType(int track, NoteType noteType)
    {
        var position = GearState.Position(track);
        if (position is null)
            return false;
        if (!string.Equals(position.Value.Bank, "Forward", StringComparison.OrdinalIgnoreCase))
            return true;
        return position.Value.LocalPos == 1 || noteType == NoteType.LinkTail;
    }

    private static int GetEditorAllowedTrackForNoteType(int track, NoteType noteType)
    {
        if (IsEditorTrackAllowedForNoteType(track, noteType))
            return track;
        var position = GearState.Position(track);
        if (position is null || !string.Equals(position.Value.Bank, "Forward", StringComparison.OrdinalIgnoreCase))
            return track;
        return EditorTrackForBankPosition(position.Value.Side, position.Value.Bank, 1);
    }

    private string GetEditorSelectionYLabel() => _editorSelectYMode switch
    {
        EditorSelectYMode.Above => "ABOVE",
        EditorSelectYMode.Below => "BELOW",
        _ => "ALL"
    };

    private int? GetEditorSelectLineSeedTick()
    {
        if (_editorSelectedGridCells.Count > 0)
            return _editorSelectedGridCells.Min(cell => cell.Tick);
        if (_editorPanelsSelectedEmptyCell is { } emptyCell)
            return emptyCell.Tick;
        if (_selectedBeatTick.HasValue)
            return _selectedBeatTick.Value;
        if (_selectedEventKey.HasValue && _chart is not null)
        {
            var selected = _chart.Events.FirstOrDefault(ev => KeyOf(ev) == _selectedEventKey.Value);
            if (selected is not null)
                return selected.Tick;
        }
        if (_editorSelectedTempoEvent is not null)
            return _editorSelectedTempoEvent.Tick;
        if (_editorMultiSelectedEvents.Count > 0)
        {
            EnsureEditorMultiSelectionGroupSummary();
            return _editorMultiSelectionMinimumTick ?? 0;
        }
        return null;
    }

    private int GetEditorSelectionReferenceTick()
    {
        // The cyan SELECT line is the visible source of truth while its menu is open.
        if (_editorMenuSection == EditorMenuSection.Select && _editorSelectReferenceLineTick.HasValue)
            return _editorSelectReferenceLineTick.Value;
        // A single empty panel cell is useful even before opening SELECT. Its tick
        // takes priority over note/event selection and can seed the reference line.
        if (_editorPanelsViewEnabled && _editorPanelsSelectedEmptyCell is { } emptyCell)
            return emptyCell.Tick;
        // In Panels view, Y RANGE is relative to the selected cell/range, not the
        // first note that happens to lie inside it.
        if (_editorPanelsViewEnabled && _editorSelectedGridCells.Count > 0)
            return _editorSelectedGridCells.Min(cell => cell.Tick);
        if (_selectedBeatTick.HasValue)
            return _selectedBeatTick.Value;
        if (_selectedEventKey.HasValue && _chart is not null)
        {
            var selected = _chart.Events.FirstOrDefault(ev => KeyOf(ev) == _selectedEventKey.Value);
            if (selected is not null)
                return selected.Tick;
        }
        if (_editorSelectedTempoEvent is not null)
            return _editorSelectedTempoEvent.Tick;
        if (_editorSelectReferenceTickOverride.HasValue)
            return _editorSelectReferenceTickOverride.Value;
        if (_editorSelectedGridCells.Count > 0)
            return _editorSelectedGridCells.Min(cell => cell.Tick);
        if (_editorMultiSelectedEvents.Count > 0)
        {
            EnsureEditorMultiSelectionGroupSummary();
            return _editorMultiSelectionMinimumTick ?? 0;
        }
        if (_tempo is null)
            return 0;
        var snap = Math.Max(1.0, GetEditorSnapTicks());
        var currentTick = _tempo.SecondsToTick(_playSeconds);
        return Math.Max(0, (int)Math.Round(currentTick / snap, MidpointRounding.AwayFromZero) * (int)Math.Round(snap));
    }

    private void ApplyEditorSelectFilters()
    {
        if (_chart is null)
            return;

        var selectionBefore = CaptureEditorChartSnapshot(selectionOnly: true);
        var panelsView = _editorPanelsViewEnabled;
        var referenceTick = GetEditorSelectionReferenceTick();
        HashSet<int> selectedTracks;
        if (_editorSelectXSelected)
        {
            selectedTracks = _editorSelectedGridCells
                .Select(cell => cell.Track)
                .Where(track => panelsView ? track is >= 17 and <= 20 : IsPlayableEditorTrack(track))
                .ToHashSet();
            if (selectedTracks.Count == 0)
            {
                var trackLimit = panelsView ? 4 : 16;
                foreach (var note in _editorMultiSelectedEvents)
                {
                    if (panelsView ? note.Track is >= 17 and <= 20 : IsPlayableEditorTrack(note.Track))
                        selectedTracks.Add(note.Track);
                    if (selectedTracks.Count >= trackLimit)
                        break;
                }
            }
            if (selectedTracks.Count == 0 && _selectedBeatTrack.HasValue &&
                (panelsView ? _selectedBeatTrack.Value is >= 17 and <= 20 : IsPlayableEditorTrack(_selectedBeatTrack.Value)))
                selectedTracks.Add(_selectedBeatTrack.Value);
            if (selectedTracks.Count == 0 && _selectedEventKey.HasValue)
            {
                var selected = _chart.Events.FirstOrDefault(ev => KeyOf(ev) == _selectedEventKey.Value);
                if (selected is not null &&
                    (panelsView ? selected.Track is >= 17 and <= 20 : IsPlayableEditorTrack(selected.Track)))
                    selectedTracks.Add(selected.Track);
            }
            if (selectedTracks.Count == 0)
            {
                _statusMessage = panelsView
                    ? "X=SELECTED needs a cyan line range first. Click/drag across animation tracks, then apply SELECT."
                    : "X=SELECTED needs a cyan line range first. Click/drag across lanes, then apply SELECT.";
                return;
            }
        }
        else
        {
            selectedTracks = panelsView
                ? Enumerable.Range(17, 4).ToHashSet()
                : Enumerable.Range(0, 16).ToHashSet();
        }

        ClearEditorMultiSelection();
        var selectedNoteCount = 0;
        foreach (var ev in _chart.Events)
        {
            if (ev.Kind != "Note" || ev.RawType != 1 || !selectedTracks.Contains(ev.Track) ||
                !(panelsView
                    ? ev.Track is >= 17 and <= 20 && ev.Attr is >= 101 and <= 108
                    : IsPlayableEditorTrack(ev.Track)) ||
                !(_editorSelectYMode switch
                {
                    EditorSelectYMode.Above => ev.Tick > referenceTick,
                    EditorSelectYMode.Below => ev.Tick < referenceTick,
                    _ => true
                }))
                continue;

            AddEditorMultiSelectedEvent(ev);
            selectedNoteCount++;
        }

        if (_editorNoteGroupEditMode && _editorEditingNoteGroupId is int editingGroupId &&
            _editorEditingNoteGroupInstanceId is int editingInstanceId &&
            _editorMultiSelectedEvents.Any(ev => ev.NoteGroupId != editingGroupId ||
                ev.NoteGroupInstanceId != editingInstanceId))
        {
            _editorNoteGroupEditMode = false;
            _editorEditingNoteGroupId = null;
            _editorEditingNoteGroupInstanceId = null;
            _statusMessage = "Exited EDIT GROUP because SELECT includes notes outside the active instance.";
        }

        ExpandEditorNoteGroupSelection(_editorMultiSelectedEvents);
        _selectedEventKey = null;
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorSelectedLinkRoot = null;
        _editorSelectedHoldEnd = false;
        _editorPlacementEnabled = false;
        _editorConnectorMode = false;
        _editorPanelsConnectorAwaitTarget = false;
        _statusMessage = $"SELECT: {_editorSelectYMode.ToString().ToUpperInvariant()}, X={(_editorSelectXSelected ? "SELECTED" : "ALL")}; selected {selectedNoteCount} {(panelsView ? "animation" : "") } note(s) relative to tick {referenceTick}.";
        CommitEditorSelectionUndoSnapshot(selectionBefore);
    }

    private void HandleEditorSelectMenuChildClick(int childIndex)
    {
        if (childIndex == 0)
        {
            _editorSelectYMode = _editorSelectYMode switch
            {
                EditorSelectYMode.All => EditorSelectYMode.Above,
                EditorSelectYMode.Above => EditorSelectYMode.Below,
                _ => EditorSelectYMode.All
            };
            _statusMessage = $"SELECT Y: {GetEditorSelectionYLabel()} (reference tick {GetEditorSelectionReferenceTick()}).";
        }
        else if (childIndex == 1)
        {
            _editorSelectXSelected = !_editorSelectXSelected;
            _statusMessage = $"SELECT X: {(_editorSelectXSelected ? "SELECTED" : "ALL")}.";
        }
        else if (childIndex == 2)
        {
            ApplyEditorSelectFilters();
        }
    }

    private List<int> GetVisibleEditorActionIds() => EditorRightActionIds.ToList();

    private bool IsEditorClipboardCompleteNoteGroupInstance()
    {
        if (_chart is null || _editorPanelsViewEnabled || _editorClipboard.Count == 0 ||
            !_editorClipboardIsNoteGroupInstance || _editorClipboardGroupSources.Count == 0)
            return false;

        // Validate every captured instance independently. Clipboard snapshots are
        // authoritative so COPY/CUT still works after the source notes or library
        // definition have been pruned from the live chart.
        var capturedKeys = new HashSet<(int GroupId, int InstanceId)>();
        foreach (var source in _editorClipboardGroupSources)
        {
            var key = (source.Instance.GroupId, source.Instance.InstanceId);
            if (!capturedKeys.Add(key) || source.Definition.Id != key.GroupId ||
                source.Definition.Members.Count == 0)
                return false;

            var entries = _editorClipboard.Where(entry => entry.NoteGroupId == key.GroupId &&
                entry.NoteGroupInstanceId == key.InstanceId).ToList();
            if (entries.Any(entry => entry.OriginalTrack is < 0 or > 15 ||
                    entry.SourceEvent.RawType != 1 || entry.NoteGroupMemberId is null))
                return false;

            var copiedMemberIds = entries.Select(entry => entry.NoteGroupMemberId!.Value).ToHashSet();
            var definedMemberIds = source.Definition.Members.Select(member => member.Id).ToHashSet();
            if (entries.Count != source.Definition.Members.Count ||
                copiedMemberIds.Count != entries.Count || !copiedMemberIds.SetEquals(definedMemberIds))
                return false;
        }

        // Every grouped clipboard entry must be covered by one captured complete
        // instance. Ungrouped notes may coexist with grouped instances in the same
        // clipboard and retain their ordinary paste behavior.
        return _editorClipboard.Where(entry => entry.NoteGroupId.HasValue || entry.NoteGroupInstanceId.HasValue ||
                entry.NoteGroupMemberId.HasValue)
            .All(entry => entry.NoteGroupId.HasValue && entry.NoteGroupInstanceId.HasValue &&
                entry.NoteGroupMemberId.HasValue &&
                capturedKeys.Contains((entry.NoteGroupId.Value, entry.NoteGroupInstanceId.Value)));
    }

    private bool IsEditorActionVisible(int actionId) => actionId switch
    {
        EditorActionDelete => HasSelectedEditorNotes() || _editorSelectedTempoEvent is not null,
        EditorActionCopy or EditorActionCut => HasSelectedEditorNotes(),
        EditorActionPaste or EditorActionPasteFit => _editorClipboard.Count > 0,
        EditorActionDeselect => HasAnyEditorSelection() || _editorSelectedGridCells.Count > 0,
        _ => true
    };

    private double GetEditorSnapTicks()
        => _tempo is null ? 1.0 : Math.Max(1.0, Math.Round(_tempo.TicksPerBeat / Math.Max(1, _editorSnapDivisor), MidpointRounding.AwayFromZero));

    private Rectangle GetEditorTopStripPanelBounds(int virtualWidth, int virtualHeight)
    {
        var unit = _projection.ScalePixels(1f);
        var margin = (int)MathF.Round((float)_chartEditorInterfaceLayout.EdgeMargin * unit);
        var bottomStrip = GetEditorBottomLeftStripBounds(virtualWidth, virtualHeight);
        var stripGap = Math.Max(1, (int)MathF.Round((float)_chartEditorInterfaceLayout.PanelGap * unit));
        if (_editorPanelsViewEnabled)
        {
            var padding = Math.Max(1, (int)MathF.Round(5f * unit));
            var desiredWidth = (int)MathF.Round((float)_chartEditorInterfaceLayout.PanelsToolbarWidth * unit);
            var availableWidth = Math.Max(76, virtualWidth - margin * 2 - padding * 2);
            var responsiveWidthCap = Math.Max(76, (int)MathF.Round(availableWidth * 0.46f));
            var width = Math.Min(desiredWidth, Math.Min(availableWidth, responsiveWidthCap));
            var attributeGap = (int)MathF.Round(4f * unit);
            // The panels toolbar contains its header, CONNECTOR, four rows of
            // attributes, DURATION, SELECT, and REPLACE SELECTION. Dock it above the
            // full-width bottom toolbar rather than against the window edge.
            var contentHeight = (int)MathF.Round(38f * unit) + attributeGap +
                                8 * ((int)MathF.Round(32f * unit) + attributeGap);
            var panelHeight = contentHeight + padding * 2;
            return new Rectangle(virtualWidth - margin - width - padding * 2,
                Math.Max(margin, bottomStrip.Y - stripGap - panelHeight), width + padding * 2, panelHeight);
        }

        // Notes / Tools / BPM / Select / Note Groups form a fixed vertical
        // strip immediately above the bottom toolbar. Expanded menus open left.
        var paddingFixed = Math.Max(1, (int)MathF.Round(8f * unit));
        var desiredContentWidth = (int)MathF.Round(152f * unit);
        var availableContentWidth = Math.Max(76, virtualWidth - margin * 2 - paddingFixed * 2);
        var widthFixed = Math.Min(desiredContentWidth, availableContentWidth);
        var tabHeight = (int)MathF.Round(34f * unit);
        var tabGap = Math.Max(1, (int)MathF.Round(5f * unit));
        var panelHeightFixed = tabHeight * 5 + tabGap * 4 + paddingFixed * 2;
        return new Rectangle(virtualWidth - margin - widthFixed - paddingFixed * 2,
            Math.Max(margin, bottomStrip.Y - stripGap - panelHeightFixed),
            widthFixed + paddingFixed * 2, panelHeightFixed);
    }

    private Rectangle GetEditorMenuPanelBounds(int virtualWidth, int virtualHeight)
    {
        // Panels SELECT and DURATION are single-column submenus sized exactly
        // like the PANELS • ATTRIBUTES panel, so they don't sprawl over the chart.
        if (_editorPanelsViewEnabled && (_editorMenuSection is EditorMenuSection.Select or EditorMenuSection.Duration))
        {
            // Keep these locals distinct from the gameplay-menu layout variables
            // below; C# forbids redeclaring those names in this nested scope.
            var selectUnit = _projection.ScalePixels(1f);
            var selectMargin = (int)MathF.Round((float)_chartEditorInterfaceLayout.EdgeMargin * selectUnit);
            var selectPadding = GetEditorMenuPanelPadding(selectUnit);
            var selectHeaderGap = GetEditorMenuHeaderContentGap(selectUnit);
            var selectHeaderHeight = (int)MathF.Round(34f * selectUnit);
            var selectRows = GetEditorMenuRowCount();
            var selectContentHeight = selectHeaderHeight + selectHeaderGap +
                GetEditorMenuRowsHeight(selectRows, selectUnit) + selectHeaderGap;
            var selectPanelHeight = selectContentHeight + selectPadding * 2;
            var selectStrip = GetEditorTopStripPanelBounds(virtualWidth, virtualHeight);
            var selectStripGap = Math.Max(1, (int)MathF.Round((float)_chartEditorInterfaceLayout.PanelGap * selectUnit));
            var anchorButton = _editorMenuSection == EditorMenuSection.Duration
                ? GetEditorPanelsDurationButtonBounds(virtualWidth, virtualHeight)
                : GetEditorPanelsSelectButtonBounds(virtualWidth, virtualHeight);
            var selectMaxY = Math.Max(selectMargin, selectStrip.Bottom - selectPanelHeight);
            var selectY = Math.Clamp(anchorButton.Y, selectMargin, selectMaxY);
            return new Rectangle(Math.Max(selectMargin, selectStrip.X - selectStripGap - selectStrip.Width),
                selectY, selectStrip.Width, selectPanelHeight);
        }

        // Other menus use their own responsive width. The Panels attributes
        // strip remains fixed below them.
        if ((_editorPanelsViewEnabled && _editorMenuSection is not (EditorMenuSection.Select or EditorMenuSection.Duration)) ||
            (!_editorPanelsViewEnabled && (_editorMenuSection is EditorMenuSection.None or EditorMenuSection.Snap or EditorMenuSection.Metronome)))
            return Rectangle.Empty;

        var unit = _projection.ScalePixels(1f);
        var margin = (int)MathF.Round((float)_chartEditorInterfaceLayout.EdgeMargin * unit);
        var padding = GetEditorMenuPanelPadding(unit);
        if (!_editorPanelsViewEnabled && _editorMenuSection == EditorMenuSection.NoteGroups)
        {
            // Keep the Note Groups panel locals distinct from the general menu
            // layout locals declared later in this method.
            var groupDesiredWidth = (int)MathF.Round(520f * unit);
            var groupAvailableWidth = Math.Max(76, virtualWidth - margin * 2 - padding * 2);
            var groupResponsiveWidthCap = Math.Max(76, (int)MathF.Round(groupAvailableWidth * 0.46f));
            var groupWidth = Math.Min(groupDesiredWidth, Math.Min(groupAvailableWidth, groupResponsiveWidthCap));
            var groupPanelHeight = (int)MathF.Round(EditorNoteGroupMenuPanelHeight * unit);
            var groupStrip = GetEditorTopStripPanelBounds(virtualWidth, virtualHeight);
            var groupStripGap = Math.Max(1, (int)MathF.Round((float)_chartEditorInterfaceLayout.PanelGap * unit));
            var groupAnchor = GetEditorMenuParentBounds(3, virtualWidth, virtualHeight);
            var groupMaxY = Math.Max(margin, groupStrip.Bottom - groupPanelHeight);
            var groupY = Math.Clamp(groupAnchor.Y, margin, groupMaxY);
            var groupPanelWidth = groupWidth + padding * 2;
            return new Rectangle(Math.Max(margin, groupStrip.X - groupStripGap - groupPanelWidth),
                groupY, groupPanelWidth, groupPanelHeight);
        }
        var tickPicking = _editorMenuSection == EditorMenuSection.Stutter &&
                          (_editorStutterStartPlacementPending || _editorStutterEndPlacementPending);
        var desiredWidthBase = tickPicking ? 300f :
            _editorMenuSection == EditorMenuSection.Stutter ? 640f :
            _editorMenuSection == EditorMenuSection.Tools ? 520f : (float)_chartEditorInterfaceLayout.ToolbarWidth;
        var desiredWidth = (int)MathF.Round(desiredWidthBase * unit);
        var availableWidth = Math.Max(76, virtualWidth - margin * 2 - padding * 2);
        var responsiveWidthCap = Math.Max(76, (int)MathF.Round(availableWidth * 0.46f));
        var width = Math.Min(desiredWidth, Math.Min(availableWidth, responsiveWidthCap));

        var headerGap = GetEditorMenuHeaderContentGap(unit);
        var headerHeight = (int)MathF.Round(34f * unit);
        var rows = GetEditorMenuRowCount();
        var contentHeight = headerHeight;
        if (rows > 0)
            contentHeight += headerGap + GetEditorMenuRowsHeight(rows, unit) + headerGap;
        var panelHeight = contentHeight + padding * 2;

        var strip = GetEditorTopStripPanelBounds(virtualWidth, virtualHeight);
        var stripGap = Math.Max(1, (int)MathF.Round((float)_chartEditorInterfaceLayout.PanelGap * unit));
        var rootIndex = GetEditorMenuRootSection() switch
        {
            EditorMenuSection.Notes => 0,
            EditorMenuSection.Tools => 1,
            EditorMenuSection.Bpm => 2,
            EditorMenuSection.NoteGroups => 3,
            EditorMenuSection.Select => 4,
            _ => 0
        };
        var anchor = GetEditorMenuParentBounds(rootIndex, virtualWidth, virtualHeight);
        var panelWidth = width + padding * 2;
        var maxY = Math.Max(margin, strip.Bottom - panelHeight);
        var y = Math.Clamp(anchor.Y, margin, maxY);
        return new Rectangle(Math.Max(margin, strip.X - stripGap - panelWidth),
            y, panelWidth, panelHeight);
    }

    private Rectangle GetEditorPanelsSelectButtonBounds(int virtualWidth, int virtualHeight)
    {
        var unit = _projection.ScalePixels(1f);
        var panel = GetEditorTopStripPanelBounds(virtualWidth, virtualHeight);
        var padding = (int)MathF.Round(5f * unit);
        var headerHeight = (int)MathF.Round(38f * unit);
        var gap = (int)MathF.Round(4f * unit);
        var buttonHeight = (int)MathF.Round(32f * unit);
        // SELECT occupies the second-last row, immediately above REPLACE SELECTION.
        // Its handler opens the shared Y RANGE / X RANGE / APPLY SELECT menu; it is
        // not a mode toggle.
        var firstRowY = panel.Y + padding + headerHeight + gap;
        var rowStride = buttonHeight + gap;
        return new Rectangle(panel.X + padding, firstRowY + 6 * rowStride,
            panel.Width - padding * 2, buttonHeight);
    }

    private Rectangle GetEditorPanelsDurationButtonBounds(int virtualWidth, int virtualHeight)
    {
        var unit = _projection.ScalePixels(1f);
        var panel = GetEditorTopStripPanelBounds(virtualWidth, virtualHeight);
        var padding = (int)MathF.Round(5f * unit);
        var headerHeight = (int)MathF.Round(38f * unit);
        var gap = (int)MathF.Round(4f * unit);
        var buttonHeight = (int)MathF.Round(32f * unit);
        var firstRowY = panel.Y + padding + headerHeight + gap;
        var rowStride = buttonHeight + gap;
        return new Rectangle(panel.X + padding, firstRowY + 5 * rowStride,
            panel.Width - padding * 2, buttonHeight);
    }

    private Rectangle GetEditorPanelsReplaceButtonBounds(int virtualWidth, int virtualHeight)
    {
        var unit = _projection.ScalePixels(1f);
        var panel = GetEditorTopStripPanelBounds(virtualWidth, virtualHeight);
        var padding = (int)MathF.Round(5f * unit);
        var headerHeight = (int)MathF.Round(38f * unit);
        var gap = (int)MathF.Round(4f * unit);
        var buttonHeight = (int)MathF.Round(32f * unit);
        var firstRowY = panel.Y + padding + headerHeight + gap;
        var rowStride = buttonHeight + gap;
        return new Rectangle(panel.X + padding, firstRowY + 7 * rowStride,
            panel.Width - padding * 2, buttonHeight);
    }

    private Rectangle GetEditorPanelsConnectorButtonBounds(int virtualWidth, int virtualHeight)
    {
        var unit = _projection.ScalePixels(1f);
        var panel = GetEditorTopStripPanelBounds(virtualWidth, virtualHeight);
        var padding = (int)MathF.Round(5f * unit);
        var headerHeight = (int)MathF.Round(38f * unit);
        var gap = (int)MathF.Round(4f * unit);
        var buttonHeight = (int)MathF.Round(32f * unit);
        return new Rectangle(panel.X + padding, panel.Y + padding + headerHeight + gap,
            panel.Width - padding * 2, buttonHeight);
    }

    private Rectangle GetEditorPanelsAttributeButtonBounds(int index, int virtualWidth, int virtualHeight)
    {
        if (index is < 0 or > 7)
            return Rectangle.Empty;
        var unit = _projection.ScalePixels(1f);
        var panel = GetEditorTopStripPanelBounds(virtualWidth, virtualHeight);
        var padding = (int)MathF.Round(5f * unit);
        var headerHeight = (int)MathF.Round(38f * unit);
        var gap = (int)MathF.Round(4f * unit);
        var buttonHeight = (int)MathF.Round(32f * unit);
        var contentX = panel.X + padding;
        var contentY = panel.Y + padding + headerHeight + gap;
        var contentWidth = panel.Width - padding * 2;
        var buttonWidth = (contentWidth - gap) / 2;
        var column = index % 2;
        var row = 1 + index / 2; // CONNECTOR, then four attribute rows above DURATION/SELECT/REPLACE.
        return new Rectangle(contentX + column * (buttonWidth + gap),
            contentY + row * (buttonHeight + gap), buttonWidth, buttonHeight);
    }

    private int GetEditorMenuChildCount() => _editorMenuSection switch
    {
        EditorMenuSection.Notes => EditorToolLabels.Length,
        EditorMenuSection.Tools => EditorEditToolLabels.Length,
        EditorMenuSection.Snap => EditorSnapDivisors.Length,
        EditorMenuSection.Bpm => 4,
        EditorMenuSection.Stutter => (_editorStutterStartPlacementPending || _editorStutterEndPlacementPending)
            ? 0
            : 12,
        EditorMenuSection.SuperFlick => _editorSuperFlickPlacementPending ? EditorSuperFlickToolLabels.Length : 0,
        EditorMenuSection.Select => 3,
        EditorMenuSection.Duration => 2,
        _ => 0
    };

    private int GetEditorMenuRowCount()
    {
        if (_editorMenuSection == EditorMenuSection.Notes)
            return 4; // SUPERFLICK sits beside VIBRATO; REPLACE SELECTION spans the final row.
        var count = GetEditorMenuChildCount();
        return (count + GetEditorMenuColumnCount() - 1) / GetEditorMenuColumnCount();
    }

    private EditorMenuSection GetEditorMenuRootSection() => _editorMenuSection switch
    {
        EditorMenuSection.Notes or EditorMenuSection.SuperFlick => EditorMenuSection.Notes,
        EditorMenuSection.Tools or EditorMenuSection.Duration => EditorMenuSection.Tools,
        EditorMenuSection.Bpm or EditorMenuSection.Stutter => EditorMenuSection.Bpm,
        EditorMenuSection.Select => EditorMenuSection.Select,
        EditorMenuSection.NoteGroups => EditorMenuSection.NoteGroups,
        _ => EditorMenuSection.None
    };

    private int GetEditorMenuColumnCount()
    {
        if (_editorPanelsViewEnabled && (_editorMenuSection is EditorMenuSection.Select or EditorMenuSection.Duration))
            return 1;
        return _editorMenuSection switch
        {
            EditorMenuSection.SuperFlick => 3,
            EditorMenuSection.Notes or EditorMenuSection.Tools or EditorMenuSection.Bpm or
                EditorMenuSection.Stutter or EditorMenuSection.Select or EditorMenuSection.Duration => 2,
            _ => 1
        };
    }

    private int GetEditorMenuChildHeight(float unit)
        => Math.Max(1, (int)MathF.Round(38f * unit));

    private int GetEditorMenuChildGap(float unit)
        => Math.Max(1, (int)MathF.Round(5f * unit));

    private int GetEditorMenuPanelPadding(float unit)
    {
        var padding = _editorPanelsViewEnabled && _editorMenuSection != EditorMenuSection.Select ? 5f :
            _editorMenuSection == EditorMenuSection.Stutter ? 12f :
            _editorMenuSection is EditorMenuSection.Bpm or EditorMenuSection.Duration or EditorMenuSection.Select ? 9f : 8f;
        return Math.Max(1, (int)MathF.Round(padding * unit));
    }

    private int GetEditorMenuHeaderContentGap(float unit)
    {
        var gap = _editorMenuSection == EditorMenuSection.Stutter ? 10f :
            _editorMenuSection is EditorMenuSection.Bpm or EditorMenuSection.Duration or EditorMenuSection.Select ? 8f : 6f;
        return Math.Max(1, (int)MathF.Round(gap * unit));
    }

    private int GetEditorStutterCellHorizontalPadding(float unit)
        => Math.Max(1, (int)MathF.Round(8f * unit));

    private int GetEditorStutterCellVerticalPadding(float unit)
        => Math.Max(1, (int)MathF.Round(6f * unit));

    // Menu items reserve a caption row above the clickable control. Keeping the
    // captions outside the buttons gives field names room to breathe.
    private int GetEditorMenuLabelHeight(float unit)
        => Math.Max(1, (int)MathF.Round(17f * unit));

    private int GetEditorMenuLabelGap(float unit)
        => Math.Max(1, (int)MathF.Round(4f * unit));

    private bool EditorMenuRowHasExternalLabels(int row) => _editorMenuSection switch
    {
        EditorMenuSection.Stutter => row is >= 0 and < 5,
        // Gameplay SELECT uses two columns, so Y RANGE and X RANGE share row 0.
        // Panels SELECT has one full-width column; each labeled filter therefore
        // needs its own caption row above its button.
        EditorMenuSection.Select when _editorPanelsViewEnabled => row is 0 or 1,
        EditorMenuSection.Duration when _editorPanelsViewEnabled => row is 0 or 1,
        EditorMenuSection.Bpm or EditorMenuSection.Duration or EditorMenuSection.Select => row == 0,
        _ => false
    };

    private int GetEditorMenuRowHeight(int row, float unit)
    {
        var buttonHeight = GetEditorMenuChildHeight(unit);
        var contentHeight = EditorMenuRowHasExternalLabels(row)
            ? GetEditorMenuLabelHeight(unit) + GetEditorMenuLabelGap(unit) + buttonHeight
            : buttonHeight;
        return _editorMenuSection == EditorMenuSection.Stutter
            // Keep the top inset for generator controls. The inter-row gap below
            // the button is supplied by GetEditorMenuChildGap, matching the
            // compact 8px stacking used by the bottom-left strip.
            ? contentHeight + GetEditorStutterCellVerticalPadding(unit)
            : contentHeight;
    }

    private int GetEditorMenuRowsHeight(int rows, float unit)
    {
        if (rows <= 0)
            return 0;
        var total = 0;
        var gap = GetEditorMenuChildGap(unit);
        for (var row = 0; row < rows; row++)
        {
            total += GetEditorMenuRowHeight(row, unit);
            if (row + 1 < rows)
                total += gap;
        }
        return total;
    }

    private int GetEditorMenuRowTop(int row, int contentY, float unit)
    {
        var y = contentY;
        var gap = GetEditorMenuChildGap(unit);
        for (var priorRow = 0; priorRow < row; priorRow++)
            y += GetEditorMenuRowHeight(priorRow, unit) + gap;
        return y;
    }

    private Rectangle GetEditorMenuChildLabelBounds(int childIndex, int virtualWidth, int virtualHeight)
    {
        if (childIndex < 0 || childIndex >= GetEditorMenuChildCount() ||
            string.IsNullOrEmpty(GetEditorMenuControlLabel(childIndex)))
            return Rectangle.Empty;
        var button = GetEditorMenuChildBounds(childIndex, virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var labelHeight = GetEditorMenuLabelHeight(unit);
        var labelGap = GetEditorMenuLabelGap(unit);
        return new Rectangle(button.X, button.Y - labelGap - labelHeight, button.Width, labelHeight);
    }

    private string GetEditorMenuControlLabel(int childIndex) => _editorMenuSection switch
    {
        EditorMenuSection.Bpm => childIndex switch
        {
            0 => "BPM",
            1 => "AUTO ADJUST",
            _ => string.Empty
        },
        EditorMenuSection.Duration => childIndex switch
        {
            0 => "DURATION (TICKS)",
            1 => "APPLY TARGETS",
            _ => string.Empty
        },
        EditorMenuSection.Select => childIndex switch
        {
            0 => "Y RANGE",
            1 => "X RANGE",
            _ => string.Empty
        },
        EditorMenuSection.Stutter => childIndex switch
        {
            0 => "START TICK",
            1 => "END TICK",
            2 => "PATTERN",
            3 => _editorStutterRamp ? "START BPM" : "SLOW BPM",
            4 => _editorStutterRamp ? "TARGET BPM" : _editorStutterAlternate ? "SLOW TIME (BEATS)" : "PULSE INTERVAL (BEATS)",
            5 => _editorStutterRamp ? "RESOLUTION" : "FAST BPM",
            6 => _editorStutterRamp ? "AUTO ADJUST" : "FAST TIME (BEATS)",
            7 => _editorStutterRamp ? "MOVE UP" : "TIMING MODE",
            8 => _editorStutterRamp ? "MOVE DOWN" : "AUTO ADJUST",
            _ => string.Empty
        },
        _ => string.Empty
    };

    private Rectangle GetEditorMenuHeaderBounds(int virtualWidth, int virtualHeight)
    {
        var unit = _projection.ScalePixels(1f);
        var hasPanelSubmenu = _editorPanelsViewEnabled &&
            (_editorMenuSection is EditorMenuSection.Select or EditorMenuSection.Duration);
        var panel = _editorPanelsViewEnabled && !hasPanelSubmenu
            ? GetEditorTopStripPanelBounds(virtualWidth, virtualHeight)
            : GetEditorMenuPanelBounds(virtualWidth, virtualHeight);
        var padding = GetEditorMenuPanelPadding(unit);
        var top = panel.Y + padding;
        var headerHeight = (int)MathF.Round((_editorPanelsViewEnabled && !hasPanelSubmenu ? 38f : 34f) * unit);
        return new Rectangle(panel.X + padding, top, panel.Width - padding * 2, headerHeight);
    }

    private Rectangle GetEditorPanelsHeaderBounds(int virtualWidth, int virtualHeight)
    {
        var unit = _projection.ScalePixels(1f);
        var panel = GetEditorTopStripPanelBounds(virtualWidth, virtualHeight);
        var padding = Math.Max(1, (int)MathF.Round(5f * unit));
        var headerHeight = (int)MathF.Round(38f * unit);
        return new Rectangle(panel.X + padding, panel.Y + padding, panel.Width - padding * 2, headerHeight);
    }

    private Rectangle GetCloseButtonBoundsForHeader(Rectangle header)
    {
        var unit = _projection.ScalePixels(1f);
        var inset = Math.Max(2, (int)MathF.Round(4f * unit));
        var size = Math.Min(Math.Max(1, header.Height - inset * 2), (int)MathF.Round(32f * unit));
        return new Rectangle(header.Right - inset - size,
            header.Y + (header.Height - size) / 2, size, size);
    }

    private Rectangle GetEditorPanelsCloseButtonBounds(int virtualWidth, int virtualHeight)
        => GetCloseButtonBoundsForHeader(GetEditorPanelsHeaderBounds(virtualWidth, virtualHeight));

    private Rectangle GetEditorMenuCloseButtonBounds(int virtualWidth, int virtualHeight)
        => GetCloseButtonBoundsForHeader(GetEditorMenuHeaderBounds(virtualWidth, virtualHeight));

    private Rectangle GetEditorMenuParentBounds(int parentIndex, int virtualWidth, int virtualHeight)
    {
        if (parentIndex is < 0 or > 4)
            return Rectangle.Empty;
        var unit = _projection.ScalePixels(1f);
        var panel = GetEditorTopStripPanelBounds(virtualWidth, virtualHeight);
        var padding = Math.Max(1, (int)MathF.Round(8f * unit));
        var gap = Math.Max(1, (int)MathF.Round(5f * unit));
        var buttonHeight = (int)MathF.Round(34f * unit);
        var contentX = panel.X + padding;
        var contentY = panel.Y + padding;
        var contentWidth = panel.Width - padding * 2;
        return new Rectangle(contentX, contentY + parentIndex * (buttonHeight + gap),
            Math.Max(1, contentWidth), buttonHeight);
    }

    private Rectangle GetEditorMenuChildBounds(int childIndex, int virtualWidth, int virtualHeight)
    {
        var count = GetEditorMenuChildCount();
        if (_editorMenuSection is EditorMenuSection.None or EditorMenuSection.Snap or EditorMenuSection.Metronome || childIndex < 0 || childIndex >= count)
            return Rectangle.Empty;
        var header = GetEditorMenuHeaderBounds(virtualWidth, virtualHeight);
        var panel = GetEditorMenuPanelBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var padding = GetEditorMenuPanelPadding(unit);
        var cellInset = _editorMenuSection == EditorMenuSection.Stutter
            ? GetEditorStutterCellHorizontalPadding(unit)
            : 0;
        var gap = GetEditorMenuChildGap(unit);
        var childHeight = GetEditorMenuChildHeight(unit);
        var columns = GetEditorMenuColumnCount();
        var row = childIndex / columns;
        var column = childIndex % columns;
        var contentX = panel.X + padding + cellInset;
        var contentY = header.Bottom + GetEditorMenuHeaderContentGap(unit);
        var contentWidth = panel.Width - (padding + cellInset) * 2;

        if (_editorMenuSection == EditorMenuSection.Stutter)
        {
            Rectangle Cell(int targetRow, int targetColumn, int targetColumns)
            {
                var cellWidth = Math.Max(1, (contentWidth - gap * (targetColumns - 1)) / targetColumns);
                var x = contentX + targetColumn * (cellWidth + gap);
                if (targetColumn == targetColumns - 1)
                    cellWidth = Math.Max(1, contentX + contentWidth - x);
                var rowTop = GetEditorMenuRowTop(targetRow, contentY, unit);
                var buttonY = rowTop + GetEditorStutterCellVerticalPadding(unit) + (targetRow < 5
                    ? GetEditorMenuLabelHeight(unit) + GetEditorMenuLabelGap(unit)
                    : 0);
                return new Rectangle(x, buttonY, cellWidth, childHeight);
            }

            Rectangle Span(int targetRow)
            {
                var rowTop = GetEditorMenuRowTop(targetRow, contentY, unit);
                var buttonY = rowTop + GetEditorStutterCellVerticalPadding(unit) + (targetRow < 5
                    ? GetEditorMenuLabelHeight(unit) + GetEditorMenuLabelGap(unit)
                    : 0);
                return new Rectangle(contentX, buttonY, contentWidth, childHeight);
            }

            // Every pattern shares the same three-button action row at the bottom.
            // Ramp uses the row above it for MOVE UP / MOVE DOWN.
            if (childIndex is 0 or 1)
                return Cell(0, childIndex, 2);
            if (childIndex == 2)
                return Span(1);
            return childIndex switch
            {
                3 => Cell(2, 0, 2), // Slow BPM / Ramp Start BPM
                4 => Cell(2, 1, 2), // Pulse Interval / Slow Time / Ramp Target BPM
                5 => Cell(3, 0, 2), // Fast BPM / Ramp Resolution
                6 => Cell(3, 1, 2), // Fast Time / Ramp Auto Adjust
                7 => Cell(4, 0, 2), // Timing Mode / Ramp Move Up
                8 => Cell(4, 1, 2), // Auto Adjust / Ramp Move Down
                9 => Cell(5, 0, 3), // Generate
                10 => Cell(5, 1, 3), // Delete
                11 => Cell(5, 2, 3), // Ungroup
                _ => Rectangle.Empty
            };
        }

        var childWidth = Math.Max(1, (contentWidth - gap * (columns - 1)) / columns);
        var y = GetEditorMenuRowTop(row, contentY, unit);
        if (EditorMenuRowHasExternalLabels(row))
            y += GetEditorMenuLabelHeight(unit) + GetEditorMenuLabelGap(unit);
        // If a grid has an odd final item (e.g. SUPERFLICK, DURATION), let the
        // final control span the whole row.
        if (columns > 1 && count % columns != 0 && childIndex == count - 1)
            return new Rectangle(contentX, y, contentWidth, childHeight);
        var x = contentX + column * (childWidth + gap);
        if (column == columns - 1)
            childWidth = Math.Max(1, contentX + contentWidth - x);
        return new Rectangle(x, y, childWidth, childHeight);
    }

    private void FocusOrPlaceEditorStutterCaret(EditorStutterInputField field, Rectangle bounds, Microsoft.Xna.Framework.Point pixel, float unit)
    {
        if (_editorStutterInputField == field)
        {
            _editorStutterInputCaretIndex = GetEditorNumericTextCaretIndex(bounds, _editorStutterInputText, pixel, unit, "stutter:" + field);
            _editorStutterSelectionAnchor = _editorStutterInputCaretIndex;
            _editorStutterInputReplaceOnNextKey = false;
            BeginEditorTextFieldMouseSelection("stutter:" + field, bounds, _editorStutterInputCaretIndex, ref _editorStutterSelectionAnchor);
            return;
        }
        FocusEditorStutterInput(field);
        _editorStutterInputCaretIndex = GetEditorNumericTextCaretIndex(bounds, _editorStutterInputText, pixel, unit, "stutter:" + field);
        _editorStutterSelectionAnchor = _editorStutterInputCaretIndex;
        _editorStutterInputReplaceOnNextKey = false;
        BeginEditorTextFieldMouseSelection("stutter:" + field, bounds, _editorStutterInputCaretIndex, ref _editorStutterSelectionAnchor);
    }

    private Rectangle GetEditorNumericInputArrowBounds(Rectangle fieldBounds, bool increase, float unit)
    {
        var spinnerWidth = Math.Min(Math.Max(26, (int)MathF.Round(34f * unit)), Math.Max(1, fieldBounds.Width / 3));
        var topHeight = Math.Max(1, fieldBounds.Height / 2);
        return new Rectangle(fieldBounds.Right - spinnerWidth,
            fieldBounds.Y + (increase ? 0 : topHeight), spinnerWidth,
            increase ? topHeight : Math.Max(1, fieldBounds.Height - topHeight));
    }

    private bool TryGetEditorStutterInputFieldForChild(int childIndex, out EditorStutterInputField field)
    {
        field = childIndex switch
        {
            3 => _editorStutterRamp ? EditorStutterInputField.RampStartBpm : EditorStutterInputField.SlowBpm,
            4 => _editorStutterRamp ? EditorStutterInputField.RampTargetBpm :
                _editorStutterAlternate ? EditorStutterInputField.SlowTimeBeats : EditorStutterInputField.PulseIntervalBeats,
            5 when !_editorStutterRamp => EditorStutterInputField.FastBpm,
            6 when !_editorStutterRamp => EditorStutterInputField.FastTimeBeats,
            _ => EditorStutterInputField.None
        };
        return field != EditorStutterInputField.None;
    }

    private bool TryHandleEditorNumericSpinnerClick(int childIndex, Rectangle bounds, Microsoft.Xna.Framework.Point pixel, float unit)
    {
        var stutterField = EditorStutterInputField.None;
        var isNumericField = _editorMenuSection switch
        {
            EditorMenuSection.Stutter => TryGetEditorStutterInputFieldForChild(childIndex, out stutterField),
            EditorMenuSection.Bpm => childIndex == 0,
            EditorMenuSection.Duration => childIndex == 0,
            _ => false
        };
        if (!isNumericField)
            return false;

        if (GetEditorNumericInputArrowBounds(bounds, true, unit).Contains(pixel))
        {
            if (_editorMenuSection == EditorMenuSection.Stutter) AdjustEditorStutterInput(stutterField, 1);
            else if (_editorMenuSection == EditorMenuSection.Bpm) AdjustEditorBpmInput(1);
            else AdjustEditorDurationInput(1);
            return true;
        }
        if (GetEditorNumericInputArrowBounds(bounds, false, unit).Contains(pixel))
        {
            if (_editorMenuSection == EditorMenuSection.Stutter) AdjustEditorStutterInput(stutterField, -1);
            else if (_editorMenuSection == EditorMenuSection.Bpm) AdjustEditorBpmInput(-1);
            else AdjustEditorDurationInput(-1);
            return true;
        }
        return false;
    }

    private int GetEditorNumericTextCaretIndex(Rectangle bounds, string value, Microsoft.Xna.Framework.Point pixel,
        float unit, string? fieldId = null)
    {
        var interfaceUi = _chartEditorInterfaceLayout;
        var font = GetChartEditorFont(interfaceUi.LabelFont);
        fieldId ??= _editorTextActiveFieldId ?? "editor-numeric";
        var toolbarInput = fieldId is "bpm" or "duration" ||
                           fieldId.StartsWith("stutter:", StringComparison.Ordinal);
        var spinnerWidth = toolbarInput ? 0 :
            Math.Min(Math.Max(26, (int)MathF.Round(34f * unit)), Math.Max(1, bounds.Width / 3));
        var textLeft = bounds.X + (int)MathF.Round(10f * unit);
        var availableWidth = Math.Max(1f, bounds.Width - spinnerWidth - 16f * unit);
        var measured = UnicodeTextRenderer.MeasureString(font, FontSafeText(font, value));
        var scale = Math.Min(1f, Math.Max(0.55f,
            (bounds.Height - 8f * unit) / Math.Max(1f, measured.Y)));
        font = GetChartEditorFont(interfaceUi.LabelFont, 24f * scale);
        scale = Math.Min(1f, (bounds.Height - 8f * unit) /
            Math.Max(1f, UnicodeTextRenderer.MeasureString(font, "Ag").Y));
        return GetEditorTextCaretIndexFromMouse(fieldId, value, pixel.X, textLeft, availableWidth, scale, font);
    }

    private Rectangle[] GetEditorStutterPatternButtonBounds(Rectangle rowBounds)
    {
        var unit = _projection.ScalePixels(1f);
        var gap = Math.Max(1, (int)MathF.Round(6f * unit));
        var segmentWidth = Math.Max(1, (rowBounds.Width - gap * 2) / 3);
        var result = new Rectangle[3];
        for (var i = 0; i < result.Length; i++)
        {
            var x = rowBounds.X + i * (segmentWidth + gap);
            var width = i == result.Length - 1
                ? Math.Max(1, rowBounds.Right - x)
                : segmentWidth;
            result[i] = new Rectangle(x, rowBounds.Y, width, rowBounds.Height);
        }
        return result;
    }

    private Rectangle GetEditorBottomActionPanelBounds(int virtualWidth, int virtualHeight)
    {
        var unit = _projection.ScalePixels(1f);
        var margin = (int)MathF.Round((float)_chartEditorInterfaceLayout.EdgeMargin * unit);
        var padding = (int)MathF.Round((float)_chartEditorInterfaceLayout.PanelPadding * unit);
        var width = Math.Min((int)MathF.Round((float)_chartEditorInterfaceLayout.ActionColumnWidth * unit), Math.Max(76, virtualWidth - margin * 2));
        var buttonHeight = (int)MathF.Round((float)_chartEditorInterfaceLayout.ActionButtonHeight * unit);
        var gap = (int)MathF.Round((float)_chartEditorInterfaceLayout.ActionButtonGap * unit);
        // The action rows are fixed even when COPY/CUT/PASTE/DESELECT are hidden.
        var contentHeight = EditorRightActionIds.Length * buttonHeight +
                            (EditorRightActionIds.Length - 1) * gap;
        return new Rectangle(virtualWidth - margin - width - padding,
            margin,
            width + padding * 2, contentHeight + padding * 2);
    }

    // Top-right action rows have fixed indices. Hidden contextual actions leave
    // an empty slot instead of shifting DELETE/UNDO/REDO up and down.
    private Rectangle GetEditorActionButtonBounds(int index, int virtualWidth, int virtualHeight,
        IReadOnlyList<int>? visibleActionIds = null)
    {
        var count = (visibleActionIds ?? GetVisibleEditorActionIds()).Count;
        if (index < 0 || index >= count)
            return Rectangle.Empty;
        var unit = _projection.ScalePixels(1f);
        var panel = GetEditorBottomActionPanelBounds(virtualWidth, virtualHeight);
        var padding = (int)MathF.Round((float)_chartEditorInterfaceLayout.PanelPadding * unit);
        var buttonHeight = (int)MathF.Round((float)_chartEditorInterfaceLayout.ActionButtonHeight * unit);
        var gap = (int)MathF.Round((float)_chartEditorInterfaceLayout.ActionButtonGap * unit);
        return new Rectangle(panel.X + padding,
            panel.Y + padding + index * (buttonHeight + gap),
            panel.Width - padding * 2, buttonHeight);
    }

    private int GetEditorBottomLeftContentWidth(int virtualWidth, int virtualHeight)
    {
        var unit = _projection.ScalePixels(1f);
        var margin = (int)MathF.Round((float)_chartEditorInterfaceLayout.EdgeMargin * unit);
        var padding = (int)MathF.Round((float)_chartEditorInterfaceLayout.PanelPadding * unit);
        // This toolbar spans the entire bottom edge, independent of the right rail.
        return Math.Max(1, virtualWidth - margin * 2 - padding * 2);
    }

    private int GetEditorBottomLeftColumnCount(int virtualWidth, int virtualHeight)
        => _editorPreviewMode ? 3 : Math.Max(1, EditorBottomLeftActionIds.Length);

    private (int Row, int Column) GetEditorBottomLeftActionSlot(int actionIndex, int columns)
    {
        if (actionIndex < 0 || actionIndex >= EditorBottomLeftActionIds.Length)
            return (-1, -1);
        var id = EditorBottomLeftActionIds[actionIndex];
        if (_editorPreviewMode)
        {
            if (id == EditorActionPlay) return (0, 0);
            if (id == EditorActionEditorPreview) return (0, 1);
            if (id == EditorActionGameplayMode) return (0, 2);
            return (-1, -1);
        }
        return (0, actionIndex);
    }

    private Rectangle GetEditorBottomLeftStripBounds(int virtualWidth, int virtualHeight)
    {
        var unit = _projection.ScalePixels(1f);
        var margin = (int)MathF.Round((float)_chartEditorInterfaceLayout.EdgeMargin * unit);
        var padding = (int)MathF.Round((float)_chartEditorInterfaceLayout.PanelPadding * unit);
        var buttonHeight = (int)MathF.Round((float)_chartEditorInterfaceLayout.LeftToolbarButtonHeight * unit);
        var height = buttonHeight + padding * 2;
        var width = Math.Max(1, virtualWidth - margin * 2);
        if (_editorPreviewMode)
        {
            var contentWidth = GetEditorBottomLeftContentWidth(virtualWidth, virtualHeight);
            var gap = (int)MathF.Round((float)_chartEditorInterfaceLayout.LeftToolbarButtonGap * unit);
            var normalButtonWidth = Math.Max(1,
                (contentWidth - gap * (EditorBottomLeftActionIds.Length - 1)) / EditorBottomLeftActionIds.Length);
            width = padding * 2 + normalButtonWidth * 3 + gap * 2;
        }
        return new Rectangle(margin, virtualHeight - margin - height, width, height);
    }

    private Rectangle GetEditorBottomLeftButtonBounds(int index, int virtualWidth, int virtualHeight)
    {
        if (index < 0 || index >= EditorBottomLeftActionIds.Length)
            return Rectangle.Empty;
        var unit = _projection.ScalePixels(1f);
        var panel = GetEditorBottomLeftStripBounds(virtualWidth, virtualHeight);
        var padding = (int)MathF.Round((float)_chartEditorInterfaceLayout.PanelPadding * unit);
        var gap = (int)MathF.Round((float)_chartEditorInterfaceLayout.LeftToolbarButtonGap * unit);
        var columns = GetEditorBottomLeftColumnCount(virtualWidth, virtualHeight);
        var id = EditorBottomLeftActionIds[index];
        if (_editorPreviewMode && id is not (EditorActionPlay or EditorActionEditorPreview or EditorActionGameplayMode))
            return Rectangle.Empty;
        var (row, column) = GetEditorBottomLeftActionSlot(index, columns);
        var fullContentWidth = GetEditorBottomLeftContentWidth(virtualWidth, virtualHeight);
        // Preview keeps the same per-button width as the full editor toolbar;
        // only the background/strip width contracts to fit its three controls.
        var normalActionCount = Math.Max(1, EditorBottomLeftActionIds.Length);
        var buttonWidth = Math.Max(1, (fullContentWidth - gap * (normalActionCount - 1)) / normalActionCount);
        var x = panel.X + padding + column * (buttonWidth + gap);
        var buttonHeight = (int)MathF.Round((float)_chartEditorInterfaceLayout.LeftToolbarButtonHeight * unit);
        var y = _editorPreviewMode
            ? panel.Y + padding
            : panel.Y + padding + row * (buttonHeight + gap);
        return new Rectangle(x, y, buttonWidth, buttonHeight);
    }

    private int GetEditorSnapActionIndex() => Array.IndexOf(EditorBottomLeftActionIds, EditorActionSnap);
    private int GetEditorMetronomeActionIndex() => Array.IndexOf(EditorBottomLeftActionIds, EditorActionMetronome);

    private Rectangle GetEditorMetronomeMenuChildBounds(int childIndex, int virtualWidth, int virtualHeight)
    {
        if (childIndex < 0 || childIndex >= EditorMetronomeDivisors.Length)
            return Rectangle.Empty;
        var actionIndex = GetEditorMetronomeActionIndex();
        if (actionIndex < 0)
            return Rectangle.Empty;
        var anchor = GetEditorBottomLeftButtonBounds(actionIndex, virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var childHeight = (int)MathF.Round(38f * unit);
        var gap = Math.Max(1, (int)MathF.Round(4f * unit));
        var y = anchor.Y - (EditorMetronomeDivisors.Length - childIndex) * (childHeight + gap);
        return new Rectangle(anchor.X, y, anchor.Width, childHeight);
    }

    private Rectangle GetEditorMetronomePopupBounds(int virtualWidth, int virtualHeight)
    {
        var firstChild = GetEditorMetronomeMenuChildBounds(0, virtualWidth, virtualHeight);
        var actionIndex = GetEditorMetronomeActionIndex();
        if (actionIndex < 0)
            return Rectangle.Empty;
        var anchor = GetEditorBottomLeftButtonBounds(actionIndex, virtualWidth, virtualHeight);
        var gap = Math.Max(1, (int)MathF.Round(4f * _projection.ScalePixels(1f)));
        return new Rectangle(firstChild.X, firstChild.Y, firstChild.Width,
            Math.Max(1, anchor.Y - gap - firstChild.Y));
    }

    private Rectangle GetEditorSnapMenuChildBounds(int childIndex, int virtualWidth, int virtualHeight)
    {
        if (childIndex < 0 || childIndex >= EditorSnapDivisors.Length)
            return Rectangle.Empty;
        var snapActionIndex = GetEditorSnapActionIndex();
        if (snapActionIndex < 0)
            return Rectangle.Empty;
        var anchor = GetEditorBottomLeftButtonBounds(snapActionIndex, virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var childHeight = (int)MathF.Round(38f * unit);
        var gap = Math.Max(1, (int)MathF.Round(4f * unit));
        var y = anchor.Y - (EditorSnapDivisors.Length - childIndex) * (childHeight + gap);
        return new Rectangle(anchor.X, y, anchor.Width, childHeight);
    }

    private Rectangle GetEditorSnapPopupBounds(int virtualWidth, int virtualHeight)
    {
        var firstChild = GetEditorSnapMenuChildBounds(0, virtualWidth, virtualHeight);
        var snapActionIndex = GetEditorSnapActionIndex();
        if (snapActionIndex < 0)
            return Rectangle.Empty;
        var anchor = GetEditorBottomLeftButtonBounds(snapActionIndex, virtualWidth, virtualHeight);
        var gap = Math.Max(1, (int)MathF.Round(4f * _projection.ScalePixels(1f)));
        return new Rectangle(firstChild.X, firstChild.Y, firstChild.Width,
            Math.Max(1, anchor.Y - gap - firstChild.Y));
    }

    private Rectangle GetEditorMetaPanelBounds(int virtualWidth, int virtualHeight)
    {
        var unit = _projection.ScalePixels(1f);
        var margin = (int)MathF.Round((float)_chartEditorInterfaceLayout.MetadataPanelMargin * unit);
        var desiredWidth = (int)MathF.Round((float)_chartEditorInterfaceLayout.MetadataPanelWidth * unit);
        // Match chartEditorInterface.lua's 660-unit panel cap. The same bounds are
        // used to calculate wrapped row heights and scroll limits for the Lua menu.
        var maxPanelWidth = (int)MathF.Round(660f * unit);
        var width = Math.Min(virtualWidth - margin * 2,
            Math.Max((int)MathF.Round(420f * unit),
                Math.Min(desiredWidth, Math.Min(maxPanelWidth, (int)MathF.Round(virtualWidth * 0.60f)))));
        width = Math.Max(1, width);
        return new Rectangle(virtualWidth - margin - width, margin, width,
            Math.Max(1, virtualHeight - margin * 2));
    }

    private Rectangle GetEditorMetaCloseButtonBounds(int virtualWidth, int virtualHeight)
    {
        var panel = GetEditorMetaPanelBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var padding = (int)MathF.Round(10f * unit);
        var button = (int)MathF.Round(42f * unit);
        return new Rectangle(panel.Right - padding - button, panel.Y + padding, button, button);
    }

    private Rectangle GetEditorMetaExitButtonBounds(int virtualWidth, int virtualHeight)
    {
        var panel = GetEditorMetaPanelBounds(virtualWidth, virtualHeight);
        var close = GetEditorMetaCloseButtonBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var width = Math.Max((int)MathF.Round(76f * unit), (int)MathF.Round(68f * unit));
        var height = Math.Max(1, (int)MathF.Round(30f * unit));
        var gap = Math.Max(3, (int)MathF.Round(6f * unit));
        return new Rectangle(close.X - gap - width,
            close.Y + (close.Height - height) / 2, width, height);
    }

    private Rectangle GetEditorMetaDifficultyButtonBounds(int index, int virtualWidth, int virtualHeight)
    {
        if (index < 0 || index >= EditorDifficultyCodes.Length)
            return Rectangle.Empty;
        var panel = GetEditorMetaPanelBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var padding = (int)MathF.Round(12f * unit);
        var gap = (int)MathF.Round(4f * unit);
        var innerWidth = Math.Max(1, panel.Width - padding * 2 - gap * (EditorDifficultyCodes.Length - 1));
        var buttonWidth = Math.Max(1, innerWidth / EditorDifficultyCodes.Length);
        var x = panel.X + padding + index * (buttonWidth + gap);
        if (index == EditorDifficultyCodes.Length - 1)
            buttonWidth = Math.Max(1, panel.Right - padding - x);
        var y = panel.Y + (int)MathF.Round(80f * unit);
        var height = Math.Max(1, (int)MathF.Round(30f * unit));
        return new Rectangle(x, y, buttonWidth, height);
    }

    private Rectangle GetEditorMetaCategoryButtonBounds(int index, int virtualWidth, int virtualHeight)
    {
        if (index < 0 || index >= EditorMetaCategories.Length) return Rectangle.Empty;
        var panel = GetEditorMetaPanelBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var padding = (int)MathF.Round(12f * unit);
        var gap = Math.Max(2, (int)MathF.Round(4f * unit));
        var innerWidth = Math.Max(1, panel.Width - padding * 2 - gap * (EditorMetaCategories.Length - 1));
        var buttonWidth = Math.Max(1, innerWidth / EditorMetaCategories.Length);
        var x = panel.X + padding + index * (buttonWidth + gap);
        if (index == EditorMetaCategories.Length - 1)
            buttonWidth = Math.Max(1, panel.Right - padding - x);
        var y = panel.Y + (int)MathF.Round(118f * unit);
        var height = Math.Max(1, (int)MathF.Round(28f * unit));
        return new Rectangle(x, y, buttonWidth, height);
    }

    private Rectangle GetEditorMetaContentBounds(int virtualWidth, int virtualHeight)
    {
        var panel = GetEditorMetaPanelBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var padding = (int)MathF.Round(12f * unit);
        var contentTop = panel.Y + (int)MathF.Round(156f * unit);
        var contentBottom = string.Equals(_editorMetaCategory, "TEST", StringComparison.OrdinalIgnoreCase)
            ? panel.Bottom - padding
            : GetEditorMetaDifficultyToggleBounds(virtualWidth, virtualHeight).Y - padding;
        return new Rectangle(
            panel.X + padding,
            contentTop,
            Math.Max(1, panel.Width - padding * 2),
            Math.Max(0, contentBottom - contentTop));
    }

    private Rectangle GetEditorMetaLanTestAdbToggleBounds(int virtualWidth, int virtualHeight)
    {
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var y = content.Y + (int)MathF.Round(2f * unit);
        return new Rectangle(content.X, y, content.Width, Math.Max(1, (int)MathF.Round(34f * unit)));
    }

    private Rectangle GetEditorMetaLanTestAdbModeBounds(bool wifi, int virtualWidth, int virtualHeight)
    {
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var gap = Math.Max(1, (int)MathF.Round(8f * unit));
        var half = Math.Max(1, (content.Width - gap) / 2);
        var y = content.Y + (int)MathF.Round(29f * unit);
        return wifi
            ? new Rectangle(content.X + half + gap, y, Math.Max(1, content.Width - half - gap), Math.Max(1, (int)MathF.Round(34f * unit)))
            : new Rectangle(content.X, y, half, Math.Max(1, (int)MathF.Round(34f * unit)));
    }

    private Rectangle GetEditorMetaLanTestAdbInputBounds(string field, int virtualWidth, int virtualHeight)
    {
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var y = content.Y + (int)MathF.Round((field == "adb_pair_code" ? 240f : 182f) * unit);
        var width = field == "adb_pair_code"
            ? Math.Max(1, content.Width - (int)MathF.Round(146f * unit))
            : content.Width;
        return new Rectangle(content.X, y, width, Math.Max(1, (int)MathF.Round(36f * unit)));
    }

    private Rectangle GetEditorMetaLanTestAdbPairActionBounds(int virtualWidth, int virtualHeight)
    {
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var width = Math.Min((int)MathF.Round(138f * unit), content.Width);
        var y = content.Y + (int)MathF.Round(240f * unit);
        return new Rectangle(content.Right - width, y, width, Math.Max(1, (int)MathF.Round(36f * unit)));
    }

    private Rectangle GetEditorMetaLanTestAdbBackBounds(int virtualWidth, int virtualHeight)
    {
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var height = Math.Max(1, (int)MathF.Round(38f * unit));
        return new Rectangle(content.X, content.Bottom - height - (int)MathF.Round(8f * unit), content.Width, height);
    }

    private Rectangle GetEditorMetaLanTestButtonBounds(int action, int virtualWidth, int virtualHeight)
    {
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var height = Math.Max(1, (int)MathF.Round(38f * unit));
        var bottomGap = (int)MathF.Round(8f * unit);
        var y = content.Bottom - height - bottomGap;
        if (!_editorLanTestRunning)
            return action == 0 ? new Rectangle(content.X, y, content.Width, height) : Rectangle.Empty;

        var gap = Math.Max(1, (int)MathF.Round(8f * unit));
        var halfWidth = Math.Max(1, (content.Width - gap) / 2);
        return action switch
        {
            1 => new Rectangle(content.X, y, halfWidth, height),
            2 => new Rectangle(content.X + halfWidth + gap, y, Math.Max(1, content.Width - halfWidth - gap), height),
            _ => Rectangle.Empty
        };
    }

    private readonly record struct EditorMetaRowLayout(int LabelHeight, int LabelGap, int FieldHeight, int RowGap)
    {
        public int Pitch => LabelHeight + LabelGap + FieldHeight + RowGap;
    }

    private int GetEditorMetaLabelHeight(float unit)
        => Math.Max(1, (int)MathF.Round(20f * unit));

    private int GetEditorMetaLabelGap(float unit)
        => Math.Max(1, (int)MathF.Round(5f * unit));

    private int GetEditorMetaFieldHeight(float unit)
        => Math.Max(1, (int)MathF.Round(46f * unit));

    private int GetEditorMetaRowGap(float unit)
        => Math.Max(1, (int)MathF.Round(8f * unit));

    private List<EditorMetaRowLayout> GetEditorMetaRowLayouts(
        IReadOnlyList<(string Label, string Value)> rows, int virtualWidth, int virtualHeight)
    {
        var unit = _projection.ScalePixels(1f);
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var scrollbarSpace = (int)MathF.Round(8f * unit);
        // Match the Lua field width: content padding, the scrollbar rail, the
        // skin's extra trailing gap, and both field text insets are reserved.
        var fieldWidth = Math.Max(1f, content.Width - (scrollbarSpace + 42f * unit));
        var font = GetChartEditorFont(_chartEditorInterfaceLayout.LabelFont, 24f);
        // Keep this in lockstep with chartEditorInterface.lua's small text size.
        var lineHeight = Math.Max(16f * unit, 24f * 1.2f * unit);
        var layouts = new List<EditorMetaRowLayout>(rows.Count);
        foreach (var row in rows)
        {
            var displayValue = row.Value;
            var fieldKey = GetEditorMetaFieldKey(row.Label);
            if (fieldKey is not null && string.Equals(fieldKey, _editorMetaEditingField, StringComparison.OrdinalIgnoreCase))
                displayValue = _editorMetaInputText;

            // These are controls rather than free-form text fields. Keep them at the
            // standard height; editable/readonly text values expand to fit wrapped lines.
            var wrapValue = row.Label is not ("ELEMENT" or "CUSTOM JUDGEMENT WINDOWS" or
                "DELETE DIFFICULTY" or "MUSIC" or "ALBUM" or "ALBUM BIG");
            var fieldHeight = GetEditorMetaFieldHeight(unit);
            if (wrapValue)
            {
                var safeValue = FontSafeText(font, displayValue);
                var isEditing = fieldKey is not null &&
                    string.Equals(fieldKey, _editorMetaEditingField, StringComparison.OrdinalIgnoreCase);
                var lineCount = isEditing
                    ? WrapEditorTextWithRanges(safeValue, fieldWidth,
                        candidate => UnicodeTextRenderer.MeasureString(font, candidate).X).Count
                    : WrapEditorHudText(font, safeValue, fieldWidth).Count;
                fieldHeight = Math.Max(fieldHeight,
                    (int)MathF.Ceiling(lineCount * lineHeight + 12f * unit));
            }

            layouts.Add(new EditorMetaRowLayout(
                GetEditorMetaLabelHeight(unit), GetEditorMetaLabelGap(unit),
                fieldHeight, GetEditorMetaRowGap(unit)));
        }
        return layouts;
    }

    private static int GetEditorMetaPrefixHeight(IReadOnlyList<EditorMetaRowLayout> layouts, int endExclusive)
    {
        var height = 0;
        for (var i = 0; i < Math.Clamp(endExclusive, 0, layouts.Count); i++)
            height += layouts[i].Pitch;
        return height;
    }

    private int GetEditorMetaMaxScrollOffset(IReadOnlyList<(string Label, string Value)> rows,
        int virtualWidth, int virtualHeight)
    {
        if (rows.Count == 0)
            return 0;
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var layouts = GetEditorMetaRowLayouts(rows, virtualWidth, virtualHeight);
        var suffixHeight = 0;
        for (var i = layouts.Count - 1; i >= 0; i--)
        {
            var pitch = layouts[i].Pitch;
            if (suffixHeight + pitch > content.Height)
                return suffixHeight == 0 ? i : i + 1;
            suffixHeight += pitch;
        }
        return 0;
    }

    private int GetEditorMetaVisibleRowCount(IReadOnlyList<(string Label, string Value)> rows,
        int virtualWidth, int virtualHeight)
    {
        if (rows.Count == 0)
            return 0;
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var layouts = GetEditorMetaRowLayouts(rows, virtualWidth, virtualHeight);
        var start = Math.Clamp(_editorMetaScrollOffset, 0, GetEditorMetaMaxScrollOffset(rows, virtualWidth, virtualHeight));
        var used = 0;
        var count = 0;
        for (var i = start; i < layouts.Count; i++)
        {
            if (count > 0 && used + layouts[i].Pitch > content.Height)
                break;
            used += layouts[i].Pitch;
            count++;
            if (used >= content.Height)
                break;
        }
        return Math.Max(1, count);
    }

    private void ClampEditorMetaScrollOffset(IReadOnlyList<(string Label, string Value)> rows,
        int virtualWidth, int virtualHeight)
        => _editorMetaScrollOffset = Math.Clamp(_editorMetaScrollOffset, 0,
            GetEditorMetaMaxScrollOffset(rows, virtualWidth, virtualHeight));

    private Rectangle GetEditorMetaFieldLabelBounds(int index, int virtualWidth, int virtualHeight)
    {
        var rows = GetEditorMetaRows(_tempo?.SecondsToTick(_playSeconds) ?? 0.0);
        var layouts = GetEditorMetaRowLayouts(rows, virtualWidth, virtualHeight);
        return GetEditorMetaFieldLabelBounds(index, virtualWidth, virtualHeight, rows, layouts);
    }

    private Rectangle GetEditorMetaFieldLabelBounds(int index, int virtualWidth, int virtualHeight,
        IReadOnlyList<(string Label, string Value)> rows, IReadOnlyList<EditorMetaRowLayout> layouts)
    {
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var start = Math.Clamp(_editorMetaScrollOffset, 0, Math.Max(0, rows.Count - 1));
        var rowTop = content.Y + GetEditorMetaPrefixHeight(layouts, index) - GetEditorMetaPrefixHeight(layouts, start);
        var scrollbarSpace = (int)MathF.Round(8f * unit);
        return new Rectangle(content.X, rowTop,
            Math.Max(1, content.Width - scrollbarSpace), layouts[index].LabelHeight);
    }

    private Rectangle GetEditorMetaFieldBounds(int index, int virtualWidth, int virtualHeight)
    {
        var rows = GetEditorMetaRows(_tempo?.SecondsToTick(_playSeconds) ?? 0.0);
        var layouts = GetEditorMetaRowLayouts(rows, virtualWidth, virtualHeight);
        return GetEditorMetaFieldBounds(index, virtualWidth, virtualHeight, rows, layouts);
    }

    private Rectangle GetEditorMetaFieldBounds(int index, int virtualWidth, int virtualHeight,
        IReadOnlyList<(string Label, string Value)> rows, IReadOnlyList<EditorMetaRowLayout> layouts)
    {
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var start = Math.Clamp(_editorMetaScrollOffset, 0, Math.Max(0, rows.Count - 1));
        var rowTop = content.Y + GetEditorMetaPrefixHeight(layouts, index) - GetEditorMetaPrefixHeight(layouts, start);
        var scrollbarSpace = (int)MathF.Round(8f * unit);
        return new Rectangle(content.X, rowTop + layouts[index].LabelHeight + layouts[index].LabelGap,
            Math.Max(1, content.Width - scrollbarSpace), layouts[index].FieldHeight);
    }

    private enum EditorMetaExportAction
    {
        Native,
        TscOnly,
        JsonOnly,
        TscZip,
        Ares,
#if !TSC_PUBLISH
        Fpk,
#endif
        Tsts
    }

    private int GetEditorMetaExportOptionCount()
#if TSC_PUBLISH
        => 5;
#else
        => 6;
#endif

    private (string Label, string Description, Color Accent, EditorMetaExportAction Action) GetEditorMetaExportOption(int index)
    {
        const string tstsDescription = "TSTS bundle containing ARES files. Compatible with ReSONANCE.";
        if (index == 0)
        {
            return _editorBundleTemplate switch
            {
                ChartBundleTemplate.Bold => ("NATIVE: BOLD / ARES", "Export the current chart in BOLD's native ARES format.", Hold, EditorMetaExportAction.Native),
                ChartBundleTemplate.Tsts => ("NATIVE: TSTS BUNDLE", tstsDescription, new Color(196, 181, 253), EditorMetaExportAction.Native),
                _ => ("TSC ZIP", "Bundle all enabled difficulty charts with song metadata and shared assets.", Active, EditorMetaExportAction.TscZip)
            };
        }

        if (index == 1)
            return ("EXPORT TSC ONLY", "Export only the current difficulty as a standalone .tsc chart.", Active, EditorMetaExportAction.TscOnly);
        if (index == 2)
            return ("EXPORT JSON META", "Export song metadata only; chart files and media are not included.", new Color(134, 239, 172), EditorMetaExportAction.JsonOnly);
        if (index == 3)
        {
            return _editorBundleTemplate == ChartBundleTemplate.Tsc
                ? ("EXPORT ARES", "Export the current difficulty as a raw ARES chart for tools that read .ares files.", Active, EditorMetaExportAction.Ares)
                : ("EXPORT TSC ZIP", "Bundle all enabled TSC difficulty charts with song metadata and shared assets.", Active, EditorMetaExportAction.TscZip);
        }
        if (index == 4)
        {
#if TSC_PUBLISH
            return _editorBundleTemplate == ChartBundleTemplate.Tsts
                ? ("EXPORT ARES", "Export the current difficulty as a raw ARES chart for tools that read .ares files.", Active, EditorMetaExportAction.Ares)
                : ("EXPORT TSTS", tstsDescription, new Color(196, 181, 253), EditorMetaExportAction.Tsts);
#else
            return _editorBundleTemplate == ChartBundleTemplate.Tsts
                ? ("EXPORT ARES", "Export the current difficulty as a raw ARES chart for tools that read .ares files.", Active, EditorMetaExportAction.Ares)
                : ("EXPORT FPK", "Pack all enabled difficulties and shared assets into a BOLD/FPK bundle.", Hold, EditorMetaExportAction.Fpk);
#endif
        }
#if !TSC_PUBLISH
        if (index == 5)
        {
            return _editorBundleTemplate == ChartBundleTemplate.Tsts
                ? ("EXPORT FPK", "Pack all enabled difficulties and shared assets into a BOLD/FPK bundle.", Hold, EditorMetaExportAction.Fpk)
                : ("EXPORT TSTS", tstsDescription, new Color(196, 181, 253), EditorMetaExportAction.Tsts);
        }
#endif
        return (string.Empty, string.Empty, Active, EditorMetaExportAction.Native);
    }

    private Rectangle GetEditorMetaDifficultyToggleBounds(int virtualWidth, int virtualHeight)
    {
        var export = GetEditorMetaExportToggleBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var gap = Math.Max(1, (int)MathF.Round(6f * unit));
        var height = Math.Max(1, (int)MathF.Round(34f * unit));
        return new Rectangle(export.X, export.Y - gap - height, export.Width, height);
    }

    private Rectangle GetEditorMetaExportToggleBounds(int virtualWidth, int virtualHeight)
    {
        var panel = GetEditorMetaPanelBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var padding = (int)MathF.Round(12f * unit);
        var height = Math.Max(1, (int)MathF.Round(38f * unit));
        return new Rectangle(panel.X + padding, panel.Bottom - padding - height,
            Math.Max(1, panel.Width - padding * 2), height);
    }

    private List<string> WrapEditorTextToWidth(SpriteFont font, string text, float maxWidth)
    {
        var lines = new List<string>();
        foreach (var sourceLine in (text ?? string.Empty).Split('\n'))
        {
            var line = string.Empty;
            foreach (var word in sourceLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                var measured = UnicodeTextRenderer.MeasureString(font, FontSafeText(font, candidate)).X;
                if (line.Length > 0 && measured > maxWidth)
                {
                    lines.Add(line);
                    line = word;
                }
                else
                {
                    line = candidate;
                }
            }
            lines.Add(line);
        }
        if (lines.Count == 0)
            lines.Add(string.Empty);
        return lines;
    }

    private int[] GetEditorMetaExportOptionRowHeights(int virtualWidth)
    {
        var count = GetEditorMetaExportOptionCount();
        var unit = _projection.ScalePixels(1f);
        var margin = Math.Max(1, (int)MathF.Round(16f * unit));
        var modalWidth = Math.Min((int)MathF.Round(760f * unit), Math.Max(1, virtualWidth - margin * 2));
        var sidePadding = Math.Max(1, (int)MathF.Round(18f * unit));
        var optionWidth = Math.Max(1, modalWidth - sidePadding * 2);
        var textWidth = Math.Max(1f, optionWidth - 32f * unit);
        var interfaceUi = _chartEditorInterfaceLayout;
        var titleFont = GetChartEditorFont(interfaceUi.ButtonFont, 20f * unit);
        var descriptionFont = GetChartEditorFont(interfaceUi.LabelFont, 16f * unit);
        var titleLineHeight = Math.Max(1f, UnicodeTextRenderer.MeasureString(titleFont, "Ag").Y);
        var descriptionLineHeight = Math.Max(1f, UnicodeTextRenderer.MeasureString(descriptionFont, "Ag").Y);
        var titleTop = (int)MathF.Round(8f * unit);
        var descriptionGap = (int)MathF.Round(4f * unit);
        var bottomPadding = (int)MathF.Round(9f * unit);
        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            var (_, description, _, _) = GetEditorMetaExportOption(i);
            var lines = WrapEditorTextToWidth(descriptionFont, description, textWidth);
            result[i] = Math.Max(1, (int)MathF.Ceiling(titleTop + titleLineHeight + descriptionGap +
                lines.Count * descriptionLineHeight * 1.22f + bottomPadding));
        }
        return result;
    }

    private Rectangle GetEditorMetaExportOptionBounds(int index, int virtualWidth, int virtualHeight)
    {
        var count = GetEditorMetaExportOptionCount();
        if (index < 0 || index >= count)
            return Rectangle.Empty;
        var unit = _projection.ScalePixels(1f);
        var margin = Math.Max(1, (int)MathF.Round(16f * unit));
        var modalWidth = Math.Min((int)MathF.Round(760f * unit), Math.Max(1, virtualWidth - margin * 2));
        var sidePadding = Math.Max(1, (int)MathF.Round(18f * unit));
        var headerHeight = (int)MathF.Round(94f * unit);
        var headerContentGap = (int)MathF.Round(24f * unit);
        var gap = (int)MathF.Round(6f * unit);
        var bottomPadding = (int)MathF.Round(18f * unit);
        var rowHeights = GetEditorMetaExportOptionRowHeights(virtualWidth);
        var rowsHeight = rowHeights.Sum();
        var modalHeight = headerHeight + headerContentGap + rowsHeight + gap * Math.Max(0, count - 1) + bottomPadding;
        modalHeight = Math.Min(modalHeight, Math.Max(1, virtualHeight - margin * 2));
        var modalX = (virtualWidth - modalWidth) / 2;
        var modalY = (virtualHeight - modalHeight) / 2;
        var x = modalX + sidePadding;
        var width = Math.Max(1, modalWidth - sidePadding * 2);
        var y = modalY + headerHeight + headerContentGap;
        for (var i = 0; i < index; i++)
            y += rowHeights[i] + gap;
        return new Rectangle(x, y, width, rowHeights[index]);
    }

    private int GetEditorMetaExportControlsTop(int virtualWidth, int virtualHeight)
        => GetEditorMetaExportToggleBounds(virtualWidth, virtualHeight).Y;

    private void PlaceEditorMetaCaretFromClick(Rectangle bounds, Microsoft.Xna.Framework.Point pixel, float unit)
    {
        var skinRuntimeAvailable = _chartEditorInterfaceRuntime.IsLoaded && _chartEditorInterfaceRuntime.LastError is null;
        var textLeft = bounds.X + 10f * unit;
        var textWidth = Math.Max(1f, bounds.Width - (skinRuntimeAvailable ? 20f : 42f) * unit);
        const string fontPath = "Roboto-Regular.ttf";
        var textSize = 24f * unit;
        SpriteFont? fallbackFont = null;
        float fallbackScale = 1f;

        float Measure(string candidate)
        {
            if (skinRuntimeAvailable)
                return _chartEditorInterfaceRuntime.MeasureTextWidthForUi(candidate, fontPath, textSize);
            fallbackFont ??= GetChartEditorFont(_chartEditorInterfaceLayout.LabelFont, 24f);
            return UnicodeTextRenderer.MeasureString(fallbackFont, FontSafeText(fallbackFont, candidate)).X * fallbackScale;
        }

        if (!skinRuntimeAvailable)
        {
            var fontName = _chartEditorInterfaceLayout.LabelFont;
            fallbackFont = GetChartEditorFont(fontName);
            var initialSize = UnicodeTextRenderer.MeasureString(fallbackFont, FontSafeText(fallbackFont, _editorMetaInputText));
            fallbackScale = Math.Min(1f, Math.Max(0.55f,
                (bounds.Height - 8f * unit) / Math.Max(1f, initialSize.Y)));
            fallbackFont = GetChartEditorFont(fontName, 24f * fallbackScale);
            fallbackScale = Math.Min(1f, (bounds.Height - 8f * unit) /
                Math.Max(1f, UnicodeTextRenderer.MeasureString(fallbackFont, "Ag").Y));
        }

        // The active field is visually wrapped now, so place the caret using both
        // pointer axes and the same wrapped line ranges used by the renderer.
        var lines = WrapEditorTextWithRanges(_editorMetaInputText, textWidth, Measure);
        var lineHeight = Math.Max(16f * unit, textSize * 1.2f);
        var blockHeight = lines.Count * lineHeight;
        var firstLineY = bounds.Y + (bounds.Height - blockHeight) * 0.5f;
        var localLine = Math.Clamp((int)MathF.Floor((pixel.Y - firstLineY) / Math.Max(1f, lineHeight)),
            0, lines.Count - 1);
        var line = lines[localLine];
        var clickX = Math.Max(0f, pixel.X - textLeft);
        var localCaret = 0;
        var previousWidth = 0f;
        foreach (var textElement in GetEditorTextElementRanges(line.Text))
        {
            var nextWidth = Measure(line.Text[..textElement.End]);
            if (clickX < (previousWidth + nextWidth) * 0.5f)
            {
                localCaret = textElement.Start;
                _editorMetaInputCaretIndex = Math.Clamp(line.Start + localCaret, 0, _editorMetaInputText.Length);
                return;
            }
            previousWidth = nextWidth;
            localCaret = textElement.End;
        }
        _editorMetaInputCaretIndex = Math.Clamp(line.Start + localCaret, 0, _editorMetaInputText.Length);
    }

    private static IEnumerable<(int Start, int End)> GetEditorTextElementRanges(string value)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            var start = enumerator.ElementIndex;
            var element = enumerator.GetTextElement();
            yield return (start, start + element.Length);
        }
    }

    private Rectangle GetEditorMetaInterfaceFieldBounds(int rowIndex, int virtualWidth, int virtualHeight)
    {
        if (_chartEditorInterfaceRuntime.TryGetUiButtonBounds($"editor_meta_row_{rowIndex}", out var bounds))
            return bounds;
        return GetEditorMetaFieldBounds(rowIndex, virtualWidth, virtualHeight);
    }

    private void HandleEditorMetaInterfaceFieldClick(int rowIndex, string fieldKey, string rowValue,
        Microsoft.Xna.Framework.Point pixel, int virtualWidth, int virtualHeight)
    {
        var bounds = GetEditorMetaInterfaceFieldBounds(rowIndex, virtualWidth, virtualHeight);
        if (bounds.IsEmpty)
            return;

        if (!string.Equals(_editorMetaEditingField, fieldKey, StringComparison.OrdinalIgnoreCase))
        {
            if (_editorMetaEditingField is not null)
                CommitEditorMetaFieldOnBlur();
            _editorMetaEditingField = fieldKey;
            _editorMetaInputText = GetEditorMetaFieldValue(fieldKey, rowValue);
            _editorMetaInputReplaceOnNextKey = false;
        }

        PlaceEditorMetaCaretFromClick(bounds, pixel, _projection.ScalePixels(1f));
        _editorMetaSelectionAnchor = _editorMetaInputCaretIndex;
        BeginEditorTextFieldMouseSelection("meta:" + fieldKey, bounds,
            _editorMetaInputCaretIndex, ref _editorMetaSelectionAnchor);
    }

    private Rectangle GetEditorElementOptionBounds(int rowIndex, int optionIndex, int virtualWidth, int virtualHeight)
    {
        if (optionIndex is < 0 or > 2)
            return Rectangle.Empty;
        var field = GetEditorMetaFieldBounds(rowIndex, virtualWidth, virtualHeight);
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var gap = Math.Max(2, (int)MathF.Round(3f * unit));
        var height = Math.Max(1, (int)MathF.Round(GetEditorMenuChildHeight(unit)));
        var popupHeight = height * 3 + gap * 2;
        var y = field.Bottom + gap;
        if (y + popupHeight > content.Bottom)
            y = field.Y - popupHeight - gap;
        y = Math.Clamp(y, content.Y, Math.Max(content.Y, content.Bottom - popupHeight));
        return new Rectangle(field.X, y + optionIndex * (height + gap), field.Width, height);
    }

    private static string NormalizeEditorElement(string? element)
        => (element ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "VOCAL" => "VOCAL",
            "SESSION" => "SESSION",
            _ => "DANCER"
        };

    private static Color GetEditorElementColor(string? element)
        => NormalizeEditorElement(element) switch
        {
            "VOCAL" => new Color(96, 165, 250),
            "SESSION" => new Color(52, 211, 153),
            _ => new Color(248, 113, 113)
        };

    private bool TryHandleEditorMetaPanelClick(Microsoft.Xna.Framework.Point pixel, int virtualWidth, int virtualHeight)
    {
        if (_editorCreateDifficultyConfirmOpen)
        {
            var unit = _projection.ScalePixels(1f);
            var modalWidth = Math.Min((int)MathF.Round(460f * unit), virtualWidth - 24);
            var modalHeight = Math.Max(1, (int)MathF.Round(190f * unit));
            var modal = new Rectangle((virtualWidth - modalWidth) / 2, (virtualHeight - modalHeight) / 2, modalWidth, modalHeight);
            var buttonHeight = (int)MathF.Round(34f * unit);
            var buttonY = modal.Bottom - (int)MathF.Round(48f * unit);
            var buttonWidth = (modal.Width - (int)MathF.Round(36f * unit)) / 2;
            var cancel = new Rectangle(modal.X + (int)MathF.Round(12f * unit), buttonY, buttonWidth, buttonHeight);
            var confirm = new Rectangle(cancel.Right + (int)MathF.Round(12f * unit), buttonY, buttonWidth, buttonHeight);
            if (confirm.Contains(pixel)) ConfirmCreateEditorDifficulty();
            else if (cancel.Contains(pixel)) CancelCreateEditorDifficulty();
            return true;
        }
        if (_editorMetaDeleteConfirmOpen)
        {
            var unit = _projection.ScalePixels(1f);
            var modalW = Math.Min((int)MathF.Round(520f * unit), virtualWidth - 48);
            var modalH = Math.Max(1, (int)MathF.Round(220f * unit));
            var modal = new Rectangle((virtualWidth - modalW) / 2, (virtualHeight - modalH) / 2, modalW, modalH);
            var buttonY = modal.Bottom - (int)MathF.Round(62f * unit);
            var buttonW = (modal.Width - (int)MathF.Round(68f * unit)) / 2;
            var cancel = new Rectangle(modal.X + (int)MathF.Round(22f * unit), buttonY, buttonW, (int)MathF.Round(42f * unit));
            var confirm = new Rectangle(cancel.Right + (int)MathF.Round(12f * unit), buttonY, buttonW, cancel.Height);
            if (confirm.Contains(pixel)) DeleteEditorDifficultyFromSession();
            else _editorMetaDeleteConfirmOpen = false;
            return true;
        }
        if (_editorMetaExportExpanded)
        {
            for (var i = 0; i < GetEditorMetaExportOptionCount(); i++)
            {
                if (GetEditorMetaExportOptionBounds(i, virtualWidth, virtualHeight).Contains(pixel))
                {
                    HandleEditorMetaExportOption(i);
                    return true;
                }
            }
            _editorMetaExportExpanded = false;
            return true;
        }
        if (!_editorMetaPanelOpen)
            return false;
        var panel = GetEditorMetaPanelBounds(virtualWidth, virtualHeight);
        if (!panel.Contains(pixel))
            return false;
        if (GetEditorMetaExitButtonBounds(virtualWidth, virtualHeight).Contains(pixel))
        {
            CancelEditorMetaField();
            _editorMetaExportExpanded = false;
            _editorMetaPanelOpen = false;
            EditorExitRequested?.Invoke();
            return true;
        }
        if (GetEditorMetaCloseButtonBounds(virtualWidth, virtualHeight).Contains(pixel))
        {
            CancelEditorMetaField();
            _editorMetaExportExpanded = false;
            _editorMetaPanelOpen = false;
            return true;
        }
        for (var i = 0; i < EditorDifficultyCodes.Length; i++)
        {
            if (!GetEditorMetaDifficultyButtonBounds(i, virtualWidth, virtualHeight).Contains(pixel))
                continue;
            _editorMetaScrollOffset = 0;
            _editorElementDropdownOpen = false;
            // Switching to an absent chart asks before creating its file.
            SwitchEditorDifficulty(EditorDifficultyCodes[i]);
            return true;
        }
        for (var i = 0; i < EditorMetaCategories.Length; i++)
        {
            if (!GetEditorMetaCategoryButtonBounds(i, virtualWidth, virtualHeight).Contains(pixel))
                continue;
            CancelEditorMetaField();
            _editorElementDropdownOpen = false;
            _editorMetaCategory = EditorMetaCategories[i];
            _editorMetaScrollOffset = 0;
            return true;
        }
        if (string.Equals(_editorMetaCategory, "TEST", StringComparison.OrdinalIgnoreCase))
        {
            if (GetEditorMetaLanTestAdbToggleBounds(virtualWidth, virtualHeight).Contains(pixel))
            {
                ToggleEditorLanTestAdb();
                return true;
            }
            if (GetEditorMetaLanTestButtonBounds(0, virtualWidth, virtualHeight).Contains(pixel))
            {
                StartEditorLanTest();
                return true;
            }
            if (GetEditorMetaLanTestButtonBounds(1, virtualWidth, virtualHeight).Contains(pixel))
            {
                RefreshEditorLanTest();
                return true;
            }
            if (GetEditorMetaLanTestButtonBounds(2, virtualWidth, virtualHeight).Contains(pixel))
            {
                StopEditorLanTest();
                return true;
            }
            if (GetEditorMetaContentBounds(virtualWidth, virtualHeight).Contains(pixel))
                return true;
        }
        if (string.Equals(_editorMetaCategory, "MEDIA", StringComparison.OrdinalIgnoreCase))
        {
            if (GetEditorMediaMusicButtonBounds(virtualWidth, virtualHeight).Contains(pixel) ||
                GetEditorMediaMusicStatusBounds(virtualWidth, virtualHeight).Contains(pixel))
            {
                CancelEditorMetaField();
                ImportEditorSongRequested?.Invoke();
                return true;
            }
            if (GetEditorMediaPreviewEditButtonBounds(virtualWidth, virtualHeight).Contains(pixel))
            {
                CancelEditorMetaField();
                OpenEditorPreviewEditor();
                return true;
            }
            if (GetEditorMediaPreviewPlayButtonBounds(virtualWidth, virtualHeight).Contains(pixel))
            {
                CancelEditorMetaField();
                if (_editorPreviewPlayer?.IsPlaying == true)
                {
                    StopEditorPreviewPlayback();
                    _statusMessage = "Preview playback stopped. Chart playback remains paused.";
                }
                else
                {
                    PlayExistingEditorPreview();
                }
                return true;
            }
            foreach (var isBig in new[] { false, true })
            {
                if (isBig && _editorBundleTemplate == ChartBundleTemplate.Tsts)
                    continue;
                if (GetEditorMediaArtworkPickButtonBounds(isBig, virtualWidth, virtualHeight).Contains(pixel) ||
                    GetEditorMediaArtworkFrameBounds(isBig, virtualWidth, virtualHeight).Contains(pixel))
                {
                    CancelEditorMetaField();
                    EditorMediaPickRequested?.Invoke(isBig ? "album_big" : "album");
                    return true;
                }
            }
            if (GetEditorMetaContentBounds(virtualWidth, virtualHeight).Contains(pixel))
                return true;
        }

        if (string.Equals(_editorMetaCategory, "TEST", StringComparison.OrdinalIgnoreCase))
            return true;

        var metaRows = GetEditorMetaRows(_tempo?.SecondsToTick(_playSeconds) ?? 0.0);
        var elementRowIndex = metaRows.FindIndex(row => row.Label == "ELEMENT");
        if (_editorElementDropdownOpen && elementRowIndex >= 0)
        {
            var elementOptions = new[] { "DANCER", "VOCAL", "SESSION" };
            for (var optionIndex = 0; optionIndex < elementOptions.Length; optionIndex++)
            {
                if (!GetEditorElementOptionBounds(elementRowIndex, optionIndex, virtualWidth, virtualHeight).Contains(pixel))
                    continue;
                if (_chart is not null)
                {
                    _chart.Element = elementOptions[optionIndex];
                    // The panel skin reads game.element dynamically every frame, but the
                    // skin runtime's view is normally set when the chart is loaded. Push
                    // this metadata edit immediately so preview panel tints update without
                    // closing or reloading the INFO menu.
                    _skinRuntime.SetChartElement(_chart.Element);
                    _pauseSkinRuntime.SetChartElement(_chart.Element);
                    _resultSkinRuntime.SetChartElement(_chart.Element);
                    SyncSharedEditorMetadataToDifficultyStates();
                    StoreActiveEditorDifficulty();
                    MarkEditorDirty();
                    _statusMessage = $"Element set to {elementOptions[optionIndex]}.";
                }
                _editorElementDropdownOpen = false;
                return true;
            }
            if (!GetEditorMetaFieldBounds(elementRowIndex, virtualWidth, virtualHeight).Contains(pixel))
                _editorElementDropdownOpen = false;
        }
        ClampEditorMetaScrollOffset(metaRows, virtualWidth, virtualHeight);
        if (GetEditorMetaDifficultyToggleBounds(virtualWidth, virtualHeight).Contains(pixel))
        {
            var code = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart?.Difficulty, _sourceName);
            if (!HasEditorDifficultyChart(code)) return true;
            ToggleEditorDifficultyEnabled(code);
            MarkEditorDirty();
            return true;
        }
        var metaContent = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var firstVisibleRow = _editorMetaScrollOffset;
        var metaRowLayouts = GetEditorMetaRowLayouts(metaRows, virtualWidth, virtualHeight);
        if (metaContent.Contains(pixel))
        {
            for (var i = firstVisibleRow; i < metaRows.Count; i++)
            {
                var bounds = GetEditorMetaFieldBounds(i, virtualWidth, virtualHeight, metaRows, metaRowLayouts);
                if (bounds.Y >= metaContent.Bottom)
                    break;
                if (bounds.Bottom <= metaContent.Y || !bounds.Contains(pixel))
                    continue;
                var fieldLabel = metaRows[i].Label;
                if (fieldLabel == "DELETE DIFFICULTY")
                {
                    if (HasEditorDifficultyChart(_editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart?.Difficulty, _sourceName)))
                        _editorMetaDeleteConfirmOpen = true;
                    return true;
                }
                if (fieldLabel == "ELEMENT")
                {
                    CancelEditorMetaField();
                    _editorElementDropdownOpen = !_editorElementDropdownOpen;
                    return true;
                }
                if (fieldLabel == "DIFFICULTY")
                {
                    CancelEditorMetaField();
                    var difficultyCode = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart?.Difficulty, _sourceName);
                    if (!HasEditorDifficultyChart(difficultyCode)) return true;
                    ToggleEditorDifficultyEnabled(difficultyCode);
                    MarkEditorDirty();
                    _editorElementDropdownOpen = false;
                    _statusMessage = IsEditorDifficultyEnabled(difficultyCode)
                        ? $"{GetEditorDifficultyName(difficultyCode)} difficulty enabled."
                        : $"{GetEditorDifficultyName(difficultyCode)} difficulty disabled; this chart will be omitted from exports.";
                    return true;
                }
                if (fieldLabel == "MUSIC")
                {
                    CancelEditorMetaField();
                    ImportEditorSongRequested?.Invoke();
                    return true;
                }
                if (fieldLabel == "PREVIEW")
                {
                    CancelEditorMetaField();
                    if (GetEditorPreviewRowPlayButtonBounds(i, virtualWidth, virtualHeight).Contains(pixel))
                    {
                        if (_editorPreviewPlayer?.IsPlaying == true)
                        {
                            StopEditorPreviewPlayback();
                            _statusMessage = "Preview playback stopped. Chart playback remains paused.";
                        }
                        else
                        {
                            PlayExistingEditorPreview();
                        }
                    }
                    else
                    {
                        OpenEditorPreviewEditor();
                    }
                    return true;
                }
                if (fieldLabel == "ALBUM" || (fieldLabel == "ALBUM BIG" && _editorBundleTemplate != ChartBundleTemplate.Tsts))
                {
                    CancelEditorMetaField();
                    EditorMediaPickRequested?.Invoke(fieldLabel == "ALBUM" ? "album" : "album_big");
                    return true;
                }
                if (fieldLabel == "CUSTOM JUDGEMENT WINDOWS")
                {
                    CancelEditorMetaField();
                    _editorCustomJudgementWindowsEnabled = !_editorCustomJudgementWindowsEnabled;
                    MarkEditorDirty();
                    if (_editorCustomJudgementWindowsEnabled && _chart is not null)
                    {
                        var code = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart.Difficulty, _sourceName);
                        _bundleJudgementWindows[code] = (_chart.PerfectPlusWindowMs, _chart.PerfectWindowMs, _chart.GoodWindowMs);
                    }
                    SyncSharedEditorMetadataToDifficultyStates();
                    StoreActiveEditorDifficulty();
                    _statusMessage = _editorCustomJudgementWindowsEnabled
                        ? "Custom judgement windows enabled; they will be included in exports."
                        : "Custom judgement windows disabled; judgement_windows_ms will be omitted from exports.";
                    return true;
                }
                var fieldKey = GetEditorMetaFieldKey(fieldLabel);
                if (fieldKey is null)
                    return true;
                if (string.Equals(_editorMetaEditingField, fieldKey, StringComparison.OrdinalIgnoreCase))
                {
                    PlaceEditorMetaCaretFromClick(bounds, pixel, _projection.ScalePixels(1f));
                    BeginEditorTextFieldMouseSelection("meta:" + fieldKey, bounds,
                        _editorMetaInputCaretIndex, ref _editorMetaSelectionAnchor);
                    return true;
                }
                if (_editorMetaEditingField is not null)
                    CommitEditorMetaFieldOnBlur();
                _editorMetaEditingField = fieldKey;
                _editorMetaInputText = GetEditorMetaFieldValue(fieldKey, metaRows[i].Value);
                PlaceEditorMetaCaretFromClick(bounds, pixel, _projection.ScalePixels(1f));
                _editorMetaInputReplaceOnNextKey = false;
                _editorMetaSelectionAnchor = _editorMetaInputCaretIndex;
                BeginEditorTextFieldMouseSelection("meta:" + fieldKey, bounds,
                    _editorMetaInputCaretIndex, ref _editorMetaSelectionAnchor);
                return true;
            }
        }
        if (GetEditorMetaExportToggleBounds(virtualWidth, virtualHeight).Contains(pixel))
        {
            CancelEditorMetaField();
            _editorMetaExportExpanded = true;
            return true;
        }
        // The panel blocks chart clicks while open, even in its unused space.
        return true;
    }

    private Rectangle GetEditorScrollSpeedPanelBounds(int virtualWidth, int virtualHeight)
    {
        var unit = _projection.ScalePixels(1f);
        var margin = (int)MathF.Round((float)_chartEditorInterfaceLayout.EdgeMargin * unit);
        var button = (int)MathF.Round((float)_chartEditorInterfaceLayout.ScrollButtonSize * unit);
        var y = margin;
        var x = margin;
        var width = (int)MathF.Round((float)_chartEditorInterfaceLayout.ScrollSpeedWidth * unit);
        return new Rectangle(x, y, width, button + (int)MathF.Round(16f * unit));
    }

    private Rectangle GetEditorScrollSpeedButtonBounds(bool increase, int virtualWidth, int virtualHeight)
    {
        var unit = _projection.ScalePixels(1f);
        var panel = GetEditorScrollSpeedPanelBounds(virtualWidth, virtualHeight);
        var button = (int)MathF.Round((float)_chartEditorInterfaceLayout.ScrollButtonSize * unit);
        var buttonOffset = increase ? (float)_chartEditorInterfaceLayout.ScrollSpeedWidth - 40f : 6f;
        return new Rectangle(panel.X + (int)MathF.Round(buttonOffset * unit),
            panel.Y + (int)MathF.Round(8f * unit), button, button);
    }

    private Rectangle GetEditorTimelineScrubberBounds(int virtualWidth, int virtualHeight)
    {
        var unit = _projection.ScalePixels(1f);
        var margin = (int)MathF.Round((float)_chartEditorInterfaceLayout.EdgeMargin * unit);
        var speedPanel = GetEditorScrollSpeedPanelBounds(virtualWidth, virtualHeight);
        var top = speedPanel.Bottom + (int)MathF.Round((float)_chartEditorInterfaceLayout.PanelGap * unit);
        var bottomLeftStrip = GetEditorBottomLeftStripBounds(virtualWidth, virtualHeight);
        var bottom = bottomLeftStrip.Y - (int)MathF.Round(10f * unit);
        var width = Math.Min((int)MathF.Round((float)_chartEditorInterfaceLayout.TimelineWidth * unit), Math.Max(64, virtualWidth / 3));
        var height = Math.Max(1, bottom - top);
        return new Rectangle(margin, top, width, height);
    }

    private float GetEditorTimelineTrackTop(Rectangle bounds)
        => bounds.Y + _projection.ScalePixels(12f);

    private float GetEditorTimelineTrackBottom(Rectangle bounds)
        => bounds.Bottom - _projection.ScalePixels(4f);

    private bool IsEditorToolbarPanelContains(Microsoft.Xna.Framework.Point pixel, int virtualWidth, int virtualHeight)
        => GetEditorProblemsPanelBounds(virtualWidth, virtualHeight).Contains(pixel) ||
           (_editorMetaPanelOpen && GetEditorMetaPanelBounds(virtualWidth, virtualHeight).Contains(pixel)) ||
           GetEditorTopStripPanelBounds(virtualWidth, virtualHeight).Contains(pixel) ||
           GetEditorMenuPanelBounds(virtualWidth, virtualHeight).Contains(pixel) ||
           GetEditorBottomActionPanelBounds(virtualWidth, virtualHeight).Contains(pixel) ||
           GetEditorBottomLeftStripBounds(virtualWidth, virtualHeight).Contains(pixel) ||
           (_editorMenuSection == EditorMenuSection.Snap && GetEditorSnapPopupBounds(virtualWidth, virtualHeight).Contains(pixel)) ||
           (_editorMenuSection == EditorMenuSection.Metronome && GetEditorMetronomePopupBounds(virtualWidth, virtualHeight).Contains(pixel)) ||
           GetEditorScrollSpeedPanelBounds(virtualWidth, virtualHeight).Contains(pixel) ||
           GetEditorTimelineScrubberBounds(virtualWidth, virtualHeight).Contains(pixel);

    private static readonly string[] EditorBankNames = { "Forward", "Center", "Backward" };

    private void EnsureEditorIndicatorProjection(int virtualWidth, int virtualHeight)
    {
        if (_editorIndicatorProjectionWidth != virtualWidth ||
            _editorIndicatorProjectionHeight != virtualHeight)
        {
            _editorIndicatorProjection.Resize(virtualWidth, virtualHeight);
            _editorIndicatorProjectionWidth = virtualWidth;
            _editorIndicatorProjectionHeight = virtualHeight;
        }

        // Keep a separate projection loaded with editorRenderConfig.lua. Never
        // derive indicator positions from the live projection, because it may be
        // compact/collapsed or using the FPV renderConfig.lua view.
        if (!ReferenceEquals(_editorIndicatorProjectionConfig, _editorRenderConfig))
        {
            _editorIndicatorProjection.LoadRenderConfig(_editorRenderConfig);
            _editorIndicatorProjectionConfig = _editorRenderConfig;
        }

        if (_tempo is not null && !ReferenceEquals(_editorIndicatorProjectionTempo, _tempo))
        {
            _editorIndicatorProjection.SetTempo(_tempo);
            _editorIndicatorProjectionTempo = _tempo;
        }

        _editorIndicatorProjection.Compact = false;
    }

    private Rectangle GetEditorBankIndicatorBounds(string side, string bank, int virtualWidth, int virtualHeight)
    {
        EnsureEditorIndicatorProjection(virtualWidth, virtualHeight);
        var unit = _editorIndicatorProjection.ScalePixels(1f);
        var width = (int)MathF.Round(62f * unit);
        var height = (int)MathF.Round(32f * unit);
        var slice = _editorIndicatorProjection.ProjectBankSliceAtTravel(0f, side, bank);
        var centerX = (slice.Inner.X + slice.Outer.X) * 0.5f;
        var x = (int)MathF.Round(centerX - width * 0.5f);
        var margin = (int)MathF.Round(2f * unit);
        x = Math.Clamp(x, margin, Math.Max(margin, virtualWidth - width - margin));
        var y = (int)MathF.Round(12f * unit);
        return new Rectangle(x, y, width, height);
    }

    private bool IsEditorBankIndicatorArea(Vector2 point, int virtualWidth, int virtualHeight)
    {
        if (!_chartEditorMode)
            return false;
        var pixel = new Microsoft.Xna.Framework.Point((int)MathF.Round(point.X), (int)MathF.Round(point.Y));
        foreach (var side in Sides)
            foreach (var bank in EditorBankNames)
                if (GetEditorBankIndicatorBounds(side, bank, virtualWidth, virtualHeight).Contains(pixel))
                    return true;
        return false;
    }

    private bool TryGetClickedEditorBankIndicator(Vector2 point, int virtualWidth, int virtualHeight, out string side, out string bank)
    {
        side = string.Empty;
        bank = string.Empty;
        if (!_chartEditorMode)
            return false;

        var pixel = new Microsoft.Xna.Framework.Point((int)MathF.Round(point.X), (int)MathF.Round(point.Y));
        foreach (var candidateSide in Sides)
        {
            foreach (var candidateBank in EditorBankNames)
            {
                if (!GetEditorBankIndicatorBounds(candidateSide, candidateBank, virtualWidth, virtualHeight).Contains(pixel))
                    continue;

                side = candidateSide;
                bank = candidateBank;
                return true;
            }
        }
        return false;
    }

    private void SelectEditorPreviewBank(string side, string bank)
    {
        // This selects the active preview route now and the route seed for
        // Restart/seek. Future successful SuperFlicks can still change it.
        if (side.Equals("left", StringComparison.OrdinalIgnoreCase))
            _editorPreviewStartLeftBank = bank;
        else
            _editorPreviewStartRightBank = bank;

        var previewState = EnsureEditorPreviewGearState();
        SetGearStateBank(previewState, side, bank);
        _gameplayState = previewState.Copy();
        SyncEditorPreviewBankIndicators(previewState);
        RecordEditorTimelineRouteState(_playSeconds, previewState);
        _editorHoverCacheValid = false;
        var modeLabel = bank switch { "Forward" => "1K", "Center" => "2K", _ => "3K" };
        _statusMessage = $"Editor bank preview: {side} {bank} ({modeLabel}). Use the bank indicators or the side-specific bank keybinds to switch routes.";
    }

    private void CycleEditorPreviewBank(string side)
    {
        if (!_chartEditorMode)
            return;
        var previewState = EnsureEditorPreviewGearState();
        var activeBank = GearTimeline.ActiveBankForSide(previewState, side);
        var index = Array.FindIndex(EditorBankNames, bank =>
            string.Equals(bank, activeBank, StringComparison.OrdinalIgnoreCase));
        if (index < 0) index = 0;
        var nextBank = EditorBankNames[(index + 1) % EditorBankNames.Length];
        SelectEditorPreviewBank(side, nextBank);
    }

    private bool TryHandleEditorBankIndicatorClick(Vector2 point, int virtualWidth, int virtualHeight)
    {
        if (!_chartEditorMode)
            return false;
        var pixel = new Microsoft.Xna.Framework.Point((int)MathF.Round(point.X), (int)MathF.Round(point.Y));
        foreach (var side in Sides)
        {
            foreach (var bank in EditorBankNames)
            {
                if (!GetEditorBankIndicatorBounds(side, bank, virtualWidth, virtualHeight).Contains(pixel))
                    continue;
                SelectEditorPreviewBank(side, bank);
                return true;
            }
        }
        return false;
    }

    private void OpenEditorTempoGenerator()
    {
        _editorMenuSection = EditorMenuSection.Stutter;
        _editorBpmInputFocused = false;
        _editorBpmInputReplaceOnNextKey = false;
        _editorBpmPlacementPending = false;
        _editorSelectedTempoEvent = null;
        _editorPlacementEnabled = false;
        _editorConnectorMode = false;
        _editorStutterInputField = EditorStutterInputField.None;
        _editorStutterEditingGroup = null;
        _editorStutterStartTick = -1;
        _editorStutterEndTick = -1;
        _editorStutterStartPlacementPending = false;
        _editorStutterEndPlacementPending = false;
        _statusMessage = "Tempo generator: choose Pulse, Alternate, or Ramp, set the start/end ticks, then generate.";
    }

    private void HandleEditorStutterKeybind(string action)
    {
        var needsGeneratorSetup = action is "editor_stutter_set_start" or "editor_stutter_set_end" or
            "editor_stutter_pattern_pulse" or "editor_stutter_pattern_alternate" or
            "editor_stutter_pattern_ramp" or "editor_stutter_toggle_timing_mode";
        if (needsGeneratorSetup && _editorMenuSection != EditorMenuSection.Stutter)
            OpenEditorTempoGenerator();

        switch (action)
        {
            case "editor_stutter_set_start":
                _editorStutterEndPlacementPending = false;
                _editorStutterStartPlacementPending = !_editorStutterStartPlacementPending;
                _editorPlacementEnabled = false;
                _editorConnectorMode = false;
                _statusMessage = _editorStutterStartPlacementPending
                    ? "Start placement armed. Tap a visible lane to place it; use Cancel Current Tool / Action to cancel."
                    : "Start placement cancelled.";
                break;
            case "editor_stutter_set_end":
                if (_editorStutterStartTick < 0)
                {
                    _statusMessage = "Set the generator start before setting its end.";
                    break;
                }
                _editorStutterStartPlacementPending = false;
                _editorStutterEndPlacementPending = !_editorStutterEndPlacementPending;
                _editorPlacementEnabled = false;
                _editorConnectorMode = false;
                _statusMessage = _editorStutterEndPlacementPending
                    ? "End placement armed. Tap a later tick to place it; use Cancel Current Tool / Action to cancel."
                    : "End placement cancelled.";
                break;
            case "editor_stutter_pattern_pulse":
                _editorStutterAlternate = false;
                _editorStutterRamp = false;
                _statusMessage = "Pattern set to Pulse: fast BPM pulses over slow BPM gaps; no separate Slow Time is used.";
                break;
            case "editor_stutter_pattern_alternate":
                _editorStutterAlternate = true;
                _editorStutterRamp = false;
                _statusMessage = "Pattern set to Alternate: Fast and Slow BPM alternate for beat-based phase lengths.";
                break;
            case "editor_stutter_pattern_ramp":
                _editorStutterAlternate = false;
                _editorStutterRamp = true;
                _statusMessage = "Pattern set to Ramp: Start BPM changes linearly to Target BPM.";
                break;
            case "editor_stutter_toggle_timing_mode":
                if (_editorStutterRamp)
                {
                    _statusMessage = "Timing mode applies to Pulse and Alternate patterns, not Ramp.";
                    break;
                }
                _editorStutterBalanced = !_editorStutterBalanced;
                _statusMessage = _editorStutterBalanced
                    ? "Balanced mode scales Fast and Slow BPM together to match the starting BPM's average timing."
                    : "Free mode uses the entered Fast and Slow BPM values without timing compensation.";
                break;
            case "editor_stutter_toggle_auto_adjust":
                _editorAutoAdjustBpm = !_editorAutoAdjustBpm;
                _statusMessage = _editorAutoAdjustBpm
                    ? "BPM auto adjust ON: later events and the generator end keep their playback timestamps."
                    : "BPM auto adjust OFF: later event ticks stay where they are.";
                break;
            case "editor_stutter_ramp_move_up":
                if (_editorStutterRamp) MoveEditorChartAfterRampEnd(1);
                else _statusMessage = "Move up is available for the Ramp pattern.";
                break;
            case "editor_stutter_ramp_move_down":
                if (_editorStutterRamp) MoveEditorChartAfterRampEnd(-1);
                else _statusMessage = "Move down is available for the Ramp pattern.";
                break;
            case "editor_stutter_cycle_resolution":
                if (_editorStutterRamp)
                {
                    _editorRampResolutionIndex = (_editorRampResolutionIndex + 1) % EditorRampResolutionBeats.Length;
                    _statusMessage = $"Ramp sample resolution set to {EditorRampResolutionLabels[_editorRampResolutionIndex]}. Tempo lines stay on this grid; BPM changes linearly.";
                }
                else _statusMessage = "Ramp resolution is available for the Ramp pattern.";
                break;
            case "editor_stutter_generate": GenerateEditorStutter(); break;
            case "editor_stutter_delete_group": DeleteEditorStutterGroup(); break;
            case "editor_stutter_ungroup": UngroupEditorStutterGroup(); break;
        }
    }

    private void FocusEditorBpmInputFromKeybind()
    {
        if (_editorPanelsViewEnabled)
            return;
        if (_editorMenuSection != EditorMenuSection.Bpm)
            HandleEditorMenuCategoryClick(2);
        if (_editorMenuSection != EditorMenuSection.Bpm)
            return;
        if (_editorSelectedTempoEvent is null && !_editorBpmPlacementPending && !_editorBpmInputPreserveOnRefocus)
            SetEditorBpmInputFromCurrentTick();
        _editorBpmInputPreserveOnRefocus = false;
        _editorBpmInputFocused = true;
        _editorBpmInputCaretIndex = _editorBpmInputText.Length;
        _editorBpmSelectionAnchor = _editorBpmInputCaretIndex;
        _editorBpmInputReplaceOnNextKey = false;
    }

    private bool HandleEditorDebugKeybinds()
    {
        if (KeybindSettings.IsPressed("editor_toggle_gameplay_mode"))
        {
            if (!_lockMenuPlayMode) ToggleGameplayMode();
            return true;
        }
        return false;
    }

    private void SeekEditorByBeats(double beatDelta)
    {
        if (_tempo is null || !double.IsFinite(beatDelta)) return;
        var ticksPerBeat = _tempo.TicksPerBeat;
        if (!double.IsFinite(ticksPerBeat) || ticksPerBeat <= 0) return;

        // Move along chart ticks, then convert back to seconds. This keeps the jump
        // equal to the requested number of beats even when a tempo change is crossed.
        var currentTick = _tempo.SecondsToTick(_playSeconds);
        var targetTick = Math.Max(0.0, currentTick + beatDelta * ticksPerBeat);
        _playSeconds = Math.Clamp(_tempo.TickToSeconds(targetTick), 0.0, ChartDuration());
    }

    private bool HandleEditorCommandKeybinds()
    {
        // Preview mode is available as a keybind as well as a toolbar action.
        if (KeybindSettings.IsPressed("editor_toggle_preview_mode"))
        {
            SetEditorPreviewMode(!_editorPreviewMode);
            return true;
        }

        // Editor playback, seeking, scroll speed, view, and bank routing are
        // configurable keybinds rather than hard-coded checks.
        if (KeybindSettings.IsPressed("editor_scroll_speed_decrease"))
        {
            AdjustPauseScrollSpeed(-0.10);
            _statusMessage = $"Editor scroll speed: {_projection.ScrollSpeedScale:0.00}x";
            return true;
        }
        if (KeybindSettings.IsPressed("editor_scroll_speed_increase"))
        {
            AdjustPauseScrollSpeed(0.10);
            _statusMessage = $"Editor scroll speed: {_projection.ScrollSpeedScale:0.00}x";
            return true;
        }
        if (KeybindSettings.IsPressed("editor_restart_preview")) { _playSeconds = 0; ResetGameplayState(); return true; }
        if (KeybindSettings.IsPressed("editor_seek_backward_1_beat")) { SeekEditorByBeats(-1.0); return true; }
        if (KeybindSettings.IsPressed("editor_seek_forward_1_beat")) { SeekEditorByBeats(1.0); return true; }
        if (KeybindSettings.IsPressed("editor_seek_backward_5_beats")) { SeekEditorByBeats(-5.0); return true; }
        if (KeybindSettings.IsPressed("editor_seek_forward_5_beats")) { SeekEditorByBeats(5.0); return true; }
        if (KeybindSettings.IsPressed("editor_switch_left_bank")) { CycleEditorPreviewBank("left"); return true; }
        if (KeybindSettings.IsPressed("editor_switch_right_bank")) { CycleEditorPreviewBank("right"); return true; }

        // File commands and selection editing. These intentionally run before
        // note/panel tool handling, and after focused text fields have consumed input.
        if (KeybindSettings.IsPressed("editor_open_export_modal"))
        {
            CancelEditorMetaField();
            _editorMetaPanelOpen = true;
            _editorMetaExportExpanded = true;
            _editorMetaDeleteConfirmOpen = false;
            _editorElementDropdownOpen = false;
            return true;
        }
        if (KeybindSettings.IsPressed("editor_quick_save")) { QuickSaveRequested?.Invoke(); return true; }
#if !TSC_PUBLISH
        if (KeybindSettings.IsPressed("editor_save_fpk")) { SaveFpkRequested?.Invoke(); return true; }
#endif
        if (KeybindSettings.IsPressed("editor_save_ares")) { SaveAresRequested?.Invoke(); return true; }
        if (KeybindSettings.IsPressed("editor_save_tsts")) { SaveTstsRequested?.Invoke(); return true; }
        if (KeybindSettings.IsPressed("editor_save_json")) { SaveJsonRequested?.Invoke(); return true; }
        if (KeybindSettings.IsPressed("editor_save_tsc_zip")) { SaveTscZipRequested?.Invoke(); return true; }
        if (KeybindSettings.IsPressed("editor_undo")) { UndoEditorChange(); return true; }
        if (KeybindSettings.IsPressed("editor_redo")) { RedoEditorChange(); return true; }
        if (KeybindSettings.IsPressed("editor_copy")) { CopySelectedEditorNotes(); return true; }
        if (KeybindSettings.IsPressed("editor_cut")) { CutSelectedEditorNotes(); return true; }
        if (KeybindSettings.IsPressed("editor_paste_fit")) { PasteEditorNotes(fitToBank: true); return true; }
        if (KeybindSettings.IsPressed("editor_paste")) { PasteEditorNotes(fitToBank: false); return true; }
        if (KeybindSettings.IsPressed("editor_deselect")) { HandleEditorActionClick(EditorActionDeselect); return true; }
        if (KeybindSettings.IsPressed("editor_select_menu")) { HandleEditorActionClick(EditorActionSelect); return true; }
        if (KeybindSettings.IsPressed("editor_quick_fix_links"))
        {
            if (!_editorPanelsViewEnabled) QuickFixEditorLinks();
            return true;
        }

        // Editor toolbar actions can be called without the mouse toolbar.
        if (KeybindSettings.IsPressed("editor_toggle_play_preview")) { HandleEditorActionClick(EditorActionPlay); return true; }
        if (KeybindSettings.IsPressed("editor_toggle_view_mode")) { HandleEditorActionClick(EditorActionViewMode); return true; }
        if (KeybindSettings.IsPressed("editor_toggle_expand")) { HandleEditorActionClick(EditorActionExpand); return true; }
        if (KeybindSettings.IsPressed("editor_toggle_beat_lines")) { HandleEditorActionClick(EditorActionBeatLines); return true; }
        if (KeybindSettings.IsPressed("editor_toggle_attributes")) { HandleEditorActionClick(EditorActionAttributes); return true; }
        if (KeybindSettings.IsPressed("editor_toggle_tempo_lines")) { HandleEditorActionClick(EditorActionTempoLines); return true; }
        if (KeybindSettings.IsPressed("editor_toggle_constant_scroll")) { HandleEditorActionClick(EditorActionConstantScroll); return true; }
        if (KeybindSettings.IsPressed("editor_toggle_snap_menu")) { HandleEditorActionClick(EditorActionSnap); return true; }
        if (KeybindSettings.IsPressed("editor_toggle_panels")) { HandleEditorActionClick(EditorActionPanels); return true; }
        if (KeybindSettings.IsPressed("editor_toggle_info")) { HandleEditorActionClick(EditorActionMeta); return true; }

        if (KeybindSettings.IsPressed("editor_menu_notes"))
        {
            if (!_editorPanelsViewEnabled) HandleEditorMenuCategoryClick(0);
            return true;
        }
        if (KeybindSettings.IsPressed("editor_menu_tools"))
        {
            if (!_editorPanelsViewEnabled) HandleEditorMenuCategoryClick(1);
            return true;
        }
        if (KeybindSettings.IsPressed("editor_menu_bpm"))
        {
            if (!_editorPanelsViewEnabled) HandleEditorMenuCategoryClick(2);
            return true;
        }
        if (KeybindSettings.IsPressed("editor_menu_duration")) { OpenEditorDurationTool(); return true; }
        if (KeybindSettings.IsPressed("editor_select_cycle_y")) { HandleEditorSelectMenuChildClick(0); return true; }
        if (KeybindSettings.IsPressed("editor_select_toggle_x")) { HandleEditorSelectMenuChildClick(1); return true; }
        if (KeybindSettings.IsPressed("editor_select_apply")) { HandleEditorSelectMenuChildClick(2); return true; }
        if (KeybindSettings.IsPressed("editor_bpm_focus_input")) { FocusEditorBpmInputFromKeybind(); return true; }
        if (KeybindSettings.IsPressed("editor_bpm_toggle_auto_adjust"))
        {
            _editorAutoAdjustBpm = !_editorAutoAdjustBpm;
            _statusMessage = _editorAutoAdjustBpm
                ? "BPM auto adjust ON: later events keep their playback times when tempo changes."
                : "BPM auto adjust OFF: later event ticks stay where they are.";
            return true;
        }
        if (KeybindSettings.IsPressed("editor_bpm_flatten")) { FlattenEditorBpmChanges(); return true; }
        if (KeybindSettings.IsPressed("editor_bpm_open_generator")) { OpenEditorTempoGenerator(); return true; }
        if (KeybindSettings.IsPressed("editor_stutter_set_start")) { HandleEditorStutterKeybind("editor_stutter_set_start"); return true; }
        if (KeybindSettings.IsPressed("editor_stutter_set_end")) { HandleEditorStutterKeybind("editor_stutter_set_end"); return true; }
        if (KeybindSettings.IsPressed("editor_stutter_pattern_pulse")) { HandleEditorStutterKeybind("editor_stutter_pattern_pulse"); return true; }
        if (KeybindSettings.IsPressed("editor_stutter_pattern_alternate")) { HandleEditorStutterKeybind("editor_stutter_pattern_alternate"); return true; }
        if (KeybindSettings.IsPressed("editor_stutter_pattern_ramp")) { HandleEditorStutterKeybind("editor_stutter_pattern_ramp"); return true; }
        if (KeybindSettings.IsPressed("editor_stutter_toggle_timing_mode")) { HandleEditorStutterKeybind("editor_stutter_toggle_timing_mode"); return true; }
        if (KeybindSettings.IsPressed("editor_stutter_toggle_auto_adjust")) { HandleEditorStutterKeybind("editor_stutter_toggle_auto_adjust"); return true; }
        if (KeybindSettings.IsPressed("editor_stutter_ramp_move_up")) { HandleEditorStutterKeybind("editor_stutter_ramp_move_up"); return true; }
        if (KeybindSettings.IsPressed("editor_stutter_ramp_move_down")) { HandleEditorStutterKeybind("editor_stutter_ramp_move_down"); return true; }
        if (KeybindSettings.IsPressed("editor_stutter_cycle_resolution")) { HandleEditorStutterKeybind("editor_stutter_cycle_resolution"); return true; }
        if (KeybindSettings.IsPressed("editor_stutter_generate")) { HandleEditorStutterKeybind("editor_stutter_generate"); return true; }
        if (KeybindSettings.IsPressed("editor_stutter_delete_group")) { HandleEditorStutterKeybind("editor_stutter_delete_group"); return true; }
        if (KeybindSettings.IsPressed("editor_stutter_ungroup")) { HandleEditorStutterKeybind("editor_stutter_ungroup"); return true; }

        foreach (var snap in new[] { ("editor_snap_1_2", 2), ("editor_snap_1_3", 3), ("editor_snap_1_4", 4), ("editor_snap_1_6", 6), ("editor_snap_1_8", 8), ("editor_snap_1_12", 12), ("editor_snap_1_16", 16), ("editor_snap_1_32", 32) })
        {
            if (!KeybindSettings.IsPressed(snap.Item1)) continue;
            _editorSnapDivisor = snap.Item2;
            _editorHoverCacheValid = false;
            _statusMessage = $"Beat snap: 1/{_editorSnapDivisor}.";
            return true;
        }
        if (KeybindSettings.IsPressed("editor_superflick_target_1k"))
        {
            if (_editorSuperFlickPlacementPending) SelectEditorSuperFlickTarget(0);
            return true;
        }
        if (KeybindSettings.IsPressed("editor_superflick_target_2k"))
        {
            if (_editorSuperFlickPlacementPending) SelectEditorSuperFlickTarget(1);
            return true;
        }
        if (KeybindSettings.IsPressed("editor_superflick_target_3k"))
        {
            if (_editorSuperFlickPlacementPending) SelectEditorSuperFlickTarget(2);
            return true;
        }

        // Panel-specific hotkeys are meaningful only while the panel editor is visible.
        if (_editorPanelsViewEnabled)
        {
            if (KeybindSettings.IsPressed("editor_panel_connector")) { ToggleEditorPanelsConnectorTool(); return true; }
            for (var attr = 101; attr <= 108; attr++)
            {
                if (KeybindSettings.IsPressed($"editor_panel_attr_{attr}"))
                {
                    SelectEditorPanelAttribute(attr);
                    return true;
                }
            }
            return false;
        }
        return false;
    }

    private bool HandleEditorToolKeybinds()
    {
        if (_editorPanelsViewEnabled)
        {
            if (KeybindSettings.IsPressed("editor_tool_replace_selection"))
            {
                ReplaceSelectedEditorNotes();
                return true;
            }
            return false;
        }
        if (KeybindSettings.IsPressed("editor_tool_tap")) { SelectEditorTool(0); return true; }
        if (KeybindSettings.IsPressed("editor_tool_connector")) { SelectEditorTool(1); return true; }
        if (KeybindSettings.IsPressed("editor_tool_flick_left")) { SelectEditorTool(2); return true; }
        if (KeybindSettings.IsPressed("editor_tool_flick_right")) { SelectEditorTool(3); return true; }
        if (KeybindSettings.IsPressed("editor_tool_vibrato")) { SelectEditorTool(4); return true; }
        if (KeybindSettings.IsPressed("editor_tool_superflick")) { ArmEditorSuperFlickTool(); return true; }
        if (KeybindSettings.IsPressed("editor_tool_replace_selection")) { ReplaceSelectedEditorNotes(); return true; }
        if (KeybindSettings.IsPressed("editor_tool_link_y")) { RunEditorEditTool(0); return true; }
        if (KeybindSettings.IsPressed("editor_tool_link_x")) { RunEditorEditTool(1); return true; }
        if (KeybindSettings.IsPressed("editor_tool_apply_duration")) { RunEditorEditTool(2); return true; }
        if (KeybindSettings.IsPressed("editor_tool_unlink_preserve")) { RunEditorEditTool(3); return true; }
        if (KeybindSettings.IsPressed("editor_tool_unlink_taps")) { RunEditorEditTool(4); return true; }
        if (KeybindSettings.IsPressed("editor_tool_unlink_all_taps")) { RunEditorEditTool(5); return true; }
        if (KeybindSettings.IsPressed("editor_tool_mirror_bank")) { RunEditorEditTool(6); return true; }
        if (KeybindSettings.IsPressed("editor_tool_mirror_x")) { RunEditorEditTool(7); return true; }
        if (KeybindSettings.IsPressed("editor_tool_auto_merge")) { RunEditorEditTool(8); return true; }
        return false;
    }

    private void HandleEditorActionClick(int actionId)
    {
        // Keep playback controls from racing an active seek gesture. Scrubbing
        // remembers its prior play state and restores it only after release.
        if (actionId == EditorActionPlay && (_editorTimelineScrubbing || _editorWheelScrollPauseActive || _editorSeekPauseActive))
        {
            _statusMessage = _editorTimelineScrubbing
                ? "Release the timeline scrubber before changing playback."
                : _editorWheelScrollPauseActive
                    ? "Playback is paused while the timeline is scrolling."
                    : "Release the seek key before changing playback.";
            return;
        }

        if (_editorSuperFlickPlacementPending && actionId != EditorActionPlay)
            CancelEditorSuperFlickPlacement("SuperFlick placement cancelled because a toolbar action was selected.");

        switch (actionId)
        {
            case EditorActionPlay:
                _playing = !_playing;
                _statusMessage = _playing ? "Preview playing." : "Preview paused.";
                break;
            case EditorActionEditorPreview:
                SetEditorPreviewMode(!_editorPreviewMode);
                break;
            case EditorActionGameplayMode:
                ToggleGameplayMode();
                break;
            case EditorActionDelete:
                if (_editorSelectedTempoEvent is not null)
                    DeleteSelectedEditorTempoChanger();
                else
                    DeleteSelectedEditorNote();
                break;
            case EditorActionUndo: UndoEditorChange(); break;
            case EditorActionRedo: RedoEditorChange(); break;
            case EditorActionQuickSave: QuickSaveRequested?.Invoke(); break;
            case EditorActionCopy: CopySelectedEditorNotes(); break;
            case EditorActionCut: CutSelectedEditorNotes(); break;
            case EditorActionPaste: PasteEditorNotes(fitToBank: false); break;
            case EditorActionPasteFit: PasteEditorNotes(fitToBank: true); break;
            case EditorActionSelect:
                _editorPlacementEnabled = false;
                _editorConnectorMode = false;
                _editorSelectLineDragging = false;
                _editorSelectLineStartTrack = -1;
                _editorRectSelecting = false;
                _editorRectSelectionArmed = false;
                ClearEditorPendingRectangleUndoSnapshot();
                _editorPanelsMarqueeArmed = false;
                _editorPanelsMarqueeSelecting = false;
                _editorPanelsHoverValid = false;
                _editorPanelsConnectorPreviewValid = false;
                _editorBpmInputFocused = false;
                // Freeze the selection reference when the options open, so applying
                // a filter does not change its own Y threshold after replacing the
                // current multi-selection. Reopening SELECT captures a fresh tick.
                _editorSelectReferenceTickOverride = null;
                _editorSelectReferenceTickOverride = GetEditorSelectionReferenceTick();
                _editorMenuSection = EditorMenuSection.Select;
                _editorSelectReferenceLineTick = GetEditorSelectLineSeedTick();
                _statusMessage = "SELECT: click/drag in the lane to place the cyan range line, choose X/Y filters, then APPLY SELECT.";
                break;
            case EditorActionDeselect: DeselectEditorSelection(); break;
            case EditorActionMeta:
                CancelEditorMetaField();
                _editorMetaPanelOpen = !_editorMetaPanelOpen;
                if (_editorMetaPanelOpen)
                    _editorMetaScrollOffset = 0;
                break;
            case EditorActionSnap:
                _editorMenuSection = _editorMenuSection == EditorMenuSection.Snap
                    ? EditorMenuSection.None
                    : EditorMenuSection.Snap;
                _editorBpmInputFocused = false;
                _editorBpmInputReplaceOnNextKey = false;
                _editorBpmPlacementPending = false;
                _editorStutterInputField = EditorStutterInputField.None;
                _editorStutterInputReplaceOnNextKey = false;
                _statusMessage = _editorMenuSection == EditorMenuSection.Snap
                    ? $"Beat snap: 1/{_editorSnapDivisor}. Choose a snap division above the SNAPS button."
                    : $"Beat snap: 1/{_editorSnapDivisor}.";
                break;
            case EditorActionMetronome:
                _editorMenuSection = _editorMenuSection == EditorMenuSection.Metronome
                    ? EditorMenuSection.None
                    : EditorMenuSection.Metronome;
                _editorBpmInputFocused = false;
                _editorBpmInputReplaceOnNextKey = false;
                _editorBpmPlacementPending = false;
                _editorStutterInputField = EditorStutterInputField.None;
                _editorStutterInputReplaceOnNextKey = false;
                _editorMetronomeLastGridIndex = null;
                _statusMessage = _editorMenuSection == EditorMenuSection.Metronome
                    ? $"Metronome: {GetEditorMetronomeLabel(_editorMetronomeDivisor)}. Choose a subdivision above METRONOME."
                    : $"Metronome: {GetEditorMetronomeLabel(_editorMetronomeDivisor)}.";
                break;
            case EditorActionViewMode: ToggleTopDownView(); break;
            case EditorActionExpand:
                if (_editorPanelsViewEnabled)
                    _statusMessage = "Collapsed mode is locked while Panels view is active.";
                else
                    ToggleCompactMode();
                break;
            case EditorActionPanels: ToggleEditorPanelsView(); break;
            case EditorActionBeatLines:
                _beatLines = !_beatLines;
                _statusMessage = _beatLines ? "Grids and beat numbers ON." : "Grids and beat numbers OFF.";
                break;
            case EditorActionAttributes:
                _editorShowAttributes = !_editorShowAttributes;
                _statusMessage = _editorShowAttributes ? "Note attributes ON." : "Note attributes OFF.";
                break;
            case EditorActionTempoLines:
                _tempoLines = !_tempoLines;
                _statusMessage = _tempoLines ? "Tempo lines and BPM numbers ON." : "Tempo lines and BPM numbers OFF.";
                break;
            case EditorActionConstantScroll:
                _constantScroll = !_constantScroll;
                SyncEditorConstantScrollMode();
                _statusMessage = _constantScroll
                    ? "Constant scroll ON: notes keep a steady screen speed; chart timing stays synced."
                    : "Constant scroll OFF: scrolling follows tempo changes.";
                break;
            case EditorActionSettings:
                EditorSettingsRequested?.Invoke();
                break;
        }
    }

    private void HandleEditorMenuCategoryClick(int categoryIndex)
    {
        var target = categoryIndex switch
        {
            0 => EditorMenuSection.Notes,
            1 => EditorMenuSection.Tools,
            2 => EditorMenuSection.Bpm,
            3 => EditorMenuSection.NoteGroups,
            _ => EditorMenuSection.Select
        };
        var current = _editorMenuSection;
        var currentRoot = GetEditorMenuRootSection();
        if (target != EditorMenuSection.NoteGroups)
            _editorNoteGroupPlacementArmed = false;

        // Commit any active text field before moving to a different part of the
        // toolbar. Invalid input keeps the current panel open for correction.
        if (current == EditorMenuSection.Duration && _editorDurationInputFocused)
        {
            if (!TryCommitEditorDurationInput())
                return;
            _editorDurationInputFocused = false;
            _editorDurationInputReplaceOnNextKey = false;
            _editorDurationInputText = string.Empty;
        }
        if (current == EditorMenuSection.Stutter && _editorStutterInputField != EditorStutterInputField.None)
        {
            if (!TryCommitEditorStutterInput())
                return;
            _editorStutterInputField = EditorStutterInputField.None;
            _editorStutterInputReplaceOnNextKey = false;
            _editorStutterInputText = string.Empty;
        }

        // Tapping an active category closes its root panel. If a nested panel is
        // open (DURATION, SUPERFLICK, or TEMPO GENERATOR), tapping its category
        // returns to that category instead of closing everything.
        if (currentRoot == target && current == target)
        {
            _editorMenuSection = EditorMenuSection.None;
            _editorSelectLineDragging = false;
            _editorSelectLineStartTrack = -1;
            if (current == EditorMenuSection.Select)
            {
                _editorSelectReferenceTickOverride = null;
                _editorSelectReferenceLineTick = null;
            }
            _editorSelectedTempoEvent = null;
            ClearEditorMenuTransientInputState();
            return;
        }

        if (current == EditorMenuSection.SuperFlick && _editorSuperFlickPlacementPending)
            CancelEditorSuperFlickPlacement("SuperFlick placement cancelled because another toolbar category was selected.");
        if (current == EditorMenuSection.Stutter)
        {
            _editorStutterStartPlacementPending = false;
            _editorStutterEndPlacementPending = false;
        }
        if (current == EditorMenuSection.Select || target == EditorMenuSection.Select)
            _editorSelectReferenceTickOverride = null;
        // Capture the selection threshold before clearing a selected tempo event
        // or switching menu state; SELECT should honor that event's tick too.
        int? selectReferenceTick = target == EditorMenuSection.Select
            ? GetEditorSelectionReferenceTick()
            : null;

        if (target != EditorMenuSection.Select)
        {
            _editorSelectReferenceLineTick = null;
            _editorSelectLineDragging = false;
            _editorSelectLineStartTrack = -1;
        }
        _editorMenuSection = target;
        ClearEditorMenuTransientInputState();
        if (target == EditorMenuSection.Select)
        {
            _editorSelectLineDragging = false;
            _editorSelectLineStartTrack = -1;
            _editorRectSelecting = false;
            _editorRectSelectionArmed = false;
            ClearEditorPendingRectangleUndoSnapshot();
            _editorPanelsMarqueeArmed = false;
            _editorPanelsMarqueeSelecting = false;
            _editorPanelsHoverValid = false;
            _editorPanelsConnectorPreviewValid = false;
        }

        if (target == EditorMenuSection.Bpm)
        {
            _editorPlacementEnabled = false;
            _editorConnectorMode = false;
            _editorSelectedTempoEvent = null;
            SetEditorBpmInputFromCurrentTick();
            _statusMessage = "BPM tool: hover a beat line to preview it; enter a value and tap to place a tempo change.";
        }
        else if (target == EditorMenuSection.Notes)
        {
            _editorSelectedTempoEvent = null;
            _statusMessage = "Choose a note tool.";
        }
        else if (target == EditorMenuSection.NoteGroups)
        {
            _editorPlacementEnabled = false;
            _editorConnectorMode = false;
            _editorSelectedTempoEvent = null;
            _editorSelectReferenceTickOverride = null;
            _statusMessage = "NOTE GROUPS: select a library entry, then click an empty lane to place an instance; manage groups here.";
        }
        else if (target == EditorMenuSection.Select)
        {
            _editorPlacementEnabled = false;
            _editorConnectorMode = false;
            _editorSelectedTempoEvent = null;
            _editorBpmInputFocused = false;
            _editorSelectReferenceTickOverride = selectReferenceTick;
            _editorSelectReferenceLineTick = GetEditorSelectLineSeedTick();
            _statusMessage = "SELECT: click/drag in the lane to place the cyan range line, choose X/Y filters, then APPLY SELECT.";
        }
        else
        {
            _editorPlacementEnabled = false;
            _editorConnectorMode = false;
            _editorSelectedTempoEvent = null;
            _statusMessage = "Choose a chart edit tool.";
        }
    }

    private void ClearEditorMenuTransientInputState()
    {
        _editorBpmInputFocused = false;
        _editorBpmInputReplaceOnNextKey = false;
        _editorBpmInputPreserveOnRefocus = false;
        _editorBpmPlacementPending = false;
        _editorStutterInputField = EditorStutterInputField.None;
        _editorStutterInputReplaceOnNextKey = false;
        _editorStutterInputText = string.Empty;
        _editorDurationInputFocused = false;
        _editorDurationInputReplaceOnNextKey = false;
        _editorDurationInputText = string.Empty;
        EndEditorTextFieldMouseSelection();
    }

    private void DismissEditorNumericInputIfClickedOutside(Microsoft.Xna.Framework.Point pixel, int virtualWidth, int virtualHeight)
    {
        var activeBounds = Rectangle.Empty;
        if (_editorMenuSection == EditorMenuSection.Bpm && _editorBpmInputFocused)
        {
            activeBounds = GetEditorMenuChildBounds(0, virtualWidth, virtualHeight);
        }
        else if (_editorMenuSection == EditorMenuSection.Duration && _editorDurationInputFocused)
        {
            activeBounds = GetEditorMenuChildBounds(0, virtualWidth, virtualHeight);
        }
        else if (_editorMenuSection == EditorMenuSection.Stutter &&
                 _editorStutterInputField != EditorStutterInputField.None)
        {
            for (var childIndex = 0; childIndex < GetEditorMenuChildCount(); childIndex++)
            {
                if (!TryGetEditorStutterInputFieldForChild(childIndex, out var field) ||
                    field != _editorStutterInputField)
                    continue;
                activeBounds = GetEditorMenuChildBounds(childIndex, virtualWidth, virtualHeight);
                break;
            }
        }

        if (activeBounds.IsEmpty || activeBounds.Contains(pixel))
            return;

        // Clicking outside confirms a valid value (or discards an invalid draft),
        // then lets the original click continue to the toolbar or chart below it.
        if (_editorMenuSection == EditorMenuSection.Bpm && _editorBpmInputFocused)
        {
            if (TryParseEditorBpmInput(out var bpm))
            {
                if (_editorSelectedTempoEvent is not null)
                {
                    EditSelectedEditorTempoChanger(bpm);
                    _editorBpmPlacementPending = false;
                }
                else
                {
                    // Confirming a fresh BPM value leaves it ready for the next lane click.
                    _editorBpmPlacementPending = true;
                }
                _editorBpmInputText = bpm.ToString("0.##", CultureInfo.InvariantCulture);
                _statusMessage = _editorSelectedTempoEvent is not null
                    ? $"Tempo change at tick {_editorSelectedTempoEvent.Tick} set to {bpm:0.##} BPM."
                    : $"BPM {bpm:0.##} ready. Tap the lane to place a tempo change.";
                _editorBpmInputPreserveOnRefocus = true;
            }
            else
            {
                _editorBpmInputText = _editorSelectedTempoEvent?.Value.ToString("0.##", CultureInfo.InvariantCulture)
                    ?? (_tempo?.BpmAt(_tempo.SecondsToTick(_playSeconds)) ?? 120.0)
                        .ToString("0.##", CultureInfo.InvariantCulture);
                _editorBpmInputPreserveOnRefocus = false;
                _statusMessage = "Invalid BPM input was dismissed; the previous value was kept.";
            }
            _editorBpmInputCaretIndex = _editorBpmInputText.Length;
            _editorBpmSelectionAnchor = _editorBpmInputCaretIndex;
            _editorBpmInputFocused = false;
            _editorBpmInputReplaceOnNextKey = false;
            EndEditorTextFieldMouseSelection();
        }
        else if (_editorMenuSection == EditorMenuSection.Duration && _editorDurationInputFocused)
        {
            TryCommitEditorDurationInput();
            _editorDurationInputFocused = false;
            _editorDurationInputReplaceOnNextKey = false;
            _editorDurationInputText = string.Empty;
            EndEditorTextFieldMouseSelection();
        }
        else if (_editorMenuSection == EditorMenuSection.Stutter &&
                 _editorStutterInputField != EditorStutterInputField.None)
        {
            TryCommitEditorStutterInput();
            _editorStutterInputField = EditorStutterInputField.None;
            _editorStutterInputReplaceOnNextKey = false;
            _editorStutterInputText = string.Empty;
            EndEditorTextFieldMouseSelection();
        }
    }

    private bool TryHandleChartEditorToolbarClick(Vector2 point, int virtualWidth, int virtualHeight)
    {
        var pixel = new Microsoft.Xna.Framework.Point((int)MathF.Round(point.X), (int)MathF.Round(point.Y));

        if (_editorMetaEditingField is not null)
        {
            var rows = GetEditorMetaRows(_tempo?.SecondsToTick(_playSeconds) ?? 0.0);
            var activeIndex = -1;
            for (var i = 0; i < rows.Count; i++)
            {
                if (string.Equals(GetEditorMetaFieldKey(rows[i].Label), _editorMetaEditingField, StringComparison.OrdinalIgnoreCase))
                {
                    activeIndex = i;
                    break;
                }
            }
            var activeBounds = activeIndex >= 0
                ? GetEditorMetaFieldBounds(activeIndex, virtualWidth, virtualHeight)
                : Rectangle.Empty;
            if (!activeBounds.Contains(pixel))
                CommitEditorMetaFieldOnBlur();
        }

        DismissEditorNumericInputIfClickedOutside(pixel, virtualWidth, virtualHeight);

        // The meta overlay is drawn last within the toolbar, so it gets click
        // priority over the Problems panel and other controls where it overlaps.
        if (TryHandleEditorMetaPanelClick(pixel, virtualWidth, virtualHeight))
            return true;

        // The SNAPS popup is drawn by the toolbar after the Problems panel, so
        // its hit targets must also take priority over the panel where they overlap.
        if (_editorMenuSection == EditorMenuSection.Snap)
        {
            var snapPopup = GetEditorSnapPopupBounds(virtualWidth, virtualHeight);
            if (snapPopup.Contains(pixel))
            {
                for (var snapIndex = 0; snapIndex < EditorSnapDivisors.Length; snapIndex++)
                {
                    if (!GetEditorSnapMenuChildBounds(snapIndex, virtualWidth, virtualHeight).Contains(pixel))
                        continue;
                    _editorSnapDivisor = EditorSnapDivisors[snapIndex];
                    _editorMenuSection = EditorMenuSection.None;
                    _editorHoverCacheValid = false;
                    _statusMessage = $"Beat snap: 1/{EditorSnapDivisors[snapIndex]}.";
                    return true;
                }
                return true;
            }
        }
        if (_editorMenuSection == EditorMenuSection.Metronome)
        {
            var popup = GetEditorMetronomePopupBounds(virtualWidth, virtualHeight);
            if (popup.Contains(pixel))
            {
                for (var index = 0; index < EditorMetronomeDivisors.Length; index++)
                {
                    if (!GetEditorMetronomeMenuChildBounds(index, virtualWidth, virtualHeight).Contains(pixel))
                        continue;
                    SetEditorMetronomeDivision(EditorMetronomeDivisors[index]);
                    _editorMenuSection = EditorMenuSection.None;
                    _editorHoverCacheValid = false;
                    return true;
                }
                return true;
            }
        }

        // Draw order is: bottom controls, top strip, then the meta overlay.
        // Hit-testing follows the reverse order so overlapping controls behave
        // the same way they look, especially on short/portrait windows.
        if (_editorMenuSection == EditorMenuSection.Snap)
        {
            var snapActionIndex = GetEditorSnapActionIndex();
            var snapButton = snapActionIndex >= 0
                ? GetEditorBottomLeftButtonBounds(snapActionIndex, virtualWidth, virtualHeight)
                : Rectangle.Empty;
            if (snapButton.Contains(pixel))
            {
                _editorMenuSection = EditorMenuSection.None;
                return true;
            }

            var snapPopup = GetEditorSnapPopupBounds(virtualWidth, virtualHeight);
            if (snapPopup.Contains(pixel))
            {
                for (var snapIndex = 0; snapIndex < EditorSnapDivisors.Length; snapIndex++)
                {
                    if (!GetEditorSnapMenuChildBounds(snapIndex, virtualWidth, virtualHeight).Contains(pixel))
                        continue;
                    _editorSnapDivisor = EditorSnapDivisors[snapIndex];
                    _editorMenuSection = EditorMenuSection.None;
                    _editorHoverCacheValid = false;
                    _statusMessage = $"Beat snap: 1/{_editorSnapDivisor}.";
                    return true;
                }
                return true;
            }

            // Clicking another control or the chart closes the popup, then lets
            // that same click continue through normal editor hit-testing.
            if (_editorMenuSection == EditorMenuSection.Select)
                _editorSelectReferenceLineTick = null;
            _editorMenuSection = EditorMenuSection.None;
        }

        if (_editorMenuSection == EditorMenuSection.Metronome)
        {
            var actionIndex = GetEditorMetronomeActionIndex();
            var button = actionIndex >= 0
                ? GetEditorBottomLeftButtonBounds(actionIndex, virtualWidth, virtualHeight)
                : Rectangle.Empty;
            if (button.Contains(pixel))
            {
                _editorMenuSection = EditorMenuSection.None;
                return true;
            }

            var popup = GetEditorMetronomePopupBounds(virtualWidth, virtualHeight);
            if (popup.Contains(pixel))
            {
                for (var index = 0; index < EditorMetronomeDivisors.Length; index++)
                {
                    if (!GetEditorMetronomeMenuChildBounds(index, virtualWidth, virtualHeight).Contains(pixel))
                        continue;
                    SetEditorMetronomeDivision(EditorMetronomeDivisors[index]);
                    _editorMenuSection = EditorMenuSection.None;
                    _editorHoverCacheValid = false;
                    return true;
                }
                return true;
            }

            _editorMenuSection = EditorMenuSection.None;
        }

        var speedPanel = GetEditorScrollSpeedPanelBounds(virtualWidth, virtualHeight);
        if (speedPanel.Contains(pixel))
        {
            if (GetEditorScrollSpeedButtonBounds(false, virtualWidth, virtualHeight).Contains(pixel))
            {
                AdjustPauseScrollSpeed(-0.10);
                _statusMessage = $"Editor scroll speed: {_projection.ScrollSpeedScale:0.00}x";
            }
            else if (GetEditorScrollSpeedButtonBounds(true, virtualWidth, virtualHeight).Contains(pixel))
            {
                AdjustPauseScrollSpeed(0.10);
                _statusMessage = $"Editor scroll speed: {_projection.ScrollSpeedScale:0.00}x";
            }
            return true;
        }

        var leftStrip = GetEditorBottomLeftStripBounds(virtualWidth, virtualHeight);
        if (leftStrip.Contains(pixel))
        {
            for (var i = 0; i < EditorBottomLeftActionIds.Length; i++)
            {
                if (GetEditorBottomLeftButtonBounds(i, virtualWidth, virtualHeight).Contains(pixel))
                {
                    HandleEditorActionClick(EditorBottomLeftActionIds[i]);
                    return true;
                }
            }
            return true;
        }

        var actionPanel = GetEditorBottomActionPanelBounds(virtualWidth, virtualHeight);
        var rootStrip = GetEditorTopStripPanelBounds(virtualWidth, virtualHeight);
        var submenuPanel = GetEditorMenuPanelBounds(virtualWidth, virtualHeight);
        // The submenu and fixed tab strip are rendered above this action stack,
        // so hit-testing gives both of them priority wherever they overlap.
        if (actionPanel.Contains(pixel) && !rootStrip.Contains(pixel) && !submenuPanel.Contains(pixel))
        {
            var actionIds = GetVisibleEditorActionIds();
            for (var i = 0; i < actionIds.Count; i++)
            {
                if (!GetEditorActionButtonBounds(i, virtualWidth, virtualHeight, actionIds).Contains(pixel))
                    continue;
                if (IsEditorActionVisible(actionIds[i]))
                    HandleEditorActionClick(actionIds[i]);
                return true;
            }
            return true;
        }

        var topPanel = GetEditorTopStripPanelBounds(virtualWidth, virtualHeight);
        var submenuBounds = GetEditorMenuPanelBounds(virtualWidth, virtualHeight);
        if (_editorPanelsViewEnabled && (_editorMenuSection is EditorMenuSection.Select or EditorMenuSection.Duration) && submenuBounds.Contains(pixel))
        {
            if (GetEditorMenuCloseButtonBounds(virtualWidth, virtualHeight).Contains(pixel))
            {
                if (_editorMenuSection == EditorMenuSection.Duration && _editorDurationInputFocused)
                {
                    if (!TryCommitEditorDurationInput())
                        return true;
                    _editorDurationInputFocused = false;
                    _editorDurationInputReplaceOnNextKey = false;
                    _editorDurationInputText = string.Empty;
                }
                _editorMenuSection = EditorMenuSection.None;
                _editorSelectLineDragging = false;
                _editorSelectLineStartTrack = -1;
                _editorSelectReferenceTickOverride = null;
                _editorSelectReferenceLineTick = null;
                return true;
            }
            for (var i = 0; i < GetEditorMenuChildCount(); i++)
            {
                var childBounds = GetEditorMenuChildBounds(i, virtualWidth, virtualHeight);
                if (!childBounds.Contains(pixel))
                    continue;
                if (_editorMenuSection == EditorMenuSection.Duration)
                {
                    HandleEditorDurationMenuChildClick(i, childBounds, pixel);
                }
                else
                {
                    HandleEditorSelectMenuChildClick(i);
                }
                return true;
            }
            return true;
        }

        if (topPanel.Contains(pixel) || (!_editorPanelsViewEnabled && submenuBounds.Contains(pixel)))
        {
            if (_editorPanelsViewEnabled)
            {
                if (GetEditorPanelsCloseButtonBounds(virtualWidth, virtualHeight).Contains(pixel))
                {
                    ToggleEditorPanelsView();
                    return true;
                }
                if (GetEditorPanelsDurationButtonBounds(virtualWidth, virtualHeight).Contains(pixel))
                {
                    OpenEditorDurationTool();
                    return true;
                }
                if (GetEditorPanelsSelectButtonBounds(virtualWidth, virtualHeight).Contains(pixel))
                {
                    // SELECT now occupies category slot 4; NOTE GROUPS is slot 3.
                    // Use the same root-category flow as the SELECT category so the
                    // selected grid range's first tick is captured before filters.
                    HandleEditorMenuCategoryClick(4);
                    return true;
                }
                if (GetEditorPanelsReplaceButtonBounds(virtualWidth, virtualHeight).Contains(pixel))
                {
                    ReplaceSelectedEditorNotes();
                    return true;
                }
                if (GetEditorPanelsConnectorButtonBounds(virtualWidth, virtualHeight).Contains(pixel))
                {
                    ToggleEditorPanelsConnectorTool();
                    return true;
                }
                for (var i = 0; i < 8; i++)
                {
                    if (!GetEditorPanelsAttributeButtonBounds(i, virtualWidth, virtualHeight).Contains(pixel))
                        continue;
                    SelectEditorPanelAttribute(101 + i);
                    return true;
                }
                return true;
            }

            for (var tabIndex = 0; tabIndex < 5; tabIndex++)
            {
                if (!GetEditorMenuParentBounds(tabIndex, virtualWidth, virtualHeight).Contains(pixel))
                    continue;
                HandleEditorMenuCategoryClick(tabIndex);
                return true;
            }

            if (_editorMenuSection is EditorMenuSection.None or EditorMenuSection.Snap or EditorMenuSection.Metronome)
                return true;

            if (GetEditorMenuCloseButtonBounds(virtualWidth, virtualHeight).Contains(pixel))
            {
                if (_editorMenuSection == EditorMenuSection.Select)
                    _editorSelectReferenceLineTick = null;
                if (_editorMenuSection == EditorMenuSection.Duration && _editorDurationInputFocused)
                {
                    if (!TryCommitEditorDurationInput())
                        return true;
                    _editorDurationInputFocused = false;
                    _editorDurationInputReplaceOnNextKey = false;
                    _editorDurationInputText = string.Empty;
                }
                if (_editorMenuSection == EditorMenuSection.Stutter &&
                    (_editorStutterStartPlacementPending || _editorStutterEndPlacementPending))
                {
                    var wasStart = _editorStutterStartPlacementPending;
                    _editorStutterStartPlacementPending = false;
                    _editorStutterEndPlacementPending = false;
                    _editorStutterInputField = EditorStutterInputField.None;
                    _editorStutterInputReplaceOnNextKey = false;
                    _editorStutterInputText = string.Empty;
                    _editorSelectedTempoEvent = null;
                    _editorMenuSection = EditorMenuSection.Bpm;
                    _statusMessage = wasStart ? "Start placement cancelled." : "End placement cancelled.";
                    return true;
                }
                if (_editorMenuSection == EditorMenuSection.Stutter &&
                    _editorStutterInputField != EditorStutterInputField.None)
                {
                    if (!TryCommitEditorStutterInput())
                        return true;
                    _editorStutterInputField = EditorStutterInputField.None;
                    _editorStutterInputReplaceOnNextKey = false;
                    _editorStutterInputText = string.Empty;
                }
                // SuperFlick is a sub-menu of Notes; its header returns to the
                // note-tool list rather than closing the entire toolbar.
                if (_editorMenuSection == EditorMenuSection.SuperFlick && _editorSuperFlickPlacementPending)
                {
                    CancelEditorSuperFlickPlacement("SuperFlick placement cancelled because no target bank was selected.");
                    _editorMenuSection = EditorMenuSection.Notes;
                }
                else if (_editorMenuSection == EditorMenuSection.Stutter)
                {
                    _editorStutterStartPlacementPending = false;
                    _editorStutterEndPlacementPending = false;
                    _editorMenuSection = EditorMenuSection.Bpm;
                }
                else if (_editorMenuSection == EditorMenuSection.Duration)
                {
                    _editorMenuSection = _editorPanelsViewEnabled
                        ? EditorMenuSection.None
                        : EditorMenuSection.Tools;
                }
                else
                {
                    if (_editorMenuSection == EditorMenuSection.Select)
                        _editorSelectReferenceTickOverride = null;
                    _editorMenuSection = _editorMenuSection == EditorMenuSection.SuperFlick
                        ? EditorMenuSection.Notes
                        : EditorMenuSection.None;
                }
                _editorBpmInputFocused = false;
                _editorBpmInputReplaceOnNextKey = false;
                _editorBpmPlacementPending = false;
                _editorStutterInputField = EditorStutterInputField.None;
                _editorStutterInputReplaceOnNextKey = false;
                _editorDurationInputFocused = false;
                _editorDurationInputReplaceOnNextKey = false;
                _editorSelectedTempoEvent = null;
                return true;
            }

            // The title bar is informational; only the dedicated X button closes it.
            if (GetEditorMenuHeaderBounds(virtualWidth, virtualHeight).Contains(pixel))
                return true;

            if (_editorMenuSection == EditorMenuSection.NoteGroups)
            {
                if (TryHandleEditorNoteGroupsMenuClick(pixel, virtualWidth, virtualHeight))
                    return true;
                return submenuBounds.Contains(pixel);
            }

            for (var childIndex = 0; childIndex < GetEditorMenuChildCount(); childIndex++)
            {
                var childBounds = GetEditorMenuChildBounds(childIndex, virtualWidth, virtualHeight);
                if (!childBounds.Contains(pixel))
                    continue;
                if (TryHandleEditorNumericSpinnerClick(childIndex, childBounds, pixel, _projection.ScalePixels(1f)))
                    return true;

                switch (_editorMenuSection)
                {
                    case EditorMenuSection.Notes:
                        if (childIndex == 5)
                        {
                            ArmEditorSuperFlickTool();
                            // Keep Notes open; the nested target-bank submenu appears
                            // only after the source point is clicked.
                        }
                        else if (childIndex == 6)
                        {
                            ReplaceSelectedEditorNotes();
                        }
                        else
                        {
                            SelectEditorTool(childIndex);
                        }
                        break;
                    case EditorMenuSection.Tools:
                        RunEditorEditTool(childIndex);
                        // Keep the submenu open for repeated edits until its header is closed.
                        break;
                    case EditorMenuSection.Duration:
                        HandleEditorDurationMenuChildClick(childIndex, childBounds, pixel);
                        break;
                    case EditorMenuSection.SuperFlick:
                        SelectEditorSuperFlickTarget(childIndex);
                        break;
                    case EditorMenuSection.Snap:
                        _editorSnapDivisor = EditorSnapDivisors[childIndex];
                        _editorHoverCacheValid = false;
                        _statusMessage = $"Beat snap: 1/{_editorSnapDivisor}.";
                        break;
                    case EditorMenuSection.Metronome:
                        SetEditorMetronomeDivision(EditorMetronomeDivisors[childIndex]);
                        _editorMenuSection = EditorMenuSection.None;
                        break;
                    case EditorMenuSection.Select:
                        HandleEditorSelectMenuChildClick(childIndex);
                        break;
                    case EditorMenuSection.Bpm:
                        if (childIndex == 0)
                        {
                            if (_editorBpmInputFocused)
                            {
                                _editorBpmInputCaretIndex = GetEditorNumericTextCaretIndex(childBounds, _editorBpmInputText, pixel, _projection.ScalePixels(1f), "bpm");
                                _editorBpmSelectionAnchor = _editorBpmInputCaretIndex;
                                _editorBpmInputReplaceOnNextKey = false;
                                BeginEditorTextFieldMouseSelection("bpm", childBounds, _editorBpmInputCaretIndex, ref _editorBpmSelectionAnchor);
                            }
                            else
                            {
                                var preserveDraft = _editorBpmInputPreserveOnRefocus;
                                if (_editorSelectedTempoEvent is null && !_editorBpmPlacementPending && !preserveDraft)
                                    SetEditorBpmInputFromCurrentTick();
                                _editorBpmInputPreserveOnRefocus = false;
                                _editorBpmInputFocused = true;
                                _editorBpmInputCaretIndex = GetEditorNumericTextCaretIndex(
                                    childBounds, _editorBpmInputText, pixel, _projection.ScalePixels(1f), "bpm");
                                _editorBpmSelectionAnchor = _editorBpmInputCaretIndex;
                                _editorBpmInputReplaceOnNextKey = false;
                                BeginEditorTextFieldMouseSelection("bpm", childBounds, _editorBpmInputCaretIndex, ref _editorBpmSelectionAnchor);
                            }
                        }
                        else if (childIndex == 1)
                        {
                            _editorAutoAdjustBpm = !_editorAutoAdjustBpm;
                            _statusMessage = _editorAutoAdjustBpm
                                ? "BPM auto adjust ON: later events keep their playback times when tempo changes."
                                : "BPM auto adjust OFF: later event ticks stay where they are.";
                        }
                        else if (childIndex == 2)
                        {
                            FlattenEditorBpmChanges();
                        }
                        else if (childIndex == 3)
                        {
                            OpenEditorTempoGenerator();
                        }
                        break;
                    case EditorMenuSection.Stutter:
                    {
                        var clickedInputField = TryGetEditorStutterInputFieldForChild(childIndex, out var clickedField)
                            ? clickedField
                            : EditorStutterInputField.None;
                        if (_editorStutterInputField != EditorStutterInputField.None && clickedInputField != _editorStutterInputField)
                        {
                            if (!TryCommitEditorStutterInput())
                                return true;
                            _editorStutterInputField = EditorStutterInputField.None;
                            _editorStutterInputReplaceOnNextKey = false;
                            _editorStutterInputText = string.Empty;
                        }
                        switch (childIndex)
                        {
                            case 0:
                                _editorStutterEndPlacementPending = false;
                                _editorStutterStartPlacementPending = !_editorStutterStartPlacementPending;
                                _editorPlacementEnabled = false;
                                _editorConnectorMode = false;
                                _statusMessage = _editorStutterStartPlacementPending
                                    ? "Start placement armed. Tap a visible lane to place it; press Escape or close the submenu to cancel."
                                    : "Start placement cancelled.";
                                break;
                            case 1:
                                if (_editorStutterStartTick < 0)
                                {
                                    _statusMessage = "Set the generator start before setting its end.";
                                    break;
                                }
                                _editorStutterStartPlacementPending = false;
                                _editorStutterEndPlacementPending = !_editorStutterEndPlacementPending;
                                _editorPlacementEnabled = false;
                                _editorConnectorMode = false;
                                _statusMessage = _editorStutterEndPlacementPending
                                    ? "End placement armed. Tap a later tick to place it; press Escape or close the submenu to cancel."
                                    : "End placement cancelled.";
                                break;
                            case 2:
                            {
                                var patternButtons = GetEditorStutterPatternButtonBounds(
                                    GetEditorMenuChildBounds(childIndex, virtualWidth, virtualHeight));
                                if (patternButtons[0].Contains(pixel))
                                {
                                    _editorStutterAlternate = false;
                                    _editorStutterRamp = false;
                                    _statusMessage = "Pattern set to Pulse: fast BPM pulses over slow BPM gaps; no separate Slow Time is used.";
                                }
                                else if (patternButtons[1].Contains(pixel))
                                {
                                    _editorStutterAlternate = true;
                                    _editorStutterRamp = false;
                                    _statusMessage = "Pattern set to Alternate: Fast and Slow BPM alternate for beat-based phase lengths.";
                                }
                                else if (patternButtons[2].Contains(pixel))
                                {
                                    _editorStutterAlternate = false;
                                    _editorStutterRamp = true;
                                    _statusMessage = "Pattern set to Ramp: Start BPM changes linearly to Target BPM.";
                                }
                                break;
                            }
                            case 3:
                                FocusOrPlaceEditorStutterCaret(_editorStutterRamp ? EditorStutterInputField.RampStartBpm : EditorStutterInputField.SlowBpm, childBounds, pixel, _projection.ScalePixels(1f));
                                break;
                            case 4:
                                FocusOrPlaceEditorStutterCaret(_editorStutterRamp
                                    ? EditorStutterInputField.RampTargetBpm
                                    : _editorStutterAlternate
                                        ? EditorStutterInputField.SlowTimeBeats
                                        : EditorStutterInputField.PulseIntervalBeats, childBounds, pixel, _projection.ScalePixels(1f));
                                break;
                            case 5:
                                if (_editorStutterRamp)
                                {
                                    _editorRampResolutionIndex = (_editorRampResolutionIndex + 1) % EditorRampResolutionBeats.Length;
                                    _statusMessage = $"Ramp sample resolution set to {EditorRampResolutionLabels[_editorRampResolutionIndex]}. Tempo lines stay on this grid; BPM changes linearly.";
                                }
                                else
                                    FocusOrPlaceEditorStutterCaret(EditorStutterInputField.FastBpm, childBounds, pixel, _projection.ScalePixels(1f));
                                break;
                            case 6:
                                if (_editorStutterRamp)
                                {
                                    _editorAutoAdjustBpm = !_editorAutoAdjustBpm;
                                    _statusMessage = _editorAutoAdjustBpm
                                        ? "BPM auto adjust ON: ramp end and later events are remapped to their nearest snap-grid positions."
                                        : "BPM auto adjust OFF: later event ticks stay where they are.";
                                }
                                else
                                    FocusOrPlaceEditorStutterCaret(EditorStutterInputField.FastTimeBeats, childBounds, pixel, _projection.ScalePixels(1f));
                                break;
                            case 7:
                                if (_editorStutterRamp)
                                    MoveEditorChartAfterRampEnd(1);
                                else
                                {
                                    _editorStutterBalanced = !_editorStutterBalanced;
                                    _statusMessage = _editorStutterBalanced
                                        ? "Balanced mode scales Fast and Slow BPM together to match the starting BPM's average timing."
                                        : "Free mode uses the entered Fast and Slow BPM values without timing compensation.";
                                }
                                break;
                            case 8:
                                if (_editorStutterRamp)
                                    MoveEditorChartAfterRampEnd(-1);
                                else
                                {
                                    _editorAutoAdjustBpm = !_editorAutoAdjustBpm;
                                    _statusMessage = _editorAutoAdjustBpm
                                        ? "BPM auto adjust ON: later events and the generator end keep their playback timestamps."
                                        : "BPM auto adjust OFF: later event ticks stay where they are.";
                                }
                                break;
                            case 9:
                                GenerateEditorStutter();
                                break;
                            case 10:
                                DeleteEditorStutterGroup();
                                break;
                            case 11:
                                UngroupEditorStutterGroup();
                                break;
                        }
                        break;
                    }
                }
                return true;
            }
            if (_editorSuperFlickPlacementPending)
                CancelEditorSuperFlickPlacement("SuperFlick placement cancelled because no target bank was selected.");
            return true;
        }

        // Problems is drawn underneath toolbar controls; only handle it after
        // those controls have had a chance to consume overlapping clicks.
        if (TryHandleEditorProblemsPanelClick(pixel, virtualWidth, virtualHeight))
            return true;

        return false;
    }

    private void ReplaceSelectedEditorNotes()
    {
        if (!_chartEditorMode || _chart is null)
        {
            _statusMessage = "Open a chart in the Chart Editor before replacing notes.";
            return;
        }
        if (_editorPanelsViewEnabled)
        {
            ReplaceSelectedEditorPanelNotes();
            return;
        }
        if (_editorConnectorMode)
        {
            _statusMessage = "Choose TAP, FLICK, or VIBRATO before replacing. Connectors need two points.";
            return;
        }
        if (_editorSuperFlickTool == EditorSuperFlickTool.Place)
        {
            _statusMessage = "REPLACE SELECTION cannot create SuperFlicks. Choose TAP, a FLICK, or VIBRATO instead.";
            return;
        }
        if (_editorNoteType is not (NoteType.Single or NoteType.Vibrato))
        {
            _statusMessage = "REPLACE SELECTION supports TAP, FLICK, and VIBRATO notes only.";
            return;
        }

        var selected = GetSelectedEditorNotes(expandLinkFamilies: true)
            .Where(ev => ev.Kind == "Note" && ev.RawType == 1 && ev.NoteType != NoteType.End &&
                        IsPlayableEditorTrack(ev.Track))
            .Distinct()
            .ToList();
        if (selected.Count == 0)
        {
            _statusMessage = "Select one or more notes first, choose TAP, FLICK, or VIBRATO, then press REPLACE SELECTION.";
            return;
        }

        var replacementAttr = _editorNoteType == NoteType.Vibrato ? 9 : _editorAttr;
        if (_editorNoteType == NoteType.Single && replacementAttr is not (0 or 10 or 50))
        {
            _statusMessage = "REPLACE SELECTION cannot use SuperFlick attributes. Choose TAP, FLICK, or VIBRATO.";
            return;
        }

        var selectedSet = selected.ToHashSet();
        var destinations = selected.Select(ev => (
                Original: ev,
                Tick: ev.Tick,
                Track: GetEditorAllowedTrackForNoteType(ev.Track, _editorNoteType)))
            .ToList();
        var duplicateTarget = destinations.GroupBy(item => (item.Tick, item.Track))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateTarget is not null)
        {
            _statusMessage = $"Cannot replace selection: multiple selected notes would occupy tick {duplicateTarget.Key.Tick}, track {duplicateTarget.Key.Track}.";
            return;
        }

        foreach (var destination in destinations)
        {
            var conflict = _chart.Events.FirstOrDefault(ev => !selectedSet.Contains(ev) &&
                ev.Kind == "Note" && ev.RawType == 1 && ev.Tick == destination.Tick &&
                ev.Track == destination.Track);
            if (conflict is not null)
            {
                _statusMessage = $"Cannot replace selection: tick {destination.Tick}, track {destination.Track} is already occupied by another note.";
                return;
            }
        }

        var selectedAnchor = _selectedEventKey.HasValue
            ? selected.FirstOrDefault(ev => KeyOf(ev) == _selectedEventKey.Value)
            : null;
        var undoSnapshot = CaptureEditorChartSnapshot();
        _ = DeleteEditorEvents(selectedSet);

        var replacements = new Dictionary<ChartEvent, ChartEvent>();
        foreach (var destination in destinations)
        {
            var track = GetOrCreateEditorTrack(destination.Track);
            var eventIndex = track.Events.Count == 0 ? 0 : track.Events.Max(ev => ev.EventIndex) + 1;
            var convertHoldToVibrato = destination.Original.NoteType == NoteType.SingleLong &&
                                       _editorNoteType == NoteType.Vibrato;
            var convertVibratoToHold = destination.Original.NoteType == NoteType.Vibrato &&
                                       _editorNoteType == NoteType.Single && replacementAttr == 0;
            var preserveHoldDuration = convertHoldToVibrato || convertVibratoToHold;
            var replacementDuration = preserveHoldDuration
                ? Math.Max(6, destination.Original.Duration)
                : Math.Max(6, _editorDuration);
            var replacementType = convertVibratoToHold ? NoteType.SingleLong : _editorNoteType;
            var replacement = new ChartEvent
            {
                Tick = destination.Tick,
                Track = destination.Track,
                Attr = replacementAttr,
                RawType = 1,
                TypeIndex = -1,
                Duration = replacementDuration,
                NoteType = replacementType,
                EventIndex = eventIndex,
                LinePair = -1,
                TwinPairId = destination.Original.TwinPairId,
                LoopReconstructed = destination.Original.LoopReconstructed,
                LoopPhase = destination.Original.LoopPhase,
                Kind = "Note",
                Value = destination.Original.Value,
                Instrument = destination.Original.Instrument,
                Velocity = destination.Original.Velocity,
                Pan = destination.Original.Pan,
                NoteGroupId = destination.Original.NoteGroupId,
                NoteGroupInstanceId = destination.Original.NoteGroupInstanceId,
                NoteGroupMemberId = destination.Original.NoteGroupMemberId
            };
            track.Events.Add(replacement);
            replacements[destination.Original] = replacement;
        }

        CommitEditorUndoSnapshot(undoSnapshot);
        RefreshAfterEditorMutation();
        ClearEditorMultiSelection();
        foreach (var replacement in replacements.Values)
            AddEditorMultiSelectedEvent(replacement);
        var anchor = selectedAnchor is not null && replacements.TryGetValue(selectedAnchor, out var anchoredReplacement)
            ? anchoredReplacement
            : replacements.Values.First();
        _selectedEventKey = KeyOf(anchor);
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorSelectedLinkRoot = null;
        _editorSelectedHoldEnd = false;
        _editorSelectedGridCells.Clear();
        _editorTrack = anchor.Track;
        _statusMessage = $"Replaced {replacements.Count} selected note(s) with {_editorNoteType}{(replacementAttr == 0 ? "" : $" attr {replacementAttr}")}.";
    }

    private void SelectEditorTool(int buttonIndex)
    {
        var wasAlreadyActive = _editorPlacementEnabled && !_editorConnectorMode &&
                               _editorNoteType == (buttonIndex == 4 ? NoteType.Vibrato : NoteType.Single) &&
                               _editorAttr == (buttonIndex switch { 2 => 10, 3 => 50, 4 => 9, _ => 0 }) &&
                               buttonIndex != 1;
        var connectorWasAlreadyActive = buttonIndex == 1 && _editorPlacementEnabled && _editorConnectorMode;
        CancelEditorSuperFlickPlacement(string.Empty, deactivateTool: true);
        var requestedAttr = buttonIndex switch
        {
            2 => 10,
            3 => 50,
            4 => 9, // TSC/ARES encodes vibrato as attribute 9.
            _ => 0
        };
        var requestedType = buttonIndex == 4 ? NoteType.Vibrato : NoteType.Single;
        var alreadyActive = wasAlreadyActive;
        if (buttonIndex == 1)
        {
            if (connectorWasAlreadyActive)
            {
                _editorPlacementEnabled = false;
                _editorConnectorMode = false;
                _statusMessage = "Connector tool off. Tap a lane to select a note or tick.";
            }
            else
            {
                _editorPlacementEnabled = true;
                _editorConnectorMode = true;
                _editorNoteType = NoteType.Single;
                _editorAttr = 0;
                _editorDuration = 6;
                _statusMessage = "Connector: click a source, then hold Connect and click a target.";
            }
        }
        else if (alreadyActive)
        {
            _editorPlacementEnabled = false;
            _editorConnectorMode = false;
            _statusMessage = "Note tool off. Tap a lane to select a tick or note.";
        }
        else
        {
            _editorPlacementEnabled = true;
            _editorConnectorMode = false;
            _editorAttr = requestedAttr;
            _editorNoteType = requestedType;
            // The default vibrato length is one 48-tick beat.
            _editorDuration = requestedType == NoteType.Vibrato ? 48 : 6;
            _statusMessage = buttonIndex switch
            {
                0 => "Tap tool active: tap any lane to place a tap note.",
                2 => "Flick-left tool active: tap a lane to place a flick; the configured inactive-bank modifier targets an inactive bank in collapsed mode.",
                3 => "Flick-right tool active: tap a lane to place a flick; the configured inactive-bank modifier targets an inactive bank in collapsed mode.",
                4 => "Vibrato tool active: tap a lane to place a vibrato note; drag its end to change the length.",
                _ => "Tap tool active: tap any lane to place a tap note."
            };
        }
        _editorSelectedTempoEvent = null;
        _editorHoverCacheValid = false;
    }

    private int[] GetEditorSelectLineTracks(bool panelsView)
    {
        var minTrack = panelsView ? 17 : 0;
        var maxTrack = panelsView ? 20 : 15;
        var tracks = _editorSelectedGridCells
            .Select(cell => cell.Track)
            .Where(track => track >= minTrack && track <= maxTrack)
            .ToHashSet();

        if (tracks.Count == 0 && _editorPanelsSelectedEmptyCell is { } emptyCell &&
            emptyCell.Track >= minTrack && emptyCell.Track <= maxTrack)
            tracks.Add(emptyCell.Track);

        if (tracks.Count == 0)
        {
            foreach (var ev in _editorMultiSelectedEvents)
                if (ev.Track >= minTrack && ev.Track <= maxTrack)
                    tracks.Add(ev.Track);
        }

        if (tracks.Count == 0 && _selectedEventKey.HasValue && _chart is not null)
        {
            var selected = _chart.Events.FirstOrDefault(ev => KeyOf(ev) == _selectedEventKey.Value);
            if (selected is not null && selected.Track >= minTrack && selected.Track <= maxTrack)
                tracks.Add(selected.Track);
        }

        if (tracks.Count == 0 && _selectedBeatTrack is int beatTrack &&
            beatTrack >= minTrack && beatTrack <= maxTrack)
            tracks.Add(beatTrack);

        return tracks.OrderBy(track => track).ToArray();
    }

    private void CollectEditorSelectLineEdges(List<Vector2> projectedEdges, float travelPixels, GearState state)
    {
        projectedEdges.Clear();
        var tracks = GetEditorSelectLineTracks(panelsView: false);
        if (tracks.Length == 0)
            return;

        // Build a horizontal slice from the actual selected lane columns in each
        // bank. This keeps the cyan line confined to the committed X range rather
        // than drawing across every bank regardless of the selection.
        var ranges = new Dictionary<(string Side, string Bank), (float Left, float Right)>();
        foreach (var track in tracks)
        {
            var position = GearState.Position(track);
            if (position is null)
                continue;

            var (side, bank, localPosition) = position.Value;
            if (_compact && !string.Equals(GearTimeline.ActiveBankForSide(state, side), bank, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!_compact && _chartEditorMode && GetEditorBankOpacity(side, bank, state) <= 0.001f)
                continue;

            var column = bank == "Center"
                ? (side == "left" ? localPosition + 1 : localPosition)
                : localPosition;
            var columnEdges = _projection.LaneColumnEdgesNormalized(side, bank, column);
            var key = (side, bank);
            if (ranges.TryGetValue(key, out var existing))
                ranges[key] = (Math.Min(existing.Left, columnEdges.Left), Math.Max(existing.Right, columnEdges.Right));
            else
                ranges[key] = columnEdges;
        }

        foreach (var range in ranges)
        {
            var left = _projection.ProjectSideTravel(range.Value.Left, travelPixels,
                range.Key.Side, range.Key.Bank).Position;
            var right = _projection.ProjectSideTravel(range.Value.Right, travelPixels,
                range.Key.Side, range.Key.Bank).Position;
            if (Math.Max(left.Y, right.Y) < -40f ||
                Math.Min(left.Y, right.Y) > _graphics.Viewport.Height + 150f)
                continue;
            projectedEdges.Add(left);
            projectedEdges.Add(right);
        }

        projectedEdges.Sort(static (a, b) => a.X.CompareTo(b.X));
    }

    private void ToggleEditorMultiSelection(ChartEvent clickedEvent)
    {
        if (_chart is null || clickedEvent.Kind != "Note" || clickedEvent.RawType != 1)
            return;

        _editorNoteGroupLibrarySelectionExplicit = false;
        var selection = _editorMultiSelectedEvents.Count > 0
            ? _editorMultiSelectedEvents.ToHashSet()
            : new HashSet<ChartEvent>();
        if (selection.Count == 0 && _selectedEventKey.HasValue)
        {
            var current = _chart.Events.FirstOrDefault(ev => KeyOf(ev) == _selectedEventKey.Value &&
                ev.Kind == "Note" && ev.RawType == 1);
            if (current is not null)
                selection.Add(current);
        }

        if (_editorNoteGroupEditMode && clickedEvent.NoteGroupId == _editorEditingNoteGroupId)
        {
            if (clickedEvent.NoteGroupInstanceId is int clickedInstanceId &&
                _editorEditingNoteGroupInstanceId != clickedInstanceId)
                selection.Clear();
            SetEditorNoteGroupEditingInstance(clickedEvent);
            if (!selection.Add(clickedEvent))
                selection.Remove(clickedEvent);
        }
        else if (!_editorNoteGroupEditMode && clickedEvent.NoteGroupId is int groupId &&
                 clickedEvent.NoteGroupInstanceId is int instanceId)
        {
            var instanceEvents = _chart.Events.Where(ev => ev.Kind == "Note" && ev.RawType == 1 &&
                ev.NoteGroupId == groupId && ev.NoteGroupInstanceId == instanceId).ToHashSet();
            if (instanceEvents.Any(selection.Contains))
                selection.ExceptWith(instanceEvents);
            else
                selection.UnionWith(instanceEvents);
        }
        else
        {
            if (_editorNoteGroupEditMode && clickedEvent.NoteGroupId != _editorEditingNoteGroupId)
            {
                _editorNoteGroupEditMode = false;
                _editorEditingNoteGroupId = null;
                _editorEditingNoteGroupInstanceId = null;
            }
            if (!selection.Add(clickedEvent))
                selection.Remove(clickedEvent);
        }

        // If a modifier-selection exits EDIT GROUP by targeting another group,
        // restore that instance's normal whole-instance selection lock.
        ExpandEditorNoteGroupSelection(selection);
        ClearEditorMultiSelection();
        foreach (var ev in selection)
            AddEditorMultiSelectedEvent(ev);

        var anchor = _editorMultiSelectedEvents.FirstOrDefault();
        _selectedEventKey = anchor is null ? null : KeyOf(anchor);
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorSelectedLinkRoot = anchor is null ? null : GetEditorLinkRoot(anchor);
        _editorSelectedHoldEnd = false;
        _editorSelectedGridCells.Clear();
        _editorPanelsSelectedEmptyCell = null;
        _editorSelectReferenceLineTick = null;
        _editorSelectReferenceTickOverride = null;
        _editorRectSelectionArmed = false;
        _editorRectSelecting = false;
        if (anchor is not null)
            _editorTrack = anchor.Track;
        RefreshEditorNoteGroupLibrarySelectionFromNotes();
        _editorHoverCacheValid = false;
        _statusMessage = _editorMultiSelectedEvents.Count switch
        {
            0 => "Multi-selection cleared.",
            1 => "Selected 1 note.",
            var count => $"Selected {count} notes."
        };
    }

    private void DeselectEditorSelection()
    {
        var selectionBefore = CaptureEditorChartSnapshot(selectionOnly: true);
        _editorNoteGroupPlacementArmed = false;
        ClearEditorNoteGroupLibrarySelection();
        _selectedEventKey = null;
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorSelectedTempoEvent = null;
        _editorStutterEditingGroup = null;
        _editorStutterStartTick = -1;
        _editorStutterEndTick = -1;
        _editorStutterStartPlacementPending = false;
        _editorStutterEndPlacementPending = false;
        _editorBpmPlacementPending = false;
        _editorBpmInputFocused = false;
        _editorBpmInputReplaceOnNextKey = false;
        _editorStutterInputField = EditorStutterInputField.None;
        _editorStutterInputReplaceOnNextKey = false;
        _editorSelectedLinkRoot = null;
        _editorSelectedHoldEnd = false;
        ClearEditorMultiSelection();
        _editorSelectedGridCells.Clear();
        _editorPanelsSelectedEmptyCell = null;
        _editorSelectReferenceLineTick = null;
        _editorSelectReferenceTickOverride = null;
        _editorRectSelectionArmed = false;
        _editorRectSelecting = false;
        ClearEditorPendingRectangleUndoSnapshot();
        _statusMessage = "Selection and grid-cell selection cleared.";
        CommitEditorSelectionUndoSnapshot(selectionBefore);
    }

    private void RunEditorEditTool(int toolIndex)
    {
        if (toolIndex == 8)
        {
            _editorAutoMergeLinks = !_editorAutoMergeLinks;
            _statusMessage = $"Auto-merge link: {(_editorAutoMergeLinks ? "ON" : "OFF")}.";
            return;
        }

        _editorPlacementEnabled = false;
        _editorConnectorMode = false;
        _editorBpmInputFocused = false;
        _editorSelectedTempoEvent = null;
        switch (toolIndex)
        {
            case 0: LinkSelectedEditorNotes(); break;
            case 1: LinkSelectedEditorNotesHorizontalFirst(); break;
            case 2: OpenEditorDurationTool(); break;
            case 3: UnlinkSelectedEditorNotes(EditorUnlinkMode.PreserveDuration); break;
            case 4: UnlinkSelectedEditorNotes(EditorUnlinkMode.Taps); break;
            case 5: UnlinkSelectedEditorNotes(EditorUnlinkMode.AllTaps); break;
            case 6: MirrorSelectedEditorNotesBank(); break;
            case 7: MirrorSelectedEditorNotesX(); break;
        }
    }

    private static bool IsPlayableEditorTrack(int track) => track is >= 0 and <= 15;

    private void HandleEditorDurationMenuChildClick(int childIndex, Rectangle childBounds, Microsoft.Xna.Framework.Point pixel)
    {
        if (childIndex == 0)
        {
            if (_editorDurationInputFocused)
            {
                _editorDurationInputCaretIndex = GetEditorNumericTextCaretIndex(
                    childBounds, _editorDurationInputText, pixel, _projection.ScalePixels(1f), "duration");
                _editorDurationSelectionAnchor = _editorDurationInputCaretIndex;
                _editorDurationInputReplaceOnNextKey = false;
                BeginEditorTextFieldMouseSelection("duration", childBounds,
                    _editorDurationInputCaretIndex, ref _editorDurationSelectionAnchor);
            }
            else
            {
                FocusEditorDurationInput(childBounds, pixel, _projection.ScalePixels(1f));
                BeginEditorTextFieldMouseSelection("duration", childBounds,
                    _editorDurationInputCaretIndex, ref _editorDurationSelectionAnchor);
            }
            return;
        }

        if (_editorDurationInputFocused)
        {
            if (!TryCommitEditorDurationInput())
                return;
            _editorDurationInputFocused = false;
            _editorDurationInputReplaceOnNextKey = false;
            _editorDurationInputText = string.Empty;
        }
        ApplyEditorDurationToSelectedNotes();
    }

    private void OpenEditorDurationTool()
    {
        _editorMenuSection = EditorMenuSection.Duration;
        _editorDurationInputFocused = false;
        _editorDurationInputReplaceOnNextKey = false;
        _editorDurationInputValuePending = false;
        _editorDurationInputText = string.Empty;
        _editorPlacementEnabled = false;
        _editorConnectorMode = false;
        _editorPanelsConnectorAwaitTarget = false;
        _editorPanelsConnectorPreviewValid = false;
        _editorPanelsHoverValid = false;
        _editorBpmInputFocused = false;
        _editorStutterInputField = EditorStutterInputField.None;
        _editorSelectedTempoEvent = null;
        _statusMessage = $"Duration tool: set a duration in ticks, then apply it to selected notes ({GetSelectedEditorDurationTargets().Count} eligible).";
    }

    private HashSet<ChartEvent> GetSelectedEditorDurationSelection()
    {
        var selected = new HashSet<ChartEvent>();
        if (_chart is null)
            return selected;

        bool IsEligibleForCurrentView(ChartEvent ev)
            => _editorPanelsViewEnabled
                ? IsEditorPanelAnimationEvent(ev)
                : IsEditorDurationNoteEvent(ev);

        foreach (var ev in _editorMultiSelectedEvents)
            if (IsEligibleForCurrentView(ev))
                selected.Add(ev);

        if (_selectedEventKey.HasValue)
        {
            var current = _chart.Events.FirstOrDefault(ev => KeyOf(ev) == _selectedEventKey.Value);
            if (current is not null && IsEligibleForCurrentView(current))
                selected.Add(current);
        }

        return selected;
    }

    private string GetEditorDurationFieldDisplayText()
    {
        if (_editorDurationInputFocused && _editorDurationInputText.Length > 0)
            return _editorDurationInputText;
        if (_editorDurationInputFocused)
        {
            var focusedSelection = GetSelectedEditorDurationSelection();
            var focusedDurations = focusedSelection.Select(ev => ev.Duration).Distinct().Take(2).ToArray();
            return focusedSelection.Count > 0 && focusedDurations.Length > 1 ? "MIXED" : string.Empty;
        }
        if (_editorDurationInputValuePending)
            return _editorDurationToolValue.ToString(CultureInfo.InvariantCulture);

        var selected = GetSelectedEditorDurationSelection();
        if (selected.Count == 0)
            return _editorDurationToolValue.ToString(CultureInfo.InvariantCulture);
        var durations = selected.Select(ev => ev.Duration).Distinct().Take(2).ToArray();
        return durations.Length > 1 ? "MIXED" : durations[0].ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsEditorDurationNoteEvent(ChartEvent ev)
        => ev.Kind == "Note" && ev.RawType == 1 &&
           (IsPlayableEditorTrack(ev.Track) ||
            (ev.Track is >= 17 and <= 20 && ev.Attr is >= 101 and <= 108));

    private List<ChartEvent> GetSelectedEditorDurationTargets()
        => GetSelectedEditorDurationSelection()
            .Where(ev => ev.NoteType is not (NoteType.LinkHead or NoteType.LinkTail or NoteType.End))
            .OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track).ThenBy(ev => ev.EventIndex)
            .ToList();

    private void ApplyEditorDurationToSelectedNotes()
    {
        if (_chart is null)
            return;

        var selected = GetSelectedEditorDurationSelection();
        var linkPoints = selected.Count(ev => ev.NoteType is NoteType.LinkHead or NoteType.LinkTail);
        var targets = selected.Where(ev => ev.NoteType is not (NoteType.LinkHead or NoteType.LinkTail or NoteType.End))
            .OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track).ThenBy(ev => ev.EventIndex).ToList();
        if (targets.Count == 0)
        {
            _statusMessage = linkPoints > 0
                ? "Link-point lengths follow connector positions. Move the connector points to change those lengths."
                : "Select one or more notes before applying a duration.";
            return;
        }

        var duration = _editorDurationToolValue;
        if (!_editorDurationInputValuePending)
        {
            var selectedDurations = selected.Select(ev => ev.Duration).Distinct().Take(2).ToArray();
            if (selectedDurations.Length > 1)
            {
                _statusMessage = "Selected notes have mixed durations. Edit the duration field before applying.";
                return;
            }
            if (selectedDurations.Length == 1)
                duration = selectedDurations[0];
        }

        var changes = new List<(ChartEvent Event, NoteType NewType)>();
        foreach (var ev in targets)
        {
            var newType = ev.NoteType is NoteType.Single or NoteType.SingleLong
                ? duration > 6 ? NoteType.SingleLong : NoteType.Single
                : ev.NoteType;
            if (ev.Duration != duration || ev.NoteType != newType)
                changes.Add((ev, newType));
        }

        if (changes.Count == 0)
        {
            _editorDurationToolValue = duration;
            _editorDurationInputValuePending = false;
            _statusMessage = linkPoints > 0
                ? $"All {targets.Count} eligible selected note(s) already use {duration} ticks; skipped {linkPoints} link point(s)."
                : $"All {targets.Count} selected note(s) already use {duration} ticks.";
            return;
        }

        var undoSnapshot = CaptureEditorChartSnapshot();
        var invalidatedTwinPairIds = changes
            .Where(change => change.Event.NoteType != change.NewType && change.Event.TwinPairId.HasValue)
            .Select(change => change.Event.TwinPairId!.Value)
            .ToHashSet();
        if (invalidatedTwinPairIds.Count > 0)
        {
            foreach (var ev in _chart.Events)
                if (ev.TwinPairId.HasValue && invalidatedTwinPairIds.Contains(ev.TwinPairId.Value))
                    ev.TwinPairId = null;
        }
        foreach (var change in changes)
        {
            change.Event.Duration = duration;
            change.Event.NoteType = change.NewType;
        }
        CommitEditorUndoSnapshot(undoSnapshot);
        RefreshAfterEditorMutation();
        _editorHoverCacheValid = false;
        _editorDurationToolValue = duration;
        _editorDurationInputValuePending = false;
        _statusMessage = linkPoints > 0
            ? $"Applied {duration} ticks to {changes.Count} selected note(s); skipped {linkPoints} link point(s), whose lengths follow connector positions."
            : $"Applied {duration} ticks to {changes.Count} selected note(s).";
    }

    private int NormalizeEditorRepeatedHorizontalLinkPoints(IEnumerable<EditorLinkChainInfo> chains)
    {
        if (_chart is null)
            return 0;

        var chainList = chains.ToList();
        // Membership must be checked against every chain, even when a caller
        // asks to normalize only the currently edited chain.
        var hasSingleActiveGroup = _editorNoteGroupEditMode &&
            _editorEditingNoteGroupId is int activeGroupId &&
            _editorEditingNoteGroupInstanceId is int activeInstanceId &&
            chainList.SelectMany(chain => chain.Nodes).All(node =>
                node.NoteGroupId == activeGroupId && node.NoteGroupInstanceId == activeInstanceId);
        var membershipChains = hasSingleActiveGroup
            ? GetEditorNoteGroupLinkChainInfos(_editorEditingNoteGroupId!.Value, _editorEditingNoteGroupInstanceId!.Value)
            : GetEditorLinkChainInfos();
        var membershipCounts = membershipChains
            .SelectMany(chain => chain.Nodes.Select(node => (Node: node, Chain: chain)))
            .GroupBy(item => item.Node)
            .ToDictionary(group => group.Key, group => group.Count());
        var removedCount = 0;

        foreach (var chain in chainList)
        {
            // Do not rewrite an ambiguous/shared path. Let diagnostics and the
            // other QUICK FIX steps report it instead of guessing which chain owns it.
            if (chain.Nodes.Any(node => !membershipCounts.TryGetValue(node, out var count) || count != 1))
                continue;

            var repeatedHorizontalGroups = Enumerable.Range(0, Math.Max(0, chain.Nodes.Count - 1))
                .Select(index => (Head: chain.Nodes[index], Tail: chain.Nodes[index + 1]))
                .Where(edge => edge.Head.Track != edge.Tail.Track)
                .GroupBy(edge => edge.Tail.Tick)
                .Where(group => group.Count() > 1)
                .ToList();

            foreach (var group in repeatedHorizontalGroups)
            {
                var tickNodes = chain.Nodes.Where(node => node.Tick == group.Key).ToList();
                // A LinkHead may itself begin at this tick. Preserve it; only
                // redundant LinkTail points can be removed by this normalization.
                if (tickNodes.Any(node => !ReferenceEquals(node, chain.Root) && node.NoteType != NoteType.LinkTail))
                    continue;

                var tailsAtTick = tickNodes.Where(node => node.NoteType == NoteType.LinkTail).ToList();
                if (tailsAtTick.Count < group.Count() || tailsAtTick.Count == 0)
                    continue;

                // Preserve the same logical tail in mirrored group instances. Their
                // physical track order is reversed, so use canonical group coordinates
                // instead of the current chart's physical traversal order.
                var groupedTails = tailsAtTick.Where(node => node.NoteGroupId.HasValue &&
                    node.NoteGroupInstanceId.HasValue && node.NoteGroupMemberId.HasValue).ToList();
                var keep = tailsAtTick[^1];
                if (groupedTails.Count == tailsAtTick.Count &&
                    groupedTails.Select(node => (node.NoteGroupId, node.NoteGroupInstanceId)).Distinct().Count() == 1)
                {
                    var instance = _chart.NoteGroupInstances.FirstOrDefault(candidate =>
                        candidate.GroupId == groupedTails[0].NoteGroupId &&
                        candidate.InstanceId == groupedTails[0].NoteGroupInstanceId);
                    if (instance is not null)
                    {
                        keep = groupedTails
                            .OrderBy(node => GetCanonicalEditorNoteGroupTrack(instance, node.Track))
                            .ThenBy(node => node.NoteGroupMemberId)
                            .ThenBy(node => node.EventIndex)
                            .Last();
                    }
                }
                foreach (var redundant in tailsAtTick.Where(node => !ReferenceEquals(node, keep)))
                {
                    foreach (var track in _chart.Tracks)
                        track.Events.RemoveAll(candidate => ReferenceEquals(candidate, redundant));
                    removedCount++;
                }
            }
        }

        return removedCount;
    }

    private List<EditorLinkChainInfo> GetEditorLinkChainInfos(HashSet<ChartEvent>? assignedNodes = null)
    {
        var result = new List<EditorLinkChainInfo>();
        if (_chart is null)
            return result;

        // Tracks 0-15 are the playable tracks. Higher-numbered tracks can carry
        // auxiliary timing/runtime data and must not affect link-path validation
        // or ID repair.
        var roots = _chart.Events
            .Where(ev => ev.Kind == "Note" && ev.RawType == 1 && IsPlayableEditorTrack(ev.Track) &&
                         ev.NoteType == NoteType.LinkHead)
            .OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track).ThenBy(ev => ev.EventIndex);
        foreach (var root in roots)
        {
            var nodes = GetEditorLinkChainNodes(root)
                .Where(ev => ev.Kind == "Note" && ev.RawType == 1 && IsPlayableEditorTrack(ev.Track) &&
                             ev.NoteType is NoteType.LinkHead or NoteType.LinkTail)
                .Distinct()
                .ToList();
            if (nodes.Count == 0)
                nodes.Add(root);

            if (assignedNodes is not null)
            {
                foreach (var node in nodes)
                    assignedNodes.Add(node);
            }

            var family = LinkBuilder.Family(root.Track);
            var endTick = nodes.Max(ev => (long)ev.EndTick);
            result.Add(new EditorLinkChainInfo(root, nodes, family, root.Tick, endTick, root.LinePair));
        }
        return result;
    }

    private List<EditorLinkPathIssue> GetEditorLinkPathIssues(List<EditorLinkChainInfo> chains)
    {
        var issues = new List<EditorLinkPathIssue>();
        if (_chart is null)
            return issues;

        void Add(ChartEvent ev, string message)
            => issues.Add(new EditorLinkPathIssue(ev.Tick, ev.Track, message, ev));

        var routeTimeline = new GearTimeline(_chart);
        var claimed = new HashSet<ChartEvent>();
        foreach (var chain in chains)
        {
            var root = chain.Root;
            var pairValid = chain.Pair is 1 or 3 or 5 or 7;
            if (!pairValid)
                Add(root, $"head tick {root.Tick}/track {root.Track} has invalid pair ID {chain.Pair}");
            if (chain.Nodes.Count < 2)
                Add(root, $"head tick {root.Tick}/track {root.Track} has no connected tail");

            foreach (var node in chain.Nodes)
            {
                if (!claimed.Add(node))
                    Add(node, $"tick {node.Tick}/track {node.Track} is shared by multiple chain paths");
                if (!string.Equals(LinkBuilder.Family(node.Track), chain.Family, StringComparison.Ordinal))
                    Add(node, $"chain at tick {root.Tick} crosses banks");
                if (node.LinePair != chain.Pair)
                    Add(node, $"tick {node.Tick}/track {node.Track} has pair {node.LinePair}, expected {chain.Pair}");
                if (pairValid)
                {
                    var expectedAttr = node.NoteType == NoteType.LinkHead ? chain.Pair : chain.Pair + 1;
                    if (node.Attr != expectedAttr)
                        Add(node, $"tick {node.Tick}/track {node.Track} has attr {node.Attr}, expected {expectedAttr}");
                }
            }
        }

        var linkPoints = _chart.Events.Where(ev => ev.Kind == "Note" && ev.RawType == 1 &&
            IsPlayableEditorTrack(ev.Track) &&
            ev.NoteType is NoteType.LinkHead or NoteType.LinkTail).ToList();
        foreach (var tail in linkPoints.Where(ev => ev.NoteType == NoteType.LinkTail && !claimed.Contains(ev)))
            Add(tail, $"orphan tail at tick {tail.Tick}/track {tail.Track} has no link-head path");

        // Orphan heads are reported from the actual rebuilt graph, not simply
        // from the presence of a LinkHead event. Their durations bound where a
        // matching tail can connect, so a later chain must not rescue them.
        foreach (var head in linkPoints.Where(ev => ev.NoteType == NoteType.LinkHead &&
                     !_chart.Links.Any(link => ReferenceEquals(link.Head, ev))))
        {
            if (!issues.Any(issue => ReferenceEquals(issue.Event, head) &&
                                     issue.Message.Contains("no connected tail", StringComparison.Ordinal)))
                Add(head, $"orphan LinkHead at tick {head.Tick}/track {head.Track} has no outgoing connector");
        }

        for (var i = 0; i < chains.Count; i++)
        {
            var a = chains[i];
            if (a.Pair is not (1 or 3 or 5 or 7))
                continue;
            for (var j = i + 1; j < chains.Count; j++)
            {
                var b = chains[j];
                if (a.Pair != b.Pair)
                    continue;

                // Reuse is allowed only until the two families have become a
                // possible route combination by the end of their overlap. A valid
                // SuperFlick registers its source/destination banks persistently.
                if (EditorLinkChainsConflictInTime(routeTimeline, a, b) &&
                    a.StartTick < b.EndTick && b.StartTick < a.EndTick)
                {
                    Add(b.Root, $"pair {a.Pair}/{a.Pair + 1} is reused by a possible route combination ({LinkBuilder.FamilyLabel(a.Family)} and {LinkBuilder.FamilyLabel(b.Family)}) between ticks {a.StartTick} and {b.StartTick}");
                    continue;
                }

                var boundaryTail = GetEditorLinkBoundaryTailConflict(a, b) ??
                                   GetEditorLinkBoundaryTailConflict(b, a);
                if (boundaryTail is not null &&
                    !EditorLinkFamiliesCanSharePair(routeTimeline, a.Family, b.Family,
                        boundaryTail.Tick, boundaryTail.Tick < int.MaxValue ? boundaryTail.Tick + 1 : boundaryTail.Tick))
                {
                    boundaryTail = null;
                }
                if (boundaryTail is not null)
                {
                    Add(boundaryTail,
                        $"pair {a.Pair}/{a.Pair + 1} cannot be reused at tick {boundaryTail.Tick}: a LinkTail at the hand-off tick is ambiguous; use a different ID");
                }
            }
        }

        return issues
            .GroupBy(issue => issue.Message, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private void QuickFixEditorLinks()
    {
        if (!_chartEditorMode || _chart is null)
        {
            _statusMessage = "Open a chart in Chart Editor before fixing links.";
            return;
        }

        var undoSnapshot = CaptureEditorChartSnapshot();
        if (undoSnapshot is null)
        {
            _statusMessage = "QUICK FIX could not create an undo snapshot; no changes were applied.";
            return;
        }

        var changed = false;
        var straightConverted = 0;
        var verticalConnectionsFixed = 0;
        var orphanTailsConverted = 0;
        var orphanHeadsConverted = 0;
        var idsReassigned = 0;
        string? idFixFailure = null;

        void RebuildEditorLinkGraph()
        {
            AresLoader.FinalizeEvents(_chart);
            LinkBuilder.Build(_chart);
        }

        // FORCE FIX: every note participates in the ordinary cleanup. Group
        // recovery runs afterward and reconstructs counterpart identities from
        // the most reliable surviving instance instead of protecting whole groups.
        RebuildEditorLinkGraph();
        var preservedGroupIds = _chart.NoteGroups.Select(group => group.Id)
            .Concat(_chart.NoteGroupInstances.Select(instance => instance.GroupId))
            .Concat(_chart.Events.Where(ev => ev.NoteGroupId.HasValue).Select(ev => ev.NoteGroupId!.Value))
            .ToHashSet();

        void ConvertLinkPointToStandalone(ChartEvent ev)
        {
            var retainedDuration = ev.Duration;
            ev.NoteType = retainedDuration > 6 ? NoteType.SingleLong : NoteType.Single;
            ev.Attr = 0;
            ev.LinePair = -1;
            ev.TwinPairId = null;
            ev.Duration = retainedDuration;
            MoveEditorNoteToAllowedTrack(ev);
        }

        // A same-track link segment is a hold-end marker, not a lane change.
        // Remove the redundant LinkTail endpoint and let LinkBuilder reconnect the
        // preceding point to the next actual lane change, just as the LINK tool does
        // when a same-track connection sets a duration on the selected point.
        for (var verticalPass = 0; verticalPass < 256; verticalPass++)
        {
            LinkSegment? verticalLinkMatch = _chart.Links
                .Where(link => IsPlayableEditorTrack(link.Head.Track) &&
                               IsPlayableEditorTrack(link.Tail.Track) &&
                               link.Head.Track == link.Tail.Track &&
                               link.Tail.NoteType == NoteType.LinkTail &&
                               link.Head.NoteType is NoteType.LinkHead or NoteType.LinkTail)
                .OrderBy(link => link.Tail.Tick)
                .ThenBy(link => link.Tail.Track)
                .Select(link => (LinkSegment?)link)
                .FirstOrDefault();
            if (!verticalLinkMatch.HasValue)
                break;

            var verticalLink = verticalLinkMatch.Value;
            var source = verticalLink.Head;
            var redundantTail = verticalLink.Tail;
            var intendedEndTick = redundantTail.Duration > 6
                ? (long)redundantTail.EndTick
                : (long)redundantTail.Tick;
            var sourceDuration = intendedEndTick - source.Tick;
            if (sourceDuration > 0)
                source.Duration = (int)Math.Clamp(Math.Max(source.Duration, sourceDuration), 1L, int.MaxValue);

            _chart.FindTrack(redundantTail.Track)?.Events.RemoveAll(candidate => ReferenceEquals(candidate, redundantTail));
            verticalConnectionsFixed++;
            changed = true;
            RebuildEditorLinkGraph();
            foreach (var chain in GetEditorLinkChainInfos())
                SyncEditorLinkChainDurations(chain.Root);
            RebuildEditorLinkGraph();
        }

        // A chain can change lanes only once at a particular connection tick.
        // Keep the final LinkTail for each repeated horizontal-connector group.
        var normalizedSameTickTails = NormalizeEditorRepeatedHorizontalLinkPoints(GetEditorLinkChainInfos());
        if (normalizedSameTickTails > 0)
        {
            changed = true;
            RebuildEditorLinkGraph();
            foreach (var chain in GetEditorLinkChainInfos())
                SyncEditorLinkChainDurations(chain.Root);
            RebuildEditorLinkGraph();
        }

        // Step 1: a connected path that never leaves its lane is just a hold.
        // Only process unambiguous paths; a point shared by multiple paths must
        // be left intact for the later ID/path validation to report safely.
        var initialChains = GetEditorLinkChainInfos();
        var initialMembershipCounts = initialChains
            .SelectMany(chain => chain.Nodes.Select(node => (Node: node, Chain: chain)))
            .GroupBy(item => item.Node)
            .ToDictionary(group => group.Key, group => group.Count());
        var straightChains = new List<EditorLinkChainInfo>();
        foreach (var chain in initialChains)
        {
            if (chain.Nodes.Count < 2 ||
                chain.Nodes.Any(node => node.Track != chain.Root.Track) ||
                chain.Nodes.Max(node => node.Tick) <= chain.Root.Tick ||
                chain.Nodes.Any(node => !initialMembershipCounts.TryGetValue(node, out var count) || count != 1))
                continue;

            var nodeSet = chain.Nodes.ToHashSet();
            var internalLinks = _chart.Links.Count(link => nodeSet.Contains(link.Head) && nodeSet.Contains(link.Tail));
            var incidentLinks = _chart.Links.Count(link => nodeSet.Contains(link.Head) || nodeSet.Contains(link.Tail));
            // Exactly one edge between each consecutive point means the path is
            // a simple straight chain, not a branch or a shared endpoint.
            if (internalLinks != chain.Nodes.Count - 1 || incidentLinks != chain.Nodes.Count - 1)
                continue;

            straightChains.Add(chain);
        }
        straightChains = straightChains
            .OrderBy(chain => chain.StartTick)
            .ThenBy(chain => chain.Root.Track)
            .ToList();

        var convertedNodes = new HashSet<ChartEvent>();
        foreach (var chain in straightChains)
        {
            if (chain.Root.NoteType != NoteType.LinkHead ||
                chain.Nodes.Any(node => convertedNodes.Contains(node) ||
                                        node.NoteType is not (NoteType.LinkHead or NoteType.LinkTail)))
                continue;

            // Intermediate link durations are body-to-next-point metadata. The
            // terminal LinkTail alone defines whether the converted hold extends
            // beyond its connection tick, matching gameplay's >6-tick rule.
            // This preserves a long terminal tail instead of cutting it at the tail.
            var terminal = chain.Nodes[^1];
            var endTick = terminal.Duration > 6 ? (long)terminal.EndTick : terminal.Tick;
            if (endTick <= chain.Root.Tick)
                continue;

            // ARES identifies SingleLong by duration > 6, so enforce that
            // minimum even for an unusually short straight link.
            var duration = (int)Math.Clamp(endTick - chain.Root.Tick, 7L, int.MaxValue);
            foreach (var tail in chain.Nodes.Where(node => !ReferenceEquals(node, chain.Root)))
            {
                _chart.FindTrack(tail.Track)?.Events.RemoveAll(candidate => ReferenceEquals(candidate, tail));
                convertedNodes.Add(tail);
            }

            chain.Root.NoteType = NoteType.SingleLong;
            chain.Root.Attr = 0;
            chain.Root.LinePair = -1;
            chain.Root.TwinPairId = null;
            chain.Root.Duration = duration;
            MoveEditorNoteToAllowedTrack(chain.Root);
            convertedNodes.Add(chain.Root);
            straightConverted++;
            changed = true;
        }

        if (straightConverted > 0)
            RebuildEditorLinkGraph();

        // Step 2: link-tail points that are not part of any path are broken
        // orphans. Keep the note and its duration when converting it to a standalone note.
        var assignedNodes = new HashSet<ChartEvent>();
        _ = GetEditorLinkChainInfos(assignedNodes);
        var orphanTails = _chart.Events
            .Where(ev => ev.Kind == "Note" && ev.RawType == 1 && IsPlayableEditorTrack(ev.Track) &&
                         ev.NoteType == NoteType.LinkTail && !assignedNodes.Contains(ev))
            .ToList();
        foreach (var tail in orphanTails)
        {
            ConvertLinkPointToStandalone(tail);
            orphanTailsConverted++;
            changed = true;
        }
        if (orphanTailsConverted > 0)
            RebuildEditorLinkGraph();

        // Step 3: any LinkHead without an outgoing connector is orphaned, even
        // if malformed link metadata made it appear in a different edge. Keep the
        // event and its duration as a standalone note, freeing the pair ID.
        var orphanHeads = _chart.Events
            .Where(ev => ev.Kind == "Note" && ev.RawType == 1 && IsPlayableEditorTrack(ev.Track) &&
                         ev.NoteType == NoteType.LinkHead &&
                         !_chart.Links.Any(link => ReferenceEquals(link.Head, ev)))
            .ToList();
        foreach (var head in orphanHeads)
        {
            ConvertLinkPointToStandalone(head);
            orphanHeadsConverted++;
            changed = true;
        }
        if (orphanHeadsConverted > 0)
            RebuildEditorLinkGraph();

        void ReassignRouteAwareLinkIds()
        {
            idFixFailure = null;
            var claimed = new HashSet<ChartEvent>();
            var chains = GetEditorLinkChainInfos(claimed)
                .OrderBy(chain => chain.StartTick)
                .ThenBy(chain => chain.Root.Track)
                .ThenBy(chain => chain.Root.EventIndex)
                .ToList();

            var memberships = chains.SelectMany(chain => chain.Nodes.Select(node => (Node: node, Chain: chain)))
                .GroupBy(item => item.Node)
                .Where(group => group.Count() > 1)
                .ToList();
            if (memberships.Count > 0)
            {
                var node = memberships[0].Key;
                idFixFailure = $"tick {node.Tick}/track {node.Track} is shared by multiple chain paths";
            }
            else
            {
                foreach (var chain in chains)
                {
                    if (chain.Family is null || chain.Nodes.Any(node =>
                            !string.Equals(LinkBuilder.Family(node.Track), chain.Family, StringComparison.Ordinal)))
                    {
                        idFixFailure = $"chain at tick {chain.StartTick} crosses banks";
                        break;
                    }
                }
            }

            var assignments = new List<(EditorLinkChainInfo Chain, int Pair)>();
            var routeTimeline = new GearTimeline(_chart);
            if (idFixFailure is null)
            {
                foreach (var chain in chains)
                {
                    var pair = new[] { 1, 3, 5, 7 }.FirstOrDefault(candidate => !assignments.Any(existing =>
                        existing.Pair == candidate && EditorLinkChainsConflictInTime(routeTimeline, existing.Chain, chain)));
                    if (pair == 0)
                    {
                        idFixFailure = $"more than four chains overlap near tick {chain.StartTick}";
                        break;
                    }
                    assignments.Add((chain, pair));
                }
            }

            if (idFixFailure is null)
            {
                var assignmentChanged = false;
                foreach (var assignment in assignments)
                {
                    var chainChanged = assignment.Chain.Nodes.Any(node =>
                        node.LinePair != assignment.Pair || node.Attr !=
                            (node.NoteType == NoteType.LinkHead ? assignment.Pair : assignment.Pair + 1));
                    if (!chainChanged)
                        continue;

                    foreach (var node in assignment.Chain.Nodes)
                    {
                        node.LinePair = assignment.Pair;
                        node.Attr = node.NoteType == NoteType.LinkHead ? assignment.Pair : assignment.Pair + 1;
                    }
                    idsReassigned++;
                    assignmentChanged = true;
                    changed = true;
                }
                if (assignmentChanged)
                    RebuildEditorLinkGraph();
            }
        }

        // Recover all grouped instances first. Their temporary pair IDs are then
        // normalized together with every ungrouped chain in one route-aware pass.
        var groupRepairStats = ForceRepairEditorQuickFixGroups();
        if (groupRepairStats.Changed)
            changed = true;
        RebuildEditorLinkGraph();
        ReassignRouteAwareLinkIds();

        if (changed)
        {
            // Force recovery has already rebuilt each recoverable group from its
            // best surviving instance. A malformed member is detached and kept as
            // a note; it must not cancel repairs to unrelated groups.

            // QUICK FIX already repairs every mirrored instance independently and
            // verifies equivalence. Do not let the currently selected EDIT GROUP
            // instance re-run a second, source-only synchronization over that work.
            var restoreGroupEditMode = _editorNoteGroupEditMode;
            var restoreEditingGroupId = _editorEditingNoteGroupId;
            var restoreEditingInstanceId = _editorEditingNoteGroupInstanceId;
            if (restoreGroupEditMode)
            {
                _editorNoteGroupEditMode = false;
                _editorEditingNoteGroupId = null;
                _editorEditingNoteGroupInstanceId = null;
            }
            try
            {
                CommitEditorUndoSnapshot(undoSnapshot, preservedGroupIds);
            }
            finally
            {
                if (restoreGroupEditMode && restoreEditingGroupId is int editingGroupId &&
                    restoreEditingInstanceId is int editingInstanceId &&
                    _chart.NoteGroupInstances.Any(instance => instance.GroupId == editingGroupId &&
                        instance.InstanceId == editingInstanceId))
                {
                    _editorNoteGroupEditMode = true;
                    _editorEditingNoteGroupId = editingGroupId;
                    _editorEditingNoteGroupInstanceId = editingInstanceId;
                }
            }

            // CommitEditorUndoSnapshot refreshes instance anchors. Rebuild only
            // the chosen reference definitions in one indexed pass afterward.
            RefreshEditorQuickFixGroupDefinitions(groupRepairStats.ReferenceInstances);
            RefreshAfterEditorMutation();
        }

        var finalChains = GetEditorLinkChainInfos();
        var remainingIssues = GetEditorLinkPathIssues(finalChains);
        var cleanupSummary = new List<string>();
        if (groupRepairStats.RepairedGroups > 0)
            cleanupSummary.Add($"force-repaired {groupRepairStats.RepairedGroups} group(s)");
        if (groupRepairStats.RecoveredMemberIds > 0)
            cleanupSummary.Add($"recovered {groupRepairStats.RecoveredMemberIds} group member ID(s)");
        if (groupRepairStats.CreatedCounterparts > 0)
            cleanupSummary.Add($"recreated {groupRepairStats.CreatedCounterparts} missing group counterpart(s)");
        if (groupRepairStats.DetachedNotes > 0)
            cleanupSummary.Add($"preserved {groupRepairStats.DetachedNotes} unmatched note(s) outside their groups");
        if (groupRepairStats.RepairedDefinitions > 0)
            cleanupSummary.Add($"rebuilt {groupRepairStats.RepairedDefinitions} group definition/instance record(s)");
        if (verticalConnectionsFixed > 0)
            cleanupSummary.Add($"fixed {verticalConnectionsFixed} same-track vertical link connection(s)");
        if (normalizedSameTickTails > 0)
            cleanupSummary.Add($"removed {normalizedSameTickTails} redundant same-tick link point(s)");
        if (straightConverted > 0)
            cleanupSummary.Add($"converted {straightConverted} straight link chain(s) to SingleLong");
        if (orphanTailsConverted > 0)
            cleanupSummary.Add($"converted {orphanTailsConverted} orphan tail(s) to standalone notes, preserving duration");
        if (orphanHeadsConverted > 0)
            cleanupSummary.Add($"converted {orphanHeadsConverted} orphan LinkHead(s) to standalone notes, preserving duration");
        if (idsReassigned > 0)
            cleanupSummary.Add($"reassigned IDs for {idsReassigned} chain(s)");

        if (idFixFailure is not null)
        {
            var cleanupText = cleanupSummary.Count > 0 ? string.Join("; ", cleanupSummary) + ". " : string.Empty;
            _statusMessage = $"QUICK FIX: {cleanupText}ID fix stopped: {idFixFailure}.";
        }
        else if (!changed)
        {
            var skippedText = cleanupSummary.Count > 0 ? string.Join("; ", cleanupSummary) + ". " : string.Empty;
            _statusMessage = remainingIssues.Count == 0
                ? $"QUICK FIX: {skippedText}all {finalChains.Count} chain(s) are already clean and have valid IDs."
                : $"QUICK FIX: {skippedText}made no changes; {remainingIssues.Count} path issue(s) remain. {remainingIssues[0].Message}";
        }
        else
        {
            var cleanupText = cleanupSummary.Count > 0 ? string.Join("; ", cleanupSummary) + ". " : string.Empty;
            _statusMessage = remainingIssues.Count == 0
                ? $"QUICK FIX complete: {cleanupText}all remaining link paths validated."
                : $"QUICK FIX: {cleanupText}{remainingIssues.Count} path issue(s) remain. {remainingIssues[0].Message}";
        }
    }

    private bool IsEditorLinkableNote(ChartEvent ev)
        => ev.Kind == "Note" && ev.RawType == 1 && ev.NoteType != NoteType.End &&
           !ev.IsFlick && !ev.IsSuperFlick && ev.NoteType != NoteType.Vibrato;

    private bool IsEditorLinkPairAvailable(string family, int pair, int startTick, int endTick,
        HashSet<ChartEvent> movingEvents, bool hasLinkTailAtStart = false)
    {
        return _chart is not null &&
               !IsLinkPairOccupied(_chart, family, pair, startTick, endTick, movingEvents, hasLinkTailAtStart);
    }

    private ChartTrack GetOrCreateEditorTrack(int trackIndex)
    {
        if (_chart is null)
            throw new InvalidOperationException("No chart is loaded.");
        var track = _chart.FindTrack(trackIndex);
        if (track is not null)
            return track;
        track = new ChartTrack { TrackIndex = trackIndex };
        _chart.Tracks.Add(track);
        return track;
    }

    private void LinkSelectedEditorNotes(
        IReadOnlyCollection<ChartEvent>? selectedNotesOverride = null,
        List<ChartEvent>? previewNodesOut = null,
        IReadOnlySet<ChartEvent>? durationSplitEvents = null)
    {
        if (!_chartEditorMode || _chart is null)
        {
            _statusMessage = "Open the chart editor before linking notes.";
            return;
        }

        var routeTimeline = new GearTimeline(_chart);
        var selected = (selectedNotesOverride is null
                ? GetSelectedEditorNotes(expandLinkFamilies: true, orderByTrack: false)
                : selectedNotesOverride.ToList())
            .Where(IsEditorLinkableNote)
            .Distinct()
            .ToList();
        if (selected.Count < 2)
        {
            _statusMessage = "Select at least two notes to build a link path.";
            return;
        }

        // A path is built from vertical segments and track changes. Notes on the
        // current track at a connection tick are duration endpoints, not link points.
        var selectedSet = selected.ToHashSet();
        var plans = new List<(string Family, List<ChartEvent> Points, List<ChartEvent> Absorbed,
            int Pair, int StartTick, int EndTick, bool VerticalOnly)>();
        // When a selected path runs into a same-tick LinkHead -> LinkTail turn,
        // the head becomes the same-track endpoint of the incoming path. Keep the
        // existing event object so its chart metadata survives, but relocate it
        // only after every path has passed validation.
        var relocatedLinkPoints = new Dictionary<ChartEvent, int>();

        foreach (var grouping in selected
                     .Select(ev => (Event: ev, Family: LinkBuilder.Family(ev.Track)))
                     .Where(item => item.Family is not null)
                     .GroupBy(item => item.Family!, item => item.Event))
        {
            var notes = grouping.OrderBy(ev => ev.Tick).ToList();
            if (notes.Count < 2)
                continue;

            var tickGroups = notes.GroupBy(ev => ev.Tick).OrderBy(group => group.Key).ToList();
            var firstGroup = tickGroups[0].ToList();
            List<ChartEvent> points;
            int currentTrack;
            if (firstGroup.Count == 1)
            {
                points = new List<ChartEvent> { firstGroup[0] };
                currentTrack = firstGroup[0].Track;
            }
            else if (TryGetEditorSameTickTapHoldPair(firstGroup, out var sameTickTap, out var sameTickHold))
            {
                // For a same-tick tap/hold pair, the tap starts the link and the
                // hold becomes its same-tick horizontal tail. The hold's duration
                // is preserved as the tail endpoint.
                points = new List<ChartEvent> { sameTickTap, sameTickHold };
                currentTrack = sameTickHold.Track;
            }
            else if (TryGetEditorSameTickLinkPair(firstGroup, out var sameTickHead, out var sameTickTail))
            {
                // An existing horizontal LinkHead -> LinkTail is already a valid
                // starting point. Keep the LinkHead as the root and continue from
                // the tail's lane instead of rejecting the two notes as ambiguous.
                points = new List<ChartEvent> { sameTickHead, sameTickTail };
                currentTrack = sameTickTail.Track;
            }
            else
            {
                _statusMessage = $"Cannot build a link path: the starting tick {tickGroups[0].Key} has multiple possible starting notes.";
                return;
            }

            var absorbed = new List<ChartEvent>();
            foreach (var tickGroup in tickGroups.Skip(1))
            {
                var atTick = tickGroup.ToList();
                var otherTracks = atTick.Select(ev => ev.Track).Where(track => track != currentTrack).Distinct().ToList();
                var onCurrentTrack = atTick.Where(ev => ev.Track == currentTrack).ToList();
                if (otherTracks.Count > 1)
                {
                    // If these simultaneous points are exactly an existing
                    // LinkHead -> LinkTail turn, route into that LinkHead by
                    // converting it to a same-track LinkTail, then continue on
                    // its existing outgoing tail. This produces a straight chain
                    // through the turn instead of treating it as a branch.
                    // Example: t10/L1 tap, t20/L2 head -> t20/L3 tail becomes
                    // t10/L1 head -> t20/L1 tail -> t20/L3 tail.
                    var sameTickTurns = _chart.Links
                        .Where(link => link.Head.NoteType == NoteType.LinkHead &&
                                       link.Head.Tick == tickGroup.Key && link.Tail.Tick == tickGroup.Key &&
                                       atTick.Contains(link.Head) && atTick.Contains(link.Tail))
                        .ToList();
                    if (onCurrentTrack.Count == 0 && atTick.Count == 2 && sameTickTurns.Count == 1)
                    {
                        var turnHead = sameTickTurns[0].Head;
                        var turnTail = sameTickTurns[0].Tail;
                        if (!ReferenceEquals(turnHead, turnTail) &&
                            turnHead.Track != currentTrack && turnTail.Track != currentTrack &&
                            LinkBuilder.Family(turnTail.Track) == grouping.Key)
                        {
                            points.Add(turnHead);
                            points.Add(turnTail);
                            relocatedLinkPoints[turnHead] = currentTrack;
                            currentTrack = turnTail.Track;
                            continue;
                        }
                    }

                    _statusMessage = $"Cannot build a link path: tick {tickGroup.Key} branches onto multiple tracks.";
                    return;
                }

                if (onCurrentTrack.Count > 1)
                {
                    _statusMessage = $"Cannot build a link path: multiple notes occupy track {currentTrack} at tick {tickGroup.Key}.";
                    return;
                }

                if (otherTracks.Count == 0)
                {
                    // Same-track notes simply extend the segment's duration.
                    absorbed.AddRange(onCurrentTrack);
                    continue;
                }

                var destinationTrack = otherTracks[0];
                var destinationNotes = atTick.Where(ev => ev.Track == destinationTrack).ToList();
                if (destinationNotes.Count != 1)
                {
                    _statusMessage = $"Cannot build a link path: multiple destination notes occupy track {destinationTrack} at tick {tickGroup.Key}.";
                    return;
                }

                // If a selected source-track note is at the transition tick, it is
                // the vertical hold end. It is intentionally absorbed, not made a tail.
                absorbed.AddRange(onCurrentTrack);
                points.Add(destinationNotes[0]);
                currentTrack = destinationTrack;
            }

            // A hold may be extended to the next track change, but never shortened
            // over an existing authored hold endpoint.
            for (var i = 0; i < points.Count; i++)
            {
                var point = points[i];
                var nextTick = i + 1 < points.Count ? points[i + 1].Tick : int.MaxValue;
                if (point.Duration > 6 && durationSplitEvents?.Contains(point) != true &&
                    (point.NoteType == NoteType.SingleLong ||
                     ((point.NoteType is NoteType.LinkHead or NoteType.LinkTail) && GetEditorNextLinkPoint(point) is null)) &&
                    i + 1 < points.Count && nextTick < point.EndTick)
                {
                    _statusMessage = $"Cannot link: the hold on track {point.Track} ends at tick {point.EndTick}, after the next connection at tick {nextTick}. Shorten it first.";
                    return;
                }
            }

            var terminalEnd = notes.Max(ev => ev.Duration > 6 ? ev.EndTick : ev.Tick);
            if (points.Count == 1)
            {
                // A path that never changes tracks is just a normal hold.
                // Preserve the earliest note and absorb later selected taps into its duration.
                var start = points[0];
                var endTick = Math.Max(terminalEnd, absorbed.Select(ev => ev.Tick).DefaultIfEmpty(start.Tick).Max());
                plans.Add((grouping.Key, points, absorbed, 0, start.Tick, endTick, true));
                continue;
            }

            var startTick = points[0].Tick;
            var endTickForPath = Math.Max(terminalEnd, points[^1].Tick);
            var hasLinkTailAtStart = points.Count > 1 && points[1].Tick == startTick;
            var pair = new[] { 1, 3, 5, 7 }.FirstOrDefault(candidate =>
                IsEditorLinkPairAvailable(grouping.Key, candidate, startTick, endTickForPath, selectedSet, hasLinkTailAtStart) &&
                !plans.Any(plan => !plan.VerticalOnly && plan.Pair == candidate &&
                    EditorLinkIntervalsConflict(
                        routeTimeline,
                        plan.Family, plan.StartTick, plan.EndTick,
                        plan.Points.Count > 1 && plan.Points[1].Tick == plan.StartTick,
                        grouping.Key, startTick, endTickForPath, hasLinkTailAtStart)));
            if (pair == 0)
            {
                _statusMessage = "No safe link pair is available for the route combinations reached by this chart.";
                return;
            }
            plans.Add((grouping.Key, points, absorbed, pair, startTick, endTickForPath, false));
        }

        if (plans.Count == 0)
        {
            _statusMessage = "Select notes from one track family to build a link path.";
            return;
        }

        if (previewNodesOut is not null)
        {
            previewNodesOut.Clear();
            foreach (var plan in plans.Where(plan => !plan.VerticalOnly))
                AppendEditorLinkPreviewPoints(previewNodesOut, plan.Points, plan.Pair, plan.EndTick);
            return;
        }

        var undoSnapshot = CaptureEditorChartSnapshot();
        // Connector split supplies a virtual tap as one of the path points. Add it
        // only after capturing history so one Undo removes the complete rebuild.
        foreach (var point in plans.SelectMany(plan => plan.Points).Distinct())
        {
            if (_chart.Events.Contains(point))
                continue;
            var destinationTrack = GetOrCreateEditorTrack(point.Track);
            point.EventIndex = destinationTrack.Events.Select(ev => ev.EventIndex).DefaultIfEmpty(-1).Max() + 1;
            destinationTrack.Events.Add(point);
        }
        ChartEvent? firstRoot = null;
        var absorbedEvents = plans.SelectMany(plan => plan.Absorbed).ToHashSet();
        foreach (var relocation in relocatedLinkPoints)
        {
            var ev = relocation.Key;
            var destinationTrackIndex = relocation.Value;
            if (ev.Track == destinationTrackIndex)
                continue;

            foreach (var track in _chart.Tracks)
                track.Events.RemoveAll(candidate => ReferenceEquals(candidate, ev));

            var destination = GetOrCreateEditorTrack(destinationTrackIndex);
            ev.Track = destinationTrackIndex;
            ev.EventIndex = destination.Events.Select(candidate => candidate.EventIndex)
                .DefaultIfEmpty(-1).Max() + 1;
            destination.Events.Add(ev);
        }

        foreach (var plan in plans)
        {
            if (plan.VerticalOnly)
            {
                var start = plan.Points[0];
                start.NoteType = NoteType.SingleLong;
                start.Attr = 0;
                start.LinePair = -1;
                start.TwinPairId = null;
                start.Duration = Math.Max(7, plan.EndTick - start.Tick);
                continue;
            }

            for (var i = 0; i < plan.Points.Count; i++)
            {
                var point = plan.Points[i];
                point.NoteType = i == 0 ? NoteType.LinkHead : NoteType.LinkTail;
                point.Attr = i == 0 ? plan.Pair : plan.Pair + 1;
                point.LinePair = plan.Pair;
                point.TwinPairId = null;
                if (i + 1 < plan.Points.Count)
                    point.Duration = Math.Max(6, plan.Points[i + 1].Tick - point.Tick);
                else
                    point.Duration = Math.Max(6, plan.EndTick - point.Tick);
                if (i == 0)
                    firstRoot ??= point;
            }
        }

        // Remove only the selected redundant taps that are now represented by
        // durations. Do not remove actual link points or unrelated chart events.
        foreach (var track in _chart.Tracks)
            track.Events.RemoveAll(ev => absorbedEvents.Contains(ev));

        CommitEditorUndoSnapshot(undoSnapshot);
        RefreshAfterEditorMutation();
        foreach (var plan in plans.Where(plan => !plan.VerticalOnly))
            if (plan.Points.Count > 0 && plan.Points[0].NoteType == NoteType.LinkHead)
                SyncEditorLinkChainDurations(plan.Points[0]);
        RefreshAfterEditorMutation();

        ClearEditorMultiSelection();
        _selectedEventKey = firstRoot is not null ? KeyOf(firstRoot) : KeyOf(plans[0].Points[0]);
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorSelectedLinkRoot = firstRoot;
        _editorSelectedHoldEnd = false;
        var selectedRoot = firstRoot ?? plans[0].Points[0];
        _editorTrack = selectedRoot.Track;
        _statusMessage = plans.All(plan => plan.VerticalOnly)
            ? $"Converted selected vertical notes into {plans.Count} hold(s); no link tails were created."
            : plans.Count == 1
                ? $"Built a link path with {plans[0].Points.Count} track-change point(s); vertical endpoints use duration."
                : $"Built {plans.Count} paths; vertical endpoints use duration and link tails only mark track changes.";
    }

    private static bool TryGetEditorSameTickTapHoldPair(
        IReadOnlyCollection<ChartEvent> events, out ChartEvent tap, out ChartEvent hold)
    {
        tap = null!;
        hold = null!;
        if (events.Count != 2)
            return false;

        var candidates = events.ToList();
        var tapCandidates = candidates.Where(ev => ev.NoteType == NoteType.Single && ev.Duration <= 6 && ev.Attr == 0).ToList();
        var holdCandidates = candidates.Where(ev => ev.NoteType == NoteType.SingleLong).ToList();
        if (tapCandidates.Count != 1 || holdCandidates.Count != 1)
            return false;

        var candidateTap = tapCandidates[0];
        var candidateHold = holdCandidates[0];
        if (candidateTap.Tick != candidateHold.Tick || candidateTap.Track == candidateHold.Track ||
            !string.Equals(LinkBuilder.Family(candidateTap.Track), LinkBuilder.Family(candidateHold.Track), StringComparison.Ordinal))
            return false;

        tap = candidateTap;
        hold = candidateHold;
        return true;
    }

    private static bool TryGetEditorSameTickLinkPair(
        IReadOnlyCollection<ChartEvent> events, out ChartEvent head, out ChartEvent tail)
    {
        head = null!;
        tail = null!;
        if (events.Count != 2)
            return false;

        var candidates = events.ToList();
        var headCandidates = candidates.Where(ev => ev.NoteType == NoteType.LinkHead).ToList();
        var tailCandidates = candidates.Where(ev => ev.NoteType == NoteType.LinkTail).ToList();
        if (headCandidates.Count != 1 || tailCandidates.Count != 1)
            return false;

        var candidateHead = headCandidates[0];
        var candidateTail = tailCandidates[0];
        if (candidateHead.Tick != candidateTail.Tick || candidateHead.Track == candidateTail.Track ||
            !string.Equals(LinkBuilder.Family(candidateHead.Track), LinkBuilder.Family(candidateTail.Track), StringComparison.Ordinal))
            return false;

        head = candidateHead;
        tail = candidateTail;
        return true;
    }

    private static int GetEditorLinkSameTickOrder(ChartEvent ev) => ev.NoteType switch
    {
        NoteType.LinkHead => 0,
        NoteType.Single when ev.Duration <= 6 => 1,
        NoteType.SingleLong => 2,
        NoteType.LinkTail => 3,
        _ => 4
    };

    private static void AppendEditorLinkPreviewPoints(
        List<ChartEvent> output,
        IReadOnlyList<ChartEvent> points,
        int pair,
        int endTick)
    {
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var nextTick = i + 1 < points.Count ? points[i + 1].Tick : endTick;
            var preview = CloneEditorLinkNote(point, point.Tick, point.Track,
                Math.Max(6, nextTick - point.Tick));
            preview.EventIndex = point.EventIndex;
            preview.NoteType = i == 0 ? NoteType.LinkHead : NoteType.LinkTail;
            preview.Attr = i == 0 ? pair : pair + 1;
            preview.LinePair = pair;
            preview.TwinPairId = null;
            // Preview points must keep the same stable event identity as their
            // authored counterparts; the synthetic tap receives a unique identity.
            output.Add(preview);
        }
    }

    private static ChartEvent CloneEditorLinkNote(ChartEvent source, int tick, int track, int duration)
        => new()
        {
            Tick = tick,
            Track = track,
            Attr = source.Attr,
            RawType = source.RawType,
            TypeIndex = source.TypeIndex,
            Duration = Math.Max(6, duration),
            NoteType = NoteType.LinkTail,
            EventIndex = 0, // Assigned when added to the destination track.
            LinePair = -1,
            TwinPairId = null,
            NoteGroupId = source.NoteGroupId,
            NoteGroupInstanceId = source.NoteGroupInstanceId,
            NoteGroupMemberId = source.NoteGroupMemberId,
            LoopReconstructed = source.LoopReconstructed,
            LoopPhase = source.LoopPhase,
            Kind = source.Kind,
            Value = source.Value,
            Instrument = source.Instrument,
            Velocity = source.Velocity,
            Pan = source.Pan
        };

    /// <summary>
    /// LINK X creates the lane change first, then extends along the destination
    /// lane. For a two-note path, the second note becomes a duration endpoint on
    /// the destination lane rather than a diagonal tail at its original tick.
    /// Longer paths retain a tail at each later lane-run endpoint so subsequent
    /// horizontal connections remain part of one link chain.
    /// </summary>
    private void LinkSelectedEditorNotesHorizontalFirst(
        IReadOnlyCollection<ChartEvent>? selectedNotesOverride = null,
        List<ChartEvent>? previewNodesOut = null,
        IReadOnlySet<ChartEvent>? durationSplitEvents = null)
    {
        if (!_chartEditorMode || _chart is null)
        {
            _statusMessage = "Open the chart editor before linking notes.";
            return;
        }

        var routeTimeline = new GearTimeline(_chart);
        var selected = (selectedNotesOverride is null
                ? GetSelectedEditorNotes(expandLinkFamilies: true, orderByTrack: false)
                : selectedNotesOverride.ToList())
            .Where(IsEditorLinkableNote)
            .Distinct()
            .ToList();
        if (selected.Count < 2)
        {
            _statusMessage = "Select at least two notes to build a link path.";
            return;
        }

        var selectedSet = selected.ToHashSet();
        var plans = new List<(string Family, List<ChartEvent> Points, List<ChartEvent> Absorbed,
            List<ChartEvent> Generated, int Pair, int StartTick, int EndTick, bool VerticalOnly)>();

        foreach (var grouping in selected
                     .Select(ev => (Event: ev, Family: LinkBuilder.Family(ev.Track)))
                     .Where(item => item.Family is not null)
                     .GroupBy(item => item.Family!, item => item.Event))
        {
            // Order by time, then by note semantics for same-tick pairs. A
            // LinkHead is the source and a LinkTail is the destination; taps also
            // precede holds for the initial tap/hold pair. Track numbers never
            // participate in deciding the route.
            var notes = grouping.OrderBy(ev => ev.Tick)
                .ThenBy(GetEditorLinkSameTickOrder)
                .ToList();
            if (notes.Count < 2)
                continue;

            var tickGroups = notes.GroupBy(ev => ev.Tick).OrderBy(group => group.Key).ToList();

            // Preserve the existing fast path for a chart consisting of only a
            // same-tick tap/hold pair. When there are later points as well, that
            // pair is accepted as the start of the lane run below.
            if (notes.Count == 2 && tickGroups.Count == 1 &&
                TryGetEditorSameTickTapHoldPair(notes, out var sameTickTap, out var sameTickHold))
            {
                var pair = new[] { 1, 3, 5, 7 }.FirstOrDefault(candidate =>
                    IsEditorLinkPairAvailable(grouping.Key, candidate, sameTickTap.Tick, sameTickHold.EndTick, selectedSet,
                        hasLinkTailAtStart: true) &&
                    !plans.Any(plan => !plan.VerticalOnly && plan.Pair == candidate &&
                        EditorLinkIntervalsConflict(
                            routeTimeline,
                            plan.Family, plan.StartTick, plan.EndTick,
                            plan.Generated.Any(ev => ev.Tick == plan.StartTick) ||
                            (plan.Points.Count > 1 && plan.Points[1].Tick == plan.StartTick),
                            grouping.Key, sameTickTap.Tick, sameTickHold.EndTick, true)));
                if (pair == 0)
                {
                    _statusMessage = "No safe link pair is available for the route combinations reached by this chart.";
                    return;
                }

                plans.Add((grouping.Key, new List<ChartEvent> { sameTickTap, sameTickHold },
                    new List<ChartEvent>(), new List<ChartEvent>(), pair,
                    sameTickTap.Tick, sameTickHold.EndTick, false));
                continue;
            }

            // Duplicate ticks are valid only for a true horizontal LinkHead /
            // LinkTail pair, or for an initial tap/hold pair. In particular, the
            // latter must remain valid when later points continue the path.
            var ambiguousGroup = tickGroups.FirstOrDefault(group =>
            {
                if (group.Count() <= 1)
                    return false;

                var sameTick = group.ToList();
                return !TryGetEditorSameTickLinkPair(sameTick, out _, out _) &&
                       !(group.Key == tickGroups[0].Key &&
                         TryGetEditorSameTickTapHoldPair(sameTick, out _, out _));
            });
            if (ambiguousGroup is not null)
            {
                _statusMessage = $"Cannot build LINK X: tick {ambiguousGroup.Key} has multiple possible notes.";
                return;
            }

            // Collapse consecutive selected notes on the same track into a lane
            // run. Only the first note starts the path; the last note of an
            // intermediate run is retained as the point where the next horizontal
            // connection begins.
            var runs = new List<List<ChartEvent>>();
            foreach (var ev in notes)
            {
                if (runs.Count == 0 || runs[^1][0].Track != ev.Track)
                    runs.Add(new List<ChartEvent>());
                runs[^1].Add(ev);
            }

            // LinkHead duration is metadata for its outgoing segment, not a
            // terminal hold. LINK X should end at that point's tick unless the
            // selected endpoint is an actual long hold or terminal LinkTail.
            var terminalEnd = notes.Max(ev =>
                ev.Duration > 6 && (ev.NoteType == NoteType.SingleLong ||
                    (ev.NoteType == NoteType.LinkTail && GetEditorNextLinkPoint(ev) is null))
                    ? ev.EndTick
                    : ev.Tick);
            if (runs.Count == 1)
            {
                plans.Add((grouping.Key, new List<ChartEvent> { notes[0] }, notes.Skip(1).ToList(),
                    new List<ChartEvent>(), 0, notes[0].Tick, terminalEnd, true));
                continue;
            }

            var points = new List<ChartEvent> { runs[0][0] };
            var absorbed = new List<ChartEvent>();
            var generated = new List<ChartEvent>();
            absorbed.AddRange(runs[0].Skip(1));

            for (var runIndex = 0; runIndex < runs.Count - 1; runIndex++)
            {
                var sourceTick = runIndex == 0 ? runs[0][0].Tick : runs[runIndex][^1].Tick;
                var destinationRun = runs[runIndex + 1];
                var destinationIsFinal = runIndex + 1 == runs.Count - 1;
                var destinationEndTick = destinationIsFinal ? terminalEnd : destinationRun[^1].Tick;
                if (destinationEndTick < sourceTick)
                {
                    _statusMessage = "Cannot build LINK X: the selected points are not in chronological order.";
                    return;
                }

                // The synthetic tail is created on the destination track at the
                // source tick. Its duration reaches the next lane-run endpoint.
                var tail = CloneEditorLinkNote(destinationRun[0], sourceTick, destinationRun[0].Track,
                    Math.Max(6, destinationEndTick - sourceTick));
                // Preserve the originating note identity only in preview output.
                // The commit path assigns a fresh per-track EventIndex below, while
                // the preview uses this identity to mark the point created from a
                // synthetic connector tap.
                if (previewNodesOut is not null)
                    tail.EventIndex = destinationRun[0].EventIndex;
                generated.Add(tail);
                points.Add(tail);

                if (destinationIsFinal)
                {
                    // The final selected lane's notes are represented by the
                    // duration of this tail; they no longer need separate taps.
                    absorbed.AddRange(destinationRun);
                }
                else
                {
                    // The generated tail already holds on this destination lane
                    // until the last selected tick in the run. That tick is the
                    // duration end, not another link point: the next transition
                    // creates a tail on the following lane at this same tick.
                    // Keeping an extra point here creates two tails at the same
                    // tick, and LinkBuilder's track ordering can reverse the path.
                    absorbed.AddRange(destinationRun);
                }
            }

            // Preserve the existing rule that a held note may not extend past a
            // later connection point. Generated tails already end exactly at the
            // next run endpoint, so this also catches authored holds that would be
            // shortened by LINK X.
            for (var i = 0; i < points.Count; i++)
            {
                var point = points[i];
                var nextTick = i + 1 < points.Count ? points[i + 1].Tick : int.MaxValue;
                if (point.Duration > 6 && durationSplitEvents?.Contains(point) != true &&
                    (point.NoteType == NoteType.SingleLong ||
                     ((point.NoteType is NoteType.LinkHead or NoteType.LinkTail) && GetEditorNextLinkPoint(point) is null)) &&
                    i + 1 < points.Count && nextTick < point.EndTick)
                {
                    _statusMessage = $"Cannot link: the hold on track {point.Track} ends at tick {point.EndTick}, after the next connection at tick {nextTick}. Shorten it first.";
                    return;
                }
            }

            var startTick = points[0].Tick;
            var hasLinkTailAtStart = generated.Any(ev => ev.Tick == startTick) ||
                                     (points.Count > 1 && points[1].Tick == startTick);
            var pairId = new[] { 1, 3, 5, 7 }.FirstOrDefault(candidate =>
                IsEditorLinkPairAvailable(grouping.Key, candidate, startTick, terminalEnd, selectedSet, hasLinkTailAtStart) &&
                !plans.Any(plan => !plan.VerticalOnly && plan.Pair == candidate &&
                    EditorLinkIntervalsConflict(
                        routeTimeline,
                        plan.Family, plan.StartTick, plan.EndTick,
                        plan.Generated.Any(ev => ev.Tick == plan.StartTick) ||
                        (plan.Points.Count > 1 && plan.Points[1].Tick == plan.StartTick),
                        grouping.Key, startTick, terminalEnd, hasLinkTailAtStart)));
            if (pairId == 0)
            {
                _statusMessage = "No safe link pair is available for the route combinations reached by this chart.";
                return;
            }

            // A generated tail must not overwrite an unselected note already
            // occupying its lane/tick.
            foreach (var generatedPoint in generated)
            {
                var conflict = _chart.Events.FirstOrDefault(ev => !selectedSet.Contains(ev) &&
                    ev.Kind == "Note" && ev.RawType == 1 && ev.Tick == generatedPoint.Tick &&
                    ev.Track == generatedPoint.Track);
                if (conflict is not null)
                {
                    _statusMessage = $"Cannot build LINK X: tick {generatedPoint.Tick}, track {generatedPoint.Track} is already occupied.";
                    return;
                }
            }

            plans.Add((grouping.Key, points, absorbed, generated, pairId, startTick, terminalEnd, false));
        }

        if (plans.Count == 0)
        {
            _statusMessage = "Select notes from one track family to build a link path.";
            return;
        }

        if (previewNodesOut is not null)
        {
            previewNodesOut.Clear();
            foreach (var plan in plans.Where(plan => !plan.VerticalOnly))
                AppendEditorLinkPreviewPoints(previewNodesOut, plan.Points, plan.Pair, plan.EndTick);
            return;
        }

        var undoSnapshot = CaptureEditorChartSnapshot();
        ChartEvent? firstRoot = null;
        var absorbedEvents = plans.SelectMany(plan => plan.Absorbed).ToHashSet();

        // Remove consumed taps first; then add synthetic horizontal points with
        // fresh per-track event indices so event ordering stays deterministic.
        foreach (var track in _chart.Tracks)
            track.Events.RemoveAll(ev => absorbedEvents.Contains(ev));
        foreach (var group in plans.SelectMany(plan => plan.Generated).GroupBy(ev => ev.Track))
        {
            var track = GetOrCreateEditorTrack(group.Key);
            var nextIndex = track.Events.Select(ev => ev.EventIndex).DefaultIfEmpty(-1).Max() + 1;
            foreach (var ev in group)
            {
                ev.EventIndex = nextIndex++;
                track.Events.Add(ev);
            }
        }

        foreach (var plan in plans)
        {
            if (plan.VerticalOnly)
            {
                var start = plan.Points[0];
                start.NoteType = NoteType.SingleLong;
                start.Attr = 0;
                start.LinePair = -1;
                start.TwinPairId = null;
                start.Duration = Math.Max(7, plan.EndTick - start.Tick);
                continue;
            }

            for (var i = 0; i < plan.Points.Count; i++)
            {
                var point = plan.Points[i];
                point.NoteType = i == 0 ? NoteType.LinkHead : NoteType.LinkTail;
                point.Attr = i == 0 ? plan.Pair : plan.Pair + 1;
                point.LinePair = plan.Pair;
                point.TwinPairId = null;
                if (i + 1 < plan.Points.Count)
                    point.Duration = Math.Max(6, plan.Points[i + 1].Tick - point.Tick);
                else
                    point.Duration = Math.Max(6, plan.EndTick - point.Tick);
                if (i == 0)
                    firstRoot ??= point;
            }
        }

        CommitEditorUndoSnapshot(undoSnapshot);
        RefreshAfterEditorMutation();
        foreach (var plan in plans.Where(plan => !plan.VerticalOnly))
            if (plan.Points.Count > 0 && plan.Points[0].NoteType == NoteType.LinkHead)
                SyncEditorLinkChainDurations(plan.Points[0]);
        RefreshAfterEditorMutation();

        ClearEditorMultiSelection();
        _selectedEventKey = firstRoot is not null ? KeyOf(firstRoot) : KeyOf(plans[0].Points[0]);
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorSelectedLinkRoot = firstRoot;
        _editorSelectedHoldEnd = false;
        var selectedRoot = firstRoot ?? plans[0].Points[0];
        _editorTrack = selectedRoot.Track;
        _statusMessage = plans.All(plan => plan.VerticalOnly)
            ? $"Converted selected vertical notes into {plans.Count} hold(s); no link tails were created."
            : plans.Count == 1
                ? $"Built a horizontal-first link path with {plans[0].Points.Count} point(s); vertical endpoints use duration."
                : $"Built {plans.Count} horizontal-first link paths; vertical endpoints use duration.";
    }

    private void ConvertEditorLinkPointToTapNote(ChartEvent ev)
    {
        ev.NoteType = NoteType.Single;
        ev.Attr = 0;
        ev.LinePair = -1;
        ev.Duration = 6;
        ev.TwinPairId = null;
        MoveEditorNoteToAllowedTrack(ev);
    }

    private void UnlinkSelectedEditorNotes(EditorUnlinkMode mode)
    {
        if (!_chartEditorMode || _chart is null)
        {
            _statusMessage = "Open the chart editor before unlinking notes.";
            return;
        }

        var selected = GetSelectedEditorNotes(expandLinkFamilies: false)
            .Where(ev => ev.NoteType is NoteType.LinkHead or NoteType.LinkTail)
            .ToHashSet();
        if (selected.Count == 0)
        {
            _statusMessage = "Select a point in a link chain to unlink it.";
            return;
        }

        var rootPlans = selected
            .Select(ev => GetEditorLinkRoot(ev) ?? (ev.NoteType == NoteType.LinkHead ? ev : null))
            .Where(root => root is not null)
            .Cast<ChartEvent>()
            .Distinct()
            .Select(root => (Root: root, Nodes: GetEditorLinkChainNodes(root)))
            .Where(item => item.Nodes.Count > 0)
            .ToList();
        if (rootPlans.Count == 0)
        {
            _statusMessage = "The selected note is not part of a link chain.";
            return;
        }

        var undoSnapshot = CaptureEditorChartSnapshot();
        var changedRoots = new List<ChartEvent>();
        var standaloneSelection = new List<ChartEvent>();
        foreach (var plan in rootPlans)
        {
            var root = plan.Root;
            var nodes = plan.Nodes;
            SyncEditorLinkChainDurations(root);
            var targets = nodes.Where(selected.Contains).ToHashSet();
            if (targets.Count == 0)
                continue;

            // ALL TAPS flattens only the affected chain(s), including materializing
            // the terminal duration end as a tap when the final point has a hold.
            // Unrelated notes and chains are left alone.
            if (mode == EditorUnlinkMode.AllTaps)
            {
                var terminal = nodes[^1];
                var terminalEndTick = terminal.EndTick;
                var hasTerminalDurationEnd = terminal.Duration > 6 && terminalEndTick > terminal.Tick;

                foreach (var node in nodes)
                {
                    ConvertEditorLinkPointToTapNote(node);
                    standaloneSelection.Add(node);
                }

                // The final link point's duration end is not a separate chart event.
                // Preserve it as a tap when flattening the whole chain to taps.
                if (hasTerminalDurationEnd && !_chart.Events.Any(ev =>
                        ev.Kind == "Note" && ev.RawType == 1 &&
                        ev.Tick == terminalEndTick && ev.Track == terminal.Track))
                {
                    var destination = GetOrCreateEditorTrack(terminal.Track);
                    var endpointTap = new ChartEvent
                    {
                        Tick = terminalEndTick,
                        Track = terminal.Track,
                        Attr = 0,
                        RawType = 1,
                        TypeIndex = terminal.TypeIndex,
                        Duration = 6,
                        NoteType = NoteType.Single,
                        EventIndex = destination.Events.Select(ev => ev.EventIndex).DefaultIfEmpty(-1).Max() + 1,
                        LinePair = -1,
                        TwinPairId = null,
                        LoopReconstructed = terminal.LoopReconstructed,
                        LoopPhase = terminal.LoopPhase,
                        Kind = "Note",
                        Value = terminal.Value,
                        Instrument = terminal.Instrument,
                        Velocity = terminal.Velocity,
                        Pan = terminal.Pan
                    };
                    destination.Events.Add(endpointTap);
                    standaloneSelection.Add(endpointTap);
                }
                continue;
            }

            var pair = root.LinePair is 1 or 3 or 5 or 7
                ? root.LinePair
                : root.Attr is 2 or 3 ? 1
                : root.Attr is 4 or 5 ? 3
                : root.Attr is 6 or 7 ? 5
                : root.Attr is 8 or 9 ? 7
                : 1;

            // Partition the chain into detached points and contiguous surviving runs.
            // A run with one point cannot be represented as a valid chain: convert it
            // to a standalone note instead of leaving an orphan LinkHead behind.
            var i = 0;
            while (i < nodes.Count)
            {
                var node = nodes[i];
                if (targets.Contains(node))
                {
                    if (mode == EditorUnlinkMode.Taps)
                        ConvertEditorLinkPointToTapNote(node);
                    else
                        ConvertEditorLinkPointToStandaloneNote(node);
                    standaloneSelection.Add(node);
                    i++;
                    continue;
                }

                var runStart = i;
                while (i < nodes.Count && !targets.Contains(nodes[i]))
                    i++;
                var runEnd = i;
                var runLength = runEnd - runStart;
                if (runLength == 1)
                {
                    var isolated = nodes[runStart];
                    if (mode == EditorUnlinkMode.Taps)
                        ConvertEditorLinkPointToTapNote(isolated);
                    else
                        ConvertEditorLinkPointToStandaloneNote(isolated);
                    standaloneSelection.Add(isolated);
                    continue;
                }

                for (var j = runStart; j < runEnd; j++)
                {
                    var survivingNode = nodes[j];
                    var beginsNewRun = j == runStart;
                    survivingNode.NoteType = beginsNewRun ? NoteType.LinkHead : NoteType.LinkTail;
                    survivingNode.Attr = beginsNewRun ? pair : pair + 1;
                    survivingNode.LinePair = pair;
                    survivingNode.TwinPairId = null;
                    changedRoots.Add(survivingNode);
                }
            }
        }

        if (standaloneSelection.Count == 0)
        {
            _statusMessage = "Select a chain point to unlink it.";
            return;
        }

        CommitEditorUndoSnapshot(undoSnapshot);
        RefreshAfterEditorMutation();
        foreach (var head in changedRoots.Where(ev => ev.NoteType == NoteType.LinkHead).Distinct().ToList())
            SyncEditorLinkChainDurations(head);
        RefreshAfterEditorMutation();

        ClearEditorMultiSelection();
        foreach (var note in standaloneSelection)
            AddEditorMultiSelectedEvent(note);
        var selectedNote = standaloneSelection.OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track).First();
        _selectedEventKey = KeyOf(selectedNote);
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorSelectedLinkRoot = null;
        _editorSelectedHoldEnd = false;
        _editorTrack = selectedNote.Track;
        _statusMessage = mode switch
        {
            EditorUnlinkMode.Taps => $"Unlinked {standaloneSelection.Count} point(s) as six-tick taps; other chain sections were preserved.",
            EditorUnlinkMode.AllTaps => $"Converted the affected chain into {standaloneSelection.Count} six-tick taps.",
            _ => standaloneSelection.Count == 1
                ? "Unlinked the selected point; the remaining chain sections were preserved."
                : $"Unlinked {standaloneSelection.Count} point(s); remaining chain sections were preserved."
        };
    }

    private static int MirrorEditorTrackX(int track)
    {
        var position = GearState.Position(track);
        if (position is null)
            return track;
        var (side, bank, local) = position.Value;
        var targetSide = side == "left" ? "right" : "left";
        var laneCount = bank == "Center" ? 2 : 3;
        var targetLocal = laneCount - 1 - local;
        return EditorTrackForBankPosition(targetSide, bank, targetLocal);
    }

    private static int MirrorEditorAttrX(int attr) => attr switch
    {
        10 => 50,
        50 => 10,
        20 => 40,
        40 => 20,
        60 => 70,
        70 => 60,
        80 => 90,
        90 => 80,
        _ => attr
    };

    // MIRROR BANK retains the former MIRROR X action: it swaps the selected notes
    // to the opposite side of the same bank and flips direction-based attributes.
    private void MirrorSelectedEditorNotesBank() => MirrorSelectedEditorNotesAcrossSides();

    // MIRROR X reflects notes around the horizontal center of the current selection,
    // rather than always reflecting around the center of the whole playfield.
    private void MirrorSelectedEditorNotesX() => MirrorSelectedEditorNotesWithinSelection();

    private List<ChartEvent> GetSelectedEditorNotesForMirror()
        => GetSelectedEditorNotes(expandLinkFamilies: true)
            .Where(ev => ev.Kind == "Note" && ev.RawType == 1 && ev.NoteType != NoteType.End &&
                         IsPlayableEditorTrack(ev.Track))
            .Distinct()
            .ToList();

    private List<NoteGroupInstance> GetCompleteSelectedEditorNoteGroupInstancesForMirror(
        IReadOnlyCollection<ChartEvent> selected)
    {
        if (_chart is null || _editorNoteGroupEditMode || selected.Count == 0)
            return new List<NoteGroupInstance>();

        var selectedSet = selected.ToHashSet();
        var result = new List<NoteGroupInstance>();
        foreach (var key in selected.Where(ev => ev.NoteGroupId.HasValue && ev.NoteGroupInstanceId.HasValue)
                     .Select(ev => (GroupId: ev.NoteGroupId!.Value, InstanceId: ev.NoteGroupInstanceId!.Value))
                     .Distinct())
        {
            var instance = _chart.NoteGroupInstances.FirstOrDefault(candidate =>
                candidate.GroupId == key.GroupId && candidate.InstanceId == key.InstanceId);
            if (instance is null)
                continue;

            var instanceNotes = _chart.Events.Where(ev => ev.Kind == "Note" && ev.RawType == 1 &&
                ev.NoteGroupId == key.GroupId && ev.NoteGroupInstanceId == key.InstanceId).ToList();
            if (instanceNotes.Count > 0 && instanceNotes.All(selectedSet.Contains))
                result.Add(instance);
        }
        return result;
    }

    private void ToggleEditorNoteGroupMirrorFlags(IEnumerable<NoteGroupInstance> instances)
    {
        if (_chart is null || _editorNoteGroupEditMode)
            return;

        foreach (var instance in instances)
        {
            var index = _chart.NoteGroupInstances.FindIndex(candidate =>
                candidate.GroupId == instance.GroupId && candidate.InstanceId == instance.InstanceId);
            if (index >= 0)
                _chart.NoteGroupInstances[index] = _chart.NoteGroupInstances[index] with
                {
                    IsMirrored = !_chart.NoteGroupInstances[index].IsMirrored
                };
        }
    }

    private void MirrorSelectedEditorNotesAcrossSides()
    {
        if (!_chartEditorMode || _chart is null)
        {
            _statusMessage = "Open the chart editor before mirroring notes.";
            return;
        }

        var selected = GetSelectedEditorNotesForMirror();
        if (selected.Count == 0)
        {
            _statusMessage = "Select one or more notes to mirror to the opposite side of their current bank.";
            return;
        }

        var undoSnapshot = CaptureEditorChartSnapshot();
        var mirroredGroupInstances = GetCompleteSelectedEditorNoteGroupInstancesForMirror(selected);
        var selectedAnchor = _selectedEventKey.HasValue
            ? selected.FirstOrDefault(ev => KeyOf(ev) == _selectedEventKey.Value)
            : null;
        var moving = selected.ToHashSet();
        var destinations = selected.ToDictionary(ev => ev, ev => GetEditorAllowedTrackForNoteType(
            MirrorEditorTrackX(ev.Track), ev.NoteType));

        foreach (var track in _chart.Tracks)
            track.Events.RemoveAll(ev => moving.Contains(ev));

        foreach (var group in selected.GroupBy(ev => destinations[ev]).OrderBy(group => group.Key))
        {
            var target = GetOrCreateEditorTrack(group.Key);
            var nextIndex = target.Events.Select(ev => ev.EventIndex).DefaultIfEmpty(-1).Max() + 1;
            foreach (var ev in group.OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track).ThenBy(ev => ev.EventIndex))
            {
                ev.Track = group.Key;
                ev.Attr = MirrorEditorAttrX(ev.Attr);
                ev.TwinPairId = null;
                ev.EventIndex = nextIndex++;
                target.Events.Add(ev);
            }
        }

        ToggleEditorNoteGroupMirrorFlags(mirroredGroupInstances);
        CommitEditorUndoSnapshot(undoSnapshot);
        RefreshAfterEditorMutation();
        ClearEditorMultiSelection();
        foreach (var ev in selected)
            AddEditorMultiSelectedEvent(ev);
        selectedAnchor ??= selected.OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track).First();
        _selectedEventKey = KeyOf(selectedAnchor);
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorSelectedLinkRoot = GetEditorLinkRoot(selectedAnchor);
        _editorSelectedHoldEnd = false;
        _editorTrack = selectedAnchor.Track;
        _statusMessage = $"Mirrored {selected.Count} note(s) to the opposite side within their current banks.";
    }

    private void MirrorSelectedEditorNotesWithinSelection()
    {
        if (!_chartEditorMode || _chart is null)
        {
            _statusMessage = "Open the chart editor before mirroring notes.";
            return;
        }

        var selected = GetSelectedEditorNotesForMirror();
        if (selected.Count == 0)
        {
            _statusMessage = "Select one or more notes to mirror around the center of the selection.";
            return;
        }

        // A complete group instance has one persistent mirror orientation. Use
        // the same canonical reflection for either mirror command so future
        // synchronized edits can invert the transform from that single flag.
        if (GetCompleteSelectedEditorNoteGroupInstancesForMirror(selected).Count > 0)
        {
            MirrorSelectedEditorNotesAcrossSides();
            return;
        }

        // A marquee includes empty lanes too. Use those lane positions to find the
        // selection center when available; otherwise use the tracks carrying the
        // selected notes. This makes a selection confined to one side of a 3K bank
        // mirror inside that side instead of jumping to the opposite side of the game.
        var selectionTracks = _editorSelectedGridCells
            .Select(cell => cell.Track)
            .Where(IsPlayableEditorTrack)
            .Concat(selected.Select(ev => ev.Track))
            .Distinct()
            .ToList();
        var selectionXs = selectionTracks.Select(track => _projection.LaneNormalizedX(track)).ToList();
        var minX = selectionXs.Min();
        var maxX = selectionXs.Max();
        var moving = selected.ToHashSet();
        var undoSnapshot = CaptureEditorChartSnapshot();
        var selectedAnchor = _selectedEventKey.HasValue
            ? selected.FirstOrDefault(ev => KeyOf(ev) == _selectedEventKey.Value)
            : null;
        var destinations = new Dictionary<ChartEvent, int>(selected.Count);

        for (var i = 0; i < selected.Count; i++)
        {
            var ev = selected[i];
            var sourcePosition = GearState.Position(ev.Track);
            if (sourcePosition is null)
            {
                destinations[ev] = ev.Track;
                continue;
            }

            var mirroredX = minX + maxX - _projection.LaneNormalizedX(ev.Track);
            var bestTrack = ev.Track;
            var bestDistance = float.MaxValue;
            var bestSameBank = false;
            for (var candidateTrack = 0; candidateTrack <= 15; candidateTrack++)
            {
                var candidatePosition = GearState.Position(candidateTrack);
                if (candidatePosition is null)
                    continue;
                var distance = MathF.Abs(_projection.LaneNormalizedX(candidateTrack) - mirroredX);
                var sameBank = string.Equals(candidatePosition.Value.Bank, sourcePosition.Value.Bank,
                    StringComparison.OrdinalIgnoreCase);
                if (distance < bestDistance - 0.00001f ||
                    (MathF.Abs(distance - bestDistance) <= 0.00001f && sameBank && !bestSameBank))
                {
                    bestTrack = candidateTrack;
                    bestDistance = distance;
                    bestSameBank = sameBank;
                }
            }

            destinations[ev] = GetEditorAllowedTrackForNoteType(bestTrack, ev.NoteType);
        }

        foreach (var track in _chart.Tracks)
            track.Events.RemoveAll(ev => moving.Contains(ev));

        foreach (var group in selected.GroupBy(ev => destinations[ev]).OrderBy(group => group.Key))
        {
            var target = GetOrCreateEditorTrack(group.Key);
            var nextIndex = target.Events.Select(ev => ev.EventIndex).DefaultIfEmpty(-1).Max() + 1;
            foreach (var ev in group.OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track).ThenBy(ev => ev.EventIndex))
            {
                ev.Track = group.Key;
                ev.Attr = MirrorEditorAttrX(ev.Attr);
                ev.TwinPairId = null;
                ev.EventIndex = nextIndex++;
                target.Events.Add(ev);
            }
        }

        CommitEditorUndoSnapshot(undoSnapshot);
        RefreshAfterEditorMutation();
        ClearEditorMultiSelection();
        foreach (var ev in selected)
            AddEditorMultiSelectedEvent(ev);
        selectedAnchor ??= selected.OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track).First();
        _selectedEventKey = KeyOf(selectedAnchor);
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorSelectedLinkRoot = GetEditorLinkRoot(selectedAnchor);
        _editorSelectedHoldEnd = false;
        _editorTrack = selectedAnchor.Track;
        _statusMessage = $"Mirrored {selected.Count} note(s) around the center of the selection.";
    }

    private void ArmEditorSuperFlickTool()
    {
        if (_editorSuperFlickTool == EditorSuperFlickTool.Place && !_editorSuperFlickPlacementPending)
        {
            _editorSuperFlickTool = EditorSuperFlickTool.None;
            _editorPlacementEnabled = false;
            _editorConnectorMode = false;
            _statusMessage = "SuperFlick tool off. Tap a lane to select a note or tick.";
        }
        else
        {
            CancelEditorSuperFlickPlacement(string.Empty, deactivateTool: true);
            _editorSuperFlickTool = EditorSuperFlickTool.Place;
            _editorPlacementEnabled = true;
            _editorConnectorMode = false;
            _editorNoteType = NoteType.Single;
            _editorAttr = 0;
            _editorDuration = 6;
            _editorMenuSection = EditorMenuSection.Notes;
            _statusMessage = "SuperFlick tool active: click a source tick/track, then choose a target bank on the same side. In collapsed view, hold the configured inactive-bank modifier to select an inactive bank.";
        }
        _editorSelectedTempoEvent = null;
        _editorHoverCacheValid = false;
    }

    private void CancelEditorSuperFlickPlacement(string message, bool deactivateTool = false)
    {
        _editorSuperFlickPlacementPending = false;
        _editorSuperFlickPendingTick = -1;
        _editorSuperFlickPendingTrack = -1;
        _editorSuperFlickPendingSide = null;
        _editorSuperFlickSourceBank = null;
        // Returning from the nested target-bank chooser returns to Notes rather
        // than closing the entire submenu. The user can close it via its header.
        if (_editorMenuSection == EditorMenuSection.SuperFlick)
            _editorMenuSection = EditorMenuSection.Notes;
        if (deactivateTool)
        {
            _editorSuperFlickTool = EditorSuperFlickTool.None;
            _editorPlacementEnabled = false;
        }
        else if (_editorSuperFlickTool == EditorSuperFlickTool.Place)
        {
            _editorPlacementEnabled = true;
        }
        _editorHoverCacheValid = false;
        if (!string.IsNullOrWhiteSpace(message))
            _statusMessage = message;
    }

    private void BeginEditorSuperFlickPlacement(int tick, int track)
    {
        if (_chart is null)
            return;
        var position = GearState.Position(track);
        if (position is null)
        {
            _statusMessage = "SuperFlick source must be on a lane.";
            return;
        }

        _editorSuperFlickPlacementPending = true;
        _editorSuperFlickPendingTick = tick;
        _editorSuperFlickPendingTrack = track;
        _editorSuperFlickPendingSide = position.Value.Side;
        _editorSuperFlickSourceBank = position.Value.Bank;
        _editorSelectedTempoEvent = null;
        _editorMenuSection = EditorMenuSection.SuperFlick;
        _editorPlacementEnabled = false;
        _selectedEventKey = null;
        _selectedBeatTick = tick;
        _selectedBeatTrack = track;
        _editorTrack = track;
        _editorHoverCacheValid = false;
        var mode = position.Value.Bank switch { "Forward" => "1K", "Center" => "2K", _ => "3K" };
        _statusMessage = $"SuperFlick source: tick {tick}, {position.Value.Side} {mode}. Choose the target bank on the {position.Value.Side} side.";
    }

    private void SelectEditorSuperFlickTarget(int buttonIndex)
    {
        if (buttonIndex < 0 || buttonIndex >= EditorSuperFlickToolLabels.Length)
            return;

        var targetBank = buttonIndex switch
        {
            0 => "Forward",
            1 => "Center",
            2 => "Backward",
            _ => string.Empty
        };
        SelectEditorSuperFlickTarget(targetBank);
    }

    private void SelectEditorSuperFlickTarget(string targetBank)
    {
        if (!_editorSuperFlickPlacementPending || _chart is null || _tempo is null ||
            string.IsNullOrEmpty(targetBank))
            return;

        var sourceBank = _editorSuperFlickSourceBank;
        var side = _editorSuperFlickPendingSide;
        var track = _editorSuperFlickPendingTrack;
        var tick = _editorSuperFlickPendingTick;
        if (string.IsNullOrEmpty(sourceBank) || string.IsNullOrEmpty(side) || track < 0 || tick < 0)
        {
            CancelEditorSuperFlickPlacement("SuperFlick placement cancelled: source point is missing.");
            return;
        }
        if (targetBank == sourceBank)
        {
            _statusMessage = $"Target bank must differ from source {BankModeLabel(sourceBank)}. Choose another bank or press Esc to cancel.";
            return;
        }
        if (!TryGetEditorSuperFlickAttr(sourceBank, targetBank, side, out var attr))
        {
            _statusMessage = "That source-to-target bank path is not supported.";
            return;
        }

        // Editor placement describes the authored source bank, not whether that
        // bank happens to be active in the current route at this tick. Inactive
        // banks are valid authoring targets; route validation remains a gameplay
        // concern handled by GearTimeline when the chart is played.
        var sourcePosition = GearState.Position(track);
        if (sourcePosition is null ||
            !string.Equals(sourcePosition.Value.Side, side, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(sourcePosition.Value.Bank, sourceBank, StringComparison.OrdinalIgnoreCase))
        {
            _statusMessage = "SuperFlick source/target bank does not match the selected lane.";
            return;
        }

        PlaceEditorSuperFlick(tick, track, attr);
    }

    private static bool TryGetEditorSuperFlickAttr(string sourceBank, string targetBank, string side, out int attr)
    {
        var direction = (sourceBank, targetBank) switch
        {
            ("Forward", "Backward") or ("Backward", "Forward") => side == "left" ? 2 : 4,
            // 1K/3K -> 2K uses attr 70 on the left and attr 60 on the right.
            ("Forward", "Center") => side == "left" ? 7 : 6,
            ("Backward", "Center") => side == "left" ? 7 : 6,
            // 2K -> 1K uses the bank-aware 60/70 attributes.
            ("Center", "Forward") => side == "left" ? 6 : 7,
            // The distinct 80/90 attributes represent outer 2K -> 3K.
            ("Center", "Backward") => side == "left" ? 8 : 9,
            _ => 0
        };
        attr = direction * 10;
        return direction != 0;
    }

    private static string BankModeLabel(string bank) => bank switch
    {
        "Forward" => "1K",
        "Center" => "2K",
        "Backward" => "3K",
        _ => bank
    };

    private static string GetEditorSuperFlickTargetLabel(ChartEvent ev)
    {
        var sourceBank = ev.GetSuperFlickSourceBank();
        var targetBank = (ev.Attr, sourceBank) switch
        {
            (20 or 40, "Forward") => "Backward",
            (20 or 40, "Backward") => "Forward",
            (60 or 70, "Forward") => "Center",
            (60 or 70, "Backward") => "Center",
            (60 or 70, "Center") => "Forward",
            (80 or 90, _) => "Backward",
            _ => null
        };

        if (!string.IsNullOrEmpty(targetBank))
            return BankModeLabel(targetBank);

        // Invalid placements remain selectable for debugging, but their source
        // bank cannot define one exact destination from the authored path.
        return ev.Attr switch
        {
            20 or 40 => "1K ↔ 3K path (invalid source bank)",
            60 or 70 => "1K/2K/3K path (invalid source bank)",
            80 or 90 => "3K",
            _ => "unknown"
        };
    }

    private bool HasSuperFlickBankCollision(int tick, int trackIndex, ISet<ChartEvent>? ignoredEvents = null)
    {
        if (_chart is null)
            return false;

        var targetPosition = GearState.Position(trackIndex);
        if (targetPosition is null)
            return false;

        return _chart.Events.Any(existing =>
        {
            if (!existing.IsSuperFlick || existing.Track is < 0 or > 15 || existing.Tick != tick ||
                ignoredEvents?.Contains(existing) == true)
                return false;

            var existingPosition = GearState.Position(existing.Track);
            return existingPosition is not null &&
                   string.Equals(existingPosition.Value.Side, targetPosition.Value.Side, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(existingPosition.Value.Bank, targetPosition.Value.Bank, StringComparison.OrdinalIgnoreCase);
        });
    }

    private void PlaceEditorSuperFlick(int tick, int trackIndex, int attr)
    {
        if (_chart is null)
            return;
        if (!IsEditorTrackAllowedForNoteType(trackIndex, NoteType.Single))
        {
            _statusMessage = "In 1K, L1/L3/R1/R3 are reserved for link tails. Place SuperFlicks on L2/R2.";
            CancelEditorSuperFlickPlacement(_statusMessage);
            return;
        }
        if (FindEditorNoteAtGridCell(tick, trackIndex) is not null)
        {
            _statusMessage = "SuperFlick placement cancelled: that tick/track is occupied.";
            CancelEditorSuperFlickPlacement(_statusMessage);
            return;
        }

        var targetPosition = GearState.Position(trackIndex);
        if (targetPosition is null)
        {
            _statusMessage = "SuperFlick placement cancelled: the source lane is invalid.";
            CancelEditorSuperFlickPlacement(_statusMessage);
            return;
        }
        if (HasSuperFlickBankCollision(tick, trackIndex))
        {
            _statusMessage = $"SuperFlick placement cancelled: the {targetPosition.Value.Side} {BankModeLabel(targetPosition.Value.Bank)} bank already has a SuperFlick at tick {tick}.";
            CancelEditorSuperFlickPlacement(_statusMessage);
            return;
        }

        var undoSnapshot = CaptureEditorChartSnapshot();
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
            Duration = 6,
            NoteType = NoteType.Single,
            EventIndex = track.Events.Count == 0 ? 0 : track.Events.Max(e => e.EventIndex) + 1,
            LinePair = -1,
            Kind = "Note",
            Instrument = 0,
            Velocity = 127,
            Pan = 64
        };
        track.Events.Add(ev);
        CancelEditorSuperFlickPlacement($"Placed SuperFlick attr {attr}: tick {tick}, track {trackIndex}.");
        _editorSuperFlickTool = EditorSuperFlickTool.Place;
        _editorPlacementEnabled = true;
        _editorNoteType = NoteType.Single;
        _editorAttr = 0;
        _editorDuration = 6;
        _selectedEventKey = KeyOf(ev);
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorTrack = trackIndex;
        _editorSelectedLinkRoot = null;
        CommitEditorUndoSnapshot(undoSnapshot);
        RefreshAfterEditorMutation();
        _selectedEventKey = KeyOf(ev);
        _statusMessage = $"Placed SuperFlick attr {attr}: tick {tick}, track {trackIndex}. Click another source point or select another tool.";
    }

    private static bool IsEditorDraggableNote(ChartEvent ev)
        => ev.Kind == "Note" && ev.RawType == 1 && ev.NoteType != NoteType.End;

    private ChartEvent? GetEditorLinkRoot(ChartEvent ev)
    {
        if (ev.NoteType is not (NoteType.LinkHead or NoteType.LinkTail))
            return null;

        // During EDIT GROUP, never resolve a member through the chart-wide graph.
        // Pair IDs may be reused by other group instances at different time ranges,
        // and a stale/global predecessor can make a valid local tail appear to
        // belong to another chain. Resolve the member only inside its active copy.
        if (_editorNoteGroupEditMode && _editorEditingNoteGroupId is int groupId &&
            _editorEditingNoteGroupInstanceId is int instanceId &&
            ev.NoteGroupId == groupId && ev.NoteGroupInstanceId == instanceId)
        {
            EnsureEditorSelectionGroupChainCache(groupId, instanceId);
            return _editorSelectionGroupChainByMember.TryGetValue(ev, out var localChain)
                ? localChain.Root
                : ev.NoteType == NoteType.LinkHead ? ev : null;
        }

        return FindLinkRootHead(ev) ?? (ev.NoteType == NoteType.LinkHead ? ev : null);
    }

    private ChartEvent? GetEditorPreferredLinkRoot(ChartEvent ev)
    {
        // A cached selected root can outlive an EDIT GROUP mode/instance change.
        // For members of the active instance, the freshly resolved local root is
        // authoritative, including null for an orphaned tail.
        if (_editorNoteGroupEditMode && _editorEditingNoteGroupId is int groupId &&
            _editorEditingNoteGroupInstanceId is int instanceId &&
            ev.NoteGroupId == groupId && ev.NoteGroupInstanceId == instanceId)
            return GetEditorLinkRoot(ev);

        return _editorSelectedLinkRoot ?? GetEditorLinkRoot(ev);
    }

    private List<ChartEvent> GetEditorLinkChainNodes(ChartEvent root)
    {
        var nodes = new List<ChartEvent> { root };
        if (_chart is null || root.NoteType != NoteType.LinkHead)
            return nodes;

        // While editing a group instance, physical LinkPair IDs may be reused by
        // sibling instances. Resolve the complete logical chain once per active
        // instance and reuse that mapping for every selected member.
        if (_editorNoteGroupEditMode && _editorEditingNoteGroupId is int groupId &&
            _editorEditingNoteGroupInstanceId is int instanceId &&
            root.NoteGroupId == groupId && root.NoteGroupInstanceId == instanceId)
        {
            EnsureEditorSelectionGroupChainCache(groupId, instanceId);
            return _editorSelectionGroupChainByMember.TryGetValue(root, out var localChain) &&
                   ReferenceEquals(localChain.Root, root)
                ? localChain.Nodes
                : nodes;
        }

        var visited = new HashSet<ChartEvent> { root };
        var current = root;
        for (var guard = 0; guard < 256; guard++)
        {
            var currentKey = KeyOf(current);
            if (_ambiguousNextLinkHeadKeys.Contains(currentKey) ||
                !_nextLinkByHeadKey.TryGetValue(currentKey, out var nextLink))
                break;

            var next = nextLink.Tail;
            if (!visited.Add(next))
                break;
            nodes.Add(next);
            current = next;
        }
        return nodes;
    }

    private ChartEvent? GetEditorPreviousLinkPoint(ChartEvent point)
    {
        if (_chart is null)
            return null;
        if (_editorNoteGroupEditMode && _editorEditingNoteGroupId is int groupId &&
            _editorEditingNoteGroupInstanceId is int instanceId &&
            point.NoteGroupId == groupId && point.NoteGroupInstanceId == instanceId)
        {
            if (!TryGetActiveEditorGroupChain(point, out var chain))
                return null;
            var index = chain.Nodes.FindIndex(node => ReferenceEquals(node, point));
            return index > 0 ? chain.Nodes[index - 1] : null;
        }
        var incoming = _chart.Links
            .Where(link => ReferenceEquals(link.Tail, point))
            .ToList();
        if (incoming.Count == 0)
            return null;

        var latestHeadTick = incoming.Max(link => link.Head.Tick);
        var candidates = incoming.Where(link => link.Head.Tick == latestHeadTick).ToList();
        return candidates.Count == 1 ? candidates[0].Head : null;
    }

    private ChartEvent? GetEditorNextLinkPoint(ChartEvent point)
    {
        if (_chart is null || point.NoteType is not (NoteType.LinkHead or NoteType.LinkTail))
            return null;
        if (_editorNoteGroupEditMode && _editorEditingNoteGroupId is int groupId &&
            _editorEditingNoteGroupInstanceId is int instanceId &&
            point.NoteGroupId == groupId && point.NoteGroupInstanceId == instanceId)
        {
            if (!TryGetActiveEditorGroupChain(point, out var chain))
                return null;
            var index = chain.Nodes.FindIndex(node => ReferenceEquals(node, point));
            return index >= 0 && index + 1 < chain.Nodes.Count ? chain.Nodes[index + 1] : null;
        }

        var pointKey = KeyOf(point);
        if (_ambiguousNextLinkHeadKeys.Contains(pointKey) ||
            !_nextLinkByHeadKey.TryGetValue(pointKey, out var nextLink))
            return null;
        return nextLink.Tail;
    }

    private ChartEvent GetEditorLinkChainEnd(ChartEvent point)
    {
        var root = GetEditorLinkRoot(point);
        if (root is null || root.NoteType != NoteType.LinkHead)
            return point;

        var nodes = GetEditorLinkChainNodes(root);
        return nodes.Count > 0 ? nodes[^1] : point;
    }

    // Every connected link point owns the hold segment up to the next point.
    // Keeping the authored duration in sync makes editing/rebuilding the chart
    // preserve the same chain instead of allowing the next load to regroup it.
    private void SyncEditorLinkChainDurations(ChartEvent? root)
    {
        if (_chart is null || root is null || root.NoteType != NoteType.LinkHead)
            return;

        var nodes = GetEditorLinkChainNodes(root);
        for (var i = 0; i + 1 < nodes.Count; i++)
        {
            var current = nodes[i];
            var next = nodes[i + 1];
            // The original format supports a horizontal (same-tick) link across
            // different tracks. Keep the default six-tick metadata for that case.
            current.Duration = Math.Max(6, next.Tick - current.Tick);
        }
    }

    // Returns the last tick occupied by a note's visible body for editor hit-testing.
    // In the chart format, duration 6 is the default metadata value for a point with
    // no hold body. This matters especially for a terminal LinkTail: it has an
    // incoming horizontal connector, but it does not occupy the next six ticks on
    // its own lane unless its duration is greater than 6. Non-terminal link points
    // instead own the source-lane body up to their next connected point.
    private int GetEditorNoteHitEndTick(ChartEvent ev)
    {
        if (ev.NoteType is NoteType.LinkHead or NoteType.LinkTail)
        {
            var nextPoint = GetEditorNextLinkPoint(ev);
            if (nextPoint is not null)
                return Math.Max(ev.Tick, nextPoint.Tick);

            return ev.Duration > 6 ? ev.EndTick : ev.Tick;
        }

        return ev.IsHoldNote && ev.Duration > 6 ? ev.EndTick : ev.Tick;
    }

    // Returns a note already occupying the snapped editor cell. Long notes and
    // link segments occupy the source lane through their visible endpoint, not
    // just their note-head tick, so placement cannot stack on their actual body.
    // The default six-tick duration on a terminal LinkTail is not a body.
    private ChartEvent? FindEditorNoteAtGridCell(int tick, int track)
    {
        if (_chart is null)
            return null;

        // Hover resolution calls this while the playhead is moving. Keep the
        // occupied-cell search allocation-free rather than building an ordered
        // LINQ pipeline every frame.
        ChartEvent? best = null;
        var bestExactTick = false;
        var bestRawNote = false;
        foreach (var ev in _chart.Events)
        {
            if (ev.Kind != "Note" || ev.Track != track)
                continue;

            var exactTick = ev.Tick == tick;
            var hitEndTick = GetEditorNoteHitEndTick(ev);
            var occupiesCell = exactTick ||
                (hitEndTick > ev.Tick && ev.Tick <= tick && tick <= hitEndTick);
            if (!occupiesCell)
                continue;

            var rawNote = ev.RawType == 1;
            if (best is null ||
                (exactTick && !bestExactTick) ||
                (exactTick == bestExactTick && rawNote && !bestRawNote) ||
                (exactTick == bestExactTick && rawNote == bestRawNote &&
                 (ev.Tick < best.Tick || (ev.Tick == best.Tick && ev.EventIndex < best.EventIndex))))
            {
                best = ev;
                bestExactTick = exactTick;
                bestRawNote = rawNote;
            }
        }
        return best;
    }

    private int GetEditorHoverMarkerTrack(int track)
    {
        // Keep the hover point, occupied-cell lookup, and placement destination
        // identical. Connector and group placement retain the literal lane because
        // their target layouts are resolved by their own tools.
        if (_editorConnectorMode || _editorNoteGroupPlacementArmed || _editorNoteGroupEditMode ||
            !_editorPlacementEnabled)
            return track;
        return GetEditorAllowedTrackForNoteType(track,
            _editorSuperFlickTool == EditorSuperFlickTool.Place ? NoteType.Single : _editorNoteType);
    }

    private EditorHoverHit? ResolveEditorHoverHit(
        Vector2 pointer, int? gridTick, int gridTrack,
        double currentTick, GearState currentState)
    {
        // Share the renderer's spatially culled event window across all hover
        // hit-tests. The marker is refreshed while playback scrolls, so scanning
        // the complete chart independently for each hit type would be wasteful.
        var candidateCount = BuildRenderEventIndexScratch(currentTick);
        var candidates = _renderEventIndexScratch;
        var linkHead = FindClickedLinkHeadMarker(
            pointer, currentTick, currentState, candidates, candidateCount);
        if (linkHead is not null)
            return new EditorHoverHit(linkHead, EditorHoverHitKind.LinkHeadMarker,
                linkHead.Tick, linkHead.Track);

        var holdEnd = FindClickedHoldEnd(pointer, currentTick, currentState, candidates, candidateCount);
        if (holdEnd is not null)
        {
            var endpointTick = GetEditorNextLinkPoint(holdEnd)?.Tick ?? holdEnd.EndTick;
            return new EditorHoverHit(holdEnd, EditorHoverHitKind.HoldEndMarker,
                endpointTick, holdEnd.Track);
        }

        var noteCard = FindClickedNote(pointer, currentTick, currentState, out _, candidates, candidateCount);
        if (noteCard is not null)
            return new EditorHoverHit(noteCard, EditorHoverHitKind.NoteCard,
                noteCard.Tick, noteCard.Track);

        if (gridTick is int tick && gridTrack is >= 0 and <= 15)
        {
            // Placement already refuses to put a note into an occupied snapped
            // cell. Resolve that exact same occupancy before rendering the hover
            // marker, so the marker turns yellow instead of promising a placement.
            var occupied = FindEditorNoteAtGridCell(tick, gridTrack);
            if (occupied is not null && ShouldRenderInCurrentBank(occupied, currentState))
                return new EditorHoverHit(occupied, EditorHoverHitKind.GridCellNote, tick, gridTrack);

            // Link connector bodies are selectable only where the visible grid
            // hover marker itself lands on the connector, not merely where the raw
            // pointer enters a generous invisible halo around it.
            var gridPoint = _projection.ProjectAtTicks(gridTrack, tick, currentTick, currentState);
            if (!OutsideNoteView(gridPoint, gridPoint))
            {
                var linkBody = FindClickedLinkChainBody(gridPoint.Position, currentTick, currentState, tick);
                if (linkBody is not null)
                    return new EditorHoverHit(linkBody, EditorHoverHitKind.LinkBody, tick, gridTrack);
            }
        }

        return null;
    }

    private ChartEvent? FindClickedLinkChainBody(
        Vector2 point, double currentTick, GearState currentState, int? snappedHoverTick = null)
    {
        if (_chart is null || (_chart.Links.Count == 0 && !_editorNoteGroupEditMode))
            return null;

        ChartEvent? bestNode = null;
        var bestDistance = float.MaxValue;
        var hitDistance = Math.Max(6f, _projection.ScalePixels(10f));
        IEnumerable<(ChartEvent Head, ChartEvent Tail)> hitEdges;
        if (_editorNoteGroupEditMode && _editorEditingNoteGroupId is int groupId &&
            _editorEditingNoteGroupInstanceId is int instanceId)
        {
            EnsureEditorSelectionGroupChainCache(groupId, instanceId);
            hitEdges = _editorSelectionGroupChains
                .SelectMany(chain => Enumerable.Range(0, Math.Max(0, chain.Nodes.Count - 1))
                    .Select(index => (Head: chain.Nodes[index], Tail: chain.Nodes[index + 1])));
        }
        else
        {
            hitEdges = _chart.Links.Select(link => (Head: link.Head, Tail: link.Tail));
        }

        foreach (var link in hitEdges)
        {
            if (!ShouldRenderInCurrentBank(link.Head, currentState) ||
                !ShouldRenderInCurrentBank(link.Tail, currentState))
                continue;
            if (link.Head.NoteType != NoteType.LinkHead && link.Head.NoteType != NoteType.LinkTail)
                continue;
            if (link.Tail.NoteType != NoteType.LinkTail)
                continue;

            // A same-tick edge is a horizontal connector only. It has no body
            // extending into later ticks on the terminal LinkTail's lane. Keep the
            // screen-space connector radius from bleeding into the next snapped
            // cell (especially +6 ticks, where a default-duration LinkTail must be
            // treated as a point rather than a six-tick hold).
            if (snappedHoverTick.HasValue && link.Head.Tick == link.Tail.Tick &&
                snappedHoverTick.Value != link.Tail.Tick)
                continue;

            var head = _projection.ProjectAtTicks(link.Head.Track, link.Head.Tick, currentTick, currentState);
            var bodyEnd = _projection.ProjectAtTicks(
                link.Head.Track, link.Tail.Tick, currentTick, currentState);
            var tail = _projection.ProjectAtTicks(link.Tail.Track, link.Tail.Tick, currentTick, currentState);
            if (OutsideNoteView(head, bodyEnd) && OutsideNoteView(bodyEnd, tail))
                continue;

            // DrawLinkChainUnified does not render a straight diagonal from one
            // note point to the next. It draws the segment body down the source
            // lane, then a separate connector across the lanes at the destination
            // tick. Hit-test those same two pieces; the old endpoint-to-endpoint
            // diagonal could make an empty middle cell appear occupied (notably at
            // the midpoint of a zig-zag chain).
            var bodyDistance = OutsideNoteView(head, bodyEnd)
                ? float.MaxValue
                : PointToSegmentDistance(point, head.Position, bodyEnd.Position);
            var connectorDistance = OutsideNoteView(bodyEnd, tail)
                ? float.MaxValue
                : PointToSegmentDistance(point, bodyEnd.Position, tail.Position);
            var distance = Math.Min(bodyDistance, connectorDistance);
            if (distance > hitDistance || distance >= bestDistance)
                continue;

            // The lane body belongs to its source note. The cross-lane connector
            // selects its nearest endpoint, while the whole chain remains selected.
            bestNode = bodyDistance <= connectorDistance
                ? link.Head
                : Vector2.DistanceSquared(point, head.Position) <= Vector2.DistanceSquared(point, tail.Position)
                    ? link.Head
                    : link.Tail;
            bestDistance = distance;
        }
        return bestNode;
    }

    private void BeginEditorDrag(ChartEvent ev, bool holdEnd, bool startedByTouch,
        Vector2 pointerPoint, double currentTick, GearState currentState)
    {
        _editorDraggedEvent = ev;
        _editorDragUndoSnapshot = CaptureEditorChartSnapshot();
        _editorDragIsHoldEnd = holdEnd;
        _editorSelectedHoldEnd = holdEnd;
        _editorDragStartedByTouch = startedByTouch;
        _editorDragChanged = false;
        _editorDragOriginalTick = ev.Tick;
        _editorDragOriginalTrack = ev.Track;
        _editorDragOriginalDuration = ev.Duration;
        _editorDragOriginalEventIndex = ev.EventIndex;
        _editorDragOriginalNoteType = ev.NoteType;
        _editorDragOriginalAttr = ev.Attr;
        _editorDragOriginalLinePair = ev.LinePair;
        _editorDragLastTick = ev.Tick;
        _editorDragLastTrack = ev.Track;
        _editorDragLastDuration = ev.Duration;
        _editorDragMovedEndpoint = holdEnd ? GetEditorNextLinkPoint(ev) : null;
        _editorDragEndpointOriginalTick = _editorDragMovedEndpoint?.Tick ?? -1;
        _editorDragMovedChainAcrossBanks = false;
        _editorDragChainNodes.Clear();
        var dragChainRoot = GetEditorPreferredLinkRoot(ev);
        if (!holdEnd && dragChainRoot is not null && dragChainRoot.NoteType == NoteType.LinkHead)
            _editorDragChainNodes.AddRange(GetEditorLinkChainNodes(dragChainRoot).Distinct());

        _editorMultiDragItems.Clear();
        _editorDragIsMultiSelection = !holdEnd && _editorMultiSelectedEvents.Count > 1 &&
                                      _editorMultiSelectedEvents.Contains(ev) && _chart is not null;
        if (_editorDragIsMultiSelection)
        {
            // Link/Unlink deliberately leave their resulting chain selected. If
            // that selection is exactly one complete chain, use the chain-aware
            // bank remapper instead of the generic multi-note track offset. This
            // is what lets a linked chain cross to the opposite side while all
            // of its points stay inside the same physical bank.
            var draggedRoot = GetEditorLinkRoot(ev);
            if (draggedRoot is not null && draggedRoot.NoteType == NoteType.LinkHead)
            {
                var selectedLinkRoots = _editorMultiSelectedEvents
                    .Select(GetEditorLinkRoot)
                    .Where(root => root is not null)
                    .Cast<ChartEvent>()
                    .Distinct()
                    .ToArray();
                var selectionIsOnlyThisChain = selectedLinkRoots.Length == 1 &&
                    ReferenceEquals(selectedLinkRoots[0], draggedRoot) &&
                    _editorMultiSelectedEvents.All(selected => ReferenceEquals(GetEditorLinkRoot(selected), draggedRoot));
                if (selectionIsOnlyThisChain)
                    _editorDragIsMultiSelection = false;
            }
        }
        if (_editorDragIsMultiSelection && _chart is not null)
        {
            var movingSet = new HashSet<ChartEvent>();
            foreach (var selected in _editorMultiSelectedEvents.Distinct())
            {
                if (_chart.Events.Any(candidate => ReferenceEquals(candidate, selected)) && IsEditorDraggableNote(selected))
                    movingSet.Add(selected);

                var selectedRoot = GetEditorLinkRoot(selected);
                if (selectedRoot is not null && selectedRoot.NoteType == NoteType.LinkHead)
                {
                    foreach (var chainNode in GetEditorLinkChainNodes(selectedRoot))
                        if (_chart.Events.Any(candidate => ReferenceEquals(candidate, chainNode)) && IsEditorDraggableNote(chainNode))
                            movingSet.Add(chainNode);
                }
            }
            foreach (var selected in movingSet.OrderBy(node => node.Track).ThenBy(node => node.Tick).ThenBy(node => node.EventIndex))
            {
                _editorMultiDragItems.Add(new EditorMultiDragItem(
                    selected, selected.Tick, selected.Track, selected.Duration, selected.EventIndex));
            }
            _editorDragIsMultiSelection = _editorMultiDragItems.Count > 1;
        }
        if (_editorDragIsMultiSelection)
        {
            // Anchor the shared movement to the pointer's snapped cell at press
            // time, not to the note head. This avoids the whole selection jumping
            // when a note is grabbed near its edge or a little way down its body.
            var pointerTick = FindClickedQuarterBeat(pointerPoint, currentTick, currentState, out var pointerTrack);
            _editorMultiDragAnchorTick = pointerTick ?? ev.Tick;
            _editorMultiDragAnchorTrack = pointerTick.HasValue ? pointerTrack : ev.Track;
            _editorMultiDragLastTickDelta = 0;
            _editorMultiDragLastTrackDelta = 0;
        }
        else
        {
            _editorMultiDragItems.Clear();
        }
    }

    private ChartEvent? FindClickedLinkHeadMarker(
        Vector2 point, double currentTick, GearState currentState,
        IReadOnlyList<int>? candidateIndices = null, int candidateCount = -1)
    {
        if (!_chartEditorMode || _chart is null || _tempo is null)
            return null;

        ChartEvent? best = null;
        var bestDistance = float.MaxValue;
        var count = candidateIndices is null
            ? _orderedRenderableEvents.Count
            : Math.Clamp(candidateCount, 0, candidateIndices.Count);
        for (var candidateIndex = 0; candidateIndex < count; candidateIndex++)
        {
            var eventIndex = candidateIndices is null ? candidateIndex : candidateIndices[candidateIndex];
            var ev = _orderedRenderableEvents[eventIndex];
            if (ev.NoteType != NoteType.LinkHead || !IsEditorDraggableNote(ev) ||
                !ShouldRenderInCurrentBank(ev, currentState))
                continue;

            var projected = _projection.ProjectAtTicks(ev.Track, ev.Tick, currentTick, currentState);
            if (OutsideNoteView(projected, projected))
                continue;
            var radius = GetEditorMarkerRadius(ev.Track, ev.Tick, currentTick, currentState, out var center, ev);
            var distance = Vector2.Distance(point, center);
            var visibleOutline = Math.Clamp(radius * 0.16f, 1f, 2.25f);
            if (distance <= radius + visibleOutline + 1f && distance < bestDistance)
            {
                best = ev;
                bestDistance = distance;
            }
        }
        return best;
    }

    private ChartEvent? FindClickedHoldEnd(
        Vector2 point, double currentTick, GearState currentState,
        IReadOnlyList<int>? candidateIndices = null, int candidateCount = -1)
    {
        if (!_chartEditorMode || _chart is null || _tempo is null)
            return null;

        ChartEvent? best = null;
        var bestDistance = float.MaxValue;
        var count = candidateIndices is null
            ? _orderedRenderableEvents.Count
            : Math.Clamp(candidateCount, 0, candidateIndices.Count);
        for (var candidateIndex = 0; candidateIndex < count; candidateIndex++)
        {
            var eventIndex = candidateIndices is null ? candidateIndex : candidateIndices[candidateIndex];
            var ev = _orderedRenderableEvents[eventIndex];
            if (!IsEditorDraggableNote(ev) || !ShouldRenderInCurrentBank(ev, currentState))
                continue;
            if (ev.NoteType is not (NoteType.SingleLong or NoteType.Vibrato or NoteType.LinkHead or NoteType.LinkTail))
                continue;

            var nextPoint = GetEditorNextLinkPoint(ev);
            // When the next link point is on this same track, its note marker and
            // this segment's duration-end marker occupy the same position. Give
            // the actual LinkTail priority so it can be dragged to repair an
            // accidental consecutive-vertical connection. The tail's tick still
            // controls this segment's vertical endpoint, so no edit capability is
            // lost by selecting the point itself.
            if (nextPoint is not null && nextPoint.Track == ev.Track)
                continue;
            // A connected point's hold end is the next LinkTail's tick, even
            // though its own Duration may not yet have been synchronized.
            if (nextPoint is null && ev.Duration <= 6)
                continue;
            var endpointTick = nextPoint?.Tick ?? ev.EndTick;
            // Link-body duration ends are drawn on the source point's lane, at the
            // next point's tick. Only the separate destination marker uses its own
            // track; testing the destination lane here makes the tail-end handle
            // impossible to click for cross-lane chains.
            var endpointTrack = ev.Track;
            var endpointTravel = _projection.ScrollPixelsAtTicks(endpointTick, currentTick);
            if (!_projection.WithinRenderDistance((float)endpointTravel))
                continue;

            var radius = GetEditorMarkerRadius(
                endpointTrack, endpointTick, currentTick, currentState, out var endpointCenter, ev);
            var distance = Vector2.Distance(point, endpointCenter);
            // Match the visible endpoint highlighter. Its outline can extend slightly
            // beyond the radius, but the hit area must not reach far into the link body
            // or nearby note heads.
            var hitRadius = radius + Math.Clamp(radius * 0.16f, 1f, 2.25f) + 1f;
            if (distance <= hitRadius && distance < bestDistance)
            {
                best = ev;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static int EditorTrackForBankPosition(string side, string bank, int localPosition)
    {
        var local = Math.Clamp(localPosition, 0, bank == "Center" ? 1 : 2);
        return (side, bank) switch
        {
            ("left", "Forward") => local,
            ("right", "Forward") => 3 + local,
            ("left", "Backward") => 6 + local,
            ("right", "Backward") => 9 + local,
            ("left", "Center") => 12 + local,
            ("right", "Center") => 14 + local,
            _ => -1
        };
    }

    private static int ScaleEditorBankLane(int localPosition, int sourceCount, int targetCount)
    {
        if (sourceCount <= 1 || targetCount <= 1)
            return 0;
        return Math.Clamp((int)Math.Round(localPosition * (targetCount - 1.0) / (sourceCount - 1.0), MidpointRounding.AwayFromZero), 0, targetCount - 1);
    }

    // PASTE FIT compresses lane coordinates when moving a wider bank into a
    // narrower one. At an exact midpoint, bias the left side toward the right
    // (higher local index) and the right side toward the left (lower local
    // index). The two physical sides are mirrored, so they must not share the
    // same midpoint rounding direction.
    private static int ScaleEditorBankLaneForPasteFit(
        int localPosition, int sourceCount, int targetCount, string side)
    {
        if (sourceCount <= 1 || targetCount <= 1)
            return 0;

        var scaled = Math.Clamp(localPosition, 0, sourceCount - 1) *
                     (targetCount - 1.0) / (sourceCount - 1.0);
        var lower = (int)Math.Floor(scaled);
        var fraction = scaled - lower;
        int mapped;
        if (Math.Abs(fraction - 0.5) <= 0.000000001)
        {
            mapped = string.Equals(side, "right", StringComparison.OrdinalIgnoreCase)
                ? lower
                : lower + 1;
        }
        else
        {
            mapped = (int)Math.Round(scaled, MidpointRounding.AwayFromZero);
        }

        return Math.Clamp(mapped, 0, targetCount - 1);
    }

    private bool TryMoveEditorLinkChainToBank(ChartEvent root, ChartEvent draggedEvent, int destinationTrack)
    {
        if (_chart is null)
            return false;

        int OriginalTrack(ChartEvent node)
            => _editorDragUndoSnapshot?.Events.FirstOrDefault(snapshot => ReferenceEquals(snapshot.Event, node))?.Track
               ?? node.Track;

        var sourceTrack = OriginalTrack(draggedEvent);
        var sourcePosition = GearState.Position(sourceTrack);
        var targetPosition = GearState.Position(destinationTrack);
        if (sourcePosition is null || targetPosition is null)
        {
            _statusMessage = "Choose a valid destination lane for the link chain.";
            return false;
        }

        var chainNodes = _editorDragChainNodes.Count > 1
            ? _editorDragChainNodes
            : GetEditorLinkChainNodes(root);
        if (chainNodes.Count <= 1)
            return false;

        var sourceSide = sourcePosition.Value.Side;
        var sourceBank = sourcePosition.Value.Bank;
        var targetSide = targetPosition.Value.Side;
        var targetBank = targetPosition.Value.Bank;
        var sourcePositions = new Dictionary<ChartEvent, (string Side, string Bank, int LocalPos)>();
        foreach (var node in chainNodes)
        {
            var nodePosition = GearState.Position(OriginalTrack(node));
            if (nodePosition is null ||
                !string.Equals(nodePosition.Value.Side, sourceSide, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(nodePosition.Value.Bank, sourceBank, StringComparison.OrdinalIgnoreCase))
            {
                _statusMessage = "This chain already spans more than one physical bank, so it cannot be moved as one chain.";
                return false;
            }
            sourcePositions[node] = nodePosition.Value;
        }

        var sourceCount = sourceBank == "Center" ? 2 : 3;
        var targetCount = targetBank == "Center" ? 2 : 3;
        var draggedScaledLane = ScaleEditorBankLane(sourcePosition.Value.LocalPos, sourceCount, targetCount);
        var laneOffset = targetPosition.Value.LocalPos - draggedScaledLane;
        var moving = chainNodes.ToHashSet();
        var newTracks = new Dictionary<ChartEvent, int>();
        foreach (var node in chainNodes)
        {
            var nodePosition = sourcePositions[node];
            var scaledLane = ScaleEditorBankLane(nodePosition.LocalPos, sourceCount, targetCount);
            var targetLane = Math.Clamp(scaledLane + laneOffset, 0, targetCount - 1);
            var mappedTrack = EditorTrackForBankPosition(targetSide, targetBank, targetLane);
            if (mappedTrack < 0)
                return false;
            if (!IsEditorTrackAllowedForNoteType(mappedTrack, node.NoteType))
            {
                _statusMessage = "In 1K, L1/L3/R1/R3 are reserved for link tails. This chain cannot be moved to that lane.";
                return false;
            }
            newTracks[node] = mappedTrack;
        }

        // During drag preview, all points follow the same destination bank and side.
        // Re-index every point as a group, including points which happen to land on
        // their original track. Otherwise a partly-overlapping remap can give two
        // chain points the same event key. Track-list membership is finalized on release.
        var nextIndexByTrack = new Dictionary<int, int>();
        foreach (var node in chainNodes.OrderBy(node => newTracks[node]).ThenBy(node => node.Tick).ThenBy(node => node.EventIndex))
        {
            var newTrack = newTracks[node];
            if (!nextIndexByTrack.TryGetValue(newTrack, out var nextIndex))
            {
                nextIndex = _chart.Tracks.SelectMany(track => track.Events)
                    .Where(candidate => !moving.Contains(candidate) && candidate.Track == newTrack)
                    .Select(candidate => candidate.EventIndex)
                    .DefaultIfEmpty(-1).Max() + 1;
            }
            node.Track = newTrack;
            node.EventIndex = nextIndex;
            nextIndexByTrack[newTrack] = nextIndex + 1;
        }

        _editorDragMovedChainAcrossBanks = chainNodes.Any(node => node.Track != OriginalTrack(node));
        _editorTrack = draggedEvent.Track;
        _statusMessage = $"Moving link chain to the {targetSide} {BankModeLabel(targetBank)} bank; all points stay together.";
        return true;
    }

    private void UpdateEditorDrag(Vector2 point, double currentTick, GearState currentState)
    {
        var ev = _editorDraggedEvent;
        if (ev is null || _tempo is null)
            return;

        var snappedTick = FindClickedQuarterBeat(point, currentTick, currentState, out var snappedTrack);
        if (snappedTick is null)
            return;

        if (_editorDragIsMultiSelection)
        {
            UpdateEditorMultiDrag(ev, snappedTick.Value, snappedTrack);
            return;
        }

        var selectedRoot = GetEditorPreferredLinkRoot(ev);
        if (!_editorDragIsHoldEnd && (ev.NoteType is NoteType.LinkHead or NoteType.LinkTail) &&
            selectedRoot is not null && selectedRoot.NoteType == NoteType.LinkHead)
        {
            var destinationTrackForChain = Math.Clamp(snappedTrack, 0, 15);
            var originalPosition = GearState.Position(_editorDragOriginalTrack);
            var targetPosition = GearState.Position(destinationTrackForChain);
            if (originalPosition is not null && targetPosition is not null &&
                (_editorDragMovedChainAcrossBanks ||
                 !string.Equals(originalPosition.Value.Side, targetPosition.Value.Side, StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(originalPosition.Value.Bank, targetPosition.Value.Bank, StringComparison.OrdinalIgnoreCase)))
            {
                if (!TryMoveEditorLinkChainToBank(selectedRoot, ev, destinationTrackForChain))
                    return;
            }
        }

        var nextPoint = GetEditorNextLinkPoint(ev);
        var previousPoint = GetEditorPreviousLinkPoint(ev);

        if (_editorDragIsHoldEnd)
        {
            if (!UpdateEditorHoldEndDrag(ev, snappedTick.Value, selectedRoot, nextPoint))
                return;
        }
        else
        {
            var destinationTrack = Math.Clamp(snappedTrack, 0, 15);
            if (!IsEditorTrackAllowedForNoteType(destinationTrack, ev.NoteType))
            {
                _statusMessage = "In 1K, L1/L3/R1/R3 are reserved for link tails. Place other notes on L2/R2.";
                return;
            }
            if (ev.IsSuperFlick)
            {
                var originalPosition = GearState.Position(_editorDragOriginalTrack);
                var destinationPosition = GearState.Position(destinationTrack);
                if (originalPosition is null || destinationPosition is null ||
                    !string.Equals(originalPosition.Value.Side, destinationPosition.Value.Side, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(originalPosition.Value.Bank, destinationPosition.Value.Bank, StringComparison.OrdinalIgnoreCase))
                {
                    _statusMessage = "SuperFlicks cannot be dragged to a different bank or side.";
                    return;
                }
                if (HasSuperFlickBankCollision(snappedTick.Value, destinationTrack, new HashSet<ChartEvent> { ev }))
                {
                    _statusMessage = "SuperFlicks cannot share the same physical bank at the same tick.";
                    return;
                }
            }
            if (ev.NoteType == NoteType.LinkHead)
            {
                var firstTail = nextPoint;
                var directHorizontal = firstTail is not null &&
                                       snappedTick.Value == firstTail.Tick &&
                                       destinationTrack != firstTail.Track;
                // If the user moves the head onto its same-tick horizontal tail,
                // allow the collision so FinishEditorDrag can merge the pair. This
                // is different from moving the head past a later tail.
                var collapsingHorizontalPair = firstTail is not null &&
                                               snappedTick.Value == firstTail.Tick &&
                                               destinationTrack == firstTail.Track;
                if (firstTail is not null && snappedTick.Value >= firstTail.Tick - 5 &&
                    !directHorizontal && !collapsingHorizontalPair)
                {
                    _statusMessage = "The link head must stay before its first tail, unless merging into its same-tick horizontal tail.";
                    return;
                }
            }
            else if (ev.NoteType == NoteType.LinkTail)
            {
                // Adjacent points may occupy the same tick when they are on
                // different tracks: this is a valid horizontal link in the source game.
                var horizontalToPrevious = previousPoint is not null &&
                                           snappedTick.Value == previousPoint.Tick &&
                                           destinationTrack != previousPoint.Track;
                var horizontalToNext = nextPoint is not null &&
                                       snappedTick.Value == nextPoint.Tick &&
                                       destinationTrack != nextPoint.Track;
                // Symmetrically, let a tail move onto its same-tick horizontal
                // head. FinishEditorDrag will merge the now same-lane pair.
                var collapsingHorizontalPairWithPrevious = previousPoint is not null &&
                                                           snappedTick.Value == previousPoint.Tick &&
                                                           destinationTrack == previousPoint.Track;
                if (previousPoint is not null && snappedTick.Value <= previousPoint.Tick + 5 &&
                    !horizontalToPrevious && !collapsingHorizontalPairWithPrevious)
                {
                    _statusMessage = "A link tail must stay after the previous point, unless merging into its same-tick horizontal head.";
                    return;
                }
                if (nextPoint is not null && snappedTick.Value >= nextPoint.Tick - 5 && !horizontalToNext)
                {
                    _statusMessage = "A link tail must stay before the next point, unless making a same-tick horizontal link.";
                    return;
                }
            }
            if ((ev.NoteType is NoteType.LinkHead or NoteType.LinkTail) &&
                !string.Equals(LinkBuilder.Family(destinationTrack), LinkBuilder.Family(ev.Track), StringComparison.Ordinal))
            {
                _statusMessage = "Link-chain points must stay within their native track family.";
                return;
            }
            if (destinationTrack != ev.Track && _chart is not null)
            {
                if (destinationTrack == _editorDragOriginalTrack)
                {
                    ev.EventIndex = _editorDragOriginalEventIndex;
                }
                else
                {
                    // Keep the changing key unique while the event is previewed on
                    // a different track; commit will normalize it again on release.
                    var destination = _chart.FindTrack(destinationTrack);
                    ev.EventIndex = destination is null || destination.Events.Count == 0
                        ? 0
                        : destination.Events.Max(candidate => candidate.EventIndex) + 1;
                }
            }

            var originalTerminalEnd = _editorDragOriginalTick + Math.Max(1, _editorDragOriginalDuration);
            var preserveTerminalHold = (ev.NoteType is NoteType.LinkHead or NoteType.LinkTail) &&
                                       nextPoint is null && ev.Duration > 6;
            ev.Tick = Math.Max(0, snappedTick.Value);
            ev.Track = destinationTrack;
            if (preserveTerminalHold)
                ev.Duration = Math.Max(7, originalTerminalEnd - ev.Tick);
            if (ev.NoteType is NoteType.LinkHead or NoteType.LinkTail)
                SyncEditorLinkChainDurations(selectedRoot);
            _editorTrack = ev.Track;
        }

        var endpointChanged = _editorDragMovedEndpoint is not null &&
            _editorDragMovedEndpoint.Tick != _editorDragEndpointOriginalTick;
        if (ev.Tick == _editorDragLastTick &&
            ev.Track == _editorDragLastTrack &&
            ev.Duration == _editorDragLastDuration &&
            ev.NoteType == _editorDragOriginalNoteType &&
            ev.Attr == _editorDragOriginalAttr &&
            ev.LinePair == _editorDragOriginalLinePair &&
            !endpointChanged)
            return;

        _editorDragLastTick = ev.Tick;
        _editorDragLastTrack = ev.Track;
        _editorDragLastDuration = ev.Duration;
        _editorDragChanged = ev.Tick != _editorDragOriginalTick ||
                             ev.Track != _editorDragOriginalTrack ||
                             ev.Duration != _editorDragOriginalDuration ||
                             ev.NoteType != _editorDragOriginalNoteType ||
                             ev.Attr != _editorDragOriginalAttr ||
                             ev.LinePair != _editorDragOriginalLinePair ||
                             endpointChanged;
        _selectedEventKey = KeyOf(ev);
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorHoverCacheValid = false;
        _statusMessage = _editorDragIsHoldEnd
            ? $"Resizing hold: tick {ev.Tick} to {GetEditorNextLinkPoint(ev)?.Tick ?? ev.EndTick}, track {ev.Track}."
            : $"Moving note: tick {ev.Tick}, track {ev.Track}.";

        // Event keys contain tick/track. A live drag changes those keys, but the
        // adjacency/root caches used to draw the chain still held the old keys;
        // the cache lookup then dropped every segment after the moved point until
        // mouse release. Rebuild the lightweight link maps during preview too.
        RebuildLinkAdjacencyCache();
        RebuildLinkRootMap();
        RebuildRenderEventCache();
    }

    private bool UpdateEditorHoldEndDrag(ChartEvent ev, int snappedTick, ChartEvent? selectedRoot, ChartEvent? nextPoint)
    {
        if (_chart is null)
            return false;

        // Hold-end dragging only changes the end tick. The endpoint always stays
        // on its existing track; it must not create or move a horizontal link.
        if (nextPoint is not null)
        {
            var targetTick = Math.Max(ev.Tick + 6, snappedTick);
            var followingPoint = GetEditorNextLinkPoint(nextPoint);
            if (followingPoint is not null && targetTick >= followingPoint.Tick - 5)
            {
                _statusMessage = "The hold end must stay before the following link point.";
                return false;
            }

            nextPoint.Tick = targetTick;
            _editorDragMovedEndpoint = nextPoint;
            ev.Duration = Math.Max(6, targetTick - ev.Tick);
            SyncEditorLinkChainDurations(selectedRoot);
            return true;
        }

        var clampedEndTick = Math.Max(ev.Tick + 7, snappedTick);
        ev.Duration = Math.Max(7, clampedEndTick - ev.Tick);
        return true;
    }

    private void UpdateEditorMultiDrag(ChartEvent anchor, int snappedTick, int snappedTrack)
    {
        if (_chart is null || _editorMultiDragItems.Count <= 1)
            return;

        var movingEvents = _editorMultiDragItems.Select(item => item.Event).ToHashSet();
        var tickDelta = snappedTick - _editorMultiDragAnchorTick;
        var minTick = _editorMultiDragItems.Min(item => item.Tick);
        tickDelta = Math.Max(-minTick, tickDelta);
        var minTrack = _editorMultiDragItems.Min(item => item.Track);
        var maxTrack = _editorMultiDragItems.Max(item => item.Track);
        var trackDelta = Math.Clamp(snappedTrack - _editorMultiDragAnchorTrack, -minTrack, 15 - maxTrack);
        var rejectedSuperFlickTrackShift = false;
        var rejectedLinkChainTrackShift = false;
        bool LinkChainsStayWithinOneBank(int delta)
        {
            if (delta == 0)
                return true;

            // Use original drag-start tracks, not the live-mutating Track values;
            // trackDelta is absolute from the drag start and is reevaluated each frame.
            var sourceTrackByEvent = _editorMultiDragItems.ToDictionary(item => item.Event, item => item.Track);
            var roots = _editorMultiDragItems
                .Select(item => GetEditorLinkRoot(item.Event))
                .Where(root => root is not null && root.NoteType == NoteType.LinkHead)
                .Cast<ChartEvent>()
                .Distinct()
                .ToArray();
            foreach (var root in roots)
            {
                var nodes = GetEditorLinkChainNodes(root);
                if (nodes.Count <= 1)
                    continue;
                if (nodes.Any(node => !movingEvents.Contains(node) || !sourceTrackByEvent.ContainsKey(node)))
                    return false;

                var firstSource = GearState.Position(sourceTrackByEvent[nodes[0]]);
                if (firstSource is null)
                    return false;
                var sourceSide = firstSource.Value.Side;
                var sourceBank = firstSource.Value.Bank;
                string? destinationSide = null;
                string? destinationBank = null;
                foreach (var node in nodes)
                {
                    var source = GearState.Position(sourceTrackByEvent[node]);
                    var destination = GearState.Position(sourceTrackByEvent[node] + delta);
                    if (source is null || destination is null ||
                        !string.Equals(source.Value.Side, sourceSide, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(source.Value.Bank, sourceBank, StringComparison.OrdinalIgnoreCase))
                        return false;

                    destinationSide ??= destination.Value.Side;
                    destinationBank ??= destination.Value.Bank;
                    if (!string.Equals(destinationSide, destination.Value.Side, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(destinationBank, destination.Value.Bank, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }
            return true;
        }
        bool TrackShiftRespectsOneKRules(int delta)
            => _editorMultiDragItems.All(item => IsEditorTrackAllowedForNoteType(item.Track + delta, item.Event.NoteType));
        bool SuperFlickTrackShiftIsValid(int delta)
        {
            foreach (var item in _editorMultiDragItems)
            {
                if (!item.Event.IsSuperFlick)
                    continue;
                var originalPosition = GearState.Position(item.Track);
                var targetPosition = GearState.Position(item.Track + delta);
                if (originalPosition is null || targetPosition is null ||
                    !string.Equals(originalPosition.Value.Side, targetPosition.Value.Side, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(originalPosition.Value.Bank, targetPosition.Value.Bank, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }
        if (!TrackShiftRespectsOneKRules(trackDelta))
        {
            _statusMessage = "In 1K, L1/L3/R1/R3 are reserved for link tails; selected notes were kept in legal lanes.";
            trackDelta = _editorMultiDragLastTrackDelta;
        }
        if (!SuperFlickTrackShiftIsValid(trackDelta))
        {
            rejectedSuperFlickTrackShift = true;
            trackDelta = _editorMultiDragLastTrackDelta;
        }
        if (!LinkChainsStayWithinOneBank(trackDelta))
        {
            rejectedLinkChainTrackShift = true;
            trackDelta = _editorMultiDragLastTrackDelta;
        }

        var rejectedSuperFlickBankCollision = false;
        bool SuperFlickPlacementsAreUnique(int candidateTickDelta, int candidateTrackDelta)
        {
            var occupied = new HashSet<(long Tick, string Side, string Bank)>();
            foreach (var item in _editorMultiDragItems)
            {
                if (!item.Event.IsSuperFlick)
                    continue;

                var targetTick = (long)item.Tick + candidateTickDelta;
                var targetTrack = item.Track + candidateTrackDelta;
                if (targetTick < 0 || targetTick > int.MaxValue)
                    return false;
                var position = GearState.Position(targetTrack);
                if (position is null)
                    return false;

                var key = ((long)targetTick, position.Value.Side.ToUpperInvariant(), position.Value.Bank.ToUpperInvariant());
                if (!occupied.Add(key) || HasSuperFlickBankCollision((int)targetTick, targetTrack, movingEvents))
                    return false;
            }
            return true;
        }

        if (!SuperFlickPlacementsAreUnique(tickDelta, trackDelta))
        {
            rejectedSuperFlickBankCollision = true;
            tickDelta = _editorMultiDragLastTickDelta;
            trackDelta = _editorMultiDragLastTrackDelta;
        }

        if (tickDelta == _editorMultiDragLastTickDelta && trackDelta == _editorMultiDragLastTrackDelta)
        {
            if (rejectedSuperFlickBankCollision)
                _statusMessage = "SuperFlicks cannot share the same physical bank at the same tick; selected notes were kept at their last valid positions.";
            else if (rejectedSuperFlickTrackShift)
                _statusMessage = "SuperFlicks cannot be dragged to a different bank or side.";
            else if (rejectedLinkChainTrackShift)
                _statusMessage = "A link chain must stay entirely within one physical bank; its points were kept together.";
            return;
        }

        foreach (var item in _editorMultiDragItems)
        {
            item.Event.Tick = item.Tick + tickDelta;
            item.Event.Track = item.Track + trackDelta;
            item.Event.Duration = item.Duration;
            if (item.Event.Track == item.Track)
                item.Event.EventIndex = item.EventIndex;
        }

        // Assign stable unique event indices for a track-shift preview, excluding
        // the selected notes from target occupancy because all of them move as a unit.
        if (trackDelta != 0)
        {
            foreach (var group in _editorMultiDragItems
                         .OrderBy(item => item.Track).ThenBy(item => item.Tick).ThenBy(item => item.EventIndex)
                         .GroupBy(item => item.Track + trackDelta))
            {
                var destination = _chart.FindTrack(group.Key);
                var nextIndex = (destination?.Events
                    .Where(candidate => !movingEvents.Contains(candidate))
                    .Select(candidate => candidate.EventIndex)
                    .DefaultIfEmpty(-1)
                    .Max() ?? -1) + 1;
                foreach (var item in group)
                    item.Event.EventIndex = nextIndex++;
            }
        }
        else
        {
            foreach (var item in _editorMultiDragItems)
                item.Event.EventIndex = item.EventIndex;
        }

        _editorMultiDragLastTickDelta = tickDelta;
        _editorMultiDragLastTrackDelta = trackDelta;
        _editorDragChanged = tickDelta != 0 || trackDelta != 0;
        _selectedEventKey = KeyOf(anchor);
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorHoverCacheValid = false;
        _statusMessage = rejectedSuperFlickBankCollision
            ? "SuperFlicks cannot share the same physical bank at the same tick; selected notes were kept at their last valid positions."
            : rejectedSuperFlickTrackShift
                ? "SuperFlicks cannot be dragged to a different bank or side. Other selected notes stayed within the SuperFlick's bank."
                : rejectedLinkChainTrackShift
                    ? "A link chain must stay entirely within one physical bank; its points were kept together."
                    : _editorDragChanged
                        ? $"Moving {_editorMultiDragItems.Count} selected notes: tick offset {tickDelta:+#;-#;0}, track offset {trackDelta:+#;-#;0}."
                        : $"Selected {_editorMultiDragItems.Count} notes. Drag to move them together.";

        // Synchronize durations only when the complete chain is part of the
        // selection. A partial selection must not silently reshape unselected
        // link points while the user is moving the selected notes.
        foreach (var item in _editorMultiDragItems.Where(item => item.Event.NoteType == NoteType.LinkHead))
        {
            var chainNodes = GetEditorLinkChainNodes(item.Event);
            if (chainNodes.Count > 1 && chainNodes.All(movingEvents.Contains))
                SyncEditorLinkChainDurations(item.Event);
        }
        RebuildLinkAdjacencyCache();
        RebuildLinkRootMap();
        RebuildRenderEventCache();
    }

    private void FinishEditorDrag()
    {
        var ev = _editorDraggedEvent;
        var changed = _editorDragChanged;
        var undoSnapshot = _editorDragUndoSnapshot;
        var originalTrack = _editorDragOriginalTrack;
        var wasHoldEnd = _editorDragIsHoldEnd;
        var wasMultiSelection = _editorDragIsMultiSelection;
        var multiItems = _editorMultiDragItems.ToArray();
        var movedChainAcrossBanks = _editorDragMovedChainAcrossBanks;
        var movedChainNodes = _editorDragChainNodes.ToArray();
        _editorDraggedEvent = null;
        _editorDragUndoSnapshot = null;
        _editorDragIsHoldEnd = false;
        _editorDragStartedByTouch = false;
        _editorDragChanged = false;
        _editorDragIsMultiSelection = false;
        _editorDragMovedChainAcrossBanks = false;
        _editorMultiDragItems.Clear();
        _editorDragChainNodes.Clear();
        _editorDragMovedEndpoint = null;
        _editorDragEndpointOriginalTick = -1;

        if (ev is null || !changed || _chart is null)
            return;

        if (wasMultiSelection)
        {
            var movingEvents = multiItems.Select(item => item.Event).ToHashSet();
            foreach (var track in _chart.Tracks)
                track.Events.RemoveAll(candidate => movingEvents.Contains(candidate));

            foreach (var item in multiItems.OrderBy(item => item.Event.Track).ThenBy(item => item.Event.Tick).ThenBy(item => item.Event.EventIndex))
            {
                var destination = _chart.FindTrack(item.Event.Track);
                if (destination is null)
                {
                    destination = new ChartTrack { TrackIndex = item.Event.Track };
                    _chart.Tracks.Add(destination);
                }
                if (item.Event.Track != item.Track)
                {
                    var nextIndex = destination.Events.Select(candidate => candidate.EventIndex).DefaultIfEmpty(-1).Max() + 1;
                    item.Event.EventIndex = nextIndex;
                }
                destination.Events.Add(item.Event);
            }

            if (!CommitEditorUndoSnapshot(undoSnapshot))
                return;
            RefreshAfterEditorMutation();
            _selectedEventKey = KeyOf(ev);
            _selectedBeatTick = null;
            _selectedBeatTrack = null;
            _editorTrack = ev.Track;
            _editorSelectedLinkRoot = GetEditorLinkRoot(ev);
            _statusMessage = $"Moved {multiItems.Length} selected notes together.";
            return;
        }

        if (movedChainAcrossBanks && movedChainNodes.Length > 1)
        {
            var movingChain = movedChainNodes.ToHashSet();
            var originalTracks = undoSnapshot?.Events
                .Where(snapshot => movingChain.Contains(snapshot.Event))
                .ToDictionary(snapshot => snapshot.Event, snapshot => snapshot.Track)
                ?? new Dictionary<ChartEvent, int>();
            foreach (var chartTrack in _chart.Tracks)
                chartTrack.Events.RemoveAll(candidate => movingChain.Contains(candidate));

            foreach (var group in movedChainNodes
                         .OrderBy(node => node.Track).ThenBy(node => node.Tick).ThenBy(node => node.EventIndex)
                         .GroupBy(node => node.Track))
            {
                var destination = _chart.FindTrack(group.Key);
                if (destination is null)
                {
                    destination = new ChartTrack { TrackIndex = group.Key };
                    _chart.Tracks.Add(destination);
                }

                var nextIndex = destination.Events.Select(candidate => candidate.EventIndex).DefaultIfEmpty(-1).Max() + 1;
                foreach (var node in group)
                {
                    if (!originalTracks.TryGetValue(node, out var oldTrack) || oldTrack != node.Track)
                        node.EventIndex = nextIndex++;
                    destination.Events.Add(node);
                }
            }

            if (!CommitEditorUndoSnapshot(undoSnapshot))
                return;
            RefreshAfterEditorMutation();
            _selectedEventKey = KeyOf(ev);
            _selectedBeatTick = null;
            _selectedBeatTrack = null;
            _editorTrack = ev.Track;
            _editorSelectedLinkRoot = GetEditorLinkRoot(ev) ?? _editorSelectedLinkRoot;
            var position = GearState.Position(ev.Track);
            _statusMessage = position is null
                ? "Moved link chain to another bank; all points stayed together."
                : $"Moved link chain to the {position.Value.Side} {BankModeLabel(position.Value.Bank)} bank; all points stayed together.";
            return;
        }

        var previousPoint = GetEditorPreviousLinkPoint(ev);
        var nextPoint = GetEditorNextLinkPoint(ev);

        // Chart.Events is a flattened view of Chart.Tracks. Move the source event
        // between track lists only on release, while its live drag preview uses the
        // mutable event object directly.
        foreach (var chartTrack in _chart.Tracks)
            chartTrack.Events.RemoveAll(candidate => ReferenceEquals(candidate, ev));

        var targetTrack = _chart.FindTrack(ev.Track);
        if (targetTrack is null)
        {
            targetTrack = new ChartTrack { TrackIndex = ev.Track };
            _chart.Tracks.Add(targetTrack);
        }

        if (ev.Track != originalTrack)
        {
            ev.EventIndex = targetTrack.Events.Count == 0
                ? 0
                : targetTrack.Events.Max(candidate => candidate.EventIndex) + 1;
        }
        targetTrack.Events.Add(ev);

        var mergedSameLane = false;
        ChartEvent? finalSelection = ev;
        var collapsingHorizontalPair = !wasHoldEnd &&
            ((ev.NoteType == NoteType.LinkHead && nextPoint is not null &&
              nextPoint.Tick == ev.Tick && nextPoint.Track == ev.Track) ||
             (ev.NoteType == NoteType.LinkTail && previousPoint is not null &&
              previousPoint.Tick == ev.Tick && previousPoint.Track == ev.Track));

        // Collapsing a same-tick horizontal connection onto one lane must always
        // merge the two points, even if optional auto-merge is disabled. Otherwise
        // the chart would be left with a zero-width, same-lane link connection.
        if ((_editorAutoMergeLinks || collapsingHorizontalPair) && !wasHoldEnd &&
            ev.NoteType == NoteType.LinkTail && previousPoint is not null && previousPoint.Track == ev.Track)
        {
            // Consecutive points on the same lane are one hold segment. Remove the
            // duplicate tail, then extend the previous point to the next real point
            // (or to this endpoint when the chain ends here).
            targetTrack.Events.Remove(ev);
            if (nextPoint is not null && nextPoint.Tick > previousPoint.Tick)
                previousPoint.Duration = nextPoint.Tick - previousPoint.Tick;
            else if (nextPoint is not null && nextPoint.Tick == previousPoint.Tick &&
                     nextPoint.Track != previousPoint.Track)
                previousPoint.Duration = 6;
            else
            {
                // A six-tick LinkTail duration is the synthetic tap-sized
                // endpoint marker, not an authored hold beyond the connector.
                // Only preserve its EndTick when it carries a real long duration.
                var mergedTailEndTick = ev.Duration > 6 ? ev.EndTick : ev.Tick;
                previousPoint.Duration = Math.Max(1, mergedTailEndTick - previousPoint.Tick);
            }

            finalSelection = previousPoint;
            mergedSameLane = true;
        }
        else if ((_editorAutoMergeLinks || collapsingHorizontalPair) && !wasHoldEnd &&
                 ev.NoteType == NoteType.LinkHead && nextPoint is not null && nextPoint.Track == ev.Track)
        {
            // A LinkHead moved onto its next tail merges symmetrically: remove the
            // same-lane tail and let the head span to the following point. If no
            // connection remains, retain the removed tail's duration; only a real
            // authored long duration should produce a standalone long note.
            foreach (var chartTrack in _chart.Tracks)
                chartTrack.Events.RemoveAll(candidate => ReferenceEquals(candidate, nextPoint));

            var followingPoint = GetEditorNextLinkPoint(nextPoint);
            if (followingPoint is not null &&
                followingPoint.Tick >= ev.Tick)
            {
                // Preserve a remaining connection even when it is another
                // same-tick horizontal segment. Horizontal links need the default
                // six-tick span; forward links use their actual endpoint distance.
                ev.Duration = followingPoint.Tick == ev.Tick
                    ? Math.Max(6, followingPoint.Tick - ev.Tick)
                    : followingPoint.Tick - ev.Tick;
            }
            else
            {
                // No connection remains after removing the horizontal tail. Its
                // six-tick duration is only the synthetic endpoint marker; the
                // standalone body spans from this head to the removed tail's tick.
                // Preserve an extension beyond that tick only for a real long tail.
                var mergedTailEndTick = nextPoint.Duration > 6 ? nextPoint.EndTick : nextPoint.Tick;
                ev.Duration = Math.Max(1, mergedTailEndTick - ev.Tick);
                ConvertEditorLinkPointToStandaloneNote(ev);
            }

            finalSelection = ev;
            mergedSameLane = true;
        }

        // Normalize same-lane merges before committing the edit. In EDIT GROUP,
        // any note removals/type conversions performed after CommitEditorUndoSnapshot
        // would affect only the source instance and could leave sibling tails behind.
        if (mergedSameLane)
        {
            RefreshAfterEditorMutation();
            var normalizedSelection = NormalizeEditorSameTrackLinkChains(finalSelection, out var normalized);
            if (normalized)
                finalSelection = normalizedSelection ?? finalSelection;
        }
        else
        {
            SyncEditorLinkChainDurations(GetEditorPreferredLinkRoot(ev));
        }

        if (!CommitEditorUndoSnapshot(undoSnapshot))
            return;
        RefreshAfterEditorMutation();

        _selectedEventKey = KeyOf(finalSelection);
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorTrack = finalSelection.Track;
        _editorSelectedHoldEnd = wasHoldEnd && !mergedSameLane;
        ClearEditorMultiSelection();
        _editorSelectedLinkRoot = GetEditorLinkRoot(finalSelection);
        _statusMessage = mergedSameLane && finalSelection.NoteType == NoteType.SingleLong
            ? $"Merged link points into one hold ending at tick {finalSelection.EndTick}, track {finalSelection.Track}."
            : mergedSameLane
                ? $"Merged same-track link points. Selected tick {finalSelection.Tick}, track {finalSelection.Track}."
                : wasHoldEnd
                    ? $"Resized hold: tick {finalSelection.Tick} to {GetEditorNextLinkPoint(finalSelection)?.Tick ?? finalSelection.EndTick}, track {finalSelection.Track}."
                    : $"Moved note: tick {finalSelection.Tick}, track {finalSelection.Track}.";
    }

    private ChartEvent? NormalizeEditorSameTrackLinkChains(ChartEvent? preferredSelection, out bool changed)
    {
        changed = false;
        if (_chart is null || preferredSelection is null)
            return preferredSelection;

        // Normalize only the chain involved in this edit. Other authored LinkHeads
        // elsewhere in the chart may intentionally be waiting for a later connector.
        var root = GetEditorLinkRoot(preferredSelection) ??
                   (preferredSelection.NoteType == NoteType.LinkHead ? preferredSelection : null);
        if (root is null || root.NoteType != NoteType.LinkHead)
            return preferredSelection;

        // RefreshAfterEditorMutation has just rebuilt _chart.Links, so this list
        // describes only the surviving chain nodes. If a same-track merge removed
        // the final tail, the old head is now a standalone body, not an orphan head.
        var nodes = GetEditorLinkChainNodes(root);
        if (nodes.Count < 2)
        {
            var hasIncidentLink = _chart.Links.Any(link =>
                ReferenceEquals(link.Head, root) || ReferenceEquals(link.Tail, root));
            if (!_ambiguousNextLinkHeadKeys.Contains(KeyOf(root)) && !hasIncidentLink)
            {
                ConvertEditorLinkPointToStandaloneNote(root);
                changed = true;
                return root;
            }
            return preferredSelection;
        }

        // Collapse each consecutive same-track run independently. A chain may
        // still leave that lane later (for example 7 -> 7 -> 8); in that case
        // the same-lane run becomes one hold ending at the next surviving point,
        // while the later cross-track LinkTail must remain in the chain.
        var removed = new HashSet<ChartEvent>();
        var runStart = 0;
        while (runStart < nodes.Count)
        {
            var runTrack = nodes[runStart].Track;
            var runEnd = runStart + 1;
            while (runEnd < nodes.Count && nodes[runEnd].Track == runTrack)
                runEnd++;

            if (runEnd - runStart > 1)
            {
                var runHead = nodes[runStart];
                var terminal = nodes[^1];
                var terminalEndTick = terminal.Duration > 6 ? terminal.EndTick : terminal.Tick;
                var runEndTick = runEnd < nodes.Count
                    ? nodes[runEnd].Tick
                    : terminalEndTick;
                runHead.Duration = Math.Max(1, runEndTick - runHead.Tick);
                for (var i = runStart + 1; i < runEnd; i++)
                    removed.Add(nodes[i]);
            }

            runStart = runEnd;
        }

        if (removed.Count == 0)
            return preferredSelection;

        foreach (var redundant in removed)
        {
            foreach (var track in _chart.Tracks)
                track.Events.RemoveAll(candidate => ReferenceEquals(candidate, redundant));
        }

        changed = true;
        var hasSurvivingIncidentLink = _chart.Links.Any(link =>
            (ReferenceEquals(link.Head, root) && !removed.Contains(link.Tail)) ||
            (ReferenceEquals(link.Tail, root) && !removed.Contains(link.Head)));
        if (nodes.Count - removed.Count == 1 && root.NoteType == NoteType.LinkHead &&
            !_ambiguousNextLinkHeadKeys.Contains(KeyOf(root)) && !hasSurvivingIncidentLink)
        {
            // Collapsing the last same-track tail leaves only the original head.
            // Convert it now while the merged body duration is already normalized.
            ConvertEditorLinkPointToStandaloneNote(root);
            return root;
        }
        return removed.Contains(preferredSelection) ? root : preferredSelection;
    }

    private ChartEvent? FindClickedNote(
        Vector2 point, double currentTick, GearState currentState, out float bestDistance,
        IReadOnlyList<int>? candidateIndices = null, int candidateCount = -1)
    {
        bestDistance = float.MaxValue;
        if (_chart is null || _tempo is null)
            return null;

        ChartEvent? best = null;
        var count = candidateIndices is null
            ? _orderedRenderableEvents.Count
            : Math.Clamp(candidateCount, 0, candidateIndices.Count);
        for (var candidateIndex = 0; candidateIndex < count; candidateIndex++)
        {
            var eventIndex = candidateIndices is null ? candidateIndex : candidateIndices[candidateIndex];
            var ev = _orderedRenderableEvents[eventIndex];
            if (!IsRenderableNoteEvent(ev))
                continue;
            if (_compact && _gameplayMode && !IsActiveEvent(ev, currentState))
                continue;
            if (!ShouldIncludeRenderableEvent(ev, currentState))
                continue;

            var startTravel = _projection.ScrollPixelsAtTicks(ev.Tick, currentTick);
            // Hit-test the same visible extent that DrawNotes renders. A terminal
            // LinkTail with Duration == 6 is a point marker only; using EndTick
            // here creates an invisible six-tick card that falsely turns the next
            // snapped cells yellow even though no body is drawn there.
            var endTick = GetEditorNoteHitEndTick(ev);
            var endTravel = _projection.ScrollPixelsAtTicks(endTick, currentTick);
            if (!_projection.RenderIntervalIntersects((float)startTravel, (float)endTravel))
                continue;

            // SuperFlicks are rendered across their complete source bank, not at
            // the single physical track stored on the event. Hit-test that same
            // bank-wide strip so users can click anywhere along the visible path.
            if (ev.IsSuperFlick)
            {
                if (!TryGetEditorSuperFlickHitDistance(point, ev, currentTick, out var superFlickDistance))
                    continue;

                if (superFlickDistance < bestDistance)
                {
                    best = ev;
                    bestDistance = superFlickDistance;
                }
                continue;
            }

            var start = _projection.ProjectAtTicks(ev.Track, ev.Tick, currentTick, currentState);
            var end = _projection.ProjectAtTicks(ev.Track, endTick, currentTick, currentState);
            if (OutsideNoteView(start, end))
                continue;

            float distance;
            Texture2D? selectionTexture = null;
            if (ev.IsFlick)
                selectionTexture = _textures.Get("note-flick");
            else if (ev.NoteType == NoteType.Vibrato)
                selectionTexture = _textures.Get("indicator-vibrato");
            else if (ev.NoteType == NoteType.SingleLong || ev.NoteType is NoteType.LinkHead or NoteType.LinkTail)
                selectionTexture = _textures.Get("note-long");
            else
                selectionTexture = _textures.Get("note-regular");

            var noteAngle = _projection.NoteStandAngleDegrees;
            var selectionWidth = ev.IsFlick ? _projection.SmallFlickNoteWidth : _projection.NoteWidth;
            if (selectionTexture is not null)
            {
                var pos = GearState.Position(ev.Track);
                var side = pos?.Side ?? (start.Position.X < _projection.JudgeX ? "left" : "right");
                var bank = pos?.Bank;
                var normalizedX = _projection.LaneNormalizedX(ev.Track);
                var travel = (float)startTravel;
                var vertices = BuildCardVertices(
                    selectionTexture, start, ev.Track, selectionWidth, travel,
                    noteAngle, ev.IsFlick && ev.FlickDirection == 3 ? -MathF.PI * 0.5f : 0f,
                    ev.IsFlick && ev.FlickDirection == 5,
                    out _, null, null, null, side, bank, normalizedX);
                var a = _projection.ScreenPositionFromCameraSpace(vertices[0].Position);
                var b = _projection.ScreenPositionFromCameraSpace(vertices[1].Position);
                var c = _projection.ScreenPositionFromCameraSpace(vertices[2].Position);
                var d = _projection.ScreenPositionFromCameraSpace(vertices[5].Position);
                var inside = PointInTriangle(point, a, b, c) || PointInTriangle(point, a, c, d);
                _quad3DVertexPool.Push(vertices);

                // A note is selectable only when the cursor is on its projected
                // quad. Previously the nearest edge distance was always returned,
                // so every click selected whichever note happened to be closest.
                if (!inside)
                    continue;

                // If notes overlap, prefer the one whose projected start is closest
                // to the cursor instead of whichever event happened to be enumerated first.
                distance = Vector2.Distance(point, start.Position);
            }
            else
            {
                continue;
            }

            if (distance < bestDistance)
            {
                best = ev;
                bestDistance = distance;
            }
        }

        return best;
    }

    private bool TryGetEditorSuperFlickHitDistance(
        Vector2 point,
        ChartEvent ev,
        double currentTick,
        out float hitDistance)
    {
        hitDistance = float.MaxValue;
        var physicalPosition = GearState.Position(ev.Track);
        if (physicalPosition is null)
            return false;

        var routeBeforeEvent = _gear?.AtTick(ev.Tick, includeTickChanges: false);
        var side = ev.GetSuperFlickRenderSide() ?? physicalPosition.Value.Side;
        var bank = ev.GetSuperFlickSourceBank(routeBeforeEvent) ?? physicalPosition.Value.Bank;
        if (string.IsNullOrWhiteSpace(side) || string.IsNullOrWhiteSpace(bank))
            return false;

        var (bankLeft, bankRight) = SuperFlickSpanEdges(side, bank);
        var surface = _projection.BuildBankSurface(side, bank);
        var travel = (float)_projection.ScrollPixelsAtTicks(ev.Tick, currentTick);

        // Sample across the bank instead of relying on the event's physical track.
        // The projected strip can curve slightly with the bank surface, so use a
        // short polyline rather than a single straight line between its endpoints.
        const int segmentCount = 6;
        var previousPoint = Vector2.Zero;
        var closestDistance = float.MaxValue;
        var maxHitRadius = 0f;

        for (var i = 0; i <= segmentCount; i++)
        {
            var amount = i / (float)segmentCount;
            var normalizedX = MathHelper.Lerp(bankLeft, bankRight, amount);
            var context = _projection.BuildStandingNoteTravelContext(
                surface, normalizedX, side, bank);
            var basis = _projection.BuildStandingNoteBasis(context, travel);
            var screenPoint = _projection.ScreenPositionFromCameraSpace(basis.Center);

            var judgeBasis = _projection.BuildStandingNoteBasis(context, 0f);
            var currentPixelsPerWorld = Math.Max(0.0001f,
                _projection.ScreenPixelsPerWorldUnit(basis.Center, basis.Across));
            var referencePixelsPerWorld = Math.Max(0.0001f,
                _projection.ScreenPixelsPerWorldUnit(judgeBasis.Center, judgeBasis.Across));
            var localScale = currentPixelsPerWorld / referencePixelsPerWorld;
            var stripHeight = Math.Max(3f, _projection.SuperFlickTextureWidth * localScale);

            // Keep the hit area comfortably larger than the thin animated strip.
            // The added padding makes it much easier to select without requiring
            // pixel-perfect clicks, especially at the edges of a bank.
            maxHitRadius = Math.Max(maxHitRadius, Math.Max(12f, stripHeight * 0.5f + 8f));

            if (i > 0)
                closestDistance = Math.Min(
                    closestDistance,
                    PointToSegmentDistance(point, previousPoint, screenPoint));

            previousPoint = screenPoint;
        }

        if (closestDistance > maxHitRadius)
            return false;

        hitDistance = closestDistance;
        return true;
    }

    private int? FindClickedQuarterBeat(Vector2 point, double currentTick, GearState currentState, out int track)
    {
        track = -1;
        if (_chart is null || _tempo is null || _tempo.TicksPerBeat <= 0)
            return null;

        var snap = GetEditorSnapTicks();
        var renderPixels = Math.Max(300f,
            Math.Max(_projection.GetRenderDistanceAhead(), _projection.GetRenderDistanceBehind()) * 1.25f);
        var lowTick = Math.Max(0.0, currentTick - 2000.0);
        var lastChartTick = _chart.PlayTick > 0
            ? _chart.PlayTick
            : (_chart.Events.Count == 0 ? 0 : _chart.Events.Max(e => e.EndTick));
        var highTick = Math.Min(lastChartTick + 1.0, currentTick + 2000.0);

        // Expanded view allows placement on visible banks. In collapsed view, use
        // the modifier to target overlapping inactive banks; invisible banks stay disabled.
        var selectableBanks = new List<(string Side, string Bank)>();
        var altHeld = KeybindSettings.IsDown("editor_place_inactive_bank_modifier");
        var ordinaryFlickPlacement = _editorPlacementEnabled && (_editorAttr is 10 or 50);
        var superFlickPlacement = _editorSuperFlickTool == EditorSuperFlickTool.Place ||
                                  _editorSuperFlickPlacementPending;
        var allowInactivePlacementBank = _chartEditorMode && _compact && altHeld &&
            (ordinaryFlickPlacement || superFlickPlacement);
        foreach (var side in Sides)
        {
            var activeBank = GearTimeline.ActiveBankForSide(currentState, side);
            var orderedBanks = allowInactivePlacementBank
                ? Banks.Where(bank => !string.Equals(activeBank, bank, StringComparison.OrdinalIgnoreCase))
                    .Concat(Banks.Where(bank => string.Equals(activeBank, bank, StringComparison.OrdinalIgnoreCase)))
                : Banks.AsEnumerable();
            foreach (var bank in orderedBanks)
            {
                // Collapsed banks overlap. Normally only the active bank is
                // selectable, except inactive-bank-modifier placement, which can choose an
                // inactive bank using the same screen-space lane.
                if ((!_chartEditorMode || _compact) && !allowInactivePlacementBank &&
                    !string.Equals(activeBank, bank, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (_chartEditorMode && !allowInactivePlacementBank &&
                    GetEditorBankOpacity(side, bank, currentState) <= 0.001f)
                    continue;

                var laneTravel = _projection.LaneTravelDistanceForNormalizedX(0f, side, bank);
                var near = _projection.ProjectBankSliceAtTravel(0f, side, bank);
                var far = _projection.ProjectBankSliceAtTravel(laneTravel, side, bank);
                var inside = PointInTriangle(point, near.Inner, near.Outer, far.Outer) ||
                             PointInTriangle(point, near.Inner, far.Outer, far.Inner);
                if (inside)
                    selectableBanks.Add((side, bank));
            }
        }

        if (selectableBanks.Count == 0)
            return null;

        var first = Math.Max(0, (int)Math.Floor(lowTick / snap) - 2);
        var last = Math.Max(first, (int)Math.Ceiling(highTick / snap) + 2);
        int? bestTick = null;
        var bestDistance = float.MaxValue;
        string? bestSide = null;
        string? bestBank = null;

        for (var i = first; i <= last; i++)
        {
            var candidateTick = i * snap;
            var depth = _projection.ScrollPixelsAtTicks(candidateTick, currentTick);
            if (Math.Abs(depth) > renderPixels)
                continue;

            foreach (var (side, bank) in selectableBanks)
            {
                var slice = _projection.ProjectBankSliceAtTravel((float)depth, side, bank);
                var distance = PointToSegmentDistance(point, slice.Inner, slice.Outer);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestTick = (int)Math.Round(candidateTick, MidpointRounding.ToEven);
                    bestSide = side;
                    bestBank = bank;
                }
            }
        }

        if (bestTick is null || bestSide is null || bestBank is null)
            return null;

        // Assign the actual clicked track by comparing projected track centers in
        // the selected bank. The old editor only kept a toolbar target track, so
        // every placement inherited track 0 even when a different column was tapped.
        var closestTrack = -1;
        var closestTrackDistance = float.MaxValue;
        for (var candidateTrack = 0; candidateTrack < 16; candidateTrack++)
        {
            var position = GearState.Position(candidateTrack);
            if (position is null ||
                !string.Equals(position.Value.Side, bestSide, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(position.Value.Bank, bestBank, StringComparison.OrdinalIgnoreCase))
                continue;

            var projected = _projection.ProjectAtTicks(candidateTrack, bestTick.Value, currentTick, currentState);
            if (projected.Scale <= 0f || float.IsNaN(projected.Position.X) || float.IsNaN(projected.Position.Y))
                continue;

            var distance = Vector2.DistanceSquared(point, projected.Position);
            if (distance < closestTrackDistance)
            {
                closestTrackDistance = distance;
                closestTrack = candidateTrack;
            }
        }

        if (closestTrack < 0)
            return null;

        track = closestTrack;
        return bestTick;
    }

    private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        var ab = b - a;
        var bc = c - b;
        var ca = a - c;
        var ap = p - a;
        var bp = p - b;
        var cp = p - c;
        var c1 = ab.X * ap.Y - ab.Y * ap.X;
        var c2 = bc.X * bp.Y - bc.Y * bp.X;
        var c3 = ca.X * cp.Y - ca.Y * cp.X;
        return (c1 >= -0.01f && c2 >= -0.01f && c3 >= -0.01f) ||
               (c1 <= 0.01f && c2 <= 0.01f && c3 <= 0.01f);
    }

    private static float PointToSegmentDistance(Vector2 point, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var lengthSquared = ab.LengthSquared();
        if (lengthSquared <= 0.0001f)
            return Vector2.Distance(point, a);

        var t = Math.Clamp(Vector2.Dot(point - a, ab) / lengthSquared, 0f, 1f);
        var closest = a + ab * t;
        return Vector2.Distance(point, closest);
    }

    private float GetEditorMarkerRadius(
        int track,
        int tick,
        double currentTick,
        GearState currentState,
        out Vector2 markerCenter,
        ChartEvent? ev = null,
        ProjectedPoint? projectedOverride = null,
        float? travelOverride = null)
        => GetEditorMarkerGeometry(track, tick, currentTick, currentState,
            out markerCenter, out _, out _, ev, projectedOverride, travelOverride);

    private float GetEditorMarkerGeometry(
        int track,
        int tick,
        double currentTick,
        GearState currentState,
        out Vector2 markerCenter,
        out Vector2 markerBoundsMin,
        out Vector2 markerBoundsMax,
        ChartEvent? ev = null,
        ProjectedPoint? projectedOverride = null,
        float? travelOverride = null)
    {
        var projected = projectedOverride ?? _projection.ProjectAtTicks(track, tick, currentTick, currentState);
        markerCenter = projected.Position;
        markerBoundsMin = projected.Position;
        markerBoundsMax = projected.Position;
        var isFlick = ev?.IsFlick ?? (_editorAttr is 10 or 50);
        var noteType = ev?.NoteType ?? _editorNoteType;
        var width = isFlick ? _projection.SmallFlickNoteWidth : _projection.NoteWidth;
        var texture = isFlick
            ? _textures.Get("note-flick")
            : noteType == NoteType.Vibrato
                ? _textures.Get("indicator-vibrato")
                : noteType is NoteType.SingleLong or NoteType.LinkHead or NoteType.LinkTail
                    ? _textures.Get("note-long")
                    : _textures.Get("note-regular");

        var position = GearState.Position(track);
        var side = position?.Side ?? (projected.Position.X < _projection.JudgeX ? "left" : "right");
        var bank = position?.Bank;
        var direction = ev?.FlickDirection ?? (_editorAttr == 50 ? 5 : 1);
        var rotation = isFlick && direction == 3 ? -MathF.PI * 0.5f : 0f;
        var flipU = isFlick && direction == 5;
        var travel = travelOverride ?? (float)_projection.ScrollPixelsAtTicks(tick, currentTick);

        if (texture is not null)
        {
            // Use the same 3D card builder as the note renderer and size the circle
            // from the actual projected note width, not a fixed minimum or a rough
            // judge-lane estimate. The indicator therefore shrinks with the note.
            var vertices = BuildCardVertices(
                texture, projected, track, width, travel,
                _projection.NoteStandAngleDegrees, rotation, flipU,
                out _, null, null, null, side, bank, _projection.LaneNormalizedX(track));
            var a = _projection.ScreenPositionFromCameraSpace(vertices[0].Position);
            var b = _projection.ScreenPositionFromCameraSpace(vertices[1].Position);
            var c = _projection.ScreenPositionFromCameraSpace(vertices[2].Position);
            var d = _projection.ScreenPositionFromCameraSpace(vertices[5].Position);
            markerCenter = (a + b + c + d) * 0.25f;
            markerBoundsMin = Vector2.Min(Vector2.Min(a, b), Vector2.Min(c, d));
            markerBoundsMax = Vector2.Max(Vector2.Max(a, b), Vector2.Max(c, d));
            _quad3DVertexPool.Push(vertices);
            return Math.Max(1.25f, 0.25f * (Vector2.Distance(a, b) + Vector2.Distance(d, c)));
        }

        var fallbackHalfWidth = Math.Max(0.5f,
            width * projected.Scale * projected.LaneRotationDepthScale * 0.5f);
        var fallbackHalfHeight = fallbackHalfWidth * 0.75f;
        markerBoundsMin = markerCenter - new Vector2(fallbackHalfWidth, fallbackHalfHeight);
        markerBoundsMax = markerCenter + new Vector2(fallbackHalfWidth, fallbackHalfHeight);
        return Math.Max(1.25f, fallbackHalfWidth);
    }

    private bool TryGetEditorFreeformAnchor(Vector2 point, double currentTick, GearState currentState,
        out double tick, out int track, out float laneX, out string side, out string bank)
    {
        tick = currentTick;
        track = -1;
        laneX = 0f;
        side = "left";
        bank = "Forward";
        if (_chart is null || _tempo is null)
            return false;

        var snappedTickValue = FindClickedQuarterBeat(point, currentTick, currentState, out track);
        var position = snappedTickValue.HasValue ? GearState.Position(track) : null;
        var anchorTravelLow = 0f;
        var anchorTravelHigh = 0f;
        double lowSearchTick;
        double highSearchTick;
        if (position is not null)
        {
            side = position.Value.Side;
            bank = position.Value.Bank;
            // A real lane hit identifies the bank precisely; refine around the hit
            // as before so the marquee remains continuous rather than grid-snapped.
            var halfWindowTicks = Math.Max(12.0, GetEditorSnapTicks() * 2.0);
            lowSearchTick = Math.Max(0.0, snappedTickValue!.Value - halfWindowTicks);
            highSearchTick = snappedTickValue.Value + halfWindowTicks;
            anchorTravelLow = _projection.ScrollPixelsAtTicks(lowSearchTick, currentTick);
            anchorTravelHigh = _projection.ScrollPixelsAtTicks(highSearchTick, currentTick);
        }
        else
        {
            // Start/end points are allowed just outside the physical lane faces.
            // Find the closest projected bank slice over the visible travel range,
            // then anchor to that bank's continuous X coordinate. This removes the
            // old requirement that both mouse points land strictly inside a bank.
            var minTravel = -_projection.GetRenderDistanceBehind();
            var maxTravel = Math.Max(_projection.GetRenderDistanceAhead(),
                _projection.LaneTravelDistanceForNormalizedX(0f));
            const int samples = 32;
            var bestDistance = float.MaxValue;
            var bestCandidateTravel = 0f;
            string bestSide = "left";
            string bestBank = "Forward";
            var travelStep = Math.Max(1f, (maxTravel - minTravel) / samples);
            foreach (var candidateSide in Sides)
            foreach (var candidateBank in Banks)
            {
                for (var i = 0; i <= samples; i++)
                {
                    var travel = minTravel + (maxTravel - minTravel) * (i / (float)samples);
                    var slice = _projection.ProjectBankSliceAtTravel(travel, candidateSide, candidateBank);
                    var distance = PointToSegmentDistance(point, slice.Inner, slice.Outer);
                    if (distance >= bestDistance)
                        continue;
                    bestDistance = distance;
                    bestCandidateTravel = travel;
                    bestSide = candidateSide;
                    bestBank = candidateBank;
                }
            }

            side = bestSide;
            bank = bestBank;
            anchorTravelLow = Math.Max(minTravel, bestCandidateTravel - travelStep);
            anchorTravelHigh = Math.Min(maxTravel, bestCandidateTravel + travelStep);
            lowSearchTick = 0.0;
            highSearchTick = Math.Min(1_000_000_000.0,
                Math.Max(currentTick + 1_000_000.0, _chart.PlayTick + 1_000_000.0));
        }

        var anchorSide = side;
        var anchorBank = bank;
        var lo = Math.Min(anchorTravelLow, anchorTravelHigh);
        var hi = Math.Max(anchorTravelLow, anchorTravelHigh);

        float DistanceAt(float travel)
        {
            var slice = _projection.ProjectBankSliceAtTravel(travel, anchorSide, anchorBank);
            return PointToSegmentDistance(point, slice.Inner, slice.Outer);
        }

        for (var i = 0; i < (snappedTickValue.HasValue ? 24 : 16); i++)
        {
            var m1 = lo + (hi - lo) / 3f;
            var m2 = hi - (hi - lo) / 3f;
            if (DistanceAt(m1) <= DistanceAt(m2))
                hi = m2;
            else
                lo = m1;
        }

        var bestTravel = (lo + hi) * 0.5f;
        // Invert the real tempo-map scroll curve so the freeform anchor remains
        // attached to the same chart tick across tempo changes and seek operations.
        var lowTick = lowSearchTick;
        var highTick = highSearchTick;
        for (var i = 0; i < 40; i++)
        {
            var midTick = (lowTick + highTick) * 0.5;
            var midTravel = _projection.ScrollPixelsAtTicks(midTick, currentTick);
            if (midTravel < bestTravel)
                lowTick = midTick;
            else
                highTick = midTick;
        }
        tick = Math.Max(0.0, (lowTick + highTick) * 0.5);
        var bestSlice = _projection.ProjectBankSliceAtTravel(bestTravel, anchorSide, anchorBank);
        var across = bestSlice.Outer - bestSlice.Inner;
        var lengthSquared = across.LengthSquared();
        var fraction = lengthSquared > 0.000001f
            ? Math.Clamp(Vector2.Dot(point - bestSlice.Inner, across) / lengthSquared, 0f, 1f)
            : 0.5f;
        var bankEdges = _projection.LaneBankEdgesNormalized(anchorSide, anchorBank);
        var innerX = anchorSide == "left" ? bankEdges.Right : bankEdges.Left;
        var outerX = anchorSide == "left" ? bankEdges.Left : bankEdges.Right;
        laneX = MathHelper.Lerp(innerX, outerX, fraction);
        if (!snappedTickValue.HasValue)
        {
            var closestTrackDistance = float.MaxValue;
            for (var candidateTrack = 0; candidateTrack < 16; candidateTrack++)
            {
                var candidatePosition = GearState.Position(candidateTrack);
                if (candidatePosition is null ||
                    !string.Equals(candidatePosition.Value.Side, anchorSide, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(candidatePosition.Value.Bank, anchorBank, StringComparison.OrdinalIgnoreCase))
                    continue;
                var distance = MathF.Abs(_projection.LaneNormalizedX(candidateTrack) - laneX);
                if (distance >= closestTrackDistance)
                    continue;
                closestTrackDistance = distance;
                track = candidateTrack;
            }
            if (track < 0)
                track = 0;
        }
        return true;
    }

    private void ArmEditorRectangleSelection(Vector2 point, double currentTick, GearState currentState)
    {
        _editorDurationInputValuePending = false;
        _editorRectSelectionArmed = true;
        _editorRectStart = point;
        _editorRectCurrent = point;
        if (TryGetEditorFreeformAnchor(point, currentTick, currentState,
                out var rectangleStartTick, out var rectangleStartTrack, out var rectangleStartLaneX,
                out var rectangleStartSide, out var rectangleStartBank))
        {
            _editorRectStartTick = rectangleStartTick;
            _editorRectStartTrack = rectangleStartTrack;
            _editorRectStartLaneX = rectangleStartLaneX;
            _editorRectStartSide = rectangleStartSide;
            _editorRectStartBank = rectangleStartBank;
            _editorRectCurrentTick = rectangleStartTick;
            _editorRectCurrentTrack = rectangleStartTrack;
            _editorRectCurrentLaneX = rectangleStartLaneX;
            _editorRectCurrentSide = rectangleStartSide;
            _editorRectCurrentBank = rectangleStartBank;
        }
        else
        {
            _editorRectStartTick = currentTick;
            _editorRectStartTrack = Math.Clamp(_editorTrack, 0, 15);
            _editorRectStartLaneX = _projection.LaneNormalizedX(_editorRectStartTrack);
            var fallbackPosition = GearState.Position(_editorRectStartTrack);
            _editorRectStartSide = fallbackPosition?.Side ?? "left";
            _editorRectStartBank = fallbackPosition?.Bank ?? "Forward";
            _editorRectCurrentTick = _editorRectStartTick;
            _editorRectCurrentTrack = _editorRectStartTrack;
            _editorRectCurrentLaneX = _editorRectStartLaneX;
            _editorRectCurrentSide = _editorRectStartSide;
            _editorRectCurrentBank = _editorRectStartBank;
        }
        ClearEditorPendingRectangleUndoSnapshot();
    }

    private void UpdateEditorRectangleEndpoint(Vector2 point, double currentTick, GearState currentState, bool updatePointer)
    {
        if (updatePointer)
            _editorRectCurrent = point;

        if (_editorRectStartTrack < 0 || _editorRectStartTrack > 15)
            return;

        if (TryGetEditorFreeformAnchor(_editorRectCurrent, currentTick, currentState,
                out var endTick, out var endTrack, out var endLaneX, out var endSide, out var endBank))
        {
            _editorRectCurrentTick = endTick;
            _editorRectCurrentTrack = endTrack;
            _editorRectCurrentLaneX = endLaneX;
            _editorRectCurrentSide = endSide;
            _editorRectCurrentBank = endBank;
        }
    }

    private void FinishEditorRectangleSelection(double currentTick, GearState currentState)
    {
        if (_chart is null)
        {
            _editorRectSelecting = false;
            return;
        }

        var lowTick = Math.Min(_editorRectStartTick, _editorRectCurrentTick);
        var highTick = Math.Max(_editorRectStartTick, _editorRectCurrentTick);
        var startX = _editorRectStartLaneX;
        var endX = _editorRectCurrentLaneX;
        var lowX = Math.Min(startX, endX) - 0.0001f;
        var highX = Math.Max(startX, endX) + 0.0001f;
        ClearEditorMultiSelection();
        _editorSelectedGridCells.Clear();
        var selectedTracks = Enumerable.Range(0, 16)
            .Where(track => IsEditorTrackVisible(track, currentState))
            .Where(track =>
            {
                var laneX = _projection.LaneNormalizedX(track);
                return laneX >= lowX && laneX <= highX;
            })
            .ToList();

        foreach (var ev in _chart.Events)
        {
            if (ev.Kind != "Note" || ev.RawType != 1 ||
                ev.Tick < lowTick || ev.Tick > highTick ||
                !ShouldIncludeRenderableEvent(ev, currentState))
                continue;

            // Use lane coordinates rather than screen-space projection/culling.
            // Notes can now be selected even if playback or seeking has scrolled
            // them beyond the visible screen while the marquee remained anchored.
            var laneX = _projection.LaneNormalizedX(ev.Track);
            if (laneX >= lowX && laneX <= highX)
                AddEditorMultiSelectedEvent(ev);
        }

        // EDIT GROUP only permits individual editing in one source instance at
        // a time. A marquee spanning anything outside that instance exits the mode
        // and returns to normal whole-instance selection for all touched groups.
        if (_editorNoteGroupEditMode && _editorEditingNoteGroupId is int editingGroupId &&
            _editorEditingNoteGroupInstanceId is int editingInstanceId &&
            _editorMultiSelectedEvents.Any(ev => ev.NoteGroupId != editingGroupId ||
                ev.NoteGroupInstanceId != editingInstanceId))
        {
            _editorNoteGroupEditMode = false;
            _editorEditingNoteGroupId = null;
            _editorEditingNoteGroupInstanceId = null;
            _statusMessage = "Exited EDIT GROUP because the selection includes notes outside the active instance.";
        }

        // In normal mode, a rectangle that touches a grouped instance selects the
        // entire instance. EDIT GROUP keeps individual note selection unlocked.
        ExpandEditorNoteGroupSelection(_editorMultiSelectedEvents);

        // A rectangle selects notes, not every empty grid cell crossed by the range.
        // Keep occupied note coordinates for the SELECT X=SELECTED filter.
        foreach (var ev in _editorMultiSelectedEvents)
            _editorSelectedGridCells.Add((ev.Tick, ev.Track));

        _editorRectSelecting = false;
        _editorRectSelectionArmed = false;
        ClearEditorPendingRectangleUndoSnapshot();
        _editorPanelsSelectedEmptyCell = null;
        _editorSelectReferenceLineTick = _editorMenuSection == EditorMenuSection.Select && _editorSelectedGridCells.Count > 0
            ? _editorSelectedGridCells.Min(cell => cell.Tick)
            : null;
        _selectedEventKey = null;
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorSelectedLinkRoot = null;
        _editorSelectedHoldEnd = false;
        _statusMessage = $"Rectangle selected {_editorMultiSelectedEvents.Count} note(s) across {selectedTracks.Count} track(s); empty cells ignored.";
    }

    private void DrawEditorMarker(
        Vector2 position, float radius, Color color,
        float fillOpacity = 0.12f, float outlineOpacity = 1f)
    {
        // Keep fill and outline opacity independent so empty-cell hover can remain
        // visible without drawing a strong white ring over the lane.
        // Multi-selection uses the reusable triangle batch below instead of this path.
        DrawCircle(_spriteBatch, position, radius, color * fillOpacity);
        DrawCircleOutline(_spriteBatch, position, radius,
            Math.Clamp(radius * 0.16f, 1f, 2.25f), color * outlineOpacity);
    }

    private static Vector2[] CreateEditorMultiSelectionMarkerUnitCircle()
    {
        var points = new Vector2[EditorMultiSelectionMarkerSegments];
        for (var i = 0; i < points.Length; i++)
        {
            var angle = MathHelper.TwoPi * i / points.Length;
            points[i] = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        }
        return points;
    }

    private void AddEditorMultiSelectionMarker(Vector2 center, float radius, Color color, float fillOpacity)
    {
        radius = Math.Max(0.75f, radius);
        var borderWidth = Math.Clamp(radius * 0.16f, 1f, 2.25f);
        var innerRadius = Math.Max(0f, radius - borderWidth);
        const int segments = EditorMultiSelectionMarkerSegments;
        const int verticesPerSegment = 9; // One fill triangle and two ring triangles.
        var required = _editorMultiSelectionMarkerVertexCount + segments * verticesPerSegment;
        if (required > _editorMultiSelectionMarkerVertices.Length)
        {
            var capacity = _editorMultiSelectionMarkerVertices.Length;
            while (capacity < required)
                capacity = checked(capacity * 2);
            Array.Resize(ref _editorMultiSelectionMarkerVertices, capacity);
        }

        var fillColor = color * fillOpacity;
        var unitCircle = EditorMultiSelectionMarkerUnitCircle;
        for (var i = 0; i < segments; i++)
        {
            var unitA = unitCircle[i];
            var unitB = unitCircle[(i + 1) % segments];
            var outerA = center + unitA * radius;
            var outerB = center + unitB * radius;
            var innerA = center + unitA * innerRadius;
            var innerB = center + unitB * innerRadius;

            // Fill disk, then opaque annulus. Both are submitted in the same draw.
            AddEditorMultiSelectionTriangle(center, outerA, outerB, fillColor);
            AddEditorMultiSelectionTriangle(outerA, outerB, innerB, color);
            AddEditorMultiSelectionTriangle(outerA, innerB, innerA, color);
        }
    }

    private void AddEditorMultiSelectionTriangle(Vector2 a, Vector2 b, Vector2 c, Color color)
    {
        var offset = _editorMultiSelectionMarkerVertexCount;
        _editorMultiSelectionMarkerVertices[offset] = new VertexPositionColor(new Vector3(a, 0f), color);
        _editorMultiSelectionMarkerVertices[offset + 1] = new VertexPositionColor(new Vector3(b, 0f), color);
        _editorMultiSelectionMarkerVertices[offset + 2] = new VertexPositionColor(new Vector3(c, 0f), color);
        _editorMultiSelectionMarkerVertexCount += 3;
    }

    private void DrawEditorMultiSelectionMarkerBatch()
    {
        var vertexCount = _editorMultiSelectionMarkerVertexCount;
        _editorMultiSelectionMarkerVertexCount = 0;
        if (vertexCount < 3)
            return;

        _graphics.BlendState = BlendState.AlphaBlend;
        _graphics.DepthStencilState = DepthStencilState.None;
        _graphics.RasterizerState = RasterizerState.CullNone;
        _primitiveEffect.World = Matrix.Identity;
        _primitiveEffect.View = Matrix.Identity;
        _primitiveEffect.Projection = Matrix.CreateOrthographicOffCenter(
            0f, _graphics.Viewport.Width, _graphics.Viewport.Height, 0f, -1f, 1f);
        foreach (var pass in _primitiveEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            _graphics.DrawUserPrimitives(PrimitiveType.TriangleList,
                _editorMultiSelectionMarkerVertices, 0, vertexCount / 3);
        }
    }

    // Event-backed selection markers always use the exact same card projection as
    // the single-selection marker. Keep group and ordinary-note selection on one
    // path so perspective, lane rotation, standing angle, and flick orientation
    // cannot drift between the two renderers.
    private float GetEditorSelectionHighlightRadius(
        int track, int tick, ChartEvent note, double currentTick, GearState currentState,
        ProjectedPoint projected, float travel, out Vector2 markerCenter)
        => GetEditorMarkerRadius(
            track, tick, currentTick, currentState, out markerCenter, note, projected, travel);

    // Used only when there is no authored event/3D basis yet (the group-placement
    // hover target). Event-backed selection highlights use the exact projected
    // card geometry above.
    private float GetEditorPlacementPreviewMarkerRadius(float noteWidth, ProjectedPoint projected)
    {
        var projectedRadius = noteWidth * projected.Scale * projected.LaneRotationDepthScale * 0.5f;
        return float.IsFinite(projectedRadius) ? Math.Max(1.25f, projectedRadius) : 1.25f;
    }

    private static readonly Color EditorGroupLockIndicatorColor = new(255, 180, 64);

    private bool TryGetEditorNoteGroupSelectionLock(out (int GroupId, int InstanceId) instance)
    {
        instance = default;
        if (!_chartEditorMode || _chart is null || _editorNoteGroupEditMode ||
            _editorNoteGroupLibrarySelectionExplicit || _editorSelectedNoteGroupId is not int groupId)
            return false;

        var selectedInstance = GetSelectedEditorNoteGroupInstance();
        if (selectedInstance is not { } selected || selected.GroupId != groupId)
            return false;

        instance = selected;
        return true;
    }

    // Reuse the same compact pointer-following label for SuperFlick and group-mode
    // feedback so these indicators stay visually consistent in size, placement,
    // background, border thickness, and viewport-edge handling.
    private void DrawEditorHoverIndicator(
        string text, Vector2 markerCenter, float markerRadius, Color borderColor, Color textColor)
    {
        var safeText = FontSafeText(_font, text);
        var textSize = UnicodeTextRenderer.MeasureString(_font, safeText);
        var padding = Math.Max(4f, _projection.ScalePixels(6f));
        var labelWidth = (int)MathF.Ceiling(textSize.X + padding * 2);
        var labelHeight = (int)MathF.Ceiling(textSize.Y + padding * 2);
        var viewportWidth = _graphics.Viewport.Width;
        var viewportHeight = _graphics.Viewport.Height;
        labelWidth = Math.Min(labelWidth, Math.Max(1, viewportWidth - 8));
        var labelX = (int)MathF.Round(markerCenter.X + markerRadius + padding);
        if (labelX + labelWidth > viewportWidth - 4)
            labelX = Math.Max(4, (int)MathF.Round(markerCenter.X - markerRadius - padding - labelWidth));
        var labelY = Math.Clamp((int)MathF.Round(markerCenter.Y - labelHeight * 0.5f),
            4, Math.Max(4, viewportHeight - labelHeight - 4));
        var labelBounds = new Rectangle(labelX, labelY, labelWidth, labelHeight);
        _spriteBatch.Draw(_pixel, labelBounds, new Color(10, 10, 11, 238));
        var thickness = Math.Max(1, (int)MathF.Round(_projection.ScalePixels(1f)));
        _spriteBatch.Draw(_pixel, new Rectangle(labelBounds.X, labelBounds.Y, labelBounds.Width, thickness), borderColor);
        _spriteBatch.Draw(_pixel, new Rectangle(labelBounds.X, labelBounds.Bottom - thickness, labelBounds.Width, thickness), borderColor);
        _spriteBatch.Draw(_pixel, new Rectangle(labelBounds.X, labelBounds.Y, thickness, labelBounds.Height), borderColor);
        _spriteBatch.Draw(_pixel, new Rectangle(labelBounds.Right - thickness, labelBounds.Y, thickness, labelBounds.Height), borderColor);
        UnicodeTextRenderer.DrawString(_spriteBatch, _font, safeText,
            new Vector2(labelBounds.X + padding, labelBounds.Y + padding), textColor);
    }

    // The selections are real meshes on the chart's 3D lane surface. These points
    // are camera-space positions, so the same perspective transform used by notes
    // and lane geometry is applied when the mesh is submitted.
    private Vector3 GetEditorSelectionPoint3D(
        float normalizedX, double tick, double currentTick, string side, string bank)
    {
        var travelPixels = _projection.ScrollPixelsAtTicks(tick, currentTick);
        return _projection.BuildStandingNoteBasis(
            normalizedX, travelPixels, side, bank).Center;
    }

    private static bool IsFiniteEditorSelectionPoint(Vector3 point)
        => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);

    private static void AddEditorSelectionTriangle(
        VertexPositionColor[] vertices, ref int offset,
        Vector3 a, Vector3 b, Vector3 c, Color color)
    {
        vertices[offset++] = new VertexPositionColor(a, color);
        vertices[offset++] = new VertexPositionColor(b, color);
        vertices[offset++] = new VertexPositionColor(c, color);
    }

    private int AppendEditorSelectionQuad3D(
        int offset, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
        Color fill, Color outline)
    {
        // The selection surface can scroll past the judge row and approach the
        // camera while a drag is held. Clip the polygon against the same near plane
        // used by the 3D projection instead of rejecting the entire quad when only
        // one corner crosses it. This also keeps the other editor quad overlays
        // visible for as long as any part of their surface remains in front of it.
        Span<Vector3> source = stackalloc Vector3[4] { p0, p1, p2, p3 };
        Span<Vector3> clipped = stackalloc Vector3[8];
        var clippedCount = 0;

        for (var edgeIndex = 0; edgeIndex < source.Length; edgeIndex++)
        {
            var a = source[edgeIndex];
            var b = source[(edgeIndex + 1) % source.Length];
            var aInside = a.Z <= -EditorSelection3DNearPlane;
            var bInside = b.Z <= -EditorSelection3DNearPlane;

            if (aInside)
                clipped[clippedCount++] = a;

            if (aInside == bInside)
                continue;

            var denominator = b.Z - a.Z;
            if (MathF.Abs(denominator) < 0.000001f)
                continue;

            var t = Math.Clamp((-EditorSelection3DNearPlane - a.Z) / denominator, 0f, 1f);
            clipped[clippedCount++] = Vector3.Lerp(a, b, t);
        }

        if (clippedCount < 3)
            return offset;

        var vertices = _editorSelection3DVertices;
        var center = Vector3.Zero;
        for (var i = 0; i < clippedCount; i++)
            center += clipped[i];
        center /= clippedCount;

        // A clipped quad can become a triangle or pentagon. Fan triangulation keeps
        // the fill continuous without adding a camera-facing billboard.
        for (var i = 1; i < clippedCount - 1; i++)
            AddEditorSelectionTriangle(vertices, ref offset,
                clipped[0], clipped[i], clipped[i + 1], fill);

        // A border is a narrow strip that lies in the same 3D face, not a
        // camera-facing SpriteBatch line. Its width stays near 1.5 screen px at
        // every depth by converting pixels to local camera-space units.
        for (var edgeIndex = 0; edgeIndex < clippedCount; edgeIndex++)
        {
            var a = clipped[edgeIndex];
            var b = clipped[(edgeIndex + 1) % clippedCount];
            var edge = b - a;
            var edgeLengthSquared = edge.LengthSquared();
            if (edgeLengthSquared < 0.000001f)
                continue;

            var midpoint = (a + b) * 0.5f;
            var toCenter = center - midpoint;
            var inward = toCenter - edge * (Vector3.Dot(toCenter, edge) / edgeLengthSquared);
            var inwardDistance = inward.Length();
            if (inwardDistance < 0.00001f)
                continue;

            inward /= inwardDistance;
            var pixelsPerWorld = _projection.ScreenPixelsPerWorldUnit(midpoint, inward);
            if (!float.IsFinite(pixelsPerWorld) || pixelsPerWorld < 0.0001f)
                continue;

            var stripWidth = Math.Min(1.5f / pixelsPerWorld, inwardDistance * 0.35f);
            var aInner = a + inward * stripWidth;
            var bInner = b + inward * stripWidth;
            AddEditorSelectionTriangle(vertices, ref offset, a, b, bInner, outline);
            AddEditorSelectionTriangle(vertices, ref offset, a, bInner, aInner, outline);
        }

        return offset;
    }

    private void DrawEditorSelectionGeometry3D(int vertexCount)
    {
        if (vertexCount < 3)
            return;

        _graphics.BlendState = BlendState.NonPremultiplied;
        _graphics.DepthStencilState = DepthStencilState.None;
        _graphics.RasterizerState = RasterizerState.CullNone;
        _primitiveEffect.World = Matrix.Identity;
        _primitiveEffect.View = Matrix.Identity;

        var viewportWidth = Math.Max(1f, (float)_graphics.Viewport.Width);
        var viewportHeight = Math.Max(1f, (float)_graphics.Viewport.Height);
        var halfWidth = viewportWidth * 0.5f;
        var halfHeight = viewportHeight * 0.5f;
        var focalLength = _projection.FocalLength;
        var nearPlane = EditorSelection3DNearPlane;
        var nearHalfWidth = halfWidth * nearPlane / focalLength;
        var nearHalfHeight = halfHeight * nearPlane / focalLength;
        var maxCameraDistance = 1024f;
        for (var i = 0; i < vertexCount; i++)
            maxCameraDistance = Math.Max(maxCameraDistance, -_editorSelection3DVertices[i].Position.Z * 1.25f);

        // CreatePerspectiveOffCenter expects its bounds at the near plane. Scale
        // them with nearPlane so the camera-space projection remains identical to
        // ScreenPositionFromCameraSpace when the near plane is brought closer.
        // Leaving these bounds at the old one-unit scale makes the frustum too
        // narrow by 1 / nearPlane and causes every editor quad to disappear.
        _primitiveEffect.Projection = Matrix.CreatePerspectiveOffCenter(
            -nearHalfWidth,
            nearHalfWidth,
            -nearHalfHeight,
            nearHalfHeight,
            nearPlane,
            Math.Max(maxCameraDistance, _projection.StandingNoteRenderFarClip()));

        foreach (var pass in _primitiveEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            _graphics.DrawUserPrimitives(
                PrimitiveType.TriangleList, _editorSelection3DVertices, 0, vertexCount / 3);
        }
    }

    private int AppendEditorLanTestSegmentOverlay(int offset, double currentTick, GearState currentState)
    {
        if (_chart is null || _tempo is null)
            return offset;
        var startTick = GetEditorLanTestSegmentDisplayTick(isStart: true);
        var endTick = GetEditorLanTestSegmentDisplayTick(isStart: false);
        if (endTick <= startTick)
            return offset;

        // Keep geometry near the current camera view. Scrolling the chart reveals
        // the next part of a longer segment without creating a huge far plane.
        var visibleStart = Math.Max(startTick, currentTick - 2000.0);
        var visibleEnd = Math.Min(endTick, currentTick + 2000.0);
        if (visibleEnd <= visibleStart)
            return offset;

        if (_compact)
        {
            // Collapsed lanes display only the banks active at the current editor
            // playhead. All three banks on each side share the same screen-space
            // position, so drawing the hidden banks would stack the translucent
            // fill and make it more opaque. Reuse the exact state passed to the lane
            // renderer and draw one bank per side.
            foreach (var side in Sides)
            {
                var bank = GearTimeline.ActiveBankForSide(currentState, side);
                if (string.Equals(bank, "Inactive", StringComparison.OrdinalIgnoreCase))
                    continue;

                offset = AppendEditorLanTestSegmentBank(
                    offset, side, bank, visibleStart, visibleEnd, currentTick);
            }
            return offset;
        }

        // Expanded mode gives each bank its own physical lane space, so all banks
        // remain highlighted as before.
        foreach (var (firstTrack, _) in EditorLanTestSegmentBanks)
        {
            var firstPosition = GearState.Position(firstTrack);
            if (firstPosition is null)
                continue;

            offset = AppendEditorLanTestSegmentBank(
                offset, firstPosition.Value.Side, firstPosition.Value.Bank,
                visibleStart, visibleEnd, currentTick);
        }
        return offset;
    }

    private int AppendEditorLanTestSegmentBank(
        int offset, string side, string bank,
        double startTick, double endTick, double currentTick)
    {
        // Track centers leave half a track-width unhighlighted at either edge.
        // Use the canonical bank edges instead, so the segment overlay covers
        // the complete physical bank surface, including its outer margins.
        var bankEdges = _projection.LaneBankEdgesNormalized(side, bank);
        var p0 = GetEditorSelectionPoint3D(bankEdges.Left, startTick, currentTick, side, bank);
        var p1 = GetEditorSelectionPoint3D(bankEdges.Right, startTick, currentTick, side, bank);
        var p2 = GetEditorSelectionPoint3D(bankEdges.Right, endTick, currentTick, side, bank);
        var p3 = GetEditorSelectionPoint3D(bankEdges.Left, endTick, currentTick, side, bank);
        if (!IsFiniteEditorSelectionPoint(p0) || !IsFiniteEditorSelectionPoint(p1) ||
            !IsFiniteEditorSelectionPoint(p2) || !IsFiniteEditorSelectionPoint(p3))
            return offset;

        return AppendEditorSelectionQuad3D(offset, p0, p1, p2, p3,
            new Color(255, 215, 0, 30), new Color(255, 225, 35, 220));
    }

    private void DrawSelectionOverlay(double currentTick, GearState currentState)
    {
        if (_chart is null || _tempo is null)
            return;

        _editorMultiSelectionMarkerVertexCount = 0;
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);

        if (_selectedEventKey.HasValue)
        {
            ChartEvent? selectedEvent;
            if (_eventsByKey.TryGetValue(_selectedEventKey.Value, out var cachedSelectedEvent))
            {
                selectedEvent = cachedSelectedEvent;
            }
            else
            {
                // Newly placed or transiently re-keyed editor events may not have
                // reached RebuildRenderEventCache yet. Preserve the old lookup as
                // a rare fallback instead of scanning the chart every frame.
                selectedEvent = _chart.Events.FirstOrDefault(e => KeyOf(e) == _selectedEventKey.Value)
                                ?? _editorDraggedEvent;
            }
            // This overlay belongs to the gameplay-lane view. Auxiliary tracks
            // (including panels 17–20 and metadata track 16) must never be
            // projected onto the gameplay lanes, even if they remain selected
            // while the user switches views.
            if ((selectedEvent is not null && (selectedEvent.Track is < 0 or > 15)) ||
                (selectedEvent is not null && !ShouldRenderInCurrentBank(selectedEvent, currentState)))
                selectedEvent = null;
            if (selectedEvent is not null && _editorSelectedLinkRoot is not null)
            {
                // Selecting any node selects the whole chain visually. Keep using
                // event references while a drag is in progress because its tick/key
                // changes continuously until release.
                var chainNodes = GetEditorLinkChainNodes(_editorSelectedLinkRoot);
                for (var i = 0; i < chainNodes.Count; i++)
                {
                    var node = chainNodes[i];
                    var isChainEnd = i == chainNodes.Count - 1;
                    if (!ShouldRenderInCurrentBank(node, currentState))
                        continue;
                    var nodeTravel = (float)_projection.ScrollPixelsAtTicks(node.Tick, currentTick);
                    if (_projection.WithinRenderDistance(nodeTravel))
                    {
                        var nodePoint = _projection.ProjectAtTravel(node.Track, nodeTravel, currentState);
                        if (!OutsideNoteView(nodePoint, nodePoint))
                        {
                            var nodeRadius = GetEditorSelectionHighlightRadius(
                                node.Track, node.Tick, node, currentTick, currentState,
                                nodePoint, nodeTravel, out var nodeCenter);
                            var selectedHoldEndNode = _editorSelectedHoldEnd && selectedEvent is not null &&
                                ReferenceEquals(node, GetEditorNextLinkPoint(selectedEvent));
                            var draggedHoldEndNode = _editorDragIsHoldEnd && _editorDraggedEvent is not null &&
                                ReferenceEquals(node, GetEditorNextLinkPoint(_editorDraggedEvent));
                            var directNodeSelected =
                                (ReferenceEquals(node, selectedEvent) && !_editorSelectedHoldEnd) ||
                                (ReferenceEquals(node, _editorDraggedEvent) && !_editorDragIsHoldEnd) ||
                                selectedHoldEndNode || draggedHoldEndNode;
                            var selectedColor = directNodeSelected
                                ? GetEditorSelectedNoteHighlightColor(node)
                                : isChainEnd ? EditorEndingColor : EditorFamilyColor;
                            // Link tails are endpoint markers, so keep their highlight
                            // visually secondary to the note heads while preserving the
                            // normal size for every other note marker.
                            DrawEditorMarker(nodeCenter,
                                node.NoteType == NoteType.LinkTail ? nodeRadius * 0.5f : nodeRadius,
                                selectedColor, 0.14f);
                        }
                    }

                    var nextNode = i + 1 < chainNodes.Count ? chainNodes[i + 1] : null;
                    var endpointTick = nextNode?.Tick ?? node.EndTick;
                    if (nextNode is not null || node.Duration > 6)
                    {
                        var endDepth = (float)_projection.ScrollPixelsAtTicks(endpointTick, currentTick);
                        if (_projection.WithinRenderDistance(endDepth))
                        {
                            var endPoint = _projection.ProjectAtTravel(node.Track, endDepth, currentState);
                            if (!OutsideNoteView(endPoint, endPoint))
                            {
                                var endRadius = GetEditorSelectionHighlightRadius(
                                    node.Track, endpointTick, node, currentTick, currentState,
                                    endPoint, endDepth, out var endCenter);
                                var directEndSelected =
                                    (ReferenceEquals(node, selectedEvent) && _editorSelectedHoldEnd) ||
                                    (ReferenceEquals(node, _editorDraggedEvent) && _editorDragIsHoldEnd);
                                var nextNodeDirectlySelected = nextNode is not null &&
                                    ReferenceEquals(nextNode, selectedEvent) && !_editorSelectedHoldEnd;
                                var endColor = directEndSelected
                                    ? GetEditorSelectedNoteHighlightColor(node)
                                    : nextNodeDirectlySelected && nextNode is not null
                                        ? GetEditorSelectedNoteHighlightColor(nextNode)
                                        : nextNode is null ? EditorEndingColor : EditorFamilyColor;
                                DrawEditorMarker(endCenter, endRadius * 0.5f, endColor, 0.15f);
                            }
                        }
                    }
                }
            }
            else if (selectedEvent is not null)
            {
                var selectedTravel = (float)_projection.ScrollPixelsAtTicks(selectedEvent.Tick, currentTick);
                if (_projection.WithinRenderDistance(selectedTravel))
                {
                    var point = _projection.ProjectAtTravel(selectedEvent.Track, selectedTravel, currentState);
                    if (!OutsideNoteView(point, point))
                    {
                        var radius = GetEditorSelectionHighlightRadius(
                            selectedEvent.Track, selectedEvent.Tick, selectedEvent, currentTick, currentState,
                            point, selectedTravel, out var markerCenter);
                        DrawEditorMarker(markerCenter,
                            selectedEvent.NoteType == NoteType.LinkTail ? radius * 0.5f : radius,
                            GetEditorSelectedNoteHighlightColor(selectedEvent), 0.10f);
                    }
                }

                // Long-note endpoints (including terminal link tails selected before
                // their chain root could be resolved) can be dragged independently.
                var isLinkPoint = selectedEvent.NoteType is NoteType.LinkHead or NoteType.LinkTail;
                var nextPoint = GetEditorNextLinkPoint(selectedEvent);
                var endpointTick = nextPoint?.Tick ?? selectedEvent.EndTick;
                if ((nextPoint is not null || selectedEvent.Duration > 6) &&
                    (selectedEvent.IsHoldNote || isLinkPoint))
                {
                    var endpointTrack = nextPoint?.Track ?? selectedEvent.Track;
                    var endDepth = (float)_projection.ScrollPixelsAtTicks(endpointTick, currentTick);
                    if (_projection.WithinRenderDistance(endDepth))
                    {
                        var endPoint = _projection.ProjectAtTravel(endpointTrack, endDepth, currentState);
                        if (!OutsideNoteView(endPoint, endPoint))
                        {
                            var endRadius = GetEditorSelectionHighlightRadius(
                                endpointTrack, endpointTick, selectedEvent, currentTick, currentState,
                                endPoint, endDepth, out var endCenter);
                            var endColor = _editorSelectedHoldEnd || _editorDragIsHoldEnd
                                ? GetEditorSelectedNoteHighlightColor(selectedEvent)
                                : EditorEndingColor;
                            DrawEditorMarker(endCenter, endRadius * 0.5f, endColor, 0.13f);
                        }
                    }
                }
            }
        }

        // Mark the point produced by the connector split preview. It uses the same
        // compact half-size ring as a LinkTail selection marker, and is drawn after
        // the preview geometry so it remains easy to spot over the preview texture.
        if (!_editorPanelsViewEnabled && _editorConnectorMode &&
            _editorConnectorSplitPreviewRoot is { } splitPreviewRoot &&
            ShouldRenderInCurrentBank(splitPreviewRoot, currentState) &&
            _editorConnectorSplitPreviewNodes is { Count: > 1 } splitPreviewNodes)
        {
            var markerNode = _editorConnectorSplitPreviewMarkerNode;
            if (markerNode is not null && !ShouldRenderInCurrentBank(markerNode, currentState))
                markerNode = null;
            var markerStyleNode = markerNode ??
                splitPreviewNodes.FirstOrDefault(node => node.NoteType == NoteType.LinkTail);
            var markerTick = markerNode?.Tick ?? _hoverBeatTick ?? -1;
            var markerTrack = markerNode?.Track ?? _hoverTrack ?? -1;
            if (markerTick >= 0 && markerTrack is >= 0 and <= 15)
            {
                var markerTravel = (float)_projection.ScrollPixelsAtTicks(markerTick, currentTick);
                if (_projection.WithinRenderDistance(markerTravel))
                {
                    var markerPoint = _projection.ProjectAtTicks(markerTrack, markerTick, currentTick, currentState);
                    if (!OutsideNoteView(markerPoint, markerPoint))
                    {
                        var markerRadius = GetEditorMarkerRadius(
                            markerTrack, markerTick, currentTick, currentState,
                            out var markerCenter, markerStyleNode, markerPoint, markerTravel);
                        DrawEditorMarker(markerCenter, markerRadius * 0.5f, EditorFamilyColor, 0.17f);
                    }
                }
            }
        }

        if (_editorMultiSelectedEvents.Count > 0)
        {
            // DrawNotes populated this compact list while it was already walking
            // the normal render-distance range (including both editor bank passes).
            // Only visible selected notes reach this loop; the selection HashSet and
            // link-chain maps are not traversed again here.
            var renderItems = _editorMultiSelectionRenderItems;
            var currentScrollUnits = GetEditorAwareScrollCoordinate(currentTick);

            for (var renderIndex = 0; renderIndex < renderItems.Count; renderIndex++)
            {
                var item = renderItems[renderIndex];
                var eventIndex = item.EventIndex;
                var note = _orderedRenderableEvents[eventIndex];
                var isDirectlySelected = item.IsDirectlySelected;
                var isInSelectedChain = item.IsInSelectedChain;
                var isChainEnd = item.IsChainEnd;

                var travel = _projection.ScrollPixelsFromCachedScrollUnits(
                    _eventScrollUnits[eventIndex], currentScrollUnits);
                var headVisible = _projection.WithinRenderDistance((float)travel);
                var hasSeparateEnd = !isInSelectedChain &&
                    (note.IsHoldNote || note.NoteType is NoteType.LinkHead or NoteType.LinkTail) &&
                    note.Duration > 6;
                var endTravel = hasSeparateEnd
                    ? _projection.ScrollPixelsFromCachedScrollUnits(
                        _eventEffectiveEndScrollUnits[eventIndex], currentScrollUnits)
                    : 0f;
                var endVisible = hasSeparateEnd && _projection.WithinRenderDistance((float)endTravel);
                if (!headVisible && !endVisible)
                    continue;

                if (headVisible)
                {
                    var projected = _projection.ProjectAtTravel(note.Track, (float)travel, currentState);
                    if (!OutsideNoteView(projected, projected))
                    {
                        var radius = GetEditorMarkerRadius(
                            note.Track, note.Tick, currentTick, currentState,
                            out var markerCenter, note, projected, (float)travel);
                        var color = isDirectlySelected
                            ? GetEditorSelectedNoteHighlightColor(note)
                            : isChainEnd ? EditorEndingColor : EditorFamilyColor;
                        AddEditorMultiSelectionMarker(markerCenter,
                            note.NoteType == NoteType.LinkTail ? radius * 0.5f : radius,
                            color, 0.14f);
                    }
                }

                if (endVisible)
                {
                    var endPoint = _projection.ProjectAtTravel(note.Track, (float)endTravel, currentState);
                    if (!OutsideNoteView(endPoint, endPoint))
                    {
                        var endRadius = GetEditorMarkerRadius(
                            note.Track, note.Tick, currentTick, currentState,
                            out var endCenter, note, endPoint, (float)endTravel);
                        var endColor = _editorSelectedHoldEnd
                            ? GetEditorSelectedNoteHighlightColor(note)
                            : EditorEndingColor;
                        AddEditorMultiSelectionMarker(endCenter,
                            endRadius * 0.5f, endColor, 0.13f);
                    }
                }
            }

            if (_editorMultiSelectionMarkerVertexCount > 0)
            {
                _spriteBatch.End();
                DrawEditorMultiSelectionMarkerBatch();
                _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);
            }
        }

        // The committed SELECT reference is a tick line, not a filled grid-range
        // rectangle. Reuse the same bank-slice projection as beat and tempo lines so
        // it follows the real 3D lane geometry. It is visible only inside SELECT.
        if (_chartEditorMode && !_editorPanelsViewEnabled &&
            _editorMenuSection == EditorMenuSection.Select &&
            _editorSelectReferenceLineTick is int selectLineTick)
        {
            var travelPixels = (float)_projection.ScrollPixelsAtTicks(selectLineTick, currentTick);
            if (_projection.WithinRenderDistance(travelPixels))
            {
                CollectEditorSelectLineEdges(_projectedBankLineEdges, travelPixels, currentState);
                if (_projectedBankLineEdges.Count >= 2)
                {
                    DrawConnectedBankLine(_projectedBankLineEdges, 7f, new Color(0, 83, 101, 235));
                    DrawConnectedBankLine(_projectedBankLineEdges, 4.5f, new Color(0, 242, 255, 255));
                    DrawConnectedBankLine(_projectedBankLineEdges, 1.25f, new Color(235, 255, 255, 255));
                }
            }
        }

        var hasSelectionGeometry = !_editorPanelsViewEnabled &&
            _editorMenuSection != EditorMenuSection.Select &&
            (_editorRectSelecting && _editorRectStartTrack >= 0 && _editorRectCurrentTrack >= 0);
        var hasTestSegmentGeometry = _chartEditorMode && _chart is not null && _tempo is not null &&
            !_editorPanelsViewEnabled && !_editorPreviewMode && IsEditorLanTestSegmentActive &&
            GetEditorLanTestSegmentDisplayTick(isStart: true) < currentTick + 2000.0 &&
            GetEditorLanTestSegmentDisplayTick(isStart: false) > currentTick - 2000.0;
        if (hasSelectionGeometry || hasTestSegmentGeometry)
        {
            // Flush the already queued 2D markers before drawing the selection quads
            // with the real perspective 3D renderer. Re-enter SpriteBatch afterward for
            // the remaining HUD-style editor markers. Avoid this state transition in
            // ordinary gameplay frames when neither selection rectangle is visible.
            _spriteBatch.End();
            var selectionVertexCount = 0;


            if (_editorRectSelecting && _editorRectStartTrack >= 0 && _editorRectCurrentTrack >= 0)
            {
                if (_topDownViewEnabled)
                {
                    // Preserve the original continuous rectangle in top-down 2D.
                    // In this view the banks are laid out side-by-side, so one quad
                    // naturally covers the selected range across the flat surface.
                    var p0 = GetEditorSelectionPoint3D(_editorRectStartLaneX, _editorRectStartTick,
                        currentTick, _editorRectStartSide, _editorRectStartBank);
                    var p1 = GetEditorSelectionPoint3D(_editorRectCurrentLaneX, _editorRectStartTick,
                        currentTick, _editorRectCurrentSide, _editorRectCurrentBank);
                    var p2 = GetEditorSelectionPoint3D(_editorRectCurrentLaneX, _editorRectCurrentTick,
                        currentTick, _editorRectCurrentSide, _editorRectCurrentBank);
                    var p3 = GetEditorSelectionPoint3D(_editorRectStartLaneX, _editorRectCurrentTick,
                        currentTick, _editorRectStartSide, _editorRectStartBank);

                    if (IsFiniteEditorSelectionPoint(p0) && IsFiniteEditorSelectionPoint(p1) &&
                        IsFiniteEditorSelectionPoint(p2) && IsFiniteEditorSelectionPoint(p3))
                    {
                        selectionVertexCount = AppendEditorSelectionQuad3D(
                            selectionVertexCount, p0, p1, p2, p3,
                            new Color(255, 165, 0, 64), EditorSelectedColor);
                    }
                }
                else
                {
                    // In 3D, each side is its own hinged bank surface. Draw a
                    // separate quad per active side so the marquee follows the
                    // V-shaped lane instead of bridging the hinge as one plank.
                    var selectionLowX = Math.Min(_editorRectStartLaneX, _editorRectCurrentLaneX);
                    var selectionHighX = Math.Max(_editorRectStartLaneX, _editorRectCurrentLaneX);
                    var selectionLowTick = Math.Min(_editorRectStartTick, _editorRectCurrentTick);
                    var selectionHighTick = Math.Max(_editorRectStartTick, _editorRectCurrentTick);

                    foreach (var side in Sides)
                    {
                        var bank = GearTimeline.ActiveBankForSide(currentState, side);
                        if (string.Equals(bank, "Inactive", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var bankEdges = _projection.LaneBankEdgesNormalized(side, bank);
                        var leftX = Math.Max(selectionLowX, bankEdges.Left);
                        var rightX = Math.Min(selectionHighX, bankEdges.Right);
                        if (rightX - leftX < 0.00001f)
                            continue;

                        var p0 = GetEditorSelectionPoint3D(leftX, selectionLowTick,
                            currentTick, side, bank);
                        var p1 = GetEditorSelectionPoint3D(rightX, selectionLowTick,
                            currentTick, side, bank);
                        var p2 = GetEditorSelectionPoint3D(rightX, selectionHighTick,
                            currentTick, side, bank);
                        var p3 = GetEditorSelectionPoint3D(leftX, selectionHighTick,
                            currentTick, side, bank);

                        if (IsFiniteEditorSelectionPoint(p0) && IsFiniteEditorSelectionPoint(p1) &&
                            IsFiniteEditorSelectionPoint(p2) && IsFiniteEditorSelectionPoint(p3))
                        {
                            selectionVertexCount = AppendEditorSelectionQuad3D(
                                selectionVertexCount, p0, p1, p2, p3,
                                new Color(255, 165, 0, 64), EditorSelectedColor);
                        }
                    }
                }
            }

            if (hasTestSegmentGeometry && _chart is not null && _tempo is not null)
                selectionVertexCount = AppendEditorLanTestSegmentOverlay(
                    selectionVertexCount, currentTick, currentState);

            DrawEditorSelectionGeometry3D(selectionVertexCount);
            _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);
        }

        if (_editorSuperFlickPlacementPending && _editorSuperFlickPendingTick >= 0 &&
            _editorSuperFlickPendingTrack is >= 0 and <= 15)
        {
            var pendingDepth = _projection.ScrollPixelsAtTicks(_editorSuperFlickPendingTick, currentTick);
            if (_projection.WithinRenderDistance((float)pendingDepth))
            {
                var pendingPoint = _projection.ProjectAtTicks(_editorSuperFlickPendingTrack, _editorSuperFlickPendingTick, currentTick, currentState);
                if (!OutsideNoteView(pendingPoint, pendingPoint))
                {
                    var pendingRadius = GetEditorMarkerRadius(_editorSuperFlickPendingTrack, _editorSuperFlickPendingTick, currentTick, currentState, out var pendingCenter);
                    DrawEditorMarker(pendingCenter, pendingRadius, OuterSuperFlick, 0.25f);
                }
            }
        }

        // Once a SuperFlick source is selected, hovering another bank on the
        // same side previews the actual strip texture and direction at the source.
        // Target hover works on the submenu buttons, bank indicators, or lane itself.
        if (_editorSuperFlickPlacementPending && _editorHoverHit is null &&
            _tempo is not null && _editorMouseHoverActive &&
            !_editorPanelsViewEnabled && _editorSuperFlickPendingTick >= 0 &&
            _editorSuperFlickPendingTrack is >= 0 and <= 15 &&
            !string.IsNullOrEmpty(_editorSuperFlickSourceBank) &&
            !string.IsNullOrEmpty(_editorSuperFlickPendingSide))
        {
            var targetBank = string.Empty;
            var targetSide = _editorSuperFlickPendingSide;
            var (virtualWidth, virtualHeight) = GetVirtualResolution(_graphics.Viewport.Width, _graphics.Viewport.Height);
            var pointer = _editorPointerPosition;
            var pointerPixel = new Microsoft.Xna.Framework.Point((int)MathF.Round(pointer.X), (int)MathF.Round(pointer.Y));

            if (_editorMenuSection == EditorMenuSection.SuperFlick)
            {
                for (var childIndex = 0; childIndex < EditorSuperFlickToolLabels.Length; childIndex++)
                {
                    if (!GetEditorMenuChildBounds(childIndex, virtualWidth, virtualHeight).Contains(pointerPixel))
                        continue;
                    targetBank = childIndex switch { 0 => "Forward", 1 => "Center", 2 => "Backward", _ => string.Empty };
                    break;
                }
            }

            if (string.IsNullOrEmpty(targetBank) &&
                TryGetClickedEditorBankIndicator(pointer, virtualWidth, virtualHeight, out var indicatorSide, out var indicatorBank) &&
                string.Equals(indicatorSide, _editorSuperFlickPendingSide, StringComparison.OrdinalIgnoreCase))
            {
                targetBank = indicatorBank;
            }

            if (string.IsNullOrEmpty(targetBank) && _hoverTrack is >= 0 and <= 15)
            {
                var hoverPosition = GearState.Position(_hoverTrack.Value);
                if (hoverPosition is not null &&
                    string.Equals(hoverPosition.Value.Side, _editorSuperFlickPendingSide, StringComparison.OrdinalIgnoreCase))
                {
                    targetBank = hoverPosition.Value.Bank;
                    targetSide = hoverPosition.Value.Side;
                }
            }

            if (!string.IsNullOrEmpty(targetBank) &&
                !string.Equals(targetBank, _editorSuperFlickSourceBank, StringComparison.OrdinalIgnoreCase) &&
                TryGetEditorSuperFlickAttr(_editorSuperFlickSourceBank, targetBank, targetSide, out var previewAttr))
            {
                var previewTrack = _editorSuperFlickPendingTrack;
                var previewTick = _editorSuperFlickPendingTick;
                var depth = (float)_projection.ScrollPixelsAtTicks(previewTick, currentTick);
                var sourcePoint = _projection.ProjectAtTicks(previewTrack, previewTick, currentTick, currentState);
                if (_projection.WithinRenderDistance(depth))
                {
                    var previewEvent = _editorSuperFlickPreviewEvent;
                    previewEvent.Tick = previewTick;
                    previewEvent.Track = previewTrack;
                    previewEvent.Attr = previewAttr;
                    _editorSuperFlickPreviewScratch.Clear();
                    var sourcePosition = GearState.Position(previewTrack);
                    if (sourcePosition is not null)
                    {
                        _editorSuperFlickPreviewScratch.Add((depth, previewEvent, sourcePoint,
                            sourcePosition.Value.Side, sourcePosition.Value.Bank, previewEvent.GetSuperFlickFamily()));
                        _spriteBatch.End();
                        DrawSuperFlickStrips(_editorSuperFlickPreviewScratch, currentTick, forceBankOpacity: true);
                        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);
                    }
                }
            }
        }

        if (!_editorPanelsViewEnabled && _selectedBeatTick.HasValue &&
            _selectedBeatTrack is >= 0 and <= 15)
        {
            var tick = _selectedBeatTick.Value;
            var track = _selectedBeatTrack.Value;
            var depth = _projection.ScrollPixelsAtTicks(tick, currentTick);
            if (_projection.WithinRenderDistance((float)depth))
            {
                var point = _projection.ProjectAtTicks(track, tick, currentTick, currentState);
                if (!OutsideNoteView(point, point))
                {
                    var radius = GetEditorMarkerRadius(track, tick, currentTick, currentState, out var markerCenter);
                    DrawEditorMarker(markerCenter, radius, EditorFamilyColor, 0.16f);
                }
            }
        }

        // The hover marker is also the interaction preview: yellow means the
        // click will select an existing note; an empty cell keeps the active tool
        // preview, or uses a neutral white marker when no note tool is armed.
        if (!_editorPanelsViewEnabled && _chartEditorMode && _editorMouseHoverActive &&
            _editorHoverCacheValid)
        {
            if (_editorHoverHit is { } hoverHit)
            {
                var hitPoint = _projection.ProjectAtTicks(
                    hoverHit.Track, hoverHit.Tick, currentTick, currentState);
                if (!OutsideNoteView(hitPoint, hitPoint))
                {
                    var hitTravel = (float)_projection.ScrollPixelsAtTicks(hoverHit.Tick, currentTick);
                    var hitRadius = GetEditorMarkerRadius(
                        hoverHit.Track, hoverHit.Tick, currentTick, currentState,
                        out var hitCenter, hoverHit.Event, hitPoint, hitTravel);
                    if (hoverHit.Kind == EditorHoverHitKind.HoldEndMarker ||
                        hoverHit.Event.NoteType == NoteType.LinkTail)
                        hitRadius *= 0.5f;
                    DrawEditorMarker(hitCenter, hitRadius, EditorHoverOverNoteColor, 0.24f);
                }
            }
            else if (_hoverBeatTick.HasValue && _hoverTrack is >= 0 and <= 15)
            {
                // EDIT GROUP is not itself a placement state. Lime is reserved
                // for an armed group-placement tool, not ordinary group editing.
                var groupPlacementHover = _editorNoteGroupPlacementArmed;
                var markerTrack = GetEditorHoverMarkerTrack(_hoverTrack.Value);
                var point = _projection.ProjectAtTicks(markerTrack, _hoverBeatTick.Value, currentTick, currentState);
                if (!OutsideNoteView(point, point))
                {
                    var radius = GetEditorMarkerRadius(markerTrack, _hoverBeatTick.Value,
                        currentTick, currentState, out var markerCenter);
                    if (_editorSuperFlickTool == EditorSuperFlickTool.Place &&
                        _editorPlacementEnabled && !groupPlacementHover)
                    {
                        var position = GearState.Position(markerTrack);
                        var validSource = position is not null;
                        DrawEditorMarker(markerCenter, radius,
                            validSource ? OuterSuperFlick : new Color(255, 78, 88), 0.20f);
                        var mode = position?.Bank switch
                        {
                            "Forward" => "1K", "Center" => "2K", "Backward" => "3K", _ => "?K"
                        };
                        var previewText = validSource
                            ? $"SUPERFLICK SOURCE {position!.Value.Side.ToUpperInvariant()} {mode} — CLICK, THEN CHOOSE TARGET"
                            : "SUPERFLICK SOURCE MUST BE ON A LANE";
                        var border = validSource ? OuterSuperFlick : new Color(255, 78, 88);
                        DrawEditorHoverIndicator(previewText, markerCenter, radius, border,
                            validSource ? Color.White : new Color(255, 137, 143));
                    }
                    else if (groupPlacementHover)
                    {
                        DrawEditorMarker(markerCenter, radius, EditorGroupSelectedColor, 0.20f);
                    }
                    else if (_editorPlacementEnabled && !_editorConnectorMode &&
                             !_editorNoteGroupEditMode)
                    {
                        DrawEditorMarker(markerCenter, radius, EditorSelectedColor, 0.16f);
                    }
                    else
                    {
                        // EDIT GROUP uses the neutral empty-cell marker. Keep the
                        // faint fill and set the outline's effective alpha to 30.
                        DrawEditorMarker(markerCenter, radius, EditorHoverEmptyColor,
                            0.16f, 30f / EditorHoverEmptyColor.A);
                    }
                }
            }
        }

        _spriteBatch.End();
    }

}
