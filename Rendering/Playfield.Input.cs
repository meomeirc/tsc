using TSC;
using TSC.Ares;
using TSC.Input;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System.Collections.Generic;
using Color = Microsoft.Xna.Framework.Color;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
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
        _editorTextInputOwnsKeyboardThisFrame = _chartEditorMode &&
            (_editorPreviewEditorOpen || EditorTextInputActive);

        // Skin reload is a global recovery shortcut. Handle it before modal and
        // text-field early returns so F10 remains available throughout the editor.
        if (Pressed(keyboard, Keys.F10))
            RefreshSkinUiConfig();

#if DEBUG
        // Diagnostic hotkeys are intentionally compiled only into Debug builds.
        // Dispatch them before preview/modal/locked-gameplay early returns so the
        // same tools remain available in regular gameplay and editor preview.
        // A focused editor text field and the embedded media-preview modal retain
        // exclusive keyboard ownership.
        if (!_editorTextInputOwnsKeyboardThisFrame && !_editorPreviewEditorOpen)
        {
            if (Pressed(keyboard, Keys.F6))
            {
                _disableOrbitLaneTransition = !_disableOrbitLaneTransition;
                _statusMessage = $"Orbit lane transitions {(_disableOrbitLaneTransition ? "disabled" : "enabled")}";
            }

            if (Pressed(keyboard, Keys.F7))
            {
                _disableTransitionFlash = !_disableTransitionFlash;
                _statusMessage = $"Transition flash {(_disableTransitionFlash ? "disabled" : "enabled")}";
            }

            var controlDown = keyboard.IsKeyDown(Keys.LeftControl) || keyboard.IsKeyDown(Keys.RightControl);
            var shiftDown = keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift);
            if (Pressed(keyboard, Keys.F8) && controlDown && shiftDown)
            {
                CopyPerformanceDebugOverlayToClipboard();
            }
            else if (Pressed(keyboard, Keys.F8) && controlDown)
            {
                ForceFullGcDiagnostic();
            }
            else if (Pressed(keyboard, Keys.F8))
            {
                _showPerformanceDebug = !_showPerformanceDebug;
                _performanceDebugOverlayText = string.Empty;
                _performanceDebugOverlayNextRefreshTimestamp = 0;
                _performanceDebugWasVisible = false;
                _statusMessage = _showPerformanceDebug ? "Performance diagnostics enabled (F8 to hide)" : "Performance diagnostics disabled";
            }

            if (Pressed(keyboard, Keys.F9))
                ToggleSuperFlickDebug();

            // F11 is reserved for the auxiliary-track diagnostic in Debug
            // gameplay. Shift+F11 remains available for the global window toggle.
            if (Pressed(keyboard, Keys.F11) &&
                !keyboard.IsKeyDown(Keys.LeftShift) &&
                !keyboard.IsKeyDown(Keys.RightShift))
                ToggleAuxTrackDebug();
        }
#endif

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
        UpdateEditorMenuScrollControllers(dt);
        if (HandleEditorFallbackScrollbarInput())
        {
            UpdateLaneHighlights(keyboard, dt);
            return;
        }

        // A focused field gets first refusal on the keyboard before preview or
        // gameplay shortcuts. Its text-edit keys must never also hit mapped lanes
        // or trigger editor actions in the same frame.
        if (_editorTextInputOwnsKeyboardThisFrame)
        {
            if (_chartEditorMode)
                HandleChartEditorInput(keyboard, _previousKeyboard);
            HandleMouseSelection();
            UpdateLaneHighlights(keyboard, dt);
            return;
        }

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
                TryToggleEditorChartPlayback();
            }
            else if (KeybindSettings.IsPressed("editor_toggle_gameplay_mode") && !_lockMenuPlayMode)
            {
                ToggleGameplayMode();
            }
        }

        // Main-menu Play is a locked gameplay session. Editor controls remain
        // disabled here; Debug-only diagnostics above stay available for normal
        // gameplay inspection and testing.
        if (_lockMenuPlayMode)
        {
#if DEBUG
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

        // A field may have received focus from a mouse click during this frame.
        // Swallow mapped keys now as well, while still allowing pointer selection.
        if (_chartEditorMode && !_editorPreviewMode && EditorTextInputActive)
        {
            // A field may have gained focus from this frame's mouse click, after
            // the initial keyboard-ownership snapshot. Claim it before the rest
            // of the frame captures gameplay or editor keybind edges.
            _editorTextInputOwnsKeyboardThisFrame = true;
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


    private void UpdateEditorMenuScrollControllers(double deltaSeconds)
    {
        var viewport = _graphics.Viewport;
        var (virtualWidth, virtualHeight) = GetVirtualResolution(viewport.Width, viewport.Height);
        if (_chartEditorMode && _chart is not null)
        {
            _editorNoteGroupsScroll.SetMaximum(GetEditorNoteGroupScrollMaximum());
            if (_editorProblemsExpanded)
                _editorProblemsScroll.SetMaximum(GetEditorProblemsScrollMaximum(virtualWidth, virtualHeight));
            else
                _editorProblemsScroll.SetMaximum(0.0);

            if (_editorMetaPanelOpen)
            {
                var rows = GetEditorMetaRows(_tempo?.SecondsToTick(_playSeconds) ?? 0.0);
                _editorMetaScroll.SetMaximum(GetEditorMetaMaxScrollOffset(rows, virtualWidth, virtualHeight));
            }
        }

        _editorNoteGroupsScroll.Update(deltaSeconds);
        _editorMetaScroll.Update(deltaSeconds);
        _editorProblemsScroll.Update(deltaSeconds);
        _editorNoteGroupsScrollOffset = GetEditorNoteGroupScrollFirstRow();
        if (_editorMetaPanelOpen)
        {
            var rows = GetEditorMetaRows(_tempo?.SecondsToTick(_playSeconds) ?? 0.0);
            var layouts = GetEditorMetaRowLayouts(rows, virtualWidth, virtualHeight);
            _editorMetaScrollOffset = GetEditorMetaScrollFirstVisibleRow(layouts);
        }
        _editorProblemsScrollOffset = GetEditorProblemsScrollFirstRow(virtualWidth, virtualHeight);
    }


    private bool HandleEditorFallbackScrollbarInput()
    {
        if (!_chartEditorMode || _chart is null)
        {
            _editorNoteGroupsScroll.CancelPointer();
            _editorMetaScroll.CancelPointer();
            _editorProblemsScroll.CancelPointer();
            return false;
        }

        // When the Lua editor interface is active, its skin controls route
        // presses and drags through the same SmoothScrollHandler instances via
        // SkinRuntime's handler resolver. Do not cancel pointer capture here:
        // this fallback path runs every frame before the Lua drag update.
        if (_chartEditorInterfaceRuntime.IsLoaded && _chartEditorInterfaceRuntime.LastError is null)
            return false;

        var viewport = _graphics.Viewport;
        var (virtualWidth, virtualHeight) = GetVirtualResolution(viewport.Width, viewport.Height);
        var mouse = GlobalInput.Mouse;
        var previousMouse = GlobalInput.PreviousMouse;
        var mouseDown = mouse.LeftButton == ButtonState.Pressed;
        var mousePressed = mouseDown && previousMouse.LeftButton != ButtonState.Pressed;
        var pointer = new Microsoft.Xna.Framework.Point(
            (int)MathF.Round(mouse.X * virtualWidth / (float)Math.Max(1, viewport.Width)),
            (int)MathF.Round(mouse.Y * virtualHeight / (float)Math.Max(1, viewport.Height)));

        if (_editorMenuSection == EditorMenuSection.NoteGroups)
        {
            var firstRow = GetEditorNoteGroupEntryBounds(0, virtualWidth, virtualHeight);
            if (!firstRow.IsEmpty)
            {
                var unit = _projection.ScalePixels(1f);
                var trackHeight = Math.Max(1, (int)MathF.Round((5 * 50f + 4 * 5f) * unit));
                var visualTrack = new Rectangle(firstRow.Right + (int)MathF.Round(9f * unit), firstRow.Y,
                    Math.Max(2, (int)MathF.Round(6f * unit)), trackHeight);
                var interactionTrack = new Rectangle(visualTrack.X - Math.Max(4, (int)MathF.Round(6f * unit)),
                    visualTrack.Y, visualTrack.Width + Math.Max(8, (int)MathF.Round(12f * unit)), visualTrack.Height);
                var maximum = GetEditorNoteGroupScrollMaximum();
                _editorNoteGroupsScroll.SetMaximum(maximum);
                if (_editorNoteGroupsScroll.HandleScrollbar(mousePressed, mouseDown, pointer.X, pointer.Y,
                    interactionTrack, GetEditorNoteGroupScrollContentExtent(), GetEditorNoteGroupScrollViewportExtent(),
                    minimumThumbSize: Math.Max(1, (int)MathF.Round(24f * unit))))
                {
                    _editorNoteGroupsScrollOffset = GetEditorNoteGroupScrollFirstRow();
                    return true;
                }
            }
        }
        _editorNoteGroupsScroll.CancelPointer();

        if (_editorMetaPanelOpen &&
            !string.Equals(_editorMetaCategory, "MEDIA", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(_editorMetaCategory, "TEST", StringComparison.OrdinalIgnoreCase))
        {
            var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
            var rows = GetEditorMetaRows(_tempo?.SecondsToTick(_playSeconds) ?? 0.0);
            var layouts = GetEditorMetaRowLayouts(rows, virtualWidth, virtualHeight);
            var maximum = GetEditorMetaMaxScrollOffset(rows, virtualWidth, virtualHeight);
            var totalContentHeight = GetEditorMetaPrefixHeight(layouts, layouts.Count);
            if (!content.IsEmpty && maximum > 0 && totalContentHeight > content.Height)
            {
                var unit = _projection.ScalePixels(1f);
                var railWidth = Math.Max(2, (int)MathF.Round(3f * unit));
                var visualTrack = new Rectangle(content.Right - railWidth, content.Y, railWidth, content.Height);
                var hitPadding = Math.Max(4, (int)MathF.Round(6f * unit));
                var interactionTrack = new Rectangle(visualTrack.X - hitPadding, visualTrack.Y,
                    visualTrack.Width + hitPadding * 2, visualTrack.Height);
                _editorMetaScroll.SetMaximum(maximum);
                if (_editorMetaScroll.HandleScrollbar(mousePressed, mouseDown, pointer.X, pointer.Y,
                    interactionTrack, totalContentHeight, content.Height,
                    minimumThumbSize: Math.Max(1, (int)MathF.Round(24f * unit))))
                {
                    _editorMetaScrollOffset = GetEditorMetaScrollFirstVisibleRow(layouts);
                    return true;
                }
            }
        }
        _editorMetaScroll.CancelPointer();

        if (_editorProblemsExpanded)
        {
            RefreshEditorDiagnostics();
            var list = GetEditorProblemsListBounds(virtualWidth, virtualHeight);
            var visibleRows = GetEditorProblemsScrollWindowRows(virtualWidth, virtualHeight);
            if (!list.IsEmpty && visibleRows > 0)
            {
                var unit = _projection.ScalePixels(1f);
                var railWidth = Math.Max(2, (int)MathF.Round(3f * unit));
                var visualTrack = new Rectangle(list.Right - railWidth, list.Y, railWidth, list.Height);
                var hitPadding = Math.Max(4, (int)MathF.Round(6f * unit));
                var interactionTrack = new Rectangle(visualTrack.X - hitPadding, visualTrack.Y,
                    visualTrack.Width + hitPadding * 2, visualTrack.Height);
                var maximum = GetEditorProblemsScrollMaximum(virtualWidth, virtualHeight);
                _editorProblemsScroll.SetMaximum(maximum);
                if (_editorProblemsScroll.HandleScrollbar(mousePressed, mouseDown, pointer.X, pointer.Y,
                    interactionTrack, GetEditorProblemsScrollContentExtent(virtualWidth, virtualHeight),
                    GetEditorProblemsScrollViewportExtent(virtualWidth, virtualHeight),
                    minimumThumbSize: Math.Max(15, (int)MathF.Round(15f * unit))))
                {
                    _editorProblemsScrollOffset = GetEditorProblemsScrollFirstRow(virtualWidth, virtualHeight);
                    return true;
                }
            }
        }
        _editorProblemsScroll.CancelPointer();
        return false;
    }

    private void SetEditorNoteGroupScrollPosition(double value, bool immediate = false)
    {
        _editorNoteGroupsScroll.SetMaximum(GetEditorNoteGroupScrollMaximum());
        var pixelPosition = Math.Max(0.0, value) * GetEditorNoteGroupScrollRowPitch();
        if (immediate) _editorNoteGroupsScroll.SetPosition(pixelPosition);
        else _editorNoteGroupsScroll.SetTarget(pixelPosition);
        _editorNoteGroupsScrollOffset = GetEditorNoteGroupScrollFirstRow();
    }

    private void SetEditorMetaScrollPosition(double value, bool immediate = false,
        IReadOnlyList<(string Label, string Value)>? rows = null, int virtualWidth = 0, int virtualHeight = 0)
    {
        if (rows is not null && virtualWidth > 0 && virtualHeight > 0)
            _editorMetaScroll.SetMaximum(GetEditorMetaMaxScrollOffset(rows, virtualWidth, virtualHeight));
        if (immediate) _editorMetaScroll.SetPosition(value);
        else _editorMetaScroll.SetTarget(value);
        var effectiveRows = rows ?? GetEditorMetaRows(_tempo?.SecondsToTick(_playSeconds) ?? 0.0);
        var fallbackResolution = GetVirtualResolution(_graphics.Viewport.Width, _graphics.Viewport.Height);
        var effectiveWidth = virtualWidth > 0 ? virtualWidth : fallbackResolution.Width;
        var effectiveHeight = virtualHeight > 0 ? virtualHeight : fallbackResolution.Height;
        var layouts = GetEditorMetaRowLayouts(effectiveRows, effectiveWidth, effectiveHeight);
        _editorMetaScrollOffset = GetEditorMetaScrollFirstVisibleRow(layouts);
    }

    private void SetEditorProblemsScrollPosition(double value, bool immediate = false)
    {
        var viewport = _graphics.Viewport;
        var (virtualWidth, virtualHeight) = GetVirtualResolution(viewport.Width, viewport.Height);
        _editorProblemsScroll.SetMaximum(GetEditorProblemsScrollMaximum(virtualWidth, virtualHeight));
        if (immediate) _editorProblemsScroll.SetPosition(value);
        else _editorProblemsScroll.SetTarget(value);
        _editorProblemsScrollOffset = GetEditorProblemsScrollFirstRow(virtualWidth, virtualHeight);
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
        var textFieldOwnsInput = _editorPreviewEditorOpen || _editorPreviewFocusedField >= 0 ||
                                 _editorMetaEditingField is not null || _editorDurationInputFocused ||
                                 _editorStutterInputField != EditorStutterInputField.None || _editorBpmInputFocused;
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
            !_editorTimelineScrubbing && !_editorWheelScrollPauseActive && CanStartEditorChartPlayback())
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

        if (shouldResume && _chartEditorMode && !_playing && CanStartEditorChartPlayback())
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

    private double GetEditorWheelScrollDistance(int virtualHeight)
    {
        // Editor content is laid out in the fixed-height virtual canvas, while
        // Info and Settings use output-screen pixels. Convert the shared wheel
        // distance so it moves the same physical distance at every resolution.
        var outputHeight = Math.Max(1, _graphics.Viewport.Height);
        var logicalLineSpacing = _font is null ? 21.0 : UnicodeTextRenderer.GetLogicalLineSpacing(_font);
        var outputPixelsPerNotch = TSC.UI.SmoothScrollHandler.GetWheelPixelsPerNotch(logicalLineSpacing);
        return outputPixelsPerNotch * Math.Max(1, virtualHeight) / (double)outputHeight;
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
            if (_editorMenuSection == EditorMenuSection.NoteGroups)
            {
                var menuPanel = GetEditorMenuPanelBounds(virtualWidth, virtualHeight);
                if (menuPanel.Contains(pointer))
                {
                    var firstRow = GetEditorNoteGroupEntryBounds(0, virtualWidth, virtualHeight);
                    var lastRow = GetEditorNoteGroupEntryBounds(EditorNoteGroupMenuVisibleRows - 1,
                        virtualWidth, virtualHeight);
                    var scrollbarPadding = (int)MathF.Round(18f * _projection.ScalePixels(1f));
                    var listBounds = new Rectangle(firstRow.X, firstRow.Y,
                        lastRow.Right - firstRow.X + scrollbarPadding, lastRow.Bottom - firstRow.Y);
                    if (listBounds.Contains(pointer))
                    {
                        _editorNoteGroupsScroll.SetMaximum(GetEditorNoteGroupScrollMaximum());
                        _editorNoteGroupsScroll.HandleWheel(wheelDelta, GetEditorWheelScrollDistance(virtualHeight));
                        _editorNoteGroupsScrollOffset = GetEditorNoteGroupScrollFirstRow();
                    }
                    return;
                }
            }
            if (_chartEditorInterfaceRuntime.IsLoaded && _chartEditorInterfaceRuntime.LastError is null)
            {
                var uiOverMetaContent = _editorMetaPanelOpen &&
                    GetEditorMetaContentBounds(virtualWidth, virtualHeight).Contains(pointer) &&
                    _chartEditorInterfaceRuntime.IsUiPointOverControlWithIdPrefix(pointer.X, pointer.Y, "editor_meta_");
                var uiOverProblems = _editorProblemsExpanded &&
                    (_chartEditorInterfaceRuntime.IsUiPointOverControlWithIdPrefix(pointer.X, pointer.Y, "editor_problems_") ||
                     _chartEditorInterfaceRuntime.IsUiPointOverControlWithIdPrefix(pointer.X, pointer.Y, "editor_problem_"));
                if (uiOverMetaContent)
                {
                    var rows = GetEditorMetaRows(_tempo?.SecondsToTick(_playSeconds) ?? 0.0);
                    _editorMetaScroll.SetMaximum(GetEditorMetaMaxScrollOffset(rows, virtualWidth, virtualHeight));
                    var layouts = GetEditorMetaRowLayouts(rows, virtualWidth, virtualHeight);
                    _editorMetaScroll.HandleWheel(wheelDelta, GetEditorWheelScrollDistance(virtualHeight));
                    _editorMetaScrollOffset = GetEditorMetaScrollFirstVisibleRow(layouts);
                    return;
                }
                if (uiOverProblems)
                {
                    RefreshEditorDiagnostics();
                    _editorProblemsScroll.SetMaximum(GetEditorProblemsScrollMaximum(virtualWidth, virtualHeight));
                    _editorProblemsScroll.HandleWheel(wheelDelta, GetEditorWheelScrollDistance(virtualHeight));
                    _editorProblemsScrollOffset = GetEditorProblemsScrollFirstRow(virtualWidth, virtualHeight);
                    return;
                }
            }
            if (GetEditorProblemsPanelBounds(virtualWidth, virtualHeight).Contains(pointer))
            {
                if (_editorProblemsExpanded && GetEditorProblemsListBounds(virtualWidth, virtualHeight).Contains(pointer))
                {
                    RefreshEditorDiagnostics();
                    _editorProblemsScroll.SetMaximum(GetEditorProblemsScrollMaximum(virtualWidth, virtualHeight));
                    _editorProblemsScroll.HandleWheel(wheelDelta, GetEditorWheelScrollDistance(virtualHeight));
                    _editorProblemsScrollOffset = GetEditorProblemsScrollFirstRow(virtualWidth, virtualHeight);
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
                    _editorMetaScroll.SetMaximum(GetEditorMetaMaxScrollOffset(rows, virtualWidth, virtualHeight));
                    var layouts = GetEditorMetaRowLayouts(rows, virtualWidth, virtualHeight);
                    _editorMetaScroll.HandleWheel(wheelDelta, GetEditorWheelScrollDistance(virtualHeight));
                    _editorMetaScrollOffset = GetEditorMetaScrollFirstVisibleRow(layouts);
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
            RemoveEditorMultiSelectedEventsWhere(ev => !ShouldRenderInCurrentBank(ev, editState));
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

    private bool IsTrackKeyDown(int track) => !_editorTextInputOwnsKeyboardThisFrame && KeybindSettings.IsTrackDown(track);

    private bool IsTrackKeyPressed(int track) => !_editorTextInputOwnsKeyboardThisFrame && KeybindSettings.IsTrackPressed(track);

    private bool IsSuperFlickInputPressed(ChartEvent ev) => !_editorTextInputOwnsKeyboardThisFrame && KeybindSettings.IsSuperFlickPressed(ev);

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

    private float GetHitSoundVolume(ChartEvent ev)
    {
        var config = _skin.GameConfig;
        var volume = ev.IsSuperFlick
            ? config.LaneSwitchVolume
            : ev.NoteType == NoteType.LinkTail
                ? config.LinkVolume
                : config.TapVolume;
        return float.IsFinite(volume) ? Math.Clamp(volume, 0f, 1f) : 0f;
    }

    private void PlayHitSound(ChartEvent ev)
    {
        var volume = GetHitSoundVolume(ev);
        if (volume <= 0f)
            return;

        if (ev.IsSuperFlick)
            _hitSounds.PlayChange(volume);
        else
            _hitSounds.PlayNote(volume);
    }

    private void AccumulateAutomaticHitSound(ChartEvent ev, ref float noteVolume, ref float changeVolume)
    {
        var volume = GetHitSoundVolume(ev);
        if (volume <= 0f)
            return;

        if (ev.IsSuperFlick)
            changeVolume = Math.Max(changeVolume, volume);
        else
            noteVolume = Math.Max(noteVolume, volume);
    }

    private void PlayAutomaticHitSounds(float noteVolume, float changeVolume)
    {
        if (noteVolume > 0f)
            _hitSounds.PlayNote(noteVolume);
        if (changeVolume > 0f)
            _hitSounds.PlayChange(changeVolume);
    }
}
