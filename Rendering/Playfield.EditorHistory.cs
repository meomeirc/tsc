using Microsoft.Xna.Framework;
using TSC.Ares;

namespace TSC.Rendering;

public sealed partial class Playfield
{
    private sealed record EditorTrackSnapshot(ChartTrack Track, List<ChartEvent> Events);

    private sealed record EditorEventSnapshot(
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
        int? TwinPairId,
        bool LoopReconstructed,
        string? LoopPhase);

    private sealed class EditorChartSnapshot
    {
        public required List<EditorTrackSnapshot> Tracks { get; init; }
        public required List<EditorEventSnapshot> Events { get; init; }
        public required double InitialTempo { get; init; }
        public required int PlayTick { get; init; }
        public required double PlayTime { get; init; }
        public required int AudioOriginTick { get; init; }
        public required uint RawMagic { get; init; }
        public required int AresTrackCount { get; init; }
        public required byte[]? AresHeaderTemplate { get; init; }
        public required byte[]? LoadedAresHeaderTemplate { get; init; }
        public required List<string> InstrumentNames { get; init; }
        public required List<uint> InstrumentNameHuffmanFrequencies { get; init; }
        public required List<int> InstrumentNameReferences { get; init; }
        public required List<AresInstrumentDefinition> Instruments { get; init; }
        public bool SelectionOnly { get; init; }
        public ChartEvent? SelectedEvent { get; init; }
        public int? SelectedBeatTick { get; init; }
        public int? SelectedBeatTrack { get; init; }
        public ChartEvent? SelectedTempoEvent { get; init; }
        public ChartEvent? SelectedLinkRoot { get; init; }
        public bool SelectedHoldEnd { get; init; }
        public List<ChartEvent> MultiSelectedEvents { get; init; } = new();
        public List<(int Tick, int Track)> SelectedGridCells { get; init; } = new();
        public (int Tick, int Track)? PanelsSelectedEmptyCell { get; init; }
        public int? SelectReferenceLineTick { get; init; }
        public int? SelectReferenceTickOverride { get; init; }
    }

    private sealed record EditorClipboardEntry(
        int TickOffset,
        int TrackOffset,
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
        string? OriginalTrackBank);

    private readonly List<EditorChartSnapshot> _editorUndoStack = new();
    private readonly List<EditorChartSnapshot> _editorRedoStack = new();
    private const int EditorHistoryLimit = 100;
    private EditorChartSnapshot? _editorDragUndoSnapshot;
    private EditorChartSnapshot? _editorPendingRectangleUndoSnapshot;
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

    private EditorChartSnapshot? CaptureEditorChartSnapshot(bool selectionOnly = false)
    {
        if (_chart is null)
            return null;

        var tracks = _chart.Tracks
            .Select(track => new EditorTrackSnapshot(track, track.Events.ToList()))
            .ToList();
        var events = tracks.SelectMany(track => track.Events).Distinct().Select(ev => new EditorEventSnapshot(
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
            ev.TwinPairId,
            ev.LoopReconstructed,
            ev.LoopPhase)).ToList();
        return new EditorChartSnapshot
        {
            Tracks = tracks,
            Events = events,
            InitialTempo = _chart.InitialTempo,
            PlayTick = _chart.PlayTick,
            PlayTime = _chart.PlayTime,
            AudioOriginTick = _chart.AudioOriginTick,
            RawMagic = _chart.RawMagic,
            AresTrackCount = _chart.AresTrackCount,
            AresHeaderTemplate = _chart.AresHeaderTemplate?.ToArray(),
            LoadedAresHeaderTemplate = _loadedAresHeaderTemplate?.ToArray(),
            InstrumentNames = _chart.InstrumentNames.ToList(),
            InstrumentNameHuffmanFrequencies = _chart.InstrumentNameHuffmanFrequencies.ToList(),
            InstrumentNameReferences = _chart.InstrumentNameReferences.ToList(),
            Instruments = _chart.Instruments.ToList(),
            SelectionOnly = selectionOnly,
            SelectedEvent = _selectedEventKey.HasValue
                ? _chart.Events.FirstOrDefault(ev => KeyOf(ev) == _selectedEventKey.Value)
                : null,
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

    private bool EditorChartMatchesSnapshot(EditorChartSnapshot snapshot)
    {
        if (_chart is null || _chart.InitialTempo != snapshot.InitialTempo ||
            _chart.PlayTick != snapshot.PlayTick || _chart.PlayTime != snapshot.PlayTime || _chart.AudioOriginTick != snapshot.AudioOriginTick ||
            _chart.RawMagic != snapshot.RawMagic || _chart.AresTrackCount != snapshot.AresTrackCount ||
            _chart.Tracks.Count != snapshot.Tracks.Count)
            return false;

        for (var i = 0; i < snapshot.Tracks.Count; i++)
        {
            var savedTrack = snapshot.Tracks[i];
            var currentTrack = _chart.Tracks[i];
            if (!ReferenceEquals(savedTrack.Track, currentTrack) ||
                savedTrack.Events.Count != currentTrack.Events.Count)
                return false;
            for (var j = 0; j < savedTrack.Events.Count; j++)
                if (!ReferenceEquals(savedTrack.Events[j], currentTrack.Events[j]))
                    return false;
        }

        foreach (var state in snapshot.Events)
        {
            var ev = state.Event;
            if (ev.Tick != state.Tick || ev.Track != state.Track || ev.Attr != state.Attr ||
                ev.Duration != state.Duration || ev.RawType != state.RawType ||
                ev.TypeIndex != state.TypeIndex || ev.Kind != state.Kind || ev.NoteType != state.NoteType ||
                ev.EventIndex != state.EventIndex || ev.LinePair != state.LinePair ||
                ev.Value != state.Value || ev.TwinPairId != state.TwinPairId ||
                ev.LoopReconstructed != state.LoopReconstructed || ev.LoopPhase != state.LoopPhase)
                return false;
        }

        return _chart.InstrumentNames.SequenceEqual(snapshot.InstrumentNames) &&
               _chart.InstrumentNameHuffmanFrequencies.SequenceEqual(snapshot.InstrumentNameHuffmanFrequencies) &&
               _chart.InstrumentNameReferences.SequenceEqual(snapshot.InstrumentNameReferences) &&
               _chart.Instruments.SequenceEqual(snapshot.Instruments);
    }

    private bool EditorSelectionMatchesSnapshot(EditorChartSnapshot snapshot)
    {
        var selectedEvent = _selectedEventKey.HasValue && _chart is not null
            ? _chart.Events.FirstOrDefault(ev => KeyOf(ev) == _selectedEventKey.Value)
            : null;
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

    private void CommitEditorUndoSnapshot(EditorChartSnapshot? snapshot)
    {
        if (snapshot is null)
            return;

        // An empty-point click can turn into rectangle selection after the next
        // pointer movement. Defer its history entry until mouse release so a
        // drag can cancel the click cleanly.
        if (_editorRectSelectionArmed)
        {
            _editorPendingRectangleUndoSnapshot = snapshot;
            return;
        }

        PushEditorUndoSnapshot(snapshot);
    }

    private void PushEditorUndoSnapshot(EditorChartSnapshot snapshot)
    {
        if (!snapshot.SelectionOnly)
            MarkEditorDirty();
        _editorUndoStack.Add(snapshot);
        if (_editorUndoStack.Count > EditorHistoryLimit)
            _editorUndoStack.RemoveAt(0);
        _editorRedoStack.Clear();
    }

    private void RestoreEditorChartSnapshot(EditorChartSnapshot snapshot)
    {
        if (_chart is null)
            return;

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
            state.Event.TwinPairId = state.TwinPairId;
            state.Event.LoopReconstructed = state.LoopReconstructed;
            state.Event.LoopPhase = state.LoopPhase;
        }

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
        foreach (var currentTrack in _chart.Tracks)
            currentTrack.Events.Clear();
        _chart.Tracks.Clear();
        foreach (var trackState in snapshot.Tracks)
        {
            trackState.Track.Events.Clear();
            trackState.Track.Events.AddRange(trackState.Events);
            _chart.Tracks.Add(trackState.Track);
        }

        _selectedEventKey = snapshot.SelectedEvent is not null && _chart.Events.Any(ev => ReferenceEquals(ev, snapshot.SelectedEvent))
            ? KeyOf(snapshot.SelectedEvent)
            : null;
        _selectedBeatTick = snapshot.SelectedBeatTick;
        _selectedBeatTrack = snapshot.SelectedBeatTrack;
        _editorSelectedTempoEvent = snapshot.SelectedTempoEvent is not null && _chart.Events.Any(ev => ReferenceEquals(ev, snapshot.SelectedTempoEvent))
            ? snapshot.SelectedTempoEvent : null;
        _editorSelectedLinkRoot = snapshot.SelectedLinkRoot is not null && _chart.Events.Any(ev => ReferenceEquals(ev, snapshot.SelectedLinkRoot))
            ? snapshot.SelectedLinkRoot : null;
        _editorSelectedHoldEnd = snapshot.SelectedHoldEnd;
        _editorMultiSelectedEvents.Clear();
        foreach (var selected in snapshot.MultiSelectedEvents)
            if (_chart.Events.Any(ev => ReferenceEquals(ev, selected))) _editorMultiSelectedEvents.Add(selected);
        _editorSelectedGridCells.Clear();
        foreach (var cell in snapshot.SelectedGridCells) _editorSelectedGridCells.Add(cell);
        _editorPanelsSelectedEmptyCell = snapshot.PanelsSelectedEmptyCell;
        _editorSelectReferenceLineTick = snapshot.SelectReferenceLineTick;
        _editorSelectReferenceTickOverride = snapshot.SelectReferenceTickOverride;
        _editorPendingRectangleUndoSnapshot = null;
        _editorSelectionPendingUndoSnapshot = null;
        _editorRectSelectionArmed = false;
        _editorRectSelecting = false;
        _editorDraggedEvent = null;
        _editorDragUndoSnapshot = null;
        _editorDragIsHoldEnd = false;
        _editorDragChanged = false;
        RefreshAfterEditorMutation();
        StoreActiveEditorDifficulty();
    }

    private void UndoEditorChange()
    {
        if (_chart is null || _editorUndoStack.Count == 0)
        {
            _statusMessage = "Nothing to undo.";
            return;
        }

        var previous = _editorUndoStack[^1];
        var current = CaptureEditorChartSnapshot(selectionOnly: previous.SelectionOnly);
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
        var current = CaptureEditorChartSnapshot(selectionOnly: next.SelectionOnly);
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
            .Where(ev => _chart.Events.Any(candidate => ReferenceEquals(candidate, ev)) && ev.Kind == "Note" && ev.RawType == 1)
            .ToHashSet();
        if (selected.Count == 0 && _selectedEventKey.HasValue)
        {
            var single = _chart.Events.FirstOrDefault(ev => KeyOf(ev) == _selectedEventKey.Value && ev.Kind == "Note" && ev.RawType == 1);
            if (single is not null)
                selected.Add(single);
        }

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

    private void CopySelectedEditorNotes()
    {
        var notes = GetSelectedEditorNotes(expandLinkFamilies: true);
        if (notes.Count == 0)
        {
            _statusMessage = "Select one or more notes to copy.";
            return;
        }

        var minTick = notes.Min(ev => ev.Tick);
        var minTrack = notes.Min(ev => ev.Track);
        var selectedAnchor = _selectedEventKey.HasValue
            ? notes.FirstOrDefault(ev => KeyOf(ev) == _selectedEventKey.Value)
            : null;
        selectedAnchor ??= notes.OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track).First();
        _editorClipboardAnchorTickOffset = selectedAnchor.Tick - minTick;
        _editorClipboardAnchorTrackOffset = selectedAnchor.Track - minTrack;
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
        foreach (var note in notes)
        {
            var root = GetEditorLinkRoot(note);
            var group = root is not null && chainGroups.TryGetValue(root, out var groupId) ? groupId : -1;
            var originalPosition = GearState.Position(note.Track);
            _editorClipboard.Add(new EditorClipboardEntry(
                note.Tick - minTick,
                note.Track - minTrack,
                note.Track,
                note.Attr,
                Math.Max(1, note.Duration),
                note.Instrument,
                note.Velocity,
                note.Pan,
                note.NoteType,
                group,
                note.LinePair,
                note.IsSuperFlick,
                originalPosition?.Side,
                originalPosition?.Bank));
        }

        _statusMessage = $"Copied {notes.Count} note(s).";
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
        var snapshot = CaptureEditorChartSnapshot();
        var removed = notes.ToHashSet();
        var replacement = DeleteEditorEvents(removed);
        CommitEditorUndoSnapshot(snapshot);
        _editorMultiSelectedEvents.Clear();
        _selectedEventKey = replacement is not null ? KeyOf(replacement) : null;
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorSelectedHoldEnd = false;
        RefreshAfterEditorMutation();
        _editorSelectedLinkRoot = replacement is not null ? GetEditorLinkRoot(replacement) : null;
        _statusMessage = $"Cut {notes.Count} note(s).";
    }

    private void PasteEditorNotes(bool fitToBank = false)
    {
        if (_chart is null || _tempo is null || _editorClipboard.Count == 0)
        {
            _statusMessage = _editorClipboard.Count == 0 ? "Clipboard is empty." : "No chart is open.";
            return;
        }

        var clipboardEntries = _editorClipboard.ToList();
        var clipboardContainsPanelEvents = clipboardEntries.Any(entry => entry.Attr is >= 101 and <= 108);
        var clipboardIsPanelOnly = clipboardEntries.All(entry =>
            entry.Attr is >= 101 and <= 108 &&
            (entry.NoteType is NoteType.Single or NoteType.SingleLong));
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
        var targetAnchorTick = _selectedBeatTick ?? (selected is not null ? selected.Tick + snap : currentTick);
        var targetAnchorTrack = _selectedBeatTrack ?? selected?.Track ?? _editorTrack;
        var baseTick = Math.Max(0, targetAnchorTick - _editorClipboardAnchorTickOffset);
        bool AllowedTargetTrack(int track) => _editorPanelsViewEnabled
            ? track is >= 17 and <= 20
            : track is >= 0 and <= 15;

        var targetTracks = new int[clipboardEntries.Count];
        var fitDroppedSuperFlicks = 0;
        if (!fitGameplayToBank)
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
                    var mappedLocal = ScaleEditorBankLane(
                        sourcePosition.Value.LocalPos, sourceLaneCount, halfBankWidth);
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
                // When the clipboard comes from just one physical side, fit it
                // into that same side of the destination bank. Never expand a
                // one-sided source over both sides just because it is wider than
                // one target half. The selected track chooses the destination
                // bank; the source-side identity chooses left or right.
                var sourceSide = sourcePositions.FirstOrDefault(position => position is not null)?.Side;
                if (sourceSide is null)
                {
                    _statusMessage = "PASTE FIT needs valid source tracks in the 1K, 2K, or 3K bank.";
                    return;
                }

                var fitStart = bankStart + (string.Equals(sourceSide, "right", StringComparison.OrdinalIgnoreCase)
                    ? halfBankWidth : 0);
                var fitWidth = halfBankWidth;
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
                        mappedOffset = (int)Math.Round(offset * (fitWidth - 1.0) / (sourceWidth - 1.0), MidpointRounding.AwayFromZero);
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
                        return FindEditorPanelsNoteAtGridCell((int)targetTick, targetTrack) is not null;
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
                if (baseTick > int.MaxValue - snap)
                    break;
                baseTick += snap;
            }

            _statusMessage = "Paste cancelled: no collision-free space was found within the next 256 grid steps.";
            return false;
        }

        var groupPairs = new Dictionary<int, int>();
        var groupRanges = new Dictionary<int, (int StartTick, int EndTick)>();
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
                var pair = new[] { 1, 3, 5, 7 }.FirstOrDefault(candidate =>
                    !IsLinkPairOccupied(_chart, candidate, startTick, endTick) &&
                    !groupPairs.Any(existing => existing.Value == candidate &&
                        groupRanges.TryGetValue(existing.Key, out var range) &&
                        range.StartTick < endTick && range.EndTick > startTick));
                if (pair != 0)
                {
                    groupPairs[group.Key] = pair;
                    groupRanges[group.Key] = (startTick, endTick);
                }
            }
        }

        // Allocate chain pairs first so an orphaned copied LinkTail (which must
        // become an ordinary note) is also moved off a restricted 1K outer lane.
        for (var pass = 0; pass < 3; pass++)
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
                    _statusMessage = "Paste cancelled: in 1K, L1/L3/R1/R3 are reserved for link tails. Use PASTE FIT to fit notes into legal lanes.";
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
        for (var i = 0; i < clipboardEntries.Count; i++)
        {
            var entry = clipboardEntries[i];
            var isChain = entry.ChainGroup >= 0 && groupPairs.ContainsKey(entry.ChainGroup);
            var effectiveType = isChain
                ? entry.NoteType
                : entry.NoteType is NoteType.LinkHead or NoteType.LinkTail
                    ? entry.Duration > 6 ? NoteType.SingleLong : NoteType.Single
                    : entry.NoteType;
            if (!IsEditorTrackAllowedForNoteType(targetTracks[i], effectiveType))
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

        var snapshot = CaptureEditorChartSnapshot();
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

        CommitEditorUndoSnapshot(snapshot);
        RefreshAfterEditorMutation();
        _editorMultiSelectedEvents.Clear();
        foreach (var ev in pasted)
            _editorMultiSelectedEvents.Add(ev);
        _selectedEventKey = KeyOf(pasted[0]);
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorTrack = pasted[0].Track;
        _editorSelectedHoldEnd = false;
        _editorSelectedLinkRoot = GetEditorLinkRoot(pasted[0]);
        _statusMessage = fitGameplayToBank
            ? $"Pasted and fitted {pasted.Count} note(s) into the destination bank." +
              (fitDroppedConflicts > 0 ? $" Skipped {fitDroppedConflicts} lower-priority/conflicting note(s)." : string.Empty) +
              (fitCollisionRemovals.Count > 0 ? $" Replaced {fitCollisionRemovals.Count} lower-priority note(s)." : string.Empty) +
              (fitDroppedSuperFlicks > 0 ? $" Removed {fitDroppedSuperFlicks} SuperFlick(s) that needed another track." : string.Empty)
            : $"Pasted {pasted.Count} note(s).";
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

        foreach (var track in _chart.Tracks)
            track.Events.RemoveAll(removals.Contains);

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
