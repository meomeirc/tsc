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
    private static long _updateStartTimestamp;
    private static long _updateStartAllocatedBytes;
    private static int _updateStartGc0;
    private static int _updateStartGc1;
    private static int _updateStartGc2;
    private static double _updateElapsedGameMs;
    private static bool _updateWasRunningSlowly;

    private static long _drawStartTimestamp;
    private static long _drawStartAllocatedBytes;
    private static int _drawStartGc0;
    private static int _drawStartGc1;
    private static int _drawStartGc2;
    private static long _previousDrawStartTimestamp;

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
    public static double MaxUpdateCpuMsOneSecond => Math.Max(_completedUpdateMaxMs, _updateMaxMsInWindow);
    public static double LastUpdateElapsedGameMs { get; private set; }
    public static double MaxUpdateCpuMsSinceLastDraw { get; private set; }
    public static int UpdateCallsSinceLastDraw { get; private set; }
    public static bool RunningSlowlySinceLastDraw { get; private set; }
    public static long UpdateAllocatedBytesSinceLastDraw { get; private set; }
    public static int UpdateGc0SinceLastDraw { get; private set; }
    public static int UpdateGc1SinceLastDraw { get; private set; }
    public static int UpdateGc2SinceLastDraw { get; private set; }

    public static double LastDrawCpuMs { get; private set; }
    public static double MaxDrawCpuMsOneSecond => Math.Max(_completedDrawMaxMs, _drawMaxMsInWindow);
    public static double LastDrawIntervalMs { get; private set; }
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

    public static void EndUpdate()
    {
        var now = Stopwatch.GetTimestamp();
        var cpuMs = ElapsedMilliseconds(_updateStartTimestamp, now);
        var allocatedBytes = Math.Max(0L, GC.GetAllocatedBytesForCurrentThread() - _updateStartAllocatedBytes);
        var gc0 = Math.Max(0, GC.CollectionCount(0) - _updateStartGc0);
        var gc1 = Math.Max(0, GC.CollectionCount(1) - _updateStartGc1);
        var gc2 = Math.Max(0, GC.CollectionCount(2) - _updateStartGc2);

        LastUpdateCpuMs = cpuMs;
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

        if (_previousDrawStartTimestamp != 0)
            LastDrawIntervalMs = ElapsedMilliseconds(_previousDrawStartTimestamp, _drawStartTimestamp);
        _previousDrawStartTimestamp = _drawStartTimestamp;
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

        MaxUpdateCpuMsSinceLastDraw = _maxUpdateMsSinceDraw;
        UpdateCallsSinceLastDraw = _updatesSinceDraw;
        RunningSlowlySinceLastDraw = _runningSlowlySinceDraw;
        UpdateAllocatedBytesSinceLastDraw = _updateAllocatedBytesSinceDraw;
        UpdateGc0SinceLastDraw = _updateGc0SinceDraw;
        UpdateGc1SinceLastDraw = _updateGc1SinceDraw;
        UpdateGc2SinceLastDraw = _updateGc2SinceDraw;

        _updatesSinceDraw = 0;
        _runningSlowlySinceDraw = false;
        _maxUpdateMsSinceDraw = 0.0;
        _updateAllocatedBytesSinceDraw = 0;
        _updateGc0SinceDraw = 0;
        _updateGc1SinceDraw = 0;
        _updateGc2SinceDraw = 0;
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
