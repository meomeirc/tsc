using TSC;
using TSC.Ares;
using TSC.Input;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System.Collections.Generic;
using Color = Microsoft.Xna.Framework.Color;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using System.Globalization;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Reflection;

namespace TSC.Rendering;

public sealed partial class Playfield
{
    // Input handling and keyboard/gameplay control integration.

    private bool _editorWheelScrollPauseActive;
    private bool _editorWheelResumeAfterScroll;
    private double _editorWheelScrollIdleRemaining;
    private bool _editorSeekPauseActive;
    private bool _editorSeekResumePlayback;
    private const double EditorWheelScrollPauseIdleSeconds = 0.22;

    private void HandleInput(KeyboardState keyboard, GameTime gameTime)
    {
        var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        _editorTextInputDeltaSeconds = Math.Max(0.0, gameTime.ElapsedGameTime.TotalSeconds);

        // Skin reload is a global recovery shortcut. Handle it before modal and
        // text-field early returns so F10 remains available throughout the editor.
        if (Pressed(keyboard, Keys.F10))
            RefreshSkinUiConfig();
        // F4 is only a legacy shortcut outside the chart editor. In the editor,
        // gameplay/autoplay is controlled by editor_toggle_gameplay_mode instead.
        if (!_lockMenuPlayMode && !_chartEditorMode && Pressed(keyboard, Keys.F4))
            ToggleGameplayMode();

        // The in-game music-preview modal exclusively owns keyboard and pointer
        // input until it closes. Handle it before editor scroll timers or global
        // shortcuts so nothing can resume or modify chart playback behind it.
        if (_chartEditorMode && _editorPreviewEditorOpen)
        {
            UpdateEditorSeekPauseState();
            HandleEditorPreviewEditorInput(keyboard, _previousKeyboard);
            return;
        }

        UpdateEditorWheelScrollPause(dt);
        UpdateEditorSeekPauseState();

        // Preview mode does not run the regular chart-editor command handler,
        // so dispatch its three playback actions from the configured keybinds.
        // Restart takes priority if the user assigns the same chord to multiple
        // actions, matching the normal editor command order.
        if (_chartEditorMode && _editorPreviewMode)
        {
            // Preview mode keeps the same configured timeline controls as the
            // ordinary editor, but disables chart-editing commands. Toggle the
            // mode first and return so the same key press cannot immediately
            // re-enter preview through the normal editor command handler.
            if (KeybindSettings.IsPressed("editor_toggle_preview_mode"))
            {
                SetEditorPreviewMode(false);
                UpdateLaneHighlights(keyboard, dt);
                return;
            }
            if (KeybindSettings.IsPressed("editor_restart_preview"))
            {
                _playSeconds = 0;
                ResetGameplayState();
            }
            else if (KeybindSettings.IsPressed("editor_seek_backward_1_beat"))
                SeekEditorByBeats(-1.0);
            else if (KeybindSettings.IsPressed("editor_seek_forward_1_beat"))
                SeekEditorByBeats(1.0);
            else if (KeybindSettings.IsPressed("editor_seek_backward_5_beats"))
                SeekEditorByBeats(-5.0);
            else if (KeybindSettings.IsPressed("editor_seek_forward_5_beats"))
                SeekEditorByBeats(5.0);
            else if (KeybindSettings.IsPressed("editor_toggle_play_preview") &&
                     !_editorTimelineScrubbing && !_editorWheelScrollPauseActive)
            {
                _playing = !_playing;
            }
            else if (KeybindSettings.IsPressed("editor_toggle_gameplay_mode") && !_lockMenuPlayMode)
            {
                ToggleGameplayMode();
            }
        }

        // Main-menu Play is a locked gameplay session. Most editor/debug
        // controls remain disabled here, but F8 is temporarily allowed so the
        // memory diagnostics can be inspected during a normal play session.
        if (_lockMenuPlayMode)
        {
#if !TSC_PUBLISH
            if (Pressed(keyboard, Keys.F12))
                OpenGameplayResult(manualTrigger: true);
            if (Pressed(keyboard, Keys.F5))
            {
                BeginClearAnimationDebugProbe();
                _skinRuntime.OnLastPlayableTick(GetClearType());
            }
#endif
            UpdateLaneHighlights(keyboard, dt);
            return;
        }

        if (_chartEditorMode && !_editorPreviewMode)
            HandleChartEditorInput(keyboard, _previousKeyboard);

        // While an inline Meta input has focus, swallow all editor/gameplay keybinds.
        // Mouse handling still runs so an outside click can confirm the edit.
        if (_chartEditorMode && !_editorPreviewMode && _editorMetaEditingField is not null)
        {
            HandleMouseSelection();
            UpdateLaneHighlights(keyboard, dt);
            return;
        }

        // While the BPM field is being edited, numeric shortcuts and editor hotkeys
        // must not leak through to gameplay (for example, B toggling the grid).
        // Mouse selection still runs so clicking a lane can place a tempo change.
        if (_chartEditorMode && !_editorPreviewMode && _editorBpmInputFocused)
        {
            HandleMouseSelection();
            UpdateLaneHighlights(keyboard, dt);
            return;
        }

        // Numeric generator/duration fields also own Left/Right while editing.
        // Mouse selection still runs so an outside click confirms or discards the
        // draft, but do not let those same arrows seek the chart on this frame.
        if (_chartEditorMode && !_editorPreviewMode &&
            (_editorDurationInputFocused || _editorStutterInputField != EditorStutterInputField.None))
        {
            HandleMouseSelection();
            UpdateLaneHighlights(keyboard, dt);
            return;
        }

        if (_chartEditorMode && !_editorPreviewMode &&
            (keyboard.IsKeyDown(Keys.LeftControl) || keyboard.IsKeyDown(Keys.RightControl)))
        {
            HandleMouseSelection();
            UpdateLaneHighlights(keyboard, dt);
            return;
        }

        var shift = keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift);
        // Outside the chart editor, retain the legacy Space shortcut. Editor
        // preview playback is handled by editor_toggle_play_preview above.
        if (!_chartEditorMode && Pressed(keyboard, Keys.Space) && !_editorTimelineScrubbing && !_editorWheelScrollPauseActive)
            _playing = !_playing;
        // These shortcuts are retained for ordinary gameplay sessions. During
        // chart editing the same actions are reached only through keybinds.
        HandleMouseSelection();
        HandleEditorMouseWheelScroll();

        if ((!_chartEditorMode || _editorPreviewMode) && !shift)
        {
            // R remains a legacy restart shortcut for ordinary gameplay only.
            // Editor preview uses editor_restart_preview from the user's keybinds.
            if (!_chartEditorMode && Pressed(keyboard, Keys.R)) { _playSeconds = 0; ResetGameplayState(); }
            if (!_lockMenuPlayMode && Pressed(keyboard, Keys.C) &&
                !keyboard.IsKeyDown(Keys.LeftControl) && !keyboard.IsKeyDown(Keys.RightControl))
                ToggleCompactMode();
            if (Pressed(keyboard, Keys.B)) _beatLines = !_beatLines;
        }

        UpdateLaneHighlights(keyboard, dt);
    }

    private bool IsEditorSeekKeyDown()
        => KeybindSettings.IsDown("editor_seek_backward_1_beat") ||
           KeybindSettings.IsDown("editor_seek_forward_1_beat") ||
           KeybindSettings.IsDown("editor_seek_backward_5_beats") ||
           KeybindSettings.IsDown("editor_seek_forward_5_beats");

    private void UpdateEditorSeekPauseState()
    {
        if (!_chartEditorMode)
        {
            _editorSeekPauseActive = false;
            _editorSeekResumePlayback = false;
            return;
        }

        // Text fields and the preview modal own their input; their keys must not
        // pause playback as if they were seek gestures.
        var textFieldOwnsInput = _editorPreviewEditorOpen || _editorMetaEditingField is not null ||
                                 _editorDurationInputFocused || _editorStutterInputField != EditorStutterInputField.None ||
                                 _editorBpmInputFocused;
        var physicalSeekHeld = IsEditorSeekKeyDown();
        // Once a seek gesture has paused playback, keep it paused until the key is
        // physically released even if focus changes while the key is still held.
        var held = physicalSeekHeld && (!textFieldOwnsInput || _editorSeekPauseActive);
        if (held)
        {
            if (!_editorSeekPauseActive)
            {
                _editorSeekPauseActive = true;
                _editorSeekResumePlayback = _playing;
            }
            if (_playing)
            {
                _playing = false;
                _audioResyncPending = true;
            }
            return;
        }

        if (!_editorSeekPauseActive)
            return;

        _editorSeekPauseActive = false;
        var shouldResume = _editorSeekResumePlayback;
        _editorSeekResumePlayback = false;
        if (shouldResume && _chartEditorMode && !_playing &&
            !_editorTimelineScrubbing && !_editorWheelScrollPauseActive)
        {
            _playing = true;
            _audioResyncPending = true;
            _statusMessage = "Preview resumed after seeking.";
        }
    }

    private void UpdateEditorWheelScrollPause(double deltaSeconds)
    {
        if (!_editorWheelScrollPauseActive)
            return;

        _editorWheelScrollIdleRemaining = Math.Max(0.0, _editorWheelScrollIdleRemaining - Math.Max(0.0, deltaSeconds));
        if (_editorWheelScrollIdleRemaining > 0.0 || _editorTimelineScrubbing)
            return;

        var shouldResume = _editorWheelResumeAfterScroll;
        _editorWheelScrollPauseActive = false;
        _editorWheelResumeAfterScroll = false;
        _editorWheelScrollIdleRemaining = 0.0;

        if (shouldResume && _chartEditorMode && !_playing)
        {
            _playing = true;
            _audioResyncPending = true;
            _statusMessage = "Preview resumed after scrolling.";
        }
    }

    private void BeginEditorWheelScrollPause()
    {
        if (!_editorWheelScrollPauseActive)
            _editorWheelResumeAfterScroll = _playing;

        _editorWheelScrollPauseActive = true;
        _editorWheelScrollIdleRemaining = EditorWheelScrollPauseIdleSeconds;
        if (_playing)
        {
            _playing = false;
            _audioResyncPending = true;
        }
    }

    private void HandleEditorMouseWheelScroll()
    {
        var wheelDelta = GlobalInput.Mouse.ScrollWheelValue - GlobalInput.PreviousMouse.ScrollWheelValue;
        if (wheelDelta == 0)
            return;

        // The chart Problems panel owns the wheel while the pointer is over it,
        // so scrolling the diagnostic list never seeks the chart underneath.
        if (_chartEditorMode && _chart is not null)
        {
            var viewport = _graphics.Viewport;
            var (virtualWidth, virtualHeight) = GetVirtualResolution(viewport.Width, viewport.Height);
            var pointer = new Microsoft.Xna.Framework.Point(
                (int)MathF.Round(GlobalInput.Mouse.X * virtualWidth / (float)Math.Max(1, viewport.Width)),
                (int)MathF.Round(GlobalInput.Mouse.Y * virtualHeight / (float)Math.Max(1, viewport.Height)));
            if (_chartEditorInterfaceRuntime.IsLoaded && _chartEditorInterfaceRuntime.LastError is null)
            {
                var uiOverMetaContent = _editorMetaPanelOpen &&
                    GetEditorMetaContentBounds(virtualWidth, virtualHeight).Contains(pointer) &&
                    _chartEditorInterfaceRuntime.IsUiPointOverControlWithIdPrefix(pointer.X, pointer.Y, "editor_meta_");
                var uiOverProblems = _editorProblemsExpanded &&
                    (_chartEditorInterfaceRuntime.IsUiPointOverControlWithIdPrefix(pointer.X, pointer.Y, "editor_problems_") ||
                     _chartEditorInterfaceRuntime.IsUiPointOverControlWithIdPrefix(pointer.X, pointer.Y, "editor_problem_"));
                var steps = Math.Max(1, (int)Math.Round(Math.Abs(wheelDelta) / 120.0));
                if (uiOverMetaContent)
                {
                    var rows = GetEditorMetaRows(_tempo?.SecondsToTick(_playSeconds) ?? 0.0);
                    _editorMetaScrollOffset = Math.Clamp(_editorMetaScrollOffset + (wheelDelta < 0 ? steps : -steps),
                        0, GetEditorMetaMaxScrollOffset(rows, virtualWidth, virtualHeight));
                    return;
                }
                if (uiOverProblems)
                {
                    RefreshEditorDiagnostics();
                    _editorProblemsScrollOffset = Math.Clamp(_editorProblemsScrollOffset + (wheelDelta < 0 ? steps : -steps),
                        0, Math.Max(0, _editorProblems.Count - 5));
                    return;
                }
            }
            if (GetEditorProblemsPanelBounds(virtualWidth, virtualHeight).Contains(pointer))
            {
                if (_editorProblemsExpanded && GetEditorProblemsListBounds(virtualWidth, virtualHeight).Contains(pointer))
                {
                    RefreshEditorDiagnostics();
                    var steps = Math.Max(1, (int)Math.Round(Math.Abs(wheelDelta) / 120.0));
                    _editorProblemsScrollOffset += wheelDelta < 0 ? steps : -steps;
                    ClampEditorProblemsScroll(virtualWidth, virtualHeight);
                }
                return;
            }
        }

        // The INFO panel owns wheel input while the pointer is over it. Only its
        // field list scrolls; difficulty/category tabs and export buttons stay put.
        if (_chartEditorMode && _editorMetaPanelOpen)
        {
            var viewport = _graphics.Viewport;
            var (virtualWidth, virtualHeight) = GetVirtualResolution(viewport.Width, viewport.Height);
            var pointer = new Microsoft.Xna.Framework.Point(
                (int)MathF.Round(GlobalInput.Mouse.X * virtualWidth / (float)Math.Max(1, viewport.Width)),
                (int)MathF.Round(GlobalInput.Mouse.Y * virtualHeight / (float)Math.Max(1, viewport.Height)));
            var panel = GetEditorMetaPanelBounds(virtualWidth, virtualHeight);
            if (panel.Contains(pointer))
            {
                var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
                if (content.Contains(pointer))
                {
                    var rows = GetEditorMetaRows(_tempo?.SecondsToTick(_playSeconds) ?? 0.0);
                    ClampEditorMetaScrollOffset(rows, virtualWidth, virtualHeight);
                    var steps = Math.Max(1, (int)Math.Round(Math.Abs(wheelDelta) / 120.0));
                    _editorMetaScrollOffset += wheelDelta < 0 ? steps : -steps;
                    ClampEditorMetaScrollOffset(rows, virtualWidth, virtualHeight);
                }
                return;
            }
        }

        if (!_chartEditorMode || _chart is null || _lockMenuPlayMode || _editorTimelineScrubbing ||
            _editorMetaEditingField is not null || _editorBpmInputFocused ||
            _editorDurationInputFocused || _editorStutterInputField != EditorStutterInputField.None)
            return;

        BeginEditorWheelScrollPause();

        // Reverse the earlier wheel direction and make each standard notch gentler.
        // Positive wheel delta (up) advances the chart; negative delta (down) moves
        // backward. Dividing by four retains smooth fractional touchpad deltas.
        var secondsDelta = wheelDelta / 480.0;
        _playSeconds = Math.Clamp(_playSeconds + secondsDelta, 0.0, ChartDuration());
    }

    private void ToggleCompactMode()
    {
        if (_chartEditorMode && !_topDownViewEnabled)
        {
            // 3D view is always collapsed. Do not overwrite the 2D preference.
            _compact = true;
            _projection.Compact = true;
            _statusMessage = "3D view is locked to collapsed view. Switch to 2D view to expand.";
            return;
        }
        if (_chartEditorMode && _editorPanelsViewEnabled)
        {
            _compact = true;
            _projection.Compact = true;
            _statusMessage = "Collapsed mode is locked while Panels view is active.";
            return;
        }
        _compact = !_compact;
        if (_chartEditorMode)
            _editorPreferredCompact = _compact;
        _projection.Compact = _compact;
        _projection.InvalidateGeometryCaches();
        InvalidateLaneMeshCache();
        _editorHoverCacheValid = false;

        // A selection made in a bank that is now invisible must not remain
        // deletable/draggable through stale editor selection state.
        if (_chartEditorMode && _chart is not null && _tempo is not null)
        {
            var editState = RenderGearState(_tempo.SecondsToTick(_playSeconds));
            _editorMultiSelectedEvents.RemoveWhere(ev => !ShouldRenderInCurrentBank(ev, editState));
            if (_selectedEventKey.HasValue)
            {
                var selected = _chart.Events.FirstOrDefault(ev => KeyOf(ev) == _selectedEventKey.Value);
                if (selected is not null && !ShouldRenderInCurrentBank(selected, editState))
                {
                    _selectedEventKey = null;
                    _editorSelectedLinkRoot = null;
                    _editorSelectedHoldEnd = false;
                }
            }
            _selectedBeatTick = null;
            _selectedBeatTrack = null;
            _statusMessage = _compact
                ? "Banks collapsed to the active route."
                : "All banks expanded for editing.";
        }
    }

    private int ComputePerspectiveSettingsSignature()
    {
        var hash = new HashCode();
        hash.Add(_compact);
        hash.Add(_projection.LaneRotationDegrees);
        hash.Add(_projection.LaneRotationDepthDegrees);
        hash.Add(_projection.LaneLength);
        hash.Add(_projection.FocalDistance);
        hash.Add(_projection.SuperFlickTransitionDurationSeconds);
        hash.Add(_projection.SuperFlickTransitionAngleDegrees);
        hash.Add(_projection.SuperFlickTransitionOvershoot);
        hash.Add(_projection.OrbitRotationPivotVerticalOffset);
        hash.Add(_projection.OrbitRotationRadius);
        hash.Add(_projection.SuperFlickScrollSpeed);
        hash.Add(_projection.SuperFlickScrollGap);
        hash.Add(_projection.SuperFlickHighlightWidth);
        hash.Add(_projection.SuperFlickHighlightSpeed);
        hash.Add(_projection.SuperFlickHighlightOpacity);
        hash.Add(_projection.TransitionFlashDelaySeconds);
        hash.Add(_projection.TransitionFlashFadeInSeconds);
        hash.Add(_projection.TransitionFlashFadeOutSeconds);
        hash.Add(_projection.TransitionFlashOpacity);
        return hash.ToHashCode();
    }

    private void ToggleSuperFlickDebug()
    {
        _superFlickDebugEnabled = !_superFlickDebugEnabled;
        if (_superFlickDebugEnabled)
        {
            try
            {
                _superFlickDebugPath = UserDataPaths.GetPath("Logs", "superFlick_debug.log");
                File.WriteAllText(_superFlickDebugPath, string.Empty, new System.Text.UTF8Encoding(false));
                _superFlickDebugRecent.Clear();
                _superFlickDebugFrame = 0;
                SuperFlickDebugLog("=== TSC SUPER FLICK DEBUG START ===");
                SuperFlickDebugLog($"source={_sourceName ?? "<none>"} chartSeconds={_playSeconds:0.000} tick={(_tempo is null ? double.NaN : _tempo.SecondsToTick(_playSeconds)):0.###}");
                SuperFlickDebugLog("F9 toggles this diagnostic mode. Successful hits, misses, timing, key transitions, active bank, and duplicate evaluations are logged.");
                _statusMessage = $"SUPER FLICK DEBUG ON: {_superFlickDebugPath}";
            }
            catch (Exception ex)
            {
                _superFlickDebugEnabled = false;
                _statusMessage = $"Super Flick debug failed: {ex.Message}";
            }
        }
        else
        {
            SuperFlickDebugLog("=== TSC SUPER FLICK DEBUG END ===");
            _statusMessage = "SUPER FLICK DEBUG OFF";
        }
    }

    private void ToggleAuxTrackDebug()
    {
        _showAuxTrackDebug = !_showAuxTrackDebug;
        _statusMessage = _showAuxTrackDebug
            ? "AUX TRACK DEBUG ON (F11 to hide)"
            : "AUX TRACK DEBUG OFF";
    }

    private void SuperFlickDebugLog(string message)
    {
        if (!_superFlickDebugEnabled)
            return;

        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        try
        {
            if (!string.IsNullOrWhiteSpace(_superFlickDebugPath))
                Program.AppendBoundedLog(_superFlickDebugPath, line);
        }
        catch
        {
            // Diagnostics must never affect gameplay.
        }

        _superFlickDebugRecent.Enqueue(line);
        while (_superFlickDebugRecent.Count > _superFlickDebugMaxRecent)
            _superFlickDebugRecent.Dequeue();
    }

    private void SuperFlickDebugLogInput(KeyboardState keyboard)
    {
        if (!_superFlickDebugEnabled)
            return;

        var tracked = new[] { Keys.S, Keys.D, Keys.F, Keys.J, Keys.K, Keys.L };
        var transitions = new List<string>();
        foreach (var key in tracked)
        {
            var now = keyboard.IsKeyDown(key);
            var before = _previousKeyboard.IsKeyDown(key);
            if (now != before)
                transitions.Add($"{key}:{(now ? "DOWN" : "UP")}");
        }

        if (transitions.Count > 0)
            SuperFlickDebugLog($"INPUT frame={_superFlickDebugFrame} transitions={string.Join(",", transitions)}");
    }

    private string SuperFlickDebugKeyState(Keys[] keys, KeyboardState keyboard)
    {
        if (keys.Length == 0)
            return "<none>";

        return string.Join("+", keys.Select(k =>
            $"{k}[down={(keyboard.IsKeyDown(k) ? 1 : 0)},pressed={(keyboard.IsKeyDown(k) && !_previousKeyboard.IsKeyDown(k) ? 1 : 0)}]"));
    }

    private string SuperFlickDebugGearState()
        => $"L={_gameplayState.LeftMode} R={_gameplayState.RightMode} G={_gameplayState.GlobalMode}";

    private void ToggleGameplayMode()
    {
        if (_chartEditorMode)
        {
            if (!_gameplayMode)
            {
                // Keep the editor preview's current route and Perfect+ score as the
                // starting point for manual play. Only discard in-flight judgement
                // state so a hold/link cannot be inherited halfway through.
                _gameplayMode = true;
                _gameplayState = EnsureEditorPreviewGearState().Copy();
                ResetGameplayTransientState(preserveEditorPreviewRoute: true);
                _gameplayState = EnsureEditorPreviewGearState().Copy();
                _autoSoundSeconds = _playSeconds;
            }
            else
            {
                // Returning to autoplay restores the score for an all-Perfect+
                // run through the current playhead, but keeps the route reached by
                // manual gameplay so the gear does not jump on mode changes.
                _gameplayMode = false;
                ResetGameplayTransientState(preserveEditorPreviewRoute: true);
                RebuildEditorAutoplayResultsToCurrentTime();
                _autoSoundSeconds = _playSeconds;
            }

            _statusMessage = _gameplayMode ? "GAMEPLAY ON" : "GAMEPLAY OFF";
            RefreshSkinGameState();
            return;
        }

        _gameplayMode = !_gameplayMode;
        ResetGameplayState();
        _statusMessage = _gameplayMode ? "GAMEPLAY ON" : "GAMEPLAY OFF";
    }

    private bool IsTrackKeyDown(int track) => KeybindSettings.IsTrackDown(track);

    private bool IsTrackKeyPressed(int track) => KeybindSettings.IsTrackPressed(track);

    private bool IsSuperFlickInputPressed(ChartEvent ev) => KeybindSettings.IsSuperFlickPressed(ev);

    private SuperFlickVisualKey SuperFlickKeyOf(ChartEvent ev)
    {
        var p = GearState.Position(ev.Track);
        var side = ev.GetSuperFlickSide();
        var routeBeforeEvent = _gear?.AtTick(ev.Tick, includeTickChanges: false);
        var family = ev.GetSuperFlickFamily(routeBeforeEvent);
        return p is null || side is null
            ? new SuperFlickVisualKey(ev.Tick, string.Empty, string.Empty, family, string.Empty, ev.Attr / 10)
            : new SuperFlickVisualKey(ev.Tick, side, p.Value.Bank, family, p.Value.Side, ev.Attr / 10);
    }

    private bool SameSuperFlickVisual(ChartEvent a, ChartEvent b)
    {
        if (!a.IsSuperFlick || !b.IsSuperFlick || a.Tick != b.Tick || a.Attr / 10 != b.Attr / 10)
            return false;
        var pa = GearState.Position(a.Track);
        var pb = GearState.Position(b.Track);
        var sideA = a.GetSuperFlickSide();
        var sideB = b.GetSuperFlickSide();
        if (pa is null || pb is null || sideA is null || sideB is null)
            return false;
        var routeA = _gear?.AtTick(a.Tick, includeTickChanges: false);
        var routeB = _gear?.AtTick(b.Tick, includeTickChanges: false);
        return string.Equals(sideA, sideB, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pa.Value.Side, pb.Value.Side, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pa.Value.Bank, pb.Value.Bank, StringComparison.OrdinalIgnoreCase)
            && a.GetSuperFlickFamily(routeA) == b.GetSuperFlickFamily(routeB);
    }

    private void MarkSuperFlickVisualHit(IEnumerable<ChartEvent> events, ChartEvent hitEvent, HashSet<ChartEventKey> hitEvents)
    {
        foreach (var ev in events)
        {
            if (SameSuperFlickVisual(ev, hitEvent))
                hitEvents.Add(KeyOf(ev));
        }
    }


    private void MarkSuperFlickVisualMiss(IEnumerable<ChartEvent> events, ChartEvent missEvent, HashSet<ChartEventKey> missedEvents)
    {
        foreach (var ev in events)
        {
            if (SameSuperFlickVisual(ev, missEvent))
                missedEvents.Add(KeyOf(ev));
        }
    }

    private void MarkSuperFlickVisualIgnored(IEnumerable<ChartEvent> events, ChartEvent ignoredEvent, HashSet<ChartEventKey> ignoredEvents)
    {
        foreach (var ev in events)
        {
            if (SameSuperFlickVisual(ev, ignoredEvent))
                ignoredEvents.Add(KeyOf(ev));
        }
    }

    private void PlayHitSound(ChartEvent ev)
    {
        var config = _skin.GameConfig;
        if (ev.IsSuperFlick)
        {
            _hitSounds.PlayChange(config.LaneSwitchVolume);
            return;
        }

        var volume = ev.NoteType == NoteType.LinkTail
            ? config.LinkVolume
            : config.TapVolume;
        _hitSounds.PlayNote(volume);
    }
}
