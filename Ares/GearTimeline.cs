namespace TSC.Ares;

public sealed class GearTimeline
{
    private readonly GearState _initial;
    private readonly List<(int Tick, GearState State)> _changes = new();

    public GearTimeline(AresChart chart)
    {
        _initial = GearState.FromStartAttribute(chart.InitialGearAttr);
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
                changed |= state.ApplySuperFlick(ev.Attr / 10, ev.Track);
            }
            if (changed)
                _changes.Add((tickGroup.Key, state.Copy()));
        }
    }

    public GearState Initial => _initial.Copy();

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
