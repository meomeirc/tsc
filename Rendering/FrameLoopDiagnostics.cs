using System.Diagnostics;
using Microsoft.Xna.Framework;

namespace TSC.Rendering;

/// <summary>
/// Host-level frame timing diagnostics. These measurements cover Game1.Update
/// and Game1.Draw, which lets the gameplay overlay distinguish scene rendering
/// cost from update work and framework/presentation pacing.
/// </summary>
internal static class FrameLoopDiagnostics
{
    internal enum UpdateStage
    {
        InputAndPlatform,
        Transition,
        Gameplay,
        Result,
        Startup
    }

    // Keep late-frame reporting aligned with TSC's configured fixed-step rate.
    // A fixed 20 ms threshold silently misses missed frames at the default 60 FPS.
    public static double TargetFrameIntervalMs { get; private set; } = 1000.0 / 60.0;
    public static double LateFrameThresholdMs => TargetFrameIntervalMs + 0.5;

    public static void SetTargetFrameRate(int framesPerSecond)
    {
        var safeRate = Math.Clamp(framesPerSecond, 1, 1000);
        TargetFrameIntervalMs = 1000.0 / safeRate;
    }

    private static long _updateStartTimestamp;
    private static long _updateStartAllocatedBytes;
    private static int _updateStartGc0;
    private static int _updateStartGc1;
    private static int _updateStartGc2;
    private static double _updateElapsedGameMs;
    private static bool _updateWasRunningSlowly;
    private static long _updateCoreStartTimestamp;
    private static double _updateCpuMsSinceDraw;
    private static double _updateCoreCpuMsSinceDraw;
    private static double _updateInputAndPlatformCpuMsSinceDraw;
    private static double _updateTransitionCpuMsSinceDraw;
    private static double _updateGameplayCpuMsSinceDraw;
    private static double _updateResultCpuMsSinceDraw;
    private static double _updateStartupCpuMsSinceDraw;

    private static long _drawStartTimestamp;
    private static long _drawStartAllocatedBytes;
    private static int _drawStartGc0;
    private static int _drawStartGc1;
    private static int _drawStartGc2;
    private static long _previousDrawStartTimestamp;
    private static double _drawCpuMsIncludedInCurrentInterval;
    private static TimeSpan _previousDrawTotalGcPauseDuration;

    private static long _updateWindowStartTimestamp;
    private static double _updateMaxMsInWindow;
    private static double _completedUpdateMaxMs;
    private static long _drawWindowStartTimestamp;
    private static double _drawMaxMsInWindow;
    private static double _completedDrawMaxMs;

    private static int _updatesSinceDraw;
    private static bool _runningSlowlySinceDraw;
    private static double _maxUpdateMsSinceDraw;
    private static long _updateAllocatedBytesSinceDraw;
    private static int _updateGc0SinceDraw;
    private static int _updateGc1SinceDraw;
    private static int _updateGc2SinceDraw;

    public static double LastUpdateCpuMs { get; private set; }
    public static double LastUpdateCoreCpuMs { get; private set; }
    public static double MaxUpdateCpuMsOneSecond => Math.Max(_completedUpdateMaxMs, _updateMaxMsInWindow);
    public static double LastUpdateElapsedGameMs { get; private set; }
    public static double MaxUpdateCpuMsSinceLastDraw { get; private set; }
    public static int UpdateCallsSinceLastDraw { get; private set; }
    public static bool RunningSlowlySinceLastDraw { get; private set; }
    public static long UpdateAllocatedBytesSinceLastDraw { get; private set; }
    public static int UpdateGc0SinceLastDraw { get; private set; }
    public static int UpdateGc1SinceLastDraw { get; private set; }
    public static int UpdateGc2SinceLastDraw { get; private set; }
    public static double UpdateCpuMsSinceLastDraw { get; private set; }
    public static double UpdateCoreCpuMsSinceLastDraw { get; private set; }
    public static double UpdateNonCoreCpuMsSinceLastDraw { get; private set; }
    public static double UpdateInputAndPlatformCpuMsSinceLastDraw { get; private set; }
    public static double UpdateTransitionCpuMsSinceLastDraw { get; private set; }
    public static double UpdateGameplayCpuMsSinceLastDraw { get; private set; }
    public static double UpdateResultCpuMsSinceLastDraw { get; private set; }
    public static double UpdateStartupCpuMsSinceLastDraw { get; private set; }
    public static double UpdateOtherCoreCpuMsSinceLastDraw { get; private set; }

    public static double LastDrawCpuMs { get; private set; }
    public static double MaxDrawCpuMsOneSecond => Math.Max(_completedDrawMaxMs, _drawMaxMsInWindow);
    public static double LastDrawIntervalMs { get; private set; }
    public static double PreviousDrawCpuMsInLastInterval { get; private set; }
    /// <summary>
    /// Estimated time between Game1 callbacks that was not spent inside the
    /// preceding Draw or the Update calls between two Draw starts. This can
    /// include platform presentation, VSync waits, framework scheduling and
    /// other work outside Game1.Update/Game1.Draw; it is not GPU execution time.
    /// </summary>
    public static double UnaccountedDrawIntervalMs { get; private set; }
    /// <summary>
    /// Runtime-reported GC pause accumulated between consecutive Draw starts.
    /// This is process-wide pause time, not only collections initiated by the game thread.
    /// </summary>
    public static double GcPauseMsSinceLastDraw { get; private set; }
    public static long LastDrawAllocatedBytes { get; private set; }
    public static int LastDrawGc0 { get; private set; }
    public static int LastDrawGc1 { get; private set; }
    public static int LastDrawGc2 { get; private set; }

    public static void BeginUpdate(GameTime gameTime)
    {
        _updateStartTimestamp = Stopwatch.GetTimestamp();
        _updateStartAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
        _updateStartGc0 = GC.CollectionCount(0);
        _updateStartGc1 = GC.CollectionCount(1);
        _updateStartGc2 = GC.CollectionCount(2);
        _updateElapsedGameMs = gameTime.ElapsedGameTime.TotalMilliseconds;
        _updateWasRunningSlowly = gameTime.IsRunningSlowly;

        _updatesSinceDraw++;
        _runningSlowlySinceDraw |= gameTime.IsRunningSlowly;
    }

    public static void BeginUpdateCore()
    {
        _updateCoreStartTimestamp = Stopwatch.GetTimestamp();
    }

    public static void EndUpdateCore()
    {
        if (_updateCoreStartTimestamp == 0)
            return;

        LastUpdateCoreCpuMs = ElapsedMilliseconds(_updateCoreStartTimestamp, Stopwatch.GetTimestamp());
        _updateCoreCpuMsSinceDraw += LastUpdateCoreCpuMs;
        _updateCoreStartTimestamp = 0;
    }

    public static long BeginStage() => Stopwatch.GetTimestamp();

    public static void EndUpdateStage(UpdateStage stage, long startTimestamp)
    {
        if (startTimestamp == 0)
            return;

        var elapsedMs = ElapsedMilliseconds(startTimestamp, Stopwatch.GetTimestamp());
        switch (stage)
        {
            case UpdateStage.InputAndPlatform:
                _updateInputAndPlatformCpuMsSinceDraw += elapsedMs;
                break;
            case UpdateStage.Transition:
                _updateTransitionCpuMsSinceDraw += elapsedMs;
                break;
            case UpdateStage.Gameplay:
                _updateGameplayCpuMsSinceDraw += elapsedMs;
                break;
            case UpdateStage.Result:
                _updateResultCpuMsSinceDraw += elapsedMs;
                break;
            case UpdateStage.Startup:
                _updateStartupCpuMsSinceDraw += elapsedMs;
                break;
        }
    }

    public static void EndUpdate()
    {
        var now = Stopwatch.GetTimestamp();
        var cpuMs = ElapsedMilliseconds(_updateStartTimestamp, now);
        var allocatedBytes = Math.Max(0L, GC.GetAllocatedBytesForCurrentThread() - _updateStartAllocatedBytes);
        var gc0 = Math.Max(0, GC.CollectionCount(0) - _updateStartGc0);
        var gc1 = Math.Max(0, GC.CollectionCount(1) - _updateStartGc1);
        var gc2 = Math.Max(0, GC.CollectionCount(2) - _updateStartGc2);

        LastUpdateCpuMs = cpuMs;
        _updateCpuMsSinceDraw += cpuMs;
        LastUpdateElapsedGameMs = _updateElapsedGameMs;
        _maxUpdateMsSinceDraw = Math.Max(_maxUpdateMsSinceDraw, cpuMs);
        _updateAllocatedBytesSinceDraw += allocatedBytes;
        _updateGc0SinceDraw += gc0;
        _updateGc1SinceDraw += gc1;
        _updateGc2SinceDraw += gc2;
        RecordWindowMaximum(
            now,
            ref _updateWindowStartTimestamp,
            ref _updateMaxMsInWindow,
            ref _completedUpdateMaxMs,
            cpuMs);
    }

    public static void BeginDraw(GameTime gameTime)
    {
        _drawStartTimestamp = Stopwatch.GetTimestamp();
        _drawStartAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
        _drawStartGc0 = GC.CollectionCount(0);
        _drawStartGc1 = GC.CollectionCount(1);
        _drawStartGc2 = GC.CollectionCount(2);

        var totalGcPauseDuration = GC.GetTotalPauseDuration();
        if (_previousDrawStartTimestamp != 0)
        {
            LastDrawIntervalMs = ElapsedMilliseconds(_previousDrawStartTimestamp, _drawStartTimestamp);
            GcPauseMsSinceLastDraw = Math.Max(0.0, (totalGcPauseDuration - _previousDrawTotalGcPauseDuration).TotalMilliseconds);
            // The draw that began at the previous timestamp is part of this
            // draw-to-draw interval. Capture it before LastDrawCpuMs is replaced
            // by the current Draw measurement in EndDraw().
            _drawCpuMsIncludedInCurrentInterval = LastDrawCpuMs;
        }
        else
        {
            LastDrawIntervalMs = 0.0;
            GcPauseMsSinceLastDraw = 0.0;
            _drawCpuMsIncludedInCurrentInterval = 0.0;
        }
        _previousDrawTotalGcPauseDuration = totalGcPauseDuration;
        _previousDrawStartTimestamp = _drawStartTimestamp;
        PublishUpdateWindowMeasurements();
        _ = gameTime;
    }

    public static void EndDraw()
    {
        var now = Stopwatch.GetTimestamp();
        LastDrawCpuMs = ElapsedMilliseconds(_drawStartTimestamp, now);
        LastDrawAllocatedBytes = Math.Max(0L, GC.GetAllocatedBytesForCurrentThread() - _drawStartAllocatedBytes);
        LastDrawGc0 = Math.Max(0, GC.CollectionCount(0) - _drawStartGc0);
        LastDrawGc1 = Math.Max(0, GC.CollectionCount(1) - _drawStartGc1);
        LastDrawGc2 = Math.Max(0, GC.CollectionCount(2) - _drawStartGc2);

        RecordWindowMaximum(
            now,
            ref _drawWindowStartTimestamp,
            ref _drawMaxMsInWindow,
            ref _completedDrawMaxMs,
            LastDrawCpuMs);

    }

    private static void PublishUpdateWindowMeasurements()
    {
        MaxUpdateCpuMsSinceLastDraw = _maxUpdateMsSinceDraw;
        UpdateCallsSinceLastDraw = _updatesSinceDraw;
        RunningSlowlySinceLastDraw = _runningSlowlySinceDraw;
        UpdateAllocatedBytesSinceLastDraw = _updateAllocatedBytesSinceDraw;
        UpdateGc0SinceLastDraw = _updateGc0SinceDraw;
        UpdateGc1SinceLastDraw = _updateGc1SinceDraw;
        UpdateGc2SinceLastDraw = _updateGc2SinceDraw;
        UpdateCpuMsSinceLastDraw = _updateCpuMsSinceDraw;
        UpdateCoreCpuMsSinceLastDraw = _updateCoreCpuMsSinceDraw;
        UpdateNonCoreCpuMsSinceLastDraw = Math.Max(0.0, _updateCpuMsSinceDraw - _updateCoreCpuMsSinceDraw);
        UpdateInputAndPlatformCpuMsSinceLastDraw = _updateInputAndPlatformCpuMsSinceDraw;
        UpdateTransitionCpuMsSinceLastDraw = _updateTransitionCpuMsSinceDraw;
        UpdateGameplayCpuMsSinceLastDraw = _updateGameplayCpuMsSinceDraw;
        UpdateResultCpuMsSinceLastDraw = _updateResultCpuMsSinceDraw;
        UpdateStartupCpuMsSinceLastDraw = _updateStartupCpuMsSinceDraw;
        var classifiedUpdateCoreMs = _updateInputAndPlatformCpuMsSinceDraw
            + _updateTransitionCpuMsSinceDraw
            + _updateGameplayCpuMsSinceDraw
            + _updateResultCpuMsSinceDraw
            + _updateStartupCpuMsSinceDraw;
        UpdateOtherCoreCpuMsSinceLastDraw = Math.Max(0.0, _updateCoreCpuMsSinceDraw - classifiedUpdateCoreMs);
        PreviousDrawCpuMsInLastInterval = _drawCpuMsIncludedInCurrentInterval;
        UnaccountedDrawIntervalMs = LastDrawIntervalMs > 0.0
            ? Math.Max(0.0, LastDrawIntervalMs - PreviousDrawCpuMsInLastInterval - UpdateCpuMsSinceLastDraw)
            : 0.0;

        _updatesSinceDraw = 0;
        _runningSlowlySinceDraw = false;
        _maxUpdateMsSinceDraw = 0.0;
        _updateAllocatedBytesSinceDraw = 0;
        _updateGc0SinceDraw = 0;
        _updateGc1SinceDraw = 0;
        _updateGc2SinceDraw = 0;
        _updateCpuMsSinceDraw = 0.0;
        _updateCoreCpuMsSinceDraw = 0.0;
        _updateInputAndPlatformCpuMsSinceDraw = 0.0;
        _updateTransitionCpuMsSinceDraw = 0.0;
        _updateGameplayCpuMsSinceDraw = 0.0;
        _updateResultCpuMsSinceDraw = 0.0;
        _updateStartupCpuMsSinceDraw = 0.0;
    }

    private static double ElapsedMilliseconds(long startTimestamp, long endTimestamp)
        => startTimestamp == 0 ? 0.0 : (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;

    private static void RecordWindowMaximum(
        long now,
        ref long windowStartTimestamp,
        ref double maxMsInWindow,
        ref double completedMaxMs,
        double sampleMs)
    {
        if (windowStartTimestamp == 0)
        {
            windowStartTimestamp = now;
            maxMsInWindow = sampleMs;
            return;
        }

        maxMsInWindow = Math.Max(maxMsInWindow, sampleMs);
        if (now - windowStartTimestamp < Stopwatch.Frequency)
            return;

        completedMaxMs = maxMsInWindow;
        windowStartTimestamp = now;
        maxMsInWindow = sampleMs;
    }
}
