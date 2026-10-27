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
    // Lane geometry, bank visibility, beat/tempo lines, and lane caching.

    private void InvalidateLaneMeshCache()
    {
        _laneBankMeshCache.Clear();
        _laneMeshTemplates.Clear();
        _laneLineMeshTemplates.Clear();
        _laneMeshCacheSignature = 0;
    }

    private int ComputeLaneMeshCacheSignature(GearState state)
    {
        _ = state;
        var viewport = _graphics.Viewport;
        var hash = new HashCode();
        hash.Add(viewport.Width);
        hash.Add(viewport.Height);
        hash.Add(_compact);
        hash.Add(_chartEditorMode); // Editor mode expands the cached bank set when compact gameplay is enabled.
        hash.Add(_projection.FocalDistance);
        hash.Add(_projection.LaneJudgePositionX);
        hash.Add(_projection.LaneJudgePositionY);
        hash.Add(_projection.LaneStartGap);
        hash.Add(_projection.LaneJudgeGap);
        hash.Add(_projection.LaneBankWidth);
        hash.Add(_projection.LaneTrackWidth);
        hash.Add(_projection.LaneRotationDegrees);
        hash.Add(_projection.LaneRotationDepthDegrees);
        hash.Add(_projection.LaneTextureWidth);
        hash.Add(_projection.OuterTrackOpacity);
        hash.Add(_projection.LaneLineNearWidth);
        hash.Add(_projection.LaneLineFarWidth);
        hash.Add(_projection.LaneEndLength);
        hash.Add(_projection.OrbitRotationPivotVerticalOffset);
        hash.Add(_projection.OrbitRotationRadius);
        return hash.ToHashCode();
    }

    private float GetEditorBankOpacity(string side, string bank, GearState state)
    {
        // Bank-only passes draw their contents at full opacity. Inactive content
        // is faded once after its lane and notes have been composited together;
        // active content stays fully opaque while that split pass is in use.
        if (_editorInactiveBankLayerPass || _editorActiveBanksOnlyPass)
            return 1f;

        var activeBank = GearTimeline.ActiveBankForSide(state, side);
        if (string.Equals(activeBank, bank, StringComparison.OrdinalIgnoreCase))
            return 1f;

        // Collapsed mode overlays every bank in the same screen-space position.
        // Inactive banks must contribute no pixels at all; a faint overlay both
        // triples opacity and makes the hidden banks look like selectable lanes.
        if (_compact)
            return 0f;

        if (!_chartEditorMode)
            return 1f;

        return Math.Clamp(_editorRenderConfig.Editor.InactiveBankOpacity, 0f, 1f);
    }

    private bool ShouldRenderBankInCurrentEditorPass(string side, string bank, GearState state)
    {
        if (!_editorActiveBanksOnlyPass && !_editorInactiveBankLayerPass)
            return true;

        var activeBank = GearTimeline.ActiveBankForSide(state, side);
        var isActive = string.Equals(activeBank, bank, StringComparison.OrdinalIgnoreCase);
        return _editorActiveBanksOnlyPass ? isActive : !isActive;
    }

    private bool ShouldRenderTrackInCurrentEditorPass(int track, GearState state)
    {
        if (!_editorActiveBanksOnlyPass && !_editorInactiveBankLayerPass)
            return true;

        var position = GearState.Position(track);
        return position is null || ShouldRenderBankInCurrentEditorPass(
            position.Value.Side, position.Value.Bank, state);
    }

    private IEnumerable<(string Side, string Bank, bool Active)> VisibleBanks(GearState state)
    {
        foreach (var side in Sides)
        {
            var active = GearTimeline.ActiveBankForSide(state, side);
            foreach (var bank in Banks)
            {
                if (_compact && !_chartEditorMode && active != bank)
                    continue;
                yield return (side, bank, active == bank);
            }
        }
    }

    private (float Left, float Right) SideEdges(string side, GearState? state = null)
        => _projection.LaneSideEdgesNormalized(side);

    private (float Left, float Right) BankEdges(string side, string bank)
        => _projection.LaneBankEdgesNormalized(side, bank);

    private (float Left, float Right) FlashEdges(string side, string bank)
    {
        // Transition flashes cover exactly the currently active tracks: 1K = 1
        // track, 2K = 2 tracks, 3K = 3 tracks. Keeping this derived from
        // ActiveColumns() makes the flash geometry follow the same bank mapping as
        // the notes and judge.
        var columns = ActiveColumns(side, bank);
        if (columns.Count == 0)
            return BankEdges(side, bank);

        var (left, _) = ColumnEdges(side, bank, columns[0]);
        var (_, right) = ColumnEdges(side, bank, columns[^1]);
        return (left, right);
    }

    private (float Left, float Right) ColumnEdges(string side, string bank, int column)
        => _projection.LaneColumnEdgesNormalized(side, bank, column);

    private static bool TryGetTrackForBankColumn(string side, string bank, int column, out int track)
    {
        track = -1;
        switch ((side, bank, column))
        {
            case ("left", "Forward", 0): track = 0; return true;
            case ("left", "Forward", 1): track = 1; return true;
            case ("left", "Forward", 2): track = 2; return true;
            case ("right", "Forward", 0): track = 3; return true;
            case ("right", "Forward", 1): track = 4; return true;
            case ("right", "Forward", 2): track = 5; return true;
            case ("left", "Backward", 0): track = 6; return true;
            case ("left", "Backward", 1): track = 7; return true;
            case ("left", "Backward", 2): track = 8; return true;
            case ("right", "Backward", 0): track = 9; return true;
            case ("right", "Backward", 1): track = 10; return true;
            case ("right", "Backward", 2): track = 11; return true;
            case ("left", "Center", 1): track = 12; return true;
            case ("left", "Center", 2): track = 13; return true;
            case ("right", "Center", 0): track = 14; return true;
            case ("right", "Center", 1): track = 15; return true;
            default: return false;
        }
    }

    private float LaneColumnCenter(string side, string bank, int column)
    {
        var (left, right) = ColumnEdges(side, bank, column);
        return (left + right) * 0.5f;
    }

    private float LaneColumnHalfWidth(string side, string bank)
    {
        var (left, right) = ColumnEdges(side, bank, 0);
        return (right - left) * 0.5f;
    }

    private static readonly int[] ForwardHighlightColumns = { 0, 1, 2 };

    private static IReadOnlyList<int> ActiveColumns(string side, string bank)
    {
        return bank switch
        {
            "Forward" => ForwardColumns,
            "Center" when side == "left" => LeftCenterColumns,
            "Center" => RightCenterColumns,
            "Backward" => BackwardColumns,
            _ => Array.Empty<int>()
        };
    }

    private static IReadOnlyList<int> HighlightColumns(string side, string bank)
    {
        // Lane highlights represent the actual input tracks, not the subset used
        // by the compact note layout. A Forward/1K bank still has three physical
        // input tracks, so it gets the same three independent highlight states as
        // a 3K bank.
        return bank == "Forward"
            ? ForwardHighlightColumns
            : ActiveColumns(side, bank);
    }

    private bool IsEditorTrackVisible(int track, GearState state)
    {
        var position = GearState.Position(track);
        if (position is null)
            return false;
        var activeBank = GearTimeline.ActiveBankForSide(state, position.Value.Side);
        if (_compact)
            return string.Equals(activeBank, position.Value.Bank, StringComparison.OrdinalIgnoreCase);
        return !_chartEditorMode || GetEditorBankOpacity(position.Value.Side, position.Value.Bank, state) > 0.001f;
    }

    private bool ShouldRenderInCurrentBank(ChartEvent ev, GearState state)
    {
        return IsEditorTrackVisible(ev.Track, state);
    }

    private void DrawWarpedAcrossLine(
        string side,
        double tick,
        double currentTick,
        GearState state,
        string? bank,
        float width,
        Color color)
    {
        var travelPixels = _projection.ScrollPixelsAtTicks(tick, currentTick);
        var resolvedBank = bank ?? GearTimeline.ActiveBankForSide(state, side);
        var slice = _projection.ProjectBankSliceAtTravel(
            travelPixels, side, resolvedBank);
        DrawLine(_spriteBatch, slice.Inner, slice.Outer, width, color);
    }

    private static void GetEditorBeatLineStyle(double tick, double subdivisionStep, int divisor,
        out Color color, out float width)
    {
        var safeStep = Math.Max(1e-9, subdivisionStep);
        var subdivisionIndex = (int)Math.Round(tick / safeStep);
        var phase = ((subdivisionIndex % divisor) + divisor) % divisor;
        switch (phase)
        {
            case 0: // full beat
                color = new Color(255, 255, 255, 255);
                width = 2.0f;
                break;
            default:
                if (divisor is 3 or 6)
                {
                    // Every non-downbeat division selected by 1/3 or 1/6 snap
                    // is green, including the 3/6 (half-beat) line.
                    color = new Color(34, 197, 94, 255);
                    width = 1.2f;
                }
                else if (divisor % 2 == 0 && phase == divisor / 2)
                {
                    color = new Color(59, 130, 246, 255); // half beat
                    width = 1.6f;
                }
                else if (divisor % 4 == 0 && phase % (divisor / 4) == 0)
                {
                    color = new Color(168, 85, 247, 255); // quarter beat
                    width = 1.3f;
                }
                else
                {
                    color = new Color(148, 163, 184, 255); // minor subdivision: gray, matching the 1/8 grid
                    width = 1.0f;
                }
                break;
        }
    }

    private void DrawBeatLines(double currentTick, GearState state)
    {
        if (!_beatLines || _chart is null || _tempo is null)
            return;

        _spriteBatch.Begin(samplerState: SamplerState.PointClamp, blendState: BlendState.AlphaBlend);
        var beat = _tempo.TicksPerBeat;
        var first = Math.Max(0, (int)Math.Floor(currentTick / beat) - 2);
        var last = (int)Math.Ceiling(currentTick + 1000);
        var lastTick = _chart.PlayTick > 0
            ? _chart.PlayTick
            : (_chart.Events.Count == 0 ? 0 : _chart.Events.Max(e => e.EndTick));
        last = Math.Min(last, lastTick + (int)beat * 2);

        // In the editor, the selected snap controls both placement and the visible
        // grid. Outside the editor, preserve the full eighth-beat guide grid.
        var projectedEdges = _projectedBankLineEdges;
        var divisor = _chartEditorMode ? Math.Clamp(_editorSnapDivisor, 2, 32) : 8;
        var subdivisionStep = Math.Max(1.0, beat / divisor);
        for (var tick = first * beat; tick <= last; tick += subdivisionStep)
        {
            var depth = _projection.ScrollPixelsAtTicks(tick, currentTick);
            if (!_projection.WithinRenderDistance((float)depth))
                continue;

            GetEditorBeatLineStyle(tick, subdivisionStep, divisor, out var color, out var width);

            var travelPixels = (float)_projection.ScrollPixelsAtTicks(tick, currentTick);
            CollectVisibleBankLineEdges(projectedEdges, travelPixels, state);
            DrawConnectedBankLine(projectedEdges, width, color);
        }
        _spriteBatch.End();
    }

    private void DrawBeatNumbers(double currentTick, GearState state)
    {
        // Beat numbers follow the GRIDS toggle. Tick 0 is displayed as beat 1,
        // then one label is placed at each full-beat line.
        if (!_chartEditorMode || !_beatLines || _chart is null || _tempo is null || _tempo.TicksPerBeat <= 0)
            return;

        var beat = _tempo.TicksPerBeat;
        var firstBeat = Math.Max(0, (int)Math.Floor(currentTick / beat) - 3);
        var chartLastTick = _chart.PlayTick > 0
            ? _chart.PlayTick
            : (_chart.Events.Count == 0 ? 0 : _chart.Events.Max(e => e.EndTick));
        // Keep the label search window bounded like DrawBeatLines; long charts
        // must not make the editor walk every future beat on every frame.
        var lastTick = Math.Min((int)Math.Ceiling(currentTick + 1000.0),
            chartLastTick + (int)beat * 2);
        var lastBeat = (int)Math.Floor(lastTick / beat);
        var labelGap = _projection.ScalePixels(5f);
        var shadowOffset = _projection.ScalePixels(1f);
        var projectedEdges = _projectedBankLineEdges;

        _spriteBatch.Begin(samplerState: SamplerState.PointClamp, blendState: BlendState.AlphaBlend);
        for (var beatIndex = firstBeat; beatIndex <= lastBeat; beatIndex++)
        {
            var tick = beatIndex * beat;
            var travelPixels = (float)_projection.ScrollPixelsAtTicks(tick, currentTick);
            if (!_projection.WithinRenderDistance(travelPixels))
                continue;

            CollectVisibleBankLineEdges(projectedEdges, travelPixels, state);
            if (projectedEdges.Count < 2)
                continue;

            // The shared edge list is X-sorted. Its first point is the left-most
            // visible lane edge, so the beat number stays beside the gameplay area
            // as the perspective bends and the active banks change.
            var leftEdge = projectedEdges[0];
            var label = (beatIndex + 1).ToString(CultureInfo.InvariantCulture);
            var size = UnicodeTextRenderer.MeasureString(_font, label);
            var x = Math.Max(_projection.ScalePixels(2f), leftEdge.X - size.X - labelGap);
            var y = leftEdge.Y - size.Y * 0.5f;
            if (y + size.Y < 0f || y > _graphics.Viewport.Height)
                continue;

            UnicodeTextRenderer.DrawString(_spriteBatch, _font, label,
                new Vector2(x + shadowOffset, y + shadowOffset), new Color(0, 0, 0, 180));
            UnicodeTextRenderer.DrawString(_spriteBatch, _font, label, new Vector2(x, y), new Color(245, 248, 252, 235));
        }
        _spriteBatch.End();
    }

    private void DrawEditorBankSeparators()
    {
        if (!_chartEditorMode || _compact)
            return;

        var color = new Color(166, 180, 198, 205);
        const float lineWidth = 2.5f;
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);

        foreach (var side in Sides)
        {
            // Sort the three banks by their projected center at the judge plane.
            // Their order stays stable along the lane even when the lane curves.
            var bankA = Banks[0];
            var bankB = Banks[1];
            var bankC = Banks[2];
            var centerA = BankCenterAtTravel(0f, side, bankA);
            var centerB = BankCenterAtTravel(0f, side, bankB);
            var centerC = BankCenterAtTravel(0f, side, bankC);

            if (centerA > centerB) { (bankA, bankB) = (bankB, bankA); (centerA, centerB) = (centerB, centerA); }
            if (centerB > centerC) { (bankB, bankC) = (bankC, bankB); (centerB, centerC) = (centerC, centerB); }
            if (centerA > centerB) { (bankA, bankB) = (bankB, bankA); (centerA, centerB) = (centerB, centerA); }

            var travelLength = _projection.LaneTravelDistanceForNormalizedX(0f, side, bankA);
            DrawBankSeparator(side, bankA, bankB, travelLength, color, lineWidth);
            DrawBankSeparator(side, bankB, bankC, travelLength, color, lineWidth);
        }

        // An additional center seam separates the complete left and right halves.
        // It follows the projected inner edges across the same travel depth as the banks.
        DrawCenterBankSeparator(
            _projection.LaneTravelDistanceForNormalizedX(0f, "left", Banks[0]),
            color,
            lineWidth);

        _spriteBatch.End();

        float BankCenterAtTravel(float travel, string side, string bank)
        {
            var slice = _projection.ProjectBankSliceAtTravel(travel, side, bank);
            return (slice.Inner.X + slice.Outer.X) * 0.5f;
        }
    }


    private void DrawCenterBankSeparator(float travelLength, Color color, float width)
    {
        const int segments = 24;
        Vector2 previous = default;
        var hasPrevious = false;
        for (var i = 0; i <= segments; i++)
        {
            var travel = travelLength * (i / (float)segments);
            var leftEdge = default(Vector2);
            var rightEdge = default(Vector2);
            var maxLeftX = float.NegativeInfinity;
            var minRightX = float.PositiveInfinity;
            foreach (var bank in Banks)
            {
                var leftSlice = _projection.ProjectBankSliceAtTravel(travel, "left", bank);
                if (leftSlice.Inner.X > maxLeftX)
                {
                    maxLeftX = leftSlice.Inner.X;
                    leftEdge = leftSlice.Inner;
                }
                if (leftSlice.Outer.X > maxLeftX)
                {
                    maxLeftX = leftSlice.Outer.X;
                    leftEdge = leftSlice.Outer;
                }

                var rightSlice = _projection.ProjectBankSliceAtTravel(travel, "right", bank);
                if (rightSlice.Inner.X < minRightX)
                {
                    minRightX = rightSlice.Inner.X;
                    rightEdge = rightSlice.Inner;
                }
                if (rightSlice.Outer.X < minRightX)
                {
                    minRightX = rightSlice.Outer.X;
                    rightEdge = rightSlice.Outer;
                }
            }

            if (!float.IsFinite(maxLeftX) || !float.IsFinite(minRightX))
                continue;
            var seam = (leftEdge + rightEdge) * 0.5f;
            if (hasPrevious && Vector2.DistanceSquared(previous, seam) > 0.25f)
                DrawLine(_spriteBatch, previous, seam, width, color);
            previous = seam;
            hasPrevious = true;
        }
    }

    private void DrawBankSeparator(string side, string firstBank, string secondBank, float travelLength, Color color, float width)
    {
        const int segments = 24;
        Vector2 previous = default;
        var hasPrevious = false;
        for (var i = 0; i <= segments; i++)
        {
            var travel = travelLength * (i / (float)segments);
            var first = _projection.ProjectBankSliceAtTravel(travel, side, firstBank);
            var second = _projection.ProjectBankSliceAtTravel(travel, side, secondBank);
            var firstCenter = (first.Inner.X + first.Outer.X) * 0.5f;
            var secondCenter = (second.Inner.X + second.Outer.X) * 0.5f;
            var firstRight = first.Inner.X >= first.Outer.X ? first.Inner : first.Outer;
            var firstLeft = first.Inner.X <= first.Outer.X ? first.Inner : first.Outer;
            var secondRight = second.Inner.X >= second.Outer.X ? second.Inner : second.Outer;
            var secondLeft = second.Inner.X <= second.Outer.X ? second.Inner : second.Outer;
            var a = firstCenter <= secondCenter ? firstRight : firstLeft;
            var b = firstCenter <= secondCenter ? secondLeft : secondRight;
            var seam = (a + b) * 0.5f;

            if (hasPrevious && Vector2.DistanceSquared(previous, seam) > 0.25f)
                DrawLine(_spriteBatch, previous, seam, width, color);
            previous = seam;
            hasPrevious = true;
        }
    }

    private void CollectVisibleBankLineEdges(List<Vector2> projectedEdges, float travelPixels, GearState state)
    {
        projectedEdges.Clear();
        foreach (var side in Sides)
        {
            // Normal gameplay must keep using each side's active bank. The editor
            // can show all banks only when its expanded layout is visible. The old
            // shared helper accidentally projected all six banks during ordinary
            // play, multiplying tempo-line work (and beat-line work when enabled).
            if (_compact || !_chartEditorMode)
            {
                var bank = GearTimeline.ActiveBankForSide(state, side);
                AddBankLineEdges(projectedEdges, travelPixels, side, bank);
            }
            else
            {
                foreach (var bank in Banks)
                    AddBankLineEdges(projectedEdges, travelPixels, side, bank);
            }
        }
        projectedEdges.Sort(static (a, b) => a.X.CompareTo(b.X));
    }

    private void AddBankLineEdges(List<Vector2> projectedEdges, float travelPixels, string side, string bank)
    {
        var slice = _projection.ProjectBankSliceAtTravel(travelPixels, side, bank);
        if (Math.Max(slice.Inner.Y, slice.Outer.Y) < -40f ||
            Math.Min(slice.Inner.Y, slice.Outer.Y) > _graphics.Viewport.Height + 150f)
            return;
        projectedEdges.Add(slice.Inner);
        projectedEdges.Add(slice.Outer);
    }

    private void DrawConnectedBankLine(IReadOnlyList<Vector2> projectedEdges, float width, Color color)
    {
        if (projectedEdges.Count < 2)
            return;

        var previous = projectedEdges[0];
        for (var edgeIndex = 1; edgeIndex < projectedEdges.Count; edgeIndex++)
        {
            var next = projectedEdges[edgeIndex];
            if (Vector2.DistanceSquared(previous, next) > 0.25f)
                DrawLine(_spriteBatch, previous, next, width, color);
            previous = next;
        }
    }

    private void DrawTempoChanges(double currentTick, GearState state)
    {
        if (!_tempoLines || _tempo is null)
            return;

        var segments = _tempo.Segments;
        if (segments.Count == 0)
            return;

        var stutterGroups = _chartEditorMode ? GetEditorStutterGroups() : new List<ChartEvent>();
        var stutterBpmColor = new Color(196, 181, 253); // Light purple for tempo values inside a generator.
        var startGuideColor = new Color(255, 220, 0, 245);
        var endGuideColor = new Color(255, 72, 72, 245);
        _spriteBatch.Begin(samplerState: SamplerState.PointClamp, blendState: BlendState.AlphaBlend);
        var projectedEdges = _projectedBankLineEdges;
        // Segment zero is the synthetic base BPM. At tick zero an explicit chart
        // tempo is the next segment; draw that value without painting the base BPM
        // label over it.
        for (var segmentIndex = 1; segmentIndex < segments.Count; segmentIndex++)
        {
            var segment = segments[segmentIndex];
            var depth = _projection.ScrollPixelsAtTicks(segment.Tick, currentTick);
            if (!_projection.WithinRenderDistance((float)depth))
                continue;

            var travelPixels = (float)_projection.ScrollPixelsAtTicks(segment.Tick, currentTick);
            CollectVisibleBankLineEdges(projectedEdges, travelPixels, state);
            if (projectedEdges.Count < 2)
                continue;

            var isSelectedTempo = _chartEditorMode && _editorSelectedTempoEvent?.Tick == segment.Tick;
            var selectedGroup = _chartEditorMode ? _editorStutterEditingGroup : null;
            var isSelectedGroupMember = selectedGroup is not null &&
                segment.Tick >= selectedGroup.Tick && segment.Tick <= GetEditorStutterEndTick(selectedGroup);
            var initialTempoColor = new Color(250, 128, 114); // salmon red
            var startGroup = _chartEditorMode
                ? stutterGroups.FirstOrDefault(group => group.Tick == segment.Tick)
                : null;
            var endingGroup = _chartEditorMode
                ? stutterGroups.FirstOrDefault(group => GetEditorStutterEndTick(group) == segment.Tick)
                : null;
            var memberGroup = _chartEditorMode
                ? stutterGroups.FirstOrDefault(group => segment.Tick >= group.Tick && segment.Tick <= GetEditorStutterEndTick(group))
                : null;

            // Generator starts are yellow and unmodified ends are red. A shared or
            // overridden endpoint is pink because the active tempo differs from the
            // generated ending value saved in the type-30 backup.
            var hasDisabledGroupEnding = endingGroup is not null &&
                FindEditorDisabledTempoEndingEvent(endingGroup) is not null;
            var sharedEndpointColor = new Color(255, 105, 180); // pink: an active tempo shares a group-ending tick
            var selectedTempoColor = new Color(34, 211, 238); // cyan: selected BPM line
            var markerColor = isSelectedTempo || isSelectedGroupMember ? selectedTempoColor
                : hasDisabledGroupEnding ? sharedEndpointColor
                : startGroup is not null ? startGuideColor
                : endingGroup is not null ? endGuideColor
                : segment.Tick == 0 ? initialTempoColor : Hold;
            var boundaryMarker = startGroup is not null || endingGroup is not null;
            DrawConnectedBankLine(projectedEdges,
                boundaryMarker || isSelectedGroupMember ? 4f : isSelectedTempo || segment.Tick == 0 ? 3f : 2f,
                markerColor);

            if (!_chartEditorMode)
                continue;

            var label = FontSafeText(_font, $"{segment.Bpm.ToString("0.##", CultureInfo.InvariantCulture)} BPM");
            var labelSize = UnicodeTextRenderer.MeasureString(_font, label);
            var leftEndpoint = projectedEdges[0];
            var labelPosition = new Vector2(leftEndpoint.X + 2f, leftEndpoint.Y - labelSize.Y - 2f);
            var bpmLabelColor = isSelectedTempo || isSelectedGroupMember ? selectedTempoColor
                : memberGroup is not null ? stutterBpmColor
                : segment.Tick == 0 ? initialTempoColor : Text;

            // Show the BPM on every tempo changer, including intermediate ramp
            // samples, so each line can be read without opening the generator.
            UnicodeTextRenderer.DrawString(_spriteBatch, _font, label, labelPosition, bpmLabelColor);

            // Keep the group name on the same row as the BPM value, to its right
            // rather than stacking it above the value.
            var groupNameX = labelPosition.X + labelSize.X + _projection.ScalePixels(6f);
            if (startGroup is not null)
            {
                var startLabel = FontSafeText(_font, GetEditorStutterStartLabel(startGroup));
                var startPosition = new Vector2(groupNameX, labelPosition.Y);
                UnicodeTextRenderer.DrawString(_spriteBatch, _font, startLabel,
                    new Vector2(startPosition.X + 1f, startPosition.Y + 1f), new Color(0, 0, 0, 180));
                UnicodeTextRenderer.DrawString(_spriteBatch, _font, startLabel, startPosition, Text);
                groupNameX += UnicodeTextRenderer.MeasureString(_font, startLabel).X + _projection.ScalePixels(8f);
            }

            if (endingGroup is not null)
            {
                var endLabel = FontSafeText(_font, endingGroup.Attr == 3 ? "RAMP END" : "STUTTER END");
                var endPosition = new Vector2(groupNameX, labelPosition.Y);
                UnicodeTextRenderer.DrawString(_spriteBatch, _font, endLabel,
                    new Vector2(endPosition.X + 1f, endPosition.Y + 1f), new Color(0, 0, 0, 180));
                UnicodeTextRenderer.DrawString(_spriteBatch, _font, endLabel, endPosition, Text);
            }
        }

        // Selecting the BPM tool enables a full bank-spanning preview line under
        // the mouse, before or after the user edits the BPM value. The preview uses
        // the same snapped tick and BPM value as placement.
        if (_chartEditorMode && _editorMenuSection == EditorMenuSection.Bpm && _editorMouseHoverActive &&
            _hoverBeatTick is int previewTick && TryParseEditorBpmInput(out var previewBpm))
        {
            var previewTravel = (float)_projection.ScrollPixelsAtTicks(previewTick, currentTick);
            if (_projection.WithinRenderDistance(previewTravel))
            {
                CollectVisibleBankLineEdges(_projectedBankLineEdges, previewTravel, state);
                if (_projectedBankLineEdges.Count >= 2)
                {
                    var previewColor = new Color(255, 175, 64, 190);
                    DrawConnectedBankLine(_projectedBankLineEdges, 3.5f, previewColor);
                    var label = FontSafeText(_font, $"{previewBpm.ToString("0.##", CultureInfo.InvariantCulture)} BPM");
                    var labelSize = UnicodeTextRenderer.MeasureString(_font, label);
                    var leftEndpoint = _projectedBankLineEdges[0];
                    var labelPosition = new Vector2(leftEndpoint.X + 3f, leftEndpoint.Y - labelSize.Y - 3f);
                    UnicodeTextRenderer.DrawString(_spriteBatch, _font, label,
                        new Vector2(labelPosition.X + 1f, labelPosition.Y + 1f), new Color(0, 0, 0, 190));
                    UnicodeTextRenderer.DrawString(_spriteBatch, _font, label, labelPosition, previewColor);
                }
            }
        }

        // Keep the picked boundaries and mouse preview visible above the generated
        // tempo lines. The hover guide is only shown while choosing that boundary.
        if (_chartEditorMode && _editorMenuSection == EditorMenuSection.Stutter)
        {
            var selectedGroupBoundaryColor = _editorStutterEditingGroup is not null
                ? new Color(34, 211, 238, 245)
                : startGuideColor;
            var selectedGroupEndColor = _editorStutterEditingGroup is not null
                ? new Color(34, 211, 238, 245)
                : endGuideColor;
            if (_editorStutterStartTick >= 0)
                DrawEditorTempoBoundaryGuide(_editorStutterStartTick, currentTick, state, selectedGroupBoundaryColor, 4f);
            if (_editorStutterEndTick > _editorStutterStartTick && _editorStutterStartTick >= 0)
                DrawEditorTempoBoundaryGuide(_editorStutterEndTick, currentTick, state, selectedGroupEndColor, 4f);

            if (_editorMouseHoverActive && _hoverBeatTick.HasValue && _editorStutterStartPlacementPending)
                DrawEditorTempoBoundaryGuide(_hoverBeatTick.Value, currentTick, state,
                    new Color(255, 220, 0, 150), 2f);
            else if (_editorMouseHoverActive && _hoverBeatTick.HasValue && _editorStutterEndPlacementPending)
                DrawEditorTempoBoundaryGuide(_hoverBeatTick.Value, currentTick, state,
                    new Color(255, 72, 72, 150), 2f);
        }
        _spriteBatch.End();
    }

    private void DrawEditorTempoBoundaryGuide(int tick, double currentTick, GearState state, Color color, float width)
    {
        if (tick < 0)
            return;
        var travelPixels = (float)_projection.ScrollPixelsAtTicks(tick, currentTick);
        if (!_projection.WithinRenderDistance(travelPixels))
            return;

        CollectVisibleBankLineEdges(_projectedBankLineEdges, travelPixels, state);
        if (_projectedBankLineEdges.Count >= 2)
            DrawConnectedBankLine(_projectedBankLineEdges, width, color);
    }

    /// <summary>
    /// Draw the native track-16 ending marker. It mirrors PlayTick rather than
    /// being an independently editable note, so the guide line follows the
    /// current play-time-derived tick automatically.
    /// </summary>
    private void DrawPlayTickMarker(double currentTick, GearState state)
    {
        if (!_chartEditorMode || _chart is null || _chart.PlayTick < 0)
            return;

        var travelPixels = (float)_projection.ScrollPixelsAtTicks(_chart.PlayTick, currentTick);
        if (!_projection.WithinRenderDistance(travelPixels))
            return;

        var projectedEdges = _projectedBankLineEdges;
        CollectVisibleBankLineEdges(projectedEdges, travelPixels, state);
        if (projectedEdges.Count < 2)
            return;

        // Match the initial-tempo marker's salmon color and 3 px line weight.
        var markerColor = new Color(250, 128, 114);
        _spriteBatch.Begin(samplerState: SamplerState.PointClamp, blendState: BlendState.AlphaBlend);
        DrawConnectedBankLine(projectedEdges, 3f, markerColor);

        var label = FontSafeText(_font, "PLAY END");
        var labelSize = UnicodeTextRenderer.MeasureString(_font, label);
        var leftEndpoint = projectedEdges[0];
        var labelY = _chart.PlayTick == 0
            ? leftEndpoint.Y + 2f
            : leftEndpoint.Y - labelSize.Y - 2f;
        UnicodeTextRenderer.DrawString(_spriteBatch, _font, label,
            new Vector2(leftEndpoint.X + 2f, labelY), markerColor);
        _spriteBatch.End();
    }
}
