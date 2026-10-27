namespace TSC.Ares;

internal static class CallbackReconstructor
{
    private readonly record struct Signature(int Track, int Attr, int Duration);

    public static void Reconstruct(AresChart chart)
    {
        try
        {
            // ARES/B2 callback streams store a future copy of the playable
            // pattern at large raw-tick intervals.  Prefer the deterministic
            // callback fold when that stream is present; fall back to the
            // older heuristic for charts without this layout.
            if (TryReconstructCallbackWindow(chart))
                return;

            ReconstructLegacy(chart);
        }
        catch
        {
            // Keep the old loader behavior: failed reconstruction must never
            // prevent the raw chart from loading.
        }
    }

    private static bool TryReconstructCallbackWindow(AresChart chart)
    {
        // Callback copies are laid out as groups of events in raw tick space.
        // The raw distance between groups is not perfectly constant (the files
        // we have seen vary by a few ticks), so mapping every event with
        //   target = seed + q * step + rawRemainder
        // accumulates timing drift.  Instead, preserve the exact spacing inside
        // each callback group and snap the group itself to the fixed playable
        // half-measure step.
        if (chart.PlayTick <= 0)
            return false;

        // The callback stream can also contain the character-panel events on
        // tracks 17-20. They use the same raw callback clock as the playable
        // tracks, so they must be folded through the exact same mapping or
        // late panel animations will remain stranded at huge raw ticks.
        var callbackTracks = chart.Tracks
            .Where(t => t.TrackIndex < 16 || (t.TrackIndex >= 17 && t.TrackIndex <= 20))
            .ToList();
        var playableTracks = chart.Tracks
            .Where(t => t.TrackIndex < 16)
            .ToList();
        var playableEvents = playableTracks
            .SelectMany(t => t.Events.Where(e => e.Kind == "Note"))
            .ToList();

        if (playableEvents.Count == 0)
            return false;

        var lastNativeTick = playableEvents
            .Where(e => e.Tick <= chart.PlayTick)
            .Select(e => e.Tick)
            .DefaultIfEmpty(-1)
            .Max();
        if (lastNativeTick < 0)
            return false;

        // The callback stream uses a separate slot clock. A normal raw callback
        // period is about 0x4000 (16384) raw ticks, but that period is NOT itself
        // a fixed playable distance. For TPM=192, one nominal callback slot is
        // 64 playable ticks, giving three callback slots per musical measure.
        // Small storage jitter changes the raw phase slightly, so we retain each
        // group's actual phase instead of forcing it onto a nominal phase table.
        // The seed's absolute musical phase is also retained instead of being
        // subtracted away when the first group is normalized.

        var postEvents = callbackTracks
            .SelectMany(t => t.Events.Where(e => e.Kind == "Note" && e.Tick > chart.PlayTick))
            .OrderBy(e => e.Tick)
            .ThenBy(e => e.Track)
            .ThenBy(e => e.EventIndex)
            .ToList();
        if (postEvents.Count < 3)
            return false;

        // Events belonging to one callback invocation are close together in
        // raw tick space. Between callback invocations there is a very large
        // gap. Group on that gap instead of assuming the raw callback period
        // is exactly 0x4000 or any other fixed number.
        var groupGapThreshold = 1000;
        var groups = new List<List<ChartEvent>>();
        List<ChartEvent>? current = null;
        var previousTick = int.MinValue;
        foreach (var ev in postEvents)
        {
            if (current is null || ev.Tick - previousTick > groupGapThreshold)
            {
                current = new List<ChartEvent>();
                groups.Add(current);
            }

            current.Add(ev);
            previousTick = ev.Tick;
        }

        // Require a stable callback stream rather than an isolated far-future row.
        if (groups.Count < 3)
            return false;

        var usefulGroups = groups.ToList();
        if (usefulGroups.Count < 2)
            return false;

        // A real callback stream should have similarly-sized groups and very
        // large raw gaps between their starts. Small raw-period drift is a
        // storage detail. Large gaps are meaningful: they represent one or
        // more EMPTY callback slots and therefore must survive reconstruction.
        var groupStarts = usefulGroups.Select(g => g[0].Tick).ToList();
        var rawGaps = groupStarts.Zip(groupStarts.Skip(1), (a, b) => b - a)
            .Where(g => g > groupGapThreshold)
            .ToList();
        if (rawGaps.Count < 2)
            return false;

        var expectedRawPeriod = 16384.0;
        var oneSlotGaps = rawGaps
            .Where(g => Math.Abs(g - expectedRawPeriod) <= expectedRawPeriod * 0.04)
            .OrderBy(g => g)
            .ToList();
        var periodCandidates = oneSlotGaps.Count >= 2 ? oneSlotGaps : rawGaps;
        var medianRawGap = periodCandidates[periodCandidates.Count / 2];
        if (medianRawGap < expectedRawPeriod * 0.25 || medianRawGap > expectedRawPeriod * 4.0)
            return false;

        // The raw callback stream runs on a nominal 0x4000 (16384) tick
        // quantum.  The stored group starts can drift slightly around that
        // value (for example 16380/16392), so do not derive the musical slot
        // count from the measured gap itself.  Doing that turns 16380 into a
        // 60-tick phase step and incorrectly creates a 16-slot measure.
        //
        // With the nominal callback quantum:
        //   16384 mod 192 = 64
        //   192 / gcd(192, 16384) = 3 callback slots per measure.
        // The exact raw remainder of each group is still preserved below, so
        // irregular positions such as G8 phase 144 stay intact.
        var ticksPerMeasure = Math.Max(1, chart.TicksPerMeasure);
        const int nominalCallbackPeriod = 0x4000;
        var callbackPhaseStep = PositiveModulo(nominalCallbackPeriod, ticksPerMeasure);
        if (callbackPhaseStep == 0)
            return false;

        var slotsPerMeasure = ticksPerMeasure / Gcd(ticksPerMeasure, nominalCallbackPeriod);
        if (slotsPerMeasure <= 0)
            return false;

        // The raw callback clock advances by 0x4000, while the playable clock
        // advances by the phase increment (64 ticks for TPM=192). Therefore each
        // callback copy is displaced by the effective callback quantum only:
        //
        //   target = raw - (slot + 1) * (0x4000 - phaseStep)
        //
        // Do not add rawSeedPhase here. The low-order raw offset is already the
        // authored musical offset inside the callback group. For example, the
        // letup callback seed 32844 maps to 16524 exactly.
        var callbackTargetStep = Math.Max(1, ticksPerMeasure / slotsPerMeasure);
        var callbackTargetQuantum = nominalCallbackPeriod - callbackPhaseStep;
        var rawSeed = usefulGroups[0][0].Tick;
        var rawSeedTarget = rawSeed - callbackTargetQuantum;
        if (rawSeedTarget >= chart.PlayTick)
            return false;

        // Require several groups to actually contain events. This keeps random
        // far-future note data from being mistaken for a callback stream. The
        // same groups are then used for both playable notes and panel tracks 17-20.
        var populated = usefulGroups.Count(g => g.Count > 0);
        if (populated < Math.Min(3, usefulGroups.Count))
            return false;

        var byTrack = callbackTracks.ToDictionary(t => t.TrackIndex);
        var existing = new HashSet<(int Track, int Tick)>(
            callbackTracks.SelectMany(t => t.Events.Where(e => e.Kind == "Note")).Select(e => (e.Track, e.Tick)));
        var usedSlots = new HashSet<int>();

        var generated = 0;
        for (var groupIndex = 0; groupIndex < usefulGroups.Count; groupIndex++)
        {
            var group = usefulGroups[groupIndex];
            var rawGroupStart = group[0].Tick;

            // Recover the callback slot number from the coarse raw cadence.
            // The raw phase stays intact; only the large callback-clock advance
            // is removed. This also preserves missing callback slots naturally.
            var rawDelta = rawGroupStart - rawSeed;
            var slotIndex = Math.Max(0, (int)Math.Round(
                (double)rawDelta / nominalCallbackPeriod,
                MidpointRounding.ToEven));
            var targetGroupStart = rawGroupStart - (slotIndex + 1) * callbackTargetQuantum;

            if (targetGroupStart > chart.PlayTick)
                break;

            foreach (var source in group)
            {
                // The low-order offsets inside a callback group are already in
                // playable tick units (the observed values are 12/24/36/48/60).
                // Do NOT divide these by the raw callback scale.
                var localOffset = source.Tick - rawGroupStart;
                var targetTick = targetGroupStart + localOffset;
                if (targetTick < rawSeedTarget || targetTick > chart.PlayTick)
                    continue;
                if (!byTrack.TryGetValue(source.Track, out var track))
                    continue;
                if (!existing.Add((source.Track, targetTick)))
                    continue;

                track.Events.Add(Clone(source, targetTick, track, "callback"));
                generated++;
            }
        }

        if (generated == 0)
            return false;

        chart.MainLoopDetected = true;
        chart.MainLoopGenerated = true;
        chart.MainLoopGeneratedEvents += generated;
        chart.MainLoopSourceTick = groupStarts[0];
        chart.MainLoopTargetTick = rawSeedTarget;
        chart.MainLoopEndTargetTick = chart.PlayTick;
        chart.LateCallbackDetected = true;
        chart.LateCallbackRawSeed = groupStarts[0];
        chart.LateCallbackTargetSeed = rawSeedTarget;
        chart.LateCallbackPeriod = medianRawGap;
        chart.LateCallbackTargetStep = callbackTargetStep;
        return true;
    }

    private static void ReconstructLegacy(AresChart chart)
    {
        try
        {
            var playTick = chart.PlayTick;
            if (playTick <= 0)
                return;

            // The playable callback slot is 64 ticks.  Keep this single value
            // shared by loop detection, callback mapping, and manual-island
            // protection so those paths cannot drift apart.
            const int targetStep = 64;

            var byTrack = chart.Tracks
                .Where(t => t.TrackIndex < 16)
                .ToDictionary(t => t.TrackIndex);
            var loopTracks = new HashSet<int>(Enumerable.Range(6, 6));
                string SignatureAt(int tick)
            {
                var sig = new List<Signature>();
                foreach (var ti in loopTracks.OrderBy(x => x))
                {
                    if (!byTrack.TryGetValue(ti, out var tr))
                        continue;
                    foreach (var ev in tr.Events)
                    {
                        if (ev.Kind == "Note" && ev.Tick == tick)
                            sig.Add(new Signature(ti, ev.Attr, ev.Duration));
                    }
                }
                return string.Join("|", sig.OrderBy(x => x.Track).ThenBy(x => x.Attr).ThenBy(x => x.Duration)
                    .Select(x => $"{x.Track}:{x.Attr}:{x.Duration}"));
            }

            var preTicks = loopTracks
                .SelectMany(ti => byTrack.TryGetValue(ti, out var tr)
                    ? tr.Events.Where(e => e.Kind == "Note" && e.Tick <= playTick).Select(e => e.Tick)
                    : Enumerable.Empty<int>())
                .Distinct().OrderBy(x => x).ToList();
            var postTicks = loopTracks
                .SelectMany(ti => byTrack.TryGetValue(ti, out var tr)
                    ? tr.Events.Where(e => e.Kind == "Note" && e.Tick > playTick).Select(e => e.Tick)
                    : Enumerable.Empty<int>())
                .Distinct().OrderBy(x => x).ToList();

            if (preTicks.Count == 0 || postTicks.Count == 0)
                return;

            // 1) Detect the ordinary repeated section from a matching run of
            //    pre-play and post-play fingerprints.
            int bestLength = 0;
            int bestSourceStart = 0;
            int bestPostIndex = 0;
            int bestSourceIndex = 0;
            for (var pi = 0; pi < Math.Min(16, postTicks.Count); pi++)
            {
                var postSignature = SignatureAt(postTicks[pi]);
                if (string.IsNullOrEmpty(postSignature))
                    continue;

                for (var si = 0; si < preTicks.Count; si++)
                {
                    if (SignatureAt(preTicks[si]) != postSignature)
                        continue;

                    var n = 0;
                    while (pi + n < postTicks.Count && si + n < preTicks.Count &&
                           SignatureAt(postTicks[pi + n]) == SignatureAt(preTicks[si + n]))
                        n++;

                    if (n >= 3 && n > bestLength)
                    {
                        bestLength = n;
                        bestSourceStart = preTicks[si];
                        bestPostIndex = pi;
                        bestSourceIndex = si;
                    }
                }
            }

            int? mainLoopSource = null;
            int? mainLoopTarget = null;
            int? mainLoopEnd = null;
            int? matchPostEnd = null;
            if (bestLength >= 3)
            {
                var targetStart = preTicks[^1] + targetStep;
                if (targetStart <= playTick)
                {
                    var sourceMatchEnd = preTicks[bestSourceIndex + bestLength - 1];
                    mainLoopSource = bestSourceStart;
                    mainLoopTarget = targetStart;
                    mainLoopEnd = Math.Min(playTick, targetStart + (sourceMatchEnd - bestSourceStart));
                    matchPostEnd = postTicks[bestPostIndex + bestLength - 1];
                }
            }

            chart.MainLoopDetected = mainLoopSource is not null;
            chart.MainLoopSourceTick = mainLoopSource;
            chart.MainLoopTargetTick = mainLoopTarget;
            chart.MainLoopEndTargetTick = mainLoopEnd;

            // 2) Infer the raw callback period and locate the first stable late
            //    callback anchor before generating anything.
            var rawPeriod = InferRawPeriod(postTicks);
            chart.LateCallbackPeriod = rawPeriod;
            chart.LateCallbackTargetStep = targetStep;

            int? lateSeedRaw = null;
            int? lateSeedTarget = null;
            if (postTicks.Count > 0)
            {
                var candidates = FindCallbackCandidates(chart, postTicks, targetStep);
                var selected = SelectCallbackAnchor(candidates, rawPeriod, targetStep);
                if (selected is null)
                {
                    var scan = postTicks.Where(t => matchPostEnd is null || t >= matchPostEnd.Value);
                    foreach (var rt in scan)
                    {
                        var sig = SignatureAt(rt);
                        var hasLong = sig.Split('|', StringSplitOptions.RemoveEmptyEntries)
                            .Select(x => x.Split(':'))
                            .Any(parts => parts.Length == 3 && int.TryParse(parts[2], out var d) && d > 6);
                        var following = postTicks.Where(t => t > rt).Take(3).ToList();
                        if (!hasLong || following.Count < 3 || following[2] - rt < rawPeriod * 3)
                            continue;
                        selected = rt;
                        break;
                    }
                }

                if (selected is not null)
                {
                    lateSeedRaw = selected.Value;
                    var callbackEpoch = rawPeriod - targetStep;
                    var q = Math.DivRem(lateSeedRaw.Value, rawPeriod, out var remainder);
                    lateSeedTarget = callbackEpoch + q * targetStep + remainder;
                }
            }

            chart.LateCallbackDetected = lateSeedRaw is not null;
            chart.LateCallbackRawSeed = lateSeedRaw;
            chart.LateCallbackTargetSeed = lateSeedTarget;

            // 3) Recreate the ordinary loop, stopping before the late callback
            //    seed and respecting authored/manual islands already occupying
            //    the destination area.
            if (mainLoopSource is not null && mainLoopTarget is not null)
            {
                var phaseEnd = mainLoopEnd ?? playTick;
                if (lateSeedTarget is not null)
                    phaseEnd = Math.Min(phaseEnd, lateSeedTarget.Value - 1);

                var nativeTargetTicks = chart.Tracks.Where(t => t.TrackIndex < 16)
                    .SelectMany(t => t.Events)
                    .Where(e => e.Kind == "Note" && !e.LoopReconstructed &&
                                e.Tick >= mainLoopTarget.Value - targetStep && e.Tick <= phaseEnd)
                    .Select(e => e.Tick).Distinct().OrderBy(x => x).ToList();

                var manualIslands = new List<(int Start, int End)>();
                if (nativeTargetTicks.Count > 0)
                {
                    var clusterStart = nativeTargetTicks[0];
                    var clusterPrev = nativeTargetTicks[0];
                    foreach (var nt in nativeTargetTicks.Skip(1))
                    {
                        if (nt - clusterPrev <= targetStep * 3)
                        {
                            clusterPrev = nt;
                            continue;
                        }
                        manualIslands.Add((
                            Math.Max(mainLoopTarget.Value, clusterStart - targetStep * 2),
                            Math.Min(phaseEnd, clusterPrev + targetStep * 2)));
                        clusterStart = clusterPrev = nt;
                    }
                    manualIslands.Add((
                        Math.Max(mainLoopTarget.Value, clusterStart - targetStep * 2),
                        Math.Min(phaseEnd, clusterPrev + targetStep * 2)));
                }

                var sourceEnd = mainLoopSource.Value + (phaseEnd - mainLoopTarget.Value) + 1;
                var sourceEvents = chart.Tracks.Where(t => t.TrackIndex < 16)
                    .SelectMany(t => t.Events.Where(e => e.Kind == "Note" &&
                                                         e.Tick >= mainLoopSource.Value && e.Tick < sourceEnd))
                    .ToList();
                var shift = mainLoopTarget.Value - mainLoopSource.Value;
                foreach (var source in sourceEvents)
                {
                    var targetTick = source.Tick + shift;
                    if (targetTick > playTick || targetTick > phaseEnd)
                        continue;
                    if (manualIslands.Any(x => targetTick >= x.Start && targetTick <= x.End))
                        continue;
                    if (!byTrack.TryGetValue(source.Track, out var tr))
                        continue;
                    if (tr.Events.Any(e => e.Kind == "Note" && e.Tick == targetTick))
                        continue;

                    tr.Events.Add(Clone(source, targetTick, tr, "main"));
                    chart.MainLoopGeneratedEvents++;
                }
            }

            // 4) Reconstruct callback-spread bridge links. These are especially
            //    important when a link head/twin tail are in one callback row and
            //    the final tail is in the next raw callback invocation.
            if (lateSeedRaw is not null && lateSeedTarget is not null && mainLoopTarget is not null && rawPeriod > 0)
            {
                var rawNotesByTrack = chart.Tracks.Where(t => t.TrackIndex < 16)
                    .ToDictionary(t => t.TrackIndex, t => t.Events.Where(e => e.Kind == "Note").ToList());
                var rawNotesByTick = rawNotesByTrack
                    .SelectMany(kv => kv.Value.Select(ev => (Tick: ev.Tick, Track: kv.Key, Event: ev)))
                    .GroupBy(x => x.Tick)
                    .ToDictionary(g => g.Key, g => g.ToList());

                int MapCallbackTick(int rawTick)
                {
                    var delta = rawTick - lateSeedRaw.Value;
                    var q = (int)Math.Round(delta / (double)rawPeriod, MidpointRounding.ToEven);
                    var remainder = delta - q * rawPeriod;
                    return lateSeedTarget.Value + q * targetStep + remainder;
                }

                var phaseEnd = chart.MainLoopEndTargetTick ?? playTick;
                phaseEnd = Math.Min(phaseEnd, lateSeedTarget.Value - 1);
                var gapLo = Math.Max(0, mainLoopTarget.Value - targetStep * 2);
                var gapHi = lateSeedTarget.Value - 1;
                var bridgeFamilies = new HashSet<string>();

                bool IsLinkHead(ChartEvent ev)
                    => ev.Kind == "Note" && !ev.IsSuperFlick && ((ev.Attr % 10) is 1 or 3 or 5 or 7);
                bool IsLinkTailForHead(ChartEvent ev, ChartEvent head)
                    => ev.Kind == "Note" && (ev.Attr % 10) == (head.Attr % 10) + 1;

                foreach (var headTick in rawNotesByTick.Keys.Where(t => t > playTick).OrderBy(t => t))
                {
                    foreach (var headRecord in rawNotesByTick[headTick].Where(x => IsLinkHead(x.Event)))
                    {
                        var headTrack = headRecord.Track;
                        var head = headRecord.Event;
                        var paired = rawNotesByTick[headTick].Any(x => x.Track != headTrack && IsLinkTailForHead(x.Event, head));
                        if (!paired || !rawNotesByTrack.TryGetValue(headTrack, out var headTrackEvents))
                            continue;

                        var finalTick = headTick + rawPeriod;
                        var final = headTrackEvents.FirstOrDefault(e => e.Tick == finalTick && IsLinkTailForHead(e, head));
                        if (final is null)
                            continue;

                        var mappedHead = MapCallbackTick(headTick);
                        var mappedFinal = MapCallbackTick(finalTick);
                        if (mappedFinal <= mappedHead || mappedFinal > playTick || mappedFinal < gapLo || mappedHead > gapHi)
                            continue;

                        var familyKey = $"{headTrack}:{headTick}:{finalTick}:{head.Attr % 10}";
                        if (!bridgeFamilies.Add(familyKey))
                            continue;

                        var selected = new List<(int Track, ChartEvent Event)>();
                        selected.AddRange(rawNotesByTick[headTick].Select(x => (x.Track, x.Event)));
                        if (rawNotesByTick.TryGetValue(finalTick, out var endRow))
                            selected.AddRange(endRow.Select(x => (x.Track, x.Event)));

                        selected.AddRange(headTrackEvents
                            .Where(e => e.Tick >= headTick - rawPeriod - targetStep * 2 && e.Tick < headTick && e.Duration <= targetStep)
                            .OrderByDescending(e => e.Tick).Take(2).Select(e => (headTrack, e)));

                        var seen = new HashSet<(int Track, int Tick, int EventIndex)>();
                        foreach (var (track, src) in selected)
                        {
                            if (!seen.Add((track, src.Tick, src.EventIndex)))
                                continue;
                            var targetTick = MapCallbackTick(src.Tick);
                            if (targetTick < gapLo || targetTick > gapHi || targetTick > playTick)
                                continue;
                            if (!byTrack.TryGetValue(track, out var tr) || tr.Events.Any(e => e.Kind == "Note" && e.Tick == targetTick))
                                continue;
                            tr.Events.Add(Clone(src, targetTick, tr, "bridge"));
                            chart.MainLoopBridgeGeneratedEvents++;
                        }
                    }
                }

                // 5) Fold raw callback events in both directions around the
                //    selected late seed. Native destination events win.
                var existingFolded = new HashSet<(int Track, int Tick)>(
                    chart.Tracks.Where(t => t.TrackIndex < 16)
                        .SelectMany(t => t.Events.Where(e => e.Kind == "Note").Select(e => (t.TrackIndex, e.Tick))));

                var rawPostEvents = chart.Tracks.Where(t => t.TrackIndex < 16)
                    .SelectMany(t => t.Events.Where(e => e.Kind == "Note" && e.Tick > playTick)
                        .Select(e => (RawTick: e.Tick, Track: t.TrackIndex, Event: e)))
                    .OrderBy(x => x.RawTick).ThenBy(x => x.Track).ThenBy(x => x.Event.EventIndex)
                    .ToList();

                foreach (var (rawTick, track, src) in rawPostEvents)
                {
                    var delta = rawTick - lateSeedRaw.Value;
                    var q = (int)Math.Round(delta / (double)rawPeriod, MidpointRounding.ToEven);
                    var remainder = delta - q * rawPeriod;
                    var targetTick = lateSeedTarget.Value + q * targetStep + remainder;
                    if (targetTick < mainLoopTarget.Value || targetTick > playTick)
                        continue;

                    var key = (track, targetTick);
                    if (!existingFolded.Add(key) || !byTrack.TryGetValue(track, out var tr))
                        continue;
                    tr.Events.Add(Clone(src, targetTick, tr, targetTick < lateSeedTarget.Value ? "late_backfill" : "late"));
                    chart.MainLoopLateGeneratedEvents++;
                }
            }
        }
        catch
        {
            // Raw ARES decoding remains valid even when an unsupported private
            // callback variant is encountered. The already-recovered events are
            // still available to the renderer.
        }

        foreach (var track in chart.Tracks.Where(t => t.TrackIndex < 16))
            track.Events.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.EventIndex.CompareTo(b.EventIndex));
    }

    private static int PositiveModulo(int value, int modulus)
    {
        if (modulus <= 0)
            return 0;
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private static int Gcd(int a, int b)
    {
        a = Math.Abs(a);
        b = Math.Abs(b);
        while (b != 0)
        {
            var t = a % b;
            a = b;
            b = t;
        }
        return a;
    }

    private static int InferRawPeriod(List<int> postTicks)
    {
        var gaps = postTicks.Zip(postTicks.Skip(1), (a, b) => b - a)
            .Where(g => g is >= 1000 and <= 40000).ToList();
        if (gaps.Count == 0)
            return 16368;
        return gaps.GroupBy(x => x)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => Math.Abs(g.Key - 16368))
            .First().Key;
    }

    private static List<int> FindCallbackCandidates(AresChart chart, List<int> postTicks, int targetStep)
    {
        var result = new List<int>();
        foreach (var rt in postTicks)
        {
            var events = chart.Tracks.Where(t => t.TrackIndex < 16)
                .SelectMany(t => t.Events.Where(e => e.Kind == "Note" && e.Tick == rt))
                .ToList();
            if (events.Count < 3)
                continue;
            var longCount = events.Count(e => e.Duration >= Math.Max(48, targetStep));
            var maxDuration = events.Max(e => e.Duration);
            if (longCount >= 2 && maxDuration >= Math.Max(96, targetStep * 2))
                result.Add(rt);
        }
        return result;
    }

    private static int? SelectCallbackAnchor(List<int> candidates, int rawPeriod, int targetStep)
    {
        foreach (var rt in candidates)
        {
            for (var mult = 2; mult <= 8; mult++)
            {
                var expected = rt + mult * rawPeriod;
                if (candidates.Any(c => Math.Abs(c - expected) <= targetStep * 2))
                    return rt;
            }
        }
        return null;
    }

    private static ChartEvent Clone(ChartEvent source, int targetTick, ChartTrack targetTrack, string phase)
    {
        var nextIndex = targetTrack.Events.Count == 0 ? 0 : targetTrack.Events.Max(e => e.EventIndex) + 1;
        return new ChartEvent
        {
            Tick = targetTick,
            Track = source.Track,
            Attr = source.Attr,
            TypeIndex = source.TypeIndex,
            Duration = source.Duration,
            NoteType = source.NoteType,
            EventIndex = nextIndex,
            LinePair = source.LinePair,
            TwinPairId = source.TwinPairId,
            Kind = source.Kind,
            Value = source.Value,
            Instrument = source.Instrument,
            Velocity = source.Velocity,
            Pan = source.Pan,
            LoopReconstructed = true,
            LoopPhase = phase
        };
    }
}
