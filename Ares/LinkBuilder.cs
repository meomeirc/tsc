namespace TSC.Ares;

public static class LinkBuilder
{
    public static void Build(AresChart chart)
    {
        chart.Links.Clear();

        var groups = chart.Events
            .Where(e => (e.NoteType is NoteType.LinkHead or NoteType.LinkTail) && e.LinePair >= 0)
            .GroupBy(e => (Family(e.Track), e.LinePair));

        foreach (var group in groups)
        {
            // The same-tick note types express direction: heads are sources and tails
            // are destinations. Do not use track number to infer path order.
            var nodes = group.OrderBy(e => e.Tick)
                .ThenBy(e => e.NoteType == NoteType.LinkHead ? 0 : 1)
                // At one tick, an existing short LinkTail is the prior point;
                // a longer tail is the horizontal-first continuation that follows
                // it. EventIndex is only a stable tie-break for equal durations.
                .ThenBy(e => e.Duration > 6 ? 1 : 0)
                .ThenByDescending(e => e.Duration)
                .ThenBy(e => e.EventIndex)
                .ToList();
            var pending = new Dictionary<int, ChartEvent>();
            (ChartEvent Head, ChartEvent Last, long Expiry)? active = null;

            // Native long-note and link-point durations are forward tick offsets:
            // a point at T with duration D ends at exactly T + D. Ordinary taps
            // keep their short metadata lifetime so they do not gain link reach.
            static long GetLinkExpiryTick(ChartEvent ev) =>
                ev.NoteType is NoteType.LinkHead or NoteType.LinkTail
                    ? ev.EndTick
                    : ev.Duration > 6
                        ? ev.EndTick
                        : (long)ev.Tick + Math.Max(1, ev.Duration - 1);

            for (var i = 0; i < nodes.Count; i++)
            {
                var ev = nodes[i];
                if (active.HasValue && ev.Tick > active.Value.Expiry)
                    active = null;

                if (ev.NoteType == NoteType.LinkHead)
                {
                    ChartEvent? sameTickTail = null;
                    if (ev.TwinPairId.HasValue && pending.Remove(ev.TwinPairId.Value, out var pairedTail))
                        sameTickTail = pairedTail;
                    else
                    {
                        var pendingCandidate = pending.FirstOrDefault(x => x.Value.Tick == ev.Tick);
                        if (pendingCandidate.Value is not null)
                        {
                            pending.Remove(pendingCandidate.Key);
                            sameTickTail = pendingCandidate.Value;
                        }
                    }

                    if (sameTickTail is not null)
                    {
                        chart.Links.Add(new LinkSegment(ev, sameTickTail));
                        active = (ev, sameTickTail, GetLinkExpiryTick(sameTickTail));
                    }
                    else
                    {
                        active = (ev, ev, GetLinkExpiryTick(ev));
                    }
                    continue;
                }

                if (active.HasValue)
                {
                    var state = active.Value;
                    if (state.Last.Tick != ev.Tick || state.Last.Track != ev.Track || state.Last.EventIndex != ev.EventIndex)
                    {
                        chart.Links.Add(new LinkSegment(state.Last, ev));
                        active = (state.Head, ev, GetLinkExpiryTick(ev));
                    }
                    continue;
                }

                if (ev.TwinPairId.HasValue)
                {
                    pending[ev.TwinPairId.Value] = ev;
                }
                else
                {
                    for (var j = i + 1; j < nodes.Count && nodes[j].Tick == ev.Tick; j++)
                    {
                        if (nodes[j].NoteType == NoteType.LinkHead)
                        {
                            pending[(ev.Tick << 20) ^ (ev.Track << 8) ^ ev.EventIndex] = ev;
                            break;
                        }
                    }
                }
            }
        }

        var unique = chart.Links
            .GroupBy(s => (s.Head.Tick, s.Head.Track, s.Head.EventIndex, s.Tail.Tick, s.Tail.Track, s.Tail.EventIndex))
            .Select(g => g.First())
            .OrderBy(s => s.Head.Tick).ThenBy(s => s.Tail.Tick).ToList();
        chart.Links.Clear();
        chart.Links.AddRange(unique);
    }

    public static string? Family(int track) => track switch
    {
        >= 0 and <= 2 => "ForwardLeft",
        >= 3 and <= 5 => "ForwardRight",
        >= 6 and <= 8 => "BackwardLeft",
        >= 9 and <= 11 => "BackwardRight",
        12 or 13 => "CenterLeft",
        14 or 15 => "CenterRight",
        _ => null
    };

    public static string FamilyLabel(string? family) => family switch
    {
        "ForwardLeft" => "Left 1K",
        "ForwardRight" => "Right 1K",
        "BackwardLeft" => "Left 3K",
        "BackwardRight" => "Right 3K",
        "CenterLeft" => "Left 2K",
        "CenterRight" => "Right 2K",
        _ => "Unknown bank"
    };
}
