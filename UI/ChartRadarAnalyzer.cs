using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TSC.Ares;

namespace TSC;

/// <summary>One radar axis, retaining both the measured rate and its display value.</summary>
internal readonly record struct ChartRadarMetric(double RawValue, double NormalizedValue, string Unit);

/// <summary>Chart-style measurements calculated from the selected difficulty's playable route.</summary>
internal readonly record struct ChartComboDistribution(int Taps, int Longs, int Slides, int Flicks)
{
    public int Total => Math.Max(0, Taps) + Math.Max(0, Longs) + Math.Max(0, Slides) + Math.Max(0, Flicks);
}

internal sealed record ChartRadarResult(
    double DurationSeconds,
    int ActiveEventCount,
    ChartRadarMetric Taps,
    ChartRadarMetric Longs,
    ChartRadarMetric Slides,
    ChartRadarMetric Flicks,
    ChartRadarMetric Stream,
    ChartRadarMetric Travel,
    ChartComboDistribution ComboDistribution);

/// <summary>
/// Computes the six experimental chart-style radar axes. This analyzer deliberately
/// does not use score, grade, level, judgement windows, or timing accuracy.
/// </summary>
internal static class ChartRadarAnalyzer
{
    // TAPS, LONGS, SLIDES and FLICKS use the same combo-per-minute unit and the
    // exact same intended-route counts as Song Select's Combo Distribution. Each
    // axis has its own stock-calibrated reference (TAPS 390, LONGS 300, SLIDES
    // 250, FLICKS 50 combos/min) so one category does not inherit another's
    // typical density. The skin never needs to implement this.
    // SC charts are excluded, including SC patterns labelled level 10.
    // STREAM and TRAVEL remain independent timing measurements with their own
    // stock references. Values are unbounded; the renderer alone caps overflow
    // graphics at 200%. Keep calibration values centralized here.
    private const double TapsReferencePerMinute = 390.0;
    private const double LongsReferencePerMinute = 300.0;
    private const double SlidesReferencePerMinute = 250.0;
    private const double FlicksReferencePerMinute = 50.0;
    private const double StreamReferencePerSecond = 11.0;
    private const double TravelReferencePerMinute = 990.0;
    private const double StreamWindowSeconds = 2.0;

    private readonly record struct TravelCandidate(
        ChartEvent Event,
        double Position,
        double PositionAfterGesture,
        double GestureDistance);

    public static ChartRadarResult Analyze(string chartPath)
    {
        if (string.IsNullOrWhiteSpace(chartPath))
            throw new ArgumentException("A chart path is required.", nameof(chartPath));

        var fullPath = Path.GetFullPath(chartPath);
        AresChart chart = Path.GetExtension(fullPath).ToLowerInvariant() switch
        {
            ".tsc" => TscLoader.Load(fullPath),
            ".ares" => AresLoader.Load(fullPath),
            _ => throw new NotSupportedException($"Chart radar does not support '{Path.GetExtension(fullPath)}' charts.")
        };

        return Analyze(chart);
    }

    public static ChartRadarResult Analyze(AresChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);

        var sourceEvents = chart.Events
            .Where(ev => ev.Track is >= 0 and <= 15 && IsPlayableNoteType(ev.NoteType))
            .ToArray();
        var tempo = new TempoMap(chart);
        var contentEndTick = sourceEvents.Length > 0
            ? sourceEvents.Max(ev => Math.Max(ev.Tick, ev.EndTick))
            : 0;
        var lastPlayableTick = chart.PlayTick > 0 ? chart.PlayTick : contentEndTick;
        if (chart.PlayTick <= 0 && double.IsFinite(chart.PlayTime) && chart.PlayTime > 0.0)
        {
            // Match the game's inferred Play End behavior for older charts without
            // an explicit marker: use audio end unless playable content extends later.
            var audioOriginSeconds = tempo.TickToSeconds(Math.Max(0, chart.AudioOriginTick));
            var audioEndTick = tempo.SecondsToTick(audioOriginSeconds + chart.PlayTime);
            if (double.IsFinite(audioEndTick) && audioEndTick > 0.0)
                lastPlayableTick = Math.Max(contentEndTick, (int)Math.Min(int.MaxValue, Math.Ceiling(audioEndTick)));
        }

        var durationSeconds = tempo.TickToSeconds(Math.Max(0, lastPlayableTick));
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0.0)
            durationSeconds = 1.0;
        var minutes = Math.Max(1.0 / 60.0, durationSeconds / 60.0);

        // Use the same deterministic SuperFlick hand-off rules as gameplay. In
        // particular, same-tick 60/70 pairs are accepted or rejected atomically.
        var timeline = new GearTimeline(chart);
        var activeSuperFlicks = BuildActiveSuperFlickSet(chart, sourceEvents, lastPlayableTick);
        var activeEvents = sourceEvents
            .Where(ev => ev.Tick <= lastPlayableTick &&
                (ev.IsSuperFlick
                    ? activeSuperFlicks.Contains(ev)
                    : timeline.IsActiveAt(ev)))
            .OrderBy(ev => ev.Tick)
            .ThenBy(ev => ev.Track)
            .ThenBy(ev => ev.EventIndex)
            .ToArray();
        // Use the exact same intended-route combo counts as the Combo
        // Distribution panel, then divide each category by playable duration.
        // Per-category stock references normalize the timing density without
        // changing the underlying category counts or their Combo Distribution.
        var comboDistribution = CalculateComboDistribution(chart, activeEvents, timeline, tempo);
        var tapRate = comboDistribution.Taps / minutes;
        var longRate = comboDistribution.Longs / minutes;
        var slideRate = comboDistribution.Slides / minutes;
        var flickRate = comboDistribution.Flicks / minutes;

        // Stream density is based on actual tap onsets. A LinkHead is tapped,
        // but each LinkTail is only a chain destination and must not add an onset.
        var onsetTicks = activeEvents
            .Where(IsStreamTapOnset)
            .Select(ev => ev.Tick)
            .Distinct()
            .OrderBy(tick => tick)
            .ToArray();
        var streamRate = CalculateStreamP90(onsetTicks, tempo);

        var travelUnits = CalculateTravelUnits(activeEvents, tempo);
        var travelRate = travelUnits / minutes;

        return new ChartRadarResult(
            durationSeconds,
            activeEvents.Length,
            Metric(tapRate, TapsReferencePerMinute, "combos/min"),
            Metric(longRate, LongsReferencePerMinute, "combos/min"),
            Metric(slideRate, SlidesReferencePerMinute, "combos/min"),
            Metric(flickRate, FlicksReferencePerMinute, "combos/min"),
            Metric(streamRate, StreamReferencePerSecond, "onsets/sec"),
            Metric(travelRate, TravelReferencePerMinute, "travel units/min"),
            comboDistribution);
    }

    private static bool IsPlayableNoteType(NoteType noteType)
        => noteType is NoteType.Single or NoteType.SingleLong or NoteType.LinkHead or NoteType.LinkTail or NoteType.Vibrato;

    // Keep these counts identical to the editor's intended-route combo model:
    // taps and flicks score once; holds, vibratos and link heads also score at
    // each active quarter-beat checkpoint, including the terminal checkpoint.
    // LinkTail events are chain destinations and never add a separate combo.
    private static ChartComboDistribution CalculateComboDistribution(
        AresChart chart,
        ChartEvent[] activeEvents,
        GearTimeline routeTimeline,
        TempoMap tempo)
    {
        var nextLinkByHead = new Dictionary<(int Tick, int Track, int EventIndex), ChartEvent>();
        foreach (var link in chart.Links)
        {
            var headKey = (link.Head.Tick, link.Head.Track, link.Head.EventIndex);
            if (!nextLinkByHead.TryGetValue(headKey, out var existing) ||
                link.Tail.Tick < existing.Tick ||
                (link.Tail.Tick == existing.Tick && link.Tail.Track < existing.Track))
            {
                nextLinkByHead[headKey] = link.Tail;
            }
        }

        var taps = 0;
        var longs = 0;
        var slides = 0;
        var flicks = 0;

        foreach (var ev in activeEvents)
        {
            switch (ev.NoteType)
            {
                case NoteType.Single:
                    if (ev.IsFlick || ev.IsSuperFlick)
                        flicks++;
                    else
                        taps++;
                    break;
                case NoteType.SingleLong:
                case NoteType.Vibrato:
                    longs += 1 + CountComboHoldTickCheckpoints(
                        ev, new[] { ev }, chart, routeTimeline, tempo);
                    break;
                case NoteType.LinkHead:
                    slides += 1 + CountComboHoldTickCheckpoints(
                        ev, BuildLinkChain(ev, nextLinkByHead), chart, routeTimeline, tempo);
                    break;
                // LinkTail is deliberately excluded, matching EditorMetaStatsData.
            }
        }

        return new ChartComboDistribution(taps, longs, slides, flicks);
    }

    private static List<ChartEvent> BuildLinkChain(
        ChartEvent root,
        IReadOnlyDictionary<(int Tick, int Track, int EventIndex), ChartEvent> nextLinkByHead)
    {
        static (int Tick, int Track, int EventIndex) KeyOf(ChartEvent ev)
            => (ev.Tick, ev.Track, ev.EventIndex);

        var nodes = new List<ChartEvent> { root };
        var visited = new HashSet<(int Tick, int Track, int EventIndex)> { KeyOf(root) };
        var current = root;
        for (var guard = 0; guard < 256 && nextLinkByHead.TryGetValue(KeyOf(current), out var next); guard++)
        {
            if (!visited.Add(KeyOf(next)))
                break;
            nodes.Add(next);
            current = next;
        }
        return nodes;
    }

    private static int CountComboHoldTickCheckpoints(
        ChartEvent root,
        IReadOnlyList<ChartEvent> nodes,
        AresChart chart,
        GearTimeline routeTimeline,
        TempoMap tempo)
    {
        if (nodes.Count == 0 || (chart.PlayTick > 0 && root.Tick > chart.PlayTick))
            return 0;

        var totalEndTick = nodes.Count > 1
            ? (nodes[^1].Duration > 6 ? nodes[^1].EndTick : nodes[^1].Tick)
            : root.EndTick;
        if (chart.PlayTick > 0)
            totalEndTick = Math.Min(totalEndTick, chart.PlayTick);

        var step = Math.Max(1.0, tempo.TicksPerBeat / 4.0);
        var count = 0;
        var lastScoredTick = double.NegativeInfinity;
        var track = root.Track;
        var nextNode = 1;

        for (var tick = root.Tick + step; tick < totalEndTick - 1e-9; tick += step)
        {
            while (nextNode < nodes.Count && nodes[nextNode].Tick <= tick)
                track = nodes[nextNode++].Track;

            if (GearState.Position(track) is null)
                continue;

            if (routeTimeline.AtTick((int)Math.Floor(tick)).IsActive(track))
                count++;
            lastScoredTick = tick;
        }

        var terminalTrack = root.Track;
        for (var i = 1; i < nodes.Count; i++)
        {
            if (nodes[i].Tick <= totalEndTick + 1e-9)
                terminalTrack = nodes[i].Track;
            else
                break;
        }

        if (GearState.Position(terminalTrack) is not null &&
            totalEndTick >= root.Tick - 1e-9 &&
            (double.IsNegativeInfinity(lastScoredTick) || Math.Abs(lastScoredTick - totalEndTick) > 1e-9) &&
            routeTimeline.AtTick(totalEndTick).IsActive(terminalTrack))
        {
            count++;
        }

        return count;
    }

    private static bool IsStreamTapOnset(ChartEvent ev)
    {
        // LinkTails are part of the link body, not a separate player input.
        // Keep LinkHead because starting a link chain requires an actual tap.
        if (ev.NoteType == NoteType.LinkTail)
            return false;

        return ev.NoteType is NoteType.Single or NoteType.SingleLong or NoteType.Vibrato or NoteType.LinkHead;
    }

    private static HashSet<ChartEvent> BuildActiveSuperFlickSet(AresChart chart, ChartEvent[] events, int endTick)
    {
        var active = new HashSet<ChartEvent>(ReferenceEqualityComparer.Instance);
        var gear = GearState.FromStartAttribute(chart.InitialGearAttr);
        foreach (var tickGroup in events
                     .Where(ev => ev.IsSuperFlick && ev.Tick <= endTick)
                     .OrderBy(ev => ev.Tick)
                     .ThenBy(ev => ev.Track)
                     .ThenBy(ev => ev.EventIndex)
                     .GroupBy(ev => ev.Tick))
        {
            var applied = GearTimeline.ApplySuperFlickChangesForTick(gear, tickGroup, tickGroup);
            foreach (var ev in applied)
                active.Add(ev);
        }
        return active;
    }

    private static double CalculateStreamP90(int[] onsetTicks, TempoMap tempo)
    {
        if (onsetTicks.Length == 0)
            return 0.0;

        var onsetSeconds = onsetTicks.Select(tick => tempo.TickToSeconds(tick)).ToArray();
        var densities = new double[onsetSeconds.Length];
        var end = 0;
        for (var start = 0; start < onsetSeconds.Length; start++)
        {
            if (end < start)
                end = start;
            var windowEnd = onsetSeconds[start] + StreamWindowSeconds;
            while (end < onsetSeconds.Length && onsetSeconds[end] < windowEnd)
                end++;
            densities[start] = (end - start) / StreamWindowSeconds;
        }

        Array.Sort(densities);
        var percentileIndex = Math.Clamp((int)Math.Ceiling(densities.Length * 0.90) - 1, 0, densities.Length - 1);
        return densities[percentileIndex];
    }

    private static double CalculateTravelUnits(ChartEvent[] activeEvents, TempoMap tempo)
    {
        var bySide = new Dictionary<string, List<TravelCandidate>>(StringComparer.OrdinalIgnoreCase)
        {
            ["left"] = new(),
            ["right"] = new()
        };

        foreach (var ev in activeEvents)
        {
            var position = GearState.Position(ev.Track);
            if (position is null)
                continue;

            var (side, bank, localPosition) = position.Value;
            // The two-lane 2K bank occupies the same outer-to-inner span as a
            // three-lane 1K/3K bank, so its two notes map to coordinates 0 and 2.
            var coordinate = bank == "Center" ? localPosition * 2.0 : localPosition;
            var gestureDistance = ev.IsFlick || ev.IsSuperFlick ? 1.0 : 0.0;
            var afterGesture = coordinate;
            if (ev.IsFlick)
                afterGesture = Math.Clamp(coordinate + (ev.Attr == 10 ? -1.0 : 1.0), 0.0, 2.0);
            // SuperFlick changes the active bank; its physical gesture is counted
            // once, while the next note is interpreted in the destination bank.
            bySide[side].Add(new TravelCandidate(ev, coordinate, afterGesture, gestureDistance));
        }

        var total = 0.0;
        foreach (var candidates in bySide.Values)
        {
            var tickGroups = candidates
                .GroupBy(candidate => candidate.Event.Tick)
                .OrderBy(group => group.Key);
            var hasPrevious = false;
            var previousPositionAfterGesture = 0.0;
            var previousGestureDistance = 0.0;
            var previousSeconds = 0.0;

            foreach (var tickGroup in tickGroups)
            {
                var sameTick = tickGroup.ToArray();
                var currentSeconds = tempo.TickToSeconds(tickGroup.Key);
                var chosen = sameTick
                    .OrderBy(candidate => hasPrevious
                        ? Math.Abs(previousPositionAfterGesture - candidate.Position)
                        : candidate.Position)
                    .ThenBy(candidate => candidate.Event.Track)
                    .ThenBy(candidate => candidate.Event.EventIndex)
                    .First();

                if (hasPrevious)
                {
                    var laneDistance = Math.Abs(chosen.Position - previousPositionAfterGesture);
                    var movementDistance = laneDistance + previousGestureDistance;
                    var elapsed = Math.Max(0.0001, currentSeconds - previousSeconds);
                    if (movementDistance > 0.0)
                    {
                        var speed = movementDistance / elapsed;
                        var speedMultiplier = 1.0 + Math.Min(1.0, speed / 4.0);
                        total += movementDistance * speedMultiplier;
                    }
                }

                // A flick's own gesture is paid into the next movement. If it is
                // the final event for this hand, it still contributes one unit.
                previousGestureDistance = sameTick.Sum(candidate => candidate.GestureDistance);
                previousPositionAfterGesture = chosen.PositionAfterGesture;
                previousSeconds = currentSeconds;
                hasPrevious = true;
            }

            if (hasPrevious && previousGestureDistance > 0.0)
                total += previousGestureDistance;
        }

        return total;
    }

    private static ChartRadarMetric Metric(double raw, double reference, string unit)
    {
        if (!double.IsFinite(raw) || raw < 0.0)
            raw = 0.0;
        // Keep the true percentage, including values above 100. The renderer
        // maps overflow beyond the 100% guide into its visible outer band.
        var normalized = reference > 0.0 ? raw / reference * 100.0 : 0.0;
        return new ChartRadarMetric(raw, normalized, unit);
    }
}
