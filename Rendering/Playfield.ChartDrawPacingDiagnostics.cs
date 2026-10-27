using System.Diagnostics;
using TSC.Ares;

namespace TSC.Rendering;

public sealed partial class Playfield
{
    // Compares the chart clock movement between consecutive Draw calls against
    // the actual wall-clock interval between those calls. This detects a chart
    // clock that advances in uneven steps even when the draw cadence looks steady.
    private bool _debugChartPacingInitialized;
    private double _debugChartPacingLastDrawSeconds;
    private bool _debugChartPacingWasPlaying;
    private double _debugChartDrawDeltaMs;
    private double _debugChartDrawDeltaErrorMs;

    private long _debugChartPacingWindowStartTimestamp;
    private int _debugChartPacingSamplesInWindow;
    private int _debugChartPacingSamplesLastWindow;
    private int _debugChartPacingSameTimeFramesInWindow;
    private int _debugChartPacingMultiStepFramesInWindow;
    private double _debugChartPacingMaxAbsErrorMsInWindow;

    private int _debugChartPacingSameTimeFramesLastWindow;
    private int _debugChartPacingMultiStepFramesLastWindow;
    private double _debugChartPacingMaxAbsErrorMsLastWindow;


    // Tracks one ordinary note head through screen space. Clock deltas can be
    // perfectly regular while the projected note position still jumps, so this
    // measures the actual motion produced by the same 3D basis used for drawing.
    private ChartEvent? _debugChartMotionAnchorEvent;
    private int _debugChartMotionAnchorTrack = -1;
    private int _debugChartMotionAnchorTick = -1;
    private bool _debugChartMotionAnchorSampleFound;
    private bool _debugChartMotionPositionInitialized;
    private double _debugChartMotionLastY;
    private double _debugChartMotionLastDeltaY;
    private double _debugChartMotionLastSecondDifferenceY;
    private double _debugChartMotionDeltaY;
    private double _debugChartMotionSecondDifferenceY;
    private double _debugChartMotionJerkY;
    private long _debugChartMotionWindowStartTimestamp;
    private double _debugChartMotionMaxAbsJerkInWindow;
    private double _debugChartMotionMaxAbsJerkLastWindow;

    private void BeginChartMotionSample()
    {
        _debugChartMotionAnchorSampleFound = false;
    }

    private void UpdateChartMotionSample(ChartEvent ev, double screenY)
    {
        // Choose the first future ordinary note that is actually traversing the
        // rendered path, then follow that exact event until it leaves the window.
        if (_debugChartMotionAnchorEvent is null)
        {
            _debugChartMotionAnchorEvent = ev;
            _debugChartMotionAnchorTrack = ev.Track;
            _debugChartMotionAnchorTick = ev.Tick;
            _debugChartMotionPositionInitialized = false;
            _debugChartMotionWindowStartTimestamp = Stopwatch.GetTimestamp();
            _debugChartMotionMaxAbsJerkInWindow = 0.0;
            _debugChartMotionMaxAbsJerkLastWindow = 0.0;
        }

        if (!ReferenceEquals(_debugChartMotionAnchorEvent, ev))
            return;

        _debugChartMotionAnchorSampleFound = true;
        var now = Stopwatch.GetTimestamp();
        if (!_debugChartMotionPositionInitialized)
        {
            _debugChartMotionPositionInitialized = true;
            _debugChartMotionLastY = screenY;
            _debugChartMotionLastDeltaY = 0.0;
            _debugChartMotionLastSecondDifferenceY = 0.0;
            _debugChartMotionDeltaY = 0.0;
            _debugChartMotionSecondDifferenceY = 0.0;
            _debugChartMotionJerkY = 0.0;
            return;
        }

        // First, second and third finite differences. Smooth perspective causes
        // gradual velocity changes; sudden spikes in the third difference are a
        // stronger sign of an irregular projected step than raw velocity alone.
        var deltaY = screenY - _debugChartMotionLastY;
        var secondDifferenceY = deltaY - _debugChartMotionLastDeltaY;
        var jerkY = secondDifferenceY - _debugChartMotionLastSecondDifferenceY;
        _debugChartMotionDeltaY = deltaY;
        _debugChartMotionSecondDifferenceY = secondDifferenceY;
        _debugChartMotionJerkY = jerkY;
        _debugChartMotionLastY = screenY;
        _debugChartMotionLastDeltaY = deltaY;
        _debugChartMotionLastSecondDifferenceY = secondDifferenceY;

        _debugChartMotionMaxAbsJerkInWindow = Math.Max(
            _debugChartMotionMaxAbsJerkInWindow, Math.Abs(jerkY));

        if (_debugChartMotionWindowStartTimestamp == 0 ||
            now - _debugChartMotionWindowStartTimestamp >= Stopwatch.Frequency)
        {
            _debugChartMotionMaxAbsJerkLastWindow = _debugChartMotionMaxAbsJerkInWindow;
            _debugChartMotionMaxAbsJerkInWindow = 0.0;
            _debugChartMotionWindowStartTimestamp = now;
        }
    }

    private void EndChartMotionSample()
    {
        if (_debugChartMotionAnchorSampleFound)
            return;

        // The tracked note was culled or passed the judgement line. Select a new
        // anchor on a following frame rather than comparing two different notes.
        _debugChartMotionAnchorEvent = null;
        _debugChartMotionAnchorTrack = -1;
        _debugChartMotionAnchorTick = -1;
        _debugChartMotionPositionInitialized = false;
        _debugChartMotionDeltaY = 0.0;
        _debugChartMotionSecondDifferenceY = 0.0;
        _debugChartMotionJerkY = 0.0;
        _debugChartMotionMaxAbsJerkInWindow = 0.0;
        _debugChartMotionMaxAbsJerkLastWindow = 0.0;
        _debugChartMotionWindowStartTimestamp = 0;
    }

    private void UpdateChartDrawPacingDiagnostics(long drawTimestamp)
    {
        if (!_debugChartPacingInitialized)
        {
            _debugChartPacingInitialized = true;
            _debugChartPacingLastDrawSeconds = _playSeconds;
            _debugChartPacingWasPlaying = _playing;
            _debugChartPacingWindowStartTimestamp = drawTimestamp;
            return;
        }

        // A large forward/backward jump is likely a user seek or chart reset, not
        // normal scrolling. Keep its raw delta visible, but do not count it as a
        // pacing fault in the one-second playback statistics.
        _debugChartDrawDeltaMs = (_playSeconds - _debugChartPacingLastDrawSeconds) * 1000.0;
        _debugChartDrawDeltaErrorMs = _debugChartDrawDeltaMs - _debugDrawIntervalMs;

        var isPlausibleContinuousPlayback =
            _playing &&
            _debugChartPacingWasPlaying &&
            _debugDrawIntervalMs > 0.1 &&
            _debugChartDrawDeltaMs >= -0.1 &&
            _debugChartDrawDeltaMs <= Math.Max(250.0, _debugDrawIntervalMs * 8.0);

        if (isPlausibleContinuousPlayback)
        {
            _debugChartPacingSamplesInWindow++;

            // With matching 60 Hz update/draw cadence the values should be close.
            // A low chart delta means a repeated chart position; a high delta means
            // the chart jumped farther than the intervening draw interval suggests.
            if (_debugChartDrawDeltaMs < _debugDrawIntervalMs * 0.5)
                _debugChartPacingSameTimeFramesInWindow++;
            else if (_debugChartDrawDeltaMs > _debugDrawIntervalMs * 1.5)
                _debugChartPacingMultiStepFramesInWindow++;

            _debugChartPacingMaxAbsErrorMsInWindow = Math.Max(
                _debugChartPacingMaxAbsErrorMsInWindow,
                Math.Abs(_debugChartDrawDeltaErrorMs));
        }

        if (drawTimestamp - _debugChartPacingWindowStartTimestamp >= Stopwatch.Frequency)
        {
            _debugChartPacingSamplesLastWindow = _debugChartPacingSamplesInWindow;
            _debugChartPacingSameTimeFramesLastWindow = _debugChartPacingSameTimeFramesInWindow;
            _debugChartPacingMultiStepFramesLastWindow = _debugChartPacingMultiStepFramesInWindow;
            _debugChartPacingMaxAbsErrorMsLastWindow = _debugChartPacingMaxAbsErrorMsInWindow;

            _debugChartPacingWindowStartTimestamp = drawTimestamp;
            _debugChartPacingSamplesInWindow = 0;
            _debugChartPacingSameTimeFramesInWindow = 0;
            _debugChartPacingMultiStepFramesInWindow = 0;
            _debugChartPacingMaxAbsErrorMsInWindow = 0.0;
        }

        _debugChartPacingLastDrawSeconds = _playSeconds;
        _debugChartPacingWasPlaying = _playing;
    }
}
