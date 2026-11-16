using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using TSC.Ares;
using TSC.Input;

namespace TSC.Rendering;

public sealed partial class Playfield
{
    // The same projected regions are used by Android touch hit-testing and by
    // the desktop debug overlay. Keeping this geometry outside the Android-only
    // input file ensures Windows can inspect the exact touch targets too.
    private readonly List<GameplayTouchHitArea> _gameplayTouchHitAreas = new(16);

#if DEBUG
    private bool _showGameplayTouchHitAreasDebug;

    private static readonly Color[] GameplayTouchHitAreaDebugColors =
    {
        new(255,  82,  82), new(255, 167,  38), new(255, 213,  79), new(156, 204, 101),
        new( 38, 166, 154), new( 41, 182, 246), new( 66, 165, 245), new(126,  87, 194),
        new(171,  71, 188), new(236,  64, 122), new(239, 108,   0), new(124, 179,  66),
        new(  0, 172, 193), new( 92, 107, 192), new(213,   0,   0), new(109,  76,  65)
    };
#endif

    private readonly record struct GameplayTouchHitArea(
        string Action,
        int Track,
        string Side,
        string Bank,
        Vector2 HitVertex0,
        Vector2 HitVertex1,
        Vector2 HitVertex2,
        Vector2 HitVertex3,
        Vector2 HitVertex4,
        int HitVertexCount,
        Vector2 LabelCenter,
        Vector2 VisualNearCenter,
        Vector2 VisualFarCenter,
        Vector2 VisualNearAcross,
        Vector2 VisualFarAcross,
        float VisualNearHalfWidth,
        float VisualFarHalfWidth);

    private void BuildGameplayTouchHitAreas(GearState gear)
    {
        _gameplayTouchHitAreas.Clear();
        var behindJudgeTravel = Math.Max(0f, _projection.LaneEndLength) * _projection.ResolutionScale;
        if (!float.IsFinite(behindJudgeTravel))
            return;

        for (var track = 0; track < 16; track++)
        {
            if (!gear.IsActive(track))
                continue;

            var position = GearState.Position(track);
            if (position is null)
                continue;

            var (side, bank, localPosition) = position.Value;
            var column = bank == "Center" && side == "left"
                ? localPosition + 1
                : localPosition;
            var (columnLeft, columnRight) = _projection.LaneColumnEdgesNormalized(side, bank, column);
            var centerX = (columnLeft + columnRight) * 0.5f;
            var visualWidth = columnRight - columnLeft;
            if (!float.IsFinite(centerX) || !float.IsFinite(visualWidth) || visualWidth <= 0.0001f)
                continue;

            // Define the touch map as seven non-overlapping cells per bank. The
            // cells are based on the bank's actual three-column pitch, not on a
            // centered scale around each track. Two extra cells are added outside
            // each bank edge, so the hit regions physically extend beyond the lanes.
            var (bankLeft, bankRight) = _projection.LaneBankEdgesNormalized(side, bank);
            var pitch = (bankRight - bankLeft) / 3f;
            if (!float.IsFinite(pitch) || pitch <= 0.0001f)
                continue;

            int startCell;
            int endCell;
            if (bank == "Center")
            {
                // 2K left:  L2 x4 | L3 x3
                // 2K right: R1 x3 | R2 x4
                // The divider follows the actual two-track split in the center bank.
                var dividerCell = side == "left" ? 2 : 1;
                startCell = localPosition == 0 ? -2 : dividerCell;
                endCell = localPosition == 0 ? dividerCell : 5;
            }
            else
            {
                // 1K/3K: outer columns get three cells, the middle column gets one.
                (startCell, endCell) = localPosition switch
                {
                    0 => (-2, 1),
                    1 => (1, 2),
                    2 => (2, 5),
                    _ => (0, 0)
                };
            }

            if (endCell <= startCell)
                continue;

            var touchLeft = bankLeft + startCell * pitch;
            var touchRight = bankLeft + endCell * pitch;
            var touchCenterX = (touchLeft + touchRight) * 0.5f;
            if (!float.IsFinite(touchLeft) || !float.IsFinite(touchRight) || touchRight <= touchLeft)
                continue;

            // The configured value resolves from behindCameraDistance in the
            // render config (with endLength retained as its compatibility fallback).
            var aheadJudgeTravel = _projection.LaneTravelDistanceForNormalizedX(touchCenterX, side, bank);
            if (!float.IsFinite(behindJudgeTravel) ||
                !float.IsFinite(aheadJudgeTravel) || aheadJudgeTravel <= 0.0001f)
                continue;

            // Use the extended projection for hit-area edges. ProjectSideTravel()
            // clamps X to the bank, which silently folds outside expansions back
            // onto the lane edge and creates misleading overlapping hit regions.
            var nearLeft = _projection.ProjectSideTravelExtended(touchLeft, -behindJudgeTravel, side, bank).Position;
            var nearRight = _projection.ProjectSideTravelExtended(touchRight, -behindJudgeTravel, side, bank).Position;
            var farLeft = _projection.ProjectSideTravelExtended(touchLeft, aheadJudgeTravel, side, bank).Position;
            var farRight = _projection.ProjectSideTravelExtended(touchRight, aheadJudgeTravel, side, bank).Position;
            var visualNearLeft = _projection.ProjectSideTravel(columnLeft, -behindJudgeTravel, side, bank).Position;
            var visualNearRight = _projection.ProjectSideTravel(columnRight, -behindJudgeTravel, side, bank).Position;
            var visualFarLeft = _projection.ProjectSideTravel(columnLeft, aheadJudgeTravel, side, bank).Position;
            var visualFarRight = _projection.ProjectSideTravel(columnRight, aheadJudgeTravel, side, bank).Position;
            var visualNearCenter = _projection.ProjectSideTravel(centerX, -behindJudgeTravel, side, bank).Position;
            var visualFarCenter = _projection.ProjectSideTravel(centerX, aheadJudgeTravel, side, bank).Position;

            if (!IsFinite(nearLeft) || !IsFinite(nearRight) ||
                !IsFinite(farLeft) || !IsFinite(farRight) ||
                !IsFinite(visualNearLeft) || !IsFinite(visualNearRight) ||
                !IsFinite(visualFarLeft) || !IsFinite(visualFarRight) ||
                !IsFinite(visualNearCenter) || !IsFinite(visualFarCenter))
                continue;

            var action = GetGameplayTouchActionForTrack(track);
            if (string.IsNullOrEmpty(action))
                continue;

            var nearCenter = (nearLeft + nearRight) * 0.5f;
            var farCenter = (farLeft + farRight) * 0.5f;
            var visualNearHalfWidth = Vector2.Distance(visualNearLeft, visualNearRight) * 0.5f;
            var visualFarHalfWidth = Vector2.Distance(visualFarLeft, visualFarRight) * 0.5f;
            _gameplayTouchHitAreas.Add(new GameplayTouchHitArea(
                action,
                track,
                side,
                bank,
                nearLeft,
                nearRight,
                farRight,
                farLeft,
                Vector2.Zero,
                4,
                (nearCenter + farCenter) * 0.5f,
                visualNearCenter,
                visualFarCenter,
                visualNearRight - visualNearLeft,
                visualFarRight - visualFarLeft,
                visualNearHalfWidth,
                visualFarHalfWidth));
        }

        // The left and right inner tracks expand toward the same center space.
        // Treat those regions like two compressible blocks: they may expand freely
        // while separated, but once their expansions meet they share one boundary
        // and are clipped there instead of drawing or accepting overlapping input.
        ConstrainInnerTouchAreasAtCenter();
    }

    private void ConstrainInnerTouchAreasAtCenter()
    {
        var leftIndex = _gameplayTouchHitAreas.FindIndex(area =>
            area.Side == "left" && string.Equals(area.Action, "game_left_3", StringComparison.OrdinalIgnoreCase));
        var rightIndex = _gameplayTouchHitAreas.FindIndex(area =>
            area.Side == "right" && string.Equals(area.Action, "game_right_1", StringComparison.OrdinalIgnoreCase));
        if (leftIndex < 0 || rightIndex < 0)
            return;

        var leftArea = _gameplayTouchHitAreas[leftIndex];
        var rightArea = _gameplayTouchHitAreas[rightIndex];
        if (!GameplayTouchHitAreasOverlap(leftArea, rightArea))
            return;

        // Only condense the pair when their requested expanded shapes actually
        // collide. The shared edge is halfway between their two expanded inner
        // boundaries, so each side gives up half of the overlap instead of both
        // being clipped all the way back to the original visible bank edges.
        // Vertex order is near-left, near-right, far-right, far-left: L3 faces
        // inward through vertex 1/2, while R1 faces inward through vertex 0/3.
        var dividerNear = (leftArea.HitVertex1 + rightArea.HitVertex0) * 0.5f;
        var dividerFar = (leftArea.HitVertex2 + rightArea.HitVertex3) * 0.5f;
        if (!IsFinite(dividerNear) || !IsFinite(dividerFar) ||
            Vector2.DistanceSquared(dividerNear, dividerFar) < 0.0001f)
            return;

        _gameplayTouchHitAreas[leftIndex] = ClipGameplayTouchHitAreaToCenterDivider(
            leftArea, dividerNear, dividerFar);
        _gameplayTouchHitAreas[rightIndex] = ClipGameplayTouchHitAreaToCenterDivider(
            rightArea, dividerNear, dividerFar);
    }

    private static bool GameplayTouchHitAreasOverlap(
        GameplayTouchHitArea first,
        GameplayTouchHitArea second)
        => !HasTouchHitAreaSeparatingAxis(first, second) &&
           !HasTouchHitAreaSeparatingAxis(second, first);

    private static bool HasTouchHitAreaSeparatingAxis(
        GameplayTouchHitArea axisSource,
        GameplayTouchHitArea other)
    {
        Span<Vector2> sourcePoints = stackalloc Vector2[5]
        {
            axisSource.HitVertex0, axisSource.HitVertex1, axisSource.HitVertex2,
            axisSource.HitVertex3, axisSource.HitVertex4
        };
        Span<Vector2> otherPoints = stackalloc Vector2[5]
        {
            other.HitVertex0, other.HitVertex1, other.HitVertex2,
            other.HitVertex3, other.HitVertex4
        };

        for (var edgeIndex = 0; edgeIndex < axisSource.HitVertexCount; edgeIndex++)
        {
            var edge = sourcePoints[(edgeIndex + 1) % axisSource.HitVertexCount] - sourcePoints[edgeIndex];
            var axis = new Vector2(-edge.Y, edge.X);
            if (axis.LengthSquared() < 0.000001f)
                continue;

            ProjectTouchHitArea(axisSource, sourcePoints, axis, out var sourceMin, out var sourceMax);
            ProjectTouchHitArea(other, otherPoints, axis, out var otherMin, out var otherMax);
            // Treat a shared edge (or a sub-hundredth-pixel sliver) as touching,
            // not as a collision that needs further compression.
            if (sourceMax <= otherMin + 0.01f || otherMax <= sourceMin + 0.01f)
                return true;
        }

        return false;
    }

    private static void ProjectTouchHitArea(
        GameplayTouchHitArea area,
        Span<Vector2> points,
        Vector2 axis,
        out float minimum,
        out float maximum)
    {
        minimum = float.PositiveInfinity;
        maximum = float.NegativeInfinity;
        for (var index = 0; index < area.HitVertexCount; index++)
        {
            var projection = Vector2.Dot(points[index], axis);
            minimum = Math.Min(minimum, projection);
            maximum = Math.Max(maximum, projection);
        }
    }

    private static GameplayTouchHitArea ClipGameplayTouchHitAreaToCenterDivider(
        GameplayTouchHitArea area,
        Vector2 dividerNear,
        Vector2 dividerFar)
    {
        Span<Vector2> input = stackalloc Vector2[5]
        {
            area.HitVertex0,
            area.HitVertex1,
            area.HitVertex2,
            area.HitVertex3,
            area.HitVertex4
        };
        Span<Vector2> output = stackalloc Vector2[5];
        var inputCount = Math.Clamp(area.HitVertexCount, 0, input.Length);
        if (inputCount < 3)
            return area;

        // Keep the half-plane containing the original visual lane center. This
        // makes the rule work for both banks without assuming screen-space winding.
        var reference = (area.VisualNearCenter + area.VisualFarCenter) * 0.5f;
        var referenceSide = Cross(dividerFar - dividerNear, reference - dividerNear);
        if (Math.Abs(referenceSide) < 0.0001f)
            return area;
        var keepSign = Math.Sign(referenceSide);

        var outputCount = 0;
        for (var index = 0; index < inputCount; index++)
        {
            var current = input[index];
            var next = input[(index + 1) % inputCount];
            var currentDistance = Cross(dividerFar - dividerNear, current - dividerNear) * keepSign;
            var nextDistance = Cross(dividerFar - dividerNear, next - dividerNear) * keepSign;
            var currentInside = currentDistance >= -0.001f;
            var nextInside = nextDistance >= -0.001f;

            if (currentInside && nextInside)
            {
                AppendUniquePoint(output, ref outputCount, next);
            }
            else if (currentInside && !nextInside)
            {
                var denominator = currentDistance - nextDistance;
                if (Math.Abs(denominator) > 0.000001f)
                {
                    var t = Math.Clamp(currentDistance / denominator, 0f, 1f);
                    AppendUniquePoint(output, ref outputCount, Vector2.Lerp(current, next, t));
                }
            }
            else if (!currentInside && nextInside)
            {
                var denominator = currentDistance - nextDistance;
                if (Math.Abs(denominator) > 0.000001f)
                {
                    var t = Math.Clamp(currentDistance / denominator, 0f, 1f);
                    AppendUniquePoint(output, ref outputCount, Vector2.Lerp(current, next, t));
                }
                AppendUniquePoint(output, ref outputCount, next);
            }
        }

        if (outputCount > 1 && Vector2.DistanceSquared(output[0], output[outputCount - 1]) < 0.0001f)
            outputCount--;
        if (outputCount < 3)
            return area;

        var v0 = output[0];
        var v1 = outputCount > 1 ? output[1] : Vector2.Zero;
        var v2 = outputCount > 2 ? output[2] : Vector2.Zero;
        var v3 = outputCount > 3 ? output[3] : Vector2.Zero;
        var v4 = outputCount > 4 ? output[4] : Vector2.Zero;
        var center = Vector2.Zero;
        for (var index = 0; index < outputCount; index++)
            center += output[index];
        center /= outputCount;

        return area with
        {
            HitVertex0 = v0,
            HitVertex1 = v1,
            HitVertex2 = v2,
            HitVertex3 = v3,
            HitVertex4 = v4,
            HitVertexCount = outputCount,
            LabelCenter = center
        };
    }

    private static void AppendUniquePoint(Span<Vector2> points, ref int count, Vector2 point)
    {
        if (count > 0 && Vector2.DistanceSquared(points[count - 1], point) < 0.0001f)
            return;
        if (count < points.Length)
            points[count++] = point;
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    private static string? GetGameplayTouchActionForTrack(int track)
    {
        // GetActionForTrack already maps center-bank tracks to the four distinct
        // 2K controls: L2/L3 on the left and R1/R2 on the right.
        return KeybindSettings.GetActionForTrack(track);
    }

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static void AddGameplayTouchHitAreaFill(
        List<VertexPositionColor> vertices,
        GameplayTouchHitArea area,
        Color color)
    {
        Span<Vector2> points = stackalloc Vector2[5]
        {
            area.HitVertex0, area.HitVertex1, area.HitVertex2, area.HitVertex3, area.HitVertex4
        };
        for (var index = 1; index < area.HitVertexCount - 1; index++)
        {
            vertices.Add(new VertexPositionColor(new Vector3(points[0], 0f), color));
            vertices.Add(new VertexPositionColor(new Vector3(points[index], 0f), color));
            vertices.Add(new VertexPositionColor(new Vector3(points[index + 1], 0f), color));
        }
    }

    private void DrawGameplayTouchHitAreaOutline(
        SpriteBatch spriteBatch,
        GameplayTouchHitArea area,
        Color color)
    {
        Span<Vector2> points = stackalloc Vector2[5]
        {
            area.HitVertex0, area.HitVertex1, area.HitVertex2, area.HitVertex3, area.HitVertex4
        };
        for (var index = 0; index < area.HitVertexCount; index++)
            DrawLine(spriteBatch, points[index], points[(index + 1) % area.HitVertexCount], 3f, color);
    }

#if DEBUG
    private static Color GameplayTouchHitAreaColor(int track)
        => GameplayTouchHitAreaDebugColors[Math.Clamp(track, 0, GameplayTouchHitAreaDebugColors.Length - 1)];

    private static string GameplayTouchActionLabel(string action)
    {
        if (action.StartsWith("game_left_", StringComparison.OrdinalIgnoreCase))
            return $"L{action[^1]}";
        if (action.StartsWith("game_right_", StringComparison.OrdinalIgnoreCase))
            return $"R{action[^1]}";
        return action;
    }

    private void DrawGameplayTouchHitAreasDebugOverlay()
    {
        var fillVertices = new List<VertexPositionColor>(_gameplayTouchHitAreas.Count * 6);
        foreach (var area in _gameplayTouchHitAreas)
        {
            var baseColor = GameplayTouchHitAreaColor(area.Track);
            var fillColor = new Color(baseColor.R, baseColor.G, baseColor.B, (byte)66);
            AddGameplayTouchHitAreaFill(fillVertices, area, fillColor);
        }
        DrawVertices(fillVertices, PrimitiveType.TriangleList);

        const int left = 12;
        const int top = 12;
        const int lineHeight = 19;
        const int panelWidth = 360;
        var rows = Math.Max(1, _gameplayTouchHitAreas.Count);
        var panelHeight = 33 + rows * lineHeight;

        _spriteBatch.Begin(samplerState: SamplerState.PointClamp, blendState: BlendState.AlphaBlend);
        foreach (var area in _gameplayTouchHitAreas)
        {
            var color = GameplayTouchHitAreaColor(area.Track);
            DrawGameplayTouchHitAreaOutline(_spriteBatch, area, color);

            var label = $"T{area.Track:00}";
            var labelSize = UnicodeTextRenderer.MeasureString(_font, label);
            var labelPosition = area.LabelCenter - labelSize * 0.5f;
            UnicodeTextRenderer.DrawString(_spriteBatch, _font, label, labelPosition + Vector2.One, Color.Black);
            UnicodeTextRenderer.DrawString(_spriteBatch, _font, label, labelPosition, color);
        }

        _spriteBatch.Draw(_pixel, new Rectangle(left, top, panelWidth, panelHeight), new Color(0, 0, 0, 218));
        UnicodeTextRenderer.DrawString(
            _spriteBatch,
            _font,
            "TOUCH HIT AREAS  (Ctrl+F12 to hide)",
            new Vector2(left + 8, top + 6),
            Color.White);

        if (_gameplayTouchHitAreas.Count == 0)
        {
            UnicodeTextRenderer.DrawString(
                _spriteBatch, _font, "No active lanes", new Vector2(left + 8, top + 24), Color.LightGray);
        }
        else
        {
            for (var index = 0; index < _gameplayTouchHitAreas.Count; index++)
            {
                var area = _gameplayTouchHitAreas[index];
                var color = GameplayTouchHitAreaColor(area.Track);
                var rowY = top + 28 + index * lineHeight;
                _spriteBatch.Draw(_pixel, new Rectangle(left + 8, rowY + 3, 12, 12), color);
                var sideLabel = area.Side == "left" ? "LEFT" : "RIGHT";
                var bankLabel = area.Bank.ToUpperInvariant();
                var text = $"T{area.Track:00}   {GameplayTouchActionLabel(area.Action),-2}   {sideLabel}/{bankLabel}";
                UnicodeTextRenderer.DrawString(_spriteBatch, _font, text, new Vector2(left + 28, rowY), Color.White);
            }
        }
        _spriteBatch.End();
    }
#endif
}
