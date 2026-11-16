#if TSC_ANDROID
using Microsoft.Xna.Framework;
using TSC.Ares;
using TSC.Input;

namespace TSC.Rendering;

public sealed partial class Playfield
{
    private readonly HashSet<string> _androidGameplayTouchActions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, AndroidGameplayTouchGestureState> _androidGameplayTouchGestureStates = new();
    private readonly HashSet<int> _androidGameplayActiveTouchIds = new();
    private readonly List<int> _androidGameplayStaleTouchIds = new(10);
    private readonly List<AndroidGameplayTouchSwipe> _androidGameplayTouchSwipesThisFrame = new(4);

    private sealed class AndroidGameplayTouchGestureState
    {
        public Vector2 LastPosition { get; set; }
        public GameplayTouchHitArea? LastArea { get; set; }
        // A touch can only trigger one flick while it remains in the same
        // physical lane. Leaving that lane rearms it, even if the finger stays down.
        public int? DisarmedFlickTrack { get; set; }
    }

    private readonly record struct AndroidGameplayTouchSwipe(
        int TouchId,
        GameplayTouchHitArea? EndArea,
        int Direction);

    /// <summary>
    /// Reads every active finger against the currently visible, projected gameplay
    /// lanes. Lane presses share the ordinary keybind path. Flicks and SuperFlicks
    /// are directional swipes from any still-down contact, including a finger that
    /// has already been holding a lane; they do not require a fresh tap.
    /// </summary>
    private void UpdateAndroidGameplayTouchActions()
    {
        _androidGameplayTouchSwipesThisFrame.Clear();
        if (!_lockMenuPlayMode || !_gameplayMode || _chartEditorMode ||
            _gameplayPaused || _gameplayResultActive || _gameplayResultTransitionPending ||
            _hardGaugeGameOverActive || _skinOutroStarted || _chart is null || _tempo is null ||
            !GlobalInput.MouseInputActive)
        {
            _androidGameplayTouchGestureStates.Clear();
            KeybindSettings.UpdateTouchGameplayActions(Array.Empty<string>(), suppressPressEdges: true);
            return;
        }

        if (GlobalInput.ActiveTouchPoints.Count == 0)
        {
            // An empty active-touch set is a real release, not a focus/transition
            // reset. Preserve the edge so holds and links receive their key-up.
            _androidGameplayTouchGestureStates.Clear();
            _androidGameplayTouchActions.Clear();
            KeybindSettings.UpdateTouchGameplayActions(_androidGameplayTouchActions);
            return;
        }

        var tick = _tempo.SecondsToTick(_playSeconds);
        var gear = RenderGearState(tick);
        BuildGameplayTouchHitAreas(gear);

        var outputViewport = _graphics.Viewport;
        var (virtualWidth, virtualHeight) = GetVirtualResolution(outputViewport.Width, outputViewport.Height);
        var scaleX = virtualWidth / (float)Math.Max(1, outputViewport.Width);
        var scaleY = virtualHeight / (float)Math.Max(1, outputViewport.Height);

        _androidGameplayTouchActions.Clear();
        _androidGameplayActiveTouchIds.Clear();
        foreach (var touch in GlobalInput.ActiveTouchPoints)
        {
            _androidGameplayActiveTouchIds.Add(touch.Id);
            // TouchInputPoint uses output-viewport pixels; lane projection and hit
            // geometry use the game's fixed-height virtual render resolution.
            var point = new Vector2(touch.Position.X * scaleX, touch.Position.Y * scaleY);
            var area = FindAndroidGameplayTouchArea(point);
            if (area is { } laneArea)
                _androidGameplayTouchActions.Add(laneArea.Action);

            UpdateAndroidGameplayTouchGesture(touch.Id, point, area);
        }

        _androidGameplayStaleTouchIds.Clear();
        foreach (var id in _androidGameplayTouchGestureStates.Keys)
        {
            if (!_androidGameplayActiveTouchIds.Contains(id))
                _androidGameplayStaleTouchIds.Add(id);
        }
        foreach (var staleId in _androidGameplayStaleTouchIds)
            _androidGameplayTouchGestureStates.Remove(staleId);

        KeybindSettings.UpdateTouchGameplayActions(_androidGameplayTouchActions);
    }

    private string? GetAndroidTouchActionForTrack(int track)
        => GetGameplayTouchActionForTrack(track);

    private bool IsAndroidTouchActionForTrack(int track, string action)
        => GetAndroidTouchActionForTrack(track) is { } touchAction &&
           string.Equals(touchAction, action, StringComparison.OrdinalIgnoreCase);

    private bool IsAndroidTouchTrackDown(int track)
        => GetAndroidTouchActionForTrack(track) is { } action && KeybindSettings.IsTouchDown(action);

    private bool IsAndroidTouchTrackPressed(int track)
        => GetAndroidTouchActionForTrack(track) is { } action && KeybindSettings.IsTouchPressed(action);

    private bool IsAndroidTouchTrackInputConsumed(int track, HashSet<string> consumedPressedKeys)
        => GetAndroidTouchActionForTrack(track) is { } action &&
           KeybindSettings.IsTouchDown(action) && consumedPressedKeys.Contains(action);

    private void ConsumeAndroidTouchTrackInput(int track, double currentSeconds, HashSet<string> consumedPressedKeys)
    {
        var standardAction = KeybindSettings.GetActionForTrack(track);
        var touchAction = GetAndroidTouchActionForTrack(track);
        if (string.IsNullOrWhiteSpace(touchAction) ||
            string.Equals(standardAction, touchAction, StringComparison.OrdinalIgnoreCase) ||
            !KeybindSettings.IsTouchDown(touchAction))
            return;

        consumedPressedKeys.Add(touchAction);
        ConsumeCurrentGameplayLanePressEdge(touchAction, currentSeconds);
    }

    private GameplayTouchHitArea? FindAndroidGameplayTouchArea(Vector2 point)
    {
        var bestArea = -1;
        var bestScore = float.PositiveInfinity;
        for (var areaIndex = 0; areaIndex < _gameplayTouchHitAreas.Count; areaIndex++)
        {
            var area = _gameplayTouchHitAreas[areaIndex];
            if (!ContainsPoint(area, point))
                continue;

            var score = GetTouchAreaDistanceScore(area, point);
            if (score >= bestScore)
                continue;

            bestScore = score;
            bestArea = areaIndex;
        }

        return bestArea >= 0 ? _gameplayTouchHitAreas[bestArea] : null;
    }

    private void UpdateAndroidGameplayTouchGesture(int touchId, Vector2 point, GameplayTouchHitArea? currentArea)
    {
        if (!_androidGameplayTouchGestureStates.TryGetValue(touchId, out var gesture))
        {
            _androidGameplayTouchGestureStates[touchId] = new AndroidGameplayTouchGestureState
            {
                LastPosition = point,
                LastArea = currentArea
            };
            return;
        }

        var previousPosition = gesture.LastPosition;
        var previousArea = gesture.LastArea;
        // Flick rearming is lane-based, not gesture- or direction-based. Once
        // the finger leaves the lane that produced a hit, that lane is eligible
        // again the next time the finger swipes into it.
        if (gesture.DisarmedFlickTrack is int disarmedTrack && currentArea?.Track != disarmedTrack)
            gesture.DisarmedFlickTrack = null;

        var directionArea = currentArea ?? previousArea;
        var acrossDirection = GetAndroidGameplayTouchAcrossDirection(directionArea, point);
        var movementAcross = Vector2.Dot(point - previousPosition, acrossDirection);

        gesture.LastPosition = point;
        gesture.LastArea = currentArea;

        // There is deliberately no pause rearm or minimum pixel distance here.
        // Every non-zero movement component along the bank's projected across-lane
        // axis is a directional flick sample. This lets short, slow, and alternating
        // movements register without waiting for a gesture timeout or crossing a
        // screen-space distance threshold. Per-track hit rearming still prevents a
        // sustained stroke from repeatedly hitting notes on the same lane.
        if (!float.IsFinite(movementAcross) || movementAcross == 0f)
            return;

        var direction = movementAcross < 0f ? -1 : 1;
        _androidGameplayTouchSwipesThisFrame.Add(new AndroidGameplayTouchSwipe(
            touchId,
            currentArea,
            direction));
    }

    private static Vector2 GetAndroidGameplayTouchAcrossDirection(GameplayTouchHitArea? area, Vector2 point)
    {
        if (area is not { } value)
            return Vector2.UnitX;

        // Use the actual projected visual bank edges, not the expanded touch
        // polygon. Interpolating near/far across vectors follows depth-dependent
        // lane rotation and perspective while preserving the screen-left to
        // screen-right sign used by chart Flick directions.
        var along = value.VisualFarCenter - value.VisualNearCenter;
        var alongLengthSquared = along.LengthSquared();
        var progress = alongLengthSquared > 0.0001f
            ? Math.Clamp(Vector2.Dot(point - value.VisualNearCenter, along) / alongLengthSquared, 0f, 1f)
            : 0f;
        var across = Vector2.Lerp(value.VisualNearAcross, value.VisualFarAcross, progress);
        if (across.LengthSquared() < 0.0001f)
            across = value.VisualNearAcross.LengthSquared() >= 0.0001f
                ? value.VisualNearAcross
                : Vector2.UnitX;
        return Vector2.Normalize(across);
    }

    private bool IsAndroidTouchFlickSwipePressed(ChartEvent ev, out int touchId)
    {
        touchId = -1;
        if (!ev.IsFlick || GearState.Position(ev.Track) is not { } position)
            return false;

        // Attr 10 is a left flick; attr 50 is a right flick.
        var expectedDirection = ev.FlickDirection == 1 ? -1 : 1;
        foreach (var swipe in _androidGameplayTouchSwipesThisFrame)
        {
            // Judge against the lane the finger is currently in. This still
            // accepts a swipe performed within a held lane or one that enters
            // the target lane, but prevents a swipe that has already left a lane
            // from continuing to trigger notes there through its stale start area.
            if (swipe.Direction != expectedDirection ||
                !TouchSwipeAreaMatchesTrackAndPosition(swipe.EndArea, ev.Track, position.Side, position.Bank) ||
                !_androidGameplayTouchGestureStates.TryGetValue(swipe.TouchId, out var gesture) ||
                gesture.DisarmedFlickTrack == ev.Track)
                continue;

            touchId = swipe.TouchId;
            return true;
        }
        return false;
    }

    private bool IsAndroidTouchSuperFlickSwipePressed(ChartEvent ev, out int touchId, out int touchedTrack)
    {
        touchId = -1;
        touchedTrack = -1;
        var position = GearState.Position(ev.Track);
        var side = ev.GetSuperFlickSide();
        if (!ev.IsSuperFlick || position is null || side is null)
            return false;

        var family = ev.GetSuperFlickFamily();
        var expectedDirection = (side, family) switch
        {
            // Inner: swipe toward the center of the playfield.
            ("left", SuperFlickFamily.Inner) => 1,
            ("right", SuperFlickFamily.Inner) => -1,
            // Outer: swipe away from the center of the playfield.
            ("left", SuperFlickFamily.Outer) => -1,
            ("right", SuperFlickFamily.Outer) => 1,
            _ => 0
        };
        if (expectedDirection == 0)
            return false;

        foreach (var swipe in _androidGameplayTouchSwipesThisFrame)
        {
            // SuperFlicks accept any currently occupied track in their bank.
            // Disarm that physical track after the first hit so a slow, sustained
            // stroke cannot repeatedly hit SuperFlicks there. Leaving it rearms it.
            if (swipe.Direction != expectedDirection ||
                swipe.EndArea is not { } endArea ||
                !TouchSwipeAreaMatchesPosition(swipe.EndArea, side, position.Value.Bank) ||
                !_androidGameplayTouchGestureStates.TryGetValue(swipe.TouchId, out var gesture) ||
                gesture.DisarmedFlickTrack == endArea.Track)
                continue;

            touchId = swipe.TouchId;
            touchedTrack = endArea.Track;
            return true;
        }
        return false;
    }

    private void DisarmAndroidGameplayTouchFlickTrack(int touchId, int track)
    {
        if (_androidGameplayTouchGestureStates.TryGetValue(touchId, out var gesture))
            gesture.DisarmedFlickTrack = track;
    }

    private static bool TouchSwipeAreaMatchesTrackAndPosition(
        GameplayTouchHitArea? area,
        int track,
        string side,
        string bank)
        => area is { } value && value.Track == track &&
           string.Equals(value.Side, side, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(value.Bank, bank, StringComparison.OrdinalIgnoreCase);

    private static bool TouchSwipeAreaMatchesPosition(GameplayTouchHitArea? area, string side, string bank)
        => area is { } value &&
           string.Equals(value.Side, side, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(value.Bank, bank, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsPoint(GameplayTouchHitArea area, Vector2 point)
    {
        // The projected lane area is a convex polygon (normally four corners;
        // center-collision clipping can produce a fifth). Consistent edge-cross
        // signs keep this allocation-free for every finger/track test.
        var positive = false;
        var negative = false;
        Span<Vector2> corners = stackalloc Vector2[5]
        {
            area.HitVertex0, area.HitVertex1, area.HitVertex2, area.HitVertex3, area.HitVertex4
        };

        for (var i = 0; i < area.HitVertexCount; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % area.HitVertexCount];
            var cross = (b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X);
            if (cross > 0.001f) positive = true;
            else if (cross < -0.001f) negative = true;
            if (positive && negative)
                return false;
        }

        return true;
    }

    private static float GetTouchAreaDistanceScore(GameplayTouchHitArea area, Vector2 point)
    {
        // Resolve overlapping banks against the actual visual track centerline,
        // not the shifted center of its expanded hit region.
        var along = area.VisualFarCenter - area.VisualNearCenter;
        var lengthSquared = along.LengthSquared();
        var progress = lengthSquared > 0.0001f
            ? Math.Clamp(Vector2.Dot(point - area.VisualNearCenter, along) / lengthSquared, 0f, 1f)
            : 0f;
        var center = Vector2.Lerp(area.VisualNearCenter, area.VisualFarCenter, progress);
        // Score against the original visible width, not the widened touch width,
        // so a 3x target does not steal touches from a lane whose center is nearer.
        var halfWidth = Math.Max(1f, MathHelper.Lerp(
            area.VisualNearHalfWidth, area.VisualFarHalfWidth, progress));
        return Vector2.DistanceSquared(point, center) / (halfWidth * halfWidth);
    }

}
#endif
