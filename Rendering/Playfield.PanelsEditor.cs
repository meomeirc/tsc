using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System.Globalization;
using TSC.Ares;
using TSC.Input;
using Color = Microsoft.Xna.Framework.Color;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace TSC.Rendering;

public sealed partial class Playfield
{
    private bool _editorPanelUsingSharedDrag;
    private int _editorPanelDragStartPointerTrack;
    private int _editorPanelDragTickOffset;
    private int _editorPanelDragEndTickOffset;
    private bool _editorPanelsMarqueeArmed;
    private bool _editorPanelsMarqueeSelecting;
    private Vector2 _editorPanelsMarqueeStart;
    private Vector2 _editorPanelsMarqueeCurrent;
    private double _editorPanelsMarqueeStartTick;
    private double _editorPanelsMarqueeCurrentTick;
    private float _editorPanelsMarqueeStartTrackCoord;
    private float _editorPanelsMarqueeCurrentTrackCoord;
    private bool _editorPanelsHoverValid;
    private int _editorPanelsHoverTick = -1;
    private int _editorPanelsHoverTrack = -1;
    private bool _editorPanelsConnectorAwaitTarget;
    private bool _editorPanelsConnectorPreviewValid;
    private int _editorPanelsConnectorPreviewTick = -1;

    private static Color GetEditorPanelsAttributeColor(int attr) => attr switch
    {
        101 => new Color(255, 59, 48),   // red
        102 => new Color(255, 149, 0),   // orange
        103 => new Color(255, 214, 10),  // yellow
        104 => new Color(52, 199, 89),   // green
        105 => new Color(48, 209, 232),  // cyan
        106 => new Color(0, 122, 255),   // blue
        107 => new Color(175, 82, 222),  // purple
        108 => new Color(255, 45, 146),  // pink
        _ => Color.White
    };

    // Panel attrs 107–108 use a short 6-tick duration by default.
    // All other panel attrs use a 48-tick duration by default.
    private int GetDefaultEditorPanelAnimationDuration(int attr)
    {
        return attr is 107 or 108 ? 6 : 48;
    }

    private Rectangle GetEditorPanelsTrackAreaBounds(int virtualWidth, int virtualHeight)
    {
        var unit = _projection.ScalePixels(1f);
        var gap = Math.Max(8, (int)MathF.Round(12f * unit));
        var leftMargin = Math.Max(3, (int)MathF.Round(8f * unit));
        var preferredWidth = Math.Max(112, (int)MathF.Round(288f * unit));
        // The attribute toolbar occupies the upper-right corner. Put the four
        // full-height animation tracks entirely to its left so the buttons never
        // cover notes or make their hit areas unreachable.
        var toolbar = GetEditorTopStripPanelBounds(virtualWidth, virtualHeight);
        var right = Math.Max(leftMargin + 1, toolbar.Left - gap);
        var width = Math.Min(preferredWidth, Math.Max(1, right - leftMargin));
        var x = Math.Max(leftMargin, right - width);
        var top = Math.Max(2, (int)MathF.Round(7f * unit));
        var bottomMargin = Math.Max(3, (int)MathF.Round(7f * unit));
        return new Rectangle(x, top, Math.Max(1, right - x), Math.Max(1, virtualHeight - top - bottomMargin));
    }

    private Rectangle GetEditorPanelsTrackColumnBounds(int track, int virtualWidth, int virtualHeight)
    {
        if (track is < 17 or > 20)
            return Rectangle.Empty;
        var area = GetEditorPanelsTrackAreaBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var gap = Math.Max(1, (int)MathF.Round(3f * unit));
        var columnWidth = Math.Max(1, (area.Width - gap * 3) / 4);
        var index = track - 17;
        var x = area.X + index * (columnWidth + gap);
        var width = index == 3 ? area.Right - x : columnWidth;
        return new Rectangle(x, area.Y, Math.Max(1, width), area.Height);
    }

    private float GetEditorPanelsPixelsPerTravelUnit()
    {
        // ScrollPixelsAtTicks is expressed in chart travel units, not screen pixels.
        // The gameplay lane spreads those units across its configured travel length.
        // Apply the same average travel-to-screen ratio here, but keep the overlay
        // linear so animation cues fall at one steady speed in both 2D and 3D views.
        var travelLength = Math.Max(1f, _projection.LaneTravelDistanceForNormalizedX(0f));
        var screenLength = Math.Max(1f, _projection.JudgeY - _projection.LaneStartY);
        return screenLength / travelLength;
    }

    private float GetEditorPanelsTickY(double tick, double currentTick, GearState currentState)
    {
        _ = currentState;
        var travelPixels = _projection.ScrollPixelsAtTicks(Math.Max(0.0, tick), currentTick);
        return _projection.JudgeY - travelPixels * GetEditorPanelsPixelsPerTravelUnit();
    }

    private double GetEditorPanelsContinuousTickAtY(float y, double currentTick, GearState currentState)
    {
        if (_chart is null || _tempo is null || !float.IsFinite(y))
            return Math.Max(0.0, currentTick);

        // Invert the actual tempo-map scroll curve. Keep this continuous for the
        // orange drag preview; the committed empty-tick selector is snapped later.
        var lowTick = 0.0;
        var highTick = Math.Min(1_000_000_000.0,
            Math.Max(currentTick + 10_000.0, _chart.PlayTick + 10_000.0));
        while (highTick < 1_000_000_000.0 && GetEditorPanelsTickY(highTick, currentTick, currentState) > y)
            highTick = Math.Min(1_000_000_000.0, Math.Max(highTick + 1.0, highTick * 2.0));

        for (var i = 0; i < 48; i++)
        {
            var midTick = (lowTick + highTick) * 0.5;
            if (GetEditorPanelsTickY(midTick, currentTick, currentState) > y)
                lowTick = midTick;
            else
                highTick = midTick;
        }

        return Math.Clamp((lowTick + highTick) * 0.5, 0.0, 1_000_000_000.0);
    }

    private int GetEditorPanelsSnappedTickAtY(float y, double currentTick, GearState currentState)
    {
        // Snap the continuous tick directly. Rounding to an integer first can
        // shift the halfway point between grid lines and cause inconsistent drops.
        var continuousTick = GetEditorPanelsContinuousTickAtY(y, currentTick, currentState);
        var snap = Math.Max(1, (int)Math.Round(GetEditorSnapTicks(), MidpointRounding.AwayFromZero));
        return Math.Max(0, (int)Math.Round(
            Math.Round(continuousTick / snap, MidpointRounding.AwayFromZero) * snap,
            MidpointRounding.AwayFromZero));
    }

    private int SnapPanelTickAtOrAfter(int tick)
    {
        var snap = Math.Max(1, (int)Math.Round(GetEditorSnapTicks(), MidpointRounding.AwayFromZero));
        return Math.Max(0, (int)Math.Ceiling(Math.Max(0, tick) / (double)snap) * snap);
    }

    private float GetEditorPanelsTrackCoordinateAtX(float x, int virtualWidth, int virtualHeight)
    {
        var area = GetEditorPanelsTrackAreaBounds(virtualWidth, virtualHeight);
        var clampedX = Math.Clamp(x, area.Left, area.Right);
        var c0 = GetEditorPanelsTrackColumnBounds(17, virtualWidth, virtualHeight).Center.X;
        var c1 = GetEditorPanelsTrackColumnBounds(18, virtualWidth, virtualHeight).Center.X;
        var c2 = GetEditorPanelsTrackColumnBounds(19, virtualWidth, virtualHeight).Center.X;
        var c3 = GetEditorPanelsTrackColumnBounds(20, virtualWidth, virtualHeight).Center.X;

        // Track centers are the X anchors, just as lane centers are in gameplay.
        // Piecewise interpolation keeps the exact visual column centers aligned
        // even when integer rounding makes the edge columns slightly different.
        if (clampedX <= c0)
            return 17f + 0.5f * ((clampedX - area.Left) / Math.Max(1f, c0 - area.Left));
        if (clampedX <= c1)
            return 17.5f + (clampedX - c0) / Math.Max(1f, c1 - c0);
        if (clampedX <= c2)
            return 18.5f + (clampedX - c1) / Math.Max(1f, c2 - c1);
        if (clampedX <= c3)
            return 19.5f + (clampedX - c2) / Math.Max(1f, c3 - c2);
        return 20.5f + 0.5f * ((clampedX - c3) / Math.Max(1f, area.Right - c3));
    }

    private int SnapPanelTick(int tick)
    {
        var snap = Math.Max(1, (int)Math.Round(GetEditorSnapTicks(), MidpointRounding.AwayFromZero));
        return Math.Max(0, (int)Math.Round(tick / (double)snap, MidpointRounding.AwayFromZero) * snap);
    }

    private int GetEditorPanelsTrackAtX(float x, int virtualWidth, int virtualHeight)
    {
        for (var track = 17; track <= 20; track++)
        {
            var column = GetEditorPanelsTrackColumnBounds(track, virtualWidth, virtualHeight);
            if (x >= column.Left && x < column.Right)
                return track;
        }
        var area = GetEditorPanelsTrackAreaBounds(virtualWidth, virtualHeight);
        var fraction = Math.Clamp((x - area.Left) / Math.Max(1f, area.Width), 0f, 0.9999f);
        return 17 + Math.Clamp((int)(fraction * 4f), 0, 3);
    }

    private bool TryMapEditorPanelsPointToCell(Vector2 point, double currentTick, GearState currentState,
        int virtualWidth, int virtualHeight, out int tick, out int track)
    {
        tick = 0;
        track = -1;
        if (!GetEditorPanelsTrackAreaBounds(virtualWidth, virtualHeight).Contains((int)point.X, (int)point.Y))
            return false;
        track = GetEditorPanelsTrackAtX(point.X, virtualWidth, virtualHeight);
        tick = GetEditorPanelsSnappedTickAtY(point.Y, currentTick, currentState);
        return true;
    }

    private Rectangle GetEditorPanelsNoteHeadBounds(ChartEvent ev, double currentTick, GearState currentState,
        int virtualWidth, int virtualHeight)
    {
        var column = GetEditorPanelsTrackColumnBounds(ev.Track, virtualWidth, virtualHeight);
        if (column.IsEmpty)
            return Rectangle.Empty;
        var startY = GetEditorPanelsTickY(ev.Tick, currentTick, currentState);
        if (!float.IsFinite(startY))
            return Rectangle.Empty;

        var unit = _projection.ScalePixels(1f);
        var headHeight = Math.Max(28, (int)MathF.Round(32f * unit));
        var xInset = Math.Max(2, (int)MathF.Round(3f * unit));
        return new Rectangle(column.X + xInset,
            (int)MathF.Round(Math.Clamp(startY - headHeight * 0.5f, -100000f, 100000f)),
            Math.Max(1, column.Width - xInset * 2), headHeight);
    }

    private Rectangle GetEditorPanelsNoteBounds(ChartEvent ev, double currentTick, GearState currentState,
        int virtualWidth, int virtualHeight)
    {
        var column = GetEditorPanelsTrackColumnBounds(ev.Track, virtualWidth, virtualHeight);
        if (column.IsEmpty)
            return Rectangle.Empty;
        var head = GetEditorPanelsNoteHeadBounds(ev, currentTick, currentState, virtualWidth, virtualHeight);
        if (head.IsEmpty)
            return Rectangle.Empty;
        var top = head.Top;
        var bottom = head.Bottom;
        if (ev.Duration > 6)
        {
            var endY = GetEditorPanelsTickY(ev.EndTick, currentTick, currentState);
            if (!float.IsFinite(endY))
                return Rectangle.Empty;
            top = Math.Min(top, (int)MathF.Floor(endY));
            bottom = Math.Max(bottom, (int)MathF.Ceiling(endY));
        }
        // Include a visible resize grip for long cues, even when their true
        // endpoint is only a pixel or two from the head. Duration-six cues have none.
        if (ev.Duration > 6)
        {
            var gripY = GetEditorPanelsDurationGripY(ev, currentTick, currentState, virtualWidth, virtualHeight);
            var gripPadding = Math.Max(3, (int)MathF.Round(5f * _projection.ScalePixels(1f)));
            top = Math.Min(top, (int)MathF.Floor(gripY) - gripPadding);
            bottom = Math.Max(bottom, (int)MathF.Ceiling(gripY) + gripPadding);
        }
        return new Rectangle(column.X, top, column.Width, Math.Max(1, bottom - top));
    }

    private float GetEditorPanelsDurationGripY(ChartEvent ev, double currentTick, GearState currentState,
        int virtualWidth, int virtualHeight)
    {
        _ = virtualWidth;
        _ = virtualHeight;
        if (ev.Duration <= 6)
            return float.NaN;
        var startY = GetEditorPanelsTickY(ev.Tick, currentTick, currentState);
        var endY = GetEditorPanelsTickY(ev.EndTick, currentTick, currentState);
        var head = GetEditorPanelsNoteHeadBounds(ev, currentTick, currentState, virtualWidth, virtualHeight);
        var shortStub = Math.Max(3f, _projection.ScalePixels(3f));
        var actualDelta = endY - startY;
        // Very short long-note endpoints can land inside the head. Extend their
        // handle a few pixels past the head so durations above six remain resizable.
        // Duration-six animation cues intentionally have no endpoint grip.
        if (MathF.Abs(actualDelta) < Math.Max(28f, _projection.ScalePixels(32f)) * 0.55f)
            return head.IsEmpty ? startY - shortStub : head.Top - shortStub;
        return endY;
    }

    private Rectangle GetEditorPanelsDurationEndMarkerBounds(ChartEvent ev, float gripY,
        double currentTick, GearState currentState, int virtualWidth, int virtualHeight)
    {
        var head = GetEditorPanelsNoteHeadBounds(ev, currentTick, currentState, virtualWidth, virtualHeight);
        if (head.IsEmpty || !float.IsFinite(gripY))
            return Rectangle.Empty;
        var markerWidth = Math.Max(1, (int)MathF.Round(head.Width * 0.5f));
        return new Rectangle(head.X + (head.Width - markerWidth) / 2,
            (int)MathF.Round(gripY - head.Height * 0.5f), markerWidth, head.Height);
    }

    private ChartEvent? FindEditorPanelsNoteAtPoint(Vector2 point, double currentTick, GearState currentState,
        int virtualWidth, int virtualHeight, out bool durationGrip)
    {
        durationGrip = false;
        if (_chart is null)
            return null;
        ChartEvent? best = null;
        var bestScore = float.MaxValue;
        var gripTolerance = Math.Max(8f, _projection.ScalePixels(6f));
        var gripWidth = Math.Max(24f, _projection.ScalePixels(28f));
        foreach (var ev in _chart.Events.Where(ev => ev.Kind == "Note" && ev.RawType == 1 &&
                     ev.Track is >= 17 and <= 20 && ev.Attr is >= 101 and <= 108))
        {
            var rect = GetEditorPanelsNoteBounds(ev, currentTick, currentState, virtualWidth, virtualHeight);
            var head = GetEditorPanelsNoteHeadBounds(ev, currentTick, currentState, virtualWidth, virtualHeight);
            var column = GetEditorPanelsTrackColumnBounds(ev.Track, virtualWidth, virtualHeight);
            if (rect.IsEmpty || column.IsEmpty || !column.Contains((int)point.X, (int)point.Y))
                continue;

            var gripY = ev.Duration > 6
                ? GetEditorPanelsDurationGripY(ev, currentTick, currentState, virtualWidth, virtualHeight)
                : float.NaN;
            var selectedForHighlight = (_selectedEventKey.HasValue && KeyOf(ev) == _selectedEventKey.Value) ||
                                       _editorMultiSelectedEvents.Contains(ev);
            // When selected, the visible end handle is a half-width hollow box.
            // Hit-test that same footprint so dragging anywhere on the highlight
            // resizes the duration instead of falling through to a head/body move.
            var selectedEndBounds = selectedForHighlight && ev.Duration > 6 && float.IsFinite(gripY)
                ? GetEditorPanelsDurationEndMarkerBounds(ev, gripY, currentTick, currentState,
                    virtualWidth, virtualHeight)
                : Rectangle.Empty;
            var inGripX = MathF.Abs(point.X - column.Center.X) <= gripWidth * 0.5f;
            var isGrip = !_editorConnectorMode && ev.Duration > 6 && float.IsFinite(gripY) &&
                         ((!selectedEndBounds.IsEmpty && selectedEndBounds.Contains((int)point.X, (int)point.Y)) ||
                          (inGripX && MathF.Abs(point.Y - gripY) <= gripTolerance));
            var hitNote = rect.Contains((int)point.X, (int)point.Y);
            if (!isGrip && !hitNote)
                continue;

            // In ordinary selection, a visible grip wins over the body hit region.
            // Connector mode ignores grips so the target note's head can be chosen.
            var score = isGrip ? -1000f + MathF.Abs(point.Y - gripY) :
                Vector2.DistanceSquared(point, new Vector2(head.Center.X, GetEditorPanelsTickY(ev.Tick, currentTick, currentState)));
            if (score < bestScore)
            {
                best = ev;
                bestScore = score;
                durationGrip = isGrip;
            }
        }
        return best;
    }

    private void ToggleEditorPanelsView()
    {
        if (!_chartEditorMode || _chart is null)
            return;

        _editorPanelsViewEnabled = !_editorPanelsViewEnabled;
        _editorMenuSection = EditorMenuSection.None;
        _editorSelectLineDragging = false;
        _editorSelectLineStartTrack = -1;
        _editorBpmInputFocused = false;
        _editorBpmInputReplaceOnNextKey = false;
        _editorBpmPlacementPending = false;
        _editorSelectedTempoEvent = null;
        _editorConnectorMode = false;
        _editorPanelsConnectorAwaitTarget = false;
        _editorPanelsConnectorPreviewValid = false;
        _editorPanelsConnectorPreviewTick = -1;
        _editorSuperFlickTool = EditorSuperFlickTool.None;
        _editorSuperFlickPlacementPending = false;
        _editorPanelUsingSharedDrag = false;
        _editorPanelsMarqueeArmed = false;
        _editorPanelsMarqueeSelecting = false;
        _editorPanelsHoverValid = false;
        _editorPanelsHoverTick = -1;
        _editorPanelsHoverTrack = -1;
        _hoverBeatTick = null;
        _hoverTrack = null;
        _editorMetaPanelOpen = false;
        _editorRectSelecting = false;
        _editorRectSelectionArmed = false;
        _editorMultiSelectedEvents.Clear();
        _editorSelectedGridCells.Clear();
        _editorPanelsSelectedEmptyCell = null;
        _editorSelectReferenceLineTick = null;
        _selectedEventKey = null;
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorSelectedLinkRoot = null;
        _editorSelectedHoldEnd = false;
        _editorPlacementEnabled = false;
        _editorAttr = 0;
        _editorNoteType = NoteType.Single;
        _editorDuration = 6;
        _editorTrack = Math.Clamp(_editorTrack, 0, 15);
        CancelEditorMetaField();
        // Panels view is always collapsed, but it must not overwrite the user's
        // preferred LANE layout. Returning to LANE restores that preference in 2D;
        // 3D remains collapsed until the user switches back to 2D.
        _compact = _editorPanelsViewEnabled
            ? true
            : (_topDownViewEnabled ? _editorPreferredCompact : true);
        _projection.Compact = _compact;
        _projection.InvalidateGeometryCaches();
        InvalidateLaneMeshCache();
        _editorHoverCacheValid = false;
        _statusMessage = _editorPanelsViewEnabled
            ? "Panels view ON. Collapsed mode is locked. Select attr 101–108 and edit tracks 17–20."
            : "Panels view OFF. Gameplay-note editing restored.";
    }

    private void ToggleEditorPanelsConnectorTool()
    {
        if (!_editorPanelsViewEnabled)
            return;
        var alreadyActive = _editorConnectorMode;
        _editorConnectorMode = !alreadyActive;
        _editorPlacementEnabled = false;
        _editorSuperFlickTool = EditorSuperFlickTool.None;
        _editorPanelsConnectorAwaitTarget = !alreadyActive &&
            GetSelectedEditorDurationSelection().Any(ev => ev.Track is >= 17 and <= 20 && ev.Attr is >= 101 and <= 108);
        _editorPanelsConnectorPreviewValid = false;
        _editorPanelsHoverValid = false;
        if (alreadyActive)
        {
            _editorPanelsConnectorAwaitTarget = false;
            _statusMessage = "Panel connector tool off.";
        }
        else if (_editorPanelsConnectorAwaitTarget)
        {
            _statusMessage = "Panel connector ready. Move to any later tick and click to set the selected animation notes' duration end.";
        }
        else
        {
            _statusMessage = "Panel connector: click a source animation note, then click any later tick to set its duration end.";
        }
    }

    private void SelectEditorPanelAttribute(int attr)
    {
        if (!_editorPanelsViewEnabled || attr is < 101 or > 108)
            return;
        var alreadyActive = _editorPanelsSelectedAttr == attr && _editorPlacementEnabled;
        _editorPanelsSelectedAttr = attr;
        _editorAttr = _editorPanelsSelectedAttr;
        _editorPlacementEnabled = !alreadyActive;
        _editorConnectorMode = false;
        _editorPanelsConnectorAwaitTarget = false;
        _editorPanelsConnectorPreviewValid = false;
        _editorNoteType = NoteType.Single;
        _editorDuration = GetDefaultEditorPanelAnimationDuration(_editorPanelsSelectedAttr);
        _editorSuperFlickTool = EditorSuperFlickTool.None;
        _editorSelectedTempoEvent = null;
        _editorMenuSection = EditorMenuSection.None;
        _statusMessage = _editorPlacementEnabled
            ? $"Panels tool: attr {_editorPanelsSelectedAttr}. Place and edit it only in tracks 17–20."
            : "Panel placement tool off. Drag on empty space to multi-select; existing panel notes can still be moved or resized.";
    }

    private bool HandleEditorPanelsMouseSelection(Vector2 point, Point pixel, double currentTick, GearState currentState,
        bool leftDown, bool leftPressed, bool rightPressed, bool touchActive, bool touchPressed, bool touchMoved,
        bool mouseMoved, bool touchInputThisFrame, bool pointerOverToolbar, int virtualWidth, int virtualHeight)
    {
        if (!_editorPanelsViewEnabled || _chart is null || _tempo is null)
            return false;

        _editorPanelsHoverValid = false;
        _editorPanelsConnectorPreviewValid = false;
        var area = GetEditorPanelsTrackAreaBounds(virtualWidth, virtualHeight);

        // SELECT is a filter submenu, not a note-selection tool. It has its own
        // cyan line gesture for choosing a horizontal track span and reference tick;
        // the orange marquee and all panel-note hit handling stay disabled.
        if (_editorMenuSection == EditorMenuSection.Select)
        {
            _editorPanelsMarqueeArmed = false;
            _editorPanelsMarqueeSelecting = false;

            if (_editorSelectLineDragging)
            {
                ContinueEditorSelectLineDrag(point, currentTick, currentState,
                    virtualWidth, virtualHeight, leftDown || touchActive, panelsView: true);
                return true;
            }

            if (pointerOverToolbar || GetEditorTimelineScrubberBounds(virtualWidth, virtualHeight).Contains(pixel))
                return false;

            if ((leftPressed || touchPressed) && area.Contains(pixel) &&
                BeginEditorSelectLineDrag(point, currentTick, currentState,
                    virtualWidth, virtualHeight, panelsView: true))
                return true;

            return true;
        }

        if (_editorMouseHoverActive && !touchInputThisFrame && !pointerOverToolbar &&
            !_editorPanelUsingSharedDrag && !_editorPanelsMarqueeSelecting && _editorPlacementEnabled &&
            !_editorConnectorMode && _editorPanelsSelectedAttr is >= 101 and <= 108 && area.Contains(pixel) &&
            TryMapEditorPanelsPointToCell(point, currentTick, currentState, virtualWidth, virtualHeight,
                out var hoverTick, out var hoverTrack))
        {
            _editorPanelsHoverValid = true;
            _editorPanelsHoverTick = hoverTick;
            _editorPanelsHoverTrack = hoverTrack;
        }

        // Connector previews follow the pointer's tick, not the track column. The
        // endpoint can now be placed on any tick, including blank space between notes.
        if (_editorMouseHoverActive && !touchInputThisFrame && !pointerOverToolbar &&
            _editorConnectorMode && _editorPanelsConnectorAwaitTarget)
        {
            _editorPanelsConnectorPreviewTick = GetEditorPanelsSnappedTickAtY(
                Math.Clamp(point.Y, area.Top, area.Bottom - 1), currentTick, currentState);
            _editorPanelsConnectorPreviewValid = true;
        }

        // Bank indicators remain usable in Panels view. Let the normal toolbar / bank
        // button hit testing handle them rather than swallowing them as an empty track.
        if (!_editorPanelUsingSharedDrag && !_editorPanelsMarqueeArmed && !_editorPanelsMarqueeSelecting &&
            IsEditorBankIndicatorArea(point, virtualWidth, virtualHeight))
            return false;

        if (_editorPanelUsingSharedDrag && _editorDraggedEvent is not null)
        {
            var held = _editorDragStartedByTouch ? touchActive : leftDown;
            if (held)
            {
                if (!pointerOverToolbar && (mouseMoved || touchMoved || touchPressed))
                    UpdateEditorPanelsDrag(point, currentTick, currentState, virtualWidth, virtualHeight);
            }
            else
            {
                FinishEditorDrag();
                _editorPanelUsingSharedDrag = false;
            }
            return true;
        }

        if (_editorPanelsMarqueeArmed || _editorPanelsMarqueeSelecting)
        {
            var held = leftDown || touchActive;
            if (!pointerOverToolbar)
                _editorPanelsMarqueeCurrent = point;

            if (_editorPanelsMarqueeArmed &&
                Vector2.DistanceSquared(_editorPanelsMarqueeStart, _editorPanelsMarqueeCurrent) >= 36f)
            {
                _editorPanelsMarqueeArmed = false;
                _editorPanelsMarqueeSelecting = true;
                // Gameplay selection replaces the old selection when an actual drag
                // begins. Shift is reserved for note-level multi-select, not ranges.
                _editorMultiSelectedEvents.Clear();
                _editorSelectedGridCells.Clear();
                _editorPanelsSelectedEmptyCell = null;
                _editorSelectReferenceLineTick = null;
                _selectedEventKey = null;
                _selectedBeatTick = null;
                _selectedBeatTrack = null;
                _editorSelectedLinkRoot = null;
                _editorSelectedHoldEnd = false;
                _statusMessage = "Selecting animation tracks by X range and tick range…";
            }

            if (_editorPanelsMarqueeSelecting)
            {
                _editorPanelsMarqueeCurrentTick = GetEditorPanelsContinuousTickAtY(
                    _editorPanelsMarqueeCurrent.Y, currentTick, currentState);
                _editorPanelsMarqueeCurrentTrackCoord = GetEditorPanelsTrackCoordinateAtX(
                    _editorPanelsMarqueeCurrent.X, virtualWidth, virtualHeight);
            }

            if (held)
                return true;

            if (_editorPanelsMarqueeSelecting)
            {
                FinishEditorPanelsMarquee(currentTick, currentState, virtualWidth, virtualHeight);
            }
            else if (_editorPanelsMarqueeArmed && area.Contains(pixel) &&
                     TryMapEditorPanelsPointToCell(point, currentTick, currentState, virtualWidth, virtualHeight,
                         out var emptyTick, out var emptyTrack) &&
                     FindEditorPanelsNoteAtGridCell(emptyTick, emptyTrack) is null)
            {
                // A simple click on an unoccupied animation cell is a real selection,
                // not a failed zero-width marquee. This also gives SELECT a tick/track
                // anchor when the menu is open.
                _editorSelectedGridCells.Clear();
                _editorSelectedGridCells.Add((emptyTick, emptyTrack));
                _editorPanelsSelectedEmptyCell = (emptyTick, emptyTrack);
                _editorMultiSelectedEvents.Clear();
                _selectedEventKey = null;
                _selectedBeatTick = null;
                _selectedBeatTrack = null;
                _editorSelectedLinkRoot = null;
                _editorSelectedHoldEnd = false;
                _editorSelectReferenceTickOverride = null;
                _editorSelectReferenceLineTick = _editorMenuSection == EditorMenuSection.Select ? emptyTick : null;
                _statusMessage = $"Selected empty panel cell: tick {emptyTick}, track {emptyTrack}.";
            }
            _editorPanelsMarqueeArmed = false;
            _editorPanelsMarqueeSelecting = false;
            return true;
        }

        // Preserve the common timeline scrubber and every visible button.
        if (GetEditorTimelineScrubberBounds(virtualWidth, virtualHeight).Contains(pixel) || pointerOverToolbar)
            return false;

        var shift = _editorConnectorMode
            ? KeybindSettings.IsModifierBindingDown("editor_connect_modifier")
            : KeybindSettings.IsDown("editor_connect_modifier");
        if (!area.Contains(pixel))
        {
            if (!_editorPlacementEnabled && !_editorConnectorMode && (leftPressed || touchPressed))
            {
                BeginEditorPanelsMarquee(point, currentTick, currentState, virtualWidth, virtualHeight);
                return true;
            }
            // Ignore the pointer's X position when setting a connector endpoint;
            // only the snapped tick (Y) matters.
            if (_editorConnectorMode && _editorPanelsConnectorAwaitTarget && (leftPressed || touchPressed))
            {
                var targetTick = GetEditorPanelsSnappedTickAtY(
                    Math.Clamp(point.Y, area.Top, area.Bottom - 1), currentTick, currentState);
                HandleEditorPanelsConnectorTargetTick(targetTick, clickedTargetNote: null);
                return true;
            }
            return true; // Panels mode must never fall through to gameplay-note placement.
        }

        if (rightPressed)
        {
            _editorPanelsConnectorAwaitTarget = false;
            _editorPanelsConnectorPreviewValid = false;
            _editorDurationInputValuePending = false;
            _editorMultiSelectedEvents.Clear();
            _editorSelectedGridCells.Clear();
            _editorPanelsSelectedEmptyCell = null;
            _editorSelectReferenceLineTick = null;
            _selectedEventKey = null;
            _selectedBeatTick = null;
            _selectedBeatTrack = null;
            _editorSelectedHoldEnd = false;
            _statusMessage = "Panel-note selection cleared.";
            return true;
        }
        if (!(leftPressed || touchPressed))
            return true;

        if (!TryMapEditorPanelsPointToCell(point, currentTick, currentState, virtualWidth, virtualHeight,
                out var snappedTick, out var trackIndex))
            return true;

        var selectedNote = FindEditorPanelsNoteAtPoint(point, currentTick, currentState,
            virtualWidth, virtualHeight, out var durationGrip);
        if (_editorConnectorMode)
        {
            _editorSelectedGridCells.Clear();
            _editorPanelsSelectedEmptyCell = null;
            _editorSelectReferenceLineTick = null;
            _editorSelectReferenceTickOverride = null;
            if (_editorPanelsConnectorAwaitTarget)
            {
                _editorDurationInputValuePending = false;
                HandleEditorPanelsConnectorTargetTick(snappedTick,
                    selectedNote is not null && selectedNote.Tick == snappedTick ? selectedNote : null);
            }
            else if (selectedNote is null)
            {
                _statusMessage = "Panel connector: click a source animation note first, then click any later tick to set its duration end.";
            }
            else
            {
                _editorDurationInputValuePending = false;
                HandleEditorPanelsConnectorSourceClick(selectedNote, shift);
            }
            return true;
        }

        if (selectedNote is not null)
        {
            _editorSelectedGridCells.Clear();
            _editorPanelsSelectedEmptyCell = null;
            _editorSelectReferenceLineTick = _editorMenuSection == EditorMenuSection.Select ? selectedNote.Tick : null;
            _editorSelectReferenceTickOverride = null;
            _editorDurationInputValuePending = false;
            var selectedKey = KeyOf(selectedNote);
            if (durationGrip)
            {
                // A duration endpoint is an independent edit target. Do not retain a
                // multi-selection behind it; only this note's duration is being edited.
                _editorMultiSelectedEvents.Clear();
            }
            else if (shift)
            {
                if (_editorMultiSelectedEvents.Contains(selectedNote))
                {
                    _editorMultiSelectedEvents.Remove(selectedNote);
                    _selectedEventKey = _editorMultiSelectedEvents.Count > 0
                        ? KeyOf(_editorMultiSelectedEvents.First())
                        : null;
                    _statusMessage = "Panel note removed from multi-selection.";
                    return true;
                }
                _editorMultiSelectedEvents.Add(selectedNote);
            }
            else if (!(_editorMultiSelectedEvents.Count > 1 && _editorMultiSelectedEvents.Contains(selectedNote)))
            {
                _editorMultiSelectedEvents.Clear();
            }

            _selectedEventKey = selectedKey;
            _selectedBeatTick = null;
            _selectedBeatTrack = selectedNote.Track;
            _editorTrack = selectedNote.Track;
            _editorSelectedLinkRoot = null;
            _editorSelectedHoldEnd = durationGrip;
            _statusMessage = durationGrip
                ? $"Selected panel note attr {selectedNote.Attr}; drag its duration handle to resize."
                : $"Selected panel note attr {selectedNote.Attr}: tick {selectedNote.Tick}, track {selectedNote.Track}. Drag to move.";
            if (IsEditorDraggableNote(selectedNote))
            {
                BeginEditorDrag(selectedNote, durationGrip, touchPressed, point, currentTick, currentState);
                _editorPanelUsingSharedDrag = true;
                _editorPanelDragStartPointerTrack = trackIndex;
                _editorPanelDragTickOffset = selectedNote.Tick - snappedTick;
                _editorPanelDragEndTickOffset = selectedNote.EndTick - snappedTick;
                if (_editorDragIsMultiSelection)
                {
                    _editorMultiDragAnchorTick = snappedTick;
                    _editorMultiDragAnchorTrack = trackIndex;
                    // Force the first update through even if the pointer remains
                    // in its initial snap cell, so any existing off-grid notes are
                    // corrected to the actual grid when dragging starts.
                    _editorMultiDragLastTickDelta = int.MinValue;
                    _editorMultiDragLastTrackDelta = int.MinValue;
                }
            }
            return true;
        }

        if (_editorPlacementEnabled && _editorPanelsSelectedAttr is >= 101 and <= 108)
        {
            _editorSelectedGridCells.Clear();
            _editorPanelsSelectedEmptyCell = null;
            _editorSelectReferenceLineTick = null;
            _editorSelectReferenceTickOverride = null;
            _editorDurationInputValuePending = false;
            AddEditorPanelNote(snappedTick, trackIndex);
            return true;
        }

        _editorDurationInputValuePending = false;
        BeginEditorPanelsMarquee(point, currentTick, currentState, virtualWidth, virtualHeight);
        return true;
    }

    private void BeginEditorPanelsMarquee(Vector2 point, double currentTick, GearState currentState,
        int virtualWidth, int virtualHeight)
    {
        _editorPanelsMarqueeArmed = true;
        _editorPanelsMarqueeSelecting = false;
        _editorPanelsMarqueeStart = point;
        _editorPanelsMarqueeCurrent = point;
        _editorPanelsMarqueeStartTick = GetEditorPanelsContinuousTickAtY(point.Y, currentTick, currentState);
        _editorPanelsMarqueeCurrentTick = _editorPanelsMarqueeStartTick;
        _editorPanelsMarqueeStartTrackCoord = GetEditorPanelsTrackCoordinateAtX(point.X, virtualWidth, virtualHeight);
        _editorPanelsMarqueeCurrentTrackCoord = _editorPanelsMarqueeStartTrackCoord;
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _statusMessage = "Drag across animation tracks to preview the X/tick selection range.";
    }

    private ChartEvent? FindEditorPanelsNoteAtGridCell(int tick, int track)
    {
        if (_chart is null || track is < 17 or > 20)
            return null;
        return _chart.Events
            .Where(ev => ev.Kind == "Note" && ev.RawType == 1 && ev.Track == track && ev.Attr is >= 101 and <= 108 &&
                        ev.Tick <= tick && tick <= ev.EndTick)
            .OrderBy(ev => ev.Tick == tick ? 0 : 1)
            .ThenBy(ev => ev.Tick)
            .ThenBy(ev => ev.EventIndex)
            .FirstOrDefault();
    }

    private void HandleEditorPanelsConnectorSourceClick(ChartEvent clicked, bool additive)
    {
        if (_chart is null || clicked.Kind != "Note" || clicked.RawType != 1 ||
            clicked.Track is < 17 or > 20 || clicked.Attr is < 101 or > 108)
        {
            _statusMessage = "Panel connector only works with animation notes on tracks 17–20.";
            return;
        }

        if (!additive)
            _editorMultiSelectedEvents.Clear();
        if (!_editorMultiSelectedEvents.Contains(clicked))
            _editorMultiSelectedEvents.Add(clicked);
        _selectedEventKey = KeyOf(clicked);
        _selectedBeatTick = null;
        _selectedBeatTrack = clicked.Track;
        _editorTrack = clicked.Track;
        _editorSelectedLinkRoot = null;
        _editorSelectedHoldEnd = false;
        _editorPanelsConnectorAwaitTarget = true;
        _editorPanelsConnectorPreviewValid = false;
        _statusMessage = $"Panel connector source: tick {clicked.Tick}, track {clicked.Track}. Move to any later tick and click to set the duration end.";
    }

    private void HandleEditorPanelsConnectorTargetTick(int targetTick, ChartEvent? clickedTargetNote)
    {
        if (_chart is null)
            return;

        var selectedSources = GetSelectedEditorDurationSelection()
            .Where(ev => ev.Kind == "Note" && ev.RawType == 1 &&
                        ev.Track is >= 17 and <= 20 && ev.Attr is >= 101 and <= 108)
            .Distinct()
            .OrderBy(ev => ev.Tick)
            .ThenBy(ev => ev.Track)
            .ThenBy(ev => ev.EventIndex)
            .ToList();

        if (selectedSources.Count == 0)
        {
            _editorPanelsConnectorAwaitTarget = false;
            _editorPanelsConnectorPreviewValid = false;
            _statusMessage = "Panel connector has no selected animation-note source. Click a source note first.";
            return;
        }

        // The requested endpoint is a chart tick, not a destination note. Apply the
        // same endpoint tick to every selected source that starts before it.
        var changes = new List<(ChartEvent Event, int Duration, NoteType Type)>();
        var tooLong = false;
        foreach (var ev in selectedSources.Where(ev => ev.Tick < targetTick))
        {
            var durationLong = (long)targetTick - ev.Tick;
            if (durationLong is < 1 or > 65535)
            {
                tooLong = true;
                continue;
            }
            var duration = (int)durationLong;
            var noteType = duration > 6 ? NoteType.SingleLong : NoteType.Single;
            if (ev.Duration != duration || ev.NoteType != noteType)
                changes.Add((ev, duration, noteType));
        }

        if (!selectedSources.Any(ev => ev.Tick < targetTick))
        {
            _statusMessage = "Choose a tick later than the selected source note(s). Their current durations were kept.";
            return;
        }

        if (changes.Count > 0)
        {
            var undoSnapshot = CaptureEditorChartSnapshot();
            foreach (var change in changes)
            {
                change.Event.Duration = change.Duration;
                change.Event.NoteType = change.Type;
                change.Event.LinePair = -1;
            }
            CommitEditorUndoSnapshot(undoSnapshot);
            RefreshAfterEditorMutation();
            _editorHoverCacheValid = false;
        }

        // If the chosen endpoint is also the start of another animation note,
        // make that note the next source (the familiar click-note-to-note workflow).
        // A free tick has no next note, so keep the current selection ready for another
        // endpoint adjustment instead of turning off the connector tool.
        if (clickedTargetNote is not null && clickedTargetNote.Tick == targetTick &&
            clickedTargetNote.Kind == "Note" && clickedTargetNote.RawType == 1 &&
            clickedTargetNote.Track is >= 17 and <= 20 && clickedTargetNote.Attr is >= 101 and <= 108)
        {
            _editorMultiSelectedEvents.Clear();
            _editorMultiSelectedEvents.Add(clickedTargetNote);
            _selectedEventKey = KeyOf(clickedTargetNote);
            _selectedBeatTick = null;
            _selectedBeatTrack = clickedTargetNote.Track;
            _editorTrack = clickedTargetNote.Track;
        }

        _editorSelectedHoldEnd = false;
        _editorSelectedLinkRoot = null;
        _editorPanelsConnectorAwaitTarget = true;
        _editorPanelsConnectorPreviewValid = false;
        _statusMessage = changes.Count > 0
            ? $"Set the duration endpoint to tick {targetTick} for {changes.Count} animation note(s)." +
              (tooLong ? " Some notes exceeded the 65535-tick limit and were skipped." : "") +
              " Click another tick to adjust it, or a note start to continue chaining."
            : $"The selected animation note(s) already end at tick {targetTick}, or none could be changed." +
              (tooLong ? " Some notes exceeded the 65535-tick limit and were skipped." : "");
    }

    private void AddEditorPanelNote(int tick, int trackIndex)
    {
        if (!_chartEditorMode || !_editorPanelsViewEnabled || _chart is null || trackIndex is < 17 or > 20 ||
            _editorPanelsSelectedAttr is < 101 or > 108)
            return;

        var occupied = FindEditorPanelsNoteAtGridCell(tick, trackIndex) is not null;
        if (occupied)
        {
            _statusMessage = "A panel note already exists at that track and tick.";
            return;
        }

        var undoSnapshot = CaptureEditorChartSnapshot();
        var track = _chart.FindTrack(trackIndex);
        if (track is null)
        {
            track = new ChartTrack { TrackIndex = trackIndex };
            _chart.Tracks.Add(track);
        }
        var duration = Math.Max(6, _editorDuration);
        var ev = new ChartEvent
        {
            Tick = tick,
            Track = trackIndex,
            Attr = _editorPanelsSelectedAttr,
            RawType = 1,
            TypeIndex = -1,
            Duration = duration,
            NoteType = duration > 6 ? NoteType.SingleLong : NoteType.Single,
            EventIndex = track.Events.Select(item => item.EventIndex).DefaultIfEmpty(-1).Max() + 1,
            LinePair = -1,
            Kind = "Note",
            Instrument = 0,
            Velocity = 127,
            Pan = 64
        };
        track.Events.Add(ev);
        CommitEditorUndoSnapshot(undoSnapshot);
        RefreshAfterEditorMutation();
        _selectedEventKey = KeyOf(ev);
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorTrack = trackIndex;
        _editorMultiSelectedEvents.Clear();
        _editorSelectedHoldEnd = false;
        _statusMessage = $"Added panel animation attr {ev.Attr} at tick {tick}, track {trackIndex}.";
    }

    private void UpdateEditorPanelsDrag(Vector2 point, double currentTick, GearState currentState,
        int virtualWidth, int virtualHeight)
    {
        if (_chart is null || _editorDraggedEvent is null)
            return;
        if (!TryMapEditorPanelsPointToCell(point, currentTick, currentState, virtualWidth, virtualHeight,
                out var snappedTick, out var snappedTrack))
        {
            // Keep the drag constrained to tracks 17-20 while the pointer leaves the
            // small track overlay, rather than allowing the note to escape its track family.
            var area = GetEditorPanelsTrackAreaBounds(virtualWidth, virtualHeight);
            snappedTrack = GetEditorPanelsTrackAtX(Math.Clamp(point.X, area.Left, area.Right - 1), virtualWidth, virtualHeight);
            snappedTick = GetEditorPanelsSnappedTickAtY(Math.Clamp(point.Y, area.Top, area.Bottom - 1), currentTick, currentState);
        }

        var ev = _editorDraggedEvent;
        if (_editorDragIsMultiSelection)
        {
            if (_editorMultiDragItems.Count == 0)
                return;
            var minTick = _editorMultiDragItems.Min(item => item.Tick);
            var minTrack = _editorMultiDragItems.Min(item => item.Track);
            var maxTrack = _editorMultiDragItems.Max(item => item.Track);
            var tickDelta = Math.Max(-minTick, snappedTick - _editorMultiDragAnchorTick);
            var trackDelta = Math.Clamp(snappedTrack - _editorMultiDragAnchorTrack, 17 - minTrack, 20 - maxTrack);
            if (tickDelta == _editorMultiDragLastTickDelta && trackDelta == _editorMultiDragLastTrackDelta)
                return;

            var moving = _editorMultiDragItems.Select(item => item.Event).ToHashSet();
            foreach (var item in _editorMultiDragItems)
            {
                // Apply the movement relative to each original note, then snap
                // each final start tick. Moving an already-offset note must not
                // carry its off-grid remainder along with it.
                item.Event.Tick = SnapPanelTick(item.Tick + tickDelta);
                item.Event.Track = item.Track + trackDelta;
                item.Event.Duration = item.Duration;
                item.Event.EventIndex = item.EventIndex;
            }
            if (trackDelta != 0)
            {
                foreach (var group in _editorMultiDragItems.GroupBy(item => item.Track + trackDelta))
                {
                    var destination = _chart.FindTrack(group.Key);
                    var nextIndex = (destination?.Events.Where(candidate => !moving.Contains(candidate))
                        .Select(candidate => candidate.EventIndex).DefaultIfEmpty(-1).Max() ?? -1) + 1;
                    foreach (var item in group.OrderBy(item => item.Tick).ThenBy(item => item.EventIndex))
                        item.Event.EventIndex = nextIndex++;
                }
            }
            _editorMultiDragLastTickDelta = tickDelta;
            _editorMultiDragLastTrackDelta = trackDelta;
            _editorDragChanged = _editorMultiDragItems.Any(item =>
                item.Event.Tick != item.Tick || item.Event.Track != item.Track);
            _statusMessage = _editorDragChanged
                ? $"Moving {_editorMultiDragItems.Count} panel notes: tick offset {tickDelta:+#;-#;0}, track offset {trackDelta:+#;-#;0}."
                : $"Selected {_editorMultiDragItems.Count} panel notes.";
            _selectedEventKey = KeyOf(ev);
        }
        else if (_editorDragIsHoldEnd)
        {
            // The end is an absolute grid target, not a relative offset from its
            // previous (possibly off-grid) tick. Keep a six-tick minimum while
            // ensuring the chosen endpoint still lands on a snap line.
            var targetEnd = SnapPanelTick(snappedTick + _editorPanelDragEndTickOffset);
            targetEnd = Math.Max(targetEnd, SnapPanelTickAtOrAfter(ev.Tick + 5));
            ev.Duration = Math.Max(7, targetEnd - ev.Tick);
            ev.NoteType = ev.Duration > 6 ? NoteType.SingleLong : NoteType.Single;
            ev.LinePair = -1;
            _editorDragChanged = ev.Duration != _editorDragOriginalDuration;
            _statusMessage = $"Resizing panel attr {ev.Attr}: tick {ev.Tick} to {ev.EndTick}.";
        }
        else
        {
            // Snap the resulting note start itself, rather than preserving any
            // off-grid remainder from the note's original placement.
            var newTick = SnapPanelTick(snappedTick + _editorPanelDragTickOffset);
            var newTrack = Math.Clamp(snappedTrack + (_editorDragOriginalTrack - _editorPanelDragStartPointerTrack), 17, 20);
            var nextIndex = ev.EventIndex;
            if (newTrack == _editorDragOriginalTrack)
            {
                nextIndex = _editorDragOriginalEventIndex;
            }
            else if (newTrack != ev.Track)
            {
                var destination = _chart.FindTrack(newTrack);
                nextIndex = (destination?.Events.Where(candidate => !ReferenceEquals(candidate, ev))
                    .Select(candidate => candidate.EventIndex).DefaultIfEmpty(-1).Max() ?? -1) + 1;
            }
            ev.Tick = newTick;
            ev.Track = newTrack;
            ev.EventIndex = nextIndex;
            _editorDragChanged = ev.Tick != _editorDragOriginalTick || ev.Track != _editorDragOriginalTrack;
            _statusMessage = $"Moving panel attr {ev.Attr}: tick {ev.Tick}, track {ev.Track}.";
            _selectedEventKey = KeyOf(ev);
        }

        _editorTrack = ev.Track;
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorHoverCacheValid = false;
        RebuildRenderEventCache();
    }

    private void FinishEditorPanelsMarquee(double currentTick, GearState currentState,
        int virtualWidth, int virtualHeight)
    {
        if (_chart is null || _tempo is null)
            return;

        var lowTick = Math.Max(0.0, Math.Min(_editorPanelsMarqueeStartTick, _editorPanelsMarqueeCurrentTick));
        var highTick = Math.Max(lowTick, Math.Max(_editorPanelsMarqueeStartTick, _editorPanelsMarqueeCurrentTick));
        var lowTrackCoord = Math.Min(_editorPanelsMarqueeStartTrackCoord, _editorPanelsMarqueeCurrentTrackCoord) - 0.0001f;
        var highTrackCoord = Math.Max(_editorPanelsMarqueeStartTrackCoord, _editorPanelsMarqueeCurrentTrackCoord) + 0.0001f;
        var selectedTracks = Enumerable.Range(17, 4)
            .Where(track => track + 0.5f >= lowTrackCoord && track + 0.5f <= highTrackCoord)
            .ToList();

        _editorMultiSelectedEvents.Clear();
        _editorSelectedGridCells.Clear();
        foreach (var ev in _chart.Events.Where(ev => ev.Kind == "Note" && ev.RawType == 1 &&
                     ev.Track is >= 17 and <= 20 && ev.Attr is >= 101 and <= 108))
        {
            if (selectedTracks.Contains(ev.Track) && ev.Tick >= lowTick && ev.Tick <= highTick)
                _editorMultiSelectedEvents.Add(ev);
        }

        // The rectangle selects animation notes only. Empty cells crossed by the
        // marquee do not become selections or broaden X=SELECTED.
        foreach (var ev in _editorMultiSelectedEvents)
            _editorSelectedGridCells.Add((ev.Tick, ev.Track));

        _selectedEventKey = null;
        _selectedBeatTick = null;
        _selectedBeatTrack = null;
        _editorSelectedLinkRoot = null;
        _editorSelectedHoldEnd = false;
        _editorPanelsSelectedEmptyCell = null;
        _editorSelectReferenceTickOverride = null;
        _editorSelectReferenceLineTick = _editorMenuSection == EditorMenuSection.Select && _editorSelectedGridCells.Count > 0
            ? _editorSelectedGridCells.Min(cell => cell.Tick)
            : null;
        _editorHoverCacheValid = false;

        if (_editorMultiSelectedEvents.Count > 0)
        {
            var first = _editorMultiSelectedEvents.OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track).First();
            // Keep the note group multi-selected, but do not set _selectedEventKey:
            // SELECT's reference comes from the selected grid range's first tick,
            // not from whichever note happens to be inside it.
            _editorTrack = first.Track;
        }
        _statusMessage = $"Rectangle selected {_editorMultiSelectedEvents.Count} animation note(s) across {selectedTracks.Count} track(s); empty cells ignored.";
    }

    private void DrawEditorPanelsView(double currentTick, GearState currentState)
    {
        if (_chart is null || !_editorPanelsViewEnabled || _tempo is null)
            return;
        var width = _graphics.Viewport.Width;
        var height = _graphics.Viewport.Height;
        var unit = _projection.ScalePixels(1f);
        var area = GetEditorPanelsTrackAreaBounds(width, height);
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);
        _spriteBatch.Draw(_pixel, area, new Color(10, 10, 11, 54));

        for (var track = 17; track <= 20; track++)
        {
            var column = GetEditorPanelsTrackColumnBounds(track, width, height);
            // The panel tracks are a neutral black veil at 20% opacity; note and
            // animation colors remain responsible for the visual hierarchy.
            _spriteBatch.Draw(_pixel, column, new Color(0, 0, 0, 51));
            var border = new Color(154, 164, 184, 82);
            DrawEditorPanelLine(new Vector2(column.Left, column.Top), new Vector2(column.Left, column.Bottom), border, Math.Max(1f, unit));
            DrawEditorPanelLine(new Vector2(column.Right - 1, column.Top), new Vector2(column.Right - 1, column.Bottom), border, Math.Max(1f, unit));
        }

        // Reuse the exact gameplay snap-grid color and width rules, but draw the
        // resulting lines horizontally on this flat 2D overlay.
        if (_beatLines)
        {
            var beat = Math.Max(1.0, _tempo.TicksPerBeat);
            var divisor = _chartEditorMode ? Math.Clamp(_editorSnapDivisor, 2, 32) : 8;
            var subdivisionStep = Math.Max(1.0, beat / divisor);
            var pixelsPerTick = Math.Max(0.0001f,
                MathF.Abs(_projection.ScrollPixelsAtTicks(currentTick + 1.0, currentTick)) *
                GetEditorPanelsPixelsPerTravelUnit());
            var firstVisibleTick = Math.Max(0.0, currentTick - (area.Bottom - _projection.JudgeY) / pixelsPerTick);
            var lastVisibleTick = Math.Max(firstVisibleTick,
                currentTick + (_projection.JudgeY - area.Top) / pixelsPerTick);
            var gridTick = Math.Floor(firstVisibleTick / subdivisionStep) * subdivisionStep;
            var guard = 0;
            for (var tick = gridTick; tick <= lastVisibleTick + subdivisionStep && guard++ < 4096; tick += subdivisionStep)
            {
                if (tick < 0)
                    continue;
                var y = GetEditorPanelsTickY(tick, currentTick, currentState);
                if (!float.IsFinite(y) || y < area.Top || y >= area.Bottom)
                    continue;
                GetEditorBeatLineStyle(tick, subdivisionStep, divisor, out var color, out var lineWidth);
                DrawEditorPanelLine(new Vector2(area.Left, y), new Vector2(area.Right, y), color, lineWidth);
            }
        }

        RefreshEditorDiagnostics();
        var visibleRect = new Rectangle(area.X, area.Y, area.Width, area.Height);
        foreach (var ev in _chart.Events.Where(ev => ev.Kind == "Note" && ev.RawType == 1 &&
                     ev.Track is >= 17 and <= 20 && ev.Attr is >= 101 and <= 108)
                 .OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track).ThenBy(ev => ev.EventIndex))
        {
            var noteBounds = GetEditorPanelsNoteBounds(ev, currentTick, currentState, width, height);
            if (noteBounds.IsEmpty || !visibleRect.Intersects(noteBounds))
                continue;
            var selected = (_selectedEventKey.HasValue && KeyOf(ev) == _selectedEventKey.Value) || _editorMultiSelectedEvents.Contains(ev);
            DrawEditorPanelNoteVisual(ev, currentTick, currentState, width, height, selected, preview: false);
            if (_editorProblemEventKeys.Contains(KeyOf(ev)))
            {
                var problemHead = GetEditorPanelsNoteHeadBounds(ev, currentTick, currentState, width, height);
                // Warnings use a hollow frame that sits outside the animation head,
                // so the note itself stays visible and the warning is easy to spot.
                var markerPad = Math.Max(5, (int)MathF.Round(6.5f * unit));
                var markerRect = new Rectangle(problemHead.X - markerPad, problemHead.Y - markerPad,
                    problemHead.Width + markerPad * 2, problemHead.Height + markerPad * 2);
                var problemClip = Rectangle.Intersect(markerRect, visibleRect);
                if (problemClip.Width > 0 && problemClip.Height > 0)
                {
                    var warningYellow = new Color(255, 204, 48, 255);
                    DrawEditorPanelRectOutline(problemClip, warningYellow, Math.Max(2f, unit * 2.25f));
                }
            }
        }

        // Hollow note/body outlines preview the duration endpoint under the cursor.
        // Every selected animation note gets its own ghost endpoint at the same tick.
        if (_editorConnectorMode && _editorPanelsConnectorAwaitTarget && _editorPanelsConnectorPreviewValid)
        {
            foreach (var source in GetSelectedEditorDurationSelection()
                         .Where(ev => ev.Kind == "Note" && ev.RawType == 1 &&
                                      ev.Track is >= 17 and <= 20 && ev.Attr is >= 101 and <= 108 &&
                                      ev.Tick < _editorPanelsConnectorPreviewTick)
                         .OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track))
            {
                var durationLong = (long)_editorPanelsConnectorPreviewTick - source.Tick;
                if (durationLong is < 1 or > 65535)
                    continue;
                var duration = (int)durationLong;
                var preview = new ChartEvent
                {
                    Tick = source.Tick,
                    Track = source.Track,
                    Attr = source.Attr,
                    RawType = 1,
                    TypeIndex = source.TypeIndex,
                    Duration = duration,
                    NoteType = duration > 6 ? NoteType.SingleLong : NoteType.Single,
                    EventIndex = -1,
                    LinePair = -1,
                    Kind = "Note",
                    Instrument = source.Instrument,
                    Velocity = source.Velocity,
                    Pan = source.Pan
                };
                DrawEditorPanelNoteVisual(preview, currentTick, currentState, width, height,
                    selected: false, preview: true);
            }

            var endpointY = GetEditorPanelsTickY(_editorPanelsConnectorPreviewTick, currentTick, currentState);
            if (float.IsFinite(endpointY) && endpointY >= area.Top && endpointY < area.Bottom)
                DrawEditorPanelLine(new Vector2(area.Left, endpointY), new Vector2(area.Right, endpointY),
                    new Color(115, 195, 255, 190), Math.Max(1f, unit));
        }

        // Hollow outline follows the mouse at the exact snapped placement cell;
        // it previews the shape without showing a specific animation attribute.
        if (_editorPanelsHoverValid && _editorPlacementEnabled &&
            _editorPanelsSelectedAttr is >= 101 and <= 108 &&
            FindEditorPanelsNoteAtGridCell(_editorPanelsHoverTick, _editorPanelsHoverTrack) is null)
        {
            var duration = Math.Max(6, _editorDuration);
            var preview = new ChartEvent
            {
                Tick = _editorPanelsHoverTick,
                Track = _editorPanelsHoverTrack,
                Attr = _editorPanelsSelectedAttr,
                RawType = 1,
                TypeIndex = -1,
                Duration = duration,
                NoteType = duration > 6 ? NoteType.SingleLong : NoteType.Single,
                EventIndex = -1,
                LinePair = -1,
                Kind = "Note",
                Instrument = 0
            };
            DrawEditorPanelNoteVisual(preview, currentTick, currentState, width, height, selected: false, preview: true);
        }

        // This stays a simple fixed 2D red line even when the gameplay lanes are
        // rendered with perspective. It also makes the panel tracks' shared time
        // origin easy to read while notes approach the judge row.
        var judgeY = _projection.JudgeY;
        if (judgeY >= area.Top && judgeY < area.Bottom)
        {
            DrawEditorPanelLine(new Vector2(area.Left, judgeY), new Vector2(area.Right, judgeY), Color.White, Math.Max(7f, unit * 5f));
            DrawEditorPanelLine(new Vector2(area.Left, judgeY), new Vector2(area.Right, judgeY), new Color(255, 55, 65, 245), Math.Max(3f, unit * 2.4f));
        }

        // The committed SELECT reference is a tick line (not a filled blue box).
        // Panels use the same tick-to-Y projection as their beat/tempo guides.
        if (_editorMenuSection == EditorMenuSection.Select &&
            _editorSelectReferenceLineTick is int selectLineTick)
        {
            var lineTracks = GetEditorSelectLineTracks(panelsView: true);
            if (lineTracks.Length > 0)
            {
                var selectedColumns = lineTracks
                    .Select(track => GetEditorPanelsTrackColumnBounds(track, width, height))
                    .Where(column => !column.IsEmpty)
                    .ToArray();
                if (selectedColumns.Length > 0)
                {
                    var lineLeft = Math.Clamp(selectedColumns.Min(column => column.Left), area.Left, area.Right);
                    var lineRight = Math.Clamp(selectedColumns.Max(column => column.Right), area.Left, area.Right);
                    var lineY = GetEditorPanelsTickY(selectLineTick, currentTick, currentState);
                    if (lineRight > lineLeft && float.IsFinite(lineY) && lineY >= area.Top && lineY < area.Bottom)
                    {
                        DrawEditorPanelLine(new Vector2(lineLeft, lineY), new Vector2(lineRight, lineY),
                            new Color(0, 83, 101, 235), Math.Max(7f, unit * 7f));
                        DrawEditorPanelLine(new Vector2(lineLeft, lineY), new Vector2(lineRight, lineY),
                            new Color(0, 242, 255, 255), Math.Max(4.5f, unit * 4.5f));
                        DrawEditorPanelLine(new Vector2(lineLeft, lineY), new Vector2(lineRight, lineY),
                            new Color(235, 255, 255, 255), Math.Max(1.25f, unit * 1.25f));
                    }
                }
            }
        }

        // A click on a genuinely empty animation cell gets its own hollow cell
        // outline. It remains distinguishable from the SELECT reference line and
        // does not paint over the panel or any neighboring notes.
        if (_editorPanelsSelectedEmptyCell is { } emptyCell &&
            emptyCell.Track is >= 17 and <= 20 &&
            FindEditorPanelsNoteAtGridCell(emptyCell.Tick, emptyCell.Track) is null)
        {
            var column = GetEditorPanelsTrackColumnBounds(emptyCell.Track, width, height);
            var noteY = GetEditorPanelsTickY(emptyCell.Tick, currentTick, currentState);
            if (!column.IsEmpty && float.IsFinite(noteY))
            {
                // Match the exact footprint used for an animation-note head:
                // same column inset and fixed note-head height, independent of
                // the selected beat subdivision. The empty-cell highlight is white.
                var headHeight = Math.Max(28, (int)MathF.Round(32f * unit));
                var xInset = Math.Max(2, (int)MathF.Round(3f * unit));
                var cellRect = Rectangle.Intersect(
                    new Rectangle(column.X + xInset,
                        (int)MathF.Round(noteY - headHeight * 0.5f),
                        Math.Max(1, column.Width - xInset * 2), headHeight),
                    new Rectangle(area.X, area.Y, area.Width, area.Height));
                if (cellRect.Width > 0 && cellRect.Height > 0)
                {
                    DrawEditorPanelRectOutline(cellRect, Color.White, Math.Max(3f, unit * 3f));
                }
            }
        }

        // The orange in-progress freeform range remains a 3D quad. The committed
        // selector itself is now the projected cyan tick line above.
        var selectionVertexCount = 0;
        if (_editorPanelsMarqueeSelecting && _editorMenuSection != EditorMenuSection.Select)
        {
            var startX = Math.Clamp(_editorPanelsMarqueeStart.X, area.Left, area.Right);
            var endX = Math.Clamp(_editorPanelsMarqueeCurrent.X, area.Left, area.Right);
            var startY = Math.Clamp(GetEditorPanelsTickY(_editorPanelsMarqueeStartTick, currentTick, currentState), area.Top, area.Bottom);
            var endY = Math.Clamp(GetEditorPanelsTickY(_editorPanelsMarqueeCurrentTick, currentTick, currentState), area.Top, area.Bottom);
            if (MathF.Abs(endX - startX) < 2f)
            {
                var centerX = (startX + endX) * 0.5f;
                startX = Math.Clamp(centerX - 1f, area.Left, area.Right);
                endX = Math.Clamp(centerX + 1f, area.Left, area.Right);
            }
            if (MathF.Abs(endY - startY) < 2f)
            {
                var centerY = (startY + endY) * 0.5f;
                startY = Math.Clamp(centerY - 1f, area.Top, area.Bottom);
                endY = Math.Clamp(centerY + 1f, area.Top, area.Bottom);
            }
            if (float.IsFinite(startY) && float.IsFinite(endY))
            {
                var p0 = GetEditorPanelsSelectionPoint3D(startX, startY, width, height);
                var p1 = GetEditorPanelsSelectionPoint3D(endX, startY, width, height);
                var p2 = GetEditorPanelsSelectionPoint3D(endX, endY, width, height);
                var p3 = GetEditorPanelsSelectionPoint3D(startX, endY, width, height);
                selectionVertexCount = AppendEditorSelectionQuad3D(
                    selectionVertexCount, p0, p1, p2, p3,
                    new Color(255, 165, 0, 64), new Color(255, 165, 0));
            }
        }

        _spriteBatch.End();
        DrawEditorSelectionGeometry3D(selectionVertexCount);
    }

    private Vector3 GetEditorPanelsSelectionPoint3D(float screenX, float screenY, int width, int height)
    {
        const float depth = 1000f;
        var focalLength = Math.Max(1f, _projection.FocalLength);
        var cameraX = (screenX - width * 0.5f) * depth / focalLength;
        var cameraY = (height * 0.5f - screenY) * depth / focalLength;
        return new Vector3(cameraX, cameraY, -depth);
    }

    private void DrawEditorPanelNoteVisual(ChartEvent ev, double currentTick, GearState currentState,
        int virtualWidth, int virtualHeight, bool selected, bool preview)
    {
        var area = GetEditorPanelsTrackAreaBounds(virtualWidth, virtualHeight);
        var column = GetEditorPanelsTrackColumnBounds(ev.Track, virtualWidth, virtualHeight);
        var head = GetEditorPanelsNoteHeadBounds(ev, currentTick, currentState, virtualWidth, virtualHeight);
        if (column.IsEmpty || head.IsEmpty)
            return;
        var visibleRect = new Rectangle(area.X, area.Y, area.Width, area.Height);
        var headClip = Rectangle.Intersect(Rectangle.Intersect(head, column), visibleRect);
        var headVisible = headClip.Width > 0 && headClip.Height > 0;

        var unit = _projection.ScalePixels(1f);
        var hue = GetEditorPanelsAttributeColor(ev.Attr);
        var bodyX = column.Center.X;
        var bodyWidth = Math.Max(4, Math.Min((int)MathF.Round(column.Width * 0.28f), (int)MathF.Round(10f * unit)));
        if (ev.Duration > 6 || preview)
        {
            var endY = GetEditorPanelsTickY(ev.EndTick, currentTick, currentState);
            var minStub = Math.Max(3f, _projection.ScalePixels(3f));
            // Extend tiny-duration long events past the head by a short visible
            // stub; for longer events, the body endpoint stays at its true tick.
            var bodyEndY = Math.Min(endY, head.Top - minStub);
            var bodyTop = (int)MathF.Floor(bodyEndY);
            var bodyBottom = head.Top;
            if (bodyBottom > bodyTop)
            {
                var bodyRect = new Rectangle(bodyX - bodyWidth / 2, bodyTop, bodyWidth, bodyBottom - bodyTop);
                var clip = Rectangle.Intersect(Rectangle.Intersect(bodyRect, column), visibleRect);
                if (clip.Width > 0 && clip.Height > 0)
                {
                    var bodyEdge = preview
                        ? new Color(225, 231, 239, 220)
                        : selected
                            ? new Color(255, 245, 175, 205)
                            : new Color(hue.R, hue.G, hue.B, (byte)150);
                    if (!preview)
                        _spriteBatch.Draw(_pixel, clip, new Color(hue.R, hue.G, hue.B, selected ? (byte)165 : (byte)52));
                    var bodyEdgeWidth = selected ? Math.Max(2.25f, unit * 2.25f) : Math.Max(1f, unit);
                    DrawEditorPanelLine(new Vector2(clip.Left, clip.Top), new Vector2(clip.Left, clip.Bottom), bodyEdge, bodyEdgeWidth);
                    DrawEditorPanelLine(new Vector2(clip.Right - 1, clip.Top), new Vector2(clip.Right - 1, clip.Bottom), bodyEdge, bodyEdgeWidth);
                    if (preview)
                    {
                        DrawEditorPanelLine(new Vector2(clip.Left, clip.Top), new Vector2(clip.Right, clip.Top), bodyEdge, Math.Max(1f, unit));
                        DrawEditorPanelLine(new Vector2(clip.Left, clip.Bottom - 1), new Vector2(clip.Right, clip.Bottom - 1), bodyEdge, Math.Max(1f, unit));
                    }
                }
            }
        }

        if (headVisible)
        {
            if (selected)
            {
                var haloPad = Math.Max(3, (int)MathF.Round(3.5f * unit));
                var halo = Rectangle.Intersect(
                    new Rectangle(headClip.X - haloPad, headClip.Y - haloPad,
                        headClip.Width + haloPad * 2, headClip.Height + haloPad * 2),
                    Rectangle.Intersect(column, visibleRect));
                if (halo.Width > 0 && halo.Height > 0)
                {
                    _spriteBatch.Draw(_pixel, halo, new Color(255, 214, 10, 120));
                    DrawEditorPanelRectOutline(halo, new Color(255, 214, 10, 255), Math.Max(3f, unit * 3f));
                }
            }

            var outline = selected
                ? Color.White
                : preview
                    ? new Color(225, 231, 239, 230)
                    : new Color(hue.R, hue.G, hue.B, (byte)175);
            var thickness = selected ? Math.Max(2.5f, unit * 2.5f)
                : preview ? Math.Max(1.5f, unit * 1.5f) : Math.Max(1f, unit);
            if (!preview)
                _spriteBatch.Draw(_pixel, headClip, new Color(hue.R, hue.G, hue.B, selected ? (byte)190 : (byte)76));
            DrawEditorPanelRectOutline(headClip, outline, thickness);

            if (!preview)
            {
                var label = ev.Attr.ToString(CultureInfo.InvariantCulture);
                var measured = UnicodeTextRenderer.MeasureString(_font, label);
                var scale = Math.Min(1f, (headClip.Width - 4f * unit) / Math.Max(1f, measured.X));
                var textY = (int)MathF.Round(head.Center.Y - measured.Y * scale * 0.5f);
                textY = Math.Clamp(textY, headClip.Top, Math.Max(headClip.Top, headClip.Bottom - (int)MathF.Ceiling(measured.Y * scale)));
                var textX = (int)MathF.Round(headClip.Center.X - measured.X * scale * 0.5f);
                UnicodeTextRenderer.DrawString(_spriteBatch, _font, label, new Vector2(textX, textY),
                    new Color((byte)255, (byte)255, (byte)255, selected ? (byte)245 : (byte)185),
                    0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            }
        }

        var gripY = ev.Duration > 6 && !preview
            ? GetEditorPanelsDurationGripY(ev, currentTick, currentState, virtualWidth, virtualHeight)
            : float.NaN;
        if (selected && ev.Duration > 6 && float.IsFinite(gripY) && gripY >= area.Top && gripY < area.Bottom)
        {
            // The duration endpoint is half the note-head width and hollow. Orange
            // means the endpoint itself is selected; cyan means only the note head
            // is selected, matching the gameplay-lane endpoint colors.
            var endMarker = GetEditorPanelsDurationEndMarkerBounds(ev, gripY,
                currentTick, currentState, virtualWidth, virtualHeight);
            var endClip = Rectangle.Intersect(Rectangle.Intersect(endMarker, column), visibleRect);
            if (endClip.Width > 0 && endClip.Height > 0)
            {
                var endpointDirectlySelected =
                    ((_editorSelectedHoldEnd || _editorDragIsHoldEnd) &&
                     ((_selectedEventKey.HasValue && KeyOf(ev) == _selectedEventKey.Value) ||
                      ReferenceEquals(ev, _editorDraggedEvent)));
                var endpointColor = endpointDirectlySelected ? EditorSelectedColor : EditorEndingColor;
                DrawEditorPanelRectOutline(endClip, endpointColor, Math.Max(2.5f, unit * 2.5f));
            }
        }
        else if (float.IsFinite(gripY) && gripY >= area.Top && gripY < area.Bottom)
        {
            var gripWidth = Math.Max(14, (int)MathF.Round(20f * unit));
            var gripHeight = Math.Max(4, (int)MathF.Round(4f * unit));
            var grip = new Rectangle(column.Center.X - gripWidth / 2,
                (int)MathF.Round(gripY) - gripHeight / 2, gripWidth, gripHeight);
            var gripClip = Rectangle.Intersect(Rectangle.Intersect(grip, column), visibleRect);
            if (gripClip.Width > 0 && gripClip.Height > 0)
                _spriteBatch.Draw(_pixel, gripClip, new Color(255, 255, 255, 200));
        }
    }

    private void DrawEditorPanelRectOutline(Rectangle rect, Color color, float thickness)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;
        var left = rect.Left;
        var right = rect.Right - 1;
        var top = rect.Top;
        var bottom = rect.Bottom - 1;
        DrawEditorPanelLine(new Vector2(left, top), new Vector2(right, top), color, thickness);
        DrawEditorPanelLine(new Vector2(left, bottom), new Vector2(right, bottom), color, thickness);
        DrawEditorPanelLine(new Vector2(left, top), new Vector2(left, bottom), color, thickness);
        DrawEditorPanelLine(new Vector2(right, top), new Vector2(right, bottom), color, thickness);
    }

    private void DrawEditorPanelLine(Vector2 start, Vector2 end, Color color, float thickness)
    {
        var delta = end - start;
        var length = delta.Length();
        if (length <= 0.01f)
            return;
        _spriteBatch.Draw(_pixel, start, null, color, MathF.Atan2(delta.Y, delta.X), Vector2.Zero,
            new Vector2(length, thickness), SpriteEffects.None, 0f);
    }
}
