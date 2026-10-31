namespace TSC.Ares;

public sealed class GearTimeline
{
    private readonly GearState _initial;
    private readonly List<(int Tick, GearState State)> _changes = new();
    // A route combination becomes possible when it is present in the starting
    // gear, appears in a later reachable gear state, or is crossed by a valid
    // SuperFlick transition. Once registered, it remains possible for the rest
    // of the chart. The earliest tick is enough because combinations persist.
    private readonly Dictionary<(string First, string Second), int> _possibleFamilyPairTicks = new();

    public GearTimeline(AresChart chart)
    {
        _initial = GearState.FromStartAttribute(chart.InitialGearAttr);
        RegisterActiveFamilyCombinations(_initial, int.MinValue);
        var state = _initial.Copy();
        foreach (var tickGroup in chart.Events
                     .Where(e => e.IsSuperFlick)
                     .OrderBy(e => e.Tick)
                     .ThenBy(e => e.Track)
                     .ThenBy(e => e.EventIndex)
                     .GroupBy(e => e.Tick))
        {
            // Every event at a tick is evaluated against the route at the start
            // of that tick. Apply at most one transition per side so duplicate
            // records cannot cascade through multiple modes in one tick.
            var before = state.Copy();
            var appliedSides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var changed = false;
            foreach (var ev in tickGroup)
            {
                var side = ev.GetSuperFlickSide();
                if (side is null || appliedSides.Contains(side) ||
                    !before.CanApplySuperFlick(ev.Attr / 10, ev.Track))
                    continue;

                appliedSides.Add(side);
                var transitionBefore = state.Copy();
                if (!state.ApplySuperFlick(ev.Attr / 10, ev.Track))
                    continue;

                changed = true;
                RegisterTransitionFamilyCombination(transitionBefore, state, side, tickGroup.Key);
            }
            if (changed)
            {
                // Both sides can transition on the same tick. Register only the
                // final state as a simultaneous combination; intermediate states
                // within that tick are not separate reachable chart states.
                RegisterActiveFamilyCombinations(state, tickGroup.Key);
                _changes.Add((tickGroup.Key, state.Copy()));
            }
        }
    }

    public GearState Initial => _initial.Copy();

    // Returns whether these two Link families have ever become a possible
    // route combination by the end of the supplied exclusive tick. A direct
    // SuperFlick transition registers its source/destination banks even though
    // those two banks are not active at the same instant; that pairing then
    // remains valid for the rest of the chart.
    public bool FamiliesCanSharePair(string? familyA, string? familyB, int endTickExclusive)
    {
        if (familyA is null || familyB is null ||
            string.Equals(familyA, familyB, StringComparison.Ordinal))
            return true;

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

    private void RegisterTransitionFamilyCombination(
        GearState before, GearState after, string side, int tick)
    {
        var sourceFamily = GetActiveFamilyForSide(before, side);
        var destinationFamily = GetActiveFamilyForSide(after, side);
        RegisterFamilyPair(sourceFamily, destinationFamily, tick);
    }

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
