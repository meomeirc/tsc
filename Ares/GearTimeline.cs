namespace TSC.Ares;

public sealed class GearTimeline
{
    private readonly GearState _initial;
    private readonly List<(int Tick, GearState State)> _changes = new();
    // A route combination becomes possible only when two banks on opposite
    // sides are simultaneously active in a reachable gear state. Banks on the
    // same side are mutually exclusive and must never share a Link ID pair.
    // Once registered, a cross-side combination remains possible for the rest
    // of the chart. The earliest tick is enough because combinations persist.
    private readonly Dictionary<(string First, string Second), int> _possibleFamilyPairTicks = new();

    public GearTimeline(AresChart chart)
    {
        _initial = GearState.FromStartAttribute(chart.InitialGearAttr);
        RegisterActiveFamilyCombinations(_initial, int.MinValue);
        var state = _initial.Copy();
        var possibleRouteStates = new Dictionary<(int Left, int Right, int CenterLeft, int CenterRight), GearState>
        {
            [StateKey(_initial)] = _initial.Copy()
        };

        foreach (var tickGroup in chart.Events
                     .Where(e => e.IsSuperFlick)
                     .OrderBy(e => e.Tick)
                     .ThenBy(e => e.Track)
                     .ThenBy(e => e.EventIndex)
                     .GroupBy(e => e.Tick))
        {
            var tickEvents = tickGroup.ToList();

            // Link validation must account for every route the player can reach,
            // not just the route where every SuperFlick succeeds. Independent
            // transitions can be hit one side at a time, even on the same tick.
            // A same-tick 60/70 pair is the exception: it is either unchanged or
            // both sides change together, never through a one-sided intermediate.
            var nextPossibleRouteStates = new Dictionary<(int Left, int Right, int CenterLeft, int CenterRight), GearState>();
            foreach (var routeBefore in possibleRouteStates.Values)
            {
                var possibleChanges = SelectSuperFlickChangesForTick(routeBefore, tickEvents, tickEvents);
                var hasAtomicInnerPair = possibleChanges.Any(ev => (ev.Attr is 60 or 70) &&
                    string.Equals(ev.GetSuperFlickSide(), "left", StringComparison.OrdinalIgnoreCase)) &&
                    possibleChanges.Any(ev => (ev.Attr is 60 or 70) &&
                    string.Equals(ev.GetSuperFlickSide(), "right", StringComparison.OrdinalIgnoreCase));
                var atomicInnerEvents = hasAtomicInnerPair
                    ? possibleChanges.Where(ev => ev.Attr is 60 or 70).ToList()
                    : new List<ChartEvent>();
                var independentEvents = hasAtomicInnerPair
                    ? possibleChanges.Where(ev => ev.Attr is not (60 or 70)).ToList()
                    : possibleChanges;

                var independentMaskCount = 1 << independentEvents.Count;
                var atomicVariantCount = hasAtomicInnerPair ? 2 : 1;
                for (var atomicVariant = 0; atomicVariant < atomicVariantCount; atomicVariant++)
                for (var mask = 0; mask < independentMaskCount; mask++)
                {
                    var routeAfter = routeBefore.Copy();
                    if (atomicVariant == 1)
                    {
                        foreach (var ev in atomicInnerEvents)
                            routeAfter.ApplySuperFlick(ev.Attr / 10, ev.Track);
                    }

                    for (var i = 0; i < independentEvents.Count; i++)
                    {
                        if ((mask & (1 << i)) == 0)
                            continue;
                        var ev = independentEvents[i];
                        routeAfter.ApplySuperFlick(ev.Attr / 10, ev.Track);
                    }

                    // Only register families that can be active together after
                    // this tick. A source and destination bank on the same side
                    // are mutually exclusive, even across a hand-off tick.
                    RegisterActiveFamilyCombinations(routeAfter, tickGroup.Key);
                    nextPossibleRouteStates[StateKey(routeAfter)] = routeAfter;
                }
            }
            possibleRouteStates = nextPossibleRouteStates;

            // Keep the deterministic authored route for AtTick/IsActiveAt. All
            // valid authored transitions are applied, using the same atomic rule.
            var applied = ApplySuperFlickChangesForTick(state, tickEvents, tickEvents);
            if (applied.Count > 0)
                _changes.Add((tickGroup.Key, state.Copy()));
        }
    }

    public GearState Initial => _initial.Copy();

    /// <summary>
    /// Selects the SuperFlick events that may change the route at one chart tick.
    /// <paramref name="authoredTickEvents"/> describes every SuperFlick authored
    /// at the tick; <paramref name="candidateEvents"/> is the subset that is
    /// actually eligible to apply (for example, successfully hit events).
    ///
    /// Attr 60/70 events authored on both sides at the same tick form one atomic
    /// transition. Both sides must have an eligible event, otherwise neither
    /// side changes. When only one side has an Attr 60/70 event at that tick, it
    /// remains independent, which permits offset left/right transitions. Attr
    /// 20/40 and 80/90 are always independent of opposite-side timing.
    /// </summary>
    public static List<ChartEvent> SelectSuperFlickChangesForTick(
        GearState state,
        IEnumerable<ChartEvent> authoredTickEvents,
        IEnumerable<ChartEvent> candidateEvents)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(authoredTickEvents);
        ArgumentNullException.ThrowIfNull(candidateEvents);

        static bool IsInner(ChartEvent ev) => ev.Attr is 60 or 70;
        static string? SideOf(ChartEvent ev) => ev.GetSuperFlickSide();
        static IOrderedEnumerable<ChartEvent> Order(IEnumerable<ChartEvent> events) => events
            .Where(ev => ev.IsSuperFlick)
            .OrderBy(ev => ev.Track)
            .ThenBy(ev => ev.EventIndex);

        var authored = Order(authoredTickEvents).ToArray();
        var candidates = Order(candidateEvents).Distinct().ToArray();
        var before = state.Copy();
        var selected = new List<ChartEvent>(2);
        var usedSides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var hasLeftInner = authored.Any(ev => IsInner(ev) &&
            string.Equals(SideOf(ev), "left", StringComparison.OrdinalIgnoreCase));
        var hasRightInner = authored.Any(ev => IsInner(ev) &&
            string.Equals(SideOf(ev), "right", StringComparison.OrdinalIgnoreCase));

        if (hasLeftInner && hasRightInner)
        {
            var left = candidates.FirstOrDefault(ev => IsInner(ev) &&
                string.Equals(SideOf(ev), "left", StringComparison.OrdinalIgnoreCase) &&
                before.CanApplySuperFlick(ev.Attr / 10, ev.Track));
            var right = candidates.FirstOrDefault(ev => IsInner(ev) &&
                string.Equals(SideOf(ev), "right", StringComparison.OrdinalIgnoreCase) &&
                before.CanApplySuperFlick(ev.Attr / 10, ev.Track));

            // Do not apply just one half of a same-tick inner transition.
            if (left is not null && right is not null)
            {
                selected.Add(left);
                selected.Add(right);
                usedSides.Add("left");
                usedSides.Add("right");
            }
        }
        else
        {
            // No same-tick opposite-side partner: process offset/lone 60/70
            // transitions independently, one transition per side.
            foreach (var ev in candidates.Where(IsInner))
            {
                var side = SideOf(ev);
                if (side is null || usedSides.Contains(side) ||
                    !before.CanApplySuperFlick(ev.Attr / 10, ev.Track))
                    continue;
                selected.Add(ev);
                usedSides.Add(side);
            }
        }

        // Outer SuperFlick families (including 20/40) do not participate in the
        // 60/70 pairing rule. They continue to resolve independently per side.
        foreach (var ev in candidates.Where(ev => !IsInner(ev)))
        {
            var side = SideOf(ev);
            if (side is null || usedSides.Contains(side) ||
                !before.CanApplySuperFlick(ev.Attr / 10, ev.Track))
                continue;
            selected.Add(ev);
            usedSides.Add(side);
        }

        return selected
            .OrderBy(ev => ev.Track)
            .ThenBy(ev => ev.EventIndex)
            .ToList();
    }

    /// <summary>Applies the selected events for one tick as a single route update.</summary>
    public static List<ChartEvent> ApplySuperFlickChangesForTick(
        GearState state,
        IEnumerable<ChartEvent> authoredTickEvents,
        IEnumerable<ChartEvent> candidateEvents)
    {
        var selected = SelectSuperFlickChangesForTick(state, authoredTickEvents, candidateEvents);
        var applied = new List<ChartEvent>(selected.Count);
        foreach (var ev in selected)
        {
            if (state.ApplySuperFlick(ev.Attr / 10, ev.Track))
                applied.Add(ev);
        }
        return applied;
    }

    // Returns whether these two Link families can be active together in a
    // reachable route state by the end of the supplied exclusive tick. Banks
    // on the same side are mutually exclusive and can always reuse an ID pair.
    public bool FamiliesCanSharePair(string? familyA, string? familyB, int endTickExclusive)
    {
        if (familyA is null || familyB is null ||
            string.Equals(familyA, familyB, StringComparison.Ordinal))
            return true;

        // Each side can display exactly one bank at a time. For example,
        // Left 1K, Left 2K, and Left 3K can never be part of one simultaneous
        // route combination, even if SuperFlicks can transition between them.
        var sideA = GetFamilySide(familyA);
        var sideB = GetFamilySide(familyB);
        if (sideA is not null && string.Equals(sideA, sideB, StringComparison.Ordinal))
            return false;

        var key = MakeFamilyPairKey(familyA, familyB);
        return _possibleFamilyPairTicks.TryGetValue(key, out var registeredTick) &&
               registeredTick < endTickExclusive;
    }

    public IEnumerable<int> ChangeTicksBetween(int startInclusive, int endExclusive)
    {
        foreach (var change in _changes)
        {
            if (change.Tick < startInclusive)
                continue;
            if (change.Tick >= endExclusive)
                yield break;
            yield return change.Tick;
        }
    }

    public GearState AtTick(int tick, bool includeTickChanges = true)
    {
        var lo = 0;
        var hi = _changes.Count;

        // Upper-bound search: <= tick when including changes at this tick,
        // < tick when evaluating the superFlick itself before it takes effect.
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            var changeTick = _changes[mid].Tick;
            var take = includeTickChanges ? changeTick <= tick : changeTick < tick;

            if (take)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo == 0 ? _initial.Copy() : _changes[lo - 1].State.Copy();
    }

    public bool IsActiveAt(ChartEvent ev)
    {
        var state = AtTick(ev.Tick, includeTickChanges: !ev.IsSuperFlick);
        return ev.IsSuperFlick
            ? state.CanApplySuperFlick(ev.Attr / 10, ev.Track)
            : state.IsActive(ev.Track);
    }

    private static (int Left, int Right, int CenterLeft, int CenterRight) StateKey(GearState state)
        => (state.Left, state.Right, state.CenterLeft, state.CenterRight);

    private void RegisterActiveFamilyCombinations(GearState state, int tick)
    {
        var activeFamilies = new List<string>(2);
        var left = GetActiveFamilyForSide(state, "left");
        var right = GetActiveFamilyForSide(state, "right");
        if (left is not null)
            activeFamilies.Add(left);
        if (right is not null)
            activeFamilies.Add(right);

        for (var i = 0; i < activeFamilies.Count; i++)
        for (var j = i + 1; j < activeFamilies.Count; j++)
            RegisterFamilyPair(activeFamilies[i], activeFamilies[j], tick);
    }

    private static string? GetFamilySide(string family)
    {
        if (family.EndsWith("Left", StringComparison.OrdinalIgnoreCase))
            return "left";
        if (family.EndsWith("Right", StringComparison.OrdinalIgnoreCase))
            return "right";
        return null;
    }

    private static string? GetActiveFamilyForSide(GearState state, string side)
    {
        int track;
        if (side == "left")
        {
            track = state.CenterLeft == GearSide.CenterLeft ? 12 :
                state.Left == GearSide.LeftForward ? 0 :
                state.Left == GearSide.LeftBackward ? 6 : -1;
        }
        else
        {
            track = state.CenterRight == GearSide.CenterRight ? 14 :
                state.Right == GearSide.RightForward ? 3 :
                state.Right == GearSide.RightBackward ? 9 : -1;
        }

        return track >= 0 ? LinkBuilder.Family(track) : null;
    }

    private void RegisterFamilyPair(string? familyA, string? familyB, int tick)
    {
        if (familyA is null || familyB is null ||
            string.Equals(familyA, familyB, StringComparison.Ordinal))
            return;

        var key = MakeFamilyPairKey(familyA, familyB);
        if (!_possibleFamilyPairTicks.TryGetValue(key, out var existingTick) || tick < existingTick)
            _possibleFamilyPairTicks[key] = tick;
    }

    private static (string First, string Second) MakeFamilyPairKey(string familyA, string familyB)
        => string.CompareOrdinal(familyA, familyB) <= 0
            ? (familyA, familyB)
            : (familyB, familyA);

    public static string ActiveBankForSide(GearState state, string side)
    {
        if (side == "left")
        {
            if (state.CenterLeft == GearSide.CenterLeft) return "Center";
            if (state.Left == GearSide.LeftForward) return "Forward";
            if (state.Left == GearSide.LeftBackward) return "Backward";
        }
        else
        {
            if (state.CenterRight == GearSide.CenterRight) return "Center";
            if (state.Right == GearSide.RightForward) return "Forward";
            if (state.Right == GearSide.RightBackward) return "Backward";
        }
        return "Inactive";
    }
}
