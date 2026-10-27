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
    // Gameplay state, timing, hold/link judgement, and event resolution.

    private GearState RenderGearState(double currentTick)
    {
        // In the chart editor, the selected bank is the starting route for the
        // preview. Successful SuperFlicks then mutate this state; do not override
        // it from the chart's authored GearTimeline every frame, or autoplay will
        // immediately snap back to the intended route.
        if (_chartEditorMode)
            return EnsureEditorPreviewGearState().Copy();

        return _gameplayMode
            ? _gameplayState
            : (_gear?.AtTick((int)Math.Floor(currentTick), true) ?? new GearState());
    }

    private static void SetGearStateBank(GearState state, string side, string bank)
    {
        if (side.Equals("left", StringComparison.OrdinalIgnoreCase))
        {
            switch (bank)
            {
                case "Forward": state.Left = GearSide.LeftForward; state.CenterLeft = GearSide.Inactive; break;
                case "Center": state.Left = GearSide.Inactive; state.CenterLeft = GearSide.CenterLeft; break;
                case "Backward": state.Left = GearSide.LeftBackward; state.CenterLeft = GearSide.Inactive; break;
            }
        }
        else
        {
            switch (bank)
            {
                case "Forward": state.Right = GearSide.RightForward; state.CenterRight = GearSide.Inactive; break;
                case "Center": state.Right = GearSide.Inactive; state.CenterRight = GearSide.CenterRight; break;
                case "Backward": state.Right = GearSide.RightBackward; state.CenterRight = GearSide.Inactive; break;
            }
        }
    }

    private GearState CreateEditorPreviewInitialState()
    {
        var state = _gear?.Initial ?? new GearState();
        if (_editorPreviewStartLeftBank is { } leftBank)
            SetGearStateBank(state, "left", leftBank);
        if (_editorPreviewStartRightBank is { } rightBank)
            SetGearStateBank(state, "right", rightBank);
        return state;
    }

    private GearState EnsureEditorPreviewGearState()
    {
        if (_editorPreviewGearState is null)
        {
            _editorPreviewGearState = CreateEditorPreviewInitialState();
            SyncEditorPreviewBankIndicators(_editorPreviewGearState);
        }
        return _editorPreviewGearState;
    }

    private void SyncEditorPreviewBankIndicators(GearState state)
    {
        if (!_chartEditorMode)
            return;
        _editorPreviewLeftBank = GearTimeline.ActiveBankForSide(state, "left");
        _editorPreviewRightBank = GearTimeline.ActiveBankForSide(state, "right");
    }

    private void ResetEditorTimelineRouteHistory(GearState? initialState)
    {
        _editorTimelineRouteHistory.Clear();
        if (initialState is not null)
            _editorTimelineRouteHistory.Add((0.0, initialState.Copy()));
    }

    private void EnsureEditorTimelineTraceCache()
    {
        if (ReferenceEquals(_editorTimelineTraceGear, _gear) &&
            ReferenceEquals(_editorTimelineTraceTempo, _tempo) &&
            ReferenceEquals(_editorTimelineTraceChart, _chart))
            return;

        _editorTimelineTraceSuperFlicks.Clear();
        if (_chart is not null)
        {
            _editorTimelineTraceSuperFlicks.AddRange(_chart.Events
                .Where(ev => ev.IsSuperFlick)
                .OrderBy(ev => ev.Tick)
                .ThenBy(ev => ev.Track)
                .ThenBy(ev => ev.EventIndex));
        }

        _editorTimelineTraceGear = _gear;
        _editorTimelineTraceTempo = _tempo;
        _editorTimelineTraceChart = _chart;
    }

    // When the editor playhead is moved, continue from the actual route at the
    // previous playhead. Only SuperFlicks crossed by this seek may change it.
    // This keeps a manually selected bank from snapping back to the authored
    // route just because the user scrubbed somewhere else in the chart.
    private void RebuildEditorPreviewRouteBetweenSeconds(
        double fromSeconds,
        double targetSeconds,
        GearState routeAtFrom)
    {
        if (!_chartEditorMode || _chart is null || _tempo is null || _gear is null)
            return;

        var duration = Math.Max(0.0, ChartDuration());
        fromSeconds = Math.Clamp(double.IsFinite(fromSeconds) ? fromSeconds : 0.0, 0.0, duration);
        targetSeconds = Math.Clamp(double.IsFinite(targetSeconds) ? targetSeconds : 0.0, 0.0, duration);
        var state = routeAtFrom.Copy();
        EnsureEditorTimelineTraceCache();

        // RecordEditorTimelineRouteState removes stale future entries, which is
        // required after a backward seek. The state recorded at the old playhead
        // must be the manual/autoplay route we actually had before seeking.
        RecordEditorTimelineRouteState(fromSeconds, state);
        const double timeEpsilon = 0.0001;

        if (targetSeconds > fromSeconds + timeEpsilon)
        {
            var eventIndex = 0;
            while (eventIndex < _editorTimelineTraceSuperFlicks.Count)
            {
                var tick = _editorTimelineTraceSuperFlicks[eventIndex].Tick;
                var transitionSeconds = _tempo.TickToSeconds(tick);
                var groupStart = eventIndex;
                var nextEventIndex = eventIndex + 1;
                while (nextEventIndex < _editorTimelineTraceSuperFlicks.Count &&
                       _editorTimelineTraceSuperFlicks[nextEventIndex].Tick == tick)
                    nextEventIndex++;
                eventIndex = nextEventIndex;

                if (transitionSeconds <= fromSeconds + timeEpsilon)
                    continue;
                if (transitionSeconds > targetSeconds + timeEpsilon)
                    break;

                // Same-tick events are checked against the same pre-transition
                // snapshot, with at most one successful SuperFlick per side.
                var before = state.Copy();
                var appliedLeft = false;
                var appliedRight = false;
                var changed = false;
                for (var i = groupStart; i < nextEventIndex; i++)
                {
                    var ev = _editorTimelineTraceSuperFlicks[i];
                    var side = ev.GetSuperFlickSide();
                    if (side is null)
                        continue;

                    var sideAlreadyApplied = side.Equals("left", StringComparison.OrdinalIgnoreCase)
                        ? appliedLeft
                        : appliedRight;
                    if (sideAlreadyApplied || !before.CanApplySuperFlick(ev.Attr / 10, ev.Track))
                        continue;
                    if (!state.ApplySuperFlick(ev.Attr / 10, ev.Track))
                        continue;

                    if (side.Equals("left", StringComparison.OrdinalIgnoreCase))
                        appliedLeft = true;
                    else
                        appliedRight = true;
                    changed = true;
                }

                if (changed)
                    RecordEditorTimelineRouteState(transitionSeconds, state);
            }
        }
        else if (targetSeconds < fromSeconds - timeEpsilon)
        {
            // Reverse only SuperFlicks crossed while moving backward, and only
            // when the current bank matches that SuperFlick's destination. For
            // example, a manual switch to 2K is not undone by rewinding across an
            // unrelated 1K<->3K flick.
            var eventEnd = _editorTimelineTraceSuperFlicks.Count;
            while (eventEnd > 0)
            {
                var groupEndExclusive = eventEnd;
                var tick = _editorTimelineTraceSuperFlicks[eventEnd - 1].Tick;
                var groupStart = eventEnd - 1;
                while (groupStart > 0 && _editorTimelineTraceSuperFlicks[groupStart - 1].Tick == tick)
                    groupStart--;
                eventEnd = groupStart;

                var transitionSeconds = _tempo.TickToSeconds(tick);
                if (transitionSeconds > fromSeconds + timeEpsilon)
                    continue;
                if (transitionSeconds <= targetSeconds + timeEpsilon)
                    break;

                var before = state.Copy();
                var appliedLeft = false;
                var appliedRight = false;
                for (var i = groupEndExclusive - 1; i >= groupStart; i--)
                {
                    var ev = _editorTimelineTraceSuperFlicks[i];
                    var side = ev.GetSuperFlickSide();
                    if (side is null)
                        continue;

                    var sideAlreadyApplied = side.Equals("left", StringComparison.OrdinalIgnoreCase)
                        ? appliedLeft
                        : appliedRight;
                    if (sideAlreadyApplied || !TryReverseEditorPreviewSuperFlick(ev, before, state))
                        continue;

                    if (side.Equals("left", StringComparison.OrdinalIgnoreCase))
                        appliedLeft = true;
                    else
                        appliedRight = true;
                }
            }

            // The playhead is now before the events we just crossed. Preserve the
            // resulting active route at the target; future playback/prediction will
            // continue from it instead of reconstructing from the chart's intended
            // start route.
            RecordEditorTimelineRouteState(targetSeconds, state);
        }

        _editorPreviewGearState = state.Copy();
        _gameplayState = state.Copy();
        SyncEditorPreviewBankIndicators(state);
    }

    private static bool TryReverseEditorPreviewSuperFlick(ChartEvent ev, GearState before, GearState state)
    {
        if (!ev.IsSuperFlick)
            return false;

        var position = GearState.Position(ev.Track);
        if (position is null)
            return false;

        var (side, bank, _) = position.Value;
        switch (ev.Attr / 10)
        {
            case 2: // 1K <-> 3K, left side.
                if (side != "left" || before.CenterLeft == GearSide.CenterLeft)
                    return false;
                if (bank == "Forward" && before.Left == GearSide.LeftBackward)
                {
                    state.Left = GearSide.LeftForward;
                    return true;
                }
                if (bank == "Backward" && before.Left == GearSide.LeftForward)
                {
                    state.Left = GearSide.LeftBackward;
                    return true;
                }
                return false;

            case 4: // 1K <-> 3K, right side.
                if (side != "right" || before.CenterRight == GearSide.CenterRight)
                    return false;
                if (bank == "Forward" && before.Right == GearSide.RightBackward)
                {
                    state.Right = GearSide.RightForward;
                    return true;
                }
                if (bank == "Backward" && before.Right == GearSide.RightForward)
                {
                    state.Right = GearSide.RightBackward;
                    return true;
                }
                return false;

            case 6: // Attr 60: outer right -> 2K, or left 2K -> 1K.
                if (bank == "Forward" || bank == "Backward")
                {
                    if (side != "right" || before.CenterRight != GearSide.CenterRight || before.Right != GearSide.Inactive)
                        return false;
                    state.CenterRight = GearSide.Inactive;
                    state.Right = bank == "Forward" ? GearSide.RightForward : GearSide.RightBackward;
                    return true;
                }
                if (bank == "Center")
                {
                    if (side != "left" || before.Left != GearSide.LeftForward || before.CenterLeft != GearSide.Inactive)
                        return false;
                    state.Left = GearSide.Inactive;
                    state.CenterLeft = GearSide.CenterLeft;
                    return true;
                }
                return false;

            case 7: // Attr 70: outer left -> 2K, or right 2K -> 1K.
                if (bank == "Forward" || bank == "Backward")
                {
                    if (side != "left" || before.CenterLeft != GearSide.CenterLeft || before.Left != GearSide.Inactive)
                        return false;
                    state.CenterLeft = GearSide.Inactive;
                    state.Left = bank == "Forward" ? GearSide.LeftForward : GearSide.LeftBackward;
                    return true;
                }
                if (bank == "Center")
                {
                    if (side != "right" || before.Right != GearSide.RightForward || before.CenterRight != GearSide.Inactive)
                        return false;
                    state.Right = GearSide.Inactive;
                    state.CenterRight = GearSide.CenterRight;
                    return true;
                }
                return false;

            case 8: // 2K -> 3K, left side.
                if (side != "left" || bank != "Center" || before.Left != GearSide.LeftBackward || before.CenterLeft != GearSide.Inactive)
                    return false;
                state.Left = GearSide.Inactive;
                state.CenterLeft = GearSide.CenterLeft;
                return true;

            case 9: // 2K -> 3K, right side.
                if (side != "right" || bank != "Center" || before.Right != GearSide.RightBackward || before.CenterRight != GearSide.Inactive)
                    return false;
                state.Right = GearSide.Inactive;
                state.CenterRight = GearSide.CenterRight;
                return true;

            default:
                return false;
        }
    }

    private void RecordEditorTimelineRouteState(double seconds, GearState state)
    {
        if (state is null)
            return;

        seconds = Math.Clamp(double.IsFinite(seconds) ? seconds : 0.0, 0.0, Math.Max(0.0, ChartDuration()));
        const double sameTimeEpsilon = 0.0001;
        while (_editorTimelineRouteHistory.Count > 0 &&
               _editorTimelineRouteHistory[^1].Seconds > seconds + sameTimeEpsilon)
        {
            _editorTimelineRouteHistory.RemoveAt(_editorTimelineRouteHistory.Count - 1);
        }

        var snapshot = state.Copy();
        if (_editorTimelineRouteHistory.Count > 0)
        {
            var last = _editorTimelineRouteHistory[^1];
            var sameState = last.State.Left == snapshot.Left &&
                            last.State.Right == snapshot.Right &&
                            last.State.CenterLeft == snapshot.CenterLeft &&
                            last.State.CenterRight == snapshot.CenterRight;
            if (Math.Abs(last.Seconds - seconds) <= sameTimeEpsilon)
            {
                _editorTimelineRouteHistory[^1] = (seconds, snapshot);
                return;
            }
            if (sameState)
                return;
        }

        _editorTimelineRouteHistory.Add((seconds, snapshot));
    }

    private void ApplyEditorPreviewSuperFlickHit(ChartEvent ev)
    {
        if (!_chartEditorMode)
            return;

        var state = EnsureEditorPreviewGearState();
        var before = state.Copy();
        if (!state.ApplySuperFlick(ev.Attr / 10, ev.Track))
            return;
        var after = state.Copy();
        _gameplayState = after.Copy();
        SyncEditorPreviewBankIndicators(after);
        RecordEditorTimelineRouteState(_playSeconds, after);

        var position = GearState.Position(ev.Track);
        if (position is not null)
        {
            var side = ev.GetSuperFlickSide() ?? position.Value.Side;
            var beforeBank = GearTimeline.ActiveBankForSide(before, side);
            var afterBank = GearTimeline.ActiveBankForSide(after, side);
            if (!string.Equals(beforeBank, afterBank, StringComparison.OrdinalIgnoreCase))
                StartBankSwitchTransition(side, afterBank, ev.Attr / 10, ev.GetSuperFlickFamily(before));
        }
    }

    private static ChartEventKey KeyOf(ChartEvent ev) => new(ev.Tick, ev.Track, ev.EventIndex);

    private bool IsHit(ChartEvent ev) => _hitEvents.Contains(KeyOf(ev));

    private void ProcessAutomaticHitSounds(double fromSeconds, double toSeconds)
    {
        if (_chart is null || _tempo is null || toSeconds <= fromSeconds)
            return;

        var fromTick = _tempo.SecondsToTick(fromSeconds);
        var toTick = _tempo.SecondsToTick(toSeconds);
        var first = UpperBound(_eventSeconds, fromSeconds);
        var last = UpperBound(_eventSeconds, toSeconds);
        var lastTick = -1;
        var lastKind = HitSoundKind.Note;

        if (_chartEditorMode)
        {
            // Keep autoplay hit sounds on the same alternate route as automatic
            // judgements. Simulate the interval locally so sounds after a
            // SuperFlick in this frame see the newly activated bank, without
            // advancing the shared state before the judgement pass runs.
            var state = EnsureEditorPreviewGearState().Copy();
            var i = first;
            while (i < last)
            {
                var tick = _orderedRenderableEvents[i].Tick;
                var stateChanges = new List<ChartEvent>();
                var seenSuperFlicks = new HashSet<SuperFlickVisualKey>();
                var seenSuperFlickSides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var next = i;
                while (next < last && _orderedRenderableEvents[next].Tick == tick)
                {
                    var ev = _orderedRenderableEvents[next++];
                    if (ev.Tick <= fromTick || ev.Tick > toTick ||
                        (ev.IsSuperFlick
                            ? !state.CanApplySuperFlick(ev.Attr / 10, ev.Track)
                            : !state.IsActive(ev.Track)))
                        continue;

                    if (ev.IsSuperFlick)
                    {
                        var side = ev.GetSuperFlickSide();
                        if (side is null || seenSuperFlickSides.Contains(side))
                            continue;
                        seenSuperFlickSides.Add(side);
                    }

                    var kind = ev.IsSuperFlick ? HitSoundKind.Change : HitSoundKind.Note;
                    if (ev.Tick != lastTick || kind != lastKind)
                    {
                        lastTick = ev.Tick;
                        lastKind = kind;
                        PlayHitSound(ev);
                    }

                    if (ev.IsSuperFlick && seenSuperFlicks.Add(SuperFlickKeyOf(ev)))
                        stateChanges.Add(ev);
                }

                // As with manual play, all notes at one tick use the state that
                // was active at the start of that tick.
                foreach (var change in stateChanges)
                    state.ApplySuperFlick(change.Attr / 10, change.Track);
                i = next;
            }
            return;
        }

        for (var i = first; i < last; i++)
        {
            var ev = _orderedRenderableEvents[i];
            if (ev.Tick <= fromTick || ev.Tick > toTick || !IsActiveEvent(ev))
                continue;
            var kind = ev.IsSuperFlick ? HitSoundKind.Change : HitSoundKind.Note;
            if (ev.Tick == lastTick && kind == lastKind)
                continue;
            lastTick = ev.Tick;
            lastKind = kind;
            PlayHitSound(ev);
        }
    }

    private double GetEditorAwareScrollCoordinate(double tick)
    {
        if (_chartEditorMode && _constantScroll && _tempo is not null)
            return _tempo.TickToSeconds(tick);
        return _tempo?.ScrollUnitsAtTick(tick) ?? tick;
    }

    private void RebuildRenderEventCache()
    {
        _orderedRenderableEvents.Clear();
        _eventSeconds.Clear();
        _eventScrollUnits.Clear();
        _eventEffectiveEndSeconds.Clear();
        _eventEffectiveEndScrollUnits.Clear();
        _longEventEndScrollUnits.Clear();
        _longRenderableEventIndicesByEnd.Clear();
        _renderEventIndexScratch.Clear();
        _renderEventMarks = Array.Empty<int>();
        _renderEventMarkStamp = 0;
        _eventSecondsByKey.Clear();
        _eventsByKey.Clear();
        _cachedRenderRangeTick = double.NaN;
        _cachedRenderRangeFirst = 0;
        _cachedRenderRangeLast = 0;

        _bankChangingSuperFlicks.Clear();
        _bankChangingSuperFlickTicks.Clear();
        _editorTimelineTraceSuperFlicks.Clear();
        _editorTimelineTraceGear = null;
        _editorTimelineTraceTempo = null;
        _editorTimelineTraceChart = null;
        _linkChainCache.Clear();
        _linkChainEffectiveEndTickCache.Clear();

        if (_chart is null || _tempo is null)
            return;

        RebuildBankChangingSuperFlickCache();

        _orderedRenderableEvents.AddRange(_chart.Events
            .Where(IsRenderableNoteEvent)
            .OrderBy(e => e.Tick)
            .ThenBy(e => e.Track)
            .ThenBy(e => e.EventIndex));

        if (_renderEventMarks.Length != _orderedRenderableEvents.Count)
            _renderEventMarks = new int[_orderedRenderableEvents.Count];

        // Link adjacency is already rebuilt by LoadChartObject. Build the immutable
        // chain lists once so DrawNotes/DrawLinkChainUnified never allocate a new
        // List<ChartEvent> while a chart is playing.
        foreach (var ev in _orderedRenderableEvents)
        {
            if (ev.NoteType != NoteType.LinkHead)
                continue;

            var nodes = new List<ChartEvent>(8) { ev };
            var visited = new HashSet<ChartEventKey> { KeyOf(ev) };
            var current = ev;
            var guard = 0;
            while (guard++ < 256 && _nextLinkByHeadKey.TryGetValue(KeyOf(current), out var next))
            {
                var tail = next.Tail;
                var tailKey = KeyOf(tail);
                if (!visited.Add(tailKey))
                    break;
                nodes.Add(tail);
                current = tail;
            }

            var key = KeyOf(ev);
            _linkChainCache[key] = nodes;
            var terminal = nodes[^1];
            _linkChainEffectiveEndTickCache[key] = nodes.Count > 1
                ? (terminal.Duration > 6 ? terminal.EndTick : terminal.Tick)
                : (ev.Duration > 6 ? ev.EndTick : ev.Tick);
        }

        for (var eventIndex = 0; eventIndex < _orderedRenderableEvents.Count; eventIndex++)
        {
            var ev = _orderedRenderableEvents[eventIndex];
            var seconds = _tempo.TickToSeconds(ev.Tick);
            var startScrollUnits = GetEditorAwareScrollCoordinate(ev.Tick);
            _eventSeconds.Add(seconds);
            _eventScrollUnits.Add(startScrollUnits);
            _eventSecondsByKey[KeyOf(ev)] = seconds;
            _eventsByKey[KeyOf(ev)] = ev;

            var effectiveEndTick = ev.Tick;
            if (ev.NoteType is NoteType.SingleLong or NoteType.Vibrato)
            {
                effectiveEndTick = ev.Duration > 6 ? ev.EndTick : ev.Tick;
            }
            else if (ev.NoteType == NoteType.LinkHead)
            {
                effectiveEndTick = _linkChainEffectiveEndTickCache.TryGetValue(KeyOf(ev), out var cachedChainEnd)
                    ? cachedChainEnd
                    : (ev.Duration > 6 ? ev.EndTick : ev.Tick);
            }
            else if (ev.NoteType == NoteType.LinkTail)
            {
                effectiveEndTick = GetLinkBodyEndTick(ev, IsTerminalLinkTail(ev));
            }

            var effectiveEndSeconds = _tempo.TickToSeconds(effectiveEndTick);
            var effectiveEndScrollUnits = GetEditorAwareScrollCoordinate(effectiveEndTick);
            _eventEffectiveEndSeconds.Add(effectiveEndSeconds);
            _eventEffectiveEndScrollUnits.Add(effectiveEndScrollUnits);
            if (ev.NoteType is NoteType.SingleLong or NoteType.Vibrato or NoteType.LinkHead or NoteType.LinkTail)
            {
                _longRenderableEventIndicesByEnd.Add(eventIndex);
                _longEventEndScrollUnits.Add(effectiveEndScrollUnits);
            }
        }

        // Index long-lived renderables by their spatial end coordinate. This lets
        // culling find bodies that extend into the render window without converting
        // the configured distance into seconds (which varies with tempo).
        if (_longRenderableEventIndicesByEnd.Count > 1)
        {
            var sortable = new List<(int EventIndex, double EndScrollUnits)>(_longRenderableEventIndicesByEnd.Count);
            for (var i = 0; i < _longRenderableEventIndicesByEnd.Count; i++)
                sortable.Add((_longRenderableEventIndicesByEnd[i], _longEventEndScrollUnits[i]));
            sortable.Sort((a, b) =>
            {
                var cmp = a.EndScrollUnits.CompareTo(b.EndScrollUnits);
                return cmp != 0 ? cmp : a.EventIndex.CompareTo(b.EventIndex);
            });
            _longRenderableEventIndicesByEnd.Clear();
            _longEventEndScrollUnits.Clear();
            foreach (var pair in sortable)
            {
                _longRenderableEventIndicesByEnd.Add(pair.EventIndex);
                _longEventEndScrollUnits.Add(pair.EndScrollUnits);
            }
        }
    }

    private void RebuildBankChangingSuperFlickCache()
    {
        if (_chart is null)
            return;

        var state = GearState.FromStartAttribute(_chart.InitialGearAttr);
        foreach (var tickGroup in _chart.Events
                     .Where(e => e.IsSuperFlick)
                     .OrderBy(e => e.Tick)
                     .ThenBy(e => e.Track)
                     .ThenBy(e => e.EventIndex)
                     .GroupBy(e => e.Tick))
        {
            var before = state.Copy();
            var appliedSides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ev in tickGroup)
            {
                var side = ev.GetSuperFlickSide();
                if (side is null || appliedSides.Contains(side) ||
                    !before.CanApplySuperFlick(ev.Attr / 10, ev.Track))
                    continue;

                var beforeMode = side.Equals("left", StringComparison.OrdinalIgnoreCase)
                    ? before.LeftMode
                    : before.RightMode;
                if (!state.ApplySuperFlick(ev.Attr / 10, ev.Track))
                    continue;

                appliedSides.Add(side);
                var afterMode = side.Equals("left", StringComparison.OrdinalIgnoreCase)
                    ? state.LeftMode
                    : state.RightMode;
                if (!string.Equals(beforeMode, afterMode, StringComparison.OrdinalIgnoreCase))
                {
                    _bankChangingSuperFlicks.Add((ev.Tick, side, ev));
                    _bankChangingSuperFlickTicks.Add((ev.Tick, side));
                }
            }
        }
    }

    private static int LowerBound(IReadOnlyList<double> values, double target)
    {
        var lo = 0;
        var hi = values.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (values[mid] < target)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private static int UpperBound(IReadOnlyList<double> values, double target)
    {
        var lo = 0;
        var hi = values.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (values[mid] <= target)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private double PixelsPerScrollUnit()
        => Math.Abs(_projection.ScrollPixelsFromCachedScrollUnits(1.0, 0.0));

    private (int First, int Last) GetRenderEventRange(double currentTick)
    {
        if (_orderedRenderableEvents.Count == 0 || _chart is null || _tempo is null)
            return (0, 0);

        if (Math.Abs(_cachedRenderRangeTick - currentTick) < 1e-9)
            return (_cachedRenderRangeFirst, _cachedRenderRangeLast);

        var effectiveAheadDistance = _projection.GetRenderDistanceAhead();
        var effectiveBehindDistance = _projection.GetRenderDistanceBehind();
        var currentScrollUnits = GetEditorAwareScrollCoordinate(currentTick);
        var pixelsPerScrollUnit = PixelsPerScrollUnit();

        // A zero scroll scale places every note at the same spatial point. In that
        // case there is no useful distance window, so leave the event list unculled.
        if (pixelsPerScrollUnit <= 1e-9)
        {
            _cachedRenderRangeTick = currentTick;
            _cachedRenderRangeFirst = 0;
            _cachedRenderRangeLast = _orderedRenderableEvents.Count;
            return (0, _orderedRenderableEvents.Count);
        }

        var leftScrollUnits = currentScrollUnits - effectiveBehindDistance / pixelsPerScrollUnit;
        var rightScrollUnits = currentScrollUnits + effectiveAheadDistance / pixelsPerScrollUnit;

        // Render windows are searched in the same spatial coordinate that positions
        // notes, not seconds. BPM therefore cannot widen or shrink the render window.
        var first = LowerBound(_eventScrollUnits, leftScrollUnits) - 1;
        var last = UpperBound(_eventScrollUnits, rightScrollUnits) + 1;
        first = Math.Max(0, first);
        last = Math.Min(_orderedRenderableEvents.Count, last);
        _cachedRenderRangeTick = currentTick;
        _cachedRenderRangeFirst = first;
        _cachedRenderRangeLast = last;
        return (first, last);
    }

    private int BuildRenderEventIndexScratch(double currentTick)
    {
        if (_orderedRenderableEvents.Count == 0)
        {
            _renderEventIndexScratch.Clear();
            return 0;
        }

        var (first, last) = GetRenderEventRange(currentTick);
        var scratch = _renderEventIndexScratch;
        scratch.Clear();

        if (_renderEventMarks.Length != _orderedRenderableEvents.Count)
            _renderEventMarks = new int[_orderedRenderableEvents.Count];

        unchecked
        {
            _renderEventMarkStamp++;
            if (_renderEventMarkStamp == 0)
            {
                Array.Clear(_renderEventMarks);
                _renderEventMarkStamp = 1;
            }
        }
        var stamp = _renderEventMarkStamp;

        for (var eventIndex = first; eventIndex < last; eventIndex++)
        {
            _renderEventMarks[eventIndex] = stamp;
            scratch.Add(eventIndex);
        }

        var effectiveBehindDistance = _projection.GetRenderDistanceBehind();
        var effectiveAheadDistance = _projection.GetRenderDistanceAhead();
        var currentScrollUnits = GetEditorAwareScrollCoordinate(currentTick);
        var pixelsPerScrollUnit = PixelsPerScrollUnit();
        var leftScrollUnits = pixelsPerScrollUnit <= 1e-9
            ? double.NegativeInfinity
            : currentScrollUnits - effectiveBehindDistance / pixelsPerScrollUnit;
        var rightScrollUnits = pixelsPerScrollUnit <= 1e-9
            ? double.PositiveInfinity
            : currentScrollUnits + effectiveAheadDistance / pixelsPerScrollUnit;
        var longFirst = LowerBound(_longEventEndScrollUnits, leftScrollUnits);

        // Only old long-lived events need the sparse side list. Events already in
        // the normal spatial head range are marked and skipped here.
        for (var longIndex = longFirst; longIndex < _longRenderableEventIndicesByEnd.Count; longIndex++)
        {
            var eventIndex = _longRenderableEventIndicesByEnd[longIndex];
            if (_eventScrollUnits[eventIndex] > rightScrollUnits)
                continue;
            if (_renderEventMarks[eventIndex] == stamp)
                continue;
            if (_eventEffectiveEndScrollUnits[eventIndex] < leftScrollUnits)
                continue;
            _renderEventMarks[eventIndex] = stamp;
            scratch.Add(eventIndex);
        }

        return scratch.Count;
    }

    private static readonly string[] GameplayLaneInputActions =
    {
        "game_left_1", "game_left_2", "game_left_3",
        "game_right_1", "game_right_2", "game_right_3"
    };

    private void CaptureGameplayLaneInputEdges(double currentSeconds)
    {
        foreach (var action in GameplayLaneInputActions)
        {
            if (KeybindSettings.IsPressed(action))
            {
                _gameplayLaneInputEdges.Add(new GameplayLaneInputEdge
                {
                    Action = action,
                    Seconds = currentSeconds,
                    IsPress = true
                });
            }
            if (KeybindSettings.IsReleased(action))
            {
                _gameplayLaneInputEdges.Add(new GameplayLaneInputEdge
                {
                    Action = action,
                    Seconds = currentSeconds,
                    IsPress = false
                });
            }
        }

        // Only a small amount of recent input history is useful for link tails.
        // Keep enough history to cover their timing window without growing for
        // the entire song.
        var oldestUsefulInput = currentSeconds - Math.Max(0.5, LinkTailLeniencyWindowSeconds * 3.0);
        for (var i = _gameplayLaneInputEdges.Count - 1; i >= 0; i--)
        {
            if (_gameplayLaneInputEdges[i].Seconds < oldestUsefulInput)
                _gameplayLaneInputEdges.RemoveAt(i);
        }
    }

    private bool TryFindLinkPressEdge(
        string action,
        double transitionSeconds,
        HashSet<string> consumedPressedKeys,
        out GameplayLaneInputEdge? edge)
    {
        edge = null;
        var bestDistance = double.PositiveInfinity;
        foreach (var candidate in _gameplayLaneInputEdges)
        {
            if (candidate.Consumed || !candidate.IsPress ||
                !string.Equals(candidate.Action, action, StringComparison.OrdinalIgnoreCase) ||
                consumedPressedKeys.Contains(candidate.Action))
                continue;

            var distance = Math.Abs(candidate.Seconds - transitionSeconds);
            if (distance > LinkTailLeniencyWindowSeconds + 1e-9 || distance >= bestDistance)
                continue;

            edge = candidate;
            bestDistance = distance;
        }
        return edge is not null;
    }

    private void ConsumeCurrentGameplayLanePressEdge(string action, double currentSeconds)
    {
        // Normal taps and hold heads consume their press edge too, so a press used
        // for one note cannot be replayed from the history buffer to satisfy a
        // nearby LinkTail on a later frame. This only marks the edge captured for
        // the current input frame; earlier tail presses remain eligible until a
        // real note has consumed them. Avoid LINQ allocations on this gameplay path.
        GameplayLaneInputEdge? closest = null;
        var closestDistance = double.PositiveInfinity;
        foreach (var candidate in _gameplayLaneInputEdges)
        {
            if (candidate.Consumed || !candidate.IsPress ||
                !string.Equals(candidate.Action, action, StringComparison.OrdinalIgnoreCase))
                continue;

            var distance = Math.Abs(candidate.Seconds - currentSeconds);
            if (distance > 1e-6 || distance >= closestDistance)
                continue;

            closest = candidate;
            closestDistance = distance;
        }

        if (closest is not null)
            closest.Consumed = true;
    }

    private bool TryConsumeLinkTailInput(
        HoldRuntime runtime,
        ChartEvent currentNode,
        ChartEvent nextNode,
        double transitionSeconds,
        HashSet<string> consumedPressedKeys,
        out int inputTrack,
        out bool usedCorrection,
        out string inputAction)
    {
        inputTrack = nextNode.Track;
        usedCorrection = false;
        inputAction = string.Empty;

        // First prefer the correctly mapped destination lane. Input history lets
        // Same-tick horizontal connections can use a destination press from the
        // wider LinkTail leniency window, even if it arrived just before the head.
        var expectedAction = KeybindSettings.GetActionForTrack(nextNode.Track);
        if (expectedAction is not null && TryFindLinkPressEdge(
                expectedAction, transitionSeconds, consumedPressedKeys, out var expectedEdge) &&
            expectedEdge is not null)
        {
            expectedEdge.Consumed = true;
            inputTrack = nextNode.Track;
            inputAction = expectedAction;
            consumedPressedKeys.Add(expectedAction);
            return true;
        }

        // A same-bank wrong-lane press can temporarily bridge a lane switch. The
        // authored lane must still be reached before the LinkTail leniency window closes.
        var targetPosition = GearState.Position(nextNode.Track);
        var currentInputTrack = ResolveHoldInputTrack(runtime, currentNode);
        GameplayLaneInputEdge? bestCorrectionEdge = null;
        var bestCorrectionTrack = -1;
        var bestCorrectionDistance = double.PositiveInfinity;
        if (targetPosition is not null)
        {
            for (var candidateTrack = 0; candidateTrack < 16; candidateTrack++)
            {
                if (candidateTrack == nextNode.Track || candidateTrack == currentInputTrack)
                    continue;

                var candidatePosition = GearState.Position(candidateTrack);
                if (candidatePosition is null ||
                    !string.Equals(candidatePosition.Value.Side, targetPosition.Value.Side, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(candidatePosition.Value.Bank, targetPosition.Value.Bank, StringComparison.OrdinalIgnoreCase))
                    continue;

                var candidateAction = KeybindSettings.GetActionForTrack(candidateTrack);
                if (string.IsNullOrWhiteSpace(candidateAction) ||
                    string.Equals(candidateAction, expectedAction, StringComparison.OrdinalIgnoreCase) ||
                    !TryFindLinkPressEdge(candidateAction, transitionSeconds, consumedPressedKeys, out var correctionEdge) ||
                    correctionEdge is null)
                    continue;

                var distance = Math.Abs(correctionEdge.Seconds - transitionSeconds);
                if (distance >= bestCorrectionDistance)
                    continue;

                bestCorrectionDistance = distance;
                bestCorrectionEdge = correctionEdge;
                bestCorrectionTrack = candidateTrack;
                inputAction = candidateAction;
            }
        }

        if (bestCorrectionEdge is not null && bestCorrectionTrack >= 0)
        {
            bestCorrectionEdge.Consumed = true;
            inputTrack = bestCorrectionTrack;
            usedCorrection = true;
            consumedPressedKeys.Add(inputAction);
            return true;
        }

        // A release is not a LinkTail hit. It only means the player may be
        // switching lanes; UpdateHoldRuntime keeps the chain alive through the
        // LinkTail's timing window and judges it only when a lane press is found.
        return false;
    }

    private void ProcessGameplayHits()
    {
        if (_chart is null || _tempo is null)
            return;

        var currentSeconds = _playSeconds;
        var currentTick = _tempo.SecondsToTick(currentSeconds);
        CaptureGameplayLaneInputEdges(currentSeconds);
        _superFlickResolvedThisFrame.Clear();
        _pendingSuperFlickStateChanges.Clear();
        var frameGameplayState = _gameplayState;
        _superFlickDebugFrame++;
        SuperFlickDebugLogInput(Keyboard.GetState());
        if (_superFlickDebugEnabled && (_superFlickDebugFrame % 30 == 0))
            SuperFlickDebugLog($"FRAME frame={_superFlickDebugFrame} tick={currentTick:0.###} sec={currentSeconds:0.000000} gear={SuperFlickDebugGearState()}");
        var ordered = _orderedRenderableEvents;
        var (firstEvent, lastEvent) = GetGameplayEventRange(currentSeconds);

        var consumedPressedKeys = _gameplayConsumedPressedKeys;
        consumedPressedKeys.Clear();
        var newlyStartedLinkRuntimes = _gameplayNewlyStartedLinkRuntimes;
        newlyStartedLinkRuntimes.Clear();

        // First update already-started holds and link chains. Link input edges are
        // captured before this pass so a release or a lane press within the tail
        // window is available even when it happened one frame earlier.
        foreach (var runtime in _holdStates.Values)
        {
            if (_hardGaugeGameOverActive)
                return;
            if (!runtime.Started || runtime.Dropped || runtime.Completed)
                continue;
            UpdateHoldRuntime(runtime, currentSeconds, currentTick, consumedPressedKeys);
        }

        for (var eventIndex = firstEvent; eventIndex < lastEvent; eventIndex++)
        {
            if (_hardGaugeGameOverActive)
                return;
            var ev = ordered[eventIndex];
            var eventKey = KeyOf(ev);

            if (!IsActiveEvent(ev, frameGameplayState))
                continue;

            if (ev.Track > 15 || ev.NoteType == NoteType.End ||
                _missedEvents.Contains(eventKey) || _gameplayIgnoredEvents.Contains(eventKey))
                continue;

            // Remember that an event was actually available for judgement only
            // after it entered its own hit window. Being active while it is still
            // far in the future must not count: a bank-switch superFlick can hide it
            // and later reveal it after the window has already expired.
            var inJudgeWindow = Math.Abs(_eventSeconds[eventIndex] - currentSeconds) <= GameplayHitWindowSeconds;
            if (inJudgeWindow)
                _gameplaySeenActiveEvents.Add(eventKey);

            if (ev.NoteType == NoteType.LinkTail)
                continue; // link tails are handled by the active hold runtime.

            var eventSeconds = _eventSeconds[eventIndex];
            var deltaSeconds = currentSeconds - eventSeconds;

            if (_superFlickDebugEnabled && ev.IsSuperFlick && Math.Abs(deltaSeconds) <= 0.35)
            {
                var fk = SuperFlickKeyOf(ev);
                var fkeys = GameplayKeybinds.GetSuperFlickKeys(ev);
                SuperFlickDebugLog($"CANDIDATE tick={ev.Tick} track={ev.Track} index={ev.EventIndex} family={ev.GetSuperFlickFamily()} dir={ev.Attr / 10} side={fk.Side} bank={fk.Bank} deltaMs={deltaSeconds * 1000.0:0.###} windowMs={GameplayHitWindowSeconds * 1000.0:0.###} active={IsActiveEvent(ev, frameGameplayState)} resolvedFrame={_superFlickResolvedThisFrame.Contains(fk)} resolvedAll={_resolvedSuperFlicks.Contains(fk)} hit={IsHit(ev)} missed={_missedEvents.Contains(KeyOf(ev))} keys={SuperFlickDebugKeyState(fkeys, Keyboard.GetState())} gear={SuperFlickDebugGearState()}");
            }

            if (ev.NoteType is NoteType.SingleLong or NoteType.Vibrato or NoteType.LinkHead)
            {
                var runtime = GetOrCreateHoldRuntime(ev);
                if (runtime.Started || runtime.StartMissed || runtime.Dropped || runtime.Completed)
                    continue;

                var holdAction = KeybindSettings.GetActionForTrack(ev.Track);
                if (Math.Abs(deltaSeconds) <= GameplayHitWindowSeconds &&
                    holdAction is not null &&
                    !consumedPressedKeys.Contains(holdAction) &&
                    IsTrackKeyPressed(ev.Track))
                {
                    consumedPressedKeys.Add(holdAction);
                    ConsumeCurrentGameplayLanePressEdge(holdAction, currentSeconds);
                    StartHold(runtime, deltaSeconds);
                    if (ev.NoteType == NoteType.LinkHead)
                        newlyStartedLinkRuntimes.Add(runtime);
                    continue;
                }

                if (deltaSeconds > GameplayHitWindowSeconds)
                {
                    if (!_gameplaySeenActiveEvents.Contains(eventKey))
                        IgnoreUnavailableEvent(ev, currentTick);
                    else
                        MissHoldStart(runtime);
                }
                continue;
            }

            if (ev.IsSuperFlick)
            {
                var superFlickKey = SuperFlickKeyOf(ev);
                if (_superFlickDebugEnabled && Math.Abs(deltaSeconds) <= 0.35 &&
                    (_superFlickResolvedThisFrame.Contains(superFlickKey) || _resolvedSuperFlicks.Contains(superFlickKey) || IsHit(ev) || _missedEvents.Contains(KeyOf(ev))))
                {
                    SuperFlickDebugLog($"SKIP tick={ev.Tick} track={ev.Track} index={ev.EventIndex} family={ev.GetSuperFlickFamily()} reason=resolved-or-consumed frameResolved={_superFlickResolvedThisFrame.Contains(superFlickKey)} allResolved={_resolvedSuperFlicks.Contains(superFlickKey)} hit={IsHit(ev)} missed={_missedEvents.Contains(KeyOf(ev))}");
                }
                if (_superFlickResolvedThisFrame.Contains(superFlickKey) || _resolvedSuperFlicks.Contains(superFlickKey) || IsHit(ev) || _missedEvents.Contains(KeyOf(ev)))
                    continue;

                if (Math.Abs(deltaSeconds) <= GameplayHitWindowSeconds && IsSuperFlickInputPressed(ev))
                {
                    if (_superFlickDebugEnabled)
                    {
                        var dbgKeys = GameplayKeybinds.GetSuperFlickKeys(ev);
                        SuperFlickDebugLog($"RESULT HIT tick={ev.Tick} track={ev.Track} index={ev.EventIndex} family={ev.GetSuperFlickFamily()} dir={ev.Attr / 10} deltaMs={deltaSeconds * 1000.0:0.###} keys={SuperFlickDebugKeyState(dbgKeys, Keyboard.GetState())} gearBefore={SuperFlickDebugGearState()}");
                    }
                    if (KeybindSettings.GetSuperFlickAction(ev) is { } superFlickAction)
                        consumedPressedKeys.Add(superFlickAction);
                    MarkSuperFlickVisualHit(ordered, ev, _hitEvents);
                    _superFlickResolvedThisFrame.Add(superFlickKey);
                    _resolvedSuperFlicks.Add(superFlickKey);
                    RegisterHit(deltaSeconds * 1000.0, ev);
                    _pendingSuperFlickStateChanges.Add((ev.Tick, ev.Attr / 10, ev.Track, ev.GetSuperFlickFamily(frameGameplayState)));
                    if (_superFlickDebugEnabled)
                        SuperFlickDebugLog($"STATE QUEUED tick={ev.Tick} track={ev.Track} pending={_pendingSuperFlickStateChanges.Count} gearSnapshot={SuperFlickDebugGearState()}");
                    PlayHitSound(ev);
                }
                else if (deltaSeconds > GameplayHitWindowSeconds)
                {
                    if (!_gameplaySeenActiveEvents.Contains(eventKey))
                    {
                        if (_superFlickDebugEnabled)
                            SuperFlickDebugLog($"RESULT IGNORE tick={ev.Tick} track={ev.Track} index={ev.EventIndex} family={ev.GetSuperFlickFamily()} dir={ev.Attr / 10} deltaMs={deltaSeconds * 1000.0:0.###} reason=ACTIVATED_AFTER_WINDOW gear={SuperFlickDebugGearState()}");

                        // The superFlick only became available after its timing window.
                        // Suppress the whole visual duplicate group without issuing
                        // a gameplay MISS.
                        MarkSuperFlickVisualIgnored(ordered, ev, _gameplayIgnoredEvents);
                        _superFlickResolvedThisFrame.Add(superFlickKey);
                        _resolvedSuperFlicks.Add(superFlickKey);
                    }
                    else
                    {
                        if (_superFlickDebugEnabled)
                            SuperFlickDebugLog($"RESULT MISS tick={ev.Tick} track={ev.Track} index={ev.EventIndex} family={ev.GetSuperFlickFamily()} dir={ev.Attr / 10} deltaMs={deltaSeconds * 1000.0:0.###} reason=TIMING_WINDOW_EXPIRED keys={SuperFlickDebugKeyState(GameplayKeybinds.GetSuperFlickKeys(ev), Keyboard.GetState())} gear={SuperFlickDebugGearState()}");
                        // Resolve a visual superFlick exactly once. Early-press detection is
                        // deliberately excluded for superFlicks; a superFlick is missed only when
                        // its own timing window has actually expired. The resolution
                        // persists across frames so a successful superFlick cannot be
                        // revisited and turned into a later MISS.
                        MarkSuperFlickVisualMiss(ordered, ev, _missedEvents);
                        _superFlickResolvedThisFrame.Add(superFlickKey);
                        _resolvedSuperFlicks.Add(superFlickKey);
                        RegisterMiss("superFlick-timeout", ev);
                    }
                }
                continue;
            }

            if (IsHit(ev))
                continue;

            var tapAction = KeybindSettings.GetActionForTrack(ev.Track);
            if (Math.Abs(deltaSeconds) <= GameplayHitWindowSeconds &&
                tapAction is not null &&
                !consumedPressedKeys.Contains(tapAction) &&
                IsTrackKeyPressed(ev.Track))
            {
                consumedPressedKeys.Add(tapAction);
                ConsumeCurrentGameplayLanePressEdge(tapAction, currentSeconds);
                _hitEvents.Add(KeyOf(ev));
                RegisterHit(deltaSeconds * 1000.0, ev);
                PlayHitSound(ev);
            }
            else if (deltaSeconds > GameplayHitWindowSeconds)
            {
                if (!_gameplaySeenActiveEvents.Contains(eventKey))
                    IgnoreUnavailableEvent(ev, currentTick);
                else
                    MissEvent(ev);
            }
        }

        // A link chain can start in the event pass after the ordinary hold update.
        // Give newly-started LinkHeads one transition update so a simultaneous or
        // slightly earlier destination-lane press can judge a same-tick LinkTail.
        foreach (var runtime in newlyStartedLinkRuntimes)
        {
            if (_hardGaugeGameOverActive)
                return;
            if (runtime.Started && !runtime.Dropped && !runtime.Completed)
                UpdateHoldRuntime(runtime, currentSeconds, currentTick, consumedPressedKeys);
        }

        // Only evaluate early misses after the normal hit pass. This lets an actual
        // hit on a very close repeated note (jack) consume the press before the
        // early-miss detector considers the next note.
        ProcessEarlyPressMisses(ordered, currentSeconds, consumedPressedKeys, frameGameplayState);

        // Apply superFlick-induced bank changes only after the entire frame has been
        // resolved against the same active-bank snapshot. This prevents a superFlick
        // from making unrelated events become active halfway through the pass.
        foreach (var change in _pendingSuperFlickStateChanges)
        {
            var before = _gameplayState.Copy();
            if (!_gameplayState.ApplySuperFlick(change.Direction, change.Track))
                continue;
            var after = _gameplayState.Copy();
            var superFlickPosition = GearState.Position(change.Track);
            if (superFlickPosition is not null)
            {
                var side = superFlickPosition.Value.Side;
                var bank = GearTimeline.ActiveBankForSide(after, side);
                StartBankSwitchTransition(side, bank, change.Direction, change.Family);
            }
            if (_chartEditorMode)
            {
                // Keep the bank buttons in sync with the state reached by an
                // actually judged manual SuperFlick. The start-bank choice is
                // separate, so Retry can restore the route the user selected.
                _editorPreviewGearState = after.Copy();
                SyncEditorPreviewBankIndicators(after);
                RecordEditorTimelineRouteState(_playSeconds, after);
            }

            if (_superFlickDebugEnabled)
                SuperFlickDebugLog($"STATE APPLY track={change.Track} dir={change.Direction} before={before.LeftMode}/{before.RightMode} after={SuperFlickDebugGearState()}");

            // A superFlick can reveal notes that were completely unavailable during
            // their own judgement window. Those notes must never become a MISS
            // just because the bank was switched back to them later. Decide this
            // at the exact activation boundary instead of trying to infer it from
            // whether the note happened to be active in an earlier bank.
            if (_tempo is not null)
            {
                var activationFirst = Math.Max(0, LowerBound(_eventSeconds, currentSeconds - 0.001));
                var activationLast = Math.Min(ordered.Count, UpperBound(_eventSeconds, currentSeconds + GameplayHitWindowSeconds) + 1);
                for (var activationIndex = activationFirst; activationIndex < activationLast; activationIndex++)
                {
                    var ev = ordered[activationIndex];
                    var key = KeyOf(ev);
                    if (_hitEvents.Contains(key) || _missedEvents.Contains(key) || _gameplayIgnoredEvents.Contains(key))
                        continue;
                    if (!IsRenderableNoteEvent(ev))
                        continue;

                    var wasActive = before.IsActive(ev.Track);
                    var isActive = after.IsActive(ev.Track);
                    if (wasActive || !isActive)
                        continue;

                    var eventSeconds = _eventSeconds[activationIndex];

                    // A bank switch takes effect strictly after the flick tick.
                    // A normal note that becomes active exactly on the switch tick
                    // is therefore not playable on the newly activated route.
                    // It remains in the score model so an alternate route that
                    // does not perform the switch can still score it normally.
                    if (!ev.IsSuperFlick && ev.Tick == change.Tick)
                    {
                        _gameplayIgnoredEvents.Add(key);
                        if (_superFlickDebugEnabled)
                            SuperFlickDebugLog($"JUDGEMENT IGNORE source=bank-activated-at-switch-tick eventTick={ev.Tick} track={ev.Track} type={ev.NoteType} currentTick={currentTick:0.###} before={before.LeftMode}/{before.RightMode} after={after.LeftMode}/{after.RightMode}");
                        continue;
                    }

                    if (currentSeconds - eventSeconds <= GameplayHitWindowSeconds)
                        continue;

                    if (ev.IsSuperFlick)
                    {
                        var superFlickKey = SuperFlickKeyOf(ev);
                        MarkSuperFlickVisualIgnored(ordered, ev, _gameplayIgnoredEvents);
                        _superFlickResolvedThisFrame.Add(superFlickKey);
                        _resolvedSuperFlicks.Add(superFlickKey);
                    }
                    else
                    {
                        _gameplayIgnoredEvents.Add(key);
                    }

                    if (_superFlickDebugEnabled)
                        SuperFlickDebugLog($"JUDGEMENT IGNORE source=bank-activated-after-window eventTick={ev.Tick} track={ev.Track} type={ev.NoteType} currentTick={currentTick:0.###} before={before.LeftMode}/{before.RightMode} after={after.LeftMode}/{after.RightMode}");
                }
            }
        }
        _pendingSuperFlickStateChanges.Clear();
    }

    private void UpdateHoldRuntime(
        HoldRuntime runtime,
        double currentSeconds,
        double currentTick,
        HashSet<string> consumedPressedKeys)
    {
        if (_tempo is null)
            return;

        var nodes = runtime.ChainNodes;
        if (nodes.Count == 0)
        {
            DropHold(runtime);
            return;
        }

        // A wrong-lane correction is a one-window grace period, not a new lane
        // assignment for the rest of the chain. If the player reaches the expected
        // lane before the deadline, continue normally; otherwise drop after the
        // original LinkTail window closes, even if the incorrect lane is still held.
        if (runtime.InputCorrectionDeadlineSeconds is double correctionDeadline)
        {
            var expectedNode = nodes[Math.Min(runtime.CurrentNodeIndex, nodes.Count - 1)];
            if (IsTrackKeyDown(expectedNode.Track))
            {
                runtime.CurrentInputTrackOverride = null;
                runtime.InputCorrectionDeadlineSeconds = null;
                runtime.AllowUnheldUntilNextTailWindow = false;
            }
            else if (currentSeconds > correctionDeadline + 1e-9)
            {
                DropHold(runtime);
                return;
            }
        }

        var comboStep = Math.Max(1.0, _tempo.TicksPerBeat / 4.0);
        var totalEndTick = GetHoldEndTick(runtime.Root);

        while (currentTick + 1e-9 >= runtime.NextComboTick && runtime.NextComboTick < totalEndTick && !runtime.Dropped && !runtime.Completed)
        {
            RegisterHoldTickJudgement(runtime, runtime.NextScoreTickIndex);
            runtime.NextScoreTickIndex++;
            runtime.NextComboTick += comboStep;
        }

        while (runtime.CurrentNodeIndex + 1 < nodes.Count && !runtime.Dropped && !runtime.Completed)
        {
            var currentNode = nodes[runtime.CurrentNodeIndex];
            var nextNode = nodes[runtime.CurrentNodeIndex + 1];
            var transitionSeconds = _tempo.TickToSeconds(nextNode.Tick);
            var deltaToTransition = currentSeconds - transitionSeconds;

            var currentInputTrack = ResolveHoldInputTrack(runtime, currentNode);

            // Allow the player to release and switch lanes across the wider LinkTail
            // window, but never judge a tail from a release alone.
            if (deltaToTransition < -LinkTailLeniencyWindowSeconds - 1e-9)
            {
                if (!runtime.AllowUnheldUntilNextTailWindow && !IsTrackKeyDown(currentInputTrack))
                    DropHold(runtime);
                break;
            }

            if (TryConsumeLinkTailInput(
                    runtime, currentNode, nextNode, transitionSeconds, consumedPressedKeys,
                    out var acceptedInputTrack, out var usedCorrection, out var acceptedInputAction))
            {
                runtime.CurrentNodeIndex++;
                if (usedCorrection)
                {
                    // Keep the incorrect lane only as a temporary signal that the
                    // correction key remains down. The authored destination lane is
                    // still required before this LinkTail's timing window expires.
                    runtime.CurrentInputTrackOverride = IsTrackKeyDown(acceptedInputTrack)
                        ? acceptedInputTrack
                        : null;
                    runtime.InputCorrectionDeadlineSeconds = transitionSeconds + LinkTailLeniencyWindowSeconds;
                    runtime.AllowUnheldUntilNextTailWindow = true;
                }
                else
                {
                    runtime.CurrentInputTrackOverride = null;
                    runtime.InputCorrectionDeadlineSeconds = null;
                    runtime.AllowUnheldUntilNextTailWindow = !IsTrackKeyDown(nextNode.Track);
                }
                if (!string.IsNullOrWhiteSpace(acceptedInputAction))
                    consumedPressedKeys.Add(acceptedInputAction);
                _hitEvents.Add(KeyOf(nextNode));
                ShowJudgement(runtime.StartJudgement, nextNode);
                PlayHitSound(nextNode);
                runtime.NextTailSoundIndex = Math.Max(runtime.NextTailSoundIndex, runtime.CurrentNodeIndex + 1);

                // A terminal LinkTail remains an authored-time endpoint. It is
                // still completed at its actual endpoint instead of disappearing
                // as soon as the input is accepted in the GOOD window.
                continue;
            }

            if (deltaToTransition > LinkTailLeniencyWindowSeconds)
            {
                DropHold(runtime);
            }
            break;
        }

        if (runtime.Dropped || runtime.Completed)
            return;

        var currentNodeAfterTransition = nodes[Math.Min(runtime.CurrentNodeIndex, nodes.Count - 1)];
        var currentEndTick = currentNodeAfterTransition.Duration > 6
            ? currentNodeAfterTransition.EndTick
            : currentNodeAfterTransition.Tick;
        var currentEndSeconds = _tempo.TickToSeconds(currentEndTick);
        var deltaToEnd = currentSeconds - currentEndSeconds;

        if (runtime.CurrentNodeIndex == nodes.Count - 1)
        {
            // Once the end enters the GOOD window, releasing is safe, but the
            // hold does not visually/physically complete until the actual end tick.
            // This keeps the body clipped at judgement while it continues scrolling.
            if (deltaToEnd >= 0)
            {
                CompleteHold(runtime);
                return;
            }

            var terminalInputTrack = ResolveHoldInputTrack(runtime, currentNodeAfterTransition);
            if (deltaToEnd < -GameplayHitWindowSeconds &&
                !runtime.AllowUnheldUntilNextTailWindow &&
                !IsTrackKeyDown(terminalInputTrack))
                DropHold(runtime);
            return;
        }

        // A link chain may briefly have no key held before its next checkpoint.
        var nextTransitionSeconds = _tempo.TickToSeconds(nodes[runtime.CurrentNodeIndex + 1].Tick);
        var currentInputTrackAfterTransition = ResolveHoldInputTrack(runtime, currentNodeAfterTransition);
        if (currentSeconds < nextTransitionSeconds - LinkTailLeniencyWindowSeconds - 1e-9 &&
            !runtime.AllowUnheldUntilNextTailWindow &&
            !IsTrackKeyDown(currentInputTrackAfterTransition))
            DropHold(runtime);
    }

    private int ResolveHoldInputTrack(HoldRuntime runtime, ChartEvent node)
    {
        // If the player has switched back to the authored lane after a correction,
        // let that lane take over as the active hold source.
        if (runtime.CurrentInputTrackOverride.HasValue && IsTrackKeyDown(node.Track))
        {
            runtime.CurrentInputTrackOverride = null;
            runtime.InputCorrectionDeadlineSeconds = null;
            runtime.AllowUnheldUntilNextTailWindow = false;
        }

        return runtime.CurrentInputTrackOverride ?? node.Track;
    }

    private void RegisterHoldTickJudgement(HoldRuntime runtime, int tickIndex)
    {
        var tier = runtime.StartJudgement;

        // A hold tick is an actual judgement checkpoint now. It inherits the
        // hold's starting timing tier/quality because the tick is generated
        // automatically while the hold is being sustained. The checkpoint is
        // still worth less than a normal note through HoldTickAccuracyWeight.
        ShowJudgement(tier);
        if (_scoreHoldTickEntries.TryGetValue(KeyOf(runtime.Root), out var entries) &&
            tickIndex >= 0 && tickIndex < entries.Count)
        {
            var entry = entries[tickIndex];
            RecordAccuracy(tier, runtime.StartOffsetMs, entry);
            AwardScorePoints(entry, tier);
        }

        _combo++;
        _maxCombo = Math.Max(_maxCombo, _combo);
        TriggerComboPulse();
        RefreshSkinGameState();
        _skinRuntime.OnComboChanged(_combo);
    }

    private void ProcessEarlyPressMisses(IReadOnlyList<ChartEvent> ordered, double currentSeconds, HashSet<string> consumedPressedKeys, GearState frameGameplayState)
    {
        if (_tempo is null)
            return;

        const double earlyWindow = 0.150;
        var pressedActions = new[] { "game_left_1", "game_left_2", "game_left_3", "game_right_1", "game_right_2", "game_right_3" }
            .Where(KeybindSettings.IsPressed)
            .Where(action => !consumedPressedKeys.Contains(action))
            .ToArray();
        if (pressedActions.Length == 0)
            return;

        foreach (var action in pressedActions)
        {
            ChartEvent? ev = null;
            var first = LowerBound(_eventSeconds, currentSeconds + GameplayHitWindowSeconds + 1e-9);
            var last = UpperBound(_eventSeconds, currentSeconds + earlyWindow + 1e-9);
            for (var i = first; i < last; i++)
            {
                var candidate = ordered[i];
                if (!IsActiveEvent(candidate, frameGameplayState) || IsHit(candidate))
                    continue;
                var candidateKey = KeyOf(candidate);
                if (_missedEvents.Contains(candidateKey) || _gameplayIgnoredEvents.Contains(candidateKey) ||
                    candidate.IsSuperFlick || candidate.NoteType is NoteType.End or NoteType.LinkTail)
                    continue;
                if (!string.Equals(KeybindSettings.GetActionForTrack(candidate.Track), action, StringComparison.OrdinalIgnoreCase))
                    continue;
                ev = candidate;
                break;
            }

            if (ev is null)
                continue;
            if (ev.IsSuperFlick)
            {
                MarkSuperFlickVisualMiss(ordered, ev, _missedEvents);
                RegisterMiss("early-superFlick", ev);
                continue;
            }

            if (ev.NoteType is NoteType.SingleLong or NoteType.Vibrato or NoteType.LinkHead)
            {
                var runtime = GetOrCreateHoldRuntime(ev);
                MissHoldStart(runtime);
            }
            else
            {
                MissEvent(ev);
            }
        }
    }


    private HoldRuntime GetOrCreateHoldRuntime(ChartEvent root)
    {
        var key = KeyOf(root);
        if (_holdStates.TryGetValue(key, out var existing))
            return existing;

        var runtime = new HoldRuntime { Root = root };
        runtime.ChainNodes.AddRange(BuildHoldChain(root));
        _holdStates[key] = runtime;
        return runtime;
    }

    private List<ChartEvent> BuildHoldChain(ChartEvent root)
    {
        if (root.NoteType == NoteType.LinkHead &&
            _linkChainCache.TryGetValue(KeyOf(root), out var cached))
            return cached;

        // Non-LinkHead callers are rare and are not part of the DrawNotes hot path.
        // Keep the old single-node behavior for those callers.
        var nodes = new List<ChartEvent>(1) { root };
        if (_chart is null || root.NoteType != NoteType.LinkHead)
            return nodes;

        var visited = new HashSet<ChartEventKey> { KeyOf(root) };
        var current = root;
        var guard = 0;
        while (guard++ < 256)
        {
            var nextKey = KeyOf(current);
            if (!_nextLinkByHeadKey.TryGetValue(nextKey, out var next))
                break;
            var tail = next.Tail;
            if (!visited.Add(KeyOf(tail)))
                break;
            nodes.Add(tail);
            current = tail;
        }

        _linkChainCache[KeyOf(root)] = nodes;
        _linkChainEffectiveEndTickCache[KeyOf(root)] = nodes.Count > 1
            ? (nodes[^1].Duration > 6 ? nodes[^1].EndTick : nodes[^1].Tick)
            : (root.Duration > 6 ? root.EndTick : root.Tick);
        return nodes;
    }

    private int GetHoldEndTick(ChartEvent root)
    {
        var runtime = _holdStates.TryGetValue(KeyOf(root), out var existing) ? existing : GetOrCreateHoldRuntime(root);
        if (runtime.ChainNodes.Count > 1)
        {
            var last = runtime.ChainNodes[^1];
            return last.Duration > 6 ? last.EndTick : last.Tick;
        }
        return root.EndTick;
    }

    private int RequiredHoldTrack(HoldRuntime runtime, double currentTick)
    {
        var track = runtime.Root.Track;
        var chainNodes = runtime.ChainNodes;
        for (var i = 1; i < chainNodes.Count; i++)
        {
            var node = chainNodes[i];
            if (node.Tick <= currentTick)
                track = node.Track;
            else
                break;
        }
        return track;
    }

    private void StartHold(HoldRuntime runtime, double deltaSeconds)
    {
        runtime.Started = true;
        runtime.StartSeconds = _playSeconds;
        runtime.StartOffsetMs = deltaSeconds * 1000.0;
        runtime.CurrentNodeIndex = 0;
        runtime.CurrentInputTrackOverride = null;
        runtime.InputCorrectionDeadlineSeconds = null;
        runtime.AllowUnheldUntilNextTailWindow = false;
        runtime.StartJudgement = RegisterHit(runtime.StartOffsetMs, runtime.Root);
        // Reserve the terminal hold-tick checkpoint. Until the hold completes or
        // drops, it is treated as an unresolved 100% result by GetAccuracyPercent().
        ReserveHoldAccuracy(runtime);
        runtime.NextComboTick = runtime.Root.Tick + Math.Max(1.0, (_tempo?.TicksPerBeat ?? 48.0) / 4.0);
        runtime.NextTailSoundIndex = 1;
        _hitEvents.Add(KeyOf(runtime.Root));
        PlayHitSound(runtime.Root);
    }

    private void ReserveHoldAccuracy(HoldRuntime runtime)
    {
        if (runtime.AccuracyCompletionReserved || _tempo is null)
            return;

        if (!_scoreHoldTickEntries.TryGetValue(KeyOf(runtime.Root), out var entries) ||
            entries.Count == 0)
            return;

        // The terminal checkpoint is represented by the final hold-tick score
        // entry, so it uses exactly the same reduced weight as every other hold
        // tick instead of reserving a separate full-weight completion.
        var terminalEntry = entries[^1];
        var weight = terminalEntry.AccuracyWeight;
        if (weight <= 0.0)
            return;

        runtime.AccuracyCompletionReserved = true;
        runtime.AccuracyCompletionWeight = weight;
    }

    private void ResolveReservedHoldAccuracy(HoldRuntime runtime, double value, JudgementTier? tierOverride = null, double? offsetOverride = null)
    {
        if (!runtime.AccuracyCompletionReserved || runtime.AccuracyCompletionResolved)
            return;

        var weight = runtime.AccuracyCompletionWeight;
        runtime.AccuracyCompletionResolved = true;

        var tier = tierOverride ?? (value >= 0.5 ? JudgementTier.Perfect : JudgementTier.Miss);
        var offsetMs = offsetOverride ?? runtime.StartOffsetMs;
        IncrementJudgementCount(tier);
        _accuracySum += AccuracyFromJudgement(tier, offsetMs, PerfectPlusWindowMs, GoodWindowMs) * Math.Clamp(value, 0.0, 1.0) * weight;
        _accuracyResolvedWeight += weight;
    }

    private void CompleteHold(HoldRuntime runtime)
    {
        if (runtime.Completed || runtime.Dropped)
            return;

        runtime.Completed = true;

        // The terminal checkpoint inherits the hold's starting judgement. This
        // keeps hold/link judgement counts consistent: a Perfect+ hold remains
        // Perfect+ through its terminal end, and a Good hold ends as Good rather
        // than silently creating a Perfect checkpoint. The terminal itself is
        // represented by the final HoldTick score entry, so its score and accuracy
        // contribution use the same 0.25 hold-tick weight as every other tick.
        ShowJudgement(runtime.StartJudgement);
        ResolveReservedHoldAccuracy(
            runtime,
            1.0,
            runtime.StartJudgement,
            runtime.StartOffsetMs);

        AwardHoldTickScore(runtime, runtime.NextScoreTickIndex, runtime.StartJudgement);

        // The terminal hold checkpoint is a real judgement, so it must also
        // advance combo just like the hold head and every hold tick. This keeps
        // the combo count and judgement count in sync for a fully completed hold.
        _combo++;
        _maxCombo = Math.Max(_maxCombo, _combo);
        TriggerComboPulse();
        RefreshSkinGameState();
        _skinRuntime.OnComboChanged(_combo);

        ReleaseHoldHitAnimations(KeyOf(runtime.Root), completedNormally: true);
    }

    private void ReleaseHoldHitAnimations(ChartEventKey rootKey, bool completedNormally = false)
    {
        foreach (var animation in _hitAnimations)
        {
            if (!animation.IsHold || animation.HoldRootKey != rootKey)
                continue;

            // Link hit effects belong to the live chain body. When the chain is
            // dropped, the body effect must vanish immediately instead of finishing
            // its authored/loop duration. Regular hold effects retain their existing
            // release behavior and play out their non-looping ending frames.
            if (animation.IsLinkAnimation)
            {
                // A normally completed link chain should let the terminal tail
                // finish the non-looping ending of the hold animation. Only the
                // live looping portion is owned by the chain while it is moving.
                // Dropping the chain still cancels link animations immediately.
                if (completedNormally && animation.IsTerminalLinkTail)
                {
                    if (!animation.PostLoopStartElapsedSeconds.HasValue)
                        animation.PostLoopStartElapsedSeconds = animation.ElapsedSeconds;
                    continue;
                }

                animation.Cancelled = true;
                continue;
            }

            if (!animation.PostLoopStartElapsedSeconds.HasValue)
                animation.PostLoopStartElapsedSeconds = animation.ElapsedSeconds;
        }
    }

    private void DropHold(HoldRuntime runtime)
    {
        if (runtime.Dropped || runtime.Completed)
            return;
        runtime.Dropped = true;
        ReleaseHoldHitAnimations(KeyOf(runtime.Root), completedNormally: false);
        runtime.DropSeconds = _playSeconds;
        runtime.DropTick = _tempo?.SecondsToTick(_playSeconds) ?? 0.0;
        _missedEvents.Add(KeyOf(runtime.Root));
        // The start checkpoint was already resolved when the hold was pressed.
        // A drop resolves the separately reserved hold-end checkpoint as a miss
        // instead of recording the start event a second time.
        RegisterMiss("hold-drop", runtime.Root, recordAccuracy: false);
        ResolveReservedHoldAccuracy(runtime, 0.0);
    }

    private void IgnoreUnavailableEvent(ChartEvent ev, double currentTick)
    {
        if (!_gameplayIgnoredEvents.Add(KeyOf(ev)))
            return;

        if (_superFlickDebugEnabled)
        {
            SuperFlickDebugLog($"JUDGEMENT IGNORE source=inactive-until-expired eventTick={ev.Tick} track={ev.Track} type={ev.NoteType} currentTick={currentTick:0.###} gear={SuperFlickDebugGearState()}");
        }
    }

    private void MissHoldStart(HoldRuntime runtime)
    {
        if (runtime.StartMissed || runtime.Started || runtime.Dropped || runtime.Completed)
            return;
        runtime.StartMissed = true;
        _missedEvents.Add(KeyOf(runtime.Root));
        RegisterMiss("hold-start-miss", runtime.Root);
    }

    private void MissEvent(ChartEvent ev)
    {
        if (_missedEvents.Add(KeyOf(ev)))
            RegisterMiss("note-timeout", ev);
    }

    private Dictionary<int, int> SimultaneousCounts(GearState currentState)
    {
        if (_chart is null || _gear is null)
            return new Dictionary<int, int>();

        var key = _compact
            ? (GearTimeline.ActiveBankForSide(currentState, "left"), GearTimeline.ActiveBankForSide(currentState, "right"))
            : ("Expanded", "Expanded");

        if (_simultaneousCountsCache.TryGetValue(key, out var cached))
            return cached;

        var counts = new Dictionary<int, int>();
        foreach (var ev in _orderedRenderableEvents)
        {
            if ((_chart.PlayTick > 0 && ev.Tick > _chart.PlayTick) || ev.IsSuperFlick || ev.NoteType == NoteType.LinkTail)
                continue;
            if (_compact && !currentState.IsActive(ev.Track))
                continue;
            counts[ev.Tick] = counts.GetValueOrDefault(ev.Tick) + 1;
        }
        _simultaneousCountsCache[key] = counts;
        return counts;
    }

    private bool IsSimultaneous(ChartEvent ev, Dictionary<int, int> counts) =>
        !ev.IsSuperFlick && ev.NoteType != NoteType.LinkTail && counts.GetValueOrDefault(ev.Tick) >= 2;

    private static bool IsRenderableNoteEvent(ChartEvent ev)
        => ev.Kind == "Note" && ev.Track is >= 0 and <= 15 && GearState.Position(ev.Track) is not null && ev.NoteType != NoteType.End;

    private bool IsSuperFlickValidAtAuthoredRoute(ChartEvent ev)
        => ev.IsSuperFlick && (_gear?.IsActiveAt(ev) ?? false);

    private bool IsActiveEvent(ChartEvent ev, GearState? stateOverride = null)
    {
        if (!IsRenderableNoteEvent(ev))
            return false;
        if (_chartEditorMode)
        {
            var state = stateOverride ?? (_gameplayMode ? _gameplayState : EnsureEditorPreviewGearState());
            return ev.IsSuperFlick
                ? state.CanApplySuperFlick(ev.Attr / 10, ev.Track)
                : state.IsActive(ev.Track);
        }
        if (!_gameplayMode)
            return _gear?.IsActiveAt(ev) ?? false;

        var gameplayState = stateOverride ?? _gameplayState;
        return ev.IsSuperFlick
            ? gameplayState.CanApplySuperFlick(ev.Attr / 10, ev.Track)
            : gameplayState.IsActive(ev.Track);
    }

    private bool IsActiveAt(ChartEvent ev) => IsActiveEvent(ev);
}
