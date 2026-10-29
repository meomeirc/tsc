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

    // Mouse hover is intentionally disabled until a real mouse movement is seen.
    // Touch presses still edit the chart, but never drive the hover preview.
    private bool _editorMouseHoverActive;
    private Vector2 _editorPointerPosition;
    private bool _editorTimelineScrubbing;
    private bool _editorTimelineResumeAfterScrub;
    private static readonly Color EditorSelectedColor = new(255, 165, 0);
    private static readonly Color EditorFamilyColor = Color.White;
    private static readonly Color EditorEndingColor = Color.Cyan;
    // Two selection quads, each with a filled face and four 3D border strips.
    // Reused each frame to avoid creating short-lived geometry arrays.
    private readonly VertexPositionColor[] _editorSelection3DVertices = new VertexPositionColor[60];

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
        _editorMultiSelectedEvents.Clear();
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
        else if (mouseMoved)
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
        if (editorLuaUiActive)
        {
            UpdateChartEditorInterfacePointer(point, leftDown || touchActive);
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

        // Preview mode still exposes its two Lua toolbar controls. Let Lua get
        // first chance to process them above, then keep clicks outside the
        // toolbar from selecting or placing chart notes beneath the preview.
        if (_chartEditorMode && _editorPreviewMode)
        {
            if (!editorLuaUiActive && (leftPressed || touchPressed))
                TryHandleChartEditorToolbarClick(point, virtualWidth, virtualHeight);
            return;
        }

        // The Lua timeline slider already maps pointer Y to the complete slider
        // bounds and dispatches seek values while captured. Do not also remap
        // held frames through the legacy C# track insets: frames with no new Lua
        // slider value would otherwise jump to a slightly different position.
        if (editorLuaUiActive && _editorTimelineScrubbing)
        {
            if (leftDown || touchActive)
                return;

            _editorTimelineScrubbing = false;
            if (_editorTimelineResumeAfterScrub)
            {
                _playing = true;
                _audioResyncPending = true;
            }
            _editorTimelineResumeAfterScrub = false;
            return;
        }

        if ((leftPressed || touchPressed) && _editorMetaEditingField is not null)
            CommitEditorMetaFieldOnBlur();
        var pointerOverToolbar = editorLuaUiActive
            ? _chartEditorInterfaceRuntime.IsUiPointOverControl(point.X, point.Y)
            : IsEditorToolbarPanelContains(pointerPixel, virtualWidth, virtualHeight) ||
              IsEditorBankIndicatorArea(point, virtualWidth, virtualHeight);

        // SELECT is an exclusive range-filter mode. The cyan range-line gesture is
        // handled separately; ordinary note hit-testing and the orange marquee are
        // disabled for the entire time the submenu is open.
        if (_chartEditorMode && _editorMenuSection == EditorMenuSection.Select)
        {
            _editorRectSelecting = false;
            _editorRectSelectionArmed = false;
            _editorPendingRectangleUndoSnapshot = null;
            _editorHoverCacheValid = false;
            _hoverBeatTick = null;
            _hoverTrack = null;
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

        // Ctrl+click deletes the note under the pointer instead of selecting,
        // dragging, or placing. Holding the DELETE FAMILY modifier as well (Shift
        // by default) removes the whole link chain or complete long note.
        if (_chartEditorMode && leftPressed && !touchInputThisFrame && !_editorPanelsViewEnabled &&
            !pointerOverToolbar && _editorMenuSection != EditorMenuSection.Select &&
            _editorMenuSection != EditorMenuSection.Bpm &&
            !_editorPreviewMode && !_editorSuperFlickPlacementPending && !_editorBpmPlacementPending &&
            !_editorStutterStartPlacementPending && !_editorStutterEndPlacementPending &&
            !GetEditorTimelineScrubberBounds(virtualWidth, virtualHeight).Contains(pointerPixel) &&
            KeybindSettings.IsModifierBindingHeld("editor_delete_click_modifier"))
        {
            var clickedNote = FindClickedLinkHeadMarker(point, currentTick, currentState);
            clickedNote ??= FindClickedHoldEnd(point, currentTick, currentState);
            clickedNote ??= FindClickedNote(point, currentTick, currentState, out _);
            clickedNote ??= FindClickedLinkChainBody(point, currentTick, currentState);
            if (clickedNote is not null)
            {
                DeleteEditorNoteAtPointer(clickedNote,
                    deleteFamily: KeybindSettings.IsModifierPartHeld("editor_delete_family"));
                return;
            }

            // Don't accidentally place a new note when the delete-click modifier
            // is held but the pointer lands on an empty lane cell.
            if (FindClickedQuarterBeat(point, currentTick, currentState, out _) is not null)
                return;
        }

        // The left timeline is a continuous scrub surface. Its five timestamps
        // are landmarks only; mouse and touch can seek to any vertical position.
        if (_chartEditorMode && !_editorTimelineScrubbing &&
            ((leftPressed || touchPressed) || (_editorRectSelecting && leftDown && !touchInputThisFrame)) &&
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
            UpdateEditorRectangleEndpoint(point, currentTick, currentState, updatePointer: !pointerOverToolbar);
            if (!leftDown)
                FinishEditorRectangleSelection(currentTick, currentState);
            return;
        }
        if (_chartEditorMode && _editorRectSelectionArmed && !touchInputThisFrame)
        {
            if (leftDown && !pointerOverToolbar && Vector2.DistanceSquared(point, _editorRectStart) >= 36f)
            {
                if (_editorPendingRectangleUndoSnapshot is not null)
                    RestoreEditorChartSnapshot(_editorPendingRectangleUndoSnapshot);
                _editorPendingRectangleUndoSnapshot = null;
                _editorRectSelectionArmed = false;
                _editorRectSelecting = true;
                _editorRectCurrent = point;
                UpdateEditorRectangleEndpoint(point, currentTick, currentState, updatePointer: false);
                _selectedEventKey = null;
                _selectedBeatTick = null;
                _selectedBeatTrack = null;
                _editorSelectedLinkRoot = null;
                _editorSelectedHoldEnd = false;
                _editorMultiSelectedEvents.Clear();
                _editorSelectedGridCells.Clear();
                _editorPanelsSelectedEmptyCell = null;
                _editorSelectReferenceLineTick = null;
                _statusMessage = "Selecting notes and empty grid cells…";
                return;
            }
            if (!leftDown)
            {
                if (_editorPendingRectangleUndoSnapshot is not null)
                    PushEditorUndoSnapshot(_editorPendingRectangleUndoSnapshot);
                _editorPendingRectangleUndoSnapshot = null;
                _editorRectSelectionArmed = false;
                return;
            }
        }

        if (_editorDraggedEvent is not null)
        {
            var dragHeld = _editorDragStartedByTouch ? touchActive : leftDown;
            if (dragHeld)
            {
                if (!pointerOverToolbar && (touchMoved || mouseMoved || touchPressed))
                    UpdateEditorDrag(point, currentTick, currentState);
            }
            else
            {
                FinishEditorDrag();
            }
            // Do not allow a release after dragging to also select/place a tick.
            return;
        }

        var tempoBoundaryPicking = _chartEditorMode && _editorMenuSection == EditorMenuSection.Stutter &&
                                   (_editorStutterStartPlacementPending || _editorStutterEndPlacementPending);
        if (_chartEditorMode && (_editorPlacementEnabled || tempoBoundaryPicking ||
                                  _editorMenuSection == EditorMenuSection.Bpm || _editorBpmPlacementPending ||
                                  _editorSuperFlickPlacementPending) &&
            _editorMouseHoverActive && !touchInputThisFrame && !pointerOverToolbar)
        {
            var signatureHash = new HashCode();
            signatureHash.Add(ComputeLaneMeshCacheSignature(currentState));
            signatureHash.Add(_projection.ScrollSpeedScale);
            signatureHash.Add(_projection.BaseScrollSpeed);
            signatureHash.Add(_projection.GetRenderDistanceAhead());
            signatureHash.Add(_projection.GetRenderDistanceBehind());
            signatureHash.Add(_projection.NoteStandAngleDegrees);
            signatureHash.Add(_topDownViewEnabled);
            var hoverSignature = signatureHash.ToHashCode();
            var pointerMoved = Vector2.DistanceSquared(mousePoint, _lastEditorHoverPointer) > 1f;
            var clockMoved = double.IsNaN(_lastEditorHoverCurrentTick) || Math.Abs(currentTick - _lastEditorHoverCurrentTick) > 0.001;
            if (!_editorHoverCacheValid || pointerMoved || clockMoved || hoverSignature != _lastEditorHoverSignature)
            {
                var hoverTick = FindClickedQuarterBeat(mousePoint, currentTick, currentState, out var hoverTrack);
                _hoverBeatTick = hoverTick;
                _hoverTrack = hoverTick.HasValue ? hoverTrack : null;
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
            _lastEditorHoverCurrentTick = double.NaN;
            _editorHoverCacheValid = false;
        }

        if (rightPressed && !touchPressed &&
            !(_chartEditorMode && _editorMenuSection == EditorMenuSection.Select))
        {
            _selectedEventKey = null;
            _selectedBeatTick = null;
            _selectedBeatTrack = null;
            _editorSelectedLinkRoot = null;
            _editorSelectedHoldEnd = false;
            _editorMultiSelectedEvents.Clear();
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

        if (_chartEditorMode && !touchPressed && !pointerOverToolbar)
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
            _editorPendingRectangleUndoSnapshot = null;
        }

        if (_chartEditorMode && _editorPlacementEnabled)
        {
            // Existing notes take priority over placement, so a note can still be
            // dragged while a note tool is selected. Empty-lane taps place new notes.
            // A link segment's duration-end handle can sit directly underneath its
            // destination point. Test endpoints first so clicking that handle edits
            // the segment end instead of selecting/moving the destination note.
            // Link heads get first refusal when hit regions overlap. This is especially
            // important for horizontal links where the hold-end marker can sit directly
            // on top of the connected head.
            var existingLinkHead = FindClickedLinkHeadMarker(point, currentTick, currentState);
            var existingHoldEnd = existingLinkHead is null
                ? FindClickedHoldEnd(point, currentTick, currentState)
                : null;
            var existingNote = existingLinkHead ?? existingHoldEnd ?? FindClickedNote(point, currentTick, currentState, out _);
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
                    _editorMultiSelectedEvents.Clear();
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
                _statusMessage = existingNote.IsSuperFlick
                    ? $"SuperFlick direction → {GetEditorSuperFlickTargetLabel(existingNote)} (attr {existingNote.Attr}), tick {existingNote.Tick}, track {existingNote.Track}."
                    : existingHoldEnd is not null
                        ? $"Selected hold end: tick {GetEditorNextLinkPoint(existingNote)?.Tick ?? existingNote.EndTick}, track {existingNote.Track}; drag to resize."
                        : $"Selected note: tick {existingNote.Tick} track {existingNote.Track}; drag to move.";
                if (IsEditorDraggableNote(existingNote))
                    BeginEditorDrag(existingNote, existingHoldEnd is not null, touchPressed, point, currentTick, currentState);
                return;
            }

            var clickedChain = FindClickedLinkChainBody(point, currentTick, currentState);
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
                _editorMultiSelectedEvents.Clear();
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
                _statusMessage = _editorConnectorMode
                    ? $"Link source {selectionPoint.Tick}/{selectionPoint.Track}; hold Connect and click target."
                    : "Selected link chain. Tap a point to drag it.";
                return;
            }

            if (FindClickedQuarterBeat(point, currentTick, currentState, out var placementTrack) is int placementTick)
            {
                // Clicking an outer 1K lane with a regular note tool places it on
                // that side's legal middle lane. Connector mode is kept separate so
                // its target can use L1/L3/R1/R3 as a link tail.
                if (!_editorConnectorMode)
                    placementTrack = GetEditorAllowedTrackForNoteType(placementTrack,
                        _editorSuperFlickTool == EditorSuperFlickTool.Place ? NoteType.Single : _editorNoteType);

                // The projected quad hit test can miss a note when the pointer is
                // near its edge or inside a hold body. Check the snapped cell as a
                // final guard before either placing a note or adding a connector.
                var occupiedNote = FindEditorNoteAtGridCell(placementTick, placementTrack);
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
                    _editorMultiSelectedEvents.Clear();
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
                    _statusMessage = _editorConnectorMode && occupiedRoot is not null
                        ? $"Link source {selected.Tick}/{selected.Track}; hold Connect and click target."
                        : _editorConnectorMode
                            ? $"Connector source {selected.Tick}/{selected.Track}; hold Connect and click target."
                            : $"Selected existing note: tick {selected.Tick}, track {selected.Track}; no overlapping note was placed.";
                    return;
                }

                _editorMultiSelectedEvents.Clear();
                _editorSelectedHoldEnd = false;
                _editorTrack = placementTrack;
                _selectedBeatTick = placementTick;
                _selectedBeatTrack = placementTrack;
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
        var linkHeadNote = FindClickedLinkHeadMarker(point, currentTick, currentState);
        var holdEndNote = linkHeadNote is null
            ? FindClickedHoldEnd(point, currentTick, currentState)
            : null;
        var note = linkHeadNote ?? holdEndNote ?? FindClickedNote(point, currentTick, currentState, out _);
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
                _editorMultiSelectedEvents.Clear();
            _editorSelectedHoldEnd = holdEndNote is not null;
            _selectedEventKey = KeyOf(note);
            _selectedBeatTick = null;
            _selectedBeatTrack = note.Track;
            _editorTrack = note.Track;
            _editorSelectedLinkRoot = GetEditorLinkRoot(note);
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
        else if (FindClickedLinkChainBody(point, currentTick, currentState) is ChartEvent chainPoint)
        {
            if (_chartEditorMode && !_editorConnectorMode && KeybindSettings.IsDown("editor_connect_modifier"))
            {
                ToggleEditorMultiSelection(chainPoint);
                return;
            }
            _editorDurationInputValuePending = false;
            _editorRectSelectionArmed = false;
            _editorMultiSelectedEvents.Clear();
            _editorSelectedHoldEnd = false;
            _selectedEventKey = KeyOf(chainPoint);
            _selectedBeatTick = null;
            _selectedBeatTrack = null;
            _editorSelectedLinkRoot = GetEditorLinkRoot(chainPoint) ??
                                      (chainPoint.NoteType == NoteType.LinkHead ? chainPoint : null);
            _editorTrack = chainPoint.Track;
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
        else if (FindClickedQuarterBeat(point, currentTick, currentState, out var selectedTrack) is int beatTick)
        {
            _editorDurationInputValuePending = false;
            _editorMultiSelectedEvents.Clear();
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
        "EDITOR/PREVIEW", "AUTOPLAY"
    };
    private static readonly int[] EditorBottomLeftActionIds =
        { EditorActionPlay, EditorActionEditorPreview, EditorActionGameplayMode,
          EditorActionViewMode, EditorActionExpand, EditorActionBeatLines,
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

    private static bool EditorLinkChainsConflictInTime(EditorLinkChainInfo a, EditorLinkChainInfo b)
    {
        var rangesOverlap = a.StartTick < b.EndTick && b.StartTick < a.EndTick;
        return rangesOverlap ||
               GetEditorLinkBoundaryTailConflict(a, b) is not null ||
               GetEditorLinkBoundaryTailConflict(b, a) is not null;
    }

    private bool HasSelectedEditorNotes()
    {
        if (_chart is null)
            return false;
        if (_editorMultiSelectedEvents.Any(ev => ev.Kind == "Note" && ev.RawType == 1))
            return true;
        if (!_selectedEventKey.HasValue)
            return false;
        var selected = _chart.Events.FirstOrDefault(ev => KeyOf(ev) == _selectedEventKey.Value);
        return selected is not null && selected.Kind == "Note" && selected.RawType == 1;
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
            return _editorMultiSelectedEvents.Min(ev => ev.Tick);
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
            return _editorMultiSelectedEvents.Min(ev => ev.Tick);
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
                selectedTracks = _editorMultiSelectedEvents.Select(ev => ev.Track)
                    .Where(track => panelsView ? track is >= 17 and <= 20 : IsPlayableEditorTrack(track))
                    .ToHashSet();
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

        var selectedNotes = _chart.Events.Where(ev =>
            ev.Kind == "Note" && ev.RawType == 1 && selectedTracks.Contains(ev.Track) &&
            (panelsView
                ? ev.Track is >= 17 and <= 20 && ev.Attr is >= 101 and <= 108
                : IsPlayableEditorTrack(ev.Track)) &&
            (_editorSelectYMode switch
            {
                EditorSelectYMode.Above => ev.Tick > referenceTick,
                EditorSelectYMode.Below => ev.Tick < referenceTick,
                _ => true
            })).ToList();

        _editorMultiSelectedEvents.Clear();
        foreach (var ev in selectedNotes)
            _editorMultiSelectedEvents.Add(ev);
        _selectedEventKey = null;
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorSelectedLinkRoot = null;
        _editorSelectedHoldEnd = false;
        _editorPlacementEnabled = false;
        _editorConnectorMode = false;
        _editorPanelsConnectorAwaitTarget = false;
        _statusMessage = $"SELECT: {_editorSelectYMode.ToString().ToUpperInvariant()}, X={(_editorSelectXSelected ? "SELECTED" : "ALL")}; selected {selectedNotes.Count} {(panelsView ? "animation" : "") } note(s) relative to tick {referenceTick}.";
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
            // attributes, DURATION, and SELECT. Dock it directly above the
            // full-width bottom toolbar rather than against the window edge.
            var contentHeight = (int)MathF.Round(38f * unit) + attributeGap +
                                7 * ((int)MathF.Round(32f * unit) + attributeGap);
            var panelHeight = contentHeight + padding * 2;
            return new Rectangle(virtualWidth - margin - width - padding * 2,
                Math.Max(margin, bottomStrip.Y - stripGap - panelHeight), width + padding * 2, panelHeight);
        }

        // Notes / Tools / BPM / Select form a fixed vertical strip immediately
        // above the bottom toolbar. Their expanded menus open to the left.
        var paddingFixed = Math.Max(1, (int)MathF.Round(8f * unit));
        var desiredContentWidth = (int)MathF.Round(152f * unit);
        var availableContentWidth = Math.Max(76, virtualWidth - margin * 2 - paddingFixed * 2);
        var widthFixed = Math.Min(desiredContentWidth, availableContentWidth);
        var tabHeight = (int)MathF.Round(34f * unit);
        var tabGap = Math.Max(1, (int)MathF.Round(4f * unit));
        var panelHeightFixed = tabHeight * 4 + tabGap * 3 + paddingFixed * 2;
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
            EditorMenuSection.Select => 3,
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
        // SELECT occupies the bottom row of the Panels toolbar, like the SELECT
        // category at the bottom-right of the gameplay editor. Its handler opens
        // the shared Y RANGE / X RANGE / APPLY SELECT menu, it is not a mode toggle.
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
        var row = 1 + index / 2; // CONNECTOR, four attribute rows, then SELECT at the bottom.
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
        if (parentIndex is < 0 or > 3)
            return Rectangle.Empty;
        var unit = _projection.ScalePixels(1f);
        var panel = GetEditorTopStripPanelBounds(virtualWidth, virtualHeight);
        var padding = Math.Max(1, (int)MathF.Round(8f * unit));
        var gap = Math.Max(1, (int)MathF.Round(4f * unit));
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
            // A missing chart is still a valid destination: switching to it
            // opens a blank chart state so the user can create that difficulty.
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
            return false;
        if (KeybindSettings.IsPressed("editor_delete_family"))
        {
            DeleteSelectedEditorNoteFamily();
            return true;
        }
        if (KeybindSettings.IsPressed("editor_delete"))
        {
            if (_editorSelectedTempoEvent is not null) DeleteSelectedEditorTempoChanger();
            else DeleteSelectedEditorNote();
            return true;
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
                _editorPendingRectangleUndoSnapshot = null;
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
            _ => EditorMenuSection.Select
        };
        var current = _editorMenuSection;
        var currentRoot = GetEditorMenuRootSection();

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
            _editorPendingRectangleUndoSnapshot = null;
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
                    // Use the exact same root-category flow as gameplay's bottom-right
                    // SELECT tab. This captures the selected grid range's first tick
                    // before the user applies the Y and X filters.
                    HandleEditorMenuCategoryClick(3);
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

            for (var tabIndex = 0; tabIndex < 4; tabIndex++)
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
                TwinPairId = null,
                Kind = "Note",
                Instrument = 0,
                Velocity = 127,
                Pan = 64
            };
            track.Events.Add(replacement);
            replacements[destination.Original] = replacement;
        }

        CommitEditorUndoSnapshot(undoSnapshot);
        RefreshAfterEditorMutation();
        _editorMultiSelectedEvents.Clear();
        foreach (var replacement in replacements.Values)
            _editorMultiSelectedEvents.Add(replacement);
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

        // The current single selection becomes the anchor on the first modifier
        // click. Later clicks toggle notes in or out without starting a drag.
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

        if (!selection.Add(clickedEvent))
            selection.Remove(clickedEvent);

        _editorMultiSelectedEvents.Clear();
        foreach (var ev in selection)
            _editorMultiSelectedEvents.Add(ev);

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
        _editorMultiSelectedEvents.Clear();
        _editorSelectedGridCells.Clear();
        _editorPanelsSelectedEmptyCell = null;
        _editorSelectReferenceLineTick = null;
        _editorSelectReferenceTickOverride = null;
        _editorRectSelectionArmed = false;
        _editorRectSelecting = false;
        _editorPendingRectangleUndoSnapshot = null;
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

        foreach (var ev in _editorMultiSelectedEvents)
            if (IsEditorDurationNoteEvent(ev))
                selected.Add(ev);

        if (_selectedEventKey.HasValue)
        {
            var current = _chart.Events.FirstOrDefault(ev => KeyOf(ev) == _selectedEventKey.Value);
            if (current is not null && IsEditorDurationNoteEvent(current))
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
        var membershipCounts = GetEditorLinkChainInfos()
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

            var nodeSet = chain.Nodes.ToHashSet();
            var repeatedHorizontalGroups = _chart.Links
                .Where(link => nodeSet.Contains(link.Head) && nodeSet.Contains(link.Tail) &&
                               link.Head.Track != link.Tail.Track)
                .GroupBy(link => link.Tail.Tick)
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

                // LinkBuilder orders same-tick points by track and EventIndex. Keep
                // the last point in the chain, so the chain turns only once at this tick.
                var keep = tailsAtTick[^1];
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

                // Normal pair ownership is half-open: a chain ending at T can
                // hand its ID to a new LinkHead at T. But if that new chain also
                // has a LinkTail at T, the old and new tails become ambiguous.
                if (a.StartTick < b.EndTick && b.StartTick < a.EndTick)
                {
                    Add(b.Root, $"global pair {a.Pair}/{a.Pair + 1} overlaps between ticks {a.StartTick} and {b.StartTick}");
                    continue;
                }

                var boundaryTail = GetEditorLinkBoundaryTailConflict(a, b) ??
                                   GetEditorLinkBoundaryTailConflict(b, a);
                if (boundaryTail is not null)
                {
                    Add(boundaryTail,
                        $"global pair {a.Pair}/{a.Pair + 1} cannot be reused at tick {boundaryTail.Tick}: a LinkTail at the hand-off tick is ambiguous; use a different ID");
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
        RebuildEditorLinkGraph();
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

        // Step 4: with straight chains and orphan points cleaned up, reassign
        // the global pair IDs. If the remaining paths are structurally unsafe,
        // keep the cleanup as one undoable change and explain why ID repair stopped.
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
        if (idFixFailure is null)
        {
            foreach (var chain in chains)
            {
                var pair = new[] { 1, 3, 5, 7 }.FirstOrDefault(candidate => !assignments.Any(existing =>
                    existing.Pair == candidate && EditorLinkChainsConflictInTime(existing.Chain, chain)));
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
                changed = true;
            }
            if (idsReassigned > 0)
                RebuildEditorLinkGraph();
        }

        if (changed)
        {
            CommitEditorUndoSnapshot(undoSnapshot);
            RefreshAfterEditorMutation();
        }

        var remainingIssues = GetEditorLinkPathIssues(GetEditorLinkChainInfos());
        var cleanupSummary = new List<string>();
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
            _statusMessage = remainingIssues.Count == 0
                ? $"QUICK FIX: all {chains.Count} chain(s) are already clean and have valid IDs."
                : $"QUICK FIX made no changes; {remainingIssues.Count} path issue(s) remain. {remainingIssues[0].Message}";
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

    private bool IsEditorLinkPairAvailable(int pair, int startTick, int endTick,
        HashSet<ChartEvent> movingEvents, bool hasLinkTailAtStart = false)
    {
        return _chart is not null &&
               !IsLinkPairOccupied(_chart, pair, startTick, endTick, movingEvents, hasLinkTailAtStart);
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

    private void LinkSelectedEditorNotes()
    {
        if (!_chartEditorMode || _chart is null)
        {
            _statusMessage = "Open the chart editor before linking notes.";
            return;
        }

        var selected = GetSelectedEditorNotes(expandLinkFamilies: true, orderByTrack: false)
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
                if (point.Duration > 6 &&
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
                IsEditorLinkPairAvailable(candidate, startTick, endTickForPath, selectedSet, hasLinkTailAtStart) &&
                !plans.Any(plan => !plan.VerticalOnly && plan.Pair == candidate &&
                    (plan.StartTick < endTickForPath && plan.EndTick > startTick ||
                     (hasLinkTailAtStart && plan.EndTick == startTick) ||
                     (endTickForPath == plan.StartTick && plan.Points.Count > 1 &&
                      plan.Points[1].Tick == plan.StartTick))));
            if (pair == 0)
            {
                _statusMessage = "No free link pair is available across all banks for this time range.";
                return;
            }
            plans.Add((grouping.Key, points, absorbed, pair, startTick, endTickForPath, false));
        }

        if (plans.Count == 0)
        {
            _statusMessage = "Select notes from one track family to build a link path.";
            return;
        }

        var undoSnapshot = CaptureEditorChartSnapshot();
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

        _editorMultiSelectedEvents.Clear();
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
    private void LinkSelectedEditorNotesHorizontalFirst()
    {
        if (!_chartEditorMode || _chart is null)
        {
            _statusMessage = "Open the chart editor before linking notes.";
            return;
        }

        var selected = GetSelectedEditorNotes(expandLinkFamilies: true, orderByTrack: false)
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
                    IsEditorLinkPairAvailable(candidate, sameTickTap.Tick, sameTickHold.EndTick, selectedSet,
                        hasLinkTailAtStart: true) &&
                    !plans.Any(plan => !plan.VerticalOnly && plan.Pair == candidate &&
                        (plan.StartTick < sameTickHold.EndTick && plan.EndTick > sameTickTap.Tick ||
                         plan.EndTick == sameTickTap.Tick ||
                         (sameTickHold.EndTick == plan.StartTick &&
                          (plan.Generated.Any(ev => ev.Tick == plan.StartTick) ||
                           (plan.Points.Count > 1 && plan.Points[1].Tick == plan.StartTick))))));
                if (pair == 0)
                {
                    _statusMessage = "No free link pair is available across all banks for this time range.";
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
                if (point.Duration > 6 &&
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
                IsEditorLinkPairAvailable(candidate, startTick, terminalEnd, selectedSet, hasLinkTailAtStart) &&
                !plans.Any(plan => !plan.VerticalOnly && plan.Pair == candidate &&
                    (plan.StartTick < terminalEnd && plan.EndTick > startTick ||
                     (hasLinkTailAtStart && plan.EndTick == startTick) ||
                     (terminalEnd == plan.StartTick &&
                      (plan.Generated.Any(ev => ev.Tick == plan.StartTick) ||
                       (plan.Points.Count > 1 && plan.Points[1].Tick == plan.StartTick))))));
            if (pairId == 0)
            {
                _statusMessage = "No free link pair is available across all banks for this time range.";
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

        _editorMultiSelectedEvents.Clear();
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

        _editorMultiSelectedEvents.Clear();
        foreach (var note in standaloneSelection)
            _editorMultiSelectedEvents.Add(note);
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

        CommitEditorUndoSnapshot(undoSnapshot);
        RefreshAfterEditorMutation();
        _editorMultiSelectedEvents.Clear();
        foreach (var ev in selected)
            _editorMultiSelectedEvents.Add(ev);
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
        _editorMultiSelectedEvents.Clear();
        foreach (var ev in selected)
            _editorMultiSelectedEvents.Add(ev);
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
        return FindLinkRootHead(ev) ?? (ev.NoteType == NoteType.LinkHead ? ev : null);
    }

    private List<ChartEvent> GetEditorLinkChainNodes(ChartEvent root)
    {
        var nodes = new List<ChartEvent> { root };
        if (_chart is null || root.NoteType != NoteType.LinkHead)
            return nodes;

        var visited = new HashSet<ChartEvent> { root };
        var current = root;
        for (var guard = 0; guard < 256; guard++)
        {
            var outgoing = _chart.Links
                .Where(link => ReferenceEquals(link.Head, current))
                .ToList();
            if (outgoing.Count == 0)
                break;

            // A valid path has one next point. If multiple points branch at the
            // same earliest tick, the graph is ambiguous; do not pick a lane by
            // track number to manufacture a route.
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
        return nodes;
    }

    private ChartEvent? GetEditorPreviousLinkPoint(ChartEvent point)
    {
        if (_chart is null)
            return null;
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

        var outgoing = _chart.Links
            .Where(link => ReferenceEquals(link.Head, point))
            .ToList();
        if (outgoing.Count == 0)
            return null;

        var nextTick = outgoing.Min(link => link.Tail.Tick);
        var candidates = outgoing.Where(link => link.Tail.Tick == nextTick).ToList();
        return candidates.Count == 1 ? candidates[0].Tail : null;
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

    // Returns a note already occupying the snapped editor cell. Long notes and
    // link segments occupy the source lane through their endpoint, not just their
    // note-head tick, so placement cannot accidentally stack a new note on them.
    private ChartEvent? FindEditorNoteAtGridCell(int tick, int track)
    {
        if (_chart is null)
            return null;

        return _chart.Events
            .Where(ev => ev.Kind == "Note" && ev.Track == track &&
                         (ev.Tick == tick ||
                          ((ev.IsHoldNote || (ev.NoteType is NoteType.LinkHead or NoteType.LinkTail)) &&
                           ev.Tick <= tick && tick <= (GetEditorNextLinkPoint(ev)?.Tick ?? ev.EndTick))))
            .OrderBy(ev => ev.Tick == tick ? 0 : 1)
            .ThenBy(ev => ev.RawType == 1 ? 0 : 1)
            .ThenBy(ev => ev.Tick)
            .ThenBy(ev => ev.EventIndex)
            .FirstOrDefault();
    }

    private ChartEvent? FindClickedLinkChainBody(Vector2 point, double currentTick, GearState currentState)
    {
        if (_chart is null || _chart.Links.Count == 0)
            return null;

        ChartEvent? bestNode = null;
        var bestDistance = float.MaxValue;
        var hitDistance = Math.Max(6f, _projection.ScalePixels(10f));
        foreach (var link in _chart.Links)
        {
            if (!ShouldRenderInCurrentBank(link.Head, currentState) ||
                !ShouldRenderInCurrentBank(link.Tail, currentState))
                continue;
            if (link.Head.NoteType != NoteType.LinkHead && link.Head.NoteType != NoteType.LinkTail)
                continue;
            if (link.Tail.NoteType != NoteType.LinkTail)
                continue;

            var head = _projection.ProjectAtTicks(link.Head.Track, link.Head.Tick, currentTick, currentState);
            var tail = _projection.ProjectAtTicks(link.Tail.Track, link.Tail.Tick, currentTick, currentState);
            if (OutsideNoteView(head, head) || OutsideNoteView(tail, tail))
                continue;

            var distance = PointToSegmentDistance(point, head.Position, tail.Position);
            if (distance > hitDistance || distance >= bestDistance)
                continue;

            // Selecting a connector body selects its nearest endpoint for deletion
            // and later dragging, while the whole chain is highlighted.
            bestNode = Vector2.DistanceSquared(point, head.Position) <= Vector2.DistanceSquared(point, tail.Position)
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
        var dragChainRoot = _editorSelectedLinkRoot ?? GetEditorLinkRoot(ev);
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

    private ChartEvent? FindClickedLinkHeadMarker(Vector2 point, double currentTick, GearState currentState)
    {
        if (!_chartEditorMode || _chart is null || _tempo is null)
            return null;

        ChartEvent? best = null;
        var bestDistance = float.MaxValue;
        foreach (var ev in _orderedRenderableEvents)
        {
            if (ev.NoteType != NoteType.LinkHead || !IsEditorDraggableNote(ev) ||
                !ShouldRenderInCurrentBank(ev, currentState))
                continue;

            var projected = _projection.ProjectAtTicks(ev.Track, ev.Tick, currentTick, currentState);
            if (OutsideNoteView(projected, projected))
                continue;
            var radius = GetEditorMarkerRadius(ev.Track, ev.Tick, currentTick, currentState, out var center, ev);
            var distance = Vector2.Distance(point, center);
            if (distance <= Math.Max(7f, radius * 1.45f) && distance < bestDistance)
            {
                best = ev;
                bestDistance = distance;
            }
        }
        return best;
    }

    private ChartEvent? FindClickedHoldEnd(Vector2 point, double currentTick, GearState currentState)
    {
        if (!_chartEditorMode || _chart is null || _tempo is null)
            return null;

        ChartEvent? best = null;
        var bestDistance = float.MaxValue;
        foreach (var ev in _orderedRenderableEvents)
        {
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

        var selectedRoot = _editorSelectedLinkRoot ?? GetEditorLinkRoot(ev);
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

            CommitEditorUndoSnapshot(undoSnapshot);
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

            CommitEditorUndoSnapshot(undoSnapshot);
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
                // The merged endpoint's own duration is authoritative. Do not
                // invent a long-note duration when the tail had none.
                previousPoint.Duration = Math.Max(1, ev.Duration);

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
                // No connection remains after removing the horizontal tail. Keep
                // the tail's authored duration instead of fabricating a minimum
                // long-note duration; conversion chooses tap vs hold from that value.
                ev.Duration = Math.Max(1, nextPoint.Duration);
                ConvertEditorLinkPointToStandaloneNote(ev);
            }

            finalSelection = ev;
            mergedSameLane = true;
        }

        if (!mergedSameLane)
            SyncEditorLinkChainDurations(_editorSelectedLinkRoot ?? GetEditorLinkRoot(ev));

        CommitEditorUndoSnapshot(undoSnapshot);
        RefreshAfterEditorMutation();

        // LinkBuilder has now rebuilt the actual connections. If lane merges left
        // an all-same-track chain, there is no cross-track connection left to keep
        // as a LinkHead family: flatten it into one normal hold and remove the now
        // redundant tail points.
        if (mergedSameLane)
        {
            var normalizedSelection = NormalizeEditorSameTrackLinkChains(finalSelection, out var normalized);
            if (normalized)
            {
                finalSelection = normalizedSelection ?? finalSelection;
                RefreshAfterEditorMutation();
            }
        }

        _selectedEventKey = KeyOf(finalSelection);
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorTrack = finalSelection.Track;
        _editorSelectedHoldEnd = wasHoldEnd && !mergedSameLane;
        _editorMultiSelectedEvents.Clear();
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
        // describes only the surviving chain nodes.
        var nodes = GetEditorLinkChainNodes(root);
        if (nodes.Count == 0 || nodes.Any(node => node.Track != root.Track))
            return preferredSelection;

        var terminal = nodes[^1];
        var endTick = Math.Max(terminal.Tick, terminal.EndTick);
        // Flatten the same-lane chain using its actual remaining extent. In
        // particular, do not force a minimum long-note duration for a point that
        // did not carry one before the merge.
        root.Duration = Math.Max(1, endTick - root.Tick);
        ConvertEditorLinkPointToStandaloneNote(root);

        foreach (var tail in nodes.Skip(1))
        {
            foreach (var track in _chart.Tracks)
                track.Events.RemoveAll(candidate => ReferenceEquals(candidate, tail));
        }

        changed = true;
        return root;
    }

    private ChartEvent? FindClickedNote(Vector2 point, double currentTick, GearState currentState, out float bestDistance)
    {
        bestDistance = float.MaxValue;
        if (_chart is null || _tempo is null)
            return null;

        ChartEvent? best = null;
        foreach (var ev in _orderedRenderableEvents)
        {
            if (!IsRenderableNoteEvent(ev))
                continue;
            if (_compact && _gameplayMode && !IsActiveEvent(ev, currentState))
                continue;
            if (!ShouldIncludeRenderableEvent(ev, currentState))
                continue;

            var startTravel = _projection.ScrollPixelsAtTicks(ev.Tick, currentTick);
            var endTick = ev.EndTick;
            var endTravel = _projection.ScrollPixelsAtTicks(endTick, currentTick);
            if (!_projection.RenderIntervalIntersects((float)startTravel, (float)endTravel))
                continue;

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
        ChartEvent? ev = null)
    {
        var projected = _projection.ProjectAtTicks(track, tick, currentTick, currentState);
        markerCenter = projected.Position;
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
        var travel = (float)_projection.ScrollPixelsAtTicks(tick, currentTick);

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
            _quad3DVertexPool.Push(vertices);
            return Math.Max(1.25f, 0.25f * (Vector2.Distance(a, b) + Vector2.Distance(d, c)));
        }

        return Math.Max(1.25f, width * projected.Scale * projected.LaneRotationDepthScale * 0.5f);
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
        _editorMultiSelectedEvents.Clear();
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
                _editorMultiSelectedEvents.Add(ev);
        }

        // A rectangle selects notes, not every empty grid cell crossed by the range.
        // Keep occupied note coordinates for the SELECT X=SELECTED filter.
        foreach (var ev in _editorMultiSelectedEvents)
            _editorSelectedGridCells.Add((ev.Tick, ev.Track));

        _editorRectSelecting = false;
        _editorRectSelectionArmed = false;
        _editorPendingRectangleUndoSnapshot = null;
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

    private void DrawEditorMarker(Vector2 position, float radius, Color color, float fillOpacity = 0.12f)
    {
        DrawCircle(_spriteBatch, position, radius, color * fillOpacity);
        DrawCircleOutline(_spriteBatch, position, radius, Math.Clamp(radius * 0.16f, 1f, 2.25f), color);
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
        => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z) && point.Z < -0.0001f;

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
        var vertices = _editorSelection3DVertices;
        var center = (p0 + p1 + p2 + p3) * 0.25f;
        AddEditorSelectionTriangle(vertices, ref offset, p0, p1, p2, fill);
        AddEditorSelectionTriangle(vertices, ref offset, p0, p2, p3, fill);

        // A border is a narrow strip that lies in the same 3D face, not a
        // camera-facing SpriteBatch line. Its width is kept close to 1.5 screen px
        // at every depth by converting pixels to local camera-space units.
        for (var edgeIndex = 0; edgeIndex < 4; edgeIndex++)
        {
            var a = edgeIndex switch { 0 => p0, 1 => p1, 2 => p2, _ => p3 };
            var b = edgeIndex switch { 0 => p1, 1 => p2, 2 => p3, _ => p0 };
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
        var maxCameraDistance = 1024f;
        for (var i = 0; i < vertexCount; i++)
            maxCameraDistance = Math.Max(maxCameraDistance, -_editorSelection3DVertices[i].Position.Z * 1.25f);

        _primitiveEffect.Projection = Matrix.CreatePerspectiveOffCenter(
            -halfWidth / focalLength,
            halfWidth / focalLength,
            -halfHeight / focalLength,
            halfHeight / focalLength,
            1f,
            Math.Max(maxCameraDistance, _projection.StandingNoteRenderFarClip()));

        foreach (var pass in _primitiveEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            _graphics.DrawUserPrimitives(
                PrimitiveType.TriangleList, _editorSelection3DVertices, 0, vertexCount / 3);
        }
    }

    private void DrawSelectionOverlay(double currentTick, GearState currentState)
    {
        if (_chart is null || _tempo is null)
            return;

        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);

        if (_selectedEventKey.HasValue)
        {
            var selectedEvent = _chart.Events.FirstOrDefault(e => KeyOf(e) == _selectedEventKey.Value)
                                ?? _editorDraggedEvent;
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
                    var nodePoint = _projection.ProjectAtTicks(node.Track, node.Tick, currentTick, currentState);
                    if (!OutsideNoteView(nodePoint, nodePoint))
                    {
                        var nodeRadius = GetEditorMarkerRadius(
                            node.Track, node.Tick, currentTick, currentState, out var nodeCenter, node);
                        var selectedHoldEndNode = _editorSelectedHoldEnd && selectedEvent is not null &&
                            ReferenceEquals(node, GetEditorNextLinkPoint(selectedEvent));
                        var draggedHoldEndNode = _editorDragIsHoldEnd && _editorDraggedEvent is not null &&
                            ReferenceEquals(node, GetEditorNextLinkPoint(_editorDraggedEvent));
                        var directNodeSelected =
                            (ReferenceEquals(node, selectedEvent) && !_editorSelectedHoldEnd) ||
                            (ReferenceEquals(node, _editorDraggedEvent) && !_editorDragIsHoldEnd) ||
                            selectedHoldEndNode || draggedHoldEndNode;
                        var selectedColor = directNodeSelected
                            ? EditorSelectedColor
                            : isChainEnd ? EditorEndingColor : EditorFamilyColor;
                        // Link tails are endpoint markers, so keep their highlight
                        // visually secondary to the note heads while preserving the
                        // normal size for every other note marker.
                        DrawEditorMarker(nodeCenter,
                            node.NoteType == NoteType.LinkTail ? nodeRadius * 0.5f : nodeRadius,
                            selectedColor, 0.14f);
                    }

                    var nextNode = i + 1 < chainNodes.Count ? chainNodes[i + 1] : null;
                    var endpointTick = nextNode?.Tick ?? node.EndTick;
                    if (nextNode is not null || node.Duration > 6)
                    {
                        var endDepth = _projection.ScrollPixelsAtTicks(endpointTick, currentTick);
                        var endPoint = _projection.ProjectAtTicks(node.Track, endpointTick, currentTick, currentState);
                        if (_projection.WithinRenderDistance((float)endDepth) && !OutsideNoteView(endPoint, endPoint))
                        {
                            var endRadius = GetEditorMarkerRadius(
                                node.Track, endpointTick, currentTick, currentState, out var endCenter, node);
                            var directEndSelected =
                                (ReferenceEquals(node, selectedEvent) && _editorSelectedHoldEnd) ||
                                (ReferenceEquals(node, _editorDraggedEvent) && _editorDragIsHoldEnd);
                            var nextNodeDirectlySelected = nextNode is not null &&
                                ReferenceEquals(nextNode, selectedEvent) && !_editorSelectedHoldEnd;
                            var endColor = directEndSelected || nextNodeDirectlySelected
                                ? EditorSelectedColor
                                : nextNode is null ? EditorEndingColor : EditorFamilyColor;
                            DrawEditorMarker(endCenter, endRadius * 0.5f, endColor, 0.15f);
                        }
                    }
                }
            }
            else if (selectedEvent is not null)
            {
                var point = _projection.ProjectAtTicks(selectedEvent.Track, selectedEvent.Tick, currentTick, currentState);
                if (!OutsideNoteView(point, point))
                {
                    var radius = GetEditorMarkerRadius(selectedEvent.Track, selectedEvent.Tick, currentTick, currentState, out var markerCenter, selectedEvent);
                    DrawEditorMarker(markerCenter,
                        selectedEvent.NoteType == NoteType.LinkTail ? radius * 0.5f : radius,
                        (_editorSelectedHoldEnd || _editorDragIsHoldEnd) ? EditorFamilyColor : EditorSelectedColor, 0.10f);
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
                    var endDepth = _projection.ScrollPixelsAtTicks(endpointTick, currentTick);
                    var endPoint = _projection.ProjectAtTicks(endpointTrack, endpointTick, currentTick, currentState);
                    if (_projection.WithinRenderDistance((float)endDepth) && !OutsideNoteView(endPoint, endPoint))
                    {
                        var endRadius = GetEditorMarkerRadius(
                            endpointTrack, endpointTick, currentTick, currentState, out var endCenter, selectedEvent);
                        var endColor = _editorSelectedHoldEnd || _editorDragIsHoldEnd
                            ? EditorSelectedColor
                            : EditorEndingColor;
                        DrawEditorMarker(endCenter, endRadius * 0.5f, endColor, 0.13f);
                    }
                }
            }
        }

        if (_editorMultiSelectedEvents.Count > 0)
        {
            var visitedNodes = new HashSet<ChartEvent>();
            foreach (var selected in _editorMultiSelectedEvents)
            {
                if (!_chart.Events.Any(ev => ReferenceEquals(ev, selected)) || selected.Kind != "Note" ||
                    (selected.Track is < 0 or > 15) ||
                    !ShouldRenderInCurrentBank(selected, currentState))
                    continue;
                var root = GetEditorLinkRoot(selected);
                if (root is not null && root.NoteType == NoteType.LinkHead)
                {
                    foreach (var node in GetEditorLinkChainNodes(root))
                    {
                        if (!visitedNodes.Add(node))
                            continue;
                        var isSelected = _editorMultiSelectedEvents.Contains(node) ||
                                         (_selectedEventKey.HasValue && KeyOf(node) == _selectedEventKey.Value);
                        var isEnd = ReferenceEquals(node, GetEditorLinkChainEnd(root));
                        var projectedNode = _projection.ProjectAtTicks(node.Track, node.Tick, currentTick, currentState);
                        if (OutsideNoteView(projectedNode, projectedNode))
                            continue;
                        var nodeRadius = GetEditorMarkerRadius(node.Track, node.Tick, currentTick, currentState, out var center, node);
                        DrawEditorMarker(center,
                            node.NoteType == NoteType.LinkTail ? nodeRadius * 0.5f : nodeRadius,
                            isSelected ? EditorSelectedColor : isEnd ? EditorEndingColor : EditorFamilyColor, 0.14f);
                    }
                }
                else
                {
                    var projectedNode = _projection.ProjectAtTicks(selected.Track, selected.Tick, currentTick, currentState);
                    if (OutsideNoteView(projectedNode, projectedNode))
                        continue;
                    var nodeRadius = GetEditorMarkerRadius(selected.Track, selected.Tick, currentTick, currentState, out var center, selected);
                    DrawEditorMarker(center,
                        selected.NoteType == NoteType.LinkTail ? nodeRadius * 0.5f : nodeRadius,
                        _editorSelectedHoldEnd ? EditorFamilyColor : EditorSelectedColor, 0.14f);
                    if ((selected.IsHoldNote || selected.NoteType is NoteType.LinkHead or NoteType.LinkTail) && selected.Duration > 6)
                    {
                        var endPoint = _projection.ProjectAtTicks(selected.Track, selected.EndTick, currentTick, currentState);
                        if (!OutsideNoteView(endPoint, endPoint))
                        {
                            var endRadius = GetEditorMarkerRadius(selected.Track, selected.EndTick, currentTick, currentState, out var endCenter, selected);
                            DrawEditorMarker(endCenter, endRadius * 0.5f,
                                _editorSelectedHoldEnd ? EditorSelectedColor : EditorEndingColor, 0.13f);
                        }
                    }
                }
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
        if (hasSelectionGeometry)
        {
            // Flush the already queued 2D markers before drawing the selection quads
            // with the real perspective 3D renderer. Re-enter SpriteBatch afterward for
            // the remaining HUD-style editor markers. Avoid this state transition in
            // ordinary gameplay frames when neither selection rectangle is visible.
            _spriteBatch.End();
            var selectionVertexCount = 0;


            if (_editorRectSelecting && _editorRectStartTrack >= 0 && _editorRectCurrentTrack >= 0)
            {
                // Keep the drag marquee's freeform chart-coordinate corners, but place
                // them directly on their physical 3D bank surfaces rather than drawing
                // a screen-space/billboard polygon between projected points.
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
        if (_editorSuperFlickPlacementPending && _tempo is not null && _editorMouseHoverActive &&
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

        // Live mouse-hover preview. Touch presses do not drive this marker; touch
        // input edits directly at the tapped lane/tick without a hover state.
        if (!_editorPanelsViewEnabled && _chartEditorMode && _editorPlacementEnabled &&
            _hoverBeatTick.HasValue && _hoverTrack is >= 0 and <= 15)
        {
            var hoverMatchesSelection = _selectedBeatTick == _hoverBeatTick && _selectedBeatTrack == _hoverTrack;
            if (_selectedEventKey.HasValue)
            {
                var selectedEvent = _chart.Events.FirstOrDefault(e => KeyOf(e) == _selectedEventKey.Value);
                hoverMatchesSelection |= selectedEvent is not null &&
                    selectedEvent.Tick == _hoverBeatTick.Value && selectedEvent.Track == _hoverTrack.Value;
            }

            // Avoid drawing two circles on top of each other immediately after a
            // placement; the selected-note marker already identifies that exact spot.
            if (!hoverMatchesSelection)
            {
                var point = _projection.ProjectAtTicks(_hoverTrack.Value, _hoverBeatTick.Value, currentTick, currentState);
                if (!OutsideNoteView(point, point))
                {
                    var radius = GetEditorMarkerRadius(_hoverTrack.Value, _hoverBeatTick.Value, currentTick, currentState, out var markerCenter);
                    if (_editorSuperFlickTool == EditorSuperFlickTool.Place)
                    {
                        var position = GearState.Position(_hoverTrack.Value);
                        // Any authored bank is a valid source in the editor, even if
                        // the chart's current route has that bank inactive. In collapsed
                        // view the configured inactive-bank modifier identifies the overlapping bank explicitly.
                        var validSource = position is not null;
                        DrawEditorMarker(markerCenter, radius, validSource ? OuterSuperFlick : new Color(255, 78, 88), 0.20f);
                        var mode = position?.Bank switch { "Forward" => "1K", "Center" => "2K", "Backward" => "3K", _ => "?K" };
                        var previewText = validSource
                            ? $"SUPERFLICK SOURCE {position!.Value.Side.ToUpperInvariant()} {mode} — CLICK, THEN CHOOSE TARGET"
                            : "SUPERFLICK SOURCE MUST BE ON A LANE";
                        var safeText = FontSafeText(_font, previewText);
                        var textSize = UnicodeTextRenderer.MeasureString(_font, safeText);
                        var padding = Math.Max(4f, _projection.ScalePixels(6f));
                        var labelWidth = (int)MathF.Ceiling(textSize.X + padding * 2);
                        var labelHeight = (int)MathF.Ceiling(textSize.Y + padding * 2);
                        var viewportWidth = _graphics.Viewport.Width;
                        var viewportHeight = _graphics.Viewport.Height;
                        labelWidth = Math.Min(labelWidth, Math.Max(1, viewportWidth - 8));
                        var labelX = (int)MathF.Round(markerCenter.X + radius + padding);
                        if (labelX + labelWidth > viewportWidth - 4)
                            labelX = Math.Max(4, (int)MathF.Round(markerCenter.X - radius - padding - labelWidth));
                        var labelY = Math.Clamp((int)MathF.Round(markerCenter.Y - labelHeight * 0.5f), 4, Math.Max(4, viewportHeight - labelHeight - 4));
                        var labelBounds = new Rectangle(labelX, labelY, labelWidth, labelHeight);
                        _spriteBatch.Draw(_pixel, labelBounds, new Color(10, 10, 11, 238));
                        var border = validSource ? OuterSuperFlick : new Color(255, 78, 88);
                        var thickness = Math.Max(1, (int)MathF.Round(_projection.ScalePixels(1f)));
                        _spriteBatch.Draw(_pixel, new Rectangle(labelBounds.X, labelBounds.Y, labelBounds.Width, thickness), border);
                        _spriteBatch.Draw(_pixel, new Rectangle(labelBounds.X, labelBounds.Bottom - thickness, labelBounds.Width, thickness), border);
                        _spriteBatch.Draw(_pixel, new Rectangle(labelBounds.X, labelBounds.Y, thickness, labelBounds.Height), border);
                        _spriteBatch.Draw(_pixel, new Rectangle(labelBounds.Right - thickness, labelBounds.Y, thickness, labelBounds.Height), border);
                        UnicodeTextRenderer.DrawString(_spriteBatch, _font, safeText, new Vector2(labelBounds.X + padding, labelBounds.Y + padding), validSource ? Color.White : new Color(255, 137, 143));
                    }
                    else
                    {
                        DrawEditorMarker(markerCenter, radius, EditorSelectedColor, 0.16f);
                    }
                }
            }
        }

        _spriteBatch.End();
    }

}
