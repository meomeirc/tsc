using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using System.Globalization;
using TSC.Ares;

namespace TSC.Rendering;

public sealed partial class Playfield
{
    private static Dictionary<string, object?> EditorUiItem(string id, string label, string action,
        object? value = null, bool selected = false, bool enabled = true, string kind = "button", bool toggle = false)
        => new()
        {
            ["id"] = id,
            ["label"] = label,
            ["action"] = action,
            ["value"] = value,
            ["selected"] = selected,
            ["enabled"] = enabled,
            ["kind"] = kind,
            ["toggle"] = toggle
        };

    private static int GetEditorTimelineTraceIndex(GearState state, string side)
    {
        var bank = GearTimeline.ActiveBankForSide(state, side);
        if (string.Equals(side, "left", StringComparison.OrdinalIgnoreCase))
            return bank switch { "Forward" => 0, "Center" => 1, "Backward" => 2, _ => 1 };
        return bank switch { "Backward" => 3, "Center" => 4, "Forward" => 5, _ => 4 };
    }

    private static object ToEditorTimelineTraceData(IEnumerable<(double Seconds, GearState State)> route)
        => route.Select(point => (object?)new Dictionary<string, object?>
        {
            ["seconds"] = point.Seconds,
            ["left"] = GetEditorTimelineTraceIndex(point.State, "left"),
            ["right"] = GetEditorTimelineTraceIndex(point.State, "right")
        }).ToList();

    private (object Actual, object Intended, object Forecast) BuildEditorTimelineTraceRoutes(double playSeconds)
    {
        var duration = Math.Max(0, ChartDuration());
        playSeconds = Math.Clamp(double.IsFinite(playSeconds) ? playSeconds : 0, 0, duration);
        var activeState = _editorPreviewGearState?.Copy() ?? _gear?.AtTick(0) ?? new GearState();
        var historyCount = 0;
        while (historyCount < _editorTimelineRouteHistory.Count &&
               _editorTimelineRouteHistory[historyCount].Seconds <= playSeconds + 0.0001)
            historyCount++;

        var actualRoute = _editorTimelineRouteHistory.Take(historyCount)
            .Select(point => (Seconds: point.Seconds, State: point.State.Copy())).ToList();
        if (actualRoute.Count == 0)
            actualRoute.Add((0.0, _gear?.Initial.Copy() ?? activeState.Copy()));
        if (actualRoute[^1].Seconds < playSeconds - 0.0001)
            actualRoute.Add((playSeconds, activeState.Copy()));
        else
            actualRoute[^1] = (playSeconds, activeState.Copy());

        var intendedRoute = new List<(double Seconds, GearState State)>();
        var intendedState = _gear?.Initial.Copy() ?? activeState.Copy();
        intendedRoute.Add((0.0, intendedState.Copy()));
        EnsureEditorTimelineTraceCache();
        if (_gear is not null && _tempo is not null)
        {
            foreach (var group in _editorTimelineTraceSuperFlicks.GroupBy(ev => ev.Tick))
            {
                var transitionSeconds = _tempo.TickToSeconds(group.Key);
                if (transitionSeconds > duration) break;
                transitionSeconds = Math.Max(0, transitionSeconds);
                var before = intendedState.Copy();
                var appliedSides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var changed = false;
                foreach (var ev in group)
                {
                    var side = ev.GetSuperFlickSide();
                    if (side is null || appliedSides.Contains(side) ||
                        !before.CanApplySuperFlick(ev.Attr / 10, ev.Track)) continue;
                    appliedSides.Add(side);
                    changed |= intendedState.ApplySuperFlick(ev.Attr / 10, ev.Track);
                }
                if (changed) intendedRoute.Add((transitionSeconds, intendedState.Copy()));
            }
        }

        var forecastRoute = new List<(double Seconds, GearState State)> { (playSeconds, activeState.Copy()) };
        if (_gear is not null && _tempo is not null)
        {
            var forecastState = activeState.Copy();
            var eventIndex = 0;
            while (eventIndex < _editorTimelineTraceSuperFlicks.Count)
            {
                var tick = _editorTimelineTraceSuperFlicks[eventIndex].Tick;
                var groupStart = eventIndex;
                var nextEventIndex = eventIndex + 1;
                while (nextEventIndex < _editorTimelineTraceSuperFlicks.Count &&
                       _editorTimelineTraceSuperFlicks[nextEventIndex].Tick == tick)
                    nextEventIndex++;
                eventIndex = nextEventIndex;
                var transitionSeconds = _tempo.TickToSeconds(tick);
                if (transitionSeconds <= playSeconds + 0.0001) continue;
                if (transitionSeconds > duration) break;

                var before = forecastState.Copy();
                var appliedLeft = false;
                var appliedRight = false;
                var changed = false;
                for (var i = groupStart; i < nextEventIndex; i++)
                {
                    var ev = _editorTimelineTraceSuperFlicks[i];
                    var key = SuperFlickKeyOf(ev);
                    var wasManuallyHit = _hitEvents.Contains(KeyOf(ev));
                    var wasMissed = _missedEvents.Contains(KeyOf(ev));
                    var wasResolved = _resolvedSuperFlicks.Contains(key);
                    var alreadyScored = _scoreSuperFlickEntries.TryGetValue(key, out var scoreEntry) &&
                                        _scoredEntryIds.Contains(scoreEntry.Id);
                    var side = ev.GetSuperFlickSide();
                    if (side is null) continue;
                    var sideAlreadyApplied = side.Equals("left", StringComparison.OrdinalIgnoreCase)
                        ? appliedLeft : appliedRight;
                    if (wasManuallyHit || alreadyScored)
                    {
                        if (side.Equals("left", StringComparison.OrdinalIgnoreCase)) appliedLeft = true;
                        else appliedRight = true;
                        continue;
                    }
                    if (wasMissed || wasResolved || sideAlreadyApplied ||
                        !before.CanApplySuperFlick(ev.Attr / 10, ev.Track)) continue;
                    if (!forecastState.ApplySuperFlick(ev.Attr / 10, ev.Track)) continue;
                    if (side.Equals("left", StringComparison.OrdinalIgnoreCase)) appliedLeft = true;
                    else appliedRight = true;
                    changed = true;
                }
                if (changed) forecastRoute.Add((transitionSeconds, forecastState.Copy()));
            }
        }

        return (ToEditorTimelineTraceData(actualRoute), ToEditorTimelineTraceData(intendedRoute),
            ToEditorTimelineTraceData(forecastRoute));
    }

    private (double CaretOffset, double SelectionOffset, double SelectionWidth, double TextWidth) GetEditorTextFieldMetrics(
        string text, int caret, int anchor)
    {
        const string fontPath = "Roboto-Regular.ttf";
        var textSize = 24f * _projection.ScalePixels(1f);
        if (_chartEditorInterfaceRuntime.IsLoaded && _chartEditorInterfaceRuntime.LastError is null)
        {
            caret = Math.Clamp(caret, 0, text.Length);
            anchor = Math.Clamp(anchor, 0, text.Length);
            var (skinSelectionStart, skinSelectionEnd) = GetEditorTextSelection(text, caret, anchor);
            double Measure(string value) => _chartEditorInterfaceRuntime.MeasureTextWidthForUi(value, fontPath, textSize);
            var skinCaretOffset = Measure(text[..caret]);
            var skinSelectionOffset = Measure(text[..skinSelectionStart]);
            var skinSelectionEndOffset = Measure(text[..skinSelectionEnd]);
            var skinTextWidth = Measure(text);
            return (skinCaretOffset, skinSelectionOffset,
                Math.Max(0, skinSelectionEndOffset - skinSelectionOffset), skinTextWidth);
        }

        var font = GetChartEditorFont(_chartEditorInterfaceLayout.LabelFont,
            24f * _projection.ScalePixels(1f));
        var safeText = FontSafeText(font, text);
        caret = Math.Clamp(caret, 0, safeText.Length);
        anchor = Math.Clamp(anchor, 0, safeText.Length);
        var (selectionStart, selectionEnd) = GetEditorTextSelection(safeText, caret, anchor);
        var caretOffset = UnicodeTextRenderer.MeasureString(font, safeText[..caret]).X;
        var selectionOffset = UnicodeTextRenderer.MeasureString(font, safeText[..selectionStart]).X;
        var selectionEndOffset = UnicodeTextRenderer.MeasureString(font, safeText[..selectionEnd]).X;
        var textWidth = UnicodeTextRenderer.MeasureString(font, safeText).X;
        return (caretOffset, selectionOffset, Math.Max(0, selectionEndOffset - selectionOffset), textWidth);
    }

    private string GetEditorLuaChildLabel(int index)
    {
        if (index < 0 || index >= GetEditorMenuChildCount()) return string.Empty;
        return _editorMenuSection switch
        {
            EditorMenuSection.Notes => EditorToolLabels[index],
            EditorMenuSection.Tools => GetEditorEditToolLabel(index),
            EditorMenuSection.SuperFlick => _editorSuperFlickSourceBank is string source &&
                                            string.Equals(source, new[] { "Forward", "Center", "Backward" }[index], StringComparison.OrdinalIgnoreCase)
                ? $"SOURCE {BankModeLabel(source)}"
                : EditorSuperFlickToolLabels[index],
            EditorMenuSection.Snap => $"1/{EditorSnapDivisors[index]}",
            EditorMenuSection.Select => index switch
            {
                0 => GetEditorSelectionYLabel(),
                1 => _editorSelectXSelected ? "SELECTED" : "ALL",
                _ => "APPLY SELECT"
            },
            EditorMenuSection.Duration => index == 0 ? GetEditorDurationFieldDisplayText() :
                $"APPLY ({GetSelectedEditorDurationTargets().Count.ToString(CultureInfo.InvariantCulture)})",
            EditorMenuSection.Bpm => index switch
            {
                0 => string.IsNullOrEmpty(_editorBpmInputText) ? "ENTER VALUE" : _editorBpmInputText,
                1 => _editorAutoAdjustBpm ? "ON" : "OFF",
                2 => "FLATTEN BPM",
                _ => "TEMPO GENERATOR"
            },
            EditorMenuSection.Stutter => GetEditorStutterChildLabel(index),
            _ => string.Empty
        };
    }

    private bool IsEditorNoteToolSelected(int index) => index switch
    {
        0 => _editorPlacementEnabled && !_editorConnectorMode && _editorNoteType == NoteType.Single && _editorAttr == 0,
        1 => _editorPlacementEnabled && _editorConnectorMode,
        2 => _editorPlacementEnabled && !_editorConnectorMode && _editorNoteType == NoteType.Single && _editorAttr == 10,
        3 => _editorPlacementEnabled && !_editorConnectorMode && _editorNoteType == NoteType.Single && _editorAttr == 50,
        4 => _editorPlacementEnabled && !_editorConnectorMode && _editorNoteType == NoteType.Vibrato && _editorAttr == 9,
        5 => _editorPlacementEnabled && !_editorConnectorMode && _editorSuperFlickTool != EditorSuperFlickTool.None,
        _ => false
    };

    private string GetEditorStutterChildLabel(int index)
    {
        if (index == 0) return _editorStutterStartPlacementPending ? "TAP LANE" :
            _editorStutterStartTick >= 0 ? _editorStutterStartTick.ToString(CultureInfo.InvariantCulture) : "PICK START";
        if (index == 1) return _editorStutterEndPlacementPending ? "TAP LANE" :
            _editorStutterStartTick >= 0 && _editorStutterEndTick > _editorStutterStartTick
                ? _editorStutterEndTick.ToString(CultureInfo.InvariantCulture) : "PICK END";
        if (index == 2) return _editorStutterRamp ? "RAMP" : _editorStutterAlternate ? "ALTERNATE" : "PULSE";
        if (index == 3) return FormatEditorStutterInput(_editorStutterRamp ? EditorStutterInputField.RampStartBpm : EditorStutterInputField.SlowBpm,
            _editorStutterRamp ? _editorRampStartBpm : _editorStutterSlowBpm);
        if (index == 4) return _editorStutterRamp
            ? FormatEditorStutterInput(EditorStutterInputField.RampTargetBpm, _editorRampTargetBpm)
            : _editorStutterAlternate
                ? FormatEditorStutterInput(EditorStutterInputField.SlowTimeBeats, _editorStutterSlowTimeBeats)
                : FormatEditorStutterInput(EditorStutterInputField.PulseIntervalBeats, _editorStutterPulseIntervalBeats);
        if (index == 5) return _editorStutterRamp
            ? EditorRampResolutionLabels[Math.Clamp(_editorRampResolutionIndex, 0, EditorRampResolutionLabels.Length - 1)]
            : FormatEditorStutterInput(EditorStutterInputField.FastBpm, _editorStutterFastBpm);
        if (index == 6) return _editorStutterRamp
            ? (_editorAutoAdjustBpm ? "AUTO ADJUST: ON" : "AUTO ADJUST: OFF")
            : FormatEditorStutterInput(EditorStutterInputField.FastTimeBeats, _editorStutterFastTimeBeats);
        if (index == 7) return _editorStutterRamp ? "MOVE UP" : _editorStutterBalanced ? "BALANCED" : "FREE";
        if (index == 8) return _editorStutterRamp ? "MOVE DOWN" : (_editorAutoAdjustBpm ? "AUTO ADJUST: ON" : "AUTO ADJUST: OFF");
        return index switch { 9 => "GENERATE", 10 => "DELETE", 11 => "UNGROUP", _ => string.Empty };
    }

    private Dictionary<string, object?> BuildChartEditorInterfaceState(int width, int height)
    {
        RefreshEditorDiagnostics();
        var rightActions = new List<object?>();
        foreach (var id in GetVisibleEditorActionIds())
        {
            if (!IsEditorActionVisible(id)) continue;
            var label = id == EditorActionPlay && _playing ? "PAUSE" : EditorActionLabels[id];
            var action = EditorUiItem($"right_{id}", label, "editor.action", id,
                selected: id == EditorActionSelect && _editorMenuSection == EditorMenuSection.Select,
                enabled: !(id is EditorActionUndo && _editorUndoStack.Count == 0) &&
                         !(id is EditorActionRedo && _editorRedoStack.Count == 0),
                toggle: id == EditorActionSelect);
            if (id == EditorActionDelete) action["variant"] = "danger";
            rightActions.Add(action);
        }

        var leftActions = new List<object?>();
        foreach (var id in EditorBottomLeftActionIds)
        {
            var label = id switch
            {
                EditorActionPlay => _playing ? "PAUSE" : "PLAY",
                EditorActionViewMode => _topDownViewEnabled ? "VIEW 2D" : "VIEW 3D",
                EditorActionEditorPreview => "PREVIEW",
                EditorActionGameplayMode => "AUTOPLAY",
                EditorActionExpand => _compact ? "COLLAPSED" : "EXPANDED",
                EditorActionBeatLines => "GRIDS",
                EditorActionAttributes => "ATTR",
                EditorActionTempoLines => "TEMPO LINES",
                EditorActionConstantScroll => "CONSTANT",
                EditorActionSnap => "SNAPS",
                EditorActionMetronome => "METRONOME",
                EditorActionPanels => "PANELS",
                EditorActionMeta => "INFO",
                EditorActionQuickSave => "SAVE",
                EditorActionSettings => "SETTINGS",
                _ => EditorActionLabels[id]
            };
            var selected = id switch
            {
                EditorActionPlay => _playing,
                EditorActionViewMode => _topDownViewEnabled,
                EditorActionEditorPreview => _editorPreviewMode,
                EditorActionGameplayMode => !_gameplayMode,
                EditorActionExpand => _compact,
                EditorActionBeatLines => _beatLines,
                EditorActionAttributes => _editorShowAttributes,
                EditorActionTempoLines => _tempoLines,
                EditorActionConstantScroll => _constantScroll,
                EditorActionSnap => _editorMenuSection == EditorMenuSection.Snap,
                EditorActionMetronome => _editorMenuSection == EditorMenuSection.Metronome,
                EditorActionPanels => _editorPanelsViewEnabled,
                EditorActionMeta => _editorMetaPanelOpen,
                _ => false
            };
            var toggle = id is EditorActionPlay or EditorActionViewMode or EditorActionExpand or EditorActionBeatLines or
                EditorActionAttributes or EditorActionPanels or EditorActionSnap or EditorActionTempoLines or EditorActionConstantScroll or
                EditorActionMetronome or EditorActionMeta or EditorActionEditorPreview or EditorActionGameplayMode;
            var leftAction = EditorUiItem($"left_{id}", label, "editor.action", id, selected,
                enabled: !(id == EditorActionExpand && !_topDownViewEnabled && _editorPanelsViewEnabled), toggle: toggle);
            if (id == EditorActionQuickSave && _editorHasUnsavedChanges)
                leftAction["variant"] = "warning";
            leftActions.Add(leftAction);
        }

        var tabs = new List<object?>();
        var activeRoot = GetEditorMenuRootSection();
        var tabLabels = new[] { "NOTES", "TOOLS", "BPM", "SELECT" };
        for (var index = 0; index < tabLabels.Length; index++)
        {
            var target = index switch
            {
                0 => EditorMenuSection.Notes,
                1 => EditorMenuSection.Tools,
                2 => EditorMenuSection.Bpm,
                _ => EditorMenuSection.Select
            };
            tabs.Add(EditorUiItem($"tab_{index}", tabLabels[index], "editor.tab", index,
                activeRoot == target));
        }

        var sectionName = _editorMenuSection.ToString().ToLowerInvariant();
        var children = new List<object?>();
        var childLabels = new List<object?>();
        var stutterPatterns = new List<object?>();
        if (_editorMenuSection == EditorMenuSection.Stutter &&
            !_editorStutterStartPlacementPending && !_editorStutterEndPlacementPending)
        {
            stutterPatterns.Add(EditorUiItem("stutter_pattern_pulse", "PULSE", "editor.stutter.pattern", "pulse",
                !_editorStutterAlternate && !_editorStutterRamp, toggle: true));
            stutterPatterns.Add(EditorUiItem("stutter_pattern_alternate", "ALTERNATE", "editor.stutter.pattern", "alternate",
                _editorStutterAlternate && !_editorStutterRamp, toggle: true));
            stutterPatterns.Add(EditorUiItem("stutter_pattern_ramp", "RAMP", "editor.stutter.pattern", "ramp",
                _editorStutterRamp, toggle: true));
        }
        for (var i = 0; i < GetEditorMenuChildCount(); i++)
        {
            if (_editorMenuSection == EditorMenuSection.Stutter && i == 2)
                continue;
            var enabled = !(_editorMenuSection == EditorMenuSection.Duration && i == 1 && GetSelectedEditorDurationTargets().Count == 0);
            var selected = (_editorMenuSection == EditorMenuSection.Snap && EditorSnapDivisors[i] == _editorSnapDivisor) ||
                           (_editorMenuSection == EditorMenuSection.SuperFlick && _editorSuperFlickTool != EditorSuperFlickTool.None) ||
                           (_editorMenuSection == EditorMenuSection.Notes && IsEditorNoteToolSelected(i)) ||
                           (_editorMenuSection == EditorMenuSection.Bpm && i == 1 && _editorAutoAdjustBpm) ||
                           (_editorMenuSection == EditorMenuSection.Tools && i == 8 && _editorAutoMergeLinks) ||
                           (_editorMenuSection == EditorMenuSection.Select && i == 1 && _editorSelectXSelected) ||
                           (_editorMenuSection == EditorMenuSection.Stutter &&
                            ((i == 6 && _editorStutterRamp && _editorAutoAdjustBpm) ||
                             (i == 7 && !_editorStutterRamp && _editorStutterBalanced) ||
                             (i == 8 && !_editorStutterRamp && _editorAutoAdjustBpm)));
            var toggle = (_editorMenuSection == EditorMenuSection.Notes && i < 6) ||
                         (_editorMenuSection == EditorMenuSection.Bpm && i == 1) ||
                         (_editorMenuSection == EditorMenuSection.Tools && i == 8) ||
                         (_editorMenuSection == EditorMenuSection.Select && i == 1) ||
                         (_editorMenuSection == EditorMenuSection.Stutter &&
                          ((i == 6 && _editorStutterRamp) || (i is 7 or 8 && !_editorStutterRamp)));
            var child = EditorUiItem($"child_{i}", GetEditorLuaChildLabel(i), "editor.child", i,
                selected: selected,
                enabled: enabled,
                kind: string.IsNullOrWhiteSpace(GetEditorMenuControlLabel(i)) ? "button" : GetEditorMenuControlLabel(i),
                toggle: toggle);
            if ((_editorMenuSection == EditorMenuSection.Bpm && i == 2) ||
                (_editorMenuSection == EditorMenuSection.Tools &&
                 string.Equals(GetEditorEditToolLabel(i), "REPLACE SELECTION", StringComparison.Ordinal)))
                child["variant"] = "warning";
            else if (_editorMenuSection == EditorMenuSection.Select && i == 2)
                child["variant"] = "success";
            children.Add(child);
            childLabels.Add(GetEditorMenuControlLabel(i));
        }

        var panelAttributes = new List<object?>();
        string AttributeColor(int attr) => attr switch
        {
            101 => "#FF3B30", 102 => "#FF9500", 103 => "#FFD60A", 104 => "#34C759",
            105 => "#30D1E8", 106 => "#007AFF", 107 => "#AF52DE", 108 => "#FF2D92",
            _ => "#FFFFFF"
        };
        for (var i = 0; i < 8; i++)
        {
            var attr = 101 + i;
            var item = EditorUiItem($"panel_attr_{attr}", attr.ToString(CultureInfo.InvariantCulture),
                "editor.panel_attr", attr, _editorPanelsSelectedAttr == attr && _editorPlacementEnabled);
            item["attribute_color"] = AttributeColor(attr);
            panelAttributes.Add(item);
        }

        var snapOptions = EditorSnapDivisors.Select((divisor, index) =>
            (object?)EditorUiItem($"snap_{index}", $"1/{divisor}", "editor.snap", divisor, divisor == _editorSnapDivisor)).ToList();
        var metronomeOptions = EditorMetronomeDivisors.Select((divisor, index) =>
            (object?)EditorUiItem($"metro_{index}", GetEditorMetronomeLabel(divisor), "editor.metronome", divisor, divisor == _editorMetronomeDivisor)).ToList();

        var problems = new List<object?>();
        var errorCount = _editorProblemErrorCount;
        var warningCount = _editorProblemWarningCount;
        var problemStart = _editorProblemsExpanded
            ? Math.Clamp(_editorProblemsScrollOffset, 0, Math.Max(0, _editorProblems.Count - LAYOUT_PROBLEM_ROW_COUNT))
            : 0;
        var problemEnd = Math.Min(_editorProblems.Count, problemStart + LAYOUT_PROBLEM_ROW_COUNT);
        for (var i = problemStart; i < problemEnd; i++)
        {
            var issue = _editorProblems[i];
            problems.Add(new Dictionary<string, object?>
            {
                ["label"] = (issue.Track >= 0 ? $"Tick {issue.Tick} · Track {issue.Track}: " : $"Tick {issue.Tick}: ") + issue.Message,
                ["warning"] = issue.IsWarning,
                ["index"] = i
            });
        }

        var currentTick = _tempo?.SecondsToTick(_playSeconds) ?? 0.0;
        var rows = GetEditorMetaRows(currentTick);
        var bpmInputMetrics = GetEditorTextFieldMetrics(_editorBpmInputText, _editorBpmInputCaretIndex, _editorBpmSelectionAnchor);
        var durationInputText = GetEditorDurationFieldDisplayText();
        var durationInputMetrics = GetEditorTextFieldMetrics(durationInputText, _editorDurationInputCaretIndex, _editorDurationSelectionAnchor);
        var stutterInputMetrics = GetEditorTextFieldMetrics(_editorStutterInputText,
            _editorStutterInputCaretIndex, _editorStutterSelectionAnchor);
        var stutterInputChild = _editorStutterInputField switch
        {
            EditorStutterInputField.RampStartBpm or EditorStutterInputField.SlowBpm => 3,
            EditorStutterInputField.RampTargetBpm or EditorStutterInputField.SlowTimeBeats or
                EditorStutterInputField.PulseIntervalBeats => 4,
            EditorStutterInputField.FastBpm => 5,
            EditorStutterInputField.FastTimeBeats => 6,
            _ => -1
        };
        var metaRows = rows.Select((row, index) =>
        {
            var fieldKey = GetEditorMetaFieldKey(row.Label);
            var editing = fieldKey is not null &&
                string.Equals(fieldKey, _editorMetaEditingField, StringComparison.OrdinalIgnoreCase);
            var value = editing ? _editorMetaInputText : row.Value;
            var kind = fieldKey is not null ? "text" : row.Label switch
            {
                "ELEMENT" or "CUSTOM JUDGEMENT WINDOWS" => "toggle",
                "DELETE DIFFICULTY" => "danger",
                "MUSIC" => "picker",
                _ => "readonly"
            };
            var selected = row.Label switch
            {
                "ELEMENT" => _editorElementDropdownOpen,
                "DIFFICULTY" => IsEditorDifficultyEnabled(_editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart?.Difficulty, _sourceName)),
                "CUSTOM JUDGEMENT WINDOWS" => _editorCustomJudgementWindowsEnabled,
                _ => false
            };
            var item = new Dictionary<string, object?>
            {
                ["label"] = row.Label,
                ["value"] = value,
                ["index"] = index,
                ["kind"] = kind,
                ["editable"] = fieldKey is not null,
                ["editing"] = editing,
                ["selected"] = selected
            };
            if (row.Label == "ELEMENT")
            {
                // The element picker is a neutral control; show the current element
                // color only as its side accent, including while the list is open.
                var element = (_chart?.Element ?? string.Empty).Trim().ToUpperInvariant();
                item["variant"] = "secondary";
                item["element_color"] = element switch
                {
                    "DANCER" => "#F87171",
                    "VOCAL" => "#60A5FA",
                    "SESSION" => "#34D399",
                    _ => "#6B7280"
                };
                item["element_hover_color"] = element switch
                {
                    "DANCER" => "#FDA4AF",
                    "VOCAL" => "#93C5FD",
                    "SESSION" => "#6EE7B7",
                    _ => "#9CA3AF"
                };
            }
            if (editing)
            {
                var caretIndex = Math.Clamp(_editorMetaInputCaretIndex, 0, value.Length);
                var anchorIndex = Math.Clamp(_editorMetaSelectionAnchor, 0, value.Length);
                var metrics = GetEditorTextFieldMetrics(value, caretIndex, anchorIndex);
                item["caret_offset"] = metrics.CaretOffset;
                item["selection_offset"] = metrics.SelectionOffset;
                item["selection_width"] = metrics.SelectionWidth;
                item["text_width"] = metrics.TextWidth;
                // Lua works with UTF-8 byte offsets. Providing byte indices lets a
                // wrapped active field place its caret and selection on the correct
                // line rather than treating the whole value as one horizontal line.
                item["caret_byte_index"] = System.Text.Encoding.UTF8.GetByteCount(value[..caretIndex]);
                item["anchor_byte_index"] = System.Text.Encoding.UTF8.GetByteCount(value[..anchorIndex]);
            }
            return (object?)item;
        }).ToList();
        var metaCategories = EditorMetaCategories.Select((category, index) =>
            (object?)EditorUiItem($"meta_category_{index}", category, "editor.meta.category", index,
                string.Equals(category, _editorMetaCategory, StringComparison.OrdinalIgnoreCase))).ToList();
        Dictionary<string, object?> ElementOption(string id, string label, int index, string variant)
        {
            var option = EditorUiItem(id, label, "editor.meta.element_option", index,
                string.Equals(_chart?.Element, label, StringComparison.OrdinalIgnoreCase));
            option["variant"] = "secondary";
            option["element_color"] = index switch
            {
                0 => "#F87171",
                1 => "#60A5FA",
                _ => "#34D399"
            };
            option["element_hover_color"] = index switch
            {
                0 => "#FDA4AF",
                1 => "#93C5FD",
                _ => "#6EE7B7"
            };
            return option;
        }
        var difficulties = EditorDifficultyCodes.Select((code, index) =>
        {
            var item = EditorUiItem($"difficulty_{index}", GetEditorDifficultyName(code).ToUpperInvariant(), "editor.difficulty", index,
                string.Equals(code, _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart?.Difficulty, _sourceName), StringComparison.OrdinalIgnoreCase));
            item["enabled"] = true;
            item["variant"] = "secondary";
            return (object?)item;
        }).ToList();
        var exportOptions = Enumerable.Range(0, GetEditorMetaExportOptionCount()).Select(index =>
        {
            var option = GetEditorMetaExportOption(index);
            var item = EditorUiItem($"meta_export_{index}", option.Label, "editor.meta.export_option", index);
            item["description"] = option.Description;
            item["variant"] = "secondary";
            return (object?)item;
        }).ToList();

        var previewState = EnsureEditorPreviewGearState();
        SyncEditorPreviewBankIndicators(previewState);
        var intended = _gear?.AtTick((int)Math.Floor(currentTick), includeTickChanges: true) ?? new GearState();
        var banks = new List<object?>();
        foreach (var side in Sides)
        foreach (var bank in EditorBankNames)
        {
            var active = GearTimeline.ActiveBankForSide(previewState, side);
            var authored = GearTimeline.ActiveBankForSide(intended, side);
            var bounds = GetEditorBankIndicatorBounds(side, bank, width, height);
            var item = EditorUiItem($"bank_{side}_{bank}", bank switch { "Forward" => "1K", "Center" => "2K", _ => "3K" },
                "editor.bank", $"{side}|{bank}",
                string.Equals(active, bank, StringComparison.OrdinalIgnoreCase));
            var intendedBank = string.Equals(authored, bank, StringComparison.OrdinalIgnoreCase);
            var isAlternateSelected = string.Equals(active, bank, StringComparison.OrdinalIgnoreCase) &&
                                      !string.Equals(active, authored, StringComparison.OrdinalIgnoreCase);
            item["intended"] = intendedBank;
            item["variant"] = isAlternateSelected ? "danger" : intendedBank
                ? (item["selected"] is true ? "success" : "warning")
                : "secondary";
            item["x"] = bounds.X;
            item["y"] = bounds.Y;
            item["width"] = bounds.Width;
            item["height"] = bounds.Height;
            item["intended"] = string.Equals(authored, bank, StringComparison.OrdinalIgnoreCase);
            banks.Add(item);
        }
        var timelineRoutes = BuildEditorTimelineTraceRoutes(_playSeconds);

        var summary = (errorCount, warningCount) switch
        {
            (0, 0) => "No problems found in this chart.",
            (0, 1) => "1 warning.",
            (0, _) => $"{warningCount} warnings.",
            (1, 0) => "1 gameplay error needs to be fixed.",
            (_, 0) => $"{errorCount} gameplay errors need to be fixed.",
            (1, _) => $"1 gameplay error · {warningCount} warning(s)",
            _ => $"{errorCount} gameplay errors · {warningCount} warning(s)"
        };
        var hudTick = _tempo?.SecondsToTick(_playSeconds) ?? 0.0;
        var hudGear = _gear?.AtTick((int)Math.Floor(hudTick), includeTickChanges: true) ?? new GearState();
        var hudBpm = _tempo?.BpmAt(hudTick) ?? 0.0;
        var hudSelection = _selectedEventKey.HasValue
            ? GetSelectedEventHudText()
            : _selectedBeatTick.HasValue
                ? $"SELECTED 1/4 BEAT   tick {_selectedBeatTick.Value}  track {_selectedBeatTrack?.ToString(CultureInfo.InvariantCulture) ?? "?"}"
                : "Click a note or lane to inspect its tick and track";
        var scoreDebugLines = _scoreDebugEnabled && _scoreEntries.Count > 0
            ? GetScoreDebugText(hudGear, hudTick).Split('\n').Cast<object?>().ToList()
            : new List<object?>();
        var superFlickDebugLines = new List<object?>();
        if (_superFlickDebugEnabled)
        {
            superFlickDebugLines.Add($"SUPER FLICK DEBUG (F9 off)  log: {_superFlickDebugPath}");
            superFlickDebugLines.AddRange(_superFlickDebugRecent.Take(8)
                .Select(line => line.Length > 150 ? line[^150..] : line)
                .Cast<object?>());
        }
        var auxDebugLines = new List<object?>();
        var auxDebugTitle = string.Empty;
        var auxDebugCursor = string.Empty;
        if (_showAuxTrackDebug && _chart is not null)
        {
            const int maxAuxEvents = 12;
            var currentAuxTick = Math.Max(0.0, currentTick);
            var auxEvents = _chart.Events
                .Where(ev => ev.Track >= 12 && ev.Tick >= currentAuxTick - 1e-6)
                .OrderBy(ev => ev.Tick)
                .ThenBy(ev => ev.Track)
                .ThenBy(ev => ev.EventIndex)
                .Take(maxAuxEvents)
                .ToArray();
            auxDebugTitle = $"AUX TRACKS 12+ · NEXT {auxEvents.Length}/{maxAuxEvents}";
            auxDebugCursor = $"current tick {currentAuxTick:0.###}   total {_chart.Events.Count(ev => ev.Track >= 12)}";
            foreach (var ev in auxEvents)
            {
                var line = ev.RawType == 1
                    ? $"tick {ev.Tick,7}  tr{ev.Track:D2}  ref{ev.TypeIndex:D2}  type1  attr{ev.Attr}" +
                      (ev.Duration != 6 ? $"  dur{ev.Duration}" : string.Empty)
                    : $"tick {ev.Tick,7}  tr{ev.Track:D2}  ref{ev.TypeIndex:D2}  type{ev.RawType}  value{ev.Value:0.###}";
                auxDebugLines.Add(new Dictionary<string, object?>
                {
                    ["label"] = line,
                    ["active"] = ev.RawType == 1
                });
            }
        }
        var previewStartMetrics = _editorPreviewFocusedField == 0
            ? GetEditorTextFieldMetrics(_editorPreviewStartText, _editorPreviewStartCaret, _editorPreviewStartSelectionAnchor)
            : (CaretOffset: 0d, SelectionOffset: 0d, SelectionWidth: 0d, TextWidth: 0d);
        var previewEndMetrics = _editorPreviewFocusedField == 1
            ? GetEditorTextFieldMetrics(_editorPreviewEndText, _editorPreviewEndCaret, _editorPreviewEndSelectionAnchor)
            : (CaretOffset: 0d, SelectionOffset: 0d, SelectionWidth: 0d, TextWidth: 0d);
#if TSC_PUBLISH
        const string editorControls = "EDITOR: Ctrl+S TSC  Ctrl+Shift+S ARES. Select a tool and tap a lane to place notes.";
#else
        const string editorControls = "EDITOR: Ctrl+S TSC  Ctrl+Shift+S ARES  Ctrl+Alt+S FPK. Select a tool and tap a lane to place notes.";
#endif

        var state = new Dictionary<string, object?>
        {
            ["actions_right"] = rightActions,
            ["actions_left"] = leftActions,
            ["tabs"] = tabs,
            ["section"] = sectionName,
            ["root_section"] = activeRoot.ToString().ToLowerInvariant(),
            ["menu_title"] = GetEditorLuaMenuTitle(),
            ["children"] = children,
            ["control_labels"] = childLabels,
            ["stutter_patterns"] = stutterPatterns,
            ["menu_width"] = _editorMenuSection switch
            {
                EditorMenuSection.Stutter when _editorStutterStartPlacementPending || _editorStutterEndPlacementPending => 300f,
                EditorMenuSection.Stutter => 540f,
                EditorMenuSection.Tools => 440f,
                _ => (float)_chartEditorInterfaceLayout.ToolbarWidth
            },
            ["panels_view"] = _editorPanelsViewEnabled,
            ["connector_active"] = _editorConnectorMode,
            ["panel_connector"] = EditorUiItem("panel_connector", "CONNECTOR", "editor.panel_connector", null, _editorConnectorMode),
            ["panel_select"] = EditorUiItem("panel_select", "SELECT", "editor.panel_select", null, _editorMenuSection == EditorMenuSection.Select),
            ["panel_duration"] = EditorUiItem("panel_duration", "DURATION", "editor.panel_duration", null, _editorMenuSection == EditorMenuSection.Duration),
            ["panel_attributes"] = panelAttributes,
            ["snap_options"] = snapOptions,
            ["metronome_options"] = metronomeOptions,
            ["scroll_speed"] = $"{_projection.ScrollSpeedScale:0.00}x",
            ["playing"] = _playing,
            ["play_seconds"] = _playSeconds,
            ["duration_seconds"] = ChartDuration(),
            ["play_label"] = FormatEditorTimestamp(_playSeconds),
            ["duration_label"] = FormatEditorTimestamp(ChartDuration()),
            ["timeline_marks"] = Enumerable.Range(0, 5)
                .Select(index => (object?)FormatEditorTimestamp(ChartDuration() * index / 4.0)).ToList(),
            ["timeline_actual_route"] = timelineRoutes.Actual,
            ["timeline_intended_route"] = timelineRoutes.Intended,
            ["timeline_forecast_route"] = timelineRoutes.Forecast,
            ["bpm_input_focused"] = _editorMenuSection == EditorMenuSection.Bpm && _editorBpmInputFocused,
            ["duration_input_focused"] = _editorMenuSection == EditorMenuSection.Duration && _editorDurationInputFocused,
            ["bpm_input_caret_offset"] = bpmInputMetrics.CaretOffset,
            ["bpm_input_selection_offset"] = bpmInputMetrics.SelectionOffset,
            ["bpm_input_selection_width"] = bpmInputMetrics.SelectionWidth,
            ["bpm_input_text_width"] = bpmInputMetrics.TextWidth,
            ["duration_input_caret_offset"] = durationInputMetrics.CaretOffset,
            ["duration_input_selection_offset"] = durationInputMetrics.SelectionOffset,
            ["duration_input_selection_width"] = durationInputMetrics.SelectionWidth,
            ["duration_input_text_width"] = durationInputMetrics.TextWidth,
            ["stutter_ramp"] = _editorStutterRamp,
            ["stutter_input_focused"] = _editorStutterInputField != EditorStutterInputField.None,
            ["stutter_input_child"] = stutterInputChild,
            ["stutter_input_caret_offset"] = stutterInputMetrics.CaretOffset,
            ["stutter_input_selection_offset"] = stutterInputMetrics.SelectionOffset,
            ["stutter_input_selection_width"] = stutterInputMetrics.SelectionWidth,
            ["stutter_input_text_width"] = stutterInputMetrics.TextWidth,
            ["hud_visible"] = !_hideDebugHud,
            ["hud_line"] = $"{FormatEditorProgressTimestamp(_playSeconds)} / {FormatEditorProgressTimestamp(ChartDuration())}   tick {hudTick:0}   BPM {hudBpm:0.##}",
            ["selection_line"] = hudSelection,
            ["selection_active"] = _selectedEventKey.HasValue || _selectedBeatTick.HasValue,
            ["play_status"] = (_gameplayMode ? "GAMEPLAY " : "AUTO ") + (_playing ? "PLAYING" : "PAUSED"),
            ["editor_controls"] = editorControls,
            ["source_name"] = _sourceName ?? string.Empty,
            ["status_message"] = _statusMessage ?? string.Empty,
            ["score_debug_lines"] = scoreDebugLines,
            ["super_flick_debug_lines"] = superFlickDebugLines,
            ["aux_debug_visible"] = _showAuxTrackDebug && _chart is not null,
            ["aux_debug_title"] = auxDebugTitle,
            ["aux_debug_cursor"] = auxDebugCursor,
            ["aux_debug_empty"] = auxDebugLines.Count == 0,
            ["aux_debug_lines"] = auxDebugLines,
            ["meta_open"] = _editorMetaPanelOpen,
            ["meta_title"] = "SONG / CHART META",
            ["meta_panel_width"] = _chartEditorInterfaceLayout.MetadataPanelWidth,
            ["meta_panel_margin"] = _chartEditorInterfaceLayout.MetadataPanelMargin,
            ["meta_category"] = _editorMetaCategory,
            ["meta_categories"] = metaCategories,
            ["meta_difficulties"] = difficulties,
            ["meta_rows"] = metaRows,
            ["meta_visible_rows"] = GetEditorMetaVisibleRowCount(rows, width, height),
            ["meta_scroll"] = _editorMetaScrollOffset,
            ["meta_scroll_max"] = GetEditorMetaMaxScrollOffset(rows, width, height),
            ["meta_export_expanded"] = _editorMetaExportExpanded,
            ["meta_export_options"] = exportOptions,
            ["meta_difficulty_enabled"] = HasEditorDifficultyChart(_editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart?.Difficulty, _sourceName))
                && IsEditorDifficultyEnabled(_editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart?.Difficulty, _sourceName)),
            ["meta_difficulty_has_chart"] = HasEditorDifficultyChart(_editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart?.Difficulty, _sourceName)),
            ["meta_difficulty_label"] = GetEditorDifficultyName(_editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart?.Difficulty, _sourceName)).ToUpperInvariant() + " DIFFICULTY",
            ["meta_delete_enabled"] = HasEditorDifficultyChart(_editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart?.Difficulty, _sourceName)),
            ["meta_delete_confirm_open"] = _editorMetaDeleteConfirmOpen,
            ["meta_create_confirm_open"] = _editorCreateDifficultyConfirmOpen,
            ["meta_create_difficulty_label"] = string.IsNullOrWhiteSpace(_editorPendingCreateDifficultyCode)
                ? "difficulty" : GetEditorDifficultyName(_editorPendingCreateDifficultyCode).ToLowerInvariant(),
            ["meta_template"] = _editorBundleTemplate.ToString().ToUpperInvariant(),
            ["meta_element_open"] = _editorElementDropdownOpen,
            ["meta_preview_playing"] = _editorPreviewPlayer?.IsPlaying == true,
            ["preview_editor_open"] = _editorPreviewEditorOpen,
            ["preview_duration"] = EditorPreviewSourceDurationSeconds,
            ["preview_start"] = _editorPreviewStartSeconds,
            ["preview_end"] = _editorPreviewEndSeconds,
            ["preview_start_text"] = _editorPreviewStartText,
            ["preview_end_text"] = _editorPreviewEndText,
            ["preview_start_label"] = FormatEditorPreviewTimestamp(_editorPreviewStartSeconds),
            ["preview_end_label"] = FormatEditorPreviewTimestamp(_editorPreviewEndSeconds),
            ["preview_duration_label"] = FormatEditorPreviewTimestamp(EditorPreviewSourceDurationSeconds),
            ["preview_start_focused"] = _editorPreviewFocusedField == 0,
            ["preview_end_focused"] = _editorPreviewFocusedField == 1,
            ["preview_start_caret_offset"] = previewStartMetrics.CaretOffset,
            ["preview_start_selection_offset"] = previewStartMetrics.SelectionOffset,
            ["preview_start_selection_width"] = previewStartMetrics.SelectionWidth,
            ["preview_start_text_width"] = previewStartMetrics.TextWidth,
            ["preview_end_caret_offset"] = previewEndMetrics.CaretOffset,
            ["preview_end_selection_offset"] = previewEndMetrics.SelectionOffset,
            ["preview_end_selection_width"] = previewEndMetrics.SelectionWidth,
            ["preview_end_text_width"] = previewEndMetrics.TextWidth,
            ["preview_message"] = _editorPreviewDialogMessage ?? string.Empty,
            ["preview_playing"] = _editorPreviewPlayer?.IsPlaying == true,
            ["meta_element_options"] = new List<object?>
            {
                ElementOption("meta_element_0", "DANCER", 0, "danger"),
                ElementOption("meta_element_1", "VOCAL", 1, "info"),
                ElementOption("meta_element_2", "SESSION", 2, "success"),
            },
            ["problems_summary"] = summary,
            ["problems_count"] = _editorProblems.Count,
            ["problems_errors"] = errorCount,
            ["problems_warnings"] = warningCount,
            ["problems_expanded"] = _editorProblemsExpanded,
            ["problems_scroll"] = _editorProblemsScrollOffset,
            ["problems_scroll_max"] = Math.Max(0, _editorProblems.Count - LAYOUT_PROBLEM_ROW_COUNT),
            ["problems"] = problems,
            ["banks"] = banks,
            ["status"] = _statusMessage ?? string.Empty,
            ["dirty"] = _editorHasUnsavedChanges,
            ["preview_mode"] = _editorPreviewMode
        };
        AddEditorLanTestUiState(state);
        return state;
    }

    private string GetEditorLuaMenuTitle() => _editorMenuSection switch
    {
        EditorMenuSection.None => string.Empty,
        EditorMenuSection.Notes => "NOTE TOOLS",
        EditorMenuSection.Tools => "CHART EDIT TOOLS",
        EditorMenuSection.SuperFlick => "SUPERFLICK TARGET",
        EditorMenuSection.Snap => "BEAT SNAP",
        EditorMenuSection.Metronome => "METRONOME",
        EditorMenuSection.Bpm => "TEMPO CONTROLS",
        EditorMenuSection.Stutter when _editorStutterStartPlacementPending => "PICK START",
        EditorMenuSection.Stutter when _editorStutterEndPlacementPending => "PICK END",
        EditorMenuSection.Stutter => _editorStutterRamp ? "TEMPO GENERATOR · RAMP" : _editorStutterAlternate ? "TEMPO GENERATOR · ALTERNATE" : "TEMPO GENERATOR · PULSE",
        EditorMenuSection.Select => "SELECT OPTIONS",
        EditorMenuSection.Duration => "DURATION",
        _ => string.Empty
    };

    private bool DispatchChartEditorInterfaceAction(SkinUiAction? action, int width, int height)
    {
        if (action is null) return false;
        var name = action.Name;
        var value = action.Value;
        if (_editorMetaEditingField is not null && name != "editor.meta.row" &&
            name != "editor.meta.close" && name != "editor.meta.exit")
            CommitEditorMetaFieldOnBlur();
        int Number() => value switch
        {
            int integer => integer,
            long integer => (int)integer,
            double number when double.IsFinite(number) => (int)number,
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => -1
        };
        bool LegacyClick(Rectangle bounds)
        {
            if (bounds.IsEmpty) return false;
            var point = new Vector2(bounds.Center.X, bounds.Center.Y);
            return TryHandleChartEditorToolbarClick(point, width, height);
        }
        bool LegacyClickFromUi(Rectangle bounds, string buttonId)
        {
            if (bounds.IsEmpty || !_chartEditorInterfaceRuntime.TryGetUiButtonBounds(buttonId, out var uiBounds) || uiBounds.IsEmpty)
                return LegacyClick(bounds);
            var relativeX = Math.Clamp((_chartEditorInterfaceRuntime.UiPointerX - uiBounds.X) / Math.Max(1f, uiBounds.Width), 0f, 1f);
            var relativeY = Math.Clamp((_chartEditorInterfaceRuntime.UiPointerY - uiBounds.Y) / Math.Max(1f, uiBounds.Height), 0f, 1f);
            var point = new Vector2(bounds.X + relativeX * bounds.Width, bounds.Y + relativeY * bounds.Height);
            return TryHandleChartEditorToolbarClick(point, width, height);
        }

        switch (name)
        {
            case "editor.block":
                if (_editorMetaPanelOpen) CommitEditorMetaFieldOnBlur();
                return true;
            case "editor.action":
                HandleEditorActionClick(Number());
                return true;
            case "editor.tab":
                HandleEditorMenuCategoryClick(Number());
                return true;
            case "editor.child":
                var childIndex = Number();
                return LegacyClickFromUi(GetEditorMenuChildBounds(childIndex, width, height), $"child_{childIndex}");
            case "editor.stutter.pattern":
                if (_editorStutterInputField != EditorStutterInputField.None && !TryCommitEditorStutterInput())
                    return true;
                _editorStutterInputField = EditorStutterInputField.None;
                _editorStutterInputReplaceOnNextKey = false;
                _editorStutterInputText = string.Empty;
                if (string.Equals(value as string, "pulse", StringComparison.OrdinalIgnoreCase))
                {
                    _editorStutterAlternate = false;
                    _editorStutterRamp = false;
                    _statusMessage = "Pattern set to Pulse: fast BPM pulses over slow BPM gaps; no separate Slow Time is used.";
                }
                else if (string.Equals(value as string, "alternate", StringComparison.OrdinalIgnoreCase))
                {
                    _editorStutterAlternate = true;
                    _editorStutterRamp = false;
                    _statusMessage = "Pattern set to Alternate: Fast and Slow BPM alternate for beat-based phase lengths.";
                }
                else if (string.Equals(value as string, "ramp", StringComparison.OrdinalIgnoreCase))
                {
                    _editorStutterAlternate = false;
                    _editorStutterRamp = true;
                    _statusMessage = "Pattern set to Ramp: Start BPM changes linearly to Target BPM.";
                }
                return true;
            case "editor.panel_connector":
                ToggleEditorPanelsConnectorTool(); return true;
            case "editor.panel_attr":
                SelectEditorPanelAttribute(Number()); return true;
            case "editor.panel_select":
                HandleEditorMenuCategoryClick(3); return true;
            case "editor.panel_duration":
                if (_editorMenuSection == EditorMenuSection.Duration) CloseEditorInterfaceMenu();
                else OpenEditorDurationTool();
                return true;
            case "editor.menu.close":
                CloseEditorInterfaceMenu(); return true;
            case "editor.snap":
                _editorSnapDivisor = Number(); _editorMenuSection = EditorMenuSection.None;
                _editorHoverCacheValid = false; _statusMessage = $"Beat snap: 1/{_editorSnapDivisor}."; return true;
            case "editor.metronome":
                SetEditorMetronomeDivision(Number()); _editorMenuSection = EditorMenuSection.None; return true;
            case "editor.bank":
                if (value is string bankValue)
                {
                    var split = bankValue.Split('|', 2);
                    if (split.Length == 2) SelectEditorPreviewBank(split[0], split[1]);
                }
                return true;
            case "editor.problems.toggle":
                _editorProblemsExpanded = !_editorProblemsExpanded; ClampEditorProblemsScroll(width, height); return true;
            case "editor.problems.quick_fix":
                QuickFixEditorLinks(); return true;
            case "editor.problems.row":
                var problemIndex = Number();
                RefreshEditorDiagnostics();
                if (problemIndex >= 0 && problemIndex < _editorProblems.Count)
                {
                    var issue = _editorProblems[problemIndex];
                    if (issue.Track is >= 17 and <= 20 && !_editorPanelsViewEnabled) ToggleEditorPanelsView();
                    else if (issue.Track is >= 0 and <= 15 && _editorPanelsViewEnabled) ToggleEditorPanelsView();
                    _playing = false; _playbackStartPending = false; _audioResyncPending = true;
                    if (_tempo is not null) _playSeconds = Math.Clamp(_tempo.TickToSeconds(Math.Max(0, issue.Tick)), 0.0, ChartDuration());
                    _statusMessage = $"Jumped to tick {issue.Tick}: {issue.Message}";
                }
                return true;
            case "editor.speed":
                AdjustPauseScrollSpeed(Number() < 0 ? -0.10 : 0.10); return true;
            case "editor.seek":
                if (value is double seconds && double.IsFinite(seconds))
                {
                    if (!_editorTimelineScrubbing)
                    {
                        _editorTimelineScrubbing = true;
                        _editorTimelineResumeAfterScrub = _playing;
                    }
                    _playing = false;
                    _playbackStartPending = false;
                    _audioResyncPending = true;
                    _playSeconds = Math.Clamp(seconds, 0.0, ChartDuration());
                }
                return true;
            case "editor.problems.scroll":
                RefreshEditorDiagnostics();
                _editorProblemsScrollOffset = Math.Clamp(_editorProblemsScrollOffset + Number(), 0, Math.Max(0, _editorProblems.Count - LAYOUT_PROBLEM_ROW_COUNT));
                return true;
            case "editor.problems.scroll_to":
                RefreshEditorDiagnostics();
                _editorProblemsScrollOffset = Math.Clamp(Number(), 0,
                    Math.Max(0, _editorProblems.Count - LAYOUT_PROBLEM_ROW_COUNT));
                return true;
            case "editor.meta.scroll":
                var metaRowsForScroll = GetEditorMetaRows(_tempo?.SecondsToTick(_playSeconds) ?? 0.0);
                _editorMetaScrollOffset = Math.Clamp(_editorMetaScrollOffset + Number(), 0,
                    GetEditorMetaMaxScrollOffset(metaRowsForScroll, width, height));
                return true;
            case "editor.meta.scroll_to":
                var scrollMetaRows = GetEditorMetaRows(_tempo?.SecondsToTick(_playSeconds) ?? 0.0);
                _editorMetaScrollOffset = Math.Clamp(Number(), 0,
                    GetEditorMetaMaxScrollOffset(scrollMetaRows, width, height));
                return true;
            case "editor.meta.close":
                CancelEditorMetaField();
                _editorMetaExportExpanded = false;
                _editorMetaPanelOpen = false;
                return true;
            case "editor.meta.exit":
                CancelEditorMetaField();
                _editorMetaExportExpanded = false;
                _editorMetaPanelOpen = false;
                EditorExitRequested?.Invoke();
                return true;
            case "editor.meta.category":
                var categoryIndex = Number();
                if (categoryIndex >= 0 && categoryIndex < EditorMetaCategories.Length)
                {
                    CancelEditorMetaField();
                    _editorElementDropdownOpen = false;
                    _editorMetaCategory = EditorMetaCategories[categoryIndex];
                    _editorMetaScrollOffset = 0;
                }
                return true;
            case "editor.difficulty":
                var difficultyIndex = Number();
                if (difficultyIndex >= 0 && difficultyIndex < EditorDifficultyCodes.Length)
                {
                    _editorMetaScrollOffset = 0;
                    _editorElementDropdownOpen = false;
                    SwitchEditorDifficulty(EditorDifficultyCodes[difficultyIndex]);
                }
                return true;
            case "editor.meta.row":
                var rowIndex = Number();
                if (rowIndex >= 0 && rowIndex < rowsCount())
                {
                    var rows = GetEditorMetaRows(_tempo?.SecondsToTick(_playSeconds) ?? 0.0);
                    var rowLabel = rows[rowIndex].Label;
                    var fieldKey = GetEditorMetaFieldKey(rowLabel);
                    if (fieldKey is not null)
                    {
                        var pixel = new Microsoft.Xna.Framework.Point(
                            (int)MathF.Round(_chartEditorInterfaceRuntime.UiPointerX),
                            (int)MathF.Round(_chartEditorInterfaceRuntime.UiPointerY));
                        HandleEditorMetaInterfaceFieldClick(rowIndex, fieldKey, rows[rowIndex].Value, pixel, width, height);
                        return true;
                    }
                    CommitEditorMetaFieldOnBlur();
                    if (rowLabel == "MUSIC") ImportEditorSongRequested?.Invoke();
                    else if (rowLabel == "ELEMENT") _editorElementDropdownOpen = !_editorElementDropdownOpen;
                    else if (rowLabel == "DIFFICULTY")
                    {
                        var difficultyCode = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart?.Difficulty, _sourceName);
                        if (!HasEditorDifficultyChart(difficultyCode)) return true;
                        ToggleEditorDifficultyEnabled(difficultyCode);
                        MarkEditorDirty();
                        _editorElementDropdownOpen = false;
                        _statusMessage = IsEditorDifficultyEnabled(difficultyCode)
                            ? $"{GetEditorDifficultyName(difficultyCode)} difficulty enabled."
                            : $"{GetEditorDifficultyName(difficultyCode)} difficulty disabled; this chart will be omitted from exports.";
                    }
                    else if (rowLabel == "CUSTOM JUDGEMENT WINDOWS")
                    {
                        _editorCustomJudgementWindowsEnabled = !_editorCustomJudgementWindowsEnabled;
                        MarkEditorDirty();
                        if (_editorCustomJudgementWindowsEnabled && _chart is not null)
                        {
                            var code = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart.Difficulty, _sourceName);
                            _bundleJudgementWindows[code] = (_chart.PerfectPlusWindowMs, _chart.PerfectWindowMs, _chart.GoodWindowMs);
                        }
                        SyncSharedEditorMetadataToDifficultyStates();
                        StoreActiveEditorDifficulty();
                        _statusMessage = _editorCustomJudgementWindowsEnabled
                            ? "Custom judgement windows enabled; they will be included in exports."
                            : "Custom judgement windows disabled; judgement_windows_ms will be omitted from exports.";
                    }
                    return true;
                }
                return true;
            case "editor.meta.element_option":
                var elementOptions = new[] { "DANCER", "VOCAL", "SESSION" };
                var selectedElementOption = Number();
                if (selectedElementOption >= 0 && selectedElementOption < elementOptions.Length && _chart is not null)
                {
                    _chart.Element = elementOptions[selectedElementOption];
                    _skinRuntime.SetChartElement(_chart.Element);
                    _pauseSkinRuntime.SetChartElement(_chart.Element);
                    _resultSkinRuntime.SetChartElement(_chart.Element);
                    SyncSharedEditorMetadataToDifficultyStates();
                    StoreActiveEditorDifficulty();
                    MarkEditorDirty();
                    _statusMessage = $"Element set to {elementOptions[selectedElementOption]}.";
                }
                _editorElementDropdownOpen = false;
                return true;
            case "editor.meta.difficulty_toggle":
                var toggleCode = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart?.Difficulty, _sourceName);
                if (!HasEditorDifficultyChart(toggleCode)) return true;
                ToggleEditorDifficultyEnabled(toggleCode);
                MarkEditorDirty();
                _statusMessage = IsEditorDifficultyEnabled(toggleCode)
                    ? $"{GetEditorDifficultyName(toggleCode)} difficulty enabled."
                    : $"{GetEditorDifficultyName(toggleCode)} difficulty disabled; this chart will be omitted from exports.";
                return true;
            case "editor.meta.delete_confirm":
                if (HasEditorDifficultyChart(_editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart?.Difficulty, _sourceName)))
                    _editorMetaDeleteConfirmOpen = true;
                return true;
            case "editor.meta.delete_cancel":
                _editorMetaDeleteConfirmOpen = false;
                return true;
            case "editor.meta.delete_yes":
                if (_editorMetaDeleteConfirmOpen) DeleteEditorDifficultyFromSession();
                return true;
            case "editor.meta.create_cancel":
                CancelCreateEditorDifficulty();
                return true;
            case "editor.meta.create_yes":
                if (_editorCreateDifficultyConfirmOpen) ConfirmCreateEditorDifficulty();
                return true;
            case "editor.meta.export_toggle":
                CancelEditorMetaField();
                _editorMetaExportExpanded = true;
                return true;
            case "editor.meta.export_close":
                _editorMetaExportExpanded = false;
                return true;
            case "editor.meta.export_option":
                HandleEditorMetaExportOption(Number());
                return true;
            case "editor.meta.media_album":
                EditorMediaPickRequested?.Invoke("album"); return true;
            case "editor.meta.media_album_big":
                if (_editorBundleTemplate != ChartBundleTemplate.Tsts) EditorMediaPickRequested?.Invoke("album_big");
                return true;
            case "editor.meta.preview_play":
                if (_editorPreviewPlayer?.IsPlaying == true)
                {
                    StopEditorPreviewPlayback();
                    _statusMessage = "Preview playback stopped. Chart playback remains paused.";
                }
                else PlayExistingEditorPreview();
                return true;
            case "editor.meta.preview_edit":
                OpenEditorPreviewEditor();
                return true;
            case "editor.preview.focus_start":
                FocusEditorPreviewInterfaceField(0, width, height); return true;
            case "editor.preview.focus_end":
                FocusEditorPreviewInterfaceField(1, width, height); return true;
            case "editor.preview.play":
                if (_editorPreviewFocusedField >= 0)
                {
                    CommitEditorPreviewFocusedTime();
                    if (_editorPreviewFocusedField >= 0) return true;
                }
                if (_editorPreviewPlayer?.IsPlaying == true)
                {
                    StopEditorPreviewPlayback();
                    _editorPreviewDialogMessage = "Preview playback stopped. Chart playback remains paused.";
                }
                else
                {
                    try { PlayEditorPreviewSelection(); }
                    catch (Exception ex) { _editorPreviewDialogMessage = ex.Message; _statusMessage = $"Could not play music preview: {ex.Message}"; }
                }
                return true;
            case "editor.preview.save":
                if (_editorPreviewFocusedField >= 0)
                {
                    CommitEditorPreviewFocusedTime();
                    if (_editorPreviewFocusedField >= 0) DiscardEditorPreviewFocusedTimeInput();
                }
                try { SaveEditorPreview(); }
                catch (Exception ex) { _editorPreviewDialogMessage = ex.Message; _statusMessage = $"Could not save music preview: {ex.Message}"; }
                return true;
            case "editor.preview.close":
                CloseEditorPreviewEditor(); return true;
            case "editor.preview.scrub":
                UpdateEditorPreviewRangeFromValue(value is double scrubValue ? scrubValue : 0.0); return true;
            case "editor.meta.media_music": ImportEditorSongRequested?.Invoke(); return true;
            case "editor.lan_test.start": StartEditorLanTest(); return true;
            case "editor.lan_test.refresh": RefreshEditorLanTest(); return true;
            case "editor.lan_test.stop": StopEditorLanTest(); return true;
            case "editor.lan_test.adb.toggle": ToggleEditorLanTestAdb(); return true;
            case "editor.lan_test.adb.disconnect":
                if (value is string serial) DisconnectEditorLanTestWirelessDevice(serial);
                return true;
            default:
                return true;
        }

        int rowsCount() => GetEditorMetaRows(_tempo?.SecondsToTick(_playSeconds) ?? 0.0).Count;
    }

    private void CloseEditorInterfaceMenu()
    {
        if (_editorMenuSection == EditorMenuSection.Duration && _editorDurationInputFocused)
        {
            if (!TryCommitEditorDurationInput()) return;
            _editorDurationInputFocused = false;
            _editorDurationInputReplaceOnNextKey = false;
            _editorDurationInputText = string.Empty;
        }
        if (_editorMenuSection == EditorMenuSection.Stutter && _editorStutterInputField != EditorStutterInputField.None)
        {
            if (!TryCommitEditorStutterInput()) return;
            _editorStutterInputField = EditorStutterInputField.None;
            _editorStutterInputReplaceOnNextKey = false;
            _editorStutterInputText = string.Empty;
        }
        if (_editorMenuSection == EditorMenuSection.SuperFlick && _editorSuperFlickPlacementPending)
            CancelEditorSuperFlickPlacement("SuperFlick placement cancelled.", deactivateTool: true);

        if (_editorMenuSection == EditorMenuSection.Select)
        {
            _editorSelectReferenceTickOverride = null;
            _editorSelectReferenceLineTick = null;
        }
        if (_editorMenuSection == EditorMenuSection.Stutter)
        {
            _editorStutterStartPlacementPending = false;
            _editorStutterEndPlacementPending = false;
        }
        _editorMenuSection = EditorMenuSection.None;
        _editorSelectLineDragging = false;
        _editorSelectLineStartTrack = -1;
        _editorSelectedTempoEvent = null;
        _editorBpmPlacementPending = false;
        ClearEditorMenuTransientInputState();
    }

    private void HandleEditorMetaExportOption(int index)
    {
        if (index < 0 || index >= GetEditorMetaExportOptionCount()) return;
        CancelEditorMetaField();
        _editorMetaExportExpanded = false;
        switch (GetEditorMetaExportOption(index).Action)
        {
            case EditorMetaExportAction.Native:
                if (_editorBundleTemplate == ChartBundleTemplate.Tsts)
                {
                    SaveTstsRequested?.Invoke();
                    _statusMessage = "Native TSTS export requested.";
                }
                else if (_editorBundleTemplate == ChartBundleTemplate.Bold)
                {
                    SaveAresRequested?.Invoke();
                    _statusMessage = "Native BOLD ARES export requested.";
                }
                else
                {
                    SaveTscZipRequested?.Invoke();
                    _statusMessage = "TSC ZIP export requested.";
                }
                break;
            case EditorMetaExportAction.TscOnly:
                SaveTscOnlyRequested?.Invoke(); _statusMessage = "TSC-only export requested."; break;
            case EditorMetaExportAction.JsonOnly:
                SaveJsonRequested?.Invoke(); _statusMessage = "JSON metadata export requested."; break;
            case EditorMetaExportAction.TscZip:
                SaveTscZipRequested?.Invoke(); _statusMessage = "TSC ZIP export requested."; break;
            case EditorMetaExportAction.Ares:
                SaveAresRequested?.Invoke(); _statusMessage = "ARES export requested."; break;
#if !TSC_PUBLISH
            case EditorMetaExportAction.Fpk:
                SaveFpkRequested?.Invoke(); _statusMessage = "FPK export requested."; break;
#endif
            case EditorMetaExportAction.Tsts:
                SaveTstsRequested?.Invoke(); _statusMessage = "TSTS export requested."; break;
        }
    }

    private void FocusEditorPreviewInterfaceField(int field, int width, int height)
    {
        if (_editorPreviewFocusedField >= 0 && _editorPreviewFocusedField != field)
            CommitEditorPreviewFocusedTime();
        _editorPreviewFocusedField = field;
        _editorPreviewInputReplaceOnNextKey = false;
        EndEditorTextFieldMouseSelection();
        var bounds = field == 0 ? GetEditorPreviewStartInputBounds(width, height) : GetEditorPreviewEndInputBounds(width, height);
        _editorPreviewStartCaret = _editorPreviewStartText.Length;
        _editorPreviewEndCaret = _editorPreviewEndText.Length;
        _editorPreviewStartSelectionAnchor = _editorPreviewStartCaret;
        _editorPreviewEndSelectionAnchor = _editorPreviewEndCaret;
        _editorTextActiveFieldId = field == 0 ? "preview-start" : "preview-end";
        _editorTextActiveBounds = bounds;
        _editorPreviewDialogMessage = null;
    }

    private void UpdateEditorPreviewRangeFromValue(double value)
    {
        if (_editorPreviewFocusedField >= 0)
        {
            CommitEditorPreviewFocusedTime();
            if (_editorPreviewFocusedField >= 0) DiscardEditorPreviewFocusedTimeInput();
        }

        var duration = EditorPreviewSourceDurationSeconds;
        if (duration <= 0) return;
        if (!_editorPreviewDraggingStart && !_editorPreviewDraggingEnd)
        {
            if (Math.Abs(value - _editorPreviewStartSeconds) <= Math.Abs(value - _editorPreviewEndSeconds))
                _editorPreviewDraggingStart = true;
            else
                _editorPreviewDraggingEnd = true;
        }
        const double minRange = 0.05;
        if (_editorPreviewDraggingStart)
            _editorPreviewStartSeconds = Math.Clamp(value, 0.0, Math.Max(0.0, _editorPreviewEndSeconds - minRange));
        else if (_editorPreviewDraggingEnd)
            _editorPreviewEndSeconds = Math.Clamp(value, Math.Min(duration, _editorPreviewStartSeconds + minRange), duration);
        _editorPreviewDialogMessage = null;
        SyncEditorPreviewTimeTexts();
    }

    private const int LAYOUT_PROBLEM_ROW_COUNT = 5;

    private void UpdateChartEditorInterfacePointer(Vector2 point, bool leftDown)
    {
        if (_chartEditorInterfaceRuntime.IsLoaded)
            _chartEditorInterfaceRuntime.UpdateUiPointer(point.X, point.Y, leftDown);
    }
}
