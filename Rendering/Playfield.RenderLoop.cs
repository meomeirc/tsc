using TSC;
using TSC.Ares;
using TSC.Input;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
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
    // Frame update/render orchestration and playfield/background rendering.

    private bool Pressed(KeyboardState keyboard, Keys key) => keyboard.IsKeyDown(key) && !_previousKeyboard.IsKeyDown(key);

    private double ChartDuration()
    {
        if (_chart is null || _tempo is null)
            return 0;
        if (_chart.PlayTime > 0)
        {
            // PlayTime is the music duration. If audio starts after chart tick
            // zero, keep the chart clock alive until that duration has elapsed
            // from AudioOriginTick rather than stopping early at PlayTime.
            var audioOriginSeconds = _tempo.TickToSeconds(Math.Max(0, _chart.AudioOriginTick));
            return audioOriginSeconds + _chart.PlayTime;
        }

        var lastTick = _chart.PlayTick;
        if (lastTick <= 0 && _chart.Events.Count > 0)
            lastTick = _chart.Events.Max(e => e.Tick);
        return _tempo.TickToSeconds(lastTick) + 1.0;
    }

    public void Draw()
    {
        var frameStartTimestamp = Stopwatch.GetTimestamp();
        if (_debugLastDrawStartTimestamp != 0)
        {
            _debugDrawIntervalMs = (frameStartTimestamp - _debugLastDrawStartTimestamp) * 1000.0 / Stopwatch.Frequency;
            if (_debugDrawIntervalWindowStartTimestamp == 0)
            {
                _debugDrawIntervalWindowStartTimestamp = _debugLastDrawStartTimestamp;
                _debugDrawIntervalWindowFrameCount = 1;
                _debugDrawIntervalMaxWindowMs = _debugDrawIntervalMs;
                _debugDrawLateIntervalsInWindow = _debugDrawIntervalMs > 20.0 ? 1 : 0;
            }
            else
            {
                _debugDrawIntervalWindowFrameCount++;
                if (_debugDrawIntervalMs > _debugDrawIntervalMaxWindowMs)
                    _debugDrawIntervalMaxWindowMs = _debugDrawIntervalMs;
                if (_debugDrawIntervalMs > 20.0)
                    _debugDrawLateIntervalsInWindow++;

                var windowTicks = frameStartTimestamp - _debugDrawIntervalWindowStartTimestamp;
                if (windowTicks >= Stopwatch.Frequency)
                {
                    _debugDrawFpsAverage = _debugDrawIntervalWindowFrameCount * (double)Stopwatch.Frequency / windowTicks;
                    _debugDrawIntervalCompletedWindowMaxMs = _debugDrawIntervalMaxWindowMs;
                    _debugDrawLateIntervalsLastWindow = _debugDrawLateIntervalsInWindow;
                    _debugDrawIntervalWindowStartTimestamp = frameStartTimestamp;
                    _debugDrawIntervalWindowFrameCount = 0;
                    _debugDrawIntervalMaxWindowMs = 0.0;
                    _debugDrawLateIntervalsInWindow = 0;
                }
            }
        }
        _debugLastDrawStartTimestamp = frameStartTimestamp;

        var frameAllocStart = GC.GetAllocatedBytesForCurrentThread();
        var gc0Start = GC.CollectionCount(0);
        var gc1Start = GC.CollectionCount(1);
        var gc2Start = GC.CollectionCount(2);

        // F8 is a diagnostic view, not a reason to instrument every single
        // gameplay frame. Sample the expensive timing/counter instrumentation
        // at 10 Hz while keeping the overlay itself visible every frame.
        var debugNow = frameStartTimestamp;
        if (_showPerformanceDebug)
        {
            _performanceDebugInstrumentFrame =
                !_performanceDebugWasVisible ||
                debugNow >= _performanceDebugNextSampleTimestamp;

            if (_performanceDebugInstrumentFrame)
                _performanceDebugNextSampleTimestamp =
                    debugNow + Math.Max(1L, Stopwatch.Frequency / 10);
        }
        else
        {
            _performanceDebugInstrumentFrame = false;
        }

        _performanceDebugWasVisible = _showPerformanceDebug;

        if (_performanceDebugInstrumentFrame)
        {
            ResetPerformanceDebugFrame();
            _projection.ResetPerformanceDebugCounters();
        }
        _projection.PerformanceDebugEnabled = _performanceDebugInstrumentFrame;

        var outputViewport = _graphics.Viewport;
        var (virtualWidth, virtualHeight) = GetVirtualResolution(outputViewport.Width, outputViewport.Height);
        EnsureGameplayRenderTarget(virtualWidth, virtualHeight);
        if (_gameplayRenderTarget is null)
            return;

        _graphics.SetRenderTarget(_gameplayRenderTarget);
        _graphics.Viewport = new Viewport(0, 0, virtualWidth, virtualHeight);
        _projection.Resize(virtualWidth, virtualHeight);
        SyncEditorConstantScrollMode();
        _projection.Compact = _compact;

        // Build the Lua skin once for this frame. Negative layers are rendered
        // between the default background and the 3D playfield; non-negative
        // layers are rendered after the normal HUD.
        _skinRuntime.DebugMeasureApiAllocations = _performanceDebugInstrumentFrame;
        var allocStart = GC.GetAllocatedBytesForCurrentThread();
        var skinBuildStartTimestamp = Stopwatch.GetTimestamp();
        _skinDrawCommands = _skinRuntime.BuildDrawCommands(virtualWidth, virtualHeight);
        _debugSkinBuildMs = ElapsedMilliseconds(skinBuildStartTimestamp);
        _debugAllocSkinBuildBytes += GC.GetAllocatedBytesForCurrentThread() - allocStart;

        _graphics.Clear(Background);
        WarmFeedbackRenderingIfNeeded();

        var editorGroupInactiveBanks = _chartEditorMode && !_editorPreviewMode && !_compact &&
            _chart is not null && _tempo is not null && _gear is not null &&
            Math.Clamp(_editorRenderConfig.Editor.InactiveBankOpacity, 0f, 1f) > 0.001f &&
            Math.Clamp(_editorRenderConfig.Editor.InactiveBankOpacity, 0f, 1f) < 0.999f;
        var laneTick = _tempo?.SecondsToTick(_playSeconds) ?? 0.0;
        GearState? laneState = _chart is not null && _tempo is not null && _gear is not null
            ? RenderGearState(laneTick)
            : null;

        if (editorGroupInactiveBanks && laneState is not null)
        {
            PrepareEditorInactiveBankLayer(laneTick, laneState, virtualWidth, virtualHeight);
            // The main target has not had any scene content drawn yet. Clear it
            // again after switching back from the layer target so this remains safe
            // even on graphics drivers that discard contents when an RT is unbound.
            _graphics.Clear(Background);
        }

        allocStart = GC.GetAllocatedBytesForCurrentThread();
        var skinDrawStartTimestamp = Stopwatch.GetTimestamp();
        DrawSkinScriptUiLayers(_skinDrawCommands, true, _skinRuntime);
        _debugSkinDrawMs = ElapsedMilliseconds(skinDrawStartTimestamp);
        _debugAllocSkinDrawBytes += GC.GetAllocatedBytesForCurrentThread() - allocStart;

        // The editor owns its background dimmer: dim background/skin layers by
        // 50% in top-down 2D, but keep the lanes and notes at full brightness.
        // 3D view (including preview mode) deliberately leaves the background undimmed.
        if (_chartEditorMode && !_editorPreviewMode && _topDownViewEnabled && !_editorPanelsViewEnabled)
        {
            _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.NonPremultiplied);
            _spriteBatch.Draw(_pixel, new Rectangle(0, 0, virtualWidth, virtualHeight), new Color(0, 0, 0, 128));
            _spriteBatch.End();
        }

        var laneDrawCallsStart = _debugTexturedDrawCalls;
        var laneVerticesStart = _debugTexturedVertices;
        // Draw the currently active banks normally. Inactive banks are skipped
        // here and rendered, together with their notes, into a separate layer below.
        _editorActiveBanksOnlyPass = editorGroupInactiveBanks;
        allocStart = GC.GetAllocatedBytesForCurrentThread();
        DrawPlayfield();
        DrawLaneHighlights();
        DrawTransitionFlashes();
        _editorActiveBanksOnlyPass = false;
        _debugAllocLaneBytes += GC.GetAllocatedBytesForCurrentThread() - allocStart;
        if (_performanceDebugInstrumentFrame)
        {
            _debugTexturedLaneDrawCalls = _debugTexturedDrawCalls - laneDrawCallsStart;
            _debugTexturedLaneVertices = _debugTexturedVertices - laneVerticesStart;
        }
        if (editorGroupInactiveBanks && laneState is not null)
        {
            CompositeEditorInactiveBankLayer(virtualWidth, virtualHeight,
                Math.Clamp(_editorRenderConfig.Editor.InactiveBankOpacity, 0f, 1f));
        }
        if (_chart is null || _tempo is null || _gear is null)
        {
            DrawOverlay("No chart loaded");
        }
        else
        {
            var currentTick = laneTick;
            var currentState = laneState ?? RenderGearState(currentTick);
            var guideDrawCallsStart = _debugTexturedDrawCalls;
            var guideVerticesStart = _debugTexturedVertices;

            // Beat subdivisions and tempo-change guide lines are editor-only.
            // Do not call their projection/drawing paths during normal gameplay:
            // the judgement line is the only timing line needed while playing.
            if (_chartEditorMode && !_editorPreviewMode)
            {
                allocStart = GC.GetAllocatedBytesForCurrentThread();
                DrawEditorBankSeparators();
                // Keep gameplay beat lines visible behind the translucent panel
                // overlay; the panel tracks also draw their own flat 2D snap grid.
                DrawBeatLines(currentTick, currentState);
                DrawBeatNumbers(currentTick, currentState);
                DrawTempoChanges(currentTick, currentState);
                DrawPlayTickMarker(currentTick, currentState);
                _debugAllocBeatTempoBytes += GC.GetAllocatedBytesForCurrentThread() - allocStart;
            }

            // Only active-bank judge markers remain in the normal pass. Inactive
            // markers were already drawn into the shared faded bank layer above.
            _editorActiveBanksOnlyPass = editorGroupInactiveBanks;
            DrawJudgementLine(currentTick);
            _editorActiveBanksOnlyPass = false;
            if (_performanceDebugInstrumentFrame)
            {
                _debugTexturedGuideDrawCalls = _debugTexturedDrawCalls - guideDrawCallsStart;
                _debugTexturedGuideVertices = _debugTexturedVertices - guideVerticesStart;
            }

            var noteDrawCallsStart = _debugTexturedDrawCalls;
            var noteVerticesStart = _debugTexturedVertices;
            var drawNotesStartTimestamp = Stopwatch.GetTimestamp();
            allocStart = GC.GetAllocatedBytesForCurrentThread();
            _editorActiveBanksOnlyPass = editorGroupInactiveBanks;
            DrawNotes(currentTick, currentState);
            _editorActiveBanksOnlyPass = false;
            if (_chartEditorMode && !_editorPreviewMode)
                DrawEditorProblemNoteMarkers(currentTick, currentState);
            _debugAllocNotesBytes += GC.GetAllocatedBytesForCurrentThread() - allocStart;
            _debugDrawNotesMs = ElapsedMilliseconds(drawNotesStartTimestamp);
            if (_performanceDebugInstrumentFrame)
            {
                _debugTexturedNoteDrawCalls = _debugTexturedDrawCalls - noteDrawCallsStart;
                _debugTexturedNoteVertices = _debugTexturedVertices - noteVerticesStart;
            }

            var feedbackDrawCallsStart = _debugTexturedDrawCalls;
            var feedbackVerticesStart = _debugTexturedVertices;
            allocStart = GC.GetAllocatedBytesForCurrentThread();
            DrawHitAnimations();
            if (!_editorPreviewMode)
                DrawSelectionOverlay(currentTick, currentState);
            if (_chartEditorMode && !_editorPreviewMode && _editorPanelsViewEnabled)
                DrawEditorPanelsView(currentTick, currentState);
            if (!_hideDebugHud && !_chartEditorMode)
                DrawHud(currentState, currentTick);
            if (_showAuxTrackDebug && !_chartEditorMode)
                DrawAuxTrackDebugOverlay(currentTick);
            _debugAllocFeedbackHudBytes += GC.GetAllocatedBytesForCurrentThread() - allocStart;
            if (_performanceDebugInstrumentFrame)
            {
                _debugTexturedFeedbackDrawCalls = _debugTexturedDrawCalls - feedbackDrawCallsStart;
                _debugTexturedFeedbackVertices = _debugTexturedVertices - feedbackVerticesStart;
            }
        }

        allocStart = GC.GetAllocatedBytesForCurrentThread();
        skinDrawStartTimestamp = Stopwatch.GetTimestamp();
        DrawSkinScriptUiLayers(_skinDrawCommands, false, _skinRuntime);
        _debugSkinDrawMs += ElapsedMilliseconds(skinDrawStartTimestamp);
        _debugAllocSkinDrawBytes += GC.GetAllocatedBytesForCurrentThread() - allocStart;
        if (_gameplayPaused)
        {
            var pauseSkinDrawn = false;
            if (_pauseSkinRuntime.IsLoaded)
            {
                try
                {
                    _pauseSkinRuntime.UpdatePauseState(_projection.ScrollSpeedScale, true, _pauseSelection);
                    _pauseSkinDrawCommands = _pauseSkinRuntime.BuildDrawCommands(virtualWidth, virtualHeight);
                    if (_pauseSkinRuntime.IsLoaded)
                    {
                        DrawSkinScriptUiLayers(_pauseSkinDrawCommands, false, _pauseSkinRuntime);
                        pauseSkinDrawn = _pauseSkinRuntime.IsLoaded;
                    }
                }
                catch (Exception ex)
                {
                    Program.LogFatal("GAMEPLAY_PAUSE_SKIN_DRAW", ex);
                }
            }

            // A broken/missing Lua pause skin must never make gameplay unpausable.
            if (!pauseSkinDrawn)
                DrawFallbackPauseMenu(virtualWidth, virtualHeight);
        }
        if (_gameplayResultActive)
        {
            _resultSkinDrawCommands = _resultSkinRuntime.BuildDrawCommands(virtualWidth, virtualHeight);
            DrawSkinScriptUiLayers(_resultSkinDrawCommands, false, _resultSkinRuntime);
        }
        // The Problems panel belongs to the editor-control layer: put it above
        // the chart/HUD content but immediately below the toolbar. This keeps
        // the bottom-left controls and their SNAPS popup fully visible and usable.
        if (_chartEditorMode && _chart is not null && _tempo is not null)
        {
            var chartEditorLuaDrawn = false;
            try
            {
                _chartEditorInterfaceRuntime.UpdateChartEditorInterfaceState(
                    BuildChartEditorInterfaceState(virtualWidth, virtualHeight));
                var editorCommands = _chartEditorInterfaceRuntime.BuildDrawCommands(virtualWidth, virtualHeight);
                if (_chartEditorInterfaceRuntime.IsLoaded && _chartEditorInterfaceRuntime.LastError is null)
                {
                    DrawSkinScriptUiLayers(editorCommands, false, _chartEditorInterfaceRuntime);
                    if (_editorMetaPanelOpen && string.Equals(_editorMetaCategory, "MEDIA", StringComparison.OrdinalIgnoreCase) &&
                        !_editorPreviewEditorOpen && !_editorMetaExportExpanded && !_editorMetaDeleteConfirmOpen && !_editorElementDropdownOpen)
                        DrawEditorMediaArtworkPreviewOverlay(virtualWidth, virtualHeight);
                    chartEditorLuaDrawn = true;
                }
            }
            catch (Exception ex)
            {
                Program.LogFatal("CHART_EDITOR_INTERFACE_LUA_DRAW", ex);
            }

            if (!chartEditorLuaDrawn)
            {
                if (!_hideDebugHud && !_editorPreviewMode)
                {
                    var fallbackTick = _tempo!.SecondsToTick(_playSeconds);
                    DrawHud(RenderGearState(fallbackTick), fallbackTick);
                }
                if (_showAuxTrackDebug && !_editorPreviewMode)
                    DrawAuxTrackDebugOverlay(_tempo!.SecondsToTick(_playSeconds));
                if (_editorPreviewMode)
                    DrawEditorPreviewToolbar();
                else
                {
                    DrawEditorProblemsPanel(virtualWidth, virtualHeight);
                    DrawEditorBankIndicators();
                    DrawEditorToolbar();
                }
            }
        }

        if (_showPerformanceDebug)
            DrawPerformanceDebugOverlay();

        _graphics.SetRenderTarget(null);
        _graphics.Viewport = outputViewport;
        _graphics.BlendState = BlendState.Opaque;
        _graphics.DepthStencilState = DepthStencilState.None;
        _spriteBatch.Begin(
            samplerState: SamplerState.LinearClamp,
            blendState: BlendState.Opaque);
        _spriteBatch.Draw(
            _gameplayRenderTarget,
            new Rectangle(0, 0, outputViewport.Width, outputViewport.Height),
            Color.White);
        _spriteBatch.End();

        // Measure after drawing the diagnostics overlay and compositing the render
        // target. The overlay shown above contains the previous completed frame's
        // value, which is preferable to reporting a partial frame time.
        _debugFrameMs = ElapsedMilliseconds(frameStartTimestamp);
        _debugAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - frameAllocStart;
        _debugGc0Delta = Math.Max(0, GC.CollectionCount(0) - gc0Start);
        _debugGc1Delta = Math.Max(0, GC.CollectionCount(1) - gc1Start);
        _debugGc2Delta = Math.Max(0, GC.CollectionCount(2) - gc2Start);

        if (_debugClearProbeFrames > 0)
        {
            _debugClearProbeFrames--;
            _debugClearMaxFrameMs = Math.Max(_debugClearMaxFrameMs, _debugFrameMs);
            _debugClearMaxSkinBuildMs = Math.Max(_debugClearMaxSkinBuildMs, _debugSkinBuildMs);
            _debugClearMaxSkinDrawMs = Math.Max(_debugClearMaxSkinDrawMs, _debugSkinDrawMs);
            _debugClearMaxAllocBytes = Math.Max(_debugClearMaxAllocBytes, _debugAllocatedBytes);
            _debugClearGc0 += _debugGc0Delta;
            _debugClearGc1 += _debugGc1Delta;
            _debugClearGc2 += _debugGc2Delta;
        }
    }

    private (Rectangle Panel, Rectangle[] Buttons, Rectangle SpeedRow) GetFallbackPauseLayout(int width, int height)
    {
        var panelWidth = Math.Min(760, Math.Max(300, width - 40));
        var panelHeight = Math.Min(500, Math.Max(360, height - 40));
        var panel = new Rectangle((width - panelWidth) / 2, (height - panelHeight) / 2, panelWidth, panelHeight);
        var buttonWidth = Math.Min(620, panelWidth - 56);
        var buttonHeight = Math.Clamp((int)MathF.Round(height * 0.048f), 42, 54);
        var buttonGap = Math.Clamp((int)MathF.Round(height * 0.012f), 8, 14);
        var buttonX = panel.X + (panel.Width - buttonWidth) / 2;
        var firstButtonY = panel.Y + (int)MathF.Round(panel.Height * 0.30f);
        var buttons = new Rectangle[3];
        for (var i = 0; i < buttons.Length; i++)
            buttons[i] = new Rectangle(buttonX, firstButtonY + i * (buttonHeight + buttonGap), buttonWidth, buttonHeight);
        var speedRow = new Rectangle(panel.X + 28, panel.Bottom - Math.Max(76, (int)MathF.Round(panel.Height * 0.20f)), panel.Width - 56,
            Math.Max(52, (int)MathF.Round(height * 0.06f)));
        return (panel, buttons, speedRow);
    }

    private void DrawFallbackPauseMenu(int virtualWidth, int virtualHeight)
    {
        if (_pixel is null || _font is null)
            return;

        var layout = GetFallbackPauseLayout(virtualWidth, virtualHeight);
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.NonPremultiplied);
        _spriteBatch.Draw(_pixel, new Rectangle(0, 0, virtualWidth, virtualHeight), new Color(0, 0, 0, 174));
        _spriteBatch.Draw(_pixel, layout.Panel, new Color(14, 15, 18, 250));
        var edge = Math.Max(2, (int)MathF.Round(virtualHeight / 540f));
        _spriteBatch.Draw(_pixel, new Rectangle(layout.Panel.X, layout.Panel.Y, layout.Panel.Width, edge), new Color(232, 185, 76));
        _spriteBatch.Draw(_pixel, new Rectangle(layout.Panel.X, layout.Panel.Bottom - edge, layout.Panel.Width, edge), new Color(65, 66, 70));
        _spriteBatch.Draw(_pixel, new Rectangle(layout.Panel.X, layout.Panel.Y, edge, layout.Panel.Height), new Color(65, 66, 70));
        _spriteBatch.Draw(_pixel, new Rectangle(layout.Panel.Right - edge, layout.Panel.Y, edge, layout.Panel.Height), new Color(65, 66, 70));

        DrawFallbackPauseText("PAUSED", new Vector2(layout.Panel.Center.X, layout.Panel.Y + layout.Panel.Height * 0.13f),
            Math.Clamp(virtualHeight * 0.045f, 28f, 48f), Color.White, centered: true);

        string[] labels = { "CONTINUE", "RETRY SONG", "BACK TO MENU" };
        for (var i = 0; i < layout.Buttons.Length; i++)
        {
            var bounds = layout.Buttons[i];
            var selected = _pauseSelection == i;
            _spriteBatch.Draw(_pixel, bounds, selected ? new Color(85, 64, 24, 255) : new Color(31, 32, 36, 255));
            var border = selected ? new Color(245, 192, 73, 255) : new Color(75, 76, 82, 255);
            _spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Y, bounds.Width, 2), border);
            _spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Bottom - 2, bounds.Width, 2), border);
            DrawFallbackPauseText(labels[i], new Vector2(bounds.Center.X, bounds.Center.Y),
                Math.Clamp(virtualHeight * 0.022f, 16f, 24f), Color.White, centered: true);
        }

        var speed = layout.SpeedRow;
        _spriteBatch.Draw(_pixel, speed, _pauseSelection == 3 ? new Color(42, 39, 31, 255) : new Color(24, 25, 28, 255));
        var speedText = $"SCROLL SPEED     {_projection.ScrollSpeedScale:0.00}x     (LEFT / RIGHT)";
        DrawFallbackPauseText(speedText, new Vector2(speed.Center.X, speed.Center.Y),
            Math.Clamp(virtualHeight * 0.018f, 14f, 20f), _pauseSelection == 3 ? new Color(255, 222, 145) : new Color(215, 216, 220), centered: true);
        DrawFallbackPauseText("UP / DOWN: SELECT     CONFIRM: ACTIVATE     BACK: RESUME",
            new Vector2(layout.Panel.Center.X, layout.Panel.Bottom - Math.Max(18, (int)(virtualHeight * 0.025f))),
            Math.Clamp(virtualHeight * 0.014f, 11f, 16f), new Color(145, 148, 156), centered: true);
        _spriteBatch.End();
    }

    private void DrawFallbackPauseText(string text, Vector2 position, float targetHeight, Color color, bool centered)
    {
        var measured = UnicodeTextRenderer.MeasureString(_font, text);
        if (measured.X <= 0 || measured.Y <= 0)
            return;
        var scale = targetHeight / measured.Y;
        var origin = centered ? measured * 0.5f : Vector2.Zero;
        UnicodeTextRenderer.DrawString(_spriteBatch, _font, text, position, color, 0f, origin, scale, SpriteEffects.None, 0f);
    }

    private static (int Width, int Height) GetVirtualResolution(int outputWidth, int outputHeight)
    {
        var height = Math.Max(1, (int)MathF.Round(ChartProjection.ReferenceResolutionHeight));
        var safeOutputHeight = Math.Max(1, outputHeight);
        var aspect = Math.Max(0.1f, outputWidth / (float)safeOutputHeight);
        var width = Math.Max(1, (int)MathF.Round(height * aspect));
        return (width, height);
    }

    private void PrepareEditorInactiveBankLayer(
        double currentTick, GearState currentState, int width, int height)
    {
        if (_gameplayRenderTarget is null)
            return;

        if (_editorInactiveBankRenderTarget is null ||
            _editorInactiveBankRenderTarget.Width != width ||
            _editorInactiveBankRenderTarget.Height != height)
        {
            _editorInactiveBankRenderTarget?.Dispose();
            _editorInactiveBankRenderTarget = new RenderTarget2D(
                _graphics,
                width,
                height,
                false,
                SurfaceFormat.Color,
                DepthFormat.None,
                0,
                RenderTargetUsage.DiscardContents);
        }

        var mainViewport = _graphics.Viewport;
        var target = _editorInactiveBankRenderTarget;
        _graphics.SetRenderTarget(target);
        _graphics.Viewport = new Viewport(0, 0, width, height);
        _graphics.Clear(Color.Transparent);
        _editorActiveBanksOnlyPass = false;
        _editorInactiveBankLayerPass = true;
        try
        {
            // Compose lane surfaces, lane highlights, transition flashes, note
            // heads, bodies, link connectors, and note overlays into the same image.
            DrawPlayfield();
            DrawLaneHighlights();
            DrawTransitionFlashes();
            // Judge textures belong to the same composite as each bank surface
            // and its notes, so the inactive-bank opacity is applied exactly once.
            DrawJudgementLine(currentTick);
            DrawNotes(currentTick, currentState);
        }
        finally
        {
            _editorInactiveBankLayerPass = false;
            _editorActiveBanksOnlyPass = false;
            _graphics.SetRenderTarget(_gameplayRenderTarget);
            _graphics.Viewport = mainViewport;
        }

    }

    private void CompositeEditorInactiveBankLayer(int width, int height, float opacity)
    {
        if (_editorInactiveBankRenderTarget is null || opacity <= 0.001f)
            return;

        // The isolated layer has already resolved all texture overlap internally;
        // apply inactiveBankOpacity once to the finished image.
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);
        _spriteBatch.Draw(_editorInactiveBankRenderTarget,
            new Rectangle(0, 0, width, height), Color.White * opacity);
        _spriteBatch.End();
    }

    private void EnsureGameplayRenderTarget(int width, int height)
    {
        // The connector preview renders to a transparent overlay before blending
        // it back. Preserve the in-progress scene while that overlay target is bound;
        // otherwise keep the faster discard-contents path used elsewhere.
        var preserveContents = _chartEditorMode && !_editorPreviewMode &&
                               _chart is not null && _editorConnectorMode;
        if (_gameplayRenderTarget is not null &&
            _gameplayRenderTarget.Width == width &&
            _gameplayRenderTarget.Height == height &&
            _gameplayRenderTargetPreservesContents == preserveContents)
            return;

        _gameplayRenderTarget?.Dispose();
        _gameplayRenderTarget = new RenderTarget2D(
            _graphics,
            width,
            height,
            false,
            SurfaceFormat.Color,
            DepthFormat.None,
            0,
            preserveContents ? RenderTargetUsage.PreserveContents : RenderTargetUsage.DiscardContents);
        _gameplayRenderTargetPreservesContents = preserveContents;
    }

    private void WarmFeedbackRenderingIfNeeded()
    {
        if (_feedbackRenderWarmupDone)
            return;

        // Hit textures are preloaded at chart/skin load, but the first SpriteBatch
        // submission can still initialize its internal batch/shader state. Do that
        // before gameplay reaches the first judgement, so the audio/hit moment is
        // not the first time this rendering path is exercised.
        var textures = new List<Texture2D>();
        foreach (var (variant, frameCount) in new[]
                 {
                     ("regular", _skin.RenderConfig.HitAnimation.Regular.FrameCount),
                     ("multi", _skin.RenderConfig.HitAnimation.Multi.FrameCount),
                     ("long", _skin.RenderConfig.HitAnimation.Long.FrameCount),
                     ("superFlickOuter", _skin.RenderConfig.HitAnimation.SuperFlick.Outer.FrameCount),
                     ("superFlickInner", _skin.RenderConfig.HitAnimation.SuperFlick.Inner.FrameCount)
                 })
        {
            for (var frame = 1; frame <= Math.Max(0, frameCount); frame++)
            {
                var texture = _textures.GetHitFrame(variant, frame);
                if (texture is not null)
                    textures.Add(texture);
            }
        }

        for (var digit = 0; digit <= 9; digit++)
        {
            var texture = _textures.GetComboDigit(digit);
            if (texture is not null)
                textures.Add(texture);
        }

        foreach (var key in new[]
                 {
                     "judgement-perfect-plus", "judgement-perfect",
                     "judgement-good", "judgement-miss"
                 })
        {
            var texture = _textures.Get(key);
            if (texture is not null)
                textures.Add(texture);
        }

        if (textures.Count > 0)
        {
            _spriteBatch.Begin(
                samplerState: SamplerState.LinearClamp,
                blendState: BlendState.NonPremultiplied);
            foreach (var texture in textures)
            {
                _spriteBatch.Draw(texture, new Vector2(-10000f, -10000f), null,
                    Color.Transparent, 0f, Vector2.Zero, 0.001f,
                    SpriteEffects.None, 0f);
            }
            _spriteBatch.End();
        }

        // Prime the reusable audio voices. The WavSoundEffect instances are
        // allocated up front, so a real hit no longer needs to create a voice.
        _hitSounds.Warmup();

        // The first judgement can also be the first execution of a whole group of
        // feedback methods. Exercise those paths once now, while we can discard
        // the result, instead of making the first real hit pay the JIT/allocation
        // cost.
        PrepareFeedbackMethods();

        if (_chart is not null && _tempo is not null && _gear is not null)
        {
            var primeEvent = _orderedRenderableEvents.FirstOrDefault(e => !e.IsSuperFlick)
                             ?? _orderedRenderableEvents.FirstOrDefault();
            if (primeEvent is not null)
            {
                var savedAnimations = _hitAnimations.ToArray();
                var savedCombo = _combo;
                var savedJudgement = _lastJudgement;
                var savedJudgementColor = _lastJudgementColor;
                var savedJudgementUiTimer = _judgementUiTimer;
                var savedComboUiTimer = _comboUiTimer;
                var savedComboPulseTimer = _comboPulseTimer;
                var savedJudgementPulseTimer = _judgementPulseTimer;
                var savedEarlyLateUiTimer = _earlyLateUiTimer;
                var savedEarlyLatePulseTimer = _earlyLatePulseTimer;
                var savedTimingOffsetUiTimer = _timingOffsetUiTimer;
                var savedTimingOffsetPulseTimer = _timingOffsetPulseTimer;
                var savedLastTimingOffsetMs = _lastTimingOffsetMs;
                var savedHasTimingOffset = _hasTimingOffset;
                var savedHitDeltaSamples = _hitDeltaSamples.ToArray();

                _hitAnimations.Clear();
                _combo = 1;
                _lastJudgement = "PERFECT";
                _lastJudgementColor = Color.Yellow;
                TriggerComboPulse();
                StartJudgementUiLifetime();
                StartTimingUi(1.0, JudgementTier.Perfect);
                RecordHitDelta(1.0, JudgementTier.Perfect);
                StartHitAnimation(primeEvent);

                DrawHitAnimations();
                DrawHud(RenderGearState(_tempo.SecondsToTick(_playSeconds)),
                    _tempo.SecondsToTick(_playSeconds));

                _graphics.Clear(Background);

                _hitAnimations.Clear();
                _hitAnimations.AddRange(savedAnimations);
                _combo = savedCombo;
                _lastJudgement = savedJudgement;
                _lastJudgementColor = savedJudgementColor;
                _judgementUiTimer = savedJudgementUiTimer;
                _comboUiTimer = savedComboUiTimer;
                _comboPulseTimer = savedComboPulseTimer;
                _judgementPulseTimer = savedJudgementPulseTimer;
                _earlyLateUiTimer = savedEarlyLateUiTimer;
                _earlyLatePulseTimer = savedEarlyLatePulseTimer;
                _timingOffsetUiTimer = savedTimingOffsetUiTimer;
                _timingOffsetPulseTimer = savedTimingOffsetPulseTimer;
                _lastTimingOffsetMs = savedLastTimingOffsetMs;
                _hasTimingOffset = savedHasTimingOffset;
                _hitDeltaSamples.Clear();
                _hitDeltaSamples.AddRange(savedHitDeltaSamples);
            }
        }

        _feedbackRenderWarmupDone = true;
    }

    private static void PrepareFeedbackMethods()
    {
        foreach (var name in new[]
                 {
                     nameof(RegisterHit), nameof(PlayHitSound), nameof(ApplyJudgement),
                     nameof(StartHitAnimation), nameof(TriggerComboPulse), nameof(StartJudgementUiLifetime),
                     nameof(StartTimingUi), nameof(RecordHitDelta), nameof(DrawHitAnimations),
                     nameof(DrawHud), nameof(DrawComboSprite), nameof(DrawJudgementSprite),
                     nameof(DrawTimingFeedbackUi), nameof(DrawHitDeltaBar), nameof(GetHitAnimationFrame)
                 })
        {
            var method = typeof(Playfield).GetMethod(
                name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic);
            if (method is not null)
                RuntimeHelpers.PrepareMethod(method.MethodHandle);
        }
    }

    private (int First, int Last) GetGameplayEventRange(double currentSeconds)
    {
        // Gameplay only needs the timing window plus a tiny margin. Events farther
        // away cannot be hit this frame and will be resolved when their turn comes.
        const double lookBehind = 0.30;
        const double lookAhead = 0.150 + 0.01;
        var first = LowerBound(_eventSeconds, currentSeconds - lookBehind);
        var last = UpperBound(_eventSeconds, currentSeconds + lookAhead);
        return (Math.Max(0, first - 1), Math.Min(_orderedRenderableEvents.Count, last + 1));
    }

    private void DrawPlayfield()
    {
        var currentTick = _tempo?.SecondsToTick(_playSeconds) ?? 0;
        var state = RenderGearState(currentTick);
        var laneTexture = _textures.Get("lane");
        var lineTexture = _textures.Get("line");
        if (laneTexture is null && lineTexture is null)
            return;

        var signature = ComputeLaneMeshCacheSignature(state);
        if (_laneMeshCacheSignature != signature)
            InvalidateLaneMeshCache();
        _laneMeshCacheSignature = signature;

        var laneCommands = _laneCommandsScratch;
        var lineCommands = _lineCommandsScratch;
        laneCommands.Clear();
        lineCommands.Clear();

        foreach (var bank in VisibleBanks(state))
        {
            if (!ShouldRenderBankInCurrentEditorPass(bank.Side, bank.Bank, state))
                continue;

            var isTransitionBank = _bankSwitchTransitions.TryGetValue(bank.Side, out var transition) &&
                                   transition.Active &&
                                   string.Equals(transition.Bank, bank.Bank, StringComparison.OrdinalIgnoreCase);

            if (!isTransitionBank && _laneBankMeshCache.TryGetValue((bank.Side, bank.Bank), out var cached))
            {
                laneCommands.AddRange(cached.Lane);
                lineCommands.AddRange(cached.Line);
                continue;
            }

            var built = BuildLaneBankMeshes(bank.Side, bank.Bank, laneTexture, lineTexture);
            laneCommands.AddRange(built.Lane);
            lineCommands.AddRange(built.Line);

            // Never overwrite a neutral cached bank with a transition frame.
            // Once the transition is finished, the existing neutral cache is valid
            // again and the expensive dynamic geometry disappears automatically.
            if (!isTransitionBank)
                _laneBankMeshCache[(bank.Side, bank.Bank)] = built;
        }

        _graphics.BlendState = BlendState.AlphaBlend;
        _graphics.RasterizerState = RasterizerState.CullNone;
        DrawLaneTextureMeshes(laneCommands, debugOrbitKind: 1, state: state);
        DrawLaneTextureMeshes(lineCommands, debugOrbitKind: 2, state: state);
    }

    private (List<LaneTextureMeshCommand> Lane, List<LaneTextureMeshCommand> Line) BuildLaneBankMeshes(
        string side,
        string bank,
        Texture2D? laneTexture,
        Texture2D? lineTexture)
    {
        var laneCommands = new List<LaneTextureMeshCommand>(3);
        var lineCommands = new List<LaneTextureMeshCommand>(3);
        // The lane surface itself owns the behind-camera extension. Both the
        // 3D and legacy paths use the configured LaneEndLength; the
        // 3D path carries that near depth into its physical 3D bank surface.
        var nearLaneDepth = _projection.DepthForExtendedLaneNear(side, bank);
        var columns = ActiveColumns(side, bank);

        foreach (var column in Columns)
        {
            var activeColumn = columns.Contains(column);
            var center = LaneColumnCenter(side, bank, column);
            if (TryGetTrackForBankColumn(side, bank, column, out var laneTrack))
                center = _projection.LaneNormalizedX(laneTrack);

            var columnOpacity = activeColumn
                ? 1f
                : Math.Clamp(_projection.OuterTrackOpacity, 0f, 1f);

            // Decorative outer columns in 1K and 2K banks keep their lane surface
            // and lane line, but both are dimmed by the same configured opacity.
            if (lineTexture is not null)
            {
                var halfWidth = LaneColumnHalfWidth(side, bank);
                lineCommands.Add(BuildLaneLineTextureMesh(
                    lineTexture,
                    side,
                    center,
                    nearLaneDepth,
                    halfWidth,
                    bank,
                    _projection.LaneLineNearWidth,
                    _projection.LaneLineFarWidth) with { Opacity = columnOpacity });
            }

            if (laneTexture is not null)
            {
                var laneWidthRatio = _projection.LaneTrackWidth <= 0.001f ? 1f : _projection.LaneTextureWidth / _projection.LaneTrackWidth;
                var halfWidth = LaneColumnHalfWidth(side, bank) * laneWidthRatio;
                laneCommands.Add(BuildLaneTextureMesh(laneTexture, side, center, nearLaneDepth, halfWidth, bank)
                    with { Opacity = columnOpacity });
            }
        }

        return (laneCommands, lineCommands);
    }
}
