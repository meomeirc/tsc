using TSC;
using TSC.Ares;
using TSC.Input;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Color = Microsoft.Xna.Framework.Color;
using XnaColor = Microsoft.Xna.Framework.Color;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using System.Globalization;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Reflection;

namespace TSC.Rendering;

public sealed partial class Playfield
{
    // HUD, combo/judgement sprites, UI layout, and status/debug text.

    private double GetExpectedMaxBaseFromCurrentPath(GearState currentState, double currentTick)
    {
        if (_scoreEntries.Count == 0 || _tempo is null)
            return _scoreBasePoints;

        var currentSecond = _playSeconds;
        var remaining = 0.0;
        var currentSegments = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["left"] = FindScoreSegment(_scoreBoundaries, "left", currentTick),
            ["right"] = FindScoreSegment(_scoreBoundaries, "right", currentTick)
        };

        foreach (var entry in _scoreEntries)
        {
            if (_scoredEntryIds.Contains(entry.Id))
                continue;
            if (entry.Seconds < currentSecond - 1e-6)
                continue;

            if (entry.Kind == ScoreEntryKind.SuperFlick)
            {
                remaining += entry.BasePoints;
                continue;
            }

            var segment = FindScoreSegment(_scoreBoundaries, entry.Side, entry.Tick);
            var currentSegment = currentSegments.GetValueOrDefault(entry.Side, 0);
            if (segment < currentSegment)
                continue;

            var targetBank = segment == currentSegment
                ? GearTimeline.ActiveBankForSide(currentState, entry.Side)
                : GetIntendedBankForScoreSegment(_scoreBoundaries, entry.Side, segment);

            if (string.Equals(targetBank, entry.Bank, StringComparison.OrdinalIgnoreCase))
                remaining += entry.BasePoints;
        }

        return Math.Clamp(_scoreBasePoints + remaining, 0.0, BaseScoreTarget);
    }

    private string GetSelectedEventHudText()
    {
        if (!_selectedEventKey.HasValue || _chart is null)
            return "";

        var ev = _chart.Events.FirstOrDefault(e => KeyOf(e) == _selectedEventKey.Value);
        if (ev is null)
            return "";

        return $"SELECTED NOTE   tick {ev.Tick}   track {ev.Track}   attr {ev.Attr}   type {ev.NoteType}   dur: {ev.Duration}";
    }

    private string GetScoreDebugText(GearState state, double currentTick)
    {
        var intended = _gear?.AtTick((int)Math.Floor(currentTick), includeTickChanges: true) ?? new GearState();
        var leftSeg = FindScoreSegment(_scoreBoundaries, "left", currentTick);
        var rightSeg = FindScoreSegment(_scoreBoundaries, "right", currentTick);
        var leftBudget = _scoreSegmentBudgets.GetValueOrDefault(("left", leftSeg));
        var rightBudget = _scoreSegmentBudgets.GetValueOrDefault(("right", rightSeg));
        var maxBase = GetExpectedMaxBaseFromCurrentPath(state, currentTick);
        var remainingBase = Math.Max(0.0, maxBase - _scoreBasePoints);
        var maxTotal = Math.Clamp(maxBase + _scorePerfectPlusBonus +
            remainingBase * (PerfectPlusBonusTarget / BaseScoreTarget),
            0.0, BaseScoreTarget + PerfectPlusBonusTarget);

        var playEndTick = _chart?.PlayTick ?? 0;
        var playEndSeconds = _chart?.PlayTime ?? 0.0;

        return string.Join("\n",
            $"PATH NOW    L {state.LeftMode}  R {state.RightMode}  G {state.GlobalMode}",
            $"PATH TARGET L {intended.LeftMode}  R {intended.RightMode}  G {intended.GlobalMode}",
            $"SEG L {leftSeg}  R {rightSeg}   BUDGET L {leftBudget:N0}  R {rightBudget:N0}",
            $"BASE {_scoreBasePoints:N0}/{BaseScoreTarget:N0}   P+ {_scorePerfectPlusBonus:N0}/{PerfectPlusBonusTarget:N0}   TOTAL {GetDisplayedScore():N0}/{(BaseScoreTarget + PerfectPlusBonusTarget):N0}",
            $"EXPECTED MAX FROM HERE {maxTotal:N0}   MODEL MAX {BaseScoreTarget:N0} BASE / {(BaseScoreTarget + PerfectPlusBonusTarget):N0} FULL",
            $"SCORE OBJECTS {_scoredEntryIds.Count}/{_scoreEntries.Count}   END {playEndSeconds:0.00}s / tick {playEndTick}");
    }

    private void DrawAuxTrackDebugOverlay(double currentTick)
    {
        if (!_showAuxTrackDebug || _chart is null)
            return;

        const int maxEvents = 12;
        var currentTickValue = Math.Max(0.0, currentTick);
        var events = _chart.Events
            .Where(e => e.Track >= 12 && e.Tick >= currentTickValue - 1e-6)
            .OrderBy(e => e.Tick)
            .ThenBy(e => e.Track)
            .ThenBy(e => e.EventIndex)
            .Take(maxEvents)
            .ToArray();

        var panelX = Math.Max(_projection.ScalePixels(18f), _graphics.Viewport.Width - _projection.ScalePixels(620f));
        var panelY = _projection.ScalePixels(116f);
        var panelWidth = _projection.ScalePixels(602f);
        var lineHeight = _projection.ScalePixels(18f);
        var panelHeight = lineHeight * (events.Length + 3) + _projection.ScalePixels(18f);

        var panel = new Rectangle(
            (int)panelX,
            (int)panelY,
            Math.Max(1, (int)panelWidth),
            Math.Max(1, (int)panelHeight));

        _spriteBatch.Begin(samplerState: SamplerState.PointClamp, blendState: BlendState.AlphaBlend);
        _spriteBatch.Draw(_pixel, panel, new Color(0, 0, 0, 220));

        var title = FontSafeText(_font, $"AUX TRACKS 12+ (F11 off)   next {events.Length}/{maxEvents}");
        UnicodeTextRenderer.DrawString(_spriteBatch, _font, title,
            new Vector2(panelX + _projection.ScalePixels(10f), panelY + _projection.ScalePixels(8f)),
            InnerSuperFlick);

        var cursorText = FontSafeText(_font, $"current tick {currentTickValue:0.###}   total {_chart.Events.Count(e => e.Track >= 12)}");
        UnicodeTextRenderer.DrawString(_spriteBatch, _font, cursorText,
            new Vector2(panelX + _projection.ScalePixels(10f), panelY + _projection.ScalePixels(26f)),
            Muted);

        var lineY = panelY + _projection.ScalePixels(44f);
        if (events.Length == 0)
        {
            UnicodeTextRenderer.DrawString(_spriteBatch, _font, FontSafeText(_font, "<no future auxiliary events>"),
                new Vector2(panelX + _projection.ScalePixels(10f), lineY), Muted);
        }
        else
        {
            foreach (var ev in events)
            {
                string line;
                if (ev.RawType == 1)
                {
                    line = $"tick {ev.Tick,7}  tr{ev.Track:D2}  ref{ev.TypeIndex:D2}  type1  attr{ev.Attr}";
                    if (ev.Duration != 6)
                        line += $"  dur{ev.Duration}";
                }
                else
                {
                    line = $"tick {ev.Tick,7}  tr{ev.Track:D2}  ref{ev.TypeIndex:D2}  type{ev.RawType}  value{ev.Value:0.###}";
                }

                UnicodeTextRenderer.DrawString(_spriteBatch, _font,
                    FontSafeText(_font, line),
                    new Vector2(panelX + _projection.ScalePixels(10f), lineY),
                    ev.RawType == 1 ? Text : Muted);
                lineY += lineHeight;
            }
        }

        _spriteBatch.End();
    }

    private void DrawHud(GearState state, double currentTick)
    {
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);
        var bpm = _tempo!.BpmAt(currentTick);
        var mode = $"L {state.LeftMode}   R {state.RightMode}   {state.GlobalMode}   VIEW {(_topDownViewEnabled ? "TOP-DOWN" : "CONFIG")}";
        var hudLine = _chartEditorMode
            ? $"{FormatEditorProgressTimestamp(_playSeconds)} / {FormatEditorProgressTimestamp(ChartDuration())}   tick {currentTick:0}   BPM {bpm:0.##}"
            : $"{_playSeconds.ToString("0.00", CultureInfo.InvariantCulture)}s   tick {currentTick:0}   BPM {bpm:0.##}";
        var selectionLine = _selectedEventKey.HasValue
            ? GetSelectedEventHudText()
            : _selectedBeatTick.HasValue
                ? $"SELECTED 1/4 BEAT   tick {_selectedBeatTick.Value}  track {_selectedBeatTrack?.ToString(CultureInfo.InvariantCulture) ?? "?"}"
                : "Click a note or lane to inspect its tick and track";
        var status = (_gameplayMode ? "GAMEPLAY " : "AUTO ") + (_playing ? "PLAYING" : "PAUSED");
        var viewHint = _topDownViewEnabled
            ? "TOP-DOWN VIEW (Tab: restore config)"
            : "CONFIG VIEW (Tab: top-down)";
        var controls = _chartEditorMode
#if TSC_PUBLISH
            ? "EDITOR: Ctrl+S TSC  Ctrl+Shift+S ARES. Select a tool and tap a lane to place notes."
#else
            ? "EDITOR: Ctrl+S TSC  Ctrl+Shift+S ARES  Ctrl+Alt+S FPK. Select a tool and tap a lane to place notes."
#endif
            : "Click note/beat = inspect tick   Right-click = clear   Space Play/Pause   F4 Gameplay   F9 SuperFlick Debug   F11 Aux Tracks   B Beat Lines   F2 FPK   F3 ARES   R Restart   F1 Settings";
        mode = FontSafeText(_font, mode);
        hudLine = FontSafeText(_font, hudLine);
        selectionLine = FontSafeText(_font, selectionLine);
        status = FontSafeText(_font, status);
        viewHint = FontSafeText(_font, viewHint);
        controls = FontSafeText(_font, controls);

        var defaultTextX = _projection.ScalePixels(22f);
        var viewWidth = _graphics.Viewport.Width;
        var viewHeight = _graphics.Viewport.Height;
        var editorHudFont = _chartEditorMode
            ? GetChartEditorFont(_chartEditorInterfaceLayout.LabelFont)
            : _font;
        var editorTopY = _projection.ScalePixels(18f);
        var editorTopMaxWidth = viewWidth - defaultTextX * 2f;
        if (_chartEditorMode)
        {
            // Keep the editor's top HUD clear of the action column and right of the timeline.
            var speedPanel = GetEditorScrollSpeedPanelBounds(viewWidth, viewHeight);
            var timelinePanel = GetEditorTimelineScrubberBounds(viewWidth, viewHeight);
            var actionPanel = GetEditorBottomActionPanelBounds(viewWidth, viewHeight);
            var padX = _projection.ScalePixels(12f);
            var padY = _projection.ScalePixels(8f);
            defaultTextX = timelinePanel.Right + padX;
            editorTopY = Math.Max(speedPanel.Bottom + padY, _projection.ScalePixels(64f));
            editorTopMaxWidth = Math.Max(_projection.ScalePixels(10f), actionPanel.X - defaultTextX - padX);

            var topLineGap = _projection.ScalePixels(22f);
            var topLines = new[]
            {
                (hudLine, Text),
                (selectionLine, _selectedEventKey.HasValue || _selectedBeatTick.HasValue ? Hold : Muted),
                (status, _playing ? Active : Muted)
            };
            for (var i = 0; i < topLines.Length; i++)
            {
                var text = FitHudText(editorHudFont, FontSafeText(editorHudFont, topLines[i].Item1), editorTopMaxWidth);
                UnicodeTextRenderer.DrawString(_spriteBatch, editorHudFont, text,
                    new Vector2(defaultTextX, editorTopY + i * topLineGap), topLines[i].Item2);
            }

            if (_scoreDebugEnabled && _scoreEntries.Count > 0)
            {
                var debugLines = GetScoreDebugText(state, currentTick).Split('\n');
                var debugY = editorTopY + topLines.Length * topLineGap + padY;
                for (var i = 0; i < debugLines.Length; i++)
                {
                    var text = FitHudText(editorHudFont, FontSafeText(editorHudFont, debugLines[i]), editorTopMaxWidth);
                    UnicodeTextRenderer.DrawString(_spriteBatch, editorHudFont, text,
                        new Vector2(defaultTextX, debugY + i * _projection.ScalePixels(18f)),
                        i == 3 ? Text : Muted);
                }
            }

            var bottomLines = new List<(string Text, Color Color)>();
            if (!string.IsNullOrWhiteSpace(_sourceName)) bottomLines.Add((_sourceName!, Muted));
            if (!string.IsNullOrWhiteSpace(_statusMessage)) bottomLines.Add((_statusMessage!, Hold));

            // Keep source/file and status information above the Problems panel, but
            // do not size it to the Problems panel itself. Use all remaining screen
            // width and wrap long messages onto additional lines instead of trimming.
            var problemsPanel = GetEditorProblemsPanelBounds(viewWidth, viewHeight);
            var infoGap = _projection.ScalePixels(5f);
            var rightMargin = _projection.ScalePixels(12f);
            var infoWidth = Math.Max(_projection.ScalePixels(10f), viewWidth - problemsPanel.X - rightMargin);
            var lineHeight = Math.Max(_projection.ScalePixels(20f),
                UnicodeTextRenderer.GetLogicalLineSpacing(editorHudFont) * _projection.ScalePixels(1f));
            var wrappedBottomLines = new List<(string Text, Color Color)>();
            foreach (var item in bottomLines)
            {
                var safeText = FontSafeText(editorHudFont, item.Text);
                foreach (var line in WrapEditorHudText(editorHudFont, safeText, infoWidth))
                    wrappedBottomLines.Add((line, item.Color));
            }
            var bottomTextTop = problemsPanel.Y - infoGap - wrappedBottomLines.Count * lineHeight;
            for (var i = 0; i < wrappedBottomLines.Count; i++)
            {
                UnicodeTextRenderer.DrawString(_spriteBatch, editorHudFont, wrappedBottomLines[i].Text,
                    new Vector2(problemsPanel.X, bottomTextTop + i * lineHeight), wrappedBottomLines[i].Color);
            }
        }
        else
        {
            UnicodeTextRenderer.DrawString(_spriteBatch, _font, mode, new Vector2(defaultTextX, _projection.ScalePixels(18)), Text);
            UnicodeTextRenderer.DrawString(_spriteBatch, _font, hudLine, new Vector2(defaultTextX, _projection.ScalePixels(42)), Text);
            UnicodeTextRenderer.DrawString(_spriteBatch, _font, selectionLine, new Vector2(defaultTextX, _projection.ScalePixels(66)),
                _selectedEventKey.HasValue || _selectedBeatTick.HasValue ? Hold : Muted);
            var viewHintSize = UnicodeTextRenderer.MeasureString(_font, viewHint);
            UnicodeTextRenderer.DrawString(_spriteBatch, _font, viewHint,
                new Vector2(viewWidth - viewHintSize.X - _projection.ScalePixels(22), _projection.ScalePixels(42)),
                _topDownViewEnabled ? Hold : Muted);
            var statusSize = UnicodeTextRenderer.MeasureString(_font, status);
            UnicodeTextRenderer.DrawString(_spriteBatch, _font, status,
                new Vector2(viewWidth - statusSize.X - _projection.ScalePixels(22), _projection.ScalePixels(18)),
                _playing ? Active : Muted);

            if (_scoreDebugEnabled && _scoreEntries.Count > 0)
            {
                var debugText = GetScoreDebugText(state, currentTick);
                var debugLines = debugText.Split('\n');
                var debugY = _projection.ScalePixels(76);
                for (var i = 0; i < debugLines.Length; i++)
                {
                    var text = FontSafeText(_font, debugLines[i]);
                    var size = UnicodeTextRenderer.MeasureString(_font, text);
                    UnicodeTextRenderer.DrawString(_spriteBatch, _font, text,
                        new Vector2(viewWidth - _projection.ScalePixels(22) - size.X, debugY + i * _projection.ScalePixels(18)),
                        i == 3 ? Text : Muted);
                }
            }

            UnicodeTextRenderer.DrawString(_spriteBatch, _font, controls, new Vector2(defaultTextX, viewHeight - _projection.ScalePixels(28)), Muted);
            if (!string.IsNullOrWhiteSpace(_sourceName))
                UnicodeTextRenderer.DrawString(_spriteBatch, _font, FontSafeText(_font, _sourceName), new Vector2(defaultTextX, viewHeight - _projection.ScalePixels(52)), Muted);
            if (!string.IsNullOrWhiteSpace(_statusMessage))
                UnicodeTextRenderer.DrawString(_spriteBatch, _font, FontSafeText(_font, _statusMessage), new Vector2(defaultTextX, viewHeight - _projection.ScalePixels(76)), Hold);
            if (_chart is not null && _chart.MainLoopDetected)
            {
                var loopText = $"loop: +{_chart.MainLoopGeneratedEvents} main / +{_chart.MainLoopBridgeGeneratedEvents} bridge / +{_chart.MainLoopLateGeneratedEvents} callback";
                loopText = FontSafeText(_font, loopText);
                var loopSize = UnicodeTextRenderer.MeasureString(_font, loopText);
                UnicodeTextRenderer.DrawString(_spriteBatch, _font, loopText,
                    new Vector2(viewWidth - loopSize.X - _projection.ScalePixels(22), viewHeight - _projection.ScalePixels(28)), Muted);
            }
        }
        if (_superFlickDebugEnabled)
        {
            var debugX = _chartEditorMode ? defaultTextX : _projection.ScalePixels(22);
            var debugY = _chartEditorMode
                ? editorTopY + _projection.ScalePixels(124f)
                : _projection.ScalePixels(86);
            var debugWidth = _chartEditorMode ? editorTopMaxWidth : viewWidth - debugX - _projection.ScalePixels(22f);
            var debugFont = _chartEditorMode ? editorHudFont : _font;
            var debugHeader = FitHudText(debugFont, FontSafeText(debugFont, $"SUPER FLICK DEBUG (F9 off)  log: {_superFlickDebugPath}"), debugWidth);
            UnicodeTextRenderer.DrawString(_spriteBatch, debugFont, debugHeader, new Vector2(debugX, debugY), InnerSuperFlick);
            var lineY = debugY + _projection.ScalePixels(22);
            foreach (var line in _superFlickDebugRecent.Take(8))
            {
                var visible = line.Length > 150 ? line[^150..] : line;
                visible = FitHudText(debugFont, FontSafeText(debugFont, visible), debugWidth);
                UnicodeTextRenderer.DrawString(_spriteBatch, debugFont, visible, new Vector2(debugX, lineY), Muted);
                lineY += _projection.ScalePixels(18);
            }
        }

        _spriteBatch.End();
    }

    private void DrawEditorBankIndicators()
    {
        if (!_chartEditorMode || _tempo is null)
            return;

        var width = _graphics.Viewport.Width;
        var height = _graphics.Viewport.Height;
        var labelFont = GetChartEditorFont(_chartEditorInterfaceLayout.ButtonFont);
        EnsureEditorIndicatorProjection(width, height);
        var unit = _editorIndicatorProjection.ScalePixels(1f);
        var currentTick = _tempo.SecondsToTick(_playSeconds);
        var intendedState = _gear?.AtTick((int)Math.Floor(currentTick), includeTickChanges: true)
            ?? new GearState();
        var previewState = EnsureEditorPreviewGearState();
        SyncEditorPreviewBankIndicators(previewState);

        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);
        foreach (var side in Sides)
        {
            var activeBank = side == "left" ? _editorPreviewLeftBank : _editorPreviewRightBank;
            var intendedBank = GearTimeline.ActiveBankForSide(intendedState, side);
            var selectedIsIntended = string.Equals(activeBank, intendedBank, StringComparison.OrdinalIgnoreCase);

            foreach (var bank in EditorBankNames)
            {
                var bounds = GetEditorBankIndicatorBounds(side, bank, width, height);
                var isSelected = string.Equals(activeBank, bank, StringComparison.OrdinalIgnoreCase);
                var isIntended = string.Equals(intendedBank, bank, StringComparison.OrdinalIgnoreCase);

                // The active bank button is green whether it follows the authored
                // route or is temporarily switched to an alternate preview bank.
                // The intended destination remains orange while the preview differs.
                Color fill;
                Color border;
                Color labelColor;
                if (isSelected)
                {
                    fill = new Color(29, 91, 62, 225);
                    border = new Color(52, 211, 153, 255);
                    labelColor = Color.White;
                }
                else if (isIntended && !selectedIsIntended)
                {
                    fill = new Color(184, 94, 20, 205);
                    border = new Color(255, 168, 64, 245);
                    labelColor = new Color(255, 245, 228, 255);
                }
                else
                {
                    fill = new Color(10, 10, 11, 150);
                    border = new Color(135, 135, 137, 150);
                    labelColor = new Color(205, 214, 226, 220);
                }

                _spriteBatch.Draw(_pixel, bounds, fill);
                var thickness = Math.Max(1, (int)MathF.Round(unit));
                _spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Y, bounds.Width, thickness), border);
                _spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Bottom - thickness, bounds.Width, thickness), border);
                _spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Y, thickness, bounds.Height), border);
                _spriteBatch.Draw(_pixel, new Rectangle(bounds.Right - thickness, bounds.Y, thickness, bounds.Height), border);
                var label = bank switch { "Forward" => "1K", "Center" => "2K", _ => "3K" };
                var safeLabel = FontSafeText(labelFont, label);
                var size = UnicodeTextRenderer.MeasureString(labelFont, safeLabel);
                UnicodeTextRenderer.DrawString(_spriteBatch, labelFont, safeLabel,
                    new Vector2(bounds.Center.X - size.X * 0.5f, bounds.Center.Y - size.Y * 0.5f),
                    labelColor);
            }
        }
        _spriteBatch.End();
    }

    private void DrawEditorPreviewToolbar()
    {
        var virtualWidth = _graphics.Viewport.Width;
        var virtualHeight = _graphics.Viewport.Height;
        var unit = _projection.ScalePixels(1f);
        var style = _chartEditorInterfaceLayout;
        var panel = GetEditorBottomLeftStripBounds(virtualWidth, virtualHeight);
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);
        _spriteBatch.Draw(_pixel, panel, style.PanelColor);

        foreach (var (id, label, selected) in new[]
                 {
                     (EditorActionPlay, _playing ? "PAUSE" : "PLAY", _playing),
                     (EditorActionEditorPreview, "PREVIEW", true),
                     (EditorActionGameplayMode, "AUTOPLAY", !_gameplayMode)
                 })
        {
            var index = Array.IndexOf(EditorBottomLeftActionIds, id);
            var bounds = GetEditorBottomLeftButtonBounds(index, virtualWidth, virtualHeight);
            if (bounds.IsEmpty) continue;
            var fill = ResolveEditorButtonStateFill(bounds, selected ? new Color(62, 62, 65) : new Color(28, 28, 30), style);
            _spriteBatch.Draw(_pixel, bounds, fill);
            var border = selected ? style.AccentColor : style.MutedTextColor;
            var thickness = Math.Max(1, (int)MathF.Round(unit));
            _spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Y, bounds.Width, thickness), border);
            _spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Bottom - thickness, bounds.Width, thickness), border);
            _spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Y, thickness, bounds.Height), border);
            _spriteBatch.Draw(_pixel, new Rectangle(bounds.Right - thickness, bounds.Y, thickness, bounds.Height), border);

            var fitted = FitChartEditorText(label, style.LabelFont, 24f * (float)style.LabelTextScale,
                bounds.Width - 8f * unit, bounds.Height - 4f * unit);
            var size = fitted.Measured * fitted.Scale;
            var position = new Vector2(bounds.Center.X - size.X * 0.5f, bounds.Center.Y - size.Y * 0.5f);
            UnicodeTextRenderer.DrawString(_spriteBatch, fitted.Font, fitted.Text, position,
                style.ButtonTextColor, 0f, Vector2.Zero, fitted.Scale,
                SpriteEffects.None, 0f);
        }
        _spriteBatch.End();
    }

    private void DrawEditorToolbar()
    {
        if (_editorMenuSection == EditorMenuSection.Bpm &&
            !_editorBpmInputFocused &&
            !_editorBpmPlacementPending &&
            _editorSelectedTempoEvent is null)
        {
            SetEditorBpmInputFromCurrentTick();
        }

        var virtualWidth = _graphics.Viewport.Width;
        var virtualHeight = _graphics.Viewport.Height;
        var unit = _projection.ScalePixels(1f);
        var interfaceUi = _chartEditorInterfaceLayout;
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);

        if (_editorPreviewMode)
        {
            var previewPanel = GetEditorBottomLeftStripBounds(virtualWidth, virtualHeight);
            _spriteBatch.Draw(_pixel, previewPanel, interfaceUi.PanelColor);
            foreach (var id in new[] { EditorActionPlay, EditorActionEditorPreview, EditorActionGameplayMode })
            {
                var index = Array.IndexOf(EditorBottomLeftActionIds, id);
                var label = id switch
                {
                    EditorActionPlay => _playing ? "PAUSE" : "PLAY",
                    EditorActionEditorPreview => "PREVIEW",
                    _ => "AUTOPLAY"
                };
                var selected = id switch
                {
                    EditorActionPlay => _playing,
                    EditorActionEditorPreview => true,
                    _ => !_gameplayMode
                };
                var fill = id == EditorActionPlay && _playing
                    ? new Color(83, 35, 40, 238)
                    : selected ? interfaceUi.SelectedButtonColor : interfaceUi.ButtonColor;
                var accent = id == EditorActionPlay && _playing
                    ? interfaceUi.DangerColor
                    : selected ? interfaceUi.AccentColor : interfaceUi.MutedTextColor;
                DrawControlButton(GetEditorBottomLeftButtonBounds(index, virtualWidth, virtualHeight), label,
                    fill, accent);
            }
            _spriteBatch.End();
            return;
        }

        // Draw the upper-right action stack first. The bottom-right workspace is
        // layered over it below, including its expanded menu and tab row.
        var actionPanel = GetEditorBottomActionPanelBounds(virtualWidth, virtualHeight);
        _spriteBatch.Draw(_pixel, actionPanel, interfaceUi.PanelColor);
        var actionIds = GetVisibleEditorActionIds();
        for (var i = 0; i < actionIds.Count; i++)
        {
            var id = actionIds[i];
            if (!IsEditorActionVisible(id))
                continue;
            var actionLabelKey = id == EditorActionPlay && _playing ? "action_pause" : GetEditorActionLabelKey(id);
            var label = interfaceUi.Label(actionLabelKey, EditorActionLabels[id]);
            var fill = id switch
            {
                EditorActionDelete => new Color(83, 35, 40, 238),
                EditorActionUndo when _editorUndoStack.Count == 0 => new Color(25, 25, 27, 238),
                EditorActionRedo when _editorRedoStack.Count == 0 => new Color(25, 25, 27, 238),
                EditorActionCopy or EditorActionCut or EditorActionPaste or EditorActionPasteFit => new Color(58, 58, 60, 238),
                EditorActionSelect => new Color(62, 62, 65, 238),
                EditorActionDeselect => new Color(50, 50, 52, 238),
                _ => new Color(28, 28, 30, 238)
            };
            var accent = id switch
            {
                EditorActionDelete => new Color(230, 93, 104),
                EditorActionUndo when _editorUndoStack.Count == 0 => new Color(83, 83, 85),
                EditorActionRedo when _editorRedoStack.Count == 0 => new Color(83, 83, 85),
                EditorActionCopy or EditorActionCut or EditorActionPaste or EditorActionPasteFit => Active,
                EditorActionSelect => new Color(129, 140, 248),
                EditorActionDeselect => new Color(148, 163, 184),
                _ => new Color(148, 163, 184)
            };
            DrawControlButton(GetEditorActionButtonBounds(i, virtualWidth, virtualHeight, actionIds), label, fill, accent);
        }

        var toolsPanel = GetEditorTopStripPanelBounds(virtualWidth, virtualHeight);
        var submenuPanel = GetEditorMenuPanelBounds(virtualWidth, virtualHeight);
        // Draw the independent submenu over the right-side action stack first;
        // the fixed category strip then draws above it at the bottom edge.
        if (!submenuPanel.IsEmpty)
            _spriteBatch.Draw(_pixel, submenuPanel, interfaceUi.PanelColor);
        _spriteBatch.Draw(_pixel, toolsPanel, interfaceUi.PanelColor);
        if (_editorPanelsViewEnabled)
        {
            DrawSubmenuHeader(GetEditorPanelsHeaderBounds(virtualWidth, virtualHeight), interfaceUi.Label("panel_title", "PANELS  •  ATTRIBUTES"), panelStripHeader: true);
            var selectMenuActive = _editorMenuSection == EditorMenuSection.Select;
            DrawControlButton(GetEditorPanelsSelectButtonBounds(virtualWidth, virtualHeight), interfaceUi.Label("panels_select", "SELECT"),
                selectMenuActive ? new Color(62, 62, 65, 245) : new Color(25, 25, 27, 242),
                selectMenuActive ? new Color(52, 211, 153) : new Color(196, 181, 253));
            var connectorActive = _editorConnectorMode;
            DrawControlButton(GetEditorPanelsConnectorButtonBounds(virtualWidth, virtualHeight), interfaceUi.Label("panels_connector", "CONNECTOR"),
                connectorActive ? new Color(38, 92, 77, 242) : new Color(28, 28, 30, 238),
                connectorActive ? new Color(52, 211, 153) : new Color(148, 163, 184));
            for (var i = 0; i < 8; i++)
            {
                var attr = 101 + i;
                var color = GetEditorPanelsAttributeColor(attr);
                var active = _editorPanelsSelectedAttr == attr && _editorPlacementEnabled;
                var fill = active
                    ? new Color(color.R, color.G, color.B, (byte)235)
                    : new Color((byte)(color.R * 0.42f), (byte)(color.G * 0.42f), (byte)(color.B * 0.42f), (byte)230);
                DrawControlButton(GetEditorPanelsAttributeButtonBounds(i, virtualWidth, virtualHeight), attr.ToString(CultureInfo.InvariantCulture),
                    fill, active ? Color.White : color);
            }

            DrawControlButton(GetEditorPanelsDurationButtonBounds(virtualWidth, virtualHeight), interfaceUi.Label("panels_duration", "DURATION"),
                _editorMenuSection == EditorMenuSection.Duration
                    ? new Color(62, 62, 65, 245) : new Color(25, 25, 27, 242),
                _editorMenuSection == EditorMenuSection.Duration
                    ? new Color(129, 140, 248) : new Color(148, 163, 184));

            // Match gameplay's SELECT submenu and controls; ApplyEditorSelectFilters
            // switches the eligible track/attribute scope based on the active view.
            if (_editorMenuSection == EditorMenuSection.Select)
            {
                DrawSubmenuHeader(GetEditorMenuHeaderBounds(virtualWidth, virtualHeight), "SELECT OPTIONS");
                for (var i = 0; i < GetEditorMenuChildCount(); i++)
                {
                    var bounds = GetEditorMenuChildBounds(i, virtualWidth, virtualHeight);
                    var label = GetEditorMenuControlLabel(i);
                    if (label.Length > 0)
                        DrawEditorControlLabel(GetEditorMenuChildLabelBounds(i, virtualWidth, virtualHeight), label);
                    if (i == 0)
                    {
                        DrawControlButton(bounds, GetEditorSelectionYLabel(),
                            new Color(58, 58, 60, 245), new Color(52, 211, 153));
                    }
                    else if (i == 1)
                    {
                        DrawControlButton(bounds, _editorSelectXSelected ? "SELECTED" : "ALL",
                            _editorSelectXSelected ? new Color(58, 58, 60, 245) : new Color(28, 28, 30, 242),
                            _editorSelectXSelected ? new Color(52, 211, 153) : new Color(125, 125, 127));
                    }
                    else
                    {
                        DrawControlButton(bounds, "APPLY SELECT",
                            new Color(62, 62, 65, 245), new Color(129, 140, 248));
                    }
                }
            }
            else if (_editorMenuSection == EditorMenuSection.Duration)
            {
                DrawSubmenuHeader(GetEditorMenuHeaderBounds(virtualWidth, virtualHeight), "DURATION");
                for (var i = 0; i < GetEditorMenuChildCount(); i++)
                {
                    var bounds = GetEditorMenuChildBounds(i, virtualWidth, virtualHeight);
                    var label = GetEditorMenuControlLabel(i);
                    if (label.Length > 0)
                        DrawEditorControlLabel(GetEditorMenuChildLabelBounds(i, virtualWidth, virtualHeight), label);
                    if (i == 0)
                    {
                        var inputText = GetEditorDurationFieldDisplayText();
                        var showCaret = _editorDurationInputFocused && inputText != "MIXED";
                        DrawEditorNumericTextField(bounds, inputText, showCaret,
                            Active, _editorDurationInputCaretIndex, unit);
                    }
                    else
                    {
                        var targetCount = GetSelectedEditorDurationTargets().Count;
                        DrawControlButton(bounds, $"APPLY ({targetCount.ToString(CultureInfo.InvariantCulture)})",
                            targetCount > 0 ? new Color(62, 62, 65, 245) : new Color(25, 25, 27, 238),
                            targetCount > 0 ? new Color(129, 140, 248) : new Color(83, 83, 85));
                    }
                }
            }
        }
        else
        {
            var activeRoot = GetEditorMenuRootSection();
            for (var tabIndex = 0; tabIndex < 4; tabIndex++)
            {
                var tabSection = tabIndex switch
                {
                    0 => EditorMenuSection.Notes,
                    1 => EditorMenuSection.Tools,
                    2 => EditorMenuSection.Bpm,
                    _ => EditorMenuSection.Select
                };
                var tabLabel = tabIndex switch
                {
                    0 => interfaceUi.Label("tab_notes", "NOTES"),
                    1 => interfaceUi.Label("tab_tools", "TOOLS"),
                    2 => interfaceUi.Label("tab_bpm", "BPM"),
                    _ => interfaceUi.Label("tab_select", "SELECT")
                };
                var tabAccent = tabIndex switch { 0 => Active, 1 => Hold, 2 => new Color(129, 140, 248), _ => new Color(196, 181, 253) };
                var activeTab = activeRoot == tabSection;
                DrawControlButton(GetEditorMenuParentBounds(tabIndex, virtualWidth, virtualHeight), tabLabel,
                    activeTab ? new Color(62, 62, 65, 245) : new Color(25, 25, 27, 242),
                    activeTab ? new Color(52, 211, 153) : tabAccent);
            }

            if (_editorMenuSection is not (EditorMenuSection.None or EditorMenuSection.Snap or EditorMenuSection.Metronome))
            {
                var headerLabel = _editorMenuSection switch
                {
                    EditorMenuSection.Notes => "NOTE TOOLS",
                    EditorMenuSection.Tools => "CHART EDIT TOOLS",
                    EditorMenuSection.SuperFlick => "SUPERFLICK TARGET",
                    EditorMenuSection.Stutter when _editorStutterStartPlacementPending => "PICK START",
                    EditorMenuSection.Stutter when _editorStutterEndPlacementPending => "PICK END",
                    EditorMenuSection.Stutter => _editorStutterRamp
                        ? "TEMPO GENERATOR  ·  RAMP"
                        : _editorStutterAlternate
                            ? "TEMPO GENERATOR  ·  ALTERNATE"
                            : "TEMPO GENERATOR  ·  PULSE",
                    EditorMenuSection.Select => "SELECT OPTIONS",
                    EditorMenuSection.Duration => "DURATION",
                    _ => "TEMPO CONTROLS"
                };
                DrawSubmenuHeader(GetEditorMenuHeaderBounds(virtualWidth, virtualHeight), headerLabel);

                // Controls remain individually styled. Leave the spaces between rows
                // neutral rather than tinting the entire section or adding color stripes.

                for (var i = 0; i < GetEditorMenuChildCount(); i++)
                {
                    var bounds = GetEditorMenuChildBounds(i, virtualWidth, virtualHeight);
                    var menuControlLabel = GetEditorMenuControlLabel(i);
                    if (menuControlLabel.Length > 0)
                        DrawEditorControlLabel(GetEditorMenuChildLabelBounds(i, virtualWidth, virtualHeight), menuControlLabel);
                    switch (_editorMenuSection)
                    {
                        case EditorMenuSection.Notes:
                        {
                            var active = i switch
                            {
                                0 => _editorPlacementEnabled && !_editorConnectorMode && _editorNoteType == NoteType.Single && _editorAttr == 0,
                                1 => _editorPlacementEnabled && _editorConnectorMode,
                                2 => _editorPlacementEnabled && !_editorConnectorMode && _editorNoteType == NoteType.Single && _editorAttr == 10,
                                3 => _editorPlacementEnabled && !_editorConnectorMode && _editorNoteType == NoteType.Single && _editorAttr == 50,
                                4 => _editorPlacementEnabled && !_editorConnectorMode && _editorNoteType == NoteType.Vibrato && _editorAttr == 9,
                                5 => _editorPlacementEnabled && !_editorConnectorMode && _editorSuperFlickTool != EditorSuperFlickTool.None,
                                _ => false
                            };
                            if (i == 6)
                            {
                                var selection = GetSelectedEditorNotes(expandLinkFamilies: true)
                                    .Where(ev => IsPlayableEditorTrack(ev.Track) && ev.NoteType != NoteType.End)
                                    .Distinct().ToList();
                                var available = selection.Count > 0 && !_editorConnectorMode &&
                                                _editorSuperFlickTool != EditorSuperFlickTool.Place;
                                DrawControlButton(bounds, EditorToolLabels[i],
                                    available ? new Color(74, 47, 35, 245) : new Color(25, 25, 27, 238),
                                    available ? new Color(251, 146, 60) : new Color(83, 83, 85));
                            }
                            else
                            {
                                var accent = i is 1 or 4 ? Hold : i >= 2 ? OuterSuperFlick : Active;
                                DrawControlButton(bounds, EditorToolLabels[i], active ? new Color(65, 65, 68, 245) : new Color(28, 28, 30, 242), accent);
                            }
                            break;
                        }
                        case EditorMenuSection.Tools:
                        {
                            var accent = i switch
                            {
                                0 => Active,
                                1 => new Color(56, 189, 248),
                                2 => new Color(196, 181, 253),
                                3 or 4 or 5 => Hold,
                                6 or 9 => new Color(251, 146, 60),
                                7 or 8 => OuterSuperFlick,
                                10 when _editorAutoMergeLinks => new Color(52, 211, 153),
                                10 => new Color(125, 125, 127),
                                _ => Active
                            };
                            var fill = i == 9 ? new Color(38, 34, 49, 245) :
                                i == 10 && _editorAutoMergeLinks ? new Color(30, 67, 59, 245) :
                                new Color(28, 28, 30, 242);
                            DrawControlButton(bounds, GetEditorEditToolLabel(i), fill, accent);
                            break;
                        }
                        case EditorMenuSection.SuperFlick:
                        {
                            var targetBank = i switch { 0 => "Forward", 1 => "Center", 2 => "Backward", _ => string.Empty };
                            var isSource = targetBank == _editorSuperFlickSourceBank;
                            var label = isSource ? $"SOURCE {BankModeLabel(targetBank)}" : EditorSuperFlickToolLabels[i];
                            var fill = isSource ? new Color(64, 47, 35, 245) : new Color(28, 28, 30, 242);
                            var accent = isSource ? new Color(245, 158, 11) : new Color(90, 90, 92);
                            DrawControlButton(bounds, label, fill, accent);
                            break;
                        }
                        case EditorMenuSection.Snap:
                        {
                            var divisor = EditorSnapDivisors[i];
                            var active = divisor == _editorSnapDivisor;
                            DrawControlButton(bounds, $"1/{divisor}", active ? new Color(58, 58, 60, 245) : new Color(28, 28, 30, 242),
                                active ? new Color(52, 211, 153) : new Color(90, 90, 92));
                            break;
                        }
                        case EditorMenuSection.Duration:
                            if (i == 0)
                            {
                                var inputText = GetEditorDurationFieldDisplayText();
                                // MIXED is a read-only placeholder for a mixed selection,
                                // not editable numeric text. A typed digit starts a fresh value.
                                var showCaret = _editorDurationInputFocused && inputText != "MIXED";
                                DrawEditorNumericTextField(bounds, inputText, showCaret,
                                    Active, _editorDurationInputCaretIndex, unit);
                            }
                            else
                            {
                                var targetCount = GetSelectedEditorDurationTargets().Count;
                                DrawControlButton(bounds, $"APPLY ({targetCount.ToString(CultureInfo.InvariantCulture)})",
                                    targetCount > 0 ? new Color(62, 62, 65, 245) : new Color(25, 25, 27, 238),
                                    targetCount > 0 ? new Color(129, 140, 248) : new Color(83, 83, 85));
                            }
                            break;
                        case EditorMenuSection.Select:
                            if (i == 0)
                            {
                                DrawControlButton(bounds, GetEditorSelectionYLabel(),
                                    new Color(58, 58, 60, 245), new Color(52, 211, 153));
                            }
                            else if (i == 1)
                            {
                                DrawControlButton(bounds, _editorSelectXSelected ? "SELECTED" : "ALL",
                                    _editorSelectXSelected ? new Color(58, 58, 60, 245) : new Color(28, 28, 30, 242),
                                    _editorSelectXSelected ? new Color(52, 211, 153) : new Color(125, 125, 127));
                            }
                            else
                            {
                                DrawControlButton(bounds, "APPLY SELECT",
                                    new Color(62, 62, 65, 245), new Color(129, 140, 248));
                            }
                            break;
                        case EditorMenuSection.Bpm:
                            if (i == 0)
                            {
                                DrawEditorNumericTextField(bounds,
                                    _editorBpmInputText.Length == 0 ? "ENTER VALUE" : _editorBpmInputText,
                                    _editorBpmInputFocused, Active, _editorBpmInputCaretIndex, unit);
                            }
                            else if (i == 1)
                            {
                                DrawControlButton(bounds, _editorAutoAdjustBpm ? "ON" : "OFF",
                                    _editorAutoAdjustBpm ? new Color(58, 58, 60, 245) : new Color(28, 28, 30, 242),
                                    _editorAutoAdjustBpm ? new Color(52, 211, 153) : new Color(125, 125, 127));
                            }
                            else if (i == 2)
                            {
                                DrawControlButton(bounds, "FLATTEN BPM",
                                    new Color(74, 47, 35, 245), new Color(251, 146, 60));
                            }
                            else if (i == 3)
                            {
                                DrawControlButton(bounds, "TEMPO GENERATOR",
                                    new Color(62, 62, 65, 245), new Color(129, 140, 248));
                            }
                            break;
                        case EditorMenuSection.Stutter:
                        {
                            switch (i)
                            {
                                case 0:
                                    DrawControlButton(bounds,
                                        _editorStutterStartPlacementPending
                                            ? "TAP LANE"
                                            : _editorStutterStartTick >= 0
                                                ? _editorStutterStartTick.ToString(CultureInfo.InvariantCulture)
                                                : "PICK START",
                                        _editorStutterStartPlacementPending ? new Color(64, 47, 86, 245) : new Color(28, 28, 30, 242),
                                        new Color(196, 181, 253));
                                    break;
                                case 1:
                                    DrawControlButton(bounds,
                                        _editorStutterEndPlacementPending
                                            ? "TAP LANE"
                                            : _editorStutterEndTick > _editorStutterStartTick && _editorStutterStartTick >= 0
                                                ? _editorStutterEndTick.ToString(CultureInfo.InvariantCulture)
                                                : "PICK END",
                                        _editorStutterEndPlacementPending ? new Color(64, 47, 86, 245) : new Color(28, 28, 30, 242),
                                        new Color(196, 181, 253));
                                    break;
                                case 2:
                                {
                                    var patternButtons = GetEditorStutterPatternButtonBounds(bounds);
                                    var patternLabels = new[] { "PULSE", "ALTERNATE", "RAMP" };
                                    for (var patternIndex = 0; patternIndex < patternButtons.Length; patternIndex++)
                                    {
                                        var selected = patternIndex switch
                                        {
                                            0 => !_editorStutterAlternate && !_editorStutterRamp,
                                            1 => _editorStutterAlternate && !_editorStutterRamp,
                                            _ => _editorStutterRamp
                                        };
                                        DrawControlButton(patternButtons[patternIndex], patternLabels[patternIndex],
                                            selected ? new Color(65, 48, 99, 248) : new Color(28, 28, 30, 242),
                                            selected ? new Color(196, 181, 253) : new Color(100, 100, 102));
                                    }
                                    break;
                                }
                                case 3:
                                    if (_editorStutterRamp)
                                        DrawEditorNumericTextField(bounds, FormatEditorStutterInput(EditorStutterInputField.RampStartBpm, _editorRampStartBpm),
                                            _editorStutterInputField == EditorStutterInputField.RampStartBpm, Active, _editorStutterInputCaretIndex, unit);
                                    else
                                        DrawEditorNumericTextField(bounds, FormatEditorStutterInput(EditorStutterInputField.SlowBpm, _editorStutterSlowBpm),
                                            _editorStutterInputField == EditorStutterInputField.SlowBpm, Active, _editorStutterInputCaretIndex, unit);
                                    break;
                                case 4:
                                    if (_editorStutterRamp)
                                        DrawEditorNumericTextField(bounds, FormatEditorStutterInput(EditorStutterInputField.RampTargetBpm, _editorRampTargetBpm),
                                            _editorStutterInputField == EditorStutterInputField.RampTargetBpm, Active, _editorStutterInputCaretIndex, unit);
                                    else if (_editorStutterAlternate)
                                        DrawEditorNumericTextField(bounds, FormatEditorStutterInput(EditorStutterInputField.SlowTimeBeats, _editorStutterSlowTimeBeats),
                                            _editorStutterInputField == EditorStutterInputField.SlowTimeBeats, Active, _editorStutterInputCaretIndex, unit);
                                    else
                                        DrawEditorNumericTextField(bounds, FormatEditorStutterInput(EditorStutterInputField.PulseIntervalBeats, _editorStutterPulseIntervalBeats),
                                            _editorStutterInputField == EditorStutterInputField.PulseIntervalBeats, Active, _editorStutterInputCaretIndex, unit);
                                    break;
                                case 5:
                                    if (_editorStutterRamp)
                                        DrawControlButton(bounds, EditorRampResolutionLabels[Math.Clamp(_editorRampResolutionIndex, 0, EditorRampResolutionLabels.Length - 1)],
                                            new Color(28, 28, 30, 242), new Color(196, 181, 253));
                                    else
                                        DrawEditorNumericTextField(bounds, FormatEditorStutterInput(EditorStutterInputField.FastBpm, _editorStutterFastBpm),
                                            _editorStutterInputField == EditorStutterInputField.FastBpm, Active, _editorStutterInputCaretIndex, unit);
                                    break;
                                case 6:
                                    if (_editorStutterRamp)
                                        DrawControlButton(bounds, _editorAutoAdjustBpm ? "ON" : "OFF",
                                            _editorAutoAdjustBpm ? new Color(58, 58, 60, 245) : new Color(28, 28, 30, 242),
                                            _editorAutoAdjustBpm ? new Color(52, 211, 153) : new Color(125, 125, 127));
                                    else
                                        DrawEditorNumericTextField(bounds, FormatEditorStutterInput(EditorStutterInputField.FastTimeBeats, _editorStutterFastTimeBeats),
                                            _editorStutterInputField == EditorStutterInputField.FastTimeBeats, Active, _editorStutterInputCaretIndex, unit);
                                    break;
                                case 7:
                                    if (_editorStutterRamp)
                                    {
                                        var canMove = IsEditorStutterEditingRampValid();
                                        DrawControlButton(bounds, "MOVE UP",
                                            canMove ? new Color(58, 58, 60, 245) : new Color(25, 25, 27, 238),
                                            canMove ? new Color(52, 211, 153) : new Color(83, 83, 85));
                                    }
                                    else
                                    {
                                        var timingLabel = _editorStutterBalanced ? "BALANCED" : "FREE";
                                        var timingFill = _editorStutterBalanced ? new Color(58, 58, 60, 245) : new Color(28, 28, 30, 242);
                                        var timingAccent = _editorStutterBalanced ? new Color(52, 211, 153) : new Color(125, 125, 127);
                                        DrawControlButton(bounds, timingLabel, timingFill, timingAccent);
                                    }
                                    break;
                                case 8:
                                    if (_editorStutterRamp)
                                    {
                                        var canMove = IsEditorStutterEditingRampValid();
                                        DrawControlButton(bounds, "MOVE DOWN",
                                            canMove ? new Color(58, 58, 60, 245) : new Color(25, 25, 27, 238),
                                            canMove ? new Color(52, 211, 153) : new Color(83, 83, 85));
                                    }
                                    else
                                        DrawControlButton(bounds, _editorAutoAdjustBpm ? "ON" : "OFF",
                                            _editorAutoAdjustBpm ? new Color(58, 58, 60, 245) : new Color(28, 28, 30, 242),
                                            _editorAutoAdjustBpm ? new Color(52, 211, 153) : new Color(125, 125, 127));
                                    break;
                                case 9:
                                    DrawControlButton(bounds, "GENERATE",
                                        new Color(74, 47, 35, 245), new Color(251, 146, 60));
                                    break;
                                case 10:
                                    DrawControlButton(bounds, "DELETE",
                                        _editorStutterEditingGroup is null ? new Color(25, 25, 27, 238) : new Color(83, 35, 40, 238),
                                        _editorStutterEditingGroup is null ? new Color(83, 83, 85) : new Color(230, 93, 104));
                                    break;
                                case 11:
                                    DrawControlButton(bounds, "UNGROUP",
                                        _editorStutterEditingGroup is null ? new Color(25, 25, 27, 238) : new Color(28, 28, 30, 242),
                                        _editorStutterEditingGroup is null ? new Color(83, 83, 85) : new Color(196, 181, 253));
                                    break;
                            }
                            break;
                        }
                    }
                }
            }
        }

        // Fixed horizontal strip at bottom-left: playback/view tools plus INFO and SAVE.
        var leftStrip = GetEditorBottomLeftStripBounds(virtualWidth, virtualHeight);
        _spriteBatch.Draw(_pixel, leftStrip, new Color(8, 8, 9, 215));
        for (var i = 0; i < EditorBottomLeftActionIds.Length; i++)
        {
            var id = EditorBottomLeftActionIds[i];
            if (_editorPreviewMode && id is not (EditorActionPlay or EditorActionEditorPreview or EditorActionGameplayMode))
                continue;
            var label = id switch
            {
                EditorActionPlay => _playing ? "PAUSE" : "PLAY",
                EditorActionViewMode => _topDownViewEnabled ? "VIEW 2D" : "VIEW 3D",
                EditorActionEditorPreview => "PREVIEW",
                EditorActionGameplayMode => "AUTOPLAY",
                EditorActionExpand => _compact ? "COLLAPSED" : "EXPANDED",
                EditorActionBeatLines => "GRIDS",
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
            var actionLabelKey = id == EditorActionPlay && _playing ? "action_pause" : GetEditorActionLabelKey(id);
            if (id is not (EditorActionGameplayMode or EditorActionPanels))
                label = interfaceUi.Label(actionLabelKey, label);
            var active = id switch
            {
                EditorActionPlay => _playing,
                EditorActionViewMode => _topDownViewEnabled,
                EditorActionEditorPreview => _editorPreviewMode,
                EditorActionGameplayMode => !_gameplayMode,
                EditorActionExpand => _compact,
                EditorActionBeatLines => _beatLines,
                EditorActionTempoLines => _tempoLines,
                EditorActionConstantScroll => _constantScroll,
                EditorActionSnap => _editorMenuSection == EditorMenuSection.Snap,
                EditorActionMetronome => _editorMenuSection == EditorMenuSection.Metronome,
                EditorActionPanels => _editorPanelsViewEnabled,
                EditorActionMeta => _editorMetaPanelOpen,
                EditorActionSettings => false,
                _ => false
            };
            var lockedCollapsed3D = id == EditorActionExpand && _chartEditorMode && !_topDownViewEnabled;
            var fill = lockedCollapsed3D
                ? new Color(43, 43, 45, 238)
                : id == EditorActionPlay && _playing
                    ? new Color(83, 35, 40, 238)
                    : id == EditorActionQuickSave && _editorHasUnsavedChanges
                        ? new Color(148, 91, 24, 238)
                    : active ? new Color(58, 58, 60, 245) : new Color(28, 28, 30, 238);
            var accent = lockedCollapsed3D ? new Color(125, 125, 127) : id switch
            {
                EditorActionPlay when _playing => new Color(230, 93, 104),
                EditorActionPlay => Active,
                EditorActionQuickSave when _editorHasUnsavedChanges => new Color(255, 177, 66),
                EditorActionEditorPreview when _editorPreviewMode => interfaceUi.AccentColor,
                EditorActionGameplayMode when !_gameplayMode => new Color(52, 211, 153),
                EditorActionMeta when _editorMetaPanelOpen => new Color(52, 211, 153),
                EditorActionMeta => Active,
                EditorActionSnap when _editorMenuSection == EditorMenuSection.Snap => new Color(52, 211, 153),
                EditorActionMetronome when _editorMenuSection == EditorMenuSection.Metronome => new Color(52, 211, 153),
                EditorActionPanels when _editorPanelsViewEnabled => new Color(129, 140, 248),
                EditorActionBeatLines when !_beatLines => new Color(125, 125, 127),
                EditorActionTempoLines when !_tempoLines => new Color(125, 125, 127),
                EditorActionConstantScroll when !_constantScroll => new Color(125, 125, 127),
                _ => active ? new Color(52, 211, 153) : Active
            };
            DrawControlButton(GetEditorBottomLeftButtonBounds(i, virtualWidth, virtualHeight), label, fill, accent);
        }

        if (_editorMenuSection == EditorMenuSection.Snap)
        {
            var snapPopup = GetEditorSnapPopupBounds(virtualWidth, virtualHeight);
            _spriteBatch.Draw(_pixel, snapPopup, interfaceUi.PanelColor);
            for (var i = 0; i < EditorSnapDivisors.Length; i++)
            {
                var selected = _editorSnapDivisor == EditorSnapDivisors[i];
                DrawControlButton(GetEditorSnapMenuChildBounds(i, virtualWidth, virtualHeight),
                    $"1/{EditorSnapDivisors[i]}",
                    selected ? new Color(58, 58, 60, 245) : new Color(28, 28, 30, 242),
                    selected ? new Color(52, 211, 153) : Active);
            }
        }

        if (_editorMenuSection == EditorMenuSection.Metronome)
        {
            var popup = GetEditorMetronomePopupBounds(virtualWidth, virtualHeight);
            _spriteBatch.Draw(_pixel, popup, interfaceUi.PanelColor);
            for (var i = 0; i < EditorMetronomeDivisors.Length; i++)
            {
                var division = EditorMetronomeDivisors[i];
                var selected = _editorMetronomeDivisor == division;
                DrawControlButton(GetEditorMetronomeMenuChildBounds(i, virtualWidth, virtualHeight),
                    GetEditorMetronomeLabel(division),
                    selected ? new Color(58, 58, 60, 245) : new Color(28, 28, 30, 242),
                    selected ? new Color(52, 211, 153) : Active);
            }
        }

        var speedPanel = GetEditorScrollSpeedPanelBounds(virtualWidth, virtualHeight);
        _spriteBatch.Draw(_pixel, speedPanel, interfaceUi.PanelColor);
        DrawControlButton(GetEditorScrollSpeedButtonBounds(false, virtualWidth, virtualHeight), "−",
            new Color(28, 28, 30, 242), new Color(148, 163, 184));
        DrawControlButton(GetEditorScrollSpeedButtonBounds(true, virtualWidth, virtualHeight), "+",
            new Color(28, 28, 30, 242), Active);
        var speedFont = GetChartEditorFont(interfaceUi.LabelFont);
        var speedText = FontSafeText(speedFont, $"{_projection.ScrollSpeedScale:0.00}x");
        var speedTextSize = UnicodeTextRenderer.MeasureString(speedFont, speedText);
        var speedUnit = _projection.ScalePixels(1f);
        var speedX = speedPanel.X + (int)MathF.Round(46f * speedUnit);
        UnicodeTextRenderer.DrawString(_spriteBatch, speedFont, speedText,
            new Vector2(speedX, speedPanel.Y + (speedPanel.Height - speedTextSize.Y) * 0.5f), interfaceUi.MutedTextColor);

        DrawEditorTimelineScrubber(GetEditorTimelineScrubberBounds(virtualWidth, virtualHeight));
        if (_editorMetaPanelOpen)
            DrawEditorMetaPanel(virtualWidth, virtualHeight);

        _spriteBatch.End();

        void DrawControlButton(Rectangle bounds, string rawLabel, Color fill, Color accent)
        {
            fill = ResolveEditorButtonStateFill(bounds, fill, interfaceUi);
            accent = ResolveEditorAccent(accent, interfaceUi);
            _spriteBatch.Draw(_pixel, bounds, fill);
            var accentWidth = Math.Max(1, (int)MathF.Round((float)interfaceUi.AccentWidth * unit));
            _spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Y, accentWidth, bounds.Height), accent);
            var availableWidth = Math.Max(1f, bounds.Width - 16f * unit);
            var fitted = FitChartEditorText(
                interfaceUi.Label("button:" + rawLabel, rawLabel), interfaceUi.ButtonFont,
                24f * (float)interfaceUi.ButtonTextScale, availableWidth, bounds.Height - 8f * unit);
            var size = fitted.Measured * fitted.Scale;
            var position = new Vector2(bounds.X + (bounds.Width - size.X) * 0.5f,
                bounds.Y + (bounds.Height - size.Y) * 0.5f);
            UnicodeTextRenderer.DrawString(_spriteBatch, fitted.Font, fitted.Text, position, interfaceUi.ButtonTextColor,
                0f, Vector2.Zero, fitted.Scale, SpriteEffects.None, 0f);
        }

        void DrawSubmenuHeader(Rectangle bounds, string rawLabel, bool panelStripHeader = false)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;
            _spriteBatch.Draw(_pixel, bounds, interfaceUi.HeaderColor);
            var close = panelStripHeader
                ? GetEditorPanelsCloseButtonBounds(virtualWidth, virtualHeight)
                : GetEditorMenuCloseButtonBounds(virtualWidth, virtualHeight);
            var dividerHeight = Math.Max(1, (int)MathF.Round(1.5f * unit));
            _spriteBatch.Draw(_pixel,
                new Rectangle(bounds.X, bounds.Bottom - dividerHeight, bounds.Width, dividerHeight),
                new Color(90, 90, 92, 240));
            var maxWidth = Math.Max(1f, close.X - bounds.X - 20f * unit);
            var fitted = FitChartEditorText(
                interfaceUi.Label("header:" + rawLabel, rawLabel), interfaceUi.HeaderFont,
                24f * (float)interfaceUi.LabelTextScale, maxWidth, bounds.Height - 6f * unit);
            var size = fitted.Measured * fitted.Scale;
            UnicodeTextRenderer.DrawString(_spriteBatch, fitted.Font, fitted.Text,
                new Vector2(bounds.X + 10f * unit, bounds.Y + (bounds.Height - size.Y) * 0.5f),
                interfaceUi.ButtonTextColor, 0f, Vector2.Zero, fitted.Scale, SpriteEffects.None, 0f);
            DrawControlButton(close, "X", new Color(55, 30, 35, 245), interfaceUi.DangerColor);
        }

        void DrawEditorNumericTextField(Rectangle bounds, string rawValue, bool focused, Color caretColor, int caretIndex, float scaleUnit)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;

            var edge = Math.Max(1, (int)MathF.Round(1.5f * scaleUnit));
            var interfaceUi = _chartEditorInterfaceLayout;
            var labelFont = GetChartEditorFont(interfaceUi.LabelFont);
            var accent = focused ? ResolveEditorAccent(caretColor, interfaceUi) : new Color(100, 100, 102, 245);
            _spriteBatch.Draw(_pixel, bounds, interfaceUi.ButtonColor);
            _spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Y, bounds.Width, edge), accent);
            _spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Bottom - edge, bounds.Width, edge), interfaceUi.HeaderColor);
            _spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Y, edge, bounds.Height), accent);
            _spriteBatch.Draw(_pixel, new Rectangle(bounds.Right - edge, bounds.Y, edge, bounds.Height), interfaceUi.HeaderColor);

            var up = GetEditorNumericInputArrowBounds(bounds, true, scaleUnit);
            var down = GetEditorNumericInputArrowBounds(bounds, false, scaleUnit);
            _spriteBatch.Draw(_pixel, up, interfaceUi.HeaderColor);
            _spriteBatch.Draw(_pixel, down, interfaceUi.ButtonColor);
            var separatorX = up.X;
            _spriteBatch.Draw(_pixel, new Rectangle(separatorX, bounds.Y, edge, bounds.Height), interfaceUi.MutedTextColor);
            _spriteBatch.Draw(_pixel, new Rectangle(up.X, down.Y, up.Width, edge), interfaceUi.MutedTextColor);

            var arrowColor = focused ? new Color(226, 232, 240, 245) : new Color(165, 178, 196, 235);
            void Chevron(Rectangle area, bool pointsUp)
            {
                var cx = area.X + area.Width * 0.5f;
                var cy = area.Y + area.Height * 0.5f;
                var halfW = Math.Max(3f * scaleUnit, Math.Min(5f * scaleUnit, area.Width * 0.18f));
                var halfH = Math.Max(2f * scaleUnit, Math.Min(3.5f * scaleUnit, area.Height * 0.18f));
                if (pointsUp)
                {
                    DrawLine(_spriteBatch, new Vector2(cx - halfW, cy + halfH * 0.5f), new Vector2(cx, cy - halfH * 0.5f), Math.Max(1.5f, scaleUnit * 1.5f), arrowColor);
                    DrawLine(_spriteBatch, new Vector2(cx, cy - halfH * 0.5f), new Vector2(cx + halfW, cy + halfH * 0.5f), Math.Max(1.5f, scaleUnit * 1.5f), arrowColor);
                }
                else
                {
                    DrawLine(_spriteBatch, new Vector2(cx - halfW, cy - halfH * 0.5f), new Vector2(cx, cy + halfH * 0.5f), Math.Max(1.5f, scaleUnit * 1.5f), arrowColor);
                    DrawLine(_spriteBatch, new Vector2(cx, cy + halfH * 0.5f), new Vector2(cx + halfW, cy - halfH * 0.5f), Math.Max(1.5f, scaleUnit * 1.5f), arrowColor);
                }
            }
            Chevron(up, true);
            Chevron(down, false);

            var textLeft = bounds.X + (int)MathF.Round(10f * scaleUnit);
            var textWidth = Math.Max(1f, up.X - textLeft - 4f * scaleUnit);
            var caretPrefix = string.Empty;
            var visibleStart = 0;
            var textScale = Math.Min(1f, Math.Max(0.55f,
                (bounds.Height - 8f * scaleUnit) / Math.Max(1f, UnicodeTextRenderer.MeasureString(labelFont, "Ag").Y)));
            labelFont = GetChartEditorFont(interfaceUi.LabelFont, 24f * textScale);
            textScale = Math.Min(1f, (bounds.Height - 8f * scaleUnit) /
                Math.Max(1f, UnicodeTextRenderer.MeasureString(labelFont, "Ag").Y));
            var safeValue = FontSafeText(labelFont, rawValue);
            var visibleEnd = safeValue.Length;
            var display = focused
                ? FitEditorTextAroundCaret(labelFont, safeValue, caretIndex, textWidth,
                    _editorTextActiveFieldId ?? "editor", textScale, out caretPrefix, out visibleStart, out visibleEnd)
                : FitHudText(labelFont, safeValue, textWidth);
            var measured = UnicodeTextRenderer.MeasureString(labelFont, display);
            var position = new Vector2(textLeft, bounds.Y + (bounds.Height - measured.Y * textScale) * 0.5f);
            if (focused && TryGetActiveEditorTextSelection(out var selectionText, out var selectionAnchor, out var selectionCaret))
            {
                var mappedAnchor = Math.Clamp(selectionAnchor, visibleStart, visibleEnd) - visibleStart;
                var mappedCaret = Math.Clamp(selectionCaret, visibleStart, visibleEnd) - visibleStart;
                DrawEditorTextSelectionHighlight(bounds, display, mappedAnchor, mappedCaret,
                    textLeft, position.Y, textScale, textWidth, scaleUnit, 1f * scaleUnit, labelFont);
            }
            UnicodeTextRenderer.DrawString(_spriteBatch, labelFont, display, position, focused ? interfaceUi.ButtonTextColor : interfaceUi.MutedTextColor,
                0f, Vector2.Zero, textScale, SpriteEffects.None, 0f);
            if (focused) DrawEditorTextCaret(caretPrefix, position, textScale, scaleUnit, labelFont);
        }

        void DrawEditorControlLabel(Rectangle bounds, string rawLabel)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;
            var interfaceUi = _chartEditorInterfaceLayout;
            var availableWidth = Math.Max(1f, bounds.Width - 4f * unit);
            var fitted = FitChartEditorText(
                interfaceUi.Label("label:" + rawLabel, rawLabel), interfaceUi.LabelFont,
                24f * (float)interfaceUi.LabelTextScale, availableWidth, bounds.Height - 4f * unit);
            var size = fitted.Measured * fitted.Scale;
            var position = new Vector2(bounds.X + 2f * unit,
                bounds.Y + (bounds.Height - size.Y) * 0.5f);
            UnicodeTextRenderer.DrawString(_spriteBatch, fitted.Font, fitted.Text, position, interfaceUi.MutedTextColor,
                0f, Vector2.Zero, fitted.Scale, SpriteEffects.None, 0f);
        }
    }

    private static string GetEditorActionLabelKey(int actionId) => actionId switch
    {
        EditorActionPlay => "action_play",
        EditorActionDelete => "action_delete",
        EditorActionUndo => "action_undo",
        EditorActionRedo => "action_redo",
        EditorActionCopy => "action_copy",
        EditorActionCut => "action_cut",
        EditorActionPaste => "action_paste",
        EditorActionDeselect => "action_deselect",
        EditorActionMeta => "action_info",
        EditorActionViewMode => "action_view_mode",
        EditorActionExpand => "action_expand",
        EditorActionBeatLines => "action_grids",
        EditorActionQuickSave => "action_save",
        EditorActionPanels => "action_panels",
        EditorActionPasteFit => "action_paste_fit",
        EditorActionSelect => "action_select",
        EditorActionSnap => "action_snaps",
        EditorActionTempoLines => "action_tempo_lines",
        EditorActionConstantScroll => "action_constant_scroll",
        EditorActionSettings => "action_settings",
        EditorActionMetronome => "action_metronome",
        EditorActionEditorPreview => "action_editor_preview",
        EditorActionGameplayMode => "action_gameplay_mode",
        _ => $"action_{actionId}"
    };

    private static Color ResolveEditorButtonFill(Color fill, ChartEditorInterfaceLayout style)
    {
        if (fill.R <= 30 && fill.G <= 30 && fill.B <= 32)
            return style.ButtonColor;
        if (fill.R is >= 55 and <= 68 && Math.Abs(fill.R - fill.G) <= 6 && Math.Abs(fill.G - fill.B) <= 6)
            return style.SelectedButtonColor;
        return fill;
    }

    private Color ResolveEditorButtonStateFill(Rectangle bounds, Color fill, ChartEditorInterfaceLayout style)
    {
        var selected = fill.R is >= 55 and <= 68 &&
                       Math.Abs(fill.R - fill.G) <= 6 &&
                       Math.Abs(fill.G - fill.B) <= 6;
        var viewport = _graphics.Viewport;
        var (virtualWidth, virtualHeight) = GetVirtualResolution(viewport.Width, viewport.Height);
        var pointer = new Microsoft.Xna.Framework.Point(
            (int)MathF.Round(GlobalInput.Mouse.X * virtualWidth / (float)Math.Max(1, viewport.Width)),
            (int)MathF.Round(GlobalInput.Mouse.Y * virtualHeight / (float)Math.Max(1, viewport.Height)));
        var hovered = bounds.Contains(pointer);

        // Match uiStyle.button_color(): pressed state takes priority, then a
        // selected button, then hover, followed by its normal fill.
        if (hovered && GlobalInput.Mouse.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed)
            return style.PressedButtonColor;
        if (selected)
            return style.SelectedButtonColor;
        if (hovered)
            return style.HoverButtonColor;
        return ResolveEditorButtonFill(fill, style);
    }

    private static List<string> WrapEditorHudText(SpriteFont font, string? rawText, float maxWidth)
    {
        var text = (rawText ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        maxWidth = Math.Max(1f, maxWidth);
        var lines = new List<string>();

        foreach (var paragraph in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(paragraph))
            {
                lines.Add(string.Empty);
                continue;
            }

            var current = string.Empty;
            foreach (var word in paragraph.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (UnicodeTextRenderer.MeasureString(font, candidate).X <= maxWidth)
                {
                    current = candidate;
                    continue;
                }

                if (current.Length > 0)
                {
                    lines.Add(current);
                    current = string.Empty;
                }

                // Long paths, URLs, or network errors may not contain spaces. Break
                // those at Unicode text-element boundaries rather than losing text.
                var chunk = string.Empty;
                var elementEnumerator = StringInfo.GetTextElementEnumerator(word);
                while (elementEnumerator.MoveNext())
                {
                    var element = elementEnumerator.GetTextElement();
                    if (chunk.Length > 0 && UnicodeTextRenderer.MeasureString(font, chunk + element).X > maxWidth)
                    {
                        lines.Add(chunk);
                        chunk = element;
                    }
                    else
                    {
                        chunk += element;
                    }
                }
                current = chunk;
            }

            if (current.Length > 0)
                lines.Add(current);
        }

        if (lines.Count == 0)
            lines.Add(string.Empty);
        return lines;
    }

    private readonly record struct EditorWrappedTextLine(string Text, int Start, int EndExclusive, int NextStart);

    // Like WrapEditorHudText, but retain source ranges so an active text field can
    // draw its caret and selection on the wrapped line that contains each index.
    // Start/EndExclusive/NextStart are UTF-16 indices into the original string.
    private static List<EditorWrappedTextLine> WrapEditorTextWithRanges(
        string? rawText, float maxWidth, Func<string, float> measure)
    {
        var text = rawText ?? string.Empty;
        maxWidth = Math.Max(1f, maxWidth);
        var lines = new List<EditorWrappedTextLine>();

        void AddLine(int start, int endExclusive, int nextStart)
            => lines.Add(new EditorWrappedTextLine(text[start..endExclusive], start, endExclusive, nextStart));

        void WrapParagraph(int paragraphStart, int paragraphEnd, int afterParagraph)
        {
            if (paragraphStart >= paragraphEnd)
            {
                AddLine(paragraphStart, paragraphStart, afterParagraph);
                return;
            }

            var lineStart = paragraphStart;
            while (lineStart < paragraphEnd)
            {
                var cursor = lineStart;
                var fitEnd = lineStart;
                var whitespaceStart = -1;
                var whitespaceEnd = -1;
                var overflow = false;
                while (cursor < paragraphEnd)
                {
                    var element = StringInfo.GetNextTextElement(text, cursor);
                    var elementEnd = Math.Min(paragraphEnd, cursor + element.Length);
                    var candidate = text[lineStart..elementEnd];
                    if (measure(candidate) <= maxWidth || fitEnd == lineStart)
                    {
                        fitEnd = elementEnd;
                        if (char.IsWhiteSpace(text[cursor]))
                        {
                            whitespaceStart = cursor;
                            whitespaceEnd = elementEnd;
                        }
                        cursor = elementEnd;
                    }
                    else
                    {
                        overflow = true;
                        break;
                    }
                }

                if (!overflow)
                {
                    AddLine(lineStart, paragraphEnd, afterParagraph);
                    break;
                }

                int lineEnd;
                int nextStart;
                if (whitespaceStart > lineStart)
                {
                    lineEnd = whitespaceStart;
                    nextStart = whitespaceEnd;
                    while (nextStart < paragraphEnd && char.IsWhiteSpace(text[nextStart]))
                        nextStart++;
                }
                else
                {
                    lineEnd = fitEnd;
                    nextStart = fitEnd;
                }

                if (lineEnd <= lineStart)
                {
                    lineEnd = Math.Max(fitEnd, lineStart + 1);
                    nextStart = lineEnd;
                }
                AddLine(lineStart, lineEnd, nextStart);
                lineStart = nextStart;
            }
        }

        var paragraphStart = 0;
        while (true)
        {
            var newline = text.IndexOf('\n', paragraphStart);
            var paragraphEnd = newline >= 0 ? newline : text.Length;
            var afterParagraph = newline >= 0 ? newline + 1 : paragraphEnd;
            WrapParagraph(paragraphStart, paragraphEnd, afterParagraph);
            if (newline < 0)
                break;
            paragraphStart = newline + 1;
        }

        if (lines.Count == 0)
            AddLine(0, 0, 0);
        return lines;
    }

    private (SpriteFont Font, string Text, Vector2 Measured, float Scale) FitChartEditorText(
        string rawText, string requestedFont, float requestedPixelHeight, float maxWidth, float maxHeight)
    {
        requestedPixelHeight = Math.Clamp(float.IsFinite(requestedPixelHeight) ? requestedPixelHeight : 24f, 8f, 256f);
        maxWidth = Math.Max(1f, maxWidth);
        maxHeight = Math.Max(1f, maxHeight);
        var font = GetChartEditorFont(requestedFont, requestedPixelHeight);
        var text = FontSafeText(font, rawText);
        var measured = UnicodeTextRenderer.MeasureString(font, text);
        var scale = Math.Min(1f, Math.Min(
            maxWidth / Math.Max(1f, measured.X),
            maxHeight / Math.Max(1f, measured.Y)));

        // Lua menu text is rasterized at its display size. If the preferred face
        // does not fit, rasterize a smaller face instead of shrinking a larger
        // glyph atlas, which makes small editor labels look soft.
        var fittedPixelHeight = requestedPixelHeight * scale;
        if (fittedPixelHeight >= 8f && fittedPixelHeight < requestedPixelHeight - 0.5f)
        {
            font = GetChartEditorFont(requestedFont, fittedPixelHeight);
            text = FontSafeText(font, rawText);
            measured = UnicodeTextRenderer.MeasureString(font, text);
            scale = Math.Min(1f, Math.Min(
                maxWidth / Math.Max(1f, measured.X),
                maxHeight / Math.Max(1f, measured.Y)));
        }

        return (font, text, measured, scale);
    }

    private SpriteFont GetChartEditorFont(string requestedFont, float requestedPixelHeight = 24f)
    {
        // Match the menu runtime: rasterize each Roboto face at the text's
        // requested display size, with a few extra symbols for editor labels.
        const string editorCharacters = " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~•…→←↑↓▼▲−×÷≤≥≈°′″";
        return _chartEditorFontStore.Load(requestedFont, requestedPixelHeight, rasterCharacters: editorCharacters)?.Font
            ?? _chartEditorDefaultFontStore?.Load(requestedFont, requestedPixelHeight, rasterCharacters: editorCharacters)?.Font
            ?? _font;
    }

    private static Color ResolveEditorAccent(Color accent, ChartEditorInterfaceLayout style)
    {
        if (accent.R > 220 && accent.G > 150 && accent.B < 100)
            return style.AccentColor;
        if (accent.R > 190 && accent.G < 150 && accent.B < 160)
            return style.DangerColor;
        return accent;
    }

    private void DrawEditorTimelineScrubber(Rectangle bounds)
    {
        var interfaceUi = _chartEditorInterfaceLayout;
        var labelFont = GetChartEditorFont(interfaceUi.LabelFont);
        var unit = _projection.ScalePixels(1f);
        var duration = Math.Max(0.0, ChartDuration());
        var trackX = bounds.X + (int)MathF.Round(7f * unit);
        var trackTop = GetEditorTimelineTrackTop(bounds);
        var trackBottom = GetEditorTimelineTrackBottom(bounds);
        var trackHeight = Math.Max(1, (int)MathF.Round(trackBottom - trackTop));
        var trackColor = interfaceUi.MutedTextColor * 0.8f;
        var progressColor = interfaceUi.AccentColor;
        var futureActiveColor = new Color(245, 248, 252, 225);
        // These are permanent background rails. The route colors are drawn on top
        // as a separate layer and never modify the base rails themselves.
        var inactiveColor = interfaceUi.ButtonColor;
        var playSeconds = Math.Clamp(_playSeconds, 0.0, duration);
        var playRatio = duration > 0.0001 ? playSeconds / duration : 0.0;
        var playY = trackBottom - (float)playRatio * trackHeight;
        // Use almost the entire scrubber width for the six route traces. The
        // timestamp labels are drawn over this expanded lane map rather than
        // occupying a separate, empty column beside a skinny progress rail.
        var traceRight = bounds.Right - Math.Max(3, (int)MathF.Round(4f * unit));
        var labelCenterX = bounds.X + bounds.Width * 0.5f;

        // Cache authored SuperFlick events in one place so both the live timeline
        // forecast and seek-route reconstruction use the same ordered event list.
        EnsureEditorTimelineTraceCache();

        _spriteBatch.Draw(_pixel,
            new Rectangle(trackX, (int)MathF.Round(trackTop), Math.Max(1, traceRight - trackX), trackHeight),
            new Color(8, 8, 9, 80));

        float TimeToY(double seconds)
        {
            var ratio = duration > 0.0001 ? Math.Clamp(seconds / duration, 0.0, 1.0) : 0.0;
            return trackBottom - (float)ratio * trackHeight;
        }

        int BankTraceIndex(GearState state, string side)
        {
            // Each rail represents a bank, not an individual playable track.
            // Left-to-right order: L1K, L2K, L3K, R3K, R2K, R1K.
            var bank = GearTimeline.ActiveBankForSide(state, side);
            if (side == "left")
            {
                return bank switch
                {
                    "Forward" => 0,   // L1K
                    "Center" => 1,    // L2K
                    "Backward" => 2,  // L3K
                    _ => 1
                };
            }

            return bank switch
            {
                "Backward" => 3, // R3K
                "Center" => 4,   // R2K
                "Forward" => 5,  // R1K
                _ => 4
            };
        }

        float BankTraceX(GearState state, string side) => TraceBaseX(BankTraceIndex(state, side));

        float TraceBaseX(int traceIndex)
        {
            var side = traceIndex < 3 ? "left" : "right";
            var localSlot = traceIndex % 3;
            var innerLeft = trackX + 4f * unit;
            var innerRight = traceRight - 4f * unit;
            var sideGap = 8f * unit;
            var sideWidth = Math.Max(1f, (innerRight - innerLeft - sideGap) * 0.5f);
            var groupStart = side == "left" ? innerLeft : innerLeft + sideWidth + sideGap;
            return groupStart + localSlot * (sideWidth * 0.5f);
        }

        // Keep the dark rails a little wider than the colored route. That way
        // the six grey lines remain visibly present on both sides of the overlay,
        // including the elapsed green section.
        var traceWidth = Math.Max(2.4f, 2.4f * unit);
        var routeWidth = Math.Max(1f, 1.2f * unit);

        // Draw six straight, permanent dark-grey rails first. They are fixed in
        // place for the entire chart; only the route overlay below changes shape.
        var chartStartY = TimeToY(0.0);
        var chartEndY = TimeToY(duration);
        for (var traceIndex = 0; traceIndex < 6; traceIndex++)
        {
            var x = TraceBaseX(traceIndex);
            DrawLine(_spriteBatch, new Vector2(x, chartStartY),
                new Vector2(x, chartEndY), traceWidth, inactiveColor);
        }

        // Current route at the playhead and the actual preview-state history.
        // This history is sampled separately from the authored route, because
        // the editor lets either side be switched to an alternate bank manually.
        var activeRouteState = _chartEditorMode && _editorPreviewGearState is not null
            ? _editorPreviewGearState.Copy()
            : (_gear?.AtTick(0) ?? new GearState());
        var historyCount = 0;
        while (historyCount < _editorTimelineRouteHistory.Count &&
               _editorTimelineRouteHistory[historyCount].Seconds <= playSeconds + 0.0001)
            historyCount++;
        var fallbackHistoryState = historyCount > 0
            ? _editorTimelineRouteHistory[0].State
            : (_gear?.Initial ?? activeRouteState.Copy());
        var actualRoute = _editorTimelineRouteHistory
            .Take(historyCount)
            .Select(point => (Seconds: point.Seconds, State: point.State.Copy()))
            .ToList();
        if (actualRoute.Count == 0)
            actualRoute.Add((0.0, fallbackHistoryState.Copy()));
        if (actualRoute[^1].Seconds < playSeconds - 0.0001)
            actualRoute.Add((playSeconds, activeRouteState.Copy()));
        else
            actualRoute[^1] = (playSeconds, activeRouteState.Copy());

        // Build the authored route from the chart's SuperFlicks. This is always
        // the intended route, regardless of a manual preview-bank override.
        var intendedRoute = new List<(double Seconds, GearState State)>();
        var intendedRouteState = _gear?.Initial ?? activeRouteState.Copy();
        intendedRoute.Add((0.0, intendedRouteState.Copy()));
        if (_gear is not null && _tempo is not null)
        {
            foreach (var tickGroup in _editorTimelineTraceSuperFlicks.GroupBy(ev => ev.Tick))
            {
                var transitionSeconds = _tempo.TickToSeconds(tickGroup.Key);
                if (transitionSeconds > duration)
                    break;
                transitionSeconds = Math.Max(0.0, transitionSeconds);

                var before = intendedRouteState.Copy();
                var appliedSides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var changed = false;
                foreach (var ev in tickGroup)
                {
                    var side = ev.GetSuperFlickSide();
                    if (side is null || appliedSides.Contains(side) ||
                        !before.CanApplySuperFlick(ev.Attr / 10, ev.Track))
                        continue;
                    appliedSides.Add(side);
                    changed |= intendedRouteState.ApplySuperFlick(ev.Attr / 10, ev.Track);
                }

                if (changed)
                    intendedRoute.Add((transitionSeconds, intendedRouteState.Copy()));
            }
        }

        static bool SameRouteSide(GearState a, GearState b, string side) =>
            string.Equals(GearTimeline.ActiveBankForSide(a, side),
                GearTimeline.ActiveBankForSide(b, side), StringComparison.OrdinalIgnoreCase);

        GearState StateAt(IReadOnlyList<(double Seconds, GearState State)> route, double seconds)
        {
            // Upper-bound search keeps timeline rendering inexpensive even when
            // a chart has many authored SuperFlick transitions.
            var lo = 0;
            var hi = route.Count;
            while (lo < hi)
            {
                var mid = lo + ((hi - lo) / 2);
                if (route[mid].Seconds <= seconds + 0.0001)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return route[Math.Max(0, lo - 1)].State;
        }

        GearState IntendedStateAt(double seconds) => StateAt(intendedRoute, seconds);

        // Draw any route over a clipped time range. An optional side filter keeps
        // left and right progress colors independent when only one side deviates.
        void DrawRouteRange(IReadOnlyList<(double Seconds, GearState State)> route,
            double rangeStart, double rangeEnd, Color color, string? onlySide = null)
        {
            if (route.Count == 0 || rangeEnd <= rangeStart)
                return;

            for (var routeIndex = 0; routeIndex < route.Count; routeIndex++)
            {
                var routePoint = route[routeIndex];
                var segmentEndSeconds = routeIndex + 1 < route.Count
                    ? route[routeIndex + 1].Seconds
                    : duration;
                var startSeconds = Math.Max(rangeStart, Math.Clamp(routePoint.Seconds, 0.0, duration));
                var endSeconds = Math.Min(rangeEnd, Math.Clamp(segmentEndSeconds, 0.0, duration));
                if (endSeconds > startSeconds)
                {
                    for (var sideIndex = 0; sideIndex < 2; sideIndex++)
                    {
                        var side = sideIndex == 0 ? "left" : "right";
                        if (onlySide is not null && !string.Equals(side, onlySide, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var x = BankTraceX(routePoint.State, side);
                        DrawLine(_spriteBatch, new Vector2(x, TimeToY(startSeconds)),
                            new Vector2(x, TimeToY(endSeconds)), routeWidth, color);
                    }
                }

                if (routeIndex + 1 < route.Count)
                {
                    var transitionSeconds = route[routeIndex + 1].Seconds;
                    if (transitionSeconds < rangeStart - 0.0001 || transitionSeconds > rangeEnd + 0.0001)
                        continue;
                    var transitionY = TimeToY(transitionSeconds);
                    var nextState = route[routeIndex + 1].State;
                    for (var sideIndex = 0; sideIndex < 2; sideIndex++)
                    {
                        var side = sideIndex == 0 ? "left" : "right";
                        if (onlySide is not null && !string.Equals(side, onlySide, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var oldX = BankTraceX(routePoint.State, side);
                        var newX = BankTraceX(nextState, side);
                        if (MathF.Abs(newX - oldX) > 0.01f)
                            DrawLine(_spriteBatch, new Vector2(oldX, transitionY),
                                new Vector2(newX, transitionY), routeWidth, color);
                    }
                }
            }
        }

        // Green is always the passed authored route, for both sides.
        DrawRouteRange(intendedRoute, 0.0, playSeconds, progressColor);

        var intendedStateAtPlayhead = IntendedStateAt(playSeconds);
        var boundaries = new SortedSet<double> { 0.0, playSeconds };
        foreach (var point in actualRoute)
            if (point.Seconds >= 0.0 && point.Seconds <= playSeconds)
                boundaries.Add(point.Seconds);
        foreach (var point in intendedRoute)
            if (point.Seconds >= 0.0 && point.Seconds <= playSeconds)
                boundaries.Add(point.Seconds);
        var routeBoundaries = boundaries.ToList();

        var alternateProgressColor = new Color(248, 70, 82, 245);
        var intendedFutureColor = new Color(245, 158, 11, 235);
        foreach (var side in new[] { "left", "right" })
        {
            // If this side is currently back on the authored route, its red trace
            // is cleared completely. The other side can remain alternate at the
            // same time without affecting this side's color or progress.
            if (SameRouteSide(activeRouteState, intendedStateAtPlayhead, side))
                continue;

            // Find the most recent point where this side matched its intended bank.
            // That resets red progress on each return to the intended path, so an
            // earlier alternate-route attempt cannot leave stale red behind.
            var lastMatchSeconds = 0.0;
            foreach (var boundary in routeBoundaries)
            {
                if (SameRouteSide(StateAt(actualRoute, boundary), IntendedStateAt(boundary), side))
                    lastMatchSeconds = boundary;
            }

            // Split on both actual and authored route transitions. This is important
            // when an authored SuperFlick changes the expected bank but the preview
            // deliberately stays on the alternate bank and has no actual transition.
            for (var i = 0; i + 1 < routeBoundaries.Count; i++)
            {
                var startSeconds = routeBoundaries[i];
                var endSeconds = routeBoundaries[i + 1];
                if (endSeconds <= lastMatchSeconds + 0.0001 || endSeconds <= startSeconds)
                    continue;

                var actualAtStart = StateAt(actualRoute, startSeconds);
                var intendedAtStart = IntendedStateAt(startSeconds);
                if (!SameRouteSide(actualAtStart, intendedAtStart, side))
                {
                    var segmentStart = Math.Max(startSeconds, lastMatchSeconds);
                    if (endSeconds > segmentStart)
                    {
                        var x = BankTraceX(actualAtStart, side);
                        DrawLine(_spriteBatch, new Vector2(x, TimeToY(segmentStart)),
                            new Vector2(x, TimeToY(endSeconds)), routeWidth, alternateProgressColor);
                    }
                }

                // Draw only this side's actual transition connector, and only when
                // it belongs to the current (not already reset) alternate excursion.
                var actualAtEnd = StateAt(actualRoute, endSeconds);
                var oldX = BankTraceX(actualAtStart, side);
                var newX = BankTraceX(actualAtEnd, side);
                if (endSeconds > lastMatchSeconds + 0.0001 && MathF.Abs(newX - oldX) > 0.01f)
                {
                    var mismatchedBefore = !SameRouteSide(actualAtStart, intendedAtStart, side);
                    var mismatchedAfter = !SameRouteSide(actualAtEnd, IntendedStateAt(endSeconds), side);
                    if (mismatchedBefore || mismatchedAfter)
                        DrawLine(_spriteBatch, new Vector2(oldX, TimeToY(endSeconds)),
                            new Vector2(newX, TimeToY(endSeconds)), routeWidth, alternateProgressColor);
                }
            }

            // Only the side currently off the intended route gets the orange
            // intended-future trace. The white forecast remains independent below.
            DrawRouteRange(intendedRoute, playSeconds, duration, intendedFutureColor, side);
        }

        // Predict forward from the bank that is currently active in the editor.
        // Only SuperFlicks that remain unresolved and are valid for the simulated
        // state can change the forecast. Events at one tick are checked against one
        // shared pre-transition snapshot, matching GearTimeline's same-tick rule.
        if (_gear is not null && _tempo is not null && playSeconds < duration && _editorTimelineTraceSuperFlicks.Count > 0)
        {
            var futureRoute = _editorTimelineForecastRoute;
            futureRoute.Clear();
            futureRoute.Add((playSeconds, activeRouteState.Copy()));
            var forecastState = activeRouteState.Copy();
            var eventIndex = 0;
            while (eventIndex < _editorTimelineTraceSuperFlicks.Count)
            {
                var tick = _editorTimelineTraceSuperFlicks[eventIndex].Tick;
                var groupStart = eventIndex;
                var transitionSeconds = _tempo.TickToSeconds(tick);
                var nextEventIndex = eventIndex + 1;
                while (nextEventIndex < _editorTimelineTraceSuperFlicks.Count &&
                       _editorTimelineTraceSuperFlicks[nextEventIndex].Tick == tick)
                    nextEventIndex++;
                eventIndex = nextEventIndex;

                if (transitionSeconds <= playSeconds + 0.0001)
                    continue;
                if (transitionSeconds > duration)
                    break;

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

                    // Autoplay records successful score entries separately from manual
                    // hit events, so exclude those events here too without scanning the
                    // entire score-entry list on every frame.
                    var alreadyScored = _scoreSuperFlickEntries.TryGetValue(key, out var scoreEntry) &&
                                        _scoredEntryIds.Contains(scoreEntry.Id);

                    var side = ev.GetSuperFlickSide();
                    if (side is null)
                        continue;
                    var sideAlreadyApplied = side.Equals("left", StringComparison.OrdinalIgnoreCase)
                        ? appliedLeft
                        : appliedRight;
                    if (wasManuallyHit || alreadyScored)
                    {
                        // The live state already includes this success, sometimes a
                        // little before its chart tick because hits have a timing window.
                        // Do not predict a second same-side transition at that same tick.
                        if (side.Equals("left", StringComparison.OrdinalIgnoreCase)) appliedLeft = true;
                        else appliedRight = true;
                        continue;
                    }
                    if (wasMissed || wasResolved || sideAlreadyApplied ||
                        !before.CanApplySuperFlick(ev.Attr / 10, ev.Track))
                        continue;
                    if (forecastState.ApplySuperFlick(ev.Attr / 10, ev.Track))
                    {
                        if (side.Equals("left", StringComparison.OrdinalIgnoreCase)) appliedLeft = true;
                        else appliedRight = true;
                        changed = true;
                    }
                }

                if (changed)
                    futureRoute.Add((transitionSeconds, forecastState.Copy()));
            }

            // Draw the forecast in white from the playhead onward. The permanent
            // grey rails remain visible around it, including under green history.
            for (var routeIndex = 0; routeIndex < futureRoute.Count; routeIndex++)
            {
                var routePoint = futureRoute[routeIndex];
                var startSeconds = Math.Clamp(routePoint.Seconds, playSeconds, duration);
                var endSeconds = routeIndex + 1 < futureRoute.Count
                    ? Math.Clamp(futureRoute[routeIndex + 1].Seconds, playSeconds, duration)
                    : duration;
                if (endSeconds > startSeconds)
                {
                    // Forecast one bank path for each side, regardless of the
                    // number of playable tracks contained in that bank.
                    for (var sideIndex = 0; sideIndex < 2; sideIndex++)
                    {
                        var side = sideIndex == 0 ? "left" : "right";
                        var x = BankTraceX(routePoint.State, side);
                        DrawLine(_spriteBatch, new Vector2(x, TimeToY(startSeconds)),
                            new Vector2(x, TimeToY(endSeconds)), routeWidth, futureActiveColor);
                    }
                }

                if (routeIndex + 1 < futureRoute.Count)
                {
                    var nextState = futureRoute[routeIndex + 1].State;
                    var transitionY = TimeToY(futureRoute[routeIndex + 1].Seconds);
                    for (var sideIndex = 0; sideIndex < 2; sideIndex++)
                    {
                        var side = sideIndex == 0 ? "left" : "right";
                        var oldX = BankTraceX(routePoint.State, side);
                        var newX = BankTraceX(nextState, side);
                        if (MathF.Abs(newX - oldX) > 0.01f)
                            DrawLine(_spriteBatch, new Vector2(oldX, transitionY),
                                new Vector2(newX, transitionY), routeWidth, futureActiveColor);
                    }
                }
            }
        }

        // Five time landmarks span the widened route map. Timestamp labels are
        // now intentionally overlaid inside the same progress bar.
        for (var i = 0; i < 5; i++)
        {
            var ratio = i / 4f;
            var y = trackBottom - ratio * trackHeight;
            _spriteBatch.Draw(_pixel,
                new Rectangle(trackX, (int)MathF.Round(y), Math.Max(1, traceRight - trackX),
                    Math.Max(1, (int)MathF.Round(unit))),
                new Color(trackColor.R, trackColor.G, trackColor.B, i is 0 or 4 ? (byte)90 : (byte)45));

            var label = FormatEditorTimestamp(duration * ratio);
            var maxLabelWidth = Math.Max(1f, bounds.Width - _projection.ScalePixels(8f));
            label = FitHudText(labelFont, label, maxLabelWidth);
            var labelSize = UnicodeTextRenderer.MeasureString(labelFont, label);
            var labelX = labelCenterX - labelSize.X * 0.5f;
            var labelY = y - labelSize.Y * 0.5f;
            var labelColor = i is 0 or 4 ? interfaceUi.ButtonTextColor : interfaceUi.MutedTextColor;
            // A one-pixel shadow keeps the plain labels readable over any lane art.
            UnicodeTextRenderer.DrawString(_spriteBatch, labelFont, label,
                new Vector2(labelX + unit, labelY + unit), new Color(0, 0, 0, 150));
            UnicodeTextRenderer.DrawString(_spriteBatch, labelFont, label, new Vector2(labelX, labelY), labelColor);
        }

        var playheadThickness = Math.Max(2, (int)MathF.Round(4f * unit));
        _spriteBatch.Draw(_pixel,
            new Rectangle(bounds.X, (int)MathF.Round(playY) - playheadThickness / 2, bounds.Width, playheadThickness),
            new Color(52, 211, 153, 160));
    }

    private List<(string Label, string Value)> GetEditorMetaRows(double currentTick)
    {
        if (_chart is null)
            return new List<(string Label, string Value)> { ("CHART", "No chart loaded") };

        string Show(string? value) => string.IsNullOrWhiteSpace(value) ? "Not set" : value;
        var events = _chart.Events;
        var notes = events.Where(ev => ev.Kind == "Note" && ev.RawType == 1 && ev.Track is >= 0 and <= 15).ToList();
        var source = _loadedSourcePath ?? _sourceName ?? _lastLoadedSourceName;
        var initialMode = _chart.InitialGearAttr switch { 0 or 2 => "1K", 4 => "2K", _ => "3K" };
        var templateLabel = _editorBundleTemplate switch
        {
            ChartBundleTemplate.Tsc => "TSC",
            ChartBundleTemplate.Bold => "BOLD",
            ChartBundleTemplate.Tsts => "TSTS",
            _ => "TSC"
        };
        var seriesValue = _editorBundleTemplate == ChartBundleTemplate.Tsts ? "Unsupported by TSTS" : Show(_chart.Series);
        var judgementPrefix = _editorCustomJudgementWindowsEnabled;
        var difficultyCode = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart.Difficulty, _sourceName);

        var rows = _editorMetaCategory switch
        {
            "SONG" => new List<(string Label, string Value)>
            {
                ("BUNDLE TEMPLATE", templateLabel),
                ("VERSION", Show(_chart.Version)),
                ("SONG", Show(_chart.Name)),
                ("ARTIST", Show(_chart.Artist)),
                ("MAPPER", Show(_chart.Mapper)),
                ("DESCRIPTION", Show(_chart.Description)),
                ("ELEMENT", Show(_chart.Element)),
                ("SERIES", seriesValue),
                ("LEVEL", _chart.Level?.ToString(CultureInfo.InvariantCulture) ?? "Not set"),
                ("BPM", _chart.MetadataBpm.ToString("0.###", CultureInfo.InvariantCulture)),
                ("INITIAL CHART BPM", GetEditorInitialChartBpm().ToString("0.###", CultureInfo.InvariantCulture)),
                ("START GEAR", $"{initialMode} (attr {_chart.InitialGearAttr})"),
                ("PLAYABLE TICK LENGTH", _chart.PlayTick.ToString(CultureInfo.InvariantCulture)),
                ("CHART DURATION", $"{_chart.PlayTime:0.###} s"),
                ("AUDIO ORIGIN TICK", _chart.AudioOriginTick.ToString(CultureInfo.InvariantCulture)),
            },
            "MEDIA" => new List<(string Label, string Value)>
            {
                ("MUSIC", GetEditorMediaButtonText("music")),
                ("PREVIEW", _editorMetaPanelOpen
                    ? GetEditorSavedPreviewPositionLabel()
                    : Show(_chart.PreviewPath)),
                ("ALBUM", GetEditorMediaButtonText("album")),
                ("ALBUM BIG", _editorBundleTemplate == ChartBundleTemplate.Tsts ? "Unsupported by TSTS" : GetEditorMediaButtonText("album_big"))
            },
            "JUDGEMENT" => new List<(string Label, string Value)>
            {
                ("CUSTOM JUDGEMENT WINDOWS", _editorCustomJudgementWindowsEnabled ? "Enabled" : "Disabled"),
                ("PERFECT+ MS", judgementPrefix ? _chart.PerfectPlusWindowMs.ToString("0.###", CultureInfo.InvariantCulture) : "Disabled"),
                ("PERFECT MS", judgementPrefix ? _chart.PerfectWindowMs.ToString("0.###", CultureInfo.InvariantCulture) : "Disabled"),
                ("GOOD MS", judgementPrefix ? _chart.GoodWindowMs.ToString("0.###", CultureInfo.InvariantCulture) : "Disabled")
            },
            "STATS" => new List<(string Label, string Value)>
            {
                ("SOURCE FILE", Show(source)),
                ("EVENTS", events.Count.ToString("N0", CultureInfo.InvariantCulture)),
                ("NOTES", notes.Count.ToString("N0", CultureInfo.InvariantCulture)),
                ("TAP / FLICK", notes.Count(ev => ev.NoteType == NoteType.Single && !ev.IsFlick && !ev.IsSuperFlick).ToString("N0", CultureInfo.InvariantCulture) + " / " + notes.Count(ev => ev.IsFlick).ToString("N0", CultureInfo.InvariantCulture)),
                ("SUPERFLICKS", notes.Count(ev => ev.IsSuperFlick).ToString("N0", CultureInfo.InvariantCulture)),
                ("HOLDS / VIBRATO", notes.Count(ev => ev.NoteType == NoteType.SingleLong).ToString("N0", CultureInfo.InvariantCulture) + " / " + notes.Count(ev => ev.NoteType == NoteType.Vibrato).ToString("N0", CultureInfo.InvariantCulture)),
                ("LINK POINTS", notes.Count(ev => ev.NoteType is NoteType.LinkHead or NoteType.LinkTail).ToString("N0", CultureInfo.InvariantCulture)),
                ("LINK SEGMENTS", _chart.Links.Count.ToString("N0", CultureInfo.InvariantCulture)),
                ("TEMPO CHANGES", _chart.TempoEvents.Count.ToString("N0", CultureInfo.InvariantCulture)),
                ("TRACKS USED", events.Select(ev => ev.Track).Distinct().Count().ToString("N0", CultureInfo.InvariantCulture)),
                ("AUX EVENTS", events.Count(ev => ev.RawType != 1).ToString("N0", CultureInfo.InvariantCulture)),
                ("INSTRUMENTS", _chart.Instruments.Count.ToString("N0", CultureInfo.InvariantCulture))
            },
            "TEST" => new List<(string Label, string Value)>(),
            _ => new List<(string Label, string Value)>()
        };
        // SC passcodes are only meaningful on the Extra difficulty tab.
        // Keep destructive actions at the bottom of the SONG list.
        if (string.Equals(_editorMetaCategory, "SONG", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(difficultyCode, "xt", StringComparison.OrdinalIgnoreCase))
                rows.Insert(2, ("SC PASSCODE", Show(_chart.ScPasscode)));
            rows.Add(("DELETE DIFFICULTY", HasEditorDifficultyChart(difficultyCode) ? "Delete this difficulty chart" : "No chart file supplied"));
        }
        return rows.Where(row => !row.Value.StartsWith("Unsupported by ", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private bool HasEditorMediaResource(string field)
    {
        if (field == "music" && (_editorSharedMusicBytes is { Length: > 0 } || _loadedMusicBytes is { Length: > 0 }))
            return true;
        if (field == "album" && _editorSharedAlbumBytes is { Length: > 0 }) return true;
        if (field == "album_big" && _editorSharedAlbumBigBytes is { Length: > 0 }) return true;
        if (field is "album" or "album_big")
            return GetEditorArtworkAssetPath(field) is not null;

        var sourceDirectory = _tstsWorkingDirectory;
        if (string.IsNullOrWhiteSpace(sourceDirectory) && !string.IsNullOrWhiteSpace(_loadedSourcePath))
            sourceDirectory = GetSongAssetDirectoryForChartPath(_loadedSourcePath);
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
            return false;

        var names = field switch
        {
            "music" => new[] { _editorBundleTemplate == ChartBundleTemplate.Tsts ? "song.opus" : "music.opus" },
            "album" => _editorBundleTemplate == ChartBundleTemplate.Tsts
                ? new[] { "cover.webp", "album.webp", "album.png", "album.jpg", "album.jpeg" }
                : _editorBundleTemplate == ChartBundleTemplate.Tsc
                    ? new[] { "album.png", "album.webp", "album.jpg", "album.jpeg" }
                    : new[] { "album", "album.png", "album.webp", "album.jpg", "album.jpeg" },
            "album_big" => _editorBundleTemplate == ChartBundleTemplate.Tsc
                ? new[] { "album_big.png", "album_big.webp", "album_big.jpg", "album_big.jpeg" }
                : new[] { "album_big", "album_big.png", "album_big.webp", "album_big.jpg", "album_big.jpeg" },
            _ => Array.Empty<string>()
        };
        return names.Any(name => File.Exists(Path.Combine(sourceDirectory, name)));
    }

    private string GetEditorMediaButtonText(string field)
        => HasEditorMediaResource(field) ? "click to change" : "click to set";

    private string FitEditorTextAroundCaret(SpriteFont font, string value, int caretIndex,
        float maxWidth, string fieldId, float textScale, out string caretPrefix, out int visibleStart, out int visibleEnd)
    {
        var safeValue = FontSafeText(font, value);
        var caret = Math.Clamp(caretIndex, 0, safeValue.Length);
        maxWidth = Math.Max(1f, maxWidth);
        textScale = Math.Max(0.01f, textScale);

        // Keep the text anchored to its current viewport. Do not rebalance the
        // left and right sides around the caret on every arrow press; that makes
        // the whole string appear to jump as the caret moves. Scroll only when
        // the caret reaches an edge of the visible text.
        if (UnicodeTextRenderer.MeasureString(font, safeValue).X * textScale <= maxWidth)
        {
            _editorTextHorizontalScrollStarts[fieldId] = 0;
            visibleStart = 0;
            visibleEnd = safeValue.Length;
            caretPrefix = safeValue[..caret];
            return safeValue;
        }

        var start = _editorTextHorizontalScrollStarts.TryGetValue(fieldId, out var previousStart)
            ? Math.Clamp(previousStart, 0, safeValue.Length)
            : 0;
        if (caret < start)
            start = caret;

        while (start < caret && UnicodeTextRenderer.MeasureString(font, safeValue[start..caret]).X * textScale > maxWidth)
            start++;

        // The prefix from the viewport start through the caret now fits. Keep
        // the same start while the caret moves within it, then fill remaining
        // space to the right without inserting a fake caret character.
        var end = caret;
        while (end < safeValue.Length && UnicodeTextRenderer.MeasureString(font, safeValue[start..(end + 1)]).X * textScale <= maxWidth)
            end++;

        visibleStart = start;
        visibleEnd = Math.Max(caret, end);
        _editorTextHorizontalScrollStarts[fieldId] = start;
        caretPrefix = safeValue[start..caret];
        return safeValue[start..visibleEnd];
    }

    private void DrawEditorTextCaret(string caretPrefix, Vector2 textPosition, float textScale, float unit, SpriteFont? font = null)
    {
        if (_font is null || _pixel is null) return;
        var caretFont = font ?? _font;
        var width = UnicodeTextRenderer.MeasureString(caretFont, caretPrefix).X * textScale;
        var fontHeight = UnicodeTextRenderer.MeasureString(caretFont, "Ag").Y * textScale;
        var x = (int)MathF.Round(textPosition.X + width);
        var y = (int)MathF.Round(textPosition.Y);
        var w = Math.Max(1, (int)MathF.Round(1.5f * unit));
        var h = Math.Max(1, (int)MathF.Ceiling(fontHeight));
        _spriteBatch.Draw(_pixel, new Rectangle(x, y, w, h), Text);
    }

    private void DrawEditorLanTestTab(int virtualWidth, int virtualHeight)
    {
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var font = _chartEditorInterfaceLayout.LabelFont;
        var muted = _chartEditorInterfaceLayout.MutedTextColor;
        var normal = _chartEditorInterfaceLayout.ButtonTextColor;
        string status;
        string details;
        string address;
        bool running;
        bool busy;
        bool stale;
        lock (_editorLanTestSync)
        {
            status = _editorLanTestStatus;
            details = _editorLanTestDetails;
            address = _editorLanTestAddress;
            running = _editorLanTestRunning;
            busy = _editorLanTestBusy;
            stale = _editorLanTestDataStale;
        }
        var chartSummary = GetEditorLanTestChartSummary();
        var musicAvailable = IsEditorLanTestMusicAvailable();
        var textFont = GetChartEditorFont(font, 24f);
        float cursorY = content.Y + (int)MathF.Round(8f * unit);
        var textLineHeight = Math.Max(16f * unit,
            UnicodeTextRenderer.GetLogicalLineSpacing(textFont) * unit);
        var textWidth = Math.Max(1f, content.Width);

        void Line(string value, Color color)
        {
            var safeText = FontSafeText(textFont, value);
            foreach (var wrapped in WrapEditorHudText(textFont, safeText, textWidth))
            {
                UnicodeTextRenderer.DrawString(_spriteBatch, textFont, wrapped,
                    new Vector2(content.X, cursorY), color);
                cursorY += textLineHeight;
            }
            cursorY += 4f * unit;
        }

        Line("STATUS: " + status, running ? new Color(52, 211, 153) : normal);
        Line("MUSIC: " + (musicAvailable ? "Ready" : "Missing — import music under MEDIA first"),
            musicAvailable ? normal : _chartEditorInterfaceLayout.DangerColor);
        Line("CHARTS: " + chartSummary, normal);
        if (!string.IsNullOrWhiteSpace(address))
            Line("ADDRESS: " + address, normal);
        Line(details, muted);
        if (stale && running)
            Line("TEST DATA IS OUT OF DATE — refresh before the next play.", new Color(251, 191, 36));
        Line("Enable Local beatmap testing in your ReSONANCE account settings.", muted);
        Line("Keep the PC and phone on the same private Wi-Fi. Allow TCP/UDP 8765 through Windows Firewall.", muted);

        if (running)
        {
            DrawEditorMetaButton(GetEditorMetaLanTestButtonBounds(1, virtualWidth, virtualHeight),
                busy ? "REFRESHING…" : "REFRESH TEST DATA",
                busy ? new Color(44, 44, 46, 250) : new Color(28, 28, 30, 250),
                busy ? new Color(100, 100, 102) : Active);
            DrawEditorMetaButton(GetEditorMetaLanTestButtonBounds(2, virtualWidth, virtualHeight), "STOP",
                new Color(55, 30, 35, 245), _chartEditorInterfaceLayout.DangerColor);
        }
        else
        {
            var enabled = !busy && _chart is not null && musicAvailable &&
                chartSummary is not ("No enabled charts" or "Open a chart first");
            DrawEditorMetaButton(GetEditorMetaLanTestButtonBounds(0, virtualWidth, virtualHeight),
                busy ? "PREPARING…" : "START LAN TEST",
                enabled ? new Color(22, 52, 42, 250) : new Color(38, 38, 40, 245),
                enabled ? Active : new Color(100, 100, 102));
        }
    }

    private void DrawEditorMetaPanel(int virtualWidth, int virtualHeight)
    {
        var panel = GetEditorMetaPanelBounds(virtualWidth, virtualHeight);
        var interfaceUi = _chartEditorInterfaceLayout;
        var headerFont = GetChartEditorFont(interfaceUi.HeaderFont);
        var labelFont = GetChartEditorFont(interfaceUi.LabelFont);
        var unit = _projection.ScalePixels(1f);
        var padding = (int)MathF.Round(12f * unit);
        var headerHeight = (int)MathF.Round(54f * unit);
        var edge = Math.Max(2, (int)MathF.Round(2f * unit));

        _spriteBatch.Draw(_pixel, panel, interfaceUi.PanelColor);
        _spriteBatch.Draw(_pixel, new Rectangle(panel.X, panel.Y, edge, panel.Height), interfaceUi.AccentColor);
        _spriteBatch.Draw(_pixel, new Rectangle(panel.X, panel.Y, panel.Width, edge), new Color(90, 90, 92, 240));
        _spriteBatch.Draw(_pixel, new Rectangle(panel.X, panel.Bottom - edge, panel.Width, edge), new Color(90, 90, 92, 240));

        var title = FontSafeText(headerFont, interfaceUi.Label("header:SONG / CHART META", "SONG / CHART META"));
        UnicodeTextRenderer.DrawString(_spriteBatch, headerFont, title,
            new Vector2(panel.X + padding, panel.Y + padding + _projection.ScalePixels(10f)), interfaceUi.ButtonTextColor);
        DrawEditorMetaButton(GetEditorMetaCloseButtonBounds(virtualWidth, virtualHeight), "X",
            new Color(55, 30, 35, 245), interfaceUi.DangerColor);
        DrawEditorMetaButton(GetEditorMetaExitButtonBounds(virtualWidth, virtualHeight), "EXIT",
            new Color(55, 30, 35, 245), interfaceUi.DangerColor);
        _spriteBatch.Draw(_pixel,
            new Rectangle(panel.X + padding, panel.Y + headerHeight, panel.Width - padding * 2, edge),
            new Color(90, 90, 92, 240));

        var rows = GetEditorMetaRows(_tempo?.SecondsToTick(_playSeconds) ?? 0.0);
        var difficultyLabel = FontSafeText(labelFont, "DIFFICULTY");
        UnicodeTextRenderer.DrawString(_spriteBatch, labelFont, difficultyLabel,
            new Vector2(panel.X + padding, panel.Y + _projection.ScalePixels(52f)), interfaceUi.MutedTextColor);
        for (var i = 0; i < EditorDifficultyCodes.Length; i++)
        {
            var bounds = GetEditorMetaDifficultyButtonBounds(i, virtualWidth, virtualHeight);
            var code = EditorDifficultyCodes[i];
            var selected = string.Equals(code, _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart?.Difficulty, _sourceName), StringComparison.OrdinalIgnoreCase);
            DrawEditorMetaButton(bounds, GetEditorDifficultyName(code).ToUpperInvariant(),
                selected ? new Color(58, 58, 60, 250) : new Color(28, 28, 30, 245),
                selected ? new Color(52, 211, 153) : new Color(100, 100, 102));
        }

        for (var i = 0; i < EditorMetaCategories.Length; i++)
        {
            var bounds = GetEditorMetaCategoryButtonBounds(i, virtualWidth, virtualHeight);
            var selected = string.Equals(EditorMetaCategories[i], _editorMetaCategory, StringComparison.OrdinalIgnoreCase);
            DrawEditorMetaButton(bounds, EditorMetaCategories[i],
                selected ? new Color(58, 58, 60, 250) : new Color(24, 24, 26, 245),
                selected ? new Color(52, 211, 153) : new Color(100, 100, 102));
        }

        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        if (string.Equals(_editorMetaCategory, "TEST", StringComparison.OrdinalIgnoreCase))
        {
            DrawEditorLanTestTab(virtualWidth, virtualHeight);
        }
        else if (string.Equals(_editorMetaCategory, "MEDIA", StringComparison.OrdinalIgnoreCase))
        {
            DrawEditorMediaTab(virtualWidth, virtualHeight);
        }
        else
        {
            ClampEditorMetaScrollOffset(rows, virtualWidth, virtualHeight);
            var rowLayouts = GetEditorMetaRowLayouts(rows, virtualWidth, virtualHeight);
            var firstRow = _editorMetaScrollOffset;
            for (var i = firstRow; i < rows.Count; i++)
            {
                var (label, rawValue) = rows[i];
                var labelBounds = GetEditorMetaFieldLabelBounds(i, virtualWidth, virtualHeight, rows, rowLayouts);
                var bounds = GetEditorMetaFieldBounds(i, virtualWidth, virtualHeight, rows, rowLayouts);
                if (labelBounds.Y >= content.Bottom || bounds.Y >= content.Bottom ||
                    (i > firstRow && bounds.Bottom + rowLayouts[i].RowGap > content.Bottom))
                    break;
                var fieldKey = GetEditorMetaFieldKey(label);
                var editing = fieldKey is not null && string.Equals(fieldKey, _editorMetaEditingField, StringComparison.OrdinalIgnoreCase);
                var value = editing ? _editorMetaInputText : rawValue;
                var mediaPicker = (label is "MUSIC" or "ALBUM" or "ALBUM BIG") && !(label == "ALBUM BIG" && _editorBundleTemplate == ChartBundleTemplate.Tsts);
                var previewMedia = label == "PREVIEW";
                var customToggle = label == "CUSTOM JUDGEMENT WINDOWS";
                var elementSelect = label == "ELEMENT";
                var deleteDifficulty = label == "DELETE DIFFICULTY";
                var difficultyToggle = false;
                var editable = fieldKey is not null || mediaPicker || previewMedia || customToggle || elementSelect || difficultyToggle || deleteDifficulty;
                var element = elementSelect ? NormalizeEditorElement(_chart?.Element) : string.Empty;
                var accent = editing ? new Color(255, 160, 122) : customToggle
                    ? (_editorCustomJudgementWindowsEnabled ? new Color(52, 211, 153) : new Color(176, 128, 86))
                    : elementSelect ? GetEditorElementColor(element)
                    : difficultyToggle ? (IsEditorDifficultyEnabled(_editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart?.Difficulty, _sourceName))
                        ? new Color(52, 211, 153) : new Color(248, 113, 113))
                    : (mediaPicker || previewMedia) ? Active : editable ? new Color(100, 100, 102) : new Color(100, 100, 102, 245);

                var labelAvailableWidth = Math.Max(1f, labelBounds.Width - 4f * unit);
                var fittedLabel = FitChartEditorText(label, interfaceUi.LabelFont, 24f,
                    labelAvailableWidth, labelBounds.Height - 4f * unit);
                var labelSize = fittedLabel.Measured * fittedLabel.Scale;
                UnicodeTextRenderer.DrawString(_spriteBatch, fittedLabel.Font, fittedLabel.Text,
                    new Vector2(labelBounds.X + 2f * unit,
                        labelBounds.Y + (labelBounds.Height - labelSize.Y) * 0.5f),
                    interfaceUi.MutedTextColor, 0f, Vector2.Zero, fittedLabel.Scale, SpriteEffects.None, 0f);

                // Keep the original font size and let the field's dynamic height carry
                // wrapped values. Only the active editor uses horizontal caret scrolling.
                var display = editing ? _editorMetaInputText : FontSafeText(labelFont, value);
                if (previewMedia)
                {
                    DrawEditorMetaButton(GetEditorPreviewRowEditButtonBounds(i, virtualWidth, virtualHeight),
                        "EDIT", new Color(28, 28, 30, 250), Active);
                    DrawEditorMetaButton(GetEditorPreviewRowPlayButtonBounds(i, virtualWidth, virtualHeight),
                        _editorPreviewPlayer?.IsPlaying == true ? "STOP" : "PLAY",
                        new Color(28, 28, 30, 250), new Color(52, 211, 153));
                }
                else if (elementSelect)
                    DrawEditorMetaSelectField(bounds, element + "  ▼", GetEditorElementColor(element));
                else if (difficultyToggle)
                    DrawEditorMetaSelectField(bounds, value, accent);
                else if (deleteDifficulty)
                    DrawEditorMetaButton(bounds, "DELETE DIFFICULTY", new Color(55, 30, 35, 245),
                        HasEditorDifficultyChart(_editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart?.Difficulty, _sourceName))
                            ? interfaceUi.DangerColor : new Color(100, 100, 102));
                else
                    DrawEditorMetaTextField(bounds, display, editing, editable, accent,
                        editing ? _editorMetaInputText : null, _editorMetaSelectionAnchor, _editorMetaInputCaretIndex,
                        reserveMetaScrollbarSpace: true);
            }

            var totalContentHeight = GetEditorMetaPrefixHeight(rowLayouts, rowLayouts.Count);
            var maxOffset = GetEditorMetaMaxScrollOffset(rows, virtualWidth, virtualHeight);
            if (totalContentHeight > content.Height && maxOffset > 0 && content.Height > 0)
            {
                var unitSize = Math.Max(2, (int)MathF.Round(3f * unit));
                var rail = new Rectangle(content.Right - unitSize, content.Y, unitSize, content.Height);
                _spriteBatch.Draw(_pixel, rail, new Color(54, 54, 56, 220));
                var thumbHeight = Math.Max((int)MathF.Round(24f * unit),
                    (int)MathF.Round(content.Height * (content.Height / (float)totalContentHeight)));
                thumbHeight = Math.Min(content.Height, thumbHeight);
                var maxScrollY = Math.Max(1, GetEditorMetaPrefixHeight(rowLayouts, maxOffset));
                var currentScrollY = GetEditorMetaPrefixHeight(rowLayouts, firstRow);
                var travel = Math.Max(0, content.Height - thumbHeight);
                var thumbY = content.Y + (int)MathF.Round(travel * Math.Clamp(currentScrollY / (float)maxScrollY, 0f, 1f));
                _spriteBatch.Draw(_pixel, new Rectangle(rail.X, thumbY, rail.Width, thumbHeight), Active);
            }

            if (_editorElementDropdownOpen && string.Equals(_editorMetaCategory, "SONG", StringComparison.OrdinalIgnoreCase))
            {
                var elementIndex = rows.FindIndex(row => row.Label == "ELEMENT");
                if (elementIndex >= firstRow && GetEditorMetaFieldLabelBounds(elementIndex, virtualWidth, virtualHeight, rows, rowLayouts).Y < content.Bottom)
                {
                    var elementOptions = new[] { "DANCER", "VOCAL", "SESSION" };
                    for (var option = 0; option < elementOptions.Length; option++)
                    {
                        var optionBounds = GetEditorElementOptionBounds(elementIndex, option, virtualWidth, virtualHeight);
                        DrawEditorMetaSelectField(optionBounds, elementOptions[option], GetEditorElementColor(elementOptions[option]));
                    }
                }
            }

        }

        if (!string.Equals(_editorMetaCategory, "TEST", StringComparison.OrdinalIgnoreCase))
        {
            var activeCode = _editorActiveDifficultyCode ?? GetEditorDifficultyCode(_chart?.Difficulty, _sourceName);
            var activeDifficultyEnabled = HasEditorDifficultyChart(activeCode) && IsEditorDifficultyEnabled(activeCode);
            var difficultyToggleBounds = GetEditorMetaDifficultyToggleBounds(virtualWidth, virtualHeight);
            DrawEditorMetaButton(difficultyToggleBounds,
                $"{GetEditorDifficultyName(activeCode).ToUpperInvariant()} DIFFICULTY  {(activeDifficultyEnabled ? "ENABLED" : "DISABLED")}",
                activeDifficultyEnabled ? new Color(22, 52, 42, 250) : new Color(55, 30, 35, 245),
                activeDifficultyEnabled ? Active : interfaceUi.DangerColor);
            var exportToggle = GetEditorMetaExportToggleBounds(virtualWidth, virtualHeight);
            DrawEditorMetaButton(exportToggle, "EXPORT", new Color(28, 28, 30, 250), Active);
            if (_editorMetaDeleteConfirmOpen) DrawEditorMetaDeleteConfirmation(virtualWidth, virtualHeight);
            if (_editorPreviewEditorOpen)
                DrawEditorPreviewEditorOverlay(virtualWidth, virtualHeight);
            if (_editorMetaExportExpanded)
                DrawEditorMetaExportPopup(virtualWidth, virtualHeight);
        }
    }

    private void DrawEditorMetaDeleteConfirmation(int virtualWidth, int virtualHeight)
    {
        var unit = _projection.ScalePixels(1f);
        _spriteBatch.Draw(_pixel, new Rectangle(0, 0, virtualWidth, virtualHeight), new Color(0, 0, 0, 190));
        var width = Math.Min((int)MathF.Round(460f * unit), virtualWidth - 24);
        var height = Math.Max(1, (int)MathF.Round(190f * unit));
        var panel = new Rectangle((virtualWidth - width) / 2, (virtualHeight - height) / 2, width, height);
        _spriteBatch.Draw(_pixel, panel, _chartEditorInterfaceLayout.PanelColor);
        var font = GetChartEditorFont(_chartEditorInterfaceLayout.HeaderFont);
        var title = FontSafeText(font, "DELETE DIFFICULTY?");
        UnicodeTextRenderer.DrawString(_spriteBatch, font, title,
            new Vector2(panel.Center.X, panel.Y + 24f * unit), _chartEditorInterfaceLayout.ButtonTextColor,
            0f, new Vector2(font.MeasureString(title).X / 2f, 0f), unit, SpriteEffects.None, 0f);
        var bodyFont = GetChartEditorFont(_chartEditorInterfaceLayout.LabelFont);
        var body = FontSafeText(bodyFont, "This marks the chart for deletion. Its file will be removed when you save.");
        var bodyFit = FitChartEditorText(body, _chartEditorInterfaceLayout.LabelFont, 16f, panel.Width - 28f * unit, 48f * unit);
        UnicodeTextRenderer.DrawString(_spriteBatch, bodyFit.Font, bodyFit.Text,
            new Vector2(panel.X + 14f * unit, panel.Y + 64f * unit), _chartEditorInterfaceLayout.MutedTextColor,
            0f, Vector2.Zero, bodyFit.Scale, SpriteEffects.None, 0f);
        var cancel = new Rectangle(panel.X + (int)MathF.Round(12f * unit), panel.Bottom - (int)MathF.Round(48f * unit),
            (panel.Width - (int)MathF.Round(36f * unit)) / 2, (int)MathF.Round(34f * unit));
        var confirm = new Rectangle(cancel.Right + (int)MathF.Round(12f * unit), cancel.Y, cancel.Width, cancel.Height);
        DrawEditorMetaButton(cancel, "CANCEL", new Color(28, 28, 30, 250), Active);
        DrawEditorMetaButton(confirm, "DELETE", new Color(55, 30, 35, 245), _chartEditorInterfaceLayout.DangerColor);
    }

    private string? GetEditorMetaFieldKey(string label) => label switch
    {
        "SONG" => "name",
        "ARTIST" => "artist",
        "MAPPER" => "mapper",
        "DESCRIPTION" => "description",
        "VERSION" => "version",
        "SC PASSCODE" => "sc_passcode",
        "SERIES" when _editorBundleTemplate != ChartBundleTemplate.Tsts => "series",
        "LEVEL" => "level",
        "BPM" => "metadata_bpm",
        "PERFECT+ MS" when _editorCustomJudgementWindowsEnabled => "judgment_perfect_plus_ms",
        "PERFECT MS" when _editorCustomJudgementWindowsEnabled => "judgment_perfect_ms",
        "GOOD MS" when _editorCustomJudgementWindowsEnabled => "judgment_good_ms",
        "INITIAL CHART BPM" => "initial_chart_bpm",
        "START GEAR" => "start_gear",
        "CHART DURATION" => "play_time",
        "AUDIO ORIGIN TICK" => "audio_origin_tick",
        _ => null
    };

    private void DrawEditorMetaSelectField(Rectangle bounds, string rawValue, Color accent)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;
        var unit = _projection.ScalePixels(1f);
        var interfaceUi = _chartEditorInterfaceLayout;
        var edge = Math.Max(1, (int)MathF.Round(1.5f * unit));
        _spriteBatch.Draw(_pixel, bounds, interfaceUi.PanelColor);
        _spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Y, bounds.Width, edge), accent);
        _spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Bottom - edge, bounds.Width, edge), interfaceUi.HeaderColor);
        var maxWidth = Math.Max(1f, bounds.Width - 20f * unit);
        var fitted = FitChartEditorText(rawValue, interfaceUi.LabelFont, 24f, maxWidth, bounds.Height - 4f * unit);
        var size = fitted.Measured * fitted.Scale;
        UnicodeTextRenderer.DrawString(_spriteBatch, fitted.Font, fitted.Text,
            new Vector2(bounds.X + 10f * unit, bounds.Y + (bounds.Height - size.Y) * 0.5f),
            accent, 0f, Vector2.Zero, fitted.Scale, SpriteEffects.None, 0f);
    }

    private void DrawEditorMetaTextField(Rectangle bounds, string rawValue, bool editing, bool editable, Color accent,
        string? selectionText = null, int selectionAnchor = 0, int caretIndex = 0,
        bool reserveMetaScrollbarSpace = false)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var unit = _projection.ScalePixels(1f);
        var labelFont = GetChartEditorFont(_chartEditorInterfaceLayout.LabelFont);
        var edge = Math.Max(1, (int)MathF.Round(1.5f * unit));
        var interfaceUi = _chartEditorInterfaceLayout;
        var border = interfaceUi.HeaderColor;
        var focusAccent = editing ? new Color(255, 160, 122) : accent;
        _spriteBatch.Draw(_pixel, bounds, interfaceUi.PanelColor);
        _spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Y, bounds.Width, edge), focusAccent);
        _spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Bottom - edge, bounds.Width, edge), border);
        _spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Y, edge, bounds.Height), focusAccent);
        _spriteBatch.Draw(_pixel, new Rectangle(bounds.Right - edge, bounds.Y, edge, bounds.Height), border);

        var textLeft = bounds.X + (int)MathF.Round(10f * unit);
        // Reserve the same right-side space as the Lua row layout so the wrap
        // measurement and the actual draw width always produce the same line count.
        // Standalone MEDIA fields keep their full width because they have no list rail.
        var textWidth = Math.Max(1f, bounds.Width - (reserveMetaScrollbarSpace ? 42f : 20f) * unit);
        var textScale = Math.Min(1f, Math.Max(0.55f,
            (bounds.Height - 8f * unit) / Math.Max(1f, UnicodeTextRenderer.MeasureString(labelFont, "Ag").Y)));
        labelFont = GetChartEditorFont(interfaceUi.LabelFont, 24f * textScale);
        textScale = Math.Min(1f, (bounds.Height - 8f * unit) /
            Math.Max(1f, UnicodeTextRenderer.MeasureString(labelFont, "Ag").Y));
        var value = FontSafeText(labelFont, rawValue);
        var textColor = editing ? interfaceUi.ButtonTextColor : editable
            ? interfaceUi.ButtonTextColor
            : interfaceUi.MutedTextColor;
        if (editing)
        {
            var wrapWidth = textWidth / Math.Max(0.01f, textScale);
            var wrappedLines = WrapEditorTextWithRanges(value, wrapWidth,
                candidate => UnicodeTextRenderer.MeasureString(labelFont, candidate).X);
            var editingLineHeight = Math.Max(16f * unit, 24f * 1.2f * unit);
            var editingBlockHeight = wrappedLines.Count * editingLineHeight;
            var editingLineY = bounds.Y + (bounds.Height - editingBlockHeight) * 0.5f;
            var selectionStart = Math.Min(Math.Clamp(selectionAnchor, 0, value.Length),
                Math.Clamp(caretIndex, 0, value.Length));
            var selectionEnd = Math.Max(Math.Clamp(selectionAnchor, 0, value.Length),
                Math.Clamp(caretIndex, 0, value.Length));
            var caret = Math.Clamp(caretIndex, 0, value.Length);
            var caretLineIndex = -1;
            var caretLocalIndex = 0;
            var fontHeight = UnicodeTextRenderer.MeasureString(labelFont, "Ag").Y * textScale;
            var selectionHeight = Math.Max(1f, Math.Min(editingLineHeight, fontHeight));

            for (var lineIndex = 0; lineIndex < wrappedLines.Count; lineIndex++)
            {
                var line = wrappedLines[lineIndex];
                var currentY = editingLineY + lineIndex * editingLineHeight;
                var visibleSelectionStart = Math.Max(selectionStart, line.Start);
                var visibleSelectionEnd = Math.Min(selectionEnd, line.EndExclusive);
                if (visibleSelectionEnd > visibleSelectionStart)
                {
                    var prefix = line.Text[..Math.Clamp(visibleSelectionStart - line.Start, 0, line.Text.Length)];
                    var selected = line.Text.Substring(
                        Math.Clamp(visibleSelectionStart - line.Start, 0, line.Text.Length),
                        Math.Clamp(visibleSelectionEnd - visibleSelectionStart, 0,
                            line.Text.Length - Math.Clamp(visibleSelectionStart - line.Start, 0, line.Text.Length)));
                    var x1 = textLeft + UnicodeTextRenderer.MeasureString(labelFont, prefix).X * textScale;
                    var width = UnicodeTextRenderer.MeasureString(labelFont, selected).X * textScale;
                    var left = Math.Max(bounds.Left + 2f * unit, x1);
                    var right = Math.Min(bounds.Right - 2f * unit, Math.Min(x1 + width, textLeft + textWidth));
                    if (right > left)
                    {
                        var selectionY = currentY + (editingLineHeight - selectionHeight) * 0.5f;
                        _spriteBatch.Draw(_pixel, new Rectangle((int)MathF.Floor(left), (int)MathF.Floor(selectionY),
                            Math.Max(1, (int)MathF.Ceiling(right - left)), Math.Max(1, (int)MathF.Ceiling(selectionHeight))),
                            new Color(88, 128, 194, 155));
                    }
                }

                UnicodeTextRenderer.DrawString(_spriteBatch, labelFont, line.Text,
                    new Vector2(textLeft, currentY), textColor, 0f, Vector2.Zero, textScale, SpriteEffects.None, 0f);

                // A caret inside a whitespace run skipped at a visual wrap belongs
                // at the end of that preceding line; the position after the run is
                // owned by the next line.
                if (caret >= line.Start && caret < line.NextStart)
                {
                    caretLineIndex = lineIndex;
                    caretLocalIndex = Math.Clamp(caret, line.Start, line.EndExclusive) - line.Start;
                }
            }

            if (caretLineIndex < 0)
            {
                caretLineIndex = wrappedLines.Count - 1;
                caretLocalIndex = Math.Clamp(caret - wrappedLines[caretLineIndex].Start, 0,
                    wrappedLines[caretLineIndex].Text.Length);
            }
            var caretLine = wrappedLines[caretLineIndex];
            var caretPrefix = caretLine.Text[..Math.Clamp(caretLocalIndex, 0, caretLine.Text.Length)];
            var caretPosition = new Vector2(textLeft, editingLineY + caretLineIndex * editingLineHeight +
                (editingLineHeight - fontHeight) * 0.5f);
            DrawEditorTextCaret(caretPrefix, caretPosition, textScale, unit, labelFont);
            return;
        }

        // Unfocused fields use real multiline wrapping instead of fitting the full
        // value into one line. The row layout grows to fit these lines, preserving
        // the normal 24px text size and keeping adjacent rows in the right position.
        var lines = WrapEditorHudText(labelFont, value, textWidth / Math.Max(0.01f, textScale));
        var lineHeight = Math.Max(16f * unit, 24f * 1.2f * unit);
        var blockHeight = lines.Count * lineHeight;
        var lineY = bounds.Y + (bounds.Height - blockHeight) * 0.5f;
        foreach (var line in lines)
        {
            UnicodeTextRenderer.DrawString(_spriteBatch, labelFont, line,
                new Vector2(textLeft, lineY), textColor, 0f, Vector2.Zero, textScale, SpriteEffects.None, 0f);
            lineY += lineHeight;
        }
    }

    private void DrawEditorMetaExportPopup(int virtualWidth, int virtualHeight)
    {
        var count = GetEditorMetaExportOptionCount();
        if (count <= 0) return;
        var unit = _projection.ScalePixels(1f);
        var first = GetEditorMetaExportOptionBounds(0, virtualWidth, virtualHeight);
        var last = GetEditorMetaExportOptionBounds(count - 1, virtualWidth, virtualHeight);
        if (first.IsEmpty || last.IsEmpty) return;
        var padding = Math.Max(1, (int)MathF.Round(18f * unit));
        var headerHeight = Math.Max(1, (int)MathF.Round(94f * unit));
        var headerContentGap = (int)MathF.Round(24f * unit);
        var bottomPadding = (int)MathF.Round(18f * unit);
        var top = first.Y - headerHeight - headerContentGap;
        var modal = new Rectangle(first.X - padding, top,
            first.Width + padding * 2, last.Bottom - top + bottomPadding);
        modal.X = Math.Max(0, modal.X);
        modal.Y = Math.Max(0, modal.Y);
        modal.Width = Math.Min(virtualWidth - modal.X, modal.Width);
        modal.Height = Math.Min(virtualHeight - modal.Y, modal.Height);

        _spriteBatch.Draw(_pixel, new Rectangle(0, 0, virtualWidth, virtualHeight), new Color(0, 0, 0, 190));
        _spriteBatch.Draw(_pixel, modal, new Color(20, 20, 22, 252));
        var accentHeight = Math.Max(1, (int)MathF.Round(4f * unit));
        _spriteBatch.Draw(_pixel, new Rectangle(modal.X, modal.Y, modal.Width, accentHeight), Active);
        var titleBounds = new Rectangle(modal.X + padding, modal.Y + (int)MathF.Round(8f * unit),
            Math.Max(1, modal.Width - padding * 2), Math.Max(1, (int)MathF.Round(38f * unit)));
        DrawEditorMetaButton(titleBounds, "EXPORT CHART", new Color(20, 20, 22, 252), Active);

        var interfaceUi = _chartEditorInterfaceLayout;
        var descFont = GetChartEditorFont(interfaceUi.LabelFont, 16f * unit);
        const string subtitle = "Choose a format for this chart or song bundle.";
        UnicodeTextRenderer.DrawString(_spriteBatch, descFont, FontSafeText(descFont, subtitle),
            new Vector2(modal.X + padding, modal.Y + 56f * unit), interfaceUi.MutedTextColor,
            0f, Vector2.Zero, 1f, SpriteEffects.None, 0f);
        var descriptionLineHeight = Math.Max(1f, UnicodeTextRenderer.MeasureString(descFont, "Ag").Y) * 1.22f;
        var titleFont = GetChartEditorFont(interfaceUi.ButtonFont, 20f * unit);
        var titleLineHeight = Math.Max(1f, UnicodeTextRenderer.MeasureString(titleFont, "Ag").Y);
        var titleTop = 8f * unit;
        var descriptionGap = 4f * unit;
        for (var i = 0; i < count; i++)
        {
            var bounds = GetEditorMetaExportOptionBounds(i, virtualWidth, virtualHeight);
            var (label, description, accent, _) = GetEditorMetaExportOption(i);
            _spriteBatch.Draw(_pixel, bounds, new Color(28, 28, 30, 250));
            var accentWidth = Math.Max(1, (int)MathF.Round((float)interfaceUi.AccentWidth * unit));
            _spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Y, accentWidth, bounds.Height), accent);
            var labelText = FontSafeText(titleFont, interfaceUi.Label("export:" + label, label));
            var labelMeasured = UnicodeTextRenderer.MeasureString(titleFont, labelText);
            var labelScale = Math.Min(1f, Math.Max(0.1f, 22f * unit / Math.Max(1f, labelMeasured.Y)));
            var labelPosition = new Vector2(bounds.X + 16f * unit, bounds.Y + titleTop);
            UnicodeTextRenderer.DrawString(_spriteBatch, titleFont, labelText, labelPosition,
                interfaceUi.ButtonTextColor, 0f, Vector2.Zero, labelScale, SpriteEffects.None, 0f);

            var lines = WrapEditorTextToWidth(descFont, description, Math.Max(1f, bounds.Width - 32f * unit));
            var descriptionY = bounds.Y + titleTop + titleLineHeight * labelScale + descriptionGap;
            for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                UnicodeTextRenderer.DrawString(_spriteBatch, descFont, FontSafeText(descFont, lines[lineIndex]),
                    new Vector2(bounds.X + 16f * unit, descriptionY + lineIndex * descriptionLineHeight),
                    interfaceUi.MutedTextColor, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0f);
            }
        }
    }

    private void DrawEditorMetaButton(Rectangle bounds, string rawLabel, Color fill, Color accent)
    {
        var interfaceUi = _chartEditorInterfaceLayout;
        fill = ResolveEditorButtonStateFill(bounds, fill, interfaceUi);
        accent = ResolveEditorAccent(accent, interfaceUi);
        var unit = _projection.ScalePixels(1f);
        _spriteBatch.Draw(_pixel, bounds, fill);
        var accentWidth = Math.Max(1, (int)MathF.Round((float)interfaceUi.AccentWidth * unit));
        _spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Y, accentWidth, bounds.Height), accent);
        var availableWidth = Math.Max(1f, bounds.Width - 16f * unit);
        var fitted = FitChartEditorText(
            interfaceUi.Label("button:" + rawLabel, rawLabel), interfaceUi.ButtonFont,
            24f * (float)interfaceUi.ButtonTextScale, availableWidth, bounds.Height - 8f * unit);
        var size = fitted.Measured * fitted.Scale;
        var position = new Vector2(bounds.X + (bounds.Width - size.X) * 0.5f,
            bounds.Y + (bounds.Height - size.Y) * 0.5f);
        UnicodeTextRenderer.DrawString(_spriteBatch, fitted.Font, fitted.Text, position, interfaceUi.ButtonTextColor,
            0f, Vector2.Zero, fitted.Scale, SpriteEffects.None, 0f);
    }

    private static string FormatEditorTimestamp(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0.0, seconds));
        var totalSeconds = Math.Max(0, (int)Math.Round(time.TotalSeconds, MidpointRounding.AwayFromZero));
        var hours = totalSeconds / 3600;
        var minutes = (totalSeconds / 60) % 60;
        var remainingSeconds = totalSeconds % 60;
        return hours > 0
            ? $"{hours:00}:{minutes:00}:{remainingSeconds:00}"
            : $"{totalSeconds / 60:00}:{remainingSeconds:00}";
    }

    private void DrawSkinNineSlice(SkinDrawCommand command, float fill)
    {
        var asset = command.Asset;
        var slice = command.NineSlice;
        if (asset is null || slice is null)
            return;
        var sliceValue = slice.Value;

        var sourceWidth = asset.Width;
        var sourceHeight = asset.Height;
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return;

        var totalWidth = sourceWidth * command.Scale.X;
        var totalHeight = sourceHeight * command.Scale.Y;
        if (totalWidth <= 0.01f || totalHeight <= 0.01f)
            return;

        var clampedFill = Math.Clamp(fill, 0f, 1f);
        var visibleWidth = totalWidth * clampedFill;
        if (visibleWidth <= 0.01f)
            return;

        var sourceLeft = Math.Clamp(sliceValue.Left, 0f, sourceWidth);
        var sourceRight = Math.Clamp(sliceValue.Right, 0f, sourceWidth - sourceLeft);
        var sourceTop = Math.Clamp(sliceValue.Top, 0f, sourceHeight);
        var sourceBottom = Math.Clamp(sliceValue.Bottom, 0f, sourceHeight - sourceTop);

        if (sourceLeft + sourceRight > sourceWidth)
        {
            var sum = Math.Max(0.0001f, sourceLeft + sourceRight);
            var factor = sourceWidth / sum;
            sourceLeft *= factor;
            sourceRight *= factor;
        }
        if (sourceTop + sourceBottom > sourceHeight)
        {
            var sum = Math.Max(0.0001f, sourceTop + sourceBottom);
            var factor = sourceHeight / sum;
            sourceTop *= factor;
            sourceBottom *= factor;
        }

        // Border thickness follows vertical scale, so changing only width keeps
        // the 9-slice corners/edges visually fixed.
        var borderScale = command.Scale.Y;
        var destLeftFull = sourceLeft * borderScale;
        var destRightFull = sourceRight * borderScale;
        var destTop = sourceTop * borderScale;
        var destBottom = sourceBottom * borderScale;

        if (destTop + destBottom > totalHeight)
        {
            var sum = Math.Max(0.0001f, destTop + destBottom);
            var factor = totalHeight / sum;
            destTop *= factor;
            destBottom *= factor;
        }

        // IMPORTANT: fill is a horizontal reveal, not a resize of the left cap.
        // The old implementation compressed the left/right caps whenever the
        // filled width was smaller than their combined width. That made the
        // beginning of the gauge look stretched. Instead:
        //   * the left cap stays at its normal size once it is fully visible;
        //   * the center stretches as the fill grows;
        //   * the right cap is only revealed progressively when the fill reaches it.
        // This is effectively a left-to-right clip of a 9-sliced bar.
        var leftVisible = Math.Min(visibleWidth, destLeftFull);
        var centerFullWidth = Math.Max(0f, totalWidth - destLeftFull - destRightFull);
        var centerVisible = Math.Min(
            Math.Max(0f, visibleWidth - destLeftFull),
            centerFullWidth);
        var rightVisible = Math.Max(
            0f,
            visibleWidth - destLeftFull - centerFullWidth);
        rightVisible = Math.Min(rightVisible, destRightFull);

        var outerTopLeft = command.Position - new Vector2(
            totalWidth * command.Anchor.X,
            totalHeight * command.Anchor.Y);

        // Horizontal segments are described in destination/source units. The
        // first segment is a cropped portion of the left cap until it reaches
        // its full width; the middle segment then grows; finally the right cap
        // is revealed from left to right without ever being stretched.
        var segments = new List<(float dstX, float dstWidth, float srcX, float srcWidth)>(3);

        if (leftVisible > 0.01f)
        {
            var srcWidth = Math.Min(sourceLeft, leftVisible / Math.Max(0.0001f, borderScale));
            segments.Add((0f, leftVisible, 0f, srcWidth));
        }

        if (centerVisible > 0.01f && centerFullWidth > 0.01f)
        {
            var sourceCenterLeft = sourceLeft;
            var sourceCenterWidth = Math.Max(1f, sourceWidth - sourceLeft - sourceRight);
            segments.Add((leftVisible, centerVisible, sourceCenterLeft, sourceCenterWidth));
        }

        if (rightVisible > 0.01f && sourceRight > 0.01f)
        {
            var sourceRightLeft = sourceWidth - sourceRight;
            var srcWidth = Math.Min(sourceRight, rightVisible / Math.Max(0.0001f, borderScale));
            segments.Add((leftVisible + centerVisible, rightVisible, sourceRightLeft, srcWidth));
        }

        var srcYEdges = new[]
        {
            0,
            (int)MathF.Round(sourceTop),
            (int)MathF.Round(sourceHeight - sourceBottom),
            sourceHeight
        };

        for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
        {
            var segment = segments[segmentIndex];

            for (var y = 0; y < 3; y++)
            {
                var sourceY = srcYEdges[y];
                var sourceHeightPixels = Math.Max(1, srcYEdges[y + 1] - srcYEdges[y]);
                var destinationY = outerTopLeft.Y + destY(y, destTop, destBottom, totalHeight);
                var destinationHeight = destHeight(y, destTop, destBottom, totalHeight);

                if (destinationHeight <= 0.01f || segment.dstWidth <= 0.01f)
                    continue;

                var sourceX = Math.Clamp((int)MathF.Round(segment.srcX), 0, sourceWidth - 1);
                var sourceWidthPixels = Math.Clamp(
                    (int)MathF.Round(segment.srcWidth),
                    1,
                    sourceWidth - sourceX);

                var visualSourceX = sourceX;
                var visualSourceY = sourceY;
                var visualSourceWidth = sourceWidthPixels;
                var visualSourceHeight = sourceHeightPixels;

                var effects = SpriteEffects.None;
                if (command.SpriteEffects.HasFlag(SpriteEffects.FlipHorizontally))
                {
                    visualSourceX = sourceWidth - sourceX - sourceWidthPixels;
                    effects |= SpriteEffects.FlipHorizontally;
                }
                if (command.SpriteEffects.HasFlag(SpriteEffects.FlipVertically))
                {
                    visualSourceY = sourceHeight - sourceY - sourceHeightPixels;
                    effects |= SpriteEffects.FlipVertically;
                }

                var sourceRect = new Rectangle(
                    visualSourceX,
                    visualSourceY,
                    visualSourceWidth,
                    visualSourceHeight);

                if (MathF.Abs(command.Rotation) < 0.000001f)
                {
                    // Round the segment edges, not the position and width
                    // independently. Independent rounding can turn two touching
                    // float rectangles into a 1-pixel gap at fractional scales.
                    var destinationLeft = (int)MathF.Round(
                        outerTopLeft.X + segment.dstX);
                    var destinationRight = (int)MathF.Round(
                        outerTopLeft.X + segment.dstX + segment.dstWidth);
                    var destinationTop = (int)MathF.Round(destinationY);
                    var destinationBottom = (int)MathF.Round(
                        destinationY + destinationHeight);

                    var destination = new Rectangle(
                        destinationLeft,
                        destinationTop,
                        Math.Max(1, destinationRight - destinationLeft),
                        Math.Max(1, destinationBottom - destinationTop));

                    _spriteBatch.Draw(
                        asset.Texture,
                        destination,
                        sourceRect,
                        command.Color * command.Alpha,
                        0f,
                        Vector2.Zero,
                        effects,
                        0f);
                }
                else
                {
                    var destinationWidth = Math.Max(0.01f, segment.dstWidth);
                    var destinationHeightFloat = Math.Max(0.01f, destinationHeight);
                    var scaleX = destinationWidth / sourceRect.Width;
                    var scaleY = destinationHeightFloat / sourceRect.Height;
                    var anchorOffsetX = command.Position.X - (outerTopLeft.X + segment.dstX);
                    var anchorOffsetY = command.Position.Y - destinationY;
                    var origin = new Vector2(
                        anchorOffsetX / Math.Max(0.000001f, scaleX),
                        anchorOffsetY / Math.Max(0.000001f, scaleY));

                    _spriteBatch.Draw(
                        asset.Texture,
                        command.Position,
                        sourceRect,
                        command.Color * command.Alpha,
                        command.Rotation,
                        origin,
                        new Vector2(scaleX, scaleY),
                        effects,
                        0f);
                }
            }
        }

        float destY(int row, float top, float bottom, float height)
        {
            return row switch
            {
                0 => 0f,
                1 => top,
                _ => height - bottom
            };
        }

        float destHeight(int row, float top, float bottom, float height)
        {
            return row switch
            {
                0 => top,
                1 => Math.Max(0f, height - top - bottom),
                _ => bottom
            };
        }
    }

    private void DrawSkinScriptUiLayers(IReadOnlyList<SkinDrawCommand> allCommands, bool belowLane, SkinRuntime runtime)
    {
        // BuildDrawCommands() has already sorted these by Lua layer/order. Keep that
        // exact ordering across both 2D SpriteBatch commands and real 3D quads.
        // SpriteBatch can only be active for 2D draws, so contiguous 2D commands
        // are batched and a 3D command flushes the current batch before it draws.
        // Consecutive 3D quads share renderer state to avoid repeating the same
        // graphics-device state changes for every background card/light/flash.
        var commands = _skinLayerScratch;
        commands.Clear();

        for (var i = 0; i < allCommands.Count; i++)
        {
            var command = allCommands[i];
            if (belowLane ? command.Layer < 0 : command.Layer >= 0)
                commands.Add(command);
        }

        if (commands.Count == 0)
            return;

        var batchOpen = false;
        var quad3DRunOpen = false;
        var batchAdditive = false;
        var batchSampler = SamplerState.LinearClamp;

        void BeginQuad3DRun()
        {
            if (quad3DRunOpen)
                return;

            runtime.BeginQuad3DRenderRun();
            quad3DRunOpen = true;
        }

        void EndQuad3DRun()
        {
            if (!quad3DRunOpen)
                return;

            runtime.EndQuad3DRenderRun();
            quad3DRunOpen = false;
        }

        void BeginBatch(SkinDrawCommand command)
        {
            var additive = command.AdditiveBlend;
            var sampler = command.Kind == SkinDrawCommandKind.Sprite && command.NineSlice is not null
                ? SamplerState.PointClamp
                : SamplerState.LinearClamp;

            if (batchOpen && batchAdditive == additive && ReferenceEquals(batchSampler, sampler))
                return;

            if (batchOpen)
                _spriteBatch.End();

            _spriteBatch.Begin(
                samplerState: sampler,
                blendState: additive ? BlendState.Additive : BlendState.NonPremultiplied);
            batchOpen = true;
            batchAdditive = additive;
            batchSampler = sampler;
        }

        void EndBatch()
        {
            if (!batchOpen)
                return;

            _spriteBatch.End();
            batchOpen = false;
        }

        try
        {
            for (var commandIndex = 0; commandIndex < commands.Count; commandIndex++)
            {
                var command = commands[commandIndex];

                if (command.Kind == SkinDrawCommandKind.Quad3D ||
                    command.Kind == SkinDrawCommandKind.Sparkles)
                {
                    EndBatch();
                    BeginQuad3DRun();
                    if (command.Kind == SkinDrawCommandKind.Quad3D)
                    {
                        var batchedCount = runtime.RenderCompatibleQuad3DBatch(
                            commands,
                            commandIndex,
                            _graphics.Viewport.Width,
                            _graphics.Viewport.Height);
                        if (batchedCount > 1)
                        {
                            commandIndex += batchedCount - 1;
                            continue;
                        }
                    }
                    runtime.RenderQuad3D(
                        command,
                        _graphics.Viewport.Width,
                        _graphics.Viewport.Height);
                    continue;
                }

                EndQuad3DRun();
                BeginBatch(command);

                switch (command.Kind)
                {
                    case SkinDrawCommandKind.Sprite:
                    {
                        var asset = command.Asset;
                        if (asset is null)
                            break;

                        var fill = Math.Clamp(command.Fill, 0f, 1f);
                        if (fill <= 0f)
                            break;

                        if (command.NineSlice is not null)
                        {
                            DrawSkinNineSlice(command, fill);
                            break;
                        }

                        Rectangle? source = null;
                        if (fill < 0.9999f)
                        {
                            source = new Rectangle(
                                0,
                                0,
                                Math.Max(1, (int)MathF.Round(asset.Width * fill)),
                                asset.Height);
                        }
                        else if (command.CropCover && command.Size.X > 0f && command.Size.Y > 0f)
                        {
                            source = SkinRuntime.CalculateCoverSourceRect(
                                asset.Width,
                                asset.Height,
                                Math.Max(1, (int)MathF.Round(command.Size.X)),
                                Math.Max(1, (int)MathF.Round(command.Size.Y)));
                        }
                        var effectiveScale = command.Scale;
                        var origin = new Vector2(
                            (source?.Width ?? asset.Width) * command.Anchor.X,
                            (source?.Height ?? asset.Height) * command.Anchor.Y);
                        _spriteBatch.Draw(
                            asset.Texture,
                            command.Position,
                            source,
                            command.Color * command.Alpha,
                            command.Rotation,
                            origin,
                            effectiveScale,
                            command.SpriteEffects,
                            0f);
                        break;
                    }

                    case SkinDrawCommandKind.Text:
                    {
                        var font = command.Font?.Font ?? _font;
                        var text = FontSafeText(font, command.Text ?? string.Empty);
                        var measured = UnicodeTextRenderer.MeasureString(font, text);
                        if (measured.X <= 0f || measured.Y <= 0f)
                            break;

                        var scale = command.Scale;
                        if (command.TextSize > 0f)
                        {
                            var uniform = command.TextSize / measured.Y;
                            scale *= uniform;
                        }

                        var origin = new Vector2(
                            measured.X * command.Anchor.X,
                            measured.Y * command.Anchor.Y);

                        void drawText(
                            SpriteFont drawFont,
                            Vector2 position,
                            XnaColor tint,
                            Vector2 drawScale,
                            Vector2 drawOrigin)
                        {
                            UnicodeTextRenderer.DrawString(_spriteBatch, 
                                drawFont,
                                text,
                                position,
                                tint,
                                command.Rotation,
                                drawOrigin,
                                drawScale,
                                SpriteEffects.None,
                                0f);
                        }

                        if (command.Shadow is not null)
                        {
                            var shadow = command.Shadow.Value;
                            var shadowFont = shadow.RasterFont?.Font ?? font;
                            var shadowScale = scale;
                            var shadowOrigin = origin;

                            if (shadow.BlurRadius > 0f && command.Font is not null)
                            {
                                // Important: gameplay, pause, and result screens use this
                                // renderer (not SkinRuntime.RenderDrawCommands). Build the
                                // blur from this command's exact font, preserving its glyph
                                // bounds and baseline with no independent scale/offset.
                                shadowFont = runtime.LoadMatchedTextShadowFont(
                                    command.Font, shadow.BlurRadius, shadow.Expand) ?? shadowFont;
                            }
                            else if (shadow.RasterFont is not null)
                            {
                                var shadowMeasured = UnicodeTextRenderer.MeasureString(shadowFont, text);
                                if (shadowMeasured.X > 0f && shadowMeasured.Y > 0f)
                                {
                                    shadowScale = new Vector2(
                                        scale.X * measured.X / shadowMeasured.X,
                                        scale.Y * measured.Y / shadowMeasured.Y);
                                    shadowOrigin = new Vector2(
                                        shadowMeasured.X * command.Anchor.X,
                                        shadowMeasured.Y * command.Anchor.Y);
                                }
                            }

                            drawText(
                                shadowFont,
                                command.Position + shadow.Offset,
                                shadow.Color * (command.Alpha * shadow.Alpha),
                                shadowScale,
                                shadowOrigin);
                        }

                        if (command.Stroke is { } stroke)
                        {
                            var radius = Math.Max(1f, stroke.Width);
                            var steps = Math.Max(8, (int)Math.Ceiling(radius * 8f));
                            for (var i = 0; i < steps; i++)
                            {
                                var angle = MathF.Tau * i / steps;
                                var offset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
                                drawText(
                                    font,
                                    command.Position + offset,
                                    stroke.Color * (command.Alpha * stroke.Alpha),
                                    scale,
                                    origin);
                            }
                        }

                        drawText(font, command.Position, command.Color * command.Alpha, scale, origin);
                        break;
                    }

                    case SkinDrawCommandKind.Rect:
                    {
                        var topLeft = command.Position - new Vector2(
                            command.Size.X * command.Anchor.X,
                            command.Size.Y * command.Anchor.Y);
                        var rect = new Rectangle(
                            (int)MathF.Round(topLeft.X),
                            (int)MathF.Round(topLeft.Y),
                            Math.Max(1, (int)MathF.Round(command.Size.X)),
                            Math.Max(1, (int)MathF.Round(command.Size.Y)));
                        _spriteBatch.Draw(_pixel, rect, command.Color * command.Alpha);
                        break;
                    }

                    case SkinDrawCommandKind.Shadow:
                    {
                        if (command.BoxShadow is not null)
                            SkinBoxShadowRenderer.Draw(_spriteBatch, _pixel, command);
                        break;
                    }

                    case SkinDrawCommandKind.Line:
                    {
                        var delta = command.EndPosition - command.Position;
                        var length = delta.Length();
                        if (length <= 0.001f)
                            break;

                        var center = (command.Position + command.EndPosition) * 0.5f;
                        var rotation = MathF.Atan2(delta.Y, delta.X);
                        _spriteBatch.Draw(
                            _pixel,
                            center,
                            null,
                            command.Color * command.Alpha,
                            rotation,
                            new Vector2(0.5f, 0.5f),
                            new Vector2(length, Math.Max(1f, command.LineWidth)),
                            SpriteEffects.None,
                            0f);
                        break;
                    }

                    case SkinDrawCommandKind.LineBatch:
                    {
                        var segments = command.LineSegments;
                        var segmentCount = Math.Min(command.LineSegmentCount, segments?.Length ?? 0);
                        for (var i = 0; i < segmentCount; i++)
                        {
                            var segment = segments![i];
                            var delta = segment.End - segment.Start;
                            var length = delta.Length();
                            if (length <= 0.001f)
                                continue;

                            var center = (segment.Start + segment.End) * 0.5f;
                            var rotation = MathF.Atan2(delta.Y, delta.X);
                            _spriteBatch.Draw(
                                _pixel,
                                center,
                                null,
                                segment.Color * segment.Alpha,
                                rotation,
                                new Vector2(0.5f, 0.5f),
                                new Vector2(length, Math.Max(1f, segment.Width)),
                                SpriteEffects.None,
                                0f);
                        }
                        break;
                    }
                }
            }
        }
        finally
        {
            EndQuad3DRun();
            EndBatch();
        }
    }

    private void DrawTimingFeedbackUi()
    {
        if (!_hasTimingOffset)
            return;

        var absOffsetMs = Math.Abs(_lastTimingOffsetMs);
        var color = TimingDirectionColor(_lastTimingOffsetMs);
        var direction = TimingDirectionText(_lastTimingOffsetMs);

        // Keep these thresholds in gameConfig so they can be changed globally
        // without touching the skin layout/configuration.
        if (!string.IsNullOrEmpty(direction) &&
            absOffsetMs > Math.Max(0f, _skin.GameConfig.EarlyLateMinimumOffsetMs))
        {
            DrawTextUiElement(direction, _skin.UIConfig.EarlyLate, color,
                _earlyLateUiTimer, _earlyLatePulseTimer);
        }

        if (absOffsetMs > Math.Max(0f, _skin.GameConfig.TimingOffsetMinimumOffsetMs))
        {
            DrawTextUiElement(FormatTimingOffset(_lastTimingOffsetMs), _skin.UIConfig.TimingOffset, color,
                _timingOffsetUiTimer, _timingOffsetPulseTimer);
        }
    }

    private void DrawHitDeltaBar()
    {
        var config = _skin.UIConfig.HitDeltaBar;
        if (_hitDeltaSamples.Count == 0)
            return;

        var viewportWidth = _graphics.Viewport.Width;
        var viewportHeight = _graphics.Viewport.Height;
        var pixelScale = GetUiPixelScale(config);
        var position = ResolveUiPositionForElement(config, viewportWidth, viewportHeight, pixelScale);
        var anchor = ParseAnchor(config.Anchor);
        var width = Math.Abs(ResolveUiAxis(config.Size, 0, viewportWidth, pixelScale, null));
        var height = Math.Abs(ResolveUiAxis(config.Size, 1, viewportHeight, pixelScale, null));
        width = Math.Max(1f, width);
        height = Math.Max(1f, height);
        var topLeft = position - new Vector2(anchor.X * width, anchor.Y * height);
        var halfWidth = width * 0.5f;
        var centerX = topLeft.X + halfWidth;
        var centerY = topLeft.Y + height * 0.5f;
        var lineWidth = Math.Max(1f, SkinUIExpression.Resolve(config.LineWidth, width, pixelScale, null, 2f));
        var zeroLineWidth = Math.Max(1f, SkinUIExpression.Resolve(config.ZeroLineWidth, width, pixelScale, null, 1f));
        var range = Math.Max(1e-3f, Math.Abs(config.RangeMs));

        // Center line marks exact timing: left = early, right = late.
        DrawLine(_spriteBatch, new Vector2(centerX, topLeft.Y),
            new Vector2(centerX, topLeft.Y + height), zeroLineWidth, new Color(145, 145, 147, 150));

        foreach (var sample in _hitDeltaSamples)
        {
            var normalized = Math.Clamp((float)(sample.OffsetMs / range), -1f, 1f);
            var x = centerX + normalized * halfWidth;
            var color = sample.Tier switch
            {
                JudgementTier.PerfectPlus => Color.White,
                JudgementTier.Perfect => new Color(255, 165, 70),
                JudgementTier.Good => new Color(145, 220, 255),
                _ => Color.Red
            };
            var y0 = topLeft.Y + height * 0.08f;
            var y1 = topLeft.Y + height * 0.92f;
            var alpha = GetUiLineOpacity(config, sample.RemainingLifetime);
            if (alpha > 0f)
                DrawLine(_spriteBatch, new Vector2(x, y0), new Vector2(x, y1), lineWidth, color * alpha);
        }
    }

    private static float GetUiLineOpacity(SkinUIElementConfig config, double remainingLifetime)
    {
        if (double.IsPositiveInfinity(remainingLifetime))
            return 1f;

        var duration = SkinUIExpression.ResolveSeconds(config.DisappearDuration, null, 0f);
        if (duration <= 0f)
            return remainingLifetime > 0 ? 1f : 0f;

        var after = SkinUIExpression.ResolveSeconds(config.DisappearAfter, null, 0f);
        var total = after + duration;
        var elapsed = total - remainingLifetime;
        if (elapsed <= after)
            return 1f;
        return Math.Clamp(1f - (float)((elapsed - after) / duration), 0f, 1f);
    }

    private void DrawComboSprite()
    {
        if (_combo <= 0)
            return;

        var text = _combo.ToString(CultureInfo.InvariantCulture);
        var textures = new List<Texture2D>(text.Length);
        foreach (var ch in text)
        {
            var texture = _textures.GetComboDigit(ch - '0');
            if (texture is null)
                return;
            textures.Add(texture);
        }

        var config = _skin.UIConfig.Combo;
        var viewportWidth = _graphics.Viewport.Width;
        var viewportHeight = _graphics.Viewport.Height;
        var pixelsPerReferencePixel = GetUiPixelScale(config);
        var pulseScale = UiPulseMultiplier(config.PulseSize, config.PulseFadeOutDuration, _comboPulseTimer,
            token => ResolveComboReferenceForPulse(token));
        var alpha = UiDisappearOpacity(config, _comboUiTimer, _comboPulseTimer);
        var sizeX = ResolveUiAxis(config.Size, 0, viewportWidth, pixelsPerReferencePixel,
            token => ResolveUiReference(token, "combo", 0, textures));
        var sizeY = ResolveUiAxis(config.Size, 1, viewportHeight, pixelsPerReferencePixel,
            token => ResolveUiReference(token, "combo", 1, textures));
        var gapX = ResolveUiAxis(config.Gap, 0, viewportWidth, pixelsPerReferencePixel,
            token => ResolveUiReference(token, "combo", 0, textures));
        var gapY = ResolveUiAxis(config.Gap, 1, viewportHeight, pixelsPerReferencePixel,
            token => ResolveUiReference(token, "combo", 1, textures));
        var basePosition = ResolveUiPosition(config, viewportWidth, viewportHeight, pixelsPerReferencePixel, textures);
        var anchor = ParseAnchor(config.Anchor);
        var pulseAnchor = ParseAnchor(config.PulseAnchor);

        var digitSizes = new Vector2[textures.Count];
        for (var i = 0; i < textures.Count; i++)
        {
            var aspect = textures[i].Width / (float)Math.Max(1, textures[i].Height);
            digitSizes[i] = ResolveAutoSize(sizeX, sizeY, aspect);
        }

        var placements = new Vector2[textures.Count];
        var min = Vector2.Zero;
        var max = Vector2.Zero;
        var x = 0f;
        for (var i = 0; i < textures.Count; i++)
        {
            var y = i * gapY;
            placements[i] = new Vector2(x, y);
            min.X = Math.Min(min.X, x);
            min.Y = Math.Min(min.Y, y);
            max.X = Math.Max(max.X, x + digitSizes[i].X);
            max.Y = Math.Max(max.Y, y + digitSizes[i].Y);
            x += digitSizes[i].X + gapX;
        }

        var groupSize = max - min;
        if (groupSize.X <= 0f || groupSize.Y <= 0f)
            return;

        // Position uses the normal sprite anchor; pulseAnchor controls the point
        // around which the whole combo scales during its pulse.
        var topLeft = basePosition - new Vector2(anchor.X * groupSize.X, anchor.Y * groupSize.Y);
        var pulsePivot = topLeft + pulseAnchor * groupSize;
        for (var i = 0; i < textures.Count; i++)
        {
            var local = placements[i] - min;
            var size = digitSizes[i];
            var unscaledCenter = topLeft + local + size * 0.5f;
            var center = pulsePivot + (unscaledCenter - pulsePivot) * pulseScale;
            var drawSize = size * pulseScale;
            var scale = new Vector2(drawSize.X / Math.Max(1, textures[i].Width), drawSize.Y / Math.Max(1, textures[i].Height));
            var origin = new Vector2(textures[i].Width * 0.5f, textures[i].Height * 0.5f);
            // Draw left-to-right so the rightmost digit is on top of the previous
            // digit whenever their images overlap.
            _spriteBatch.Draw(textures[i], center, null, Color.White * alpha, 0f, origin, scale, SpriteEffects.None, 0f);
        }
    }

    private void DrawJudgementSprite()
    {
        var key = _lastJudgement switch
        {
            "PERFECT+" => "judgement-perfect-plus",
            "PERFECT" => "judgement-perfect",
            "GOOD" => "judgement-good",
            "MISS" => "judgement-miss",
            _ => string.Empty
        };
        if (string.IsNullOrEmpty(key))
            return;

        var texture = _textures.Get(key);
        if (texture is null)
            return;

        var config = _skin.UIConfig.Judgement;
        var viewportWidth = _graphics.Viewport.Width;
        var viewportHeight = _graphics.Viewport.Height;
        var pixelsPerReferencePixel = GetUiPixelScale(config);
        var position = ResolveUiPosition(config, viewportWidth, viewportHeight, pixelsPerReferencePixel, null);
        var anchor = ParseAnchor(config.Anchor);
        var pulseAnchor = ParseAnchor(config.PulseAnchor);
        var aspect = texture.Width / (float)Math.Max(1, texture.Height);
        var sizeX = ResolveUiAxis(config.Size, 0, viewportWidth, pixelsPerReferencePixel,
            token => ResolveUiReference(token, "judgement", 0, null));
        var sizeY = ResolveUiAxis(config.Size, 1, viewportHeight, pixelsPerReferencePixel,
            token => ResolveUiReference(token, "judgement", 1, null));
        var size = ResolveAutoSize(sizeX, sizeY, aspect);
        var pulseScale = UiPulseMultiplier(config.PulseSize, config.PulseFadeOutDuration, _judgementPulseTimer,
            token => ResolveUiReference(token, "judgement", 0, null));
        var alpha = UiDisappearOpacity(config, _judgementUiTimer, _judgementPulseTimer);
        var baseTopLeft = position - new Vector2(anchor.X * size.X, anchor.Y * size.Y);
        var pulsePivot = baseTopLeft + pulseAnchor * size;
        var center = pulsePivot + (baseTopLeft + size * 0.5f - pulsePivot) * pulseScale;
        size *= pulseScale;
        var origin = new Vector2(texture.Width * 0.5f, texture.Height * 0.5f);
        var drawScale = new Vector2(size.X / Math.Max(1, texture.Width), size.Y / Math.Max(1, texture.Height));
        _spriteBatch.Draw(texture, center, null, Color.White * alpha, 0f, origin, drawScale, SpriteEffects.None, 0f);
    }

    private Vector2 ResolveUiPosition(
        SkinUIElementConfig config,
        int viewportWidth,
        int viewportHeight,
        float pixelsPerReferencePixel,
        List<Texture2D>? comboTextures)
    {
        var element = ReferenceEquals(config, _skin.UIConfig.Combo) ? "combo" : "judgement";
        var x = ResolveUiAxis(config.Position, 0, viewportWidth, pixelsPerReferencePixel,
            token => ResolveUiReference(token, element, 0, comboTextures));
        var yFromBottom = ResolveUiAxis(config.Position, 1, viewportHeight, pixelsPerReferencePixel,
            token => ResolveUiReference(token, element, 1, comboTextures));
        return new Vector2(x, viewportHeight - yFromBottom);
    }

    private float GetUiPixelScale(SkinUIElementConfig config)
    {
        if (!config.RefScale)
            return 1f;

        // Reference-width mode treats 1920px as the maximum UI width.
        // Wider screens stay at 1x; narrower screens scale down in proportion
        // to their width. Percentage values are unaffected because the
        // percentage base is still the actual viewport dimension.
        return Math.Clamp(
            _graphics.Viewport.Width / ChartProjection.ReferenceResolutionWidth,
            0f,
            1f);
    }

    private float ResolveUiAxis(
        string[]? values,
        int axis,
        float percentageBase,
        float pixelsPerReferencePixel,
        Func<string, float?>? referenceResolver)
    {
        var expression = values is { Length: > 0 } ? values[Math.Clamp(axis, 0, values.Length - 1)] : "0";
        return SkinUIExpression.Resolve(expression, percentageBase, pixelsPerReferencePixel, referenceResolver, 0f);
    }

    private static Vector2 ResolveAutoSize(float width, float height, float aspect)
    {
        aspect = Math.Max(0.0001f, aspect);
        if (Math.Abs(width) <= 0.0001f && Math.Abs(height) <= 0.0001f)
            return new Vector2(0f, 0f);
        if (Math.Abs(width) <= 0.0001f)
            width = height * aspect;
        else if (Math.Abs(height) <= 0.0001f)
            height = width / aspect;
        return new Vector2(Math.Abs(width), Math.Abs(height));
    }

    private static Vector2 ParseAnchor(string[]? anchor)
    {
        var x = anchor is { Length: > 0 } ? anchor[0] : "center";
        var y = anchor is { Length: > 1 } ? anchor[1] : "center";
        return new Vector2(ParseHorizontalAnchor(x), ParseVerticalAnchor(y));
    }

    private static float ParseHorizontalAnchor(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "left" => 0f,
            "right" => 1f,
            _ => 0.5f
        };

    private static float ParseVerticalAnchor(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "top" => 0f,
            "bottom" => 1f,
            _ => 0.5f
        };

    private float? ResolveUiReference(string token, string currentElement, int axis, List<Texture2D>? comboTextures)
    {
        token = token.Trim();
        var lower = token.ToLowerInvariant();
        var dot = lower.IndexOf('.');
        var element = dot >= 0 ? lower[..dot] : lower;
        var remainder = dot >= 0 ? lower[(dot + 1)..] : "position";
        if (element is not ("combo" or "judgement") || element == currentElement)
            return null;

        var config = element == "combo"
            ? (SkinUIElementConfig)_skin.UIConfig.Combo
            : _skin.UIConfig.Judgement;

        // Named sprite anchors (combo.top, judgement.right, etc.) are resolved
        // from the sprite's actual on-screen bounds. X accepts left/center/right;
        // Y accepts top/center/bottom. A bare position reference keeps the old
        // axis-specific behavior.
        if (remainder is "left" or "right" or "center" or "top" or "bottom")
        {
            var requestedAxis = axis;
            if (remainder is "left" or "right") requestedAxis = 0;
            else if (remainder is "top" or "bottom") requestedAxis = 1;

            if (remainder == "center")
            {
                // center is valid for either axis, so use the axis of the
                // expression in which it appears.
            }
            return ResolveSpriteAnchorReference(element, requestedAxis, remainder, comboTextures);
        }

        var explicitAxis = -1;
        if (remainder.EndsWith(".x", StringComparison.Ordinal))
        {
            explicitAxis = 0;
            remainder = remainder[..^2];
        }
        else if (remainder.EndsWith(".y", StringComparison.Ordinal))
        {
            explicitAxis = 1;
            remainder = remainder[..^2];
        }
        var referenceAxis = explicitAxis >= 0 ? explicitAxis : axis;
        var viewportWidth = _graphics.Viewport.Width;
        var viewportHeight = _graphics.Viewport.Height;
        var pixelScale = GetUiPixelScale(config);

        return remainder switch
        {
            "position" => ResolveUiAxis(
                config.Position,
                referenceAxis,
                referenceAxis == 0 ? viewportWidth : viewportHeight,
                pixelScale,
                name => ResolveUiReference(name, element, referenceAxis, comboTextures)),
            "size" => ResolveReferenceSize(config, element, referenceAxis, comboTextures),
            "gap" when element == "combo" => ResolveUiAxis(
                _skin.UIConfig.Combo.Gap,
                referenceAxis,
                referenceAxis == 0 ? viewportWidth : viewportHeight,
                pixelScale,
                name => ResolveUiReference(name, element, referenceAxis, comboTextures)),
            "pulsesize" => SkinUIExpression.ResolveMultiplier(config.PulseSize, null, 1f),
            "pulsefadeoutduration" => SkinUIExpression.ResolveSeconds(config.PulseFadeOutDuration, null, 0f),
            "disappearafter" => SkinUIExpression.ResolveSeconds(config.DisappearAfter, name => ResolveUiReference(name, element, referenceAxis, comboTextures), 0f),
            "disappearduration" => SkinUIExpression.ResolveSeconds(config.DisappearDuration, name => ResolveUiReference(name, element, referenceAxis, comboTextures), 0f),
            _ => null
        };
    }

    private float? ResolveSpriteAnchorReference(string element, int axis, string anchorName, List<Texture2D>? comboTextures)
    {
        var config = element == "combo"
            ? (SkinUIElementConfig)_skin.UIConfig.Combo
            : _skin.UIConfig.Judgement;
        var width = _graphics.Viewport.Width;
        var height = _graphics.Viewport.Height;
        var pixelScale = GetUiPixelScale(config);
        var position = ResolveUiPosition(config, width, height, pixelScale,
            element == "combo" ? (comboTextures ?? GetCurrentComboTextures()) : null);
        var size = ResolveReferenceSize(config, element, 0, element == "combo" ? (comboTextures ?? GetCurrentComboTextures()) : null) ?? 0f;
        var sizeY = ResolveReferenceSize(config, element, 1, element == "combo" ? (comboTextures ?? GetCurrentComboTextures()) : null) ?? 0f;
        var anchor = ParseAnchor(config.Anchor);

        // ResolveUiPosition returns screen-space Y. Convert named anchors back
        // to the bottom-origin coordinate system used by UI expressions.
        if (axis == 0)
        {
            return anchorName switch
            {
                "left" => position.X - anchor.X * size,
                "right" => position.X + (1f - anchor.X) * size,
                _ => position.X + (0.5f - anchor.X) * size
            };
        }

        var topScreen = position.Y - anchor.Y * sizeY;
        var bottomScreen = position.Y + (1f - anchor.Y) * sizeY;
        return anchorName switch
        {
            "top" => height - topScreen,
            "bottom" => height - bottomScreen,
            _ => height - ((topScreen + bottomScreen) * 0.5f)
        };
    }

    private float? ResolveReferenceSize(SkinUIElementConfig config, string element, int axis, List<Texture2D>? comboTextures)
    {
        var viewportWidth = _graphics.Viewport.Width;
        var viewportHeight = _graphics.Viewport.Height;
        var pixelScale = GetUiPixelScale(config);
        var rawX = ResolveUiAxis(config.Size, 0, viewportWidth, pixelScale,
            name => ResolveUiReference(name, element, 0, comboTextures));
        var rawY = ResolveUiAxis(config.Size, 1, viewportHeight, pixelScale,
            name => ResolveUiReference(name, element, 1, comboTextures));

        if (element == "combo" && comboTextures is { Count: > 0 })
        {
            var maxWidth = 0f;
            var maxHeight = 0f;
            foreach (var texture in comboTextures)
            {
                var size = ResolveAutoSize(rawX, rawY,
                    texture.Width / (float)Math.Max(1, texture.Height));
                maxWidth = Math.Max(maxWidth, size.X);
                maxHeight = Math.Max(maxHeight, size.Y);
            }
            return axis == 0 ? maxWidth : maxHeight;
        }

        if (element == "judgement")
        {
            var texture = GetCurrentJudgementTexture();
            if (texture is not null)
            {
                var size = ResolveAutoSize(rawX, rawY,
                    texture.Width / (float)Math.Max(1, texture.Height));
                return axis == 0 ? size.X : size.Y;
            }
        }

        return axis == 0 ? rawX : rawY;
    }

    private Texture2D? GetCurrentJudgementTexture()
    {
        var key = _lastJudgement switch
        {
            "PERFECT+" => "judgement-perfect-plus",
            "PERFECT" => "judgement-perfect",
            "GOOD" => "judgement-good",
            "MISS" => "judgement-miss",
            _ => string.Empty
        };
        return string.IsNullOrEmpty(key) ? null : _textures.Get(key);
    }

    private float UiPulseMultiplier(string expression, string durationExpression, double remainingSeconds, Func<string, float?>? referenceResolver)
    {
        var pulseSize = SkinUIExpression.ResolveMultiplier(expression, referenceResolver, 1f);
        var duration = SkinUIExpression.ResolveSeconds(durationExpression, null, 0f);
        if (pulseSize <= 1f || duration <= 0f || remainingSeconds <= 0)
            return 1f;
        var t = Math.Clamp((float)(remainingSeconds / duration), 0f, 1f);
        var eased = t * t * (3f - 2f * t);
        return 1f + (pulseSize - 1f) * eased;
    }

    private float? ResolveComboReferenceForPulse(string token)
        => ResolveUiReference(token, "judgement", 0, GetCurrentComboTextures());

    private List<Texture2D> GetCurrentComboTextures()
    {
        var textures = new List<Texture2D>();
        if (_combo <= 0)
            return textures;
        foreach (var ch in _combo.ToString(CultureInfo.InvariantCulture))
        {
            var texture = _textures.GetComboDigit(ch - '0');
            if (texture is null)
                return new List<Texture2D>();
            textures.Add(texture);
        }
        return textures;
    }

    private void TriggerComboPulse()
    {
        var config = _skin.UIConfig.Combo;
        _comboPulseTimer = SkinUIExpression.ResolveSeconds(config.PulseFadeOutDuration, null, 0f);
        _comboUiTimer = GetUiLifetime(config);
    }

    private void StartJudgementUiLifetime()
    {
        var config = _skin.UIConfig.Judgement;
        _judgementPulseTimer = SkinUIExpression.ResolveSeconds(config.PulseFadeOutDuration, null, 0f);
        _judgementUiTimer = GetUiLifetime(config);
    }

    private static double AdvanceUiTimer(double remaining, double dt)
    {
        if (double.IsPositiveInfinity(remaining))
            return remaining;
        return Math.Max(0, remaining - dt);
    }

    private static float GetFeedbackUiLifetime(SkinUIElementConfig config)
    {
        var pulse = SkinUIExpression.ResolveSeconds(config.PulseFadeOutDuration, null, 0f);
        var after = SkinUIExpression.ResolveSeconds(config.DisappearAfter, null, 0f);
        var duration = SkinUIExpression.ResolveSeconds(config.DisappearDuration, null, 0f);
        if (pulse <= 0f && after <= 0f && duration <= 0f)
            return float.PositiveInfinity;
        return pulse + after + duration;
    }

    private static double GetHitDeltaLifetime(SkinUIHitDeltaBarConfig config)
    {
        var after = SkinUIExpression.ResolveSeconds(config.DisappearAfter, null, 0f);
        var duration = SkinUIExpression.ResolveSeconds(config.DisappearDuration, null, 0f);
        if (after <= 0f && duration <= 0f)
            return double.PositiveInfinity;
        return after + duration;
    }

    private static float GetUiLifetime(SkinUIElementConfig config)
    {
        var pulse = SkinUIExpression.ResolveSeconds(config.PulseFadeOutDuration, null, 0f);
        var after = SkinUIExpression.ResolveSeconds(config.DisappearAfter, null, 0f);
        var duration = SkinUIExpression.ResolveSeconds(config.DisappearDuration, null, 0f);
        return Math.Max(0f, pulse + after + duration);
    }

    private static float UiDisappearOpacity(SkinUIElementConfig config, double remainingLifetime, double remainingPulse)
    {
        var after = SkinUIExpression.ResolveSeconds(config.DisappearAfter, null, 0f);
        var duration = SkinUIExpression.ResolveSeconds(config.DisappearDuration, null, 0f);
        if (duration <= 0f)
            return 1f;

        if (double.IsPositiveInfinity(remainingLifetime))
            return 1f;

        var pulse = SkinUIExpression.ResolveSeconds(config.PulseFadeOutDuration, null, 0f);
        var elapsed = GetUiLifetime(config) - remainingLifetime;
        var disappearElapsed = (float)elapsed - pulse - after;
        if (disappearElapsed <= 0f)
            return 1f;

        return Math.Clamp(1f - disappearElapsed / duration, 0f, 1f);
    }

    private static string FitHudText(SpriteFont font, string? text, float maxWidth)
    {
        var safe = FontSafeText(font, text);
        if (safe.Length == 0 || maxWidth <= 1f)
            return string.Empty;
        if (UnicodeTextRenderer.MeasureString(font, safe).X <= maxWidth)
            return safe;

        const string suffix = "...";
        var length = safe.Length;
        while (length > 0)
        {
            var candidate = safe[..length].TrimEnd() + suffix;
            if (UnicodeTextRenderer.MeasureString(font, candidate).X <= maxWidth)
                return candidate;
            length--;
        }
        return string.Empty;
    }

    private void DrawOverlay(string text)
    {
        text = FontSafeText(_font, text);
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);
        var size = UnicodeTextRenderer.MeasureString(_font, text);
        UnicodeTextRenderer.DrawString(_spriteBatch, _font, text, new Vector2((_graphics.Viewport.Width - size.X) * 0.5f, _graphics.Viewport.Height * 0.45f), Text);
        _spriteBatch.End();
    }

    private static string FontSafeText(SpriteFont font, string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        try
        {
            UnicodeTextRenderer.MeasureString(font, text);
            return text;
        }
        catch (ArgumentException)
        {
            var replacement = '?';
            foreach (var candidate in new[] { '?', '-', '_', ' ' })
            {
                try
                {
                    UnicodeTextRenderer.MeasureString(font, candidate.ToString());
                    replacement = candidate;
                    break;
                }
                catch (ArgumentException)
                {
                }
            }

            var buffer = new System.Text.StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (c == '\r' || c == '\n' || char.IsWhiteSpace(c))
                {
                    buffer.Append(c);
                    continue;
                }

                try
                {
                    UnicodeTextRenderer.MeasureString(font, c.ToString());
                    buffer.Append(c);
                }
                catch (ArgumentException)
                {
                    buffer.Append(replacement);
                }
            }

            return buffer.ToString();
        }
    }
}
