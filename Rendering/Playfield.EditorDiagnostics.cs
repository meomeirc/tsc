using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using Point = Microsoft.Xna.Framework.Point;
using Color = Microsoft.Xna.Framework.Color;
using Microsoft.Xna.Framework.Graphics;
using TSC.Ares;

namespace TSC.Rendering;

public sealed partial class Playfield
{
    private sealed record EditorChartProblem(
        int Tick, int Track, string Message, ChartEvent? Event, bool ForceWarning = false)
    {
        // Panel tracks contain background-animation cues only. Keep their issues
        // visible in the same report, but present them as warnings rather than
        // gameplay errors. Some bundle compatibility notices are also warnings.
        public bool IsWarning => ForceWarning || Track is >= 17 and <= 20;
    }

    private sealed record EditorDiagnosticsSnapshot(
        AresChart SourceChart,
        long Revision,
        ChartBundleTemplate BundleTemplate,
        ChartEvent[][] EventChunks,
        int EventCount,
        (int Tick, double Bpm)[] TempoEvents,
        int TicksPerMeasure,
        double InitialTempo,
        int InitialGearAttr);

    private sealed class EditorDiagnosticsCapture
    {
        public EditorDiagnosticsCapture(AresChart chart, long revision, ChartBundleTemplate bundleTemplate)
        {
            Chart = chart;
            Revision = revision;
            BundleTemplate = bundleTemplate;
            EventCount = chart.Events.Count;
            TempoEvents = chart.TempoEvents.ToArray();
            TicksPerMeasure = chart.TicksPerMeasure;
            InitialTempo = chart.InitialTempo;
            InitialGearAttr = chart.InitialGearAttr;
        }

        public AresChart Chart { get; }
        public long Revision { get; }
        public ChartBundleTemplate BundleTemplate { get; }
        public int EventCount { get; }
        public (int Tick, double Bpm)[] TempoEvents { get; }
        public int TicksPerMeasure { get; }
        public double InitialTempo { get; }
        public int InitialGearAttr { get; }
        public List<ChartEvent[]> EventChunks { get; } = new();
        public int NextEventIndex { get; set; }
    }

    private const int EditorDiagnosticsCaptureChunkSize = 4096;

    private sealed record EditorDiagnosticsResult(
        List<EditorChartProblem> Problems,
        HashSet<ChartEventKey> EventKeys,
        List<ChartEvent> MarkerEvents,
        int ErrorCount,
        int WarningCount);

    private Task<EditorDiagnosticsResult>? _editorDiagnosticsTask;
    private AresChart? _editorDiagnosticsTaskChart;
    private long _editorDiagnosticsTaskRevision = -1;
    private EditorDiagnosticsCapture? _editorDiagnosticsCapture;
    private bool _editorDiagnosticsAdvancedThisUpdate;
    private bool _editorDiagnosticsSkipThisUpdate;

    private void RefreshEditorDiagnostics()
    {
        // Diagnostics capture is budgeted to one small step per Playfield.Update.
        // UI-state construction and draw callbacks may also query the report, but
        // they must never perform a second copy/worker handoff in the render path.
        if (_editorDiagnosticsAdvancedThisUpdate || _editorDiagnosticsSkipThisUpdate)
            return;
        _editorDiagnosticsAdvancedThisUpdate = true;
        var chart = _chart;
        if (!_chartEditorMode || chart is null)
        {
            _editorProblems.Clear();
            _editorProblemEventKeys.Clear();
            _editorProblemMarkerEvents.Clear();
            _editorProblemErrorCount = 0;
            _editorProblemWarningCount = 0;
            _editorDiagnosticsChart = null;
            _editorDiagnosticsDirty = false;
            _editorDiagnosticsTask = null;
            _editorDiagnosticsTaskChart = null;
            _editorDiagnosticsTaskRevision = -1;
            _editorDiagnosticsCapture = null;
            return;
        }

        if (!_editorDiagnosticsDirty && ReferenceEquals(_editorDiagnosticsChart, chart))
            return;

        if (_editorDiagnosticsTask is { IsCompleted: true } completedTask)
        {
            var taskChart = _editorDiagnosticsTaskChart;
            var taskRevision = _editorDiagnosticsTaskRevision;
            _editorDiagnosticsTask = null;
            _editorDiagnosticsTaskChart = null;
            _editorDiagnosticsTaskRevision = -1;

            if (ReferenceEquals(taskChart, chart) && taskRevision == _editorRecoveryRevision)
            {
                try
                {
                    var result = completedTask.GetAwaiter().GetResult();
                    // Swap completed results in directly. Copying a large issue list
                    // and key set on the render thread would move a diagnostics hitch
                    // back onto the UI even though validation itself ran in a worker.
                    _editorProblems = result.Problems;
                    _editorProblemEventKeys = result.EventKeys;
                    _editorProblemMarkerEvents = result.MarkerEvents;
                    _editorProblemErrorCount = result.ErrorCount;
                    _editorProblemWarningCount = result.WarningCount;
                    _editorDiagnosticsChart = chart;
                    _editorDiagnosticsDirty = false;
                }
                catch (Exception ex)
                {
                    _editorProblems.Clear();
                    _editorProblemEventKeys.Clear();
                    _editorProblemMarkerEvents.Clear();
                    _editorProblems.Add(new EditorChartProblem(0, -1,
                        $"Chart checks could not be completed: {ex.GetBaseException().Message}", null));
                    _editorProblemErrorCount = 1;
                    _editorProblemWarningCount = 0;
                    _editorDiagnosticsChart = chart;
                    _editorDiagnosticsDirty = false;
                    Program.LogRuntime(
                        $"EDITOR_DIAGNOSTICS_FAILED chart={chart.Name ?? "Untitled"} " +
                        $"error={ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}");
                }
            }
        }

        if (!_editorDiagnosticsDirty || _editorDiagnosticsTask is not null)
            return;

        // Diagnostics are editor tooling, not playback-critical work. Start or
        // continue the lazy capture only while paused so opening the panel cannot
        // compete with audio/gameplay timing. The expanded panel remains visible
        // and reports that checks are waiting for playback to pause.
        if (_playing)
            return;

        // Copy chart references over several frames instead of allocating/copying
        // one chart-sized array on the opening frame. A chart edit changes the
        // revision and restarts this capture before a worker can consume it.
        var capture = _editorDiagnosticsCapture;
        if (capture is null || !ReferenceEquals(capture.Chart, chart) ||
            capture.Revision != _editorRecoveryRevision || capture.EventCount != chart.Events.Count)
        {
            capture = new EditorDiagnosticsCapture(chart, _editorRecoveryRevision, _editorBundleTemplate);
            _editorDiagnosticsCapture = capture;
        }

        var remaining = capture.EventCount - capture.NextEventIndex;
        if (remaining > 0)
        {
            var count = Math.Min(EditorDiagnosticsCaptureChunkSize, remaining);
            var chunk = new ChartEvent[count];
            chart.Events.CopyTo(capture.NextEventIndex, chunk, 0, count);
            capture.EventChunks.Add(chunk);
            capture.NextEventIndex += count;
        }

        if (capture.NextEventIndex < capture.EventCount)
            return;

        // The worker consumes a jagged set of small arrays, so even the final
        // handoff avoids flattening a large chart on the gameplay thread.
        var snapshot = new EditorDiagnosticsSnapshot(
            capture.Chart,
            capture.Revision,
            capture.BundleTemplate,
            capture.EventChunks.ToArray(),
            capture.EventCount,
            capture.TempoEvents,
            capture.TicksPerMeasure,
            capture.InitialTempo,
            capture.InitialGearAttr);
        _editorDiagnosticsCapture = null;
        _editorDiagnosticsTaskChart = chart;
        _editorDiagnosticsTaskRevision = snapshot.Revision;
        // Use the shared thread pool instead of LongRunning. The latter creates a
        // dedicated OS thread synchronously on the UI thread on first use, which
        // can itself produce a visible first-open hitch. The worker still lowers
        // its priority while validating so playback/input gets CPU time first.
        _editorDiagnosticsTask = Task.Run(() =>
        {
            var workerThread = Thread.CurrentThread;
            var previousPriority = workerThread.Priority;
            try { workerThread.Priority = ThreadPriority.Lowest; }
            catch (PlatformNotSupportedException) { }
            catch (System.Security.SecurityException) { }
            try { return BuildEditorDiagnostics(snapshot); }
            finally
            {
                try { workerThread.Priority = previousPriority; }
                catch (PlatformNotSupportedException) { }
                catch (System.Security.SecurityException) { }
            }
        });
    }

    private bool IsEditorDiagnosticsPendingForCurrentChart()
        => (_editorDiagnosticsTask is { IsCompleted: false } &&
            ReferenceEquals(_editorDiagnosticsTaskChart, _chart) &&
            _editorDiagnosticsTaskRevision == _editorRecoveryRevision) ||
           (_editorDiagnosticsCapture is not null &&
            ReferenceEquals(_editorDiagnosticsCapture.Chart, _chart) &&
            _editorDiagnosticsCapture.Revision == _editorRecoveryRevision);

    private bool IsEditorDiagnosticsPreparingForCurrentChart()
        => _editorDiagnosticsCapture is not null &&
           ReferenceEquals(_editorDiagnosticsCapture.Chart, _chart) &&
           _editorDiagnosticsCapture.Revision == _editorRecoveryRevision;

    private static EditorDiagnosticsResult BuildEditorDiagnostics(EditorDiagnosticsSnapshot snapshot)
    {
        var chart = CreateEditorDiagnosticsChart(snapshot);
        var bundleTemplate = snapshot.BundleTemplate;
        var problems = new List<EditorChartProblem>();
        var problemEventKeys = new HashSet<ChartEventKey>();

        var notes = chart.Events
            .Where(ev => ev.Kind == "Note" && ev.RawType == 1 && ev.NoteType != NoteType.End)
            .OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track).ThenBy(ev => ev.EventIndex)
            .ToList();

        var playableNotes = notes.Where(ev => (ev.Track is >= 0 and <= 15) || (ev.Track is >= 17 and <= 20)).ToList();
        foreach (var group in playableNotes.GroupBy(ev => (ev.Tick, ev.Track)).Where(group => group.Count() > 1))
        {
            var first = group.First();
            var members = group.ToList();
            problems.Add(new EditorChartProblem(first.Tick, first.Track,
                $"Overlapping notes on track {first.Track} at tick {first.Tick} ({members.Count} notes).", first));
            foreach (var ev in members)
                problemEventKeys.Add(KeyOf(ev));
        }

        // SuperFlicks are bank-wide strips, not lane-local notes. Two different
        // tracks in the same physical bank at one tick would overlap visually.
        var superFlicksByPhysicalBank = playableNotes
            .Where(ev => ev.Track is >= 0 and <= 15 && ev.IsSuperFlick)
            .Select(ev => (Event: ev, Position: GearState.Position(ev.Track)))
            .Where(item => item.Position.HasValue)
            .GroupBy(item => (item.Event.Tick, item.Position!.Value.Side, item.Position.Value.Bank))
            .Where(group => group.Count() > 1);
        foreach (var group in superFlicksByPhysicalBank)
        {
            var members = group.Select(item => item.Event).ToList();
            var first = members[0];
            var position = GearState.Position(first.Track)!.Value;
            var message = $"Multiple SuperFlicks occupy the {position.Side} {BankModeLabel(position.Bank)} bank at tick {first.Tick}; only one is allowed per bank at a tick.";
            problems.Add(new EditorChartProblem(first.Tick, first.Track, message, first));
            foreach (var ev in members)
                problemEventKeys.Add(KeyOf(ev));
        }

        // TSTS bundles may not support the distinct outer 2K->3K SuperFlick
        // paths (attrs 80/90) in ReSONANCE. Keep these as compatibility warnings:
        // the chart remains editable/exportable, but the mapper should be aware.
        if (bundleTemplate == ChartBundleTemplate.Tsts)
        {
            foreach (var ev in playableNotes.Where(ev => ev.Track is >= 0 and <= 15 &&
                         ev.IsSuperFlick && (ev.Attr is 80 or 90)))
            {
                var side = ev.Attr == 80 ? "left" : "right";
                var message = $"2K-to-3K {side} SuperFlick may not work in ReSONANCE with a TSTS bundle.";
                // Keep compatibility warnings out of the red gameplay-note marker overlay.
                problems.Add(new EditorChartProblem(ev.Tick, ev.Track, message, ev, ForceWarning: true));
            }
        }

        // Build link adjacency only when the chart actually contains link points.
        // Most charts do not; skipping LinkBuilder's grouping/sorting passes makes
        // the common empty-problems check substantially lighter.
        // Keep malformed link points in the diagnostic path too: negative or
        // missing pair IDs are themselves useful errors (orphan/invalid link
        // paths), even though LinkBuilder cannot connect them.
        var hasLinkCandidates = notes.Any(ev =>
            ev.NoteType is NoteType.LinkHead or NoteType.LinkTail);
        string? linkBuildFailure = null;
        if (hasLinkCandidates)
        {
            try
            {
                LinkBuilder.Build(chart);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                linkBuildFailure = ex.Message;
            }
        }
        var builtLinks = chart.Links.ToArray();
        var expectedLinkEndpoints = builtLinks
            .Select(link => (Head: link.Head, Tail: link.Tail))
            .ToHashSet();

        // A chain may use only one horizontal connector at any endpoint tick.
        // LinkSegment's connector is at Tail.Tick, even if Head.Tick is earlier.
        var editorChains = linkBuildFailure is null && hasLinkCandidates
            ? GetEditorLinkChainInfosForDiagnostics(chart)
            : new List<EditorLinkChainInfo>();
        var chainMembershipCounts = editorChains
            .SelectMany(chain => chain.Nodes.Select(node => (Node: node, Chain: chain)))
            .GroupBy(item => item.Node)
            .ToDictionary(group => group.Key, group => group.Count());
        foreach (var chain in editorChains)
        {
            var chainNodes = chain.Nodes.ToHashSet();
            var extraHorizontalConnections = builtLinks
                .Where(link => chainNodes.Contains(link.Head) && chainNodes.Contains(link.Tail) &&
                               link.Head.Track != link.Tail.Track)
                .GroupBy(link => link.Tail.Tick)
                .Where(group => group.Count() > 1);
            foreach (var group in extraHorizontalConnections)
            {
                var sameTickNodes = chain.Nodes.Where(node => node.Tick == group.Key).ToList();
                var message = $"Link chain from tick {chain.Root.Tick}/track {chain.Root.Track} has {group.Count()} horizontal connections at tick {group.Key}; only one is allowed at a tick.";
                if (!problems.Any(problem => string.Equals(problem.Message, message, StringComparison.Ordinal)))
                    problems.Add(new EditorChartProblem(group.Key, sameTickNodes.FirstOrDefault()?.Track ?? chain.Root.Track,
                        message, sameTickNodes.FirstOrDefault() ?? chain.Root));
                foreach (var node in sameTickNodes)
                    problemEventKeys.Add(KeyOf(node));
                foreach (var link in group)
                {
                    problemEventKeys.Add(KeyOf(link.Head));
                    problemEventKeys.Add(KeyOf(link.Tail));
                }
            }
        }

        // Link rendering has two parts: a body that follows the source lane up
        // to the tail tick, and a horizontal connector across lanes at that tick.
        // A crossover is the horizontal connector intersecting a note or body
        // between its endpoints. Check the actual connection tick rather than
        // treating the whole head-to-tail segment as a diagonal line.
        if (linkBuildFailure is null)
        {
            var candidatesByFamily = playableNotes
                .Where(ev => ev.Track is >= 0 and <= 15)
                .Select(ev => (Event: ev, Family: LinkBuilder.Family(ev.Track), Position: GearState.Position(ev.Track)?.LocalPos))
                .Where(item => item.Family is not null && item.Position.HasValue)
                .GroupBy(item => item.Family!, item => (item.Event, Position: item.Position!.Value))
                .ToDictionary(group => group.Key, group => group.ToList());

            var outgoingByHead = builtLinks
                .GroupBy(link => link.Head)
                .ToDictionary(group => group.Key,
                    group => group.OrderBy(link => link.Tail.Tick).ThenBy(link => link.Tail.Track).First());

            foreach (var chain in editorChains)
            {
                if (chain.Nodes.Any(node => !chainMembershipCounts.TryGetValue(node, out var count) || count != 1))
                    continue;
                var chainNodes = chain.Nodes.ToHashSet();
                foreach (var connector in builtLinks.Where(link => chainNodes.Contains(link.Head) && chainNodes.Contains(link.Tail)))
                {
                    var family = LinkBuilder.Family(connector.Head.Track);
                    if (family is null || family != LinkBuilder.Family(connector.Tail.Track) ||
                        !candidatesByFamily.TryGetValue(family, out var candidates))
                        continue;

                    var x1 = GearState.Position(connector.Head.Track)?.LocalPos;
                    var x2 = GearState.Position(connector.Tail.Track)?.LocalPos;
                    if (!x1.HasValue || !x2.HasValue || x1.Value == x2.Value)
                        continue; // A same-track connection has no horizontal connector.

                    var connectionTick = connector.Tail.Tick;
                    var loX = Math.Min(x1.Value, x2.Value);
                    var hiX = Math.Max(x1.Value, x2.Value);
                    foreach (var candidate in candidates)
                    {
                        var ev = candidate.Event;
                        var noteX = candidate.Position;
                        if (ReferenceEquals(ev, connector.Head) || ReferenceEquals(ev, connector.Tail) ||
                            (ev.Track == connector.Head.Track && ev.Tick == connector.Head.Tick) ||
                            (ev.Track == connector.Tail.Track && ev.Tick == connector.Tail.Tick))
                            continue;

                        // Lanes at the connector's two endpoints are intentional
                        // joins. Only notes strictly between those lanes cross it.
                        if (noteX <= loX || noteX >= hiX)
                            continue;

                        var noteEndTick = ev.EndTick;
                        var hasOutgoingBody = false;
                        if ((ev.NoteType is NoteType.LinkHead or NoteType.LinkTail) &&
                            outgoingByHead.TryGetValue(ev, out var outgoing))
                        {
                            noteEndTick = outgoing.Tail.Tick;
                            hasOutgoingBody = true;
                        }

                        var hasBody = ev.IsHoldNote || ev.Duration > 6 || hasOutgoingBody;
                        // Bodies occupy the lane up to, but not through, their end tick.
                        // A connector that meets the exact authored endpoint is a handoff,
                        // not a crossover through a still-active body. This matters for
                        // adjacent same-tick links such as L2->L1 beside L3->L2.
                        var bodyStartTick = Math.Min(ev.Tick, noteEndTick);
                        var bodyEndTick = Math.Max(ev.Tick, noteEndTick);
                        var occupiesConnection = hasBody
                            ? bodyStartTick <= connectionTick && connectionTick < bodyEndTick
                            : ev.Tick == connectionTick;
                        if (!occupiesConnection)
                            continue;

                        var message = $"Link crossover: connector at tick {connectionTick} from track {connector.Head.Track} to track {connector.Tail.Track} crosses a note/body at tick {ev.Tick}/track {ev.Track}.";
                        problems.Add(new EditorChartProblem(ev.Tick, ev.Track, message, ev));
                        problemEventKeys.Add(KeyOf(connector.Head));
                        problemEventKeys.Add(KeyOf(connector.Tail));
                        problemEventKeys.Add(KeyOf(ev));
                    }
                }
            }
        }

        // A hold occupies the lane until its end tick. A later unrelated note
        // inside that interval is an overlap even when the notes start on
        // different ticks. Panel animation notes may hand off exactly at the
        // hold end tick. Connected link endpoints are diagnosed below instead.
        var notesByTrack = playableNotes
            .GroupBy(ev => ev.Track)
            .ToDictionary(group => group.Key, group => group
                .OrderBy(ev => ev.Tick).ThenBy(ev => ev.EventIndex).ToArray());
        foreach (var hold in playableNotes
                     .Where(ev => ev.Tick >= 0 && ev.EndTick > ev.Tick &&
                                  (ev.IsHoldNote || ev.Duration > 6))
                     .OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track).ThenBy(ev => ev.EventIndex))
        {
            if (!notesByTrack.TryGetValue(hold.Track, out var laneNotes))
                continue;

            // Locate the first note strictly after the hold's start in O(log N),
            // then scan only the notes that could fall within this hold's interval.
            var low = 0;
            var high = laneNotes.Length;
            while (low < high)
            {
                var middle = low + ((high - low) >> 1);
                if (laneNotes[middle].Tick <= hold.Tick)
                    low = middle + 1;
                else
                    high = middle;
            }

            for (var i = low; i < laneNotes.Length; i++)
            {
                var next = laneNotes[i];
                if (next.Tick > hold.EndTick)
                    break;
                if (hold.Track is >= 17 and <= 20 && next.Tick == hold.EndTick)
                    continue;
                if (expectedLinkEndpoints.Contains((Head: hold, Tail: next)))
                    continue;

                problems.Add(new EditorChartProblem(next.Tick, next.Track,
                    $"Overlapping note on track {hold.Track}: note at tick {next.Tick} falls inside the hold from tick {hold.Tick} to {hold.EndTick}.", next));
                problemEventKeys.Add(KeyOf(hold));
                problemEventKeys.Add(KeyOf(next));
            }
        }

        // A same-track LinkHead -> LinkTail is already a vertical connection.
        // Do not require a further outgoing link: a duration on that LinkTail
        // (for example head@10 -> tail@20 -> duration end@30) is represented by
        // the tail's duration, not by another LinkBuilder segment, and used to
        // escape this check entirely.
        if (linkBuildFailure is null)
        {
            foreach (var link in builtLinks.Where(link =>
                         link.Head.Track is >= 0 and <= 15 && link.Head.Track == link.Tail.Track))
            {
                var tail = link.Tail;
                AddProblem(tail,
                    $"Vertical link connection on track {tail.Track} " +
                    $"(ticks {link.Head.Tick}–{tail.Tick}) stays on the same track. " +
                    "Move the LinkTail to another track.");
                problemEventKeys.Add(KeyOf(link.Head));
            }
        }

        foreach (var ev in notes)
        {
            if (ev.Tick < 0)
            {
                AddProblem(ev, $"Note has a negative tick ({ev.Tick}).");
            }
        }

        if (linkBuildFailure is null)
        {
            var headCounts = builtLinks.GroupBy(link => KeyOf(link.Head))
                .ToDictionary(group => group.Key, group => group.Count());
            var tailCounts = builtLinks.GroupBy(link => KeyOf(link.Tail))
                .ToDictionary(group => group.Key, group => group.Count());

            foreach (var ev in playableNotes.Where(ev => ev.NoteType == NoteType.LinkHead))
            {
                if (headCounts.TryGetValue(KeyOf(ev), out var count) && count > 1)
                    AddProblem(ev, $"Broken link path: LinkHead branches into {count} outgoing segments.");
            }
            foreach (var ev in playableNotes.Where(ev => ev.NoteType == NoteType.LinkTail))
            {
                if (tailCounts.TryGetValue(KeyOf(ev), out var count) && count > 1)
                    AddProblem(ev, $"Broken link path: multiple segments end at the same LinkTail ({count} incoming segments).");
            }

            // The full path validator is part of the automatic error report. This
            // includes invalid/reused IDs, bad attrs, orphan tails and orphan heads;
            // no separate Tools action is needed.
            if (hasLinkCandidates)
            {
                foreach (var issue in GetEditorLinkPathIssuesForDiagnostics(chart, editorChains))
                {
                    problems.Add(new EditorChartProblem(issue.Tick, issue.Track,
                        $"Link path: {issue.Message}", issue.Event));
                    if (issue.Event is not null)
                        problemEventKeys.Add(KeyOf(issue.Event));
                }
            }
        }
        else
        {
            problems.Add(new EditorChartProblem(0, -1, $"Could not validate link paths: {linkBuildFailure}", null));
        }

        foreach (var tempo in chart.TempoEvents)
        {
            if (tempo.Tick < 0)
                problems.Add(new EditorChartProblem(tempo.Tick, -1, $"Tempo change has a negative tick ({tempo.Tick}).", null));
            if (!double.IsFinite(tempo.Bpm) || tempo.Bpm <= 0)
                problems.Add(new EditorChartProblem(tempo.Tick, -1, $"Tempo change has an invalid BPM value ({tempo.Bpm}).", null));
        }
        foreach (var group in chart.TempoEvents.GroupBy(tempo => tempo.Tick).Where(group => group.Count() > 1))
        {
            var values = string.Join(", ", group.Select(tempo => tempo.Bpm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
            problems.Add(new EditorChartProblem(group.Key, -1,
                $"Multiple tempo changes share tick {group.Key} (BPM values: {values}).", null));
        }

        problems.Sort((a, b) =>
        {
            var tickComparison = a.Tick.CompareTo(b.Tick);
            if (tickComparison != 0) return tickComparison;
            var trackComparison = a.Track.CompareTo(b.Track);
            return trackComparison != 0 ? trackComparison : string.Compare(a.Message, b.Message, StringComparison.Ordinal);
        });
        var errorCount = problems.Count(problem => !problem.IsWarning);
        var warningCount = problems.Count(problem => problem.IsWarning);
        // The render loop needs only the notes that actually receive a marker.
        // Resolve these once on the diagnostics worker instead of scanning the
        // entire chart on every gameplay frame while the Problems report is open.
        var markerEvents = new List<ChartEvent>();
        var markerEventReferences = new HashSet<ChartEvent>(ReferenceEqualityComparer.Instance);
        foreach (var ev in chart.Events)
        {
            if (ev.Track is >= 0 and <= 15 && problemEventKeys.Contains(KeyOf(ev)) &&
                markerEventReferences.Add(ev))
                markerEvents.Add(ev);
        }

        void AddProblem(ChartEvent ev, string message)
        {
            problems.Add(new EditorChartProblem(ev.Tick, ev.Track, message, ev));
            problemEventKeys.Add(KeyOf(ev));
        }

        return new EditorDiagnosticsResult(problems, problemEventKeys, markerEvents, errorCount, warningCount);
    }

    private static AresChart CreateEditorDiagnosticsChart(EditorDiagnosticsSnapshot snapshot)
    {
        var chart = new AresChart
        {
            TicksPerMeasure = snapshot.TicksPerMeasure,
            InitialTempo = snapshot.InitialTempo,
            InitialGearAttr = snapshot.InitialGearAttr
        };
        chart.Events.EnsureCapacity(snapshot.EventCount);
        foreach (var chunk in snapshot.EventChunks)
            chart.Events.AddRange(chunk);
        chart.TempoEvents.AddRange(snapshot.TempoEvents);
        return chart;
    }

    private static List<EditorLinkChainInfo> GetEditorLinkChainInfosForDiagnostics(AresChart chart)
    {
        var nextByHeadKey = new Dictionary<ChartEventKey, LinkSegment>();
        var ambiguousHeads = new HashSet<ChartEventKey>();
        foreach (var link in chart.Links)
        {
            var key = KeyOf(link.Head);
            if (!nextByHeadKey.TryGetValue(key, out var existing) || link.Tail.Tick < existing.Tail.Tick)
            {
                nextByHeadKey[key] = link;
                ambiguousHeads.Remove(key);
            }
            else if (link.Tail.Tick == existing.Tail.Tick)
            {
                ambiguousHeads.Add(key);
                if (link.Tail.Track < existing.Tail.Track)
                    nextByHeadKey[key] = link;
            }
        }

        var result = new List<EditorLinkChainInfo>();
        var roots = chart.Events
            .Where(ev => ev.Kind == "Note" && ev.RawType == 1 && IsPlayableEditorTrack(ev.Track) &&
                         ev.NoteType == NoteType.LinkHead)
            .OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track).ThenBy(ev => ev.EventIndex);
        foreach (var root in roots)
        {
            var nodes = new List<ChartEvent> { root };
            var visited = new HashSet<ChartEvent>(ReferenceEqualityComparer.Instance) { root };
            var current = root;
            for (var guard = 0; guard < 256; guard++)
            {
                var key = KeyOf(current);
                if (ambiguousHeads.Contains(key) || !nextByHeadKey.TryGetValue(key, out var nextLink))
                    break;
                if (!visited.Add(nextLink.Tail))
                    break;
                nodes.Add(nextLink.Tail);
                current = nextLink.Tail;
            }
            // `visited` already guarantees reference uniqueness as each node is
            // appended. Avoid LINQ Distinct here: the framework reference comparer
            // can widen type inference to object on this target framework.
            nodes = nodes.Where(ev => ev.Kind == "Note" && ev.RawType == 1 &&
                    IsPlayableEditorTrack(ev.Track) && ev.NoteType is NoteType.LinkHead or NoteType.LinkTail)
                .ToList();
            if (nodes.Count == 0)
                nodes.Add(root);
            result.Add(new EditorLinkChainInfo(root, nodes, LinkBuilder.Family(root.Track),
                root.Tick, nodes.Max(ev => (long)ev.EndTick), root.LinePair));
        }
        return result;
    }

    private static List<EditorLinkPathIssue> GetEditorLinkPathIssuesForDiagnostics(
        AresChart chart, List<EditorLinkChainInfo> chains)
    {
        var issues = new List<EditorLinkPathIssue>();
        if (chart is null)
            return issues;

        void Add(ChartEvent ev, string message)
            => issues.Add(new EditorLinkPathIssue(ev.Tick, ev.Track, message, ev));

        var routeTimeline = new GearTimeline(chart);
        var claimed = new HashSet<ChartEvent>(ReferenceEqualityComparer.Instance);
        var headsWithoutConnectedTail = new HashSet<ChartEvent>(ReferenceEqualityComparer.Instance);
        foreach (var chain in chains)
        {
            var root = chain.Root;
            var pairValid = chain.Pair is 1 or 3 or 5 or 7;
            if (!pairValid)
                Add(root, $"head tick {root.Tick}/track {root.Track} has invalid pair ID {chain.Pair}");
            if (chain.Nodes.Count < 2)
            {
                Add(root, $"head tick {root.Tick}/track {root.Track} has no connected tail");
                headsWithoutConnectedTail.Add(root);
            }

            foreach (var node in chain.Nodes)
            {
                if (!claimed.Add(node))
                    Add(node, $"tick {node.Tick}/track {node.Track} is shared by multiple chain paths");
                if (!string.Equals(LinkBuilder.Family(node.Track), chain.Family, StringComparison.Ordinal))
                    Add(node, $"chain at tick {root.Tick} crosses banks");
                if (node.LinePair != chain.Pair)
                    Add(node, $"tick {node.Tick}/track {node.Track} has pair {node.LinePair}, expected {chain.Pair}");
                if (pairValid)
                {
                    var expectedAttr = node.NoteType == NoteType.LinkHead ? chain.Pair : chain.Pair + 1;
                    if (node.Attr != expectedAttr)
                        Add(node, $"tick {node.Tick}/track {node.Track} has attr {node.Attr}, expected {expectedAttr}");
                }
            }
        }

        var linkPoints = chart.Events.Where(ev => ev.Kind == "Note" && ev.RawType == 1 &&
            IsPlayableEditorTrack(ev.Track) &&
            ev.NoteType is NoteType.LinkHead or NoteType.LinkTail).ToList();
        foreach (var tail in linkPoints.Where(ev => ev.NoteType == NoteType.LinkTail && !claimed.Contains(ev)))
            Add(tail, $"orphan tail at tick {tail.Tick}/track {tail.Track} has no link-head path");

        // Build the linked-head set once. The previous Any() nested in the
        // LinkHead loop rescanned every link for every head (O(heads * links)).
        var linkedHeads = new HashSet<ChartEvent>(
            chart.Links.Select(link => link.Head), ReferenceEqualityComparer.Instance);
        foreach (var head in linkPoints.Where(ev => ev.NoteType == NoteType.LinkHead && !linkedHeads.Contains(ev)))
        {
            if (!headsWithoutConnectedTail.Contains(head))
                Add(head, $"orphan LinkHead at tick {head.Tick}/track {head.Track} has no outgoing connector");
        }

        // Pair IDs are reused by non-overlapping chains. Sort each pair group by
        // start tick and stop once later chains are beyond the current interval;
        // this avoids comparing every chain with every other chain in the chart.
        foreach (var pairGroup in chains
                     .Where(chain => chain.Pair is 1 or 3 or 5 or 7)
                     .GroupBy(chain => chain.Pair))
        {
            var ordered = pairGroup.OrderBy(chain => chain.StartTick)
                .ThenBy(chain => chain.EndTick)
                .ToArray();
            for (var i = 0; i < ordered.Length; i++)
            {
                var a = ordered[i];
                for (var j = i + 1; j < ordered.Length; j++)
                {
                    var b = ordered[j];
                    if (b.StartTick > a.EndTick)
                        break;

                    var overlaps = a.StartTick < b.EndTick && b.StartTick < a.EndTick;
                    // Reuse is blocked only when the two banks can be visible
                    // simultaneously during the overlapping part of the chains.
                    if (overlaps && EditorLinkChainsConflictInTime(routeTimeline, a, b))
                    {
                        Add(b.Root, $"pair {a.Pair}/{a.Pair + 1} is reused by a possible route combination ({LinkBuilder.FamilyLabel(a.Family)} and {LinkBuilder.FamilyLabel(b.Family)}) between ticks {a.StartTick} and {b.StartTick}");
                        continue;
                    }

                    var boundaryTail = GetEditorLinkBoundaryTailConflict(a, b) ??
                                       GetEditorLinkBoundaryTailConflict(b, a);
                    if (boundaryTail is not null &&
                        !EditorLinkFamiliesCanSharePair(routeTimeline, a.Family, b.Family,
                            boundaryTail.Tick, boundaryTail.Tick < int.MaxValue ? boundaryTail.Tick + 1 : boundaryTail.Tick))
                    {
                        boundaryTail = null;
                    }
                    if (boundaryTail is not null)
                    {
                        Add(boundaryTail,
                            $"pair {a.Pair}/{a.Pair + 1} cannot be reused at tick {boundaryTail.Tick}: a same-bank LinkTail at the hand-off tick is ambiguous; use a different ID");
                    }
                }
            }
        }

        return issues
            .GroupBy(issue => issue.Message, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }
    private Rectangle GetEditorProblemsPanelBounds(int virtualWidth, int virtualHeight)
    {
        var unit = _projection.ScalePixels(1f);
        var scrubber = GetEditorTimelineScrubberBounds(virtualWidth, virtualHeight);
        var bottomStrip = GetEditorBottomLeftStripBounds(virtualWidth, virtualHeight);
        var margin = Math.Max(4, (int)MathF.Round((float)_chartEditorInterfaceLayout.EdgeMargin * unit));
        var x = scrubber.Right + margin;
        // Give the Problems list more room for full diagnostic messages while
        // leaving a clear gap for the fixed right-side editor controls.
        var widthLimit = Math.Max((int)MathF.Round(300f * unit), virtualWidth - x - margin - (int)MathF.Round((float)_chartEditorInterfaceLayout.ActionColumnWidth * unit));
        var width = Math.Min((int)MathF.Round((float)_chartEditorInterfaceLayout.ProblemsMaxWidth * unit), widthLimit);
        width = Math.Max(1, width);
        var headerHeight = Math.Max(30, (int)MathF.Round(42f * unit));
        var availableHeight = Math.Max(headerHeight, bottomStrip.Y - margin * 2);
        var height = _editorProblemsExpanded
            ? Math.Min((int)MathF.Round((float)_chartEditorInterfaceLayout.ProblemsMaxHeight * unit), availableHeight)
            : headerHeight;
        height = Math.Max(headerHeight, height);
        return new Rectangle(x, Math.Max(margin, bottomStrip.Y - margin - height), width, height);
    }

    private Rectangle GetEditorProblemsHeaderBounds(int virtualWidth, int virtualHeight)
    {
        var panel = GetEditorProblemsPanelBounds(virtualWidth, virtualHeight);
        var headerHeight = Math.Min(panel.Height, Math.Max(30, (int)MathF.Round(42f * _projection.ScalePixels(1f))));
        return new Rectangle(panel.X, panel.Y, panel.Width, headerHeight);
    }

    private Rectangle GetEditorProblemsQuickFixButtonBounds(int virtualWidth, int virtualHeight)
    {
        var header = GetEditorProblemsHeaderBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var width = Math.Min(Math.Max(96, (int)MathF.Round(132f * unit)), Math.Max(1, header.Width / 2));
        var height = Math.Max(20, header.Height - (int)MathF.Round(10f * unit));
        var rightInset = Math.Max(8, (int)MathF.Round(10f * unit));
        var chevronSpace = Math.Max(22, (int)MathF.Round(28f * unit));
        var x = Math.Max(header.X + 44, header.Right - rightInset - chevronSpace - width);
        return new Rectangle(x, header.Y + (header.Height - height) / 2,
            Math.Max(1, Math.Min(width, header.Right - rightInset - chevronSpace - x)), height);
    }

    private Rectangle GetEditorProblemsListBounds(int virtualWidth, int virtualHeight)
    {
        var panel = GetEditorProblemsPanelBounds(virtualWidth, virtualHeight);
        var header = GetEditorProblemsHeaderBounds(virtualWidth, virtualHeight);
        var inset = Math.Max(3, (int)MathF.Round(5f * _projection.ScalePixels(1f)));
        return new Rectangle(panel.X + inset, header.Bottom,
            Math.Max(1, panel.Width - inset * 2), Math.Max(0, panel.Bottom - header.Bottom - inset));
    }

    private int GetEditorProblemsVisibleRows(int virtualWidth, int virtualHeight)
    {
        var list = GetEditorProblemsListBounds(virtualWidth, virtualHeight);
        var pitch = Math.Max(22, (int)MathF.Round(30f * _projection.ScalePixels(1f)));
        return Math.Max(0, list.Height / pitch);
    }

    private int GetEditorProblemsScrollWindowRows(int virtualWidth, int virtualHeight)
    {
        // The normal Lua interface renders a fixed five-row window. The C# fallback
        // sizes its list to the available panel height, so its window can differ.
        return _chartEditorInterfaceRuntime.IsLoaded && _chartEditorInterfaceRuntime.LastError is null
            ? LAYOUT_PROBLEM_ROW_COUNT
            : Math.Max(1, GetEditorProblemsVisibleRows(virtualWidth, virtualHeight));
    }

    private double GetEditorProblemsScrollRowPitch(int virtualWidth, int virtualHeight)
    {
        var unit = _projection.ScalePixels(1f);
        return _chartEditorInterfaceRuntime.IsLoaded && _chartEditorInterfaceRuntime.LastError is null
            ? 44.0 * unit
            : Math.Max(22.0, (int)MathF.Round(30f * unit));
    }

    private double GetEditorProblemsScrollContentExtent(int virtualWidth, int virtualHeight)
    {
        if (_editorProblems.Count <= 0) return 0.0;
        var pitch = GetEditorProblemsScrollRowPitch(virtualWidth, virtualHeight);
        var trailingGap = _chartEditorInterfaceRuntime.IsLoaded && _chartEditorInterfaceRuntime.LastError is null
            ? 4.0 * _projection.ScalePixels(1f)
            : 0.0;
        return Math.Max(0.0, _editorProblems.Count * pitch - trailingGap);
    }

    private double GetEditorProblemsScrollViewportExtent(int virtualWidth, int virtualHeight)
    {
        if (_chartEditorInterfaceRuntime.IsLoaded && _chartEditorInterfaceRuntime.LastError is null)
        {
            var rowCount = Math.Min(LAYOUT_PROBLEM_ROW_COUNT, _editorProblems.Count);
            if (rowCount <= 0) return 0.0;
            var rowPitch = GetEditorProblemsScrollRowPitch(virtualWidth, virtualHeight);
            return Math.Max(0.0, rowCount * rowPitch - 4.0 * _projection.ScalePixels(1f));
        }

        return Math.Max(0, GetEditorProblemsListBounds(virtualWidth, virtualHeight).Height);
    }

    private double GetEditorProblemsScrollMaximum(int virtualWidth, int virtualHeight)
        => Math.Max(0.0, GetEditorProblemsScrollContentExtent(virtualWidth, virtualHeight) -
            GetEditorProblemsScrollViewportExtent(virtualWidth, virtualHeight));

    private int GetEditorProblemsScrollFirstRow(int virtualWidth, int virtualHeight)
    {
        var pitch = Math.Max(1.0, GetEditorProblemsScrollRowPitch(virtualWidth, virtualHeight));
        return Math.Clamp((int)Math.Floor(_editorProblemsScroll.Position / pitch), 0,
            Math.Max(0, _editorProblems.Count - 1));
    }

    private double GetEditorProblemsScrollPixelRemainder(int virtualWidth, int virtualHeight)
    {
        var pitch = Math.Max(1.0, GetEditorProblemsScrollRowPitch(virtualWidth, virtualHeight));
        return Math.Clamp(_editorProblemsScroll.Position -
            GetEditorProblemsScrollFirstRow(virtualWidth, virtualHeight) * pitch, 0.0, pitch);
    }

    private void ClampEditorProblemsScroll(int virtualWidth, int virtualHeight)
    {
        _editorProblemsScroll.SetMaximum(GetEditorProblemsScrollMaximum(virtualWidth, virtualHeight));
        _editorProblemsScrollOffset = GetEditorProblemsScrollFirstRow(virtualWidth, virtualHeight);
    }

    private void DrawEditorProblemsPanel(int virtualWidth, int virtualHeight)
    {
        if (!_chartEditorMode || _chart is null || _tempo is null)
            return;
        var interfaceUi = _chartEditorInterfaceLayout;
        var labelFont = _font;
        var unit = _projection.ScalePixels(1f);
        var textScale = GetEditorDefaultFontScale(18f * unit);
        var panel = GetEditorProblemsPanelBounds(virtualWidth, virtualHeight);
        var header = GetEditorProblemsHeaderBounds(virtualWidth, virtualHeight);
        var list = GetEditorProblemsListBounds(virtualWidth, virtualHeight);
        var edge = Math.Max(1, (int)MathF.Round(2f * unit));
        var red = interfaceUi.DangerColor;
        var yellow = new Color(255, 205, 64);
        var errorCount = _editorProblemErrorCount;
        var warningCount = _editorProblemWarningCount;
        var statusColor = errorCount > 0 ? red : warningCount > 0 ? yellow : new Color(52, 211, 153);
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);
        _spriteBatch.Draw(_pixel, panel, interfaceUi.PanelColor);
        _spriteBatch.Draw(_pixel, new Rectangle(panel.X, panel.Y, panel.Width, edge), interfaceUi.HeaderColor);
        _spriteBatch.Draw(_pixel, new Rectangle(panel.X, panel.Bottom - edge, panel.Width, edge), interfaceUi.HeaderColor);
        _spriteBatch.Draw(_pixel, new Rectangle(panel.X, panel.Y, edge, panel.Height), interfaceUi.HeaderColor);
        _spriteBatch.Draw(_pixel, new Rectangle(panel.Right - edge, panel.Y, edge, panel.Height), interfaceUi.HeaderColor);
        _spriteBatch.Draw(_pixel, header,
            ResolveEditorButtonStateFill(header, interfaceUi.HeaderColor, interfaceUi));
        _spriteBatch.Draw(_pixel, new Rectangle(header.X, header.Y, edge + (int)MathF.Round(2f * unit), header.Height),
            statusColor);

        var diagnosticsReady = !_editorDiagnosticsDirty && ReferenceEquals(_editorDiagnosticsChart, _chart);
        var diagnosticsPending = IsEditorDiagnosticsPendingForCurrentChart();
        var diagnosticsPreparing = IsEditorDiagnosticsPreparingForCurrentChart();
        var summary = !diagnosticsReady
            ? _playing && _editorDiagnosticsTask is null ? "Chart checks will run when playback pauses…" :
                diagnosticsPreparing ? "Preparing chart checks…" : diagnosticsPending ? "Checking chart…" : "Open to refresh chart checks."
            : (errorCount, warningCount) switch
            {
                (0, 0) => interfaceUi.Label("problems_none", "No problems found in this chart."),
                (0, 1) => "1 warning.",
                (0, _) => $"{warningCount} warnings.",
                (1, 0) => "1 gameplay error needs to be fixed.",
                (_, 0) => $"{errorCount} gameplay errors need to be fixed.",
                (1, _) => $"1 gameplay error · {warningCount} warning(s)",
                _ => $"{errorCount} gameplay errors · {warningCount} warning(s)"
            };
        var quickFixButton = GetEditorProblemsQuickFixButtonBounds(virtualWidth, virtualHeight);
        var summaryMax = Math.Max(1f, quickFixButton.X - header.X - 20f * unit);
        summary = FitHudText(labelFont, FontSafeText(labelFont, summary), summaryMax / textScale);
        var summaryScale = GetEditorDefaultFontScale(summary, 18f * unit);
        var summarySize = UnicodeTextRenderer.MeasureString(labelFont, summary) * summaryScale;
        UnicodeTextRenderer.DrawString(_spriteBatch, labelFont, summary,
            new Vector2(header.X + 12f * unit, header.Y + (header.Height - summarySize.Y) * 0.5f),
            errorCount > 0 ? new Color(255, 205, 208) : warningCount > 0 ? new Color(255, 226, 158) : new Color(165, 239, 198),
            0f, Vector2.Zero, summaryScale, SpriteEffects.None, 0f);
        var quickFixBorder = interfaceUi.AccentColor;
        var quickFixFill = ResolveEditorButtonStateFill(quickFixButton, interfaceUi.ButtonColor, interfaceUi);
        _spriteBatch.Draw(_pixel, quickFixButton, quickFixFill);
        _spriteBatch.Draw(_pixel, new Rectangle(quickFixButton.X, quickFixButton.Y, quickFixButton.Width, edge), quickFixBorder);
        _spriteBatch.Draw(_pixel, new Rectangle(quickFixButton.X, quickFixButton.Bottom - edge, quickFixButton.Width, edge), quickFixBorder);
        _spriteBatch.Draw(_pixel, new Rectangle(quickFixButton.X, quickFixButton.Y, edge, quickFixButton.Height), quickFixBorder);
        _spriteBatch.Draw(_pixel, new Rectangle(quickFixButton.Right - edge, quickFixButton.Y, edge, quickFixButton.Height), quickFixBorder);
        var quickFixFit = FitChartEditorText(
            interfaceUi.Label("button:QUICK FIX", "QUICK FIX"),
            24f * unit * (float)interfaceUi.ButtonTextScale,
            Math.Max(1f, quickFixButton.Width - 12f * unit),
            Math.Max(1f, quickFixButton.Height - 8f * unit));
        var quickFixTextSize = quickFixFit.Measured * quickFixFit.Scale;
        UnicodeTextRenderer.DrawString(_spriteBatch, quickFixFit.Font, quickFixFit.Text,
            new Vector2(quickFixButton.X + (quickFixButton.Width - quickFixTextSize.X) * 0.5f,
                quickFixButton.Y + (quickFixButton.Height - quickFixTextSize.Y) * 0.5f),
            interfaceUi.ButtonTextColor, 0f, Vector2.Zero, quickFixFit.Scale, SpriteEffects.None, 0f);

        var chevron = _editorProblemsExpanded ? "−" : "+";
        var chevronScale = GetEditorDefaultFontScale(chevron, 18f * unit);
        var chevronSize = UnicodeTextRenderer.MeasureString(labelFont, chevron) * chevronScale;
        UnicodeTextRenderer.DrawString(_spriteBatch, labelFont, chevron,
            new Vector2(header.Right - chevronSize.X - 12f * unit, header.Y + (header.Height - chevronSize.Y) * 0.5f),
            interfaceUi.ButtonTextColor, 0f, Vector2.Zero, chevronScale, SpriteEffects.None, 0f);

        if (_editorProblemsExpanded && list.Height > 0 && _editorProblems.Count > 0)
        {
            ClampEditorProblemsScroll(virtualWidth, virtualHeight);
            var rowPitch = Math.Max(22, (int)MathF.Round(30f * unit));
            var visibleRows = GetEditorProblemsVisibleRows(virtualWidth, virtualHeight);
            var fractionalOffset = GetEditorProblemsScrollPixelRemainder(virtualWidth, virtualHeight);
            for (var slot = 0; slot <= visibleRows; slot++)
            {
                var index = _editorProblemsScrollOffset + slot;
                if (index >= _editorProblems.Count) break;
                var problem = _editorProblems[index];
                var rowY = list.Y + slot * rowPitch - (int)MathF.Round((float)fractionalOffset);
                var rowTop = Math.Max(list.Y, rowY);
                var rowBottom = Math.Min(list.Bottom, rowY + rowPitch);
                if (rowBottom <= rowTop) continue;
                var row = new Rectangle(list.X, rowTop, list.Width, rowBottom - rowTop);
                var rowFill = index % 2 == 0 ? interfaceUi.ButtonColor : interfaceUi.PanelColor;
                _spriteBatch.Draw(_pixel, row, ResolveEditorButtonStateFill(row, rowFill, interfaceUi));
                var issueColor = problem.IsWarning ? yellow : red;
                if (row.Height > 6)
                    _spriteBatch.Draw(_pixel, new Rectangle(row.X, row.Y + 3,
                        Math.Max(2, (int)MathF.Round(3f * unit)), row.Height - 6), issueColor);
                var prefix = problem.Track >= 0 ? $"Tick {problem.Tick} · Track {problem.Track}: " : $"Tick {problem.Tick}: ";
                var rowText = FitHudText(labelFont, FontSafeText(labelFont, prefix + problem.Message),
                    Math.Max(1f, row.Width - 18f * unit) / textScale);
                var rowTextScale = GetEditorDefaultFontScale(rowText, 18f * unit);
                var textSize = UnicodeTextRenderer.MeasureString(labelFont, rowText) * rowTextScale;
                var textY = rowY + (rowPitch - textSize.Y) * 0.5f;
                if (textY >= list.Y && textY + textSize.Y <= list.Bottom)
                {
                    UnicodeTextRenderer.DrawString(_spriteBatch, labelFont, rowText,
                        new Vector2(row.X + 10f * unit, (float)textY),
                        problem.IsWarning ? new Color(255, 229, 168, 245) : new Color(247, 210, 213, 245),
                        0f, Vector2.Zero, rowTextScale, SpriteEffects.None, 0f);
                }
            }
            if (_editorProblems.Count > visibleRows && list.Height > 0)
            {
                var railWidth = Math.Max(2, (int)MathF.Round(3f * unit));
                var rail = new Rectangle(list.Right - railWidth, list.Y, railWidth, list.Height);
                var maxScroll = GetEditorProblemsScrollMaximum(virtualWidth, virtualHeight);
                TSC.UI.ScrollBarGeometry.Draw(_spriteBatch, _pixel, rail,
                    _editorProblemsScroll.Position, maxScroll,
                    GetEditorProblemsScrollContentExtent(virtualWidth, virtualHeight),
                    GetEditorProblemsScrollViewportExtent(virtualWidth, virtualHeight),
                    interfaceUi.HeaderColor, statusColor,
                    minimumThumbSize: Math.Max(15, (int)MathF.Round(15f * unit)));
            }
        }
        _spriteBatch.End();
    }

    private bool TryHandleEditorProblemsPanelClick(Point pixel, int virtualWidth, int virtualHeight)
    {
        if (!_chartEditorMode || _chart is null || _tempo is null)
            return false;
        var panel = GetEditorProblemsPanelBounds(virtualWidth, virtualHeight);
        if (!panel.Contains(pixel))
            return false;
        if (GetEditorProblemsQuickFixButtonBounds(virtualWidth, virtualHeight).Contains(pixel))
        {
            RefreshEditorDiagnostics();
            if (_editorDiagnosticsDirty || !ReferenceEquals(_editorDiagnosticsChart, _chart))
            {
                _statusMessage = _playing
                    ? "Chart checks will start after playback pauses. Try QUICK FIX again afterward."
                    : "Chart checks are still running. Try QUICK FIX again when they finish.";
                return true;
            }
            QuickFixEditorLinks();
            return true;
        }
        if (GetEditorProblemsHeaderBounds(virtualWidth, virtualHeight).Contains(pixel))
        {
            _editorProblemsExpanded = !_editorProblemsExpanded;
            if (_editorProblemsExpanded)
                _editorDiagnosticsSkipThisUpdate = true;
            // Keep the panel-opening frame free of chart scanning/capture.
            ClampEditorProblemsScroll(virtualWidth, virtualHeight);
            return true;
        }
        if (!_editorProblemsExpanded)
            return true;
        RefreshEditorDiagnostics();
        if (_editorDiagnosticsDirty)
            return true;
        var list = GetEditorProblemsListBounds(virtualWidth, virtualHeight);
        if (!list.Contains(pixel))
            return true;
        var rowPitch = Math.Max(22, (int)MathF.Round(30f * _projection.ScalePixels(1f)));
        var fractionalOffset = GetEditorProblemsScrollPixelRemainder(virtualWidth, virtualHeight);
        var visibleIndex = (int)Math.Floor((pixel.Y - list.Y + fractionalOffset) / rowPitch);
        var index = _editorProblemsScrollOffset + visibleIndex;
        if (index < 0 || index >= _editorProblems.Count)
            return true;
        var problem = _editorProblems[index];

        // A diagnostic should open the editor view where its track can be seen.
        // Panel tracks belong only to Panels view; gameplay notes belong only to Lane view.
        if (problem.Track is >= 17 and <= 20 && !_editorPanelsViewEnabled)
            ToggleEditorPanelsView();
        else if (problem.Track is >= 0 and <= 15 && _editorPanelsViewEnabled)
            ToggleEditorPanelsView();

        _playing = false;
        _playbackStartPending = false;
        _audioResyncPending = true;
        _playSeconds = Math.Clamp(_tempo.TickToSeconds(Math.Max(0, problem.Tick)), 0.0, ChartDuration());
        _statusMessage = $"Jumped to tick {problem.Tick}: {problem.Message}";
        return true;
    }

    private void DrawEditorProblemNoteMarkers(double currentTick, GearState currentState)
    {
        // Diagnostics are deliberately refreshed only when the mapper opens or
        // interacts with the Problems panel. Never run the full-chart checker
        // from the normal note-render pass after every edit.
        if (!_chartEditorMode || _chart is null || _editorDiagnosticsDirty ||
            !ReferenceEquals(_editorDiagnosticsChart, _chart))
            return;
        if (_editorProblemMarkerEvents.Count == 0)
            return;

        var width = _graphics.Viewport.Width;
        var height = _graphics.Viewport.Height;
        var red = new Color(255, 48, 64, 245);
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);
        foreach (var ev in _editorProblemMarkerEvents)
        {
            // The worker has already filtered this list to unique gameplay-lane
            // event references that require markers; do not rescan every chart event.
            // Match the projected size of the real note selection marker instead
            // of drawing a fixed-size square that can dwarf notes at a distance.
            var radius = GetEditorMarkerRadius(ev.Track, ev.Tick, currentTick, currentState,
                out var center, ev);
            if (ev.NoteType == NoteType.LinkTail)
                radius *= 0.5f;
            var markerSize = radius * 2f;
            if (!float.IsFinite(center.X) || !float.IsFinite(center.Y) || !float.IsFinite(markerSize) ||
                center.X < -markerSize || center.X > width + markerSize ||
                center.Y < -markerSize || center.Y > height + markerSize)
                continue;
            DrawEditorProblemSquareMarker(center, Math.Max(1.25f, radius), red, 0.07f);
        }
        _spriteBatch.End();
    }

    private void DrawEditorProblemSquareMarker(Vector2 center, float radius, Color color, float fillOpacity)
    {
        var side = Math.Max(2, (int)MathF.Round(radius * 2f));
        var rect = new Rectangle(
            (int)MathF.Round(center.X - side * 0.5f),
            (int)MathF.Round(center.Y - side * 0.5f), side, side);
        var thickness = Math.Max(1, (int)MathF.Ceiling(Math.Clamp(radius * 0.16f, 1f, 2.25f)));
        _spriteBatch.Draw(_pixel, rect, color * fillOpacity);
        _spriteBatch.Draw(_pixel, new Rectangle(rect.X, rect.Y, rect.Width, thickness), color);
        _spriteBatch.Draw(_pixel, new Rectangle(rect.X, rect.Bottom - thickness, rect.Width, thickness), color);
        _spriteBatch.Draw(_pixel, new Rectangle(rect.X, rect.Y + thickness, thickness,
            Math.Max(1, rect.Height - thickness * 2)), color);
        _spriteBatch.Draw(_pixel, new Rectangle(rect.Right - thickness, rect.Y + thickness, thickness,
            Math.Max(1, rect.Height - thickness * 2)), color);
    }
}
