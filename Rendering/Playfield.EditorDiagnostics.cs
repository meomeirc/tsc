using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using Point = Microsoft.Xna.Framework.Point;
using Color = Microsoft.Xna.Framework.Color;
using Microsoft.Xna.Framework.Graphics;
using TSC.Ares;

namespace TSC.Rendering;

public sealed partial class Playfield
{
    private sealed record EditorChartProblem(int Tick, int Track, string Message, ChartEvent? Event)
    {
        // Panel tracks contain background-animation cues only. Keep their issues
        // visible in the same report, but present them as warnings rather than
        // gameplay errors.
        public bool IsWarning => Track is >= 17 and <= 20;
    }

    private void RefreshEditorDiagnostics()
    {
        if (!_chartEditorMode || _chart is null)
        {
            _editorProblems.Clear();
            _editorProblemEventKeys.Clear();
            _editorProblemErrorCount = 0;
            _editorProblemWarningCount = 0;
            _editorDiagnosticsChart = null;
            _editorDiagnosticsDirty = false;
            return;
        }
        if (!_editorDiagnosticsDirty && ReferenceEquals(_editorDiagnosticsChart, _chart))
            return;

        _editorProblems.Clear();
        _editorProblemEventKeys.Clear();
        _editorDiagnosticsChart = _chart;
        _editorDiagnosticsDirty = false;

        var notes = _chart.Events
            .Where(ev => ev.Kind == "Note" && ev.RawType == 1 && ev.NoteType != NoteType.End)
            .OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track).ThenBy(ev => ev.EventIndex)
            .ToList();

        var playableNotes = notes.Where(ev => (ev.Track is >= 0 and <= 15) || (ev.Track is >= 17 and <= 20)).ToList();
        foreach (var group in playableNotes.GroupBy(ev => (ev.Tick, ev.Track)).Where(group => group.Count() > 1))
        {
            var first = group.First();
            var members = group.ToList();
            _editorProblems.Add(new EditorChartProblem(first.Tick, first.Track,
                $"Overlapping notes on track {first.Track} at tick {first.Tick} ({members.Count} notes).", first));
            foreach (var ev in members)
                _editorProblemEventKeys.Add(KeyOf(ev));
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
            _editorProblems.Add(new EditorChartProblem(first.Tick, first.Track, message, first));
            foreach (var ev in members)
                _editorProblemEventKeys.Add(KeyOf(ev));
        }

        // Build link adjacency before hold-overlap checks so a link's actual next
        // point is not mistaken for an unrelated note sitting inside its duration.
        // In particular, a same-track endpoint is drawn on top of that next point;
        // the generic overlap warning used to hide the real consecutive-vertical
        // connection problem and made the tail difficult to select for recovery.
        string? linkBuildFailure = null;
        try
        {
            LinkBuilder.Build(_chart);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            linkBuildFailure = ex.Message;
        }
        var builtLinks = _chart.Links.ToArray();
        var expectedLinkEndpoints = builtLinks
            .Select(link => (Head: link.Head, Tail: link.Tail))
            .ToHashSet();

        // A chain may use only one horizontal connector at any endpoint tick.
        // LinkSegment's connector is at Tail.Tick, even if Head.Tick is earlier.
        var editorChains = linkBuildFailure is null ? GetEditorLinkChainInfos() : new List<EditorLinkChainInfo>();
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
                if (!_editorProblems.Any(problem => string.Equals(problem.Message, message, StringComparison.Ordinal)))
                    _editorProblems.Add(new EditorChartProblem(group.Key, sameTickNodes.FirstOrDefault()?.Track ?? chain.Root.Track,
                        message, sameTickNodes.FirstOrDefault() ?? chain.Root));
                foreach (var node in sameTickNodes)
                    _editorProblemEventKeys.Add(KeyOf(node));
                foreach (var link in group)
                {
                    _editorProblemEventKeys.Add(KeyOf(link.Head));
                    _editorProblemEventKeys.Add(KeyOf(link.Tail));
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
                        _editorProblems.Add(new EditorChartProblem(ev.Tick, ev.Track, message, ev));
                        _editorProblemEventKeys.Add(KeyOf(connector.Head));
                        _editorProblemEventKeys.Add(KeyOf(connector.Tail));
                        _editorProblemEventKeys.Add(KeyOf(ev));
                    }
                }
            }
        }

        // A hold occupies the lane until its end tick. A later unrelated note
        // inside that interval is an overlap even when the notes start on
        // different ticks. Panel animation notes may hand off exactly at the
        // hold end tick. Connected link endpoints are diagnosed below instead.
        foreach (var hold in playableNotes
                     .Where(ev => ev.Tick >= 0 && ev.EndTick > ev.Tick &&
                                  (ev.IsHoldNote || ev.Duration > 6))
                     .OrderBy(ev => ev.Tick).ThenBy(ev => ev.Track).ThenBy(ev => ev.EventIndex))
        {
            foreach (var next in playableNotes.Where(ev => ev.Track == hold.Track && ev.Tick > hold.Tick && ev.Tick <= hold.EndTick))
            {
                if (hold.Track is >= 17 and <= 20 && next.Tick == hold.EndTick)
                    continue;
                if (expectedLinkEndpoints.Contains((Head: hold, Tail: next)))
                    continue;

                _editorProblems.Add(new EditorChartProblem(next.Tick, next.Track,
                    $"Overlapping note on track {hold.Track}: note at tick {next.Tick} falls inside the hold from tick {hold.Tick} to {hold.EndTick}.", next));
                _editorProblemEventKeys.Add(KeyOf(hold));
                _editorProblemEventKeys.Add(KeyOf(next));
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
                _editorProblemEventKeys.Add(KeyOf(link.Head));
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
            foreach (var issue in GetEditorLinkPathIssues(GetEditorLinkChainInfos()))
            {
                _editorProblems.Add(new EditorChartProblem(issue.Tick, issue.Track,
                    $"Link path: {issue.Message}", issue.Event));
                if (issue.Event is not null)
                    _editorProblemEventKeys.Add(KeyOf(issue.Event));
            }
        }
        else
        {
            _editorProblems.Add(new EditorChartProblem(0, -1, $"Could not validate link paths: {linkBuildFailure}", null));
        }

        foreach (var tempo in _chart.TempoEvents)
        {
            if (tempo.Tick < 0)
                _editorProblems.Add(new EditorChartProblem(tempo.Tick, -1, $"Tempo change has a negative tick ({tempo.Tick}).", null));
            if (!double.IsFinite(tempo.Bpm) || tempo.Bpm <= 0)
                _editorProblems.Add(new EditorChartProblem(tempo.Tick, -1, $"Tempo change has an invalid BPM value ({tempo.Bpm}).", null));
        }
        foreach (var group in _chart.TempoEvents.GroupBy(tempo => tempo.Tick).Where(group => group.Count() > 1))
        {
            var values = string.Join(", ", group.Select(tempo => tempo.Bpm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
            _editorProblems.Add(new EditorChartProblem(group.Key, -1,
                $"Multiple tempo changes share tick {group.Key} (BPM values: {values}).", null));
        }

        _editorProblems.Sort((a, b) =>
        {
            var tickComparison = a.Tick.CompareTo(b.Tick);
            if (tickComparison != 0) return tickComparison;
            var trackComparison = a.Track.CompareTo(b.Track);
            return trackComparison != 0 ? trackComparison : string.Compare(a.Message, b.Message, StringComparison.Ordinal);
        });
        _editorProblemErrorCount = _editorProblems.Count(problem => !problem.IsWarning);
        _editorProblemWarningCount = _editorProblems.Count(problem => problem.IsWarning);

        void AddProblem(ChartEvent ev, string message)
        {
            _editorProblems.Add(new EditorChartProblem(ev.Tick, ev.Track, message, ev));
            _editorProblemEventKeys.Add(KeyOf(ev));
        }
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

    private void ClampEditorProblemsScroll(int virtualWidth, int virtualHeight)
    {
        var visible = GetEditorProblemsScrollWindowRows(virtualWidth, virtualHeight);
        _editorProblemsScrollOffset = Math.Clamp(_editorProblemsScrollOffset, 0,
            Math.Max(0, _editorProblems.Count - visible));
    }

    private void DrawEditorProblemsPanel(int virtualWidth, int virtualHeight)
    {
        if (!_chartEditorMode || _chart is null || _tempo is null)
            return;
        RefreshEditorDiagnostics();
        var interfaceUi = _chartEditorInterfaceLayout;
        var labelFont = GetChartEditorFont(interfaceUi.LabelFont);
        var unit = _projection.ScalePixels(1f);
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

        var summary = (errorCount, warningCount) switch
        {
            (0, 0) => interfaceUi.Label("problems_none", "No problems found in this chart."),
            (0, 1) => "1 panel warning.",
            (0, _) => $"{warningCount} panel warnings.",
            (1, 0) => "1 gameplay error needs to be fixed.",
            (_, 0) => $"{errorCount} gameplay errors need to be fixed.",
            (1, _) => $"1 gameplay error · {warningCount} panel warning(s)",
            _ => $"{errorCount} gameplay errors · {warningCount} panel warning(s)"
        };
        var quickFixButton = GetEditorProblemsQuickFixButtonBounds(virtualWidth, virtualHeight);
        var summaryMax = Math.Max(1f, quickFixButton.X - header.X - 20f * unit);
        summary = FitHudText(labelFont, FontSafeText(labelFont, summary), summaryMax);
        var summarySize = UnicodeTextRenderer.MeasureString(labelFont, summary);
        UnicodeTextRenderer.DrawString(_spriteBatch, labelFont, summary,
            new Vector2(header.X + 12f * unit, header.Y + (header.Height - summarySize.Y) * 0.5f),
            errorCount > 0 ? new Color(255, 205, 208) : warningCount > 0 ? new Color(255, 226, 158) : new Color(165, 239, 198));
        var quickFixBorder = interfaceUi.AccentColor;
        var quickFixFill = ResolveEditorButtonStateFill(quickFixButton, interfaceUi.ButtonColor, interfaceUi);
        _spriteBatch.Draw(_pixel, quickFixButton, quickFixFill);
        _spriteBatch.Draw(_pixel, new Rectangle(quickFixButton.X, quickFixButton.Y, quickFixButton.Width, edge), quickFixBorder);
        _spriteBatch.Draw(_pixel, new Rectangle(quickFixButton.X, quickFixButton.Bottom - edge, quickFixButton.Width, edge), quickFixBorder);
        _spriteBatch.Draw(_pixel, new Rectangle(quickFixButton.X, quickFixButton.Y, edge, quickFixButton.Height), quickFixBorder);
        _spriteBatch.Draw(_pixel, new Rectangle(quickFixButton.Right - edge, quickFixButton.Y, edge, quickFixButton.Height), quickFixBorder);
        var quickFixFit = FitChartEditorText(
            interfaceUi.Label("button:QUICK FIX", "QUICK FIX"), interfaceUi.ButtonFont,
            24f * (float)interfaceUi.ButtonTextScale,
            Math.Max(1f, quickFixButton.Width - 12f * unit),
            Math.Max(1f, quickFixButton.Height - 8f * unit));
        var quickFixTextSize = quickFixFit.Measured * quickFixFit.Scale;
        UnicodeTextRenderer.DrawString(_spriteBatch, quickFixFit.Font, quickFixFit.Text,
            new Vector2(quickFixButton.X + (quickFixButton.Width - quickFixTextSize.X) * 0.5f,
                quickFixButton.Y + (quickFixButton.Height - quickFixTextSize.Y) * 0.5f),
            interfaceUi.ButtonTextColor, 0f, Vector2.Zero, quickFixFit.Scale, SpriteEffects.None, 0f);

        var chevron = _editorProblemsExpanded ? "−" : "+";
        var chevronSize = UnicodeTextRenderer.MeasureString(labelFont, chevron);
        UnicodeTextRenderer.DrawString(_spriteBatch, labelFont, chevron,
            new Vector2(header.Right - chevronSize.X - 12f * unit, header.Y + (header.Height - chevronSize.Y) * 0.5f), interfaceUi.ButtonTextColor);

        if (_editorProblemsExpanded && list.Height > 0 && _editorProblems.Count > 0)
        {
            ClampEditorProblemsScroll(virtualWidth, virtualHeight);
            var rowPitch = Math.Max(22, (int)MathF.Round(30f * unit));
            var visibleRows = GetEditorProblemsVisibleRows(virtualWidth, virtualHeight);
            for (var slot = 0; slot < visibleRows; slot++)
            {
                var index = _editorProblemsScrollOffset + slot;
                if (index >= _editorProblems.Count) break;
                var problem = _editorProblems[index];
                var row = new Rectangle(list.X, list.Y + slot * rowPitch, list.Width, rowPitch);
                var rowFill = index % 2 == 0 ? interfaceUi.ButtonColor : interfaceUi.PanelColor;
                _spriteBatch.Draw(_pixel, row, ResolveEditorButtonStateFill(row, rowFill, interfaceUi));
                var issueColor = problem.IsWarning ? yellow : red;
                _spriteBatch.Draw(_pixel, new Rectangle(row.X, row.Y + 3, Math.Max(2, (int)MathF.Round(3f * unit)), row.Height - 6), issueColor);
                var prefix = problem.Track >= 0 ? $"Tick {problem.Tick} · Track {problem.Track}: " : $"Tick {problem.Tick}: ";
                var rowText = FitHudText(labelFont, FontSafeText(labelFont, prefix + problem.Message), Math.Max(1f, row.Width - 18f * unit));
                var textSize = UnicodeTextRenderer.MeasureString(labelFont, rowText);
                UnicodeTextRenderer.DrawString(_spriteBatch, labelFont, rowText,
                    new Vector2(row.X + 10f * unit, row.Y + (row.Height - textSize.Y) * 0.5f),
                    problem.IsWarning ? new Color(255, 229, 168, 245) : new Color(247, 210, 213, 245));
            }
            if (_editorProblems.Count > visibleRows && list.Height > 0)
            {
                var railWidth = Math.Max(2, (int)MathF.Round(3f * unit));
                var rail = new Rectangle(list.Right - railWidth, list.Y, railWidth, list.Height);
                _spriteBatch.Draw(_pixel, rail, interfaceUi.HeaderColor);
                var thumbHeight = Math.Max(15, (int)MathF.Round(list.Height * visibleRows / (float)_editorProblems.Count));
                thumbHeight = Math.Min(list.Height, thumbHeight);
                var maxScroll = Math.Max(1, _editorProblems.Count - Math.Max(1, visibleRows));
                var thumbY = list.Y + (int)MathF.Round((list.Height - thumbHeight) * (_editorProblemsScrollOffset / (float)maxScroll));
                _spriteBatch.Draw(_pixel, new Rectangle(rail.X, thumbY, rail.Width, thumbHeight), statusColor);
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
        RefreshEditorDiagnostics();
        if (GetEditorProblemsQuickFixButtonBounds(virtualWidth, virtualHeight).Contains(pixel))
        {
            QuickFixEditorLinks();
            return true;
        }
        if (GetEditorProblemsHeaderBounds(virtualWidth, virtualHeight).Contains(pixel))
        {
            _editorProblemsExpanded = !_editorProblemsExpanded;
            ClampEditorProblemsScroll(virtualWidth, virtualHeight);
            return true;
        }
        if (!_editorProblemsExpanded)
            return true;
        var list = GetEditorProblemsListBounds(virtualWidth, virtualHeight);
        if (!list.Contains(pixel))
            return true;
        var rowPitch = Math.Max(22, (int)MathF.Round(30f * _projection.ScalePixels(1f)));
        var visibleIndex = (pixel.Y - list.Y) / rowPitch;
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
        if (!_chartEditorMode || _chart is null)
            return;
        RefreshEditorDiagnostics();
        if (_editorProblemEventKeys.Count == 0)
            return;

        var width = _graphics.Viewport.Width;
        var height = _graphics.Viewport.Height;
        var red = new Color(255, 48, 64, 245);
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);
        foreach (var ev in _chart.Events)
        {
            // This overlay is projected onto the gameplay lanes. Auxiliary
            // tracks are shown only by their dedicated editor view, never here.
            if (!_editorProblemEventKeys.Contains(KeyOf(ev)) || ev.Track is < 0 or > 15)
                continue;

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
