using System.Diagnostics;
using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using TSC;
using TSC.Ares;
using TSC.Input;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace TSC.Rendering;

public sealed partial class Playfield
{
    private bool _editorPreviewEditorOpen;
    // 0 = pick preview START, 1 = pick preview END from a snapped chart tick.
    private int _editorPreviewPendingChartTickTarget = -1;
    private bool _editorPreviewRestoreMetaPanelAfterChartTickPick;
    private bool _editorPreviewRangeInitialized;
    private double _editorPreviewStartSeconds;
    private double _editorPreviewEndSeconds;
    private string _editorPreviewStartText = "0.000";
    private string _editorPreviewEndText = "30.000";
    private int _editorPreviewFocusedField = -1; // 0 = start, 1 = end
    private int _editorPreviewStartCaret;
    private int _editorPreviewEndCaret;
    private bool _editorPreviewDraggingStart;
    private bool _editorPreviewDraggingEnd;
    private bool _editorPreviewDraggingPlayhead;
    private int _editorPreviewStartDragPointerOffsetX;
    private int _editorPreviewEndDragPointerOffsetX;
    private bool _editorPreviewWasPlayingBeforePlayheadDrag;
    private bool _editorPreviewDraggingTimelinePan;
    private int _editorPreviewTimelinePanLastX;
    private double _editorPreviewTimelineViewStartSeconds;
    private double _editorPreviewTimelineViewDurationSeconds;
    private double _editorPreviewTimelineTargetStartSeconds;
    private double _editorPreviewTimelineTargetDurationSeconds;
    private int _editorPreviewTimelineViewGeneration = -1;
    private bool _editorPreviewSelectionPlaying;
    private double _editorPreviewPlayheadSeconds;
    private long _editorPreviewSelectionPlaybackTimestamp;
    private float[]? _editorPreviewWaveformRms;
    // Normalized prefix sums let each displayed pixel average its source range
    // in O(1), even when the full 32K envelope is zoomed out to the screen width.
    private float[]? _editorPreviewWaveformRmsPrefix;
    private Task<float[]>? _editorPreviewWaveformTask;
    private float[]? _editorPreviewWaveformRenderSource;
    private float[]? _editorPreviewWaveformRenderBars;
    private int _editorPreviewWaveformRenderWidth;
    private int _editorPreviewWaveformRenderStep;
    private int _editorPreviewWaveformRenderFirstLevel;
    private int _editorPreviewWaveformRenderLastLevel;
    private int _editorPreviewWaveformGeneration = -1;
    private string? _editorPreviewDialogMessage;
    private byte[]? _editorSharedPreviewBytes;
    private OpusAudioPlayer? _editorPreviewPlayer;
    private OpusAudioPlayer? _editorPreviewSelectionPlayer;
    private double _editorPreviewPlaybackBufferStartSeconds;
    private double _editorPreviewPlaybackBufferFadeEndSeconds;
    private double _editorPreviewPlaybackSelectionStartSeconds;
    private long _editorPreviewPlaybackStartTimestamp;
    private double _editorPreviewPlaybackDurationSeconds;
    private double? _editorSavedPreviewDurationSeconds;

    private bool _editorPreviewAssetDirectoryCacheValid;
    private string? _editorPreviewAssetDirectoryCacheWorkingDirectory;
    private string? _editorPreviewAssetDirectoryCacheSourcePath;
    private string? _editorPreviewAssetDirectoryCacheResult;
    private bool _editorMediaResourceDirectoryCacheValid;
    private string? _editorMediaResourceDirectoryCacheWorkingDirectory;
    private string? _editorMediaResourceDirectoryCacheSourcePath;
    private string? _editorMediaResourceDirectoryCacheResult;
    private readonly Dictionary<EditorArtworkPathCacheKey, string?> _editorArtworkPathCache = new();
    private readonly Dictionary<EditorMediaResourceCacheKey, bool> _editorMediaResourceCache = new();

    private readonly record struct EditorArtworkPathCacheKey(
        string Field, string Directory, ChartBundleTemplate Template, string? AlbumPath, string? AlbumBigPath);
    private readonly record struct EditorMediaResourceCacheKey(
        string Field, string Directory, ChartBundleTemplate Template);

    private sealed class EditorArtworkTextureCache : IDisposable
    {
        public Texture2D? Texture { get; set; }
        public byte[]? SourceBytes { get; set; }
        public string? SourcePath { get; set; }
        public long SourceLength { get; set; }
        public DateTime SourceLastWriteUtc { get; set; }
        public bool IsMemorySource { get; set; }

        public void Reset()
        {
            Texture?.Dispose();
            Texture = null;
            SourceBytes = null;
            SourcePath = null;
            SourceLength = 0;
            SourceLastWriteUtc = default;
            IsMemorySource = false;
        }

        public void Dispose() => Reset();
    }

    private readonly EditorArtworkTextureCache _editorAlbumArtworkCache = new();
    private readonly EditorArtworkTextureCache _editorAlbumBigArtworkCache = new();

    private double EditorPreviewSourceDurationSeconds
        => _audio.IsLoaded && double.IsFinite(_audio.DurationSeconds)
            ? Math.Max(0, _audio.DurationSeconds)
            : 0;

    private double GetEditorPreviewTimelineMinimumDuration(double sourceDuration)
        => Math.Min(sourceDuration, Math.Max(0.25, sourceDuration / 32768.0 * 4.0));

    private void EnsureEditorPreviewTimelineView()
    {
        var duration = EditorPreviewSourceDurationSeconds;
        var generation = _audio.SourceGeneration;
        if (_editorPreviewTimelineViewGeneration != generation)
        {
            _editorPreviewTimelineViewGeneration = generation;
            _editorPreviewTimelineViewStartSeconds = 0;
            _editorPreviewTimelineViewDurationSeconds = duration;
            _editorPreviewTimelineTargetStartSeconds = 0;
            _editorPreviewTimelineTargetDurationSeconds = duration;
        }
        else if (duration > 0 && _editorPreviewTimelineViewDurationSeconds <= 0)
        {
            _editorPreviewTimelineViewStartSeconds = 0;
            _editorPreviewTimelineViewDurationSeconds = duration;
            _editorPreviewTimelineTargetStartSeconds = 0;
            _editorPreviewTimelineTargetDurationSeconds = duration;
        }

        if (duration <= 0)
            return;

        var minimumView = GetEditorPreviewTimelineMinimumDuration(duration);
        _editorPreviewTimelineViewDurationSeconds = Math.Clamp(
            _editorPreviewTimelineViewDurationSeconds, minimumView, duration);
        _editorPreviewTimelineViewStartSeconds = Math.Clamp(_editorPreviewTimelineViewStartSeconds, 0,
            Math.Max(0, duration - _editorPreviewTimelineViewDurationSeconds));
        _editorPreviewTimelineTargetDurationSeconds = Math.Clamp(
            _editorPreviewTimelineTargetDurationSeconds, minimumView, duration);
        _editorPreviewTimelineTargetStartSeconds = Math.Clamp(_editorPreviewTimelineTargetStartSeconds, 0,
            Math.Max(0, duration - _editorPreviewTimelineTargetDurationSeconds));
    }

    private double GetEditorPreviewTimelineViewStartSeconds()
    {
        EnsureEditorPreviewTimelineView();
        return _editorPreviewTimelineViewStartSeconds;
    }

    private double GetEditorPreviewTimelineViewDurationSeconds()
    {
        EnsureEditorPreviewTimelineView();
        return Math.Max(0, _editorPreviewTimelineViewDurationSeconds);
    }

    private void SetEditorPreviewTimelineView(double startSeconds, double viewDurationSeconds)
    {
        var duration = EditorPreviewSourceDurationSeconds;
        if (duration <= 0)
            return;

        var minimumView = GetEditorPreviewTimelineMinimumDuration(duration);
        var nextViewDuration = Math.Clamp(viewDurationSeconds, minimumView, duration);
        _editorPreviewTimelineTargetDurationSeconds = nextViewDuration;
        _editorPreviewTimelineTargetStartSeconds = Math.Clamp(startSeconds, 0,
            Math.Max(0, duration - nextViewDuration));
    }

    private void UpdateEditorPreviewTimelineMotion(double deltaSeconds)
    {
        if (EditorPreviewSourceDurationSeconds <= 0)
            return;

        EnsureEditorPreviewTimelineView();
        var elapsed = Math.Clamp(double.IsFinite(deltaSeconds) ? deltaSeconds : 0, 0, 0.1);
        if (elapsed <= 0)
            return;

        // Exponential damping is frame-rate independent and makes trackpad input
        // feel continuous without allowing the timeline to lag far behind the pointer.
        var blend = 1.0 - Math.Exp(-elapsed / 0.075);
        _editorPreviewTimelineViewStartSeconds +=
            (_editorPreviewTimelineTargetStartSeconds - _editorPreviewTimelineViewStartSeconds) * blend;
        _editorPreviewTimelineViewDurationSeconds +=
            (_editorPreviewTimelineTargetDurationSeconds - _editorPreviewTimelineViewDurationSeconds) * blend;

        if (Math.Abs(_editorPreviewTimelineTargetStartSeconds - _editorPreviewTimelineViewStartSeconds) < 0.0005 &&
            Math.Abs(_editorPreviewTimelineTargetDurationSeconds - _editorPreviewTimelineViewDurationSeconds) < 0.0005)
        {
            _editorPreviewTimelineViewStartSeconds = _editorPreviewTimelineTargetStartSeconds;
            _editorPreviewTimelineViewDurationSeconds = _editorPreviewTimelineTargetDurationSeconds;
        }
    }

    private void ZoomEditorPreviewTimeline(double wheelDelta, int pointerX, Rectangle pointerBounds, Rectangle timeline)
    {
        var duration = EditorPreviewSourceDurationSeconds;
        if (duration <= 0 || timeline.Width <= 0 || pointerBounds.Width <= 0 || Math.Abs(wheelDelta) < 0.0001)
            return;

        EnsureEditorPreviewTimelineView();
        var oldViewDuration = _editorPreviewTimelineTargetDurationSeconds;
        var pointerRatio = Math.Clamp((pointerX - pointerBounds.X) / (double)pointerBounds.Width, 0, 1);
        var anchorSeconds = _editorPreviewTimelineTargetStartSeconds + pointerRatio * oldViewDuration;
        // SDL supplies wheel movement in notch units, including fractional
        // high-resolution trackpad values. Do not divide by MouseState's 120-step.
        var wheelNotches = wheelDelta;
        var minimumView = GetEditorPreviewTimelineMinimumDuration(duration);
        var nextViewDuration = Math.Clamp(oldViewDuration * Math.Pow(0.8, wheelNotches), minimumView, duration);

        // At either zoom limit, consume the gesture without converting the unused
        // zoom delta into a small accidental pan around the cursor anchor.
        if (Math.Abs(nextViewDuration - oldViewDuration) < 0.000001)
            return;

        var nextViewStart = anchorSeconds - pointerRatio * nextViewDuration;
        SetEditorPreviewTimelineView(nextViewStart, nextViewDuration);
    }

    private void ScrollEditorPreviewTimeline(double wheelDelta)
    {
        if (Math.Abs(wheelDelta) < 0.0001)
            return;
        EnsureEditorPreviewTimelineView();
        var scrollSeconds = _editorPreviewTimelineTargetDurationSeconds * 0.16 * wheelDelta;
        SetEditorPreviewTimelineView(_editorPreviewTimelineTargetStartSeconds - scrollSeconds,
            _editorPreviewTimelineTargetDurationSeconds);
    }

    private void ScrollEditorPreviewTimelineHorizontal(double horizontalDelta)
    {
        if (Math.Abs(horizontalDelta) < 0.0001)
            return;
        EnsureEditorPreviewTimelineView();
        // SDL reports horizontal wheel movement in wheel-step units (including
        // fractional trackpad deltas). Positive movement reveals later song time.
        var scrollSeconds = _editorPreviewTimelineTargetDurationSeconds * 0.28 * horizontalDelta;
        SetEditorPreviewTimelineView(_editorPreviewTimelineTargetStartSeconds + scrollSeconds,
            _editorPreviewTimelineTargetDurationSeconds);
    }

    private void PanEditorPreviewTimeline(int deltaPixels, Rectangle timeline)
    {
        if (deltaPixels == 0 || timeline.Width <= 0)
            return;
        EnsureEditorPreviewTimelineView();
        var secondsPerPixel = _editorPreviewTimelineTargetDurationSeconds / timeline.Width;
        SetEditorPreviewTimelineView(_editorPreviewTimelineTargetStartSeconds - deltaPixels * secondsPerPixel,
            _editorPreviewTimelineTargetDurationSeconds);
    }

    private double GetEditorPreviewTimelineTimeAtX(int mouseX, Rectangle timeline)
    {
        var duration = EditorPreviewSourceDurationSeconds;
        if (duration <= 0 || timeline.Width <= 0)
            return 0;
        var viewStart = GetEditorPreviewTimelineViewStartSeconds();
        var viewDuration = GetEditorPreviewTimelineViewDurationSeconds();
        var ratio = Math.Clamp((mouseX - timeline.X) / (double)timeline.Width, 0, 1);
        return Math.Clamp(viewStart + ratio * viewDuration, 0, duration);
    }

    private void EnsureEditorPreviewWaveformRequested()
    {
        var generation = _audio.SourceGeneration;
        if (_editorPreviewWaveformGeneration != generation)
        {
            _editorPreviewWaveformRms = null;
            _editorPreviewWaveformRmsPrefix = null;
            _editorPreviewWaveformTask = null;
            _editorPreviewWaveformGeneration = generation;
        }
        if (!_audio.IsLoaded || _audio.PcmBytes <= 0)
            return;
        if (_editorPreviewWaveformRms is not null || _editorPreviewWaveformTask is not null)
            return;

        _editorPreviewWaveformTask = _audio.GetWaveformRmsEnvelopeAsync(32768);
    }

    private void UpdateEditorPreviewWaveformCache()
    {
        EnsureEditorPreviewWaveformRequested();
        var task = _editorPreviewWaveformTask;
        if (task is null || !task.IsCompleted)
            return;

        _editorPreviewWaveformTask = null;
        if (_editorPreviewWaveformGeneration != _audio.SourceGeneration)
            return;
        if (task.IsCompletedSuccessfully)
        {
            _editorPreviewWaveformRms = task.Result;
            _editorPreviewWaveformRmsPrefix = BuildEditorPreviewWaveformPrefix(task.Result);
            Program.LogRuntime($"EDITOR_PREVIEW_WAVEFORM_READY rms_buckets={task.Result.Length}");
        }
        else if (!task.IsCanceled)
        {
            var error = task.Exception?.GetBaseException();
            _editorPreviewWaveformRms = Array.Empty<float>();
            _editorPreviewWaveformRmsPrefix = Array.Empty<float>();
            if (error is not null)
                Program.LogRuntime($"EDITOR_PREVIEW_WAVEFORM_FAILED type={error.GetType().Name} message={error.Message}");
        }
    }

    private static float[] BuildEditorPreviewWaveformPrefix(float[] levels)
    {
        var prefix = new float[levels.Length + 1];
        if (levels.Length == 0)
            return prefix;

        // Normalize by the full envelope length so the cumulative total stays
        // close to [0, 1]. This keeps float prefix subtraction accurate even for
        // very short visible ranges near the end of a long song.
        var normalization = 1f / levels.Length;
        var sum = 0f;
        for (var index = 0; index < levels.Length; index++)
        {
            sum += Math.Clamp(levels[index], 0f, 1f) * normalization;
            prefix[index + 1] = sum;
        }
        return prefix;
    }

    private double GetEditorPreviewPlayheadSeconds()
    {
        if (!_editorPreviewSelectionPlaying || _editorPreviewSelectionPlaybackTimestamp <= 0)
            return Math.Clamp(_editorPreviewPlayheadSeconds, 0, EditorPreviewSourceDurationSeconds);

        var elapsed = (Stopwatch.GetTimestamp() - _editorPreviewSelectionPlaybackTimestamp) / (double)Stopwatch.Frequency;
        return Math.Clamp(_editorPreviewPlayheadSeconds + Math.Max(0, elapsed), 0, EditorPreviewSourceDurationSeconds);
    }

    private static double GetEditorPreviewFadeDurationSeconds(double clipDuration)
        => Math.Min(0.5, Math.Max(0.0, clipDuration / 4.0));

    private void EnsureEditorPreviewSelectionPlayer(double targetSeconds, bool play, bool forceRebuild = false)
    {
        var duration = EditorPreviewSourceDurationSeconds;
        var start = Math.Clamp(_editorPreviewStartSeconds, 0, duration);
        var end = Math.Clamp(_editorPreviewEndSeconds, start, duration);
        if (end - start < 0.001)
            throw new InvalidOperationException("Preview must be at least 0.001 seconds long.");
        if (_audio.PcmBytes <= 0)
            throw new InvalidOperationException("Decoded song audio is unavailable for real-time preview fades. Reload the song audio and try again.");

        targetSeconds = Math.Clamp(targetSeconds, 0, end);
        var bufferStart = Math.Min(targetSeconds, start);
        var currentPlayer = _editorPreviewSelectionPlayer;
        var rangeMatches = currentPlayer is not null &&
            Math.Abs(_editorPreviewPlaybackSelectionStartSeconds - start) < 0.0005 &&
            Math.Abs(_editorPreviewPlaybackBufferFadeEndSeconds - end) < 0.0005;
        var rebuild = forceRebuild || !rangeMatches || currentPlayer is null ||
            bufferStart < _editorPreviewPlaybackBufferStartSeconds - 0.0005;

        if (rebuild)
        {
            var fadeSeconds = GetEditorPreviewFadeDurationSeconds(end - start);
            // A playhead before START can audition the lead-in at its original
            // level. The fade itself always begins at the selected START marker,
            // matching the baked preview and every subsequent loop.
            var fadeInStart = start;
            var pcm = _audio.CreateFadedPcmRange(bufferStart, fadeInStart, end, fadeSeconds);
            var replacement = new OpusAudioPlayer();
            try
            {
                replacement.LoadPcm(pcm);
                replacement.Volume = GetEditorMusicPlaybackVolume();
                replacement.IsLooped = false;
                replacement.PrepareInitialPlayback(retainPcmForSeeking: true);
                var localTargetSeconds = Math.Max(0, targetSeconds - bufferStart);
                // Position the replacement while paused. If playback is active,
                // start it only after the previous source has been stopped so
                // rebuilding a range cannot briefly double the music volume.
                replacement.SeekToSeconds(localTargetSeconds, play: false);
            }
            catch
            {
                replacement.Dispose();
                throw;
            }

            var previous = _editorPreviewSelectionPlayer;
            _editorPreviewSelectionPlayer = replacement;
            _editorPreviewPlaybackBufferStartSeconds = bufferStart;
            _editorPreviewPlaybackBufferFadeEndSeconds = end;
            _editorPreviewPlaybackSelectionStartSeconds = start;
            try { previous?.Stop(); } catch { }
            try { previous?.Dispose(); } catch { }
            if (play)
                replacement.Resume();
            return;
        }

        currentPlayer!.Volume = GetEditorMusicPlaybackVolume();
        currentPlayer.SeekToSeconds(Math.Max(0, targetSeconds - _editorPreviewPlaybackBufferStartSeconds), play);
    }

    private void RefreshEditorPreviewSelectionPlayerAfterRangeChange()
    {
        if (_editorPreviewSelectionPlayer is null)
            return;

        var wasPlaying = _editorPreviewSelectionPlaying;
        var playhead = wasPlaying ? GetEditorPreviewPlayheadSeconds() : _editorPreviewPlayheadSeconds;
        if (playhead >= _editorPreviewEndSeconds)
            playhead = _editorPreviewStartSeconds;
        _editorPreviewPlayheadSeconds = Math.Clamp(playhead, 0, EditorPreviewSourceDurationSeconds);

        try
        {
            EnsureEditorPreviewSelectionPlayer(_editorPreviewPlayheadSeconds, wasPlaying, forceRebuild: true);
            _editorPreviewSelectionPlaying = wasPlaying;
            _editorPreviewSelectionPlaybackTimestamp = wasPlaying ? Stopwatch.GetTimestamp() : 0;
        }
        catch (Exception ex)
        {
            _editorPreviewSelectionPlaying = false;
            _editorPreviewSelectionPlaybackTimestamp = 0;
            _editorPreviewSelectionPlayer?.Pause();
            _editorPreviewDialogMessage = $"Could not update preview fades: {ex.Message}";
        }
    }

    private void DisposeEditorPreviewSelectionPlayer()
    {
        var player = _editorPreviewSelectionPlayer;
        _editorPreviewSelectionPlayer = null;
        _editorPreviewPlaybackBufferStartSeconds = 0;
        _editorPreviewPlaybackBufferFadeEndSeconds = 0;
        _editorPreviewPlaybackSelectionStartSeconds = 0;
        _editorPreviewSelectionPlaying = false;
        _editorPreviewSelectionPlaybackTimestamp = 0;
        try { player?.Stop(); } catch { }
        try { player?.Dispose(); } catch { }
    }

    private void UpdateEditorPreviewSelectionPlayback()
    {
        if (!_editorPreviewSelectionPlaying)
            return;

        var duration = EditorPreviewSourceDurationSeconds;
        if (duration <= 0 || !_audio.IsLoaded || _editorPreviewSelectionPlayer is null)
        {
            _editorPreviewSelectionPlaying = false;
            _editorPreviewSelectionPlaybackTimestamp = 0;
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsed = _editorPreviewSelectionPlaybackTimestamp > 0
            ? (now - _editorPreviewSelectionPlaybackTimestamp) / (double)Stopwatch.Frequency
            : 0.0;
        var current = _editorPreviewPlayheadSeconds + Math.Max(0, elapsed);
        var start = Math.Clamp(_editorPreviewStartSeconds, 0, duration);
        var end = Math.Clamp(_editorPreviewEndSeconds, start, duration);
        var range = end - start;
        if (range < 0.001)
        {
            PauseEditorPreviewSelection();
            _editorPreviewPlayheadSeconds = start;
            return;
        }

        if (current >= end)
        {
            current = start + ((current - end) % range);
            _editorPreviewPlayheadSeconds = current;
            _editorPreviewSelectionPlaybackTimestamp = now;
            _editorPreviewSelectionPlayer.SeekToSeconds(
                Math.Max(0, current - _editorPreviewPlaybackBufferStartSeconds), play: true);
        }
    }

    private void SeekEditorPreviewPlayhead(bool resumePlayback)
    {
        var duration = EditorPreviewSourceDurationSeconds;
        if (duration <= 0 || !_audio.IsLoaded)
            return;

        _editorPreviewPlayheadSeconds = Math.Clamp(_editorPreviewPlayheadSeconds, 0, duration);
        // The playhead remains independent of START. When it sits before START,
        // the audition includes the unmodified lead-in; the selected fade begins
        // exactly at START. Positions at/after END restart at START.
        if (resumePlayback && _editorPreviewPlayheadSeconds >= _editorPreviewEndSeconds)
            _editorPreviewPlayheadSeconds = _editorPreviewStartSeconds;

        if (resumePlayback || _editorPreviewSelectionPlayer is not null)
        {
            EnsureEditorPreviewSelectionPlayer(_editorPreviewPlayheadSeconds, resumePlayback);
            _editorPreviewSelectionPlaying = resumePlayback;
            _editorPreviewSelectionPlaybackTimestamp = resumePlayback ? Stopwatch.GetTimestamp() : 0;
        }
        else
        {
            _editorPreviewSelectionPlaying = false;
            _editorPreviewSelectionPlaybackTimestamp = 0;
        }
    }

    private void NormalizeEditorPreviewPlayheadToSelection()
    {
        var duration = EditorPreviewSourceDurationSeconds;
        if (duration <= 0)
            return;
        var start = Math.Clamp(_editorPreviewStartSeconds, 0, duration);
        var end = Math.Clamp(_editorPreviewEndSeconds, start, duration);
        var playhead = GetEditorPreviewPlayheadSeconds();
        // Moving START must not drag the independent playhead with it. If END is
        // moved behind an actively playing position, restart at the new START.
        if (_editorPreviewSelectionPlaying && playhead >= end)
        {
            _editorPreviewPlayheadSeconds = start;
            if (_editorPreviewSelectionPlayer is not null)
            {
                var localSeconds = Math.Max(0, start - _editorPreviewPlaybackBufferStartSeconds);
                _editorPreviewSelectionPlayer.SeekToSeconds(localSeconds, play: true);
            }
            _editorPreviewSelectionPlaybackTimestamp = Stopwatch.GetTimestamp();
        }
    }

    private void PauseEditorPreviewSelection()
    {
        if (_editorPreviewSelectionPlaying)
            _editorPreviewPlayheadSeconds = GetEditorPreviewPlayheadSeconds();
        _editorPreviewSelectionPlaying = false;
        _editorPreviewSelectionPlaybackTimestamp = 0;
        if (_editorPreviewSelectionPlayer is not null)
        {
            var localSeconds = Math.Max(0, _editorPreviewPlayheadSeconds - _editorPreviewPlaybackBufferStartSeconds);
            _editorPreviewSelectionPlayer.SeekToSeconds(localSeconds, play: false);
        }
    }

    private void PlayEditorPreviewSelectionFromPlayhead()
    {
        if (_chart is null || !_chartEditorMode)
            return;
        var duration = EditorPreviewSourceDurationSeconds;
        if (_audio.IsLoading)
            throw new InvalidOperationException("Music is still being prepared. Preview controls will be available shortly.");
        if (duration <= 0)
            throw new InvalidOperationException("Import the full song audio before creating a preview.");
        if (!TryReadEditorPreviewRange(out var startSeconds, out var endSeconds, out var validationMessage,
                extendEndWhenStartPassesIt: _editorPreviewFocusedField == 0))
            throw new InvalidOperationException(validationMessage);

        if (_editorPreviewSelectionPlaying)
            _editorPreviewPlayheadSeconds = GetEditorPreviewPlayheadSeconds();
        _editorPreviewStartSeconds = startSeconds;
        _editorPreviewEndSeconds = endSeconds;
        SyncEditorPreviewTimeTexts();
        if (_editorPreviewPlayheadSeconds >= endSeconds)
            _editorPreviewPlayheadSeconds = startSeconds;

        EnsureEditorPreviewSelectionPlayer(_editorPreviewPlayheadSeconds, play: true);
        _editorPreviewSelectionPlaying = true;
        _editorPreviewSelectionPlaybackTimestamp = Stopwatch.GetTimestamp();
        _editorPreviewDialogMessage = $"Playing selected range ({FormatEditorPreviewTimestamp(startSeconds)}–{FormatEditorPreviewTimestamp(endSeconds)}). Not saved yet.";
        _statusMessage = "Playing selected music preview. Chart playback is paused.";
    }

    private void StopEditorPreviewSelectionPlayback(bool resetPlayhead = true)
    {
        if (resetPlayhead)
            _editorPreviewPlayheadSeconds = Math.Clamp(_editorPreviewStartSeconds, 0, EditorPreviewSourceDurationSeconds);
        else if (_editorPreviewSelectionPlaying)
            _editorPreviewPlayheadSeconds = GetEditorPreviewPlayheadSeconds();

        _editorPreviewSelectionPlaying = false;
        _editorPreviewSelectionPlaybackTimestamp = 0;
        if (_editorPreviewSelectionPlayer is not null)
        {
            if (resetPlayhead && _editorPreviewSelectionPlayer.IsLoaded)
                EnsureEditorPreviewSelectionPlayer(_editorPreviewPlayheadSeconds, play: false);
            else
            {
                var localSeconds = Math.Max(0, _editorPreviewPlayheadSeconds - _editorPreviewPlaybackBufferStartSeconds);
                _editorPreviewSelectionPlayer.SeekToSeconds(localSeconds, play: false);
            }
        }
    }

    private void OnEditorSourceAudioPrepared()
    {
        if (!_editorPreviewEditorOpen || !_audio.IsLoaded)
            return;

        var duration = EditorPreviewSourceDurationSeconds;
        if (duration <= 0)
            return;

        InitializeEditorPreviewRange(duration);
        SyncEditorPreviewTimeTexts();
        _editorPreviewPlayheadSeconds = _editorPreviewStartSeconds;
        EnsureEditorPreviewWaveformRequested();
        _editorPreviewDialogMessage = null;
        Program.LogRuntime($"EDITOR_PREVIEW_SOURCE_AUDIO_READY durationSeconds={duration:0.000}");
    }

    private void OnEditorSourceAudioPreparationFailed(Exception error)
    {
        if (!_editorPreviewEditorOpen)
            return;

        _editorPreviewDialogMessage = $"Music could not be prepared: {error.Message}";
        Program.LogRuntime($"EDITOR_PREVIEW_SOURCE_AUDIO_FAILED type={error.GetType().Name} message={error.Message}");
    }

    private static string FormatEditorPreviewSeconds(double seconds)
        => Math.Max(0, double.IsFinite(seconds) ? seconds : 0).ToString("0.000", CultureInfo.InvariantCulture);

    private static double RoundEditorPreviewMilliseconds(double seconds)
        => Math.Round((double.IsFinite(seconds) ? seconds : 0) * 1000.0,
            MidpointRounding.AwayFromZero) / 1000.0;

    // Chart time is measured from tick zero, but preview times are offsets into
    // the source audio file. Account for the chart's audio-origin marker so the
    // picked position matches what the preview waveform and audio player use.
    private double GetEditorPreviewAudioTimeAtTick(int tick)
    {
        if (_tempo is null || _chart is null)
            return double.NaN;
        var audioOriginSeconds = _tempo.TickToSeconds(Math.Max(0, _chart.AudioOriginTick));
        return _tempo.TickToSeconds(tick) - audioOriginSeconds;
    }

    private string GetEditorPreviewAudioTimeLabelAtTick(int tick)
    {
        var seconds = GetEditorPreviewAudioTimeAtTick(tick);
        if (!double.IsFinite(seconds))
            return "--:--:---";
        if (seconds < 0)
            return "BEFORE AUDIO";
        if (seconds > EditorPreviewSourceDurationSeconds && EditorPreviewSourceDurationSeconds > 0)
            return "AFTER AUDIO";
        return FormatEditorProgressTimestamp(seconds);
    }

    private static string FormatEditorPreviewTimestamp(double seconds)
    {
        seconds = Math.Max(0, double.IsFinite(seconds) ? seconds : 0);
        var minutes = Math.Floor(seconds / 60.0);
        var remainder = seconds - minutes * 60.0;
        return $"{minutes:00}:{remainder:00.000}";
    }

    private static string FormatEditorProgressTimestamp(double seconds)
    {
        var milliseconds = (long)Math.Round(
            Math.Max(0, double.IsFinite(seconds) ? seconds : 0) * 1000.0,
            MidpointRounding.AwayFromZero);
        var minutes = milliseconds / 60_000;
        var remainingSeconds = milliseconds / 1000 % 60;
        var remainingMilliseconds = milliseconds % 1000;
        return $"{minutes:00}:{remainingSeconds:00}:{remainingMilliseconds:000}";
    }

    private string GetEditorSavedPreviewPositionLabel()
    {
        if (_chart is null || string.IsNullOrWhiteSpace(_chart.PreviewPath))
            return "Not set";

        var duration = _editorPreviewPlayer?.DurationSeconds ?? GetEditorSavedPreviewDurationSeconds();
        if (!double.IsFinite(duration) || duration <= 0)
            return "00:00:000 / --:--:---";

        var elapsed = _editorPreviewPlayer is null ? 0 : GetEditorPreviewPlaybackElapsedSeconds();
        return $"{FormatEditorProgressTimestamp(elapsed)} / {FormatEditorProgressTimestamp(duration)}";
    }

    private void WriteEditorSharedPreviewAsset(string directory)
    {
        if (_editorSharedPreviewBytes is not { Length: > 0 })
            return;
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "preview.opus"), _editorSharedPreviewBytes);
    }

    private void ReplaceEditorSharedPreviewEntry(List<FpkWriteEntry> entries)
    {
        if (_editorSharedPreviewBytes is not { Length: > 0 })
            return;
        entries.RemoveAll(entry => string.Equals(Path.GetFileName(entry.Name), "preview.opus", StringComparison.OrdinalIgnoreCase));
        entries.Add(new FpkWriteEntry("preview.opus", _editorSharedPreviewBytes.ToArray()));
    }

    private void PauseChartPlaybackForPreviewEditor()
    {
        // Cancel any pending editor-scroll resume. The modal must keep gameplay
        // audio paused even if it opens during the scroll-idle grace period.
        _editorWheelScrollPauseActive = false;
        _editorWheelResumeAfterScroll = false;
        _editorWheelScrollIdleRemaining = 0.0;
        _editorTimelineScrubbing = false;
        _editorTimelineResumeAfterScrub = false;
        _playing = false;
        _playbackStartPending = false;
        _audio.Pause();
        _audioResyncPending = true;
        _audioStartedForCurrentRun = false;
    }

    private void InitializeEditorPreviewRange(double duration)
    {
        if (duration <= 0)
        {
            _editorPreviewStartSeconds = 0;
            _editorPreviewEndSeconds = 0;
            _editorPreviewRangeInitialized = false;
            return;
        }

        if (_chart?.HasPreviewRange == true)
        {
            _editorPreviewStartSeconds = _chart.PreviewStartMilliseconds!.Value / 1000.0;
            _editorPreviewEndSeconds = _chart.PreviewEndMilliseconds!.Value / 1000.0;
            _editorPreviewRangeInitialized = true;
        }
        else if (!_editorPreviewRangeInitialized)
        {
            _editorPreviewStartSeconds = 0;
            _editorPreviewEndSeconds = Math.Min(30.0, duration);
            _editorPreviewRangeInitialized = true;
        }

        _editorPreviewStartSeconds = Math.Clamp(RoundEditorPreviewMilliseconds(_editorPreviewStartSeconds), 0, duration);
        _editorPreviewEndSeconds = Math.Clamp(RoundEditorPreviewMilliseconds(_editorPreviewEndSeconds), 0, duration);
        if (_editorPreviewEndSeconds <= _editorPreviewStartSeconds)
        {
            _editorPreviewStartSeconds = 0;
            _editorPreviewEndSeconds = Math.Min(30.0, duration);
        }
    }

    private void CommitEditorPreviewRangeToChart()
    {
        if (_chart is null || !_chartEditorMode)
            return;

        var startMilliseconds = (int)Math.Clamp(
            Math.Round(RoundEditorPreviewMilliseconds(_editorPreviewStartSeconds) * 1000.0,
                MidpointRounding.AwayFromZero), 0, int.MaxValue);
        var endMilliseconds = (int)Math.Clamp(
            Math.Round(RoundEditorPreviewMilliseconds(_editorPreviewEndSeconds) * 1000.0,
                MidpointRounding.AwayFromZero), 0, int.MaxValue);
        if (endMilliseconds <= startMilliseconds ||
            (_chart.PreviewStartMilliseconds == startMilliseconds &&
             _chart.PreviewEndMilliseconds == endMilliseconds))
            return;

        var undoSnapshot = CaptureEditorChartSnapshot();
        _chart.PreviewStartMilliseconds = startMilliseconds;
        _chart.PreviewEndMilliseconds = endMilliseconds;
        if (undoSnapshot is not null && !EditorChartMatchesSnapshot(undoSnapshot))
            PushEditorUndoSnapshot(undoSnapshot);

        SyncSharedEditorMetadataToDifficultyStates();
        StoreActiveEditorDifficulty();
        MarkEditorDirty();
        RefreshEditorPreviewSelectionPlayerAfterRangeChange();
    }

    private void OpenEditorPreviewEditor()
    {
        if (!_chartEditorMode || _chart is null)
            return;

        PauseChartPlaybackForPreviewEditor();
        StopEditorPreviewPlayback();
        var duration = EditorPreviewSourceDurationSeconds;
        InitializeEditorPreviewRange(duration);
        SyncEditorPreviewTimeTexts();
        _editorPreviewPlayheadSeconds = _editorPreviewStartSeconds;
        _editorPreviewSelectionPlaying = false;
        _editorPreviewSelectionPlaybackTimestamp = 0;
        EnsureEditorPreviewWaveformRequested();
        _editorPreviewFocusedField = -1;
        _editorPreviewDialogMessage = _audio.IsLoading
            ? "Music is still being prepared. Preview controls will be available shortly."
            : _audioLoadFailed && _loadedMusicBytes is { Length: > 0 }
                ? "Music could not be prepared. Re-import the song audio to try again."
                : duration > 0 ? null : "Import the full song audio before creating a preview.";
        _editorPreviewEditorOpen = true;
        _editorMetaEditingField = null;
        _statusMessage = "Music preview editor opened. Playback is paused.";
    }

    private void CloseEditorPreviewEditor()
    {
        if (_editorPreviewPendingChartTickTarget >= 0)
        {
            _editorPreviewPendingChartTickTarget = -1;
            ClearEditorPreviewChartTickPickHover();
            RestoreEditorMetaPanelAfterChartTickPick();
        }

        _editorPreviewEditorOpen = false;
        _editorPreviewFocusedField = -1;
        _editorPreviewDraggingStart = false;
        _editorPreviewDraggingEnd = false;
        _editorPreviewDraggingPlayhead = false;
        _editorPreviewStartDragPointerOffsetX = 0;
        _editorPreviewEndDragPointerOffsetX = 0;
        _editorPreviewWasPlayingBeforePlayheadDrag = false;
        _editorPreviewDraggingTimelinePan = false;
        StopEditorPreviewSelectionPlayback(resetPlayhead: false);
        DisposeEditorPreviewSelectionPlayer();
        _editorPreviewWaveformRms = null;
        _editorPreviewWaveformRmsPrefix = null;
        _editorPreviewWaveformTask = null;
        _editorPreviewWaveformGeneration = -1;
        _audio.ReleaseWaveformRmsEnvelope();
        _editorPreviewDialogMessage = null;
        EndEditorTextFieldMouseSelection();
        _editorPreviewInputReplaceOnNextKey = false;
        StopEditorPreviewPlayback();

        // Marker drags and typed/picked times are edits in progress until SAVE is
        // pressed. Re-read the chart metadata on close so discarding the dialog
        // restores the last saved preview range instead of retaining unsaved edits.
        _editorPreviewRangeInitialized = false;
        InitializeEditorPreviewRange(EditorPreviewSourceDurationSeconds);
        SyncEditorPreviewTimeTexts();
        _editorPreviewPlayheadSeconds = _editorPreviewStartSeconds;

        // Deliberately leave chart playback paused. The user can resume it explicitly.
        PauseChartPlaybackForPreviewEditor();
        _statusMessage = "Music preview editor closed. Playback remains paused.";
    }

    private void SyncEditorPreviewTimeTexts()
    {
        var duration = EditorPreviewSourceDurationSeconds;
        _editorPreviewStartSeconds = Math.Clamp(RoundEditorPreviewMilliseconds(_editorPreviewStartSeconds), 0, duration);
        _editorPreviewEndSeconds = Math.Clamp(RoundEditorPreviewMilliseconds(_editorPreviewEndSeconds), 0, duration);
        _editorPreviewStartText = FormatEditorPreviewSeconds(_editorPreviewStartSeconds);
        _editorPreviewEndText = FormatEditorPreviewSeconds(_editorPreviewEndSeconds);
        _editorPreviewStartCaret = _editorPreviewStartText.Length;
        _editorPreviewEndCaret = _editorPreviewEndText.Length;
    }

    private string? GetEditorPreviewAssetDirectory()
    {
        if (_editorPreviewAssetDirectoryCacheValid &&
            string.Equals(_editorPreviewAssetDirectoryCacheWorkingDirectory, _tstsWorkingDirectory, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_editorPreviewAssetDirectoryCacheSourcePath, _loadedSourcePath, StringComparison.OrdinalIgnoreCase))
            return _editorPreviewAssetDirectoryCacheResult;

        string? result = null;
        if (!string.IsNullOrWhiteSpace(_tstsWorkingDirectory) && Directory.Exists(_tstsWorkingDirectory))
        {
            result = _tstsWorkingDirectory;
        }
        else if (!string.IsNullOrWhiteSpace(_loadedSourcePath))
        {
            try
            {
                result = string.Equals(Path.GetExtension(_loadedSourcePath), ".fpk", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetDirectoryName(Path.GetFullPath(_loadedSourcePath))
                    : GetSongAssetDirectoryForChartPath(_loadedSourcePath);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                result = null;
            }
        }

        _editorPreviewAssetDirectoryCacheWorkingDirectory = _tstsWorkingDirectory;
        _editorPreviewAssetDirectoryCacheSourcePath = _loadedSourcePath;
        _editorPreviewAssetDirectoryCacheResult = result;
        _editorPreviewAssetDirectoryCacheValid = true;
        return result;
    }

    private string? GetEditorMediaResourceDirectory()
    {
        if (_editorMediaResourceDirectoryCacheValid &&
            string.Equals(_editorMediaResourceDirectoryCacheWorkingDirectory, _tstsWorkingDirectory, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_editorMediaResourceDirectoryCacheSourcePath, _loadedSourcePath, StringComparison.OrdinalIgnoreCase))
            return _editorMediaResourceDirectoryCacheResult;

        var sourceDirectory = _tstsWorkingDirectory;
        if (string.IsNullOrWhiteSpace(sourceDirectory) && !string.IsNullOrWhiteSpace(_loadedSourcePath))
        {
            try { sourceDirectory = GetSongAssetDirectoryForChartPath(_loadedSourcePath); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                sourceDirectory = null;
            }
        }

        var result = !string.IsNullOrWhiteSpace(sourceDirectory) && Directory.Exists(sourceDirectory)
            ? sourceDirectory
            : null;
        _editorMediaResourceDirectoryCacheWorkingDirectory = _tstsWorkingDirectory;
        _editorMediaResourceDirectoryCacheSourcePath = _loadedSourcePath;
        _editorMediaResourceDirectoryCacheResult = result;
        _editorMediaResourceDirectoryCacheValid = true;
        return result;
    }

    private void InvalidateEditorMediaAssetCaches()
    {
        _editorPreviewAssetDirectoryCacheValid = false;
        _editorPreviewAssetDirectoryCacheWorkingDirectory = null;
        _editorPreviewAssetDirectoryCacheSourcePath = null;
        _editorPreviewAssetDirectoryCacheResult = null;
        _editorMediaResourceDirectoryCacheValid = false;
        _editorMediaResourceDirectoryCacheWorkingDirectory = null;
        _editorMediaResourceDirectoryCacheSourcePath = null;
        _editorMediaResourceDirectoryCacheResult = null;
        _editorArtworkPathCache.Clear();
        _editorMediaResourceCache.Clear();
    }

    private byte[]? GetEditorPreviewMusicBytes()
    {
        if (_editorSharedMusicBytes is { Length: > 0 })
            return _editorSharedMusicBytes;
        if (_loadedMusicBytes is { Length: > 0 })
            return _loadedMusicBytes;

        if (!string.IsNullOrWhiteSpace(_loadedSourcePath) &&
            string.Equals(Path.GetExtension(_loadedSourcePath), ".fpk", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(_loadedSourcePath))
        {
            try
            {
                var package = GetOrLoadFpkPackage(_loadedSourcePath);
                var entry = package.FindMusic();
                if (entry is not null)
                    return package.Extract(entry);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _statusMessage = $"Could not read source music from FPK: {ex.Message}";
            }
        }

        var directory = GetEditorPreviewAssetDirectory();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        if (!string.IsNullOrWhiteSpace(_chart?.MusicPath))
        {
            try
            {
                var path = Path.IsPathRooted(_chart.MusicPath)
                    ? _chart.MusicPath
                    : Path.Combine(directory, _chart.MusicPath);
                if (File.Exists(path))
                    return File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Try fixed template file names next.
            }
        }

        foreach (var name in new[] { "music.opus", "song.opus" })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
                return File.ReadAllBytes(path);
        }
        return null;
    }

    private byte[]? GetExistingEditorPreviewBytes()
    {
        if (_editorSharedPreviewBytes is { Length: > 0 })
            return _editorSharedPreviewBytes;

        if (!string.IsNullOrWhiteSpace(_loadedSourcePath) &&
            string.Equals(Path.GetExtension(_loadedSourcePath), ".fpk", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(_loadedSourcePath))
        {
            try
            {
                var package = GetOrLoadFpkPackage(_loadedSourcePath);
                var requestedName = string.IsNullOrWhiteSpace(_chart?.PreviewPath)
                    ? "preview.opus"
                    : Path.GetFileName(_chart.PreviewPath);
                var entry = package.Entries.FirstOrDefault(item =>
                    string.Equals(Path.GetFileName(item.Name), requestedName, StringComparison.OrdinalIgnoreCase))
                    ?? package.Entries.FirstOrDefault(item =>
                        string.Equals(Path.GetFileName(item.Name), "preview.opus", StringComparison.OrdinalIgnoreCase));
                if (entry is not null)
                {
                    var bytes = package.Extract(entry);
                    if (string.Equals(Path.GetExtension(entry.Name), ".opus", StringComparison.OrdinalIgnoreCase))
                        return bytes;
                    return ConvertEditorAudioBytesToOpus(bytes, entry.Name);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _statusMessage = $"Could not read existing preview from FPK: {ex.Message}";
            }
        }

        var directory = GetEditorPreviewAssetDirectory();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(_chart?.PreviewPath))
        {
            try
            {
                candidates.Add(Path.IsPathRooted(_chart.PreviewPath)
                    ? _chart.PreviewPath
                    : Path.Combine(directory, _chart.PreviewPath));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // Ignore invalid metadata path and try standard names.
            }
        }
        candidates.AddRange(new[] { "preview.opus", "preview.ogg", "preview.mp3", "preview.wav", "preview.m4a", "preview.aac" }
            .Select(name => Path.Combine(directory, name)));

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
                continue;
            var bytes = File.ReadAllBytes(path);
            if (string.Equals(Path.GetExtension(path), ".opus", StringComparison.OrdinalIgnoreCase))
                return bytes;
            return ConvertEditorAudioBytesToOpus(bytes, path);
        }
        return null;
    }

    private double GetEditorSavedPreviewDurationSeconds()
    {
        if (_editorSavedPreviewDurationSeconds.HasValue)
            return _editorSavedPreviewDurationSeconds.Value;

        try
        {
            var bytes = GetExistingEditorPreviewBytes();
            if (bytes is { Length: > 0 })
            {
                using var probe = new OpusAudioPlayer();
                probe.LoadOpus(bytes);
                _editorSavedPreviewDurationSeconds = Math.Max(0, probe.DurationSeconds);
                return _editorSavedPreviewDurationSeconds.Value;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // The media panel can still be used to import or recreate a missing/broken preview.
        }

        _editorSavedPreviewDurationSeconds = 0;
        return 0;
    }

    private void InvalidateEditorSavedPreviewDuration()
        => _editorSavedPreviewDurationSeconds = null;

    private static byte[] ConvertEditorAudioBytesToOpus(byte[] inputBytes, string sourceName)
    {
        var ffmpeg = FindTstsFfmpegExecutable();
        if (ffmpeg is null)
            throw ExternalDependencyPrompts.CreateFfmpegNotFoundException("convert this preview audio to Opus");
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "TSC-preview-convert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var inputPath = Path.Combine(temporaryDirectory, "input" + (Path.GetExtension(sourceName) is { Length: > 0 } ext ? ext : ".audio"));
        var outputPath = Path.Combine(temporaryDirectory, "preview.opus");
        try
        {
            File.WriteAllBytes(inputPath, inputBytes);
            RunEditorPreviewFfmpeg(ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error", "-y", "-i", inputPath,
                "-map", "0:a:0", "-vn", "-ac", "2", "-ar", "48000",
                "-c:a", "libopus", "-b:a", "160k", "-application", "audio", "-vbr", "on", "-f", "opus", outputPath
            });
            return File.ReadAllBytes(outputPath);
        }
        finally
        {
            try { Directory.Delete(temporaryDirectory, recursive: true); } catch { }
        }
    }

    private static void RunEditorPreviewFfmpeg(string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start FFmpeg.");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? "FFmpeg could not process the audio file." : stderr.Trim();
            if (detail.Length > 1200) detail = detail[..1200];
            throw new InvalidDataException(detail);
        }
    }

    private void PlayExistingEditorPreview()
    {
        PauseChartPlaybackForPreviewEditor();
        StopEditorPreviewPlayback();
        try
        {
            var bytes = GetExistingEditorPreviewBytes();
            if (bytes is not { Length: > 0 })
            {
                var message = "No existing preview audio was found. Use EDIT, then SAVE to create one.";
                if (_editorPreviewEditorOpen) _editorPreviewDialogMessage = message;
                _statusMessage = "No existing music preview was found.";
                return;
            }
            _editorPreviewPlayer = new OpusAudioPlayer();
            _editorPreviewPlayer.LoadOpus(bytes);
            _editorPreviewPlayer.Volume = GetEditorMusicPlaybackVolume();
            _editorPreviewPlayer.IsLooped = true;
            _editorPreviewPlayer.PrepareInitialPlayback(retainPcmForSeeking: true);
            _editorPreviewPlayer.SyncToChartTime(0.0, 0.0);
            _editorPreviewPlaybackDurationSeconds = _editorPreviewPlayer.DurationSeconds;
            _editorSavedPreviewDurationSeconds = _editorPreviewPlayer.DurationSeconds;
            _editorPreviewPlaybackStartTimestamp = Stopwatch.GetTimestamp();
            _statusMessage = "Playing existing music preview. Chart playback is paused.";
            if (_editorPreviewEditorOpen) _editorPreviewDialogMessage = "Playing existing preview. Chart playback is paused.";
        }
        catch (Exception ex)
        {
            StopEditorPreviewPlayback();
            var message = $"Could not play preview: {ex.Message}";
            if (_editorPreviewEditorOpen) _editorPreviewDialogMessage = message;
            _statusMessage = "Could not play music preview. See the preview editor for details.";
        }
    }

    private void StopEditorPreviewPlayback()
    {
        var player = _editorPreviewPlayer;
        _editorPreviewPlayer = null;
        _editorPreviewPlaybackStartTimestamp = 0;
        try { player?.Stop(); } catch { }
        try { player?.Dispose(); } catch { }
    }

    private double GetEditorPreviewPlaybackElapsedSeconds()
    {
        if (_editorPreviewPlaybackStartTimestamp == 0 || _editorPreviewPlaybackDurationSeconds <= 0)
            return 0;
        var elapsed = (Stopwatch.GetTimestamp() - _editorPreviewPlaybackStartTimestamp) / (double)Stopwatch.Frequency;
        if (!double.IsFinite(elapsed) || elapsed <= 0)
            return 0;
        return elapsed % _editorPreviewPlaybackDurationSeconds;
    }

    private byte[] CreateBakedEditorPreviewBytes(double startSeconds, double endSeconds)
    {
        var musicBytes = GetEditorPreviewMusicBytes();
        if (musicBytes is not { Length: > 0 })
            throw new InvalidOperationException("The full song audio could not be found. Re-import the song audio first.");

        var ffmpeg = FindTstsFfmpegExecutable();
        if (ffmpeg is null)
            throw ExternalDependencyPrompts.CreateFfmpegNotFoundException("create or play the selected music preview");

        var clipDuration = endSeconds - startSeconds;
        var fadeSeconds = GetEditorPreviewFadeDurationSeconds(clipDuration);
        var fadeOutStart = Math.Max(0, clipDuration - fadeSeconds);
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "TSC-preview-bake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var inputPath = Path.Combine(temporaryDirectory, "music.opus");
        var outputPath = Path.Combine(temporaryDirectory, "preview.opus");
        try
        {
            File.WriteAllBytes(inputPath, musicBytes);
            var arguments = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-ss", startSeconds.ToString("0.######", CultureInfo.InvariantCulture),
                "-i", inputPath,
                "-t", clipDuration.ToString("0.######", CultureInfo.InvariantCulture),
                "-map", "0:a:0", "-vn",
                "-af", $"afade=t=in:st=0:d={fadeSeconds.ToString("0.######", CultureInfo.InvariantCulture)}:curve=tri,afade=t=out:st={fadeOutStart.ToString("0.######", CultureInfo.InvariantCulture)}:d={fadeSeconds.ToString("0.######", CultureInfo.InvariantCulture)}:curve=tri",
                "-ac", "2", "-ar", "48000", "-c:a", "libopus", "-b:a", "160k", "-application", "audio", "-vbr", "on", "-f", "opus", outputPath
            };
            RunEditorPreviewFfmpeg(ffmpeg, arguments);
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                throw new InvalidDataException("FFmpeg did not produce a preview audio file.");
            return File.ReadAllBytes(outputPath);
        }
        finally
        {
            try { Directory.Delete(temporaryDirectory, recursive: true); } catch { }
        }
    }

    private bool SaveEditorPreview()
    {
        if (_chart is null || !_chartEditorMode)
            return false;

        var duration = EditorPreviewSourceDurationSeconds;
        if (_audio.IsLoading)
            throw new InvalidOperationException("Music is still being prepared. Preview controls will be available shortly.");
        if (duration <= 0)
            throw new InvalidOperationException("Import the full song audio before creating a preview.");
        if (!TryReadEditorPreviewRange(out var startSeconds, out var endSeconds, out var validationMessage,
                extendEndWhenStartPassesIt: _editorPreviewFocusedField == 0))
            throw new InvalidOperationException(validationMessage);

        StopEditorPreviewSelectionPlayback();
        StopEditorPreviewPlayback();
        var bakedBytes = CreateBakedEditorPreviewBytes(startSeconds, endSeconds);

        // SAVE is the only action that commits the selected range to chart metadata.
        // Do this only after the preview audio was generated successfully.
        _editorPreviewStartSeconds = startSeconds;
        _editorPreviewEndSeconds = endSeconds;
        CommitEditorPreviewRangeToChart();

        // This is the only action that writes the preview beside the source.
        // Until SAVE is pressed, generated playback remains temporary only.
        _editorSharedPreviewBytes = bakedBytes;
        InvalidateEditorSavedPreviewDuration();
        _chart.PreviewPath = "preview.opus";
        string? assetWriteWarning = null;
        var targetDirectory = GetEditorPreviewAssetDirectory();
        if (!string.IsNullOrWhiteSpace(targetDirectory))
        {
            try
            {
                Directory.CreateDirectory(targetDirectory);
                File.WriteAllBytes(Path.Combine(targetDirectory, "preview.opus"), bakedBytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                assetWriteWarning = ex.Message;
            }
        }

        SyncSharedEditorMetadataToDifficultyStates();
        StoreActiveEditorDifficulty();
        MarkEditorDirty(auxiliaryChange: true);
        _editorPreviewStartSeconds = startSeconds;
        _editorPreviewEndSeconds = endSeconds;
        _editorPreviewPlayheadSeconds = startSeconds;
        SyncEditorPreviewTimeTexts();
        _editorPreviewPlaybackDurationSeconds = Math.Max(0.0, endSeconds - startSeconds);
        _editorPreviewDialogMessage = assetWriteWarning is null
            ? string.IsNullOrWhiteSpace(targetDirectory)
                ? $"Saved preview in memory ({FormatEditorPreviewTimestamp(startSeconds)}–{FormatEditorPreviewTimestamp(endSeconds)}). It will be included on export."
                : $"Saved preview.opus ({FormatEditorPreviewTimestamp(startSeconds)}–{FormatEditorPreviewTimestamp(endSeconds)})."
            : $"Saved preview in memory. It will be included on export; writing beside the source failed: {assetWriteWarning}";
        _statusMessage = assetWriteWarning is null
            ? string.IsNullOrWhiteSpace(targetDirectory)
                ? "Music preview saved in memory and will be included on export."
                : "Music preview saved."
            : "Music preview saved in memory; writing beside the source failed. See the preview editor for details.";
        return true;
    }

    private bool TryReadEditorPreviewRange(out double startSeconds, out double endSeconds, out string message,
        bool extendEndWhenStartPassesIt = false)
    {
        startSeconds = _editorPreviewStartSeconds;
        endSeconds = _editorPreviewEndSeconds;
        message = string.Empty;
        if (_audio.IsLoading)
        {
            message = "Music is still being prepared. Preview controls will be available shortly.";
            return false;
        }
        if (!double.TryParse(_editorPreviewStartText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out startSeconds) || !double.IsFinite(startSeconds))
        {
            message = "Start time must be a valid number of seconds.";
            return false;
        }
        if (!double.TryParse(_editorPreviewEndText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out endSeconds) || !double.IsFinite(endSeconds))
        {
            message = "End time must be a valid number of seconds.";
            return false;
        }
        var sourceDuration = EditorPreviewSourceDurationSeconds;
        if (sourceDuration <= 0)
        {
            message = "Import the full song audio before creating a preview.";
            return false;
        }

        // The preview editor uses millisecond precision consistently for typed
        // values and marker dragging, rather than retaining sub-millisecond
        // floating point noise that cannot be represented in the UI.
        startSeconds = RoundEditorPreviewMilliseconds(startSeconds);
        endSeconds = RoundEditorPreviewMilliseconds(endSeconds);
        if (startSeconds < 0 || startSeconds >= sourceDuration)
        {
            message = $"Preview start must be between 0 and the song length ({sourceDuration:0.###} seconds).";
            return false;
        }

        if (extendEndWhenStartPassesIt && startSeconds >= endSeconds)
        {
            // Keep the selected start and move the end to 10 seconds later,
            // limited by the actual remaining song length.
            endSeconds = Math.Min(startSeconds + 10.0, sourceDuration);
        }
        else if (endSeconds < 0 || startSeconds >= endSeconds)
        {
            message = "Preview start must be earlier than preview end.";
            return false;
        }

        if (endSeconds > sourceDuration + 0.001)
        {
            message = $"Preview end cannot exceed the song length ({sourceDuration:0.###} seconds).";
            return false;
        }
        if (endSeconds - startSeconds < 0.001)
        {
            message = "Preview must be at least 0.001 seconds long.";
            return false;
        }
        startSeconds = Math.Clamp(startSeconds, 0, sourceDuration);
        endSeconds = Math.Clamp(endSeconds, 0, sourceDuration);
        return true;
    }

    private void CommitEditorPreviewFocusedTime()
    {
        if (_editorPreviewFocusedField < 0)
            return;
        if (!TryReadEditorPreviewRange(out var start, out var end, out var message,
                extendEndWhenStartPassesIt: _editorPreviewFocusedField == 0))
        {
            _editorPreviewDialogMessage = message;
            return;
        }
        _editorPreviewStartSeconds = start;
        _editorPreviewEndSeconds = end;
        SyncEditorPreviewTimeTexts();
        NormalizeEditorPreviewPlayheadToSelection();
        RefreshEditorPreviewSelectionPlayerAfterRangeChange();
        _editorPreviewFocusedField = -1;
        _editorPreviewStartSelectionAnchor = _editorPreviewStartCaret;
        _editorPreviewEndSelectionAnchor = _editorPreviewEndCaret;
        _editorPreviewInputReplaceOnNextKey = false;
        EndEditorTextFieldMouseSelection();
        _editorPreviewDialogMessage = null;
    }

    private void DiscardEditorPreviewFocusedTimeInput()
    {
        if (_editorPreviewFocusedField == 0)
        {
            _editorPreviewStartText = FormatEditorPreviewSeconds(_editorPreviewStartSeconds);
            _editorPreviewStartCaret = _editorPreviewStartText.Length;
            _editorPreviewStartSelectionAnchor = _editorPreviewStartCaret;
        }
        else if (_editorPreviewFocusedField == 1)
        {
            _editorPreviewEndText = FormatEditorPreviewSeconds(_editorPreviewEndSeconds);
            _editorPreviewEndCaret = _editorPreviewEndText.Length;
            _editorPreviewEndSelectionAnchor = _editorPreviewEndCaret;
        }
        _editorPreviewFocusedField = -1;
        _editorPreviewInputReplaceOnNextKey = false;
        EndEditorTextFieldMouseSelection();
    }

    private void ClearEditorPreviewChartTickPickHover()
    {
        _hoverBeatTick = null;
        _hoverTrack = null;
        _editorHoverHit = null;
        _editorHoverCacheValid = false;
    }

    private void RestoreEditorMetaPanelAfterChartTickPick()
    {
        if (_editorPreviewRestoreMetaPanelAfterChartTickPick)
            _editorMetaPanelOpen = true;
        _editorPreviewRestoreMetaPanelAfterChartTickPick = false;
    }

    private void BeginEditorPreviewChartTickPick(int target)
    {
        if (target is not (0 or 1) || _chart is null || _tempo is null)
            return;
        if (_editorPreviewFocusedField >= 0)
            CommitEditorPreviewFocusedTime();
        if (_editorPreviewFocusedField >= 0)
            return;

        var wasAlreadyPicking = _editorPreviewPendingChartTickTarget >= 0;
        _editorPreviewPendingChartTickTarget = target;
        if (!wasAlreadyPicking)
            _editorPreviewRestoreMetaPanelAfterChartTickPick = _editorMetaPanelOpen;
        // The media/info panel can cover part of the chart. Hide it during picking
        // so the chart and its normal bottom playback controls remain accessible.
        _editorMetaPanelOpen = false;
        _editorPreviewDialogMessage = target == 0
            ? "Picking preview START. Chart editor controls remain available."
            : "Picking preview END. Chart editor controls remain available.";
        _statusMessage = target == 0
            ? "Choose a visible chart tick to set preview START. Use normal editor controls to navigate; Escape cancels."
            : "Choose a visible chart tick to set preview END. Use normal editor controls to navigate; Escape cancels.";
    }

    private bool CancelEditorPreviewChartTickPick()
    {
        if (_editorPreviewPendingChartTickTarget < 0)
            return false;

        _editorPreviewPendingChartTickTarget = -1;
        ClearEditorPreviewChartTickPickHover();
        RestoreEditorMetaPanelAfterChartTickPick();
        _editorPreviewDialogMessage = "Preview tick selection cancelled.";
        _statusMessage = "Preview tick selection cancelled.";
        return true;
    }

    private bool HandleEditorPreviewChartTickPick(KeyboardState keyboard)
    {
        if (_editorPreviewPendingChartTickTarget < 0)
            return false;

        if (KeybindSettings.IsPressed("editor_cancel"))
        {
            CancelEditorPreviewChartTickPick();
            return true;
        }

        if (_chart is null || _tempo is null || !_chartEditorMode)
        {
            _editorPreviewPendingChartTickTarget = -1;
            _editorPreviewRestoreMetaPanelAfterChartTickPick = false;
            ClearEditorPreviewChartTickPickHover();
            return true;
        }

        var mouse = GlobalInput.Mouse;
        var previousMouse = GlobalInput.PreviousMouse;
        var leftPressed = mouse.LeftButton == ButtonState.Pressed &&
                          previousMouse.LeftButton != ButtonState.Pressed;
        if (!leftPressed)
            return false;

        var (virtualWidth, virtualHeight) = GetVirtualResolution(_graphics.Viewport.Width, _graphics.Viewport.Height);
        var point = new Vector2(
            mouse.X * virtualWidth / (float)Math.Max(1, _graphics.Viewport.Width),
            mouse.Y * virtualHeight / (float)Math.Max(1, _graphics.Viewport.Height));
        var pointerPixel = new Microsoft.Xna.Framework.Point((int)MathF.Round(point.X), (int)MathF.Round(point.Y));
        var editorLuaUiActive = _chartEditorInterfaceRuntime.IsLoaded &&
                                _chartEditorInterfaceRuntime.LastError is null;
        var chartPlayButtonBounds = GetEditorPreviewChartPlayButtonBounds(virtualWidth, virtualHeight);
        var pointerOverUi = chartPlayButtonBounds.Contains(pointerPixel) ||
                            (editorLuaUiActive && _chartEditorInterfaceRuntime.IsUiPointOverControl(point.X, point.Y)) ||
                            GetEditorTimelineScrubberBounds(virtualWidth, virtualHeight).Contains(pointerPixel) ||
                            (!editorLuaUiActive &&
                             (IsEditorToolbarPanelContains(pointerPixel, virtualWidth, virtualHeight) ||
                              IsEditorBankIndicatorArea(point, virtualWidth, virtualHeight)));
        // UI clicks are left entirely to the ordinary chart-editor dispatcher.
        // Only a click on the chart canvas itself is consumed by the tick picker.
        if (pointerOverUi)
            return false;

        var currentTick = _tempo.SecondsToTick(_playSeconds);
        var currentState = RenderGearState(currentTick);
        int? pickedTick = null;
        if (_editorPanelsViewEnabled)
        {
            // The panels view has no gameplay lanes. Its four animation columns
            // share the same vertical tick projection, so any point inside the
            // panel track area can select the snapped tick at that Y position.
            var panelsArea = GetEditorPanelsTrackAreaBounds(virtualWidth, virtualHeight);
            if (panelsArea.Contains(pointerPixel))
                pickedTick = GetEditorPanelsSnappedTickAtY(point.Y, currentTick, currentState);
        }
        else
        {
            pickedTick = FindClickedQuarterBeat(point, currentTick, currentState, out _);
        }
        if (pickedTick is null)
        {
            _editorPreviewDialogMessage = _editorPanelsViewEnabled
                ? "No chart tick found there. Click inside one of the four panel columns."
                : "No chart tick found there. Move over a lane and click a visible tick.";
            _statusMessage = _editorPreviewDialogMessage;
            return true;
        }

        var sourceDuration = EditorPreviewSourceDurationSeconds;
        if (sourceDuration <= 0)
        {
            _editorPreviewPendingChartTickTarget = -1;
            ClearEditorPreviewChartTickPickHover();
            RestoreEditorMetaPanelAfterChartTickPick();
            _editorPreviewDialogMessage = "Music preview audio is not ready.";
            _statusMessage = _editorPreviewDialogMessage;
            return true;
        }

        // Keep the selected chart tick exact (to the preview editor's millisecond
        // precision). A chart can be longer than the imported audio, so reject a
        // tick outside that audio instead of clamping it to the song's final frame.
        var pickedSeconds = GetEditorPreviewAudioTimeAtTick(pickedTick.Value);
        if (!double.IsFinite(pickedSeconds))
        {
            _editorPreviewDialogMessage = "That chart tick has no valid time. Choose another tick.";
            _statusMessage = _editorPreviewDialogMessage;
            return true;
        }
        if (pickedSeconds < 0)
        {
            _editorPreviewDialogMessage = "That chart tick is before the audio origin. Choose a later tick.";
            _statusMessage = _editorPreviewDialogMessage;
            return true;
        }
        if (pickedSeconds > sourceDuration)
        {
            _editorPreviewDialogMessage = "That chart tick is after the imported song audio. Choose an earlier tick.";
            _statusMessage = _editorPreviewDialogMessage;
            return true;
        }
        pickedSeconds = RoundEditorPreviewMilliseconds(pickedSeconds);

        var pickedStart = _editorPreviewPendingChartTickTarget == 0;
        var nextStart = RoundEditorPreviewMilliseconds(_editorPreviewStartSeconds);
        var nextEnd = RoundEditorPreviewMilliseconds(_editorPreviewEndSeconds);
        if (pickedStart)
        {
            nextStart = pickedSeconds;
            if (nextStart >= nextEnd)
                nextEnd = Math.Min(sourceDuration, nextStart + 10.0);
        }
        else
        {
            nextEnd = pickedSeconds;
            if (nextEnd <= nextStart)
                nextStart = Math.Max(0.0, nextEnd - 10.0);
        }

        // The endpoint must remain the actual tick the user clicked. At the very
        // beginning/end of the audio there may be no room for a valid range; leave
        // the picker armed and let the user choose a nearby tick instead.
        if (nextStart < 0 || nextStart >= sourceDuration || nextEnd > sourceDuration ||
            nextEnd - nextStart < 0.001)
        {
            _editorPreviewDialogMessage = pickedStart
                ? "Not enough audio after that tick to set START. Choose an earlier tick."
                : "Not enough audio before that tick to set END. Choose a later tick.";
            _statusMessage = _editorPreviewDialogMessage;
            return true;
        }

        _editorPreviewStartSeconds = nextStart;
        _editorPreviewEndSeconds = nextEnd;
        _editorPreviewRangeInitialized = true;
        SyncEditorPreviewTimeTexts();
        NormalizeEditorPreviewPlayheadToSelection();
        RefreshEditorPreviewSelectionPlayerAfterRangeChange();

        // A successfully picked chart tick is also a stop point for chart playback.
        // Pause only the chart's audio source; the preview editor's separate waveform
        // audition player is intentionally left alone. Clear deferred auto-resume state
        // so a recent seek, scrub, or wheel gesture cannot restart playback afterward.
        var chartPlaybackWasRunning = _playing || _playbackStartPending;
        _playing = false;
        _playbackStartPending = false;
        _audioResyncPending = true;
        _audio.Pause();
        _editorWheelResumeAfterScroll = false;
        _editorTimelineResumeAfterScrub = false;
        _editorSeekResumePlayback = false;

        _editorPreviewPendingChartTickTarget = -1;
        ClearEditorPreviewChartTickPickHover();
        RestoreEditorMetaPanelAfterChartTickPick();
        var pickedEndpoint = pickedStart ? "START" : "END";
        _editorPreviewDialogMessage = $"Preview {pickedEndpoint} set from chart tick {pickedTick.Value}. Press SAVE to keep this change." +
                                      (chartPlaybackWasRunning ? " Chart playback paused." : string.Empty);
        _statusMessage = $"Preview {pickedEndpoint} set from chart tick {pickedTick.Value}." +
                         (chartPlaybackWasRunning ? " Chart playback paused." : string.Empty);
        return true;
    }

    private void UpdateEditorPreviewEditorWhileChartTickPicking(double deltaSeconds)
    {
        // Keep the preview editor's display and background playback state current,
        // but leave every input event to the regular chart editor while selecting.
        UpdateEditorPreviewTimelineMotion(deltaSeconds);
        UpdateEditorPreviewWaveformCache();
        UpdateEditorPreviewSelectionPlayback();
    }

    private void ToggleChartPlaybackForPreviewTickPick()
    {
        var wasPlaying = _playing;
        if (!wasPlaying && !CanStartEditorChartPlayback())
        {
            _editorPreviewDialogMessage = _statusMessage ?? "Chart playback is not ready yet.";
            return;
        }

        // Starting chart playback should not layer it over the isolated waveform
        // audition. Keep the waveform playhead where it was so audition can resume
        // later after leaving tick-pick mode.
        if (!wasPlaying)
        {
            if (_editorPreviewSelectionPlaying)
                StopEditorPreviewSelectionPlayback(resetPlayhead: false);
            if (_editorPreviewPlayer?.IsPlaying == true)
                StopEditorPreviewPlayback();
        }

        TryToggleEditorChartPlayback();
        _editorPreviewDialogMessage = _playing
            ? "Chart playback started. Click a chart tick when you hear the desired point."
            : "Chart playback paused. The tick picker is still active.";
    }

    private void HandleEditorPreviewEditorInput(KeyboardState keyboard, KeyboardState previousKeyboard, double deltaSeconds)
    {
        UpdateEditorPreviewTimelineMotion(deltaSeconds);
        UpdateEditorPreviewWaveformCache();
        UpdateEditorPreviewSelectionPlayback();
        if (_audio.IsLoading && _editorPreviewFocusedField >= 0)
        {
            _editorPreviewFocusedField = -1;
            _editorPreviewInputReplaceOnNextKey = false;
            EndEditorTextFieldMouseSelection();
            _editorPreviewDialogMessage = "Music is still being prepared. Preview controls will be available shortly.";
        }
        UpdateEditorTextFieldMouseDrag();

        if (_editorPreviewFocusedField >= 0)
        {
            var isStart = _editorPreviewFocusedField == 0;
            var textHandled = isStart
                ? HandleEditorTextFieldKeyboard("preview-start", ref _editorPreviewStartText,
                    ref _editorPreviewStartCaret, ref _editorPreviewStartSelectionAnchor,
                    ref _editorPreviewInputReplaceOnNextKey, keyboard, previousKeyboard,
                    static (current, c) => char.IsDigit(c) || (c == '.' && !current.Contains('.')))
                : HandleEditorTextFieldKeyboard("preview-end", ref _editorPreviewEndText,
                    ref _editorPreviewEndCaret, ref _editorPreviewEndSelectionAnchor,
                    ref _editorPreviewInputReplaceOnNextKey, keyboard, previousKeyboard,
                    static (current, c) => char.IsDigit(c) || (c == '.' && !current.Contains('.')));
            if (textHandled)
            {
                _editorPreviewDialogMessage = null;
            }
            else if (KeybindSettings.IsPressed("editor_cancel"))
            {
                DiscardEditorPreviewFocusedTimeInput();
                _editorPreviewDialogMessage = null;
                return;
            }
            else if (KeybindSettings.IsTextFieldCommitPressed())
            {
                CommitEditorPreviewFocusedTime();
            }
            else if (KeybindSettings.IsPressed("editor_field_next") || KeybindSettings.IsPressed("editor_field_previous"))
            {
                if (_editorPreviewFocusedField == 0)
                {
                    CommitEditorPreviewFocusedTime();
                    if (_editorPreviewFocusedField >= 0)
                        return;
                    _editorPreviewFocusedField = 1;
                }
                else
                {
                    _editorPreviewFocusedField = 0;
                }
                _editorPreviewStartCaret = _editorPreviewStartText.Length;
                _editorPreviewEndCaret = _editorPreviewEndText.Length;
                _editorPreviewStartSelectionAnchor = _editorPreviewStartCaret;
                _editorPreviewEndSelectionAnchor = _editorPreviewEndCaret;
                var (tabWidth, tabHeight) = GetVirtualResolution(_graphics.Viewport.Width, _graphics.Viewport.Height);
                _editorTextActiveFieldId = _editorPreviewFocusedField == 0 ? "preview-start" : "preview-end";
                _editorTextActiveBounds = _editorPreviewFocusedField == 0
                    ? GetEditorPreviewStartInputBounds(tabWidth, tabHeight)
                    : GetEditorPreviewEndInputBounds(tabWidth, tabHeight);
                _editorTextMouseSelecting = false;
                _editorPreviewInputReplaceOnNextKey = false;
            }
        }
        else if (KeybindSettings.IsPressed("editor_cancel"))
        {
            CloseEditorPreviewEditor();
            return;
        }

        // Preview transport shortcuts are intentionally disabled while a time
        // field owns the keyboard, so Space remains available for text editing.
        if (_editorPreviewFocusedField < 0 && !_audio.IsLoading &&
            keyboard.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.Space) && !previousKeyboard.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.Space) &&
            !keyboard.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.LeftControl) && !keyboard.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.RightControl) &&
            !keyboard.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.LeftAlt) && !keyboard.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.RightAlt))
        {
            var shiftHeld = keyboard.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.LeftShift) || keyboard.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.RightShift);
            if (shiftHeld)
            {
                if (_editorPreviewSelectionPlaying)
                {
                    PauseEditorPreviewSelection();
                    _editorPreviewDialogMessage = "Preview paused. The playhead position was kept.";
                }
            }
            else if (_editorPreviewSelectionPlaying)
            {
                StopEditorPreviewSelectionPlayback();
                _editorPreviewDialogMessage = "Preview stopped. The playhead returned to the start marker.";
            }
            else
            {
                try
                {
                    PlayEditorPreviewSelectionFromPlayhead();
                    _editorPreviewDialogMessage = null;
                }
                catch (Exception ex)
                {
                    _editorPreviewDialogMessage = $"Could not play music preview: {ex.Message}";
                    _statusMessage = "Could not play music preview. See the preview editor for details.";
                }
            }
            return;
        }

        var mouse = GlobalInput.Mouse;
        var previousMouse = GlobalInput.PreviousMouse;
        var leftDown = mouse.LeftButton == ButtonState.Pressed;
        var leftPressed = leftDown && previousMouse.LeftButton != ButtonState.Pressed;
        var leftReleased = !leftDown && previousMouse.LeftButton == ButtonState.Pressed;
        var (virtualWidth, virtualHeight) = GetVirtualResolution(_graphics.Viewport.Width, _graphics.Viewport.Height);
        var point = new Microsoft.Xna.Framework.Point(
            (int)MathF.Round(mouse.X * virtualWidth / (float)Math.Max(1, _graphics.Viewport.Width)),
            (int)MathF.Round(mouse.Y * virtualHeight / (float)Math.Max(1, _graphics.Viewport.Height)));

        // The preview editor owns input before the regular chart-editor pointer
        // dispatcher runs. Keep the skin runtime's pointer snapshot synchronized
        // so the bottom-strip PLAY/PAUSE control receives normal hover and press input.
        UpdateChartEditorInterfacePointer(new Vector2(point.X, point.Y), leftDown);

        // Tick-pick input is routed by HandleInput before the normal editor
        // dispatcher. This method only handles the preview editor when the picker
        // is inactive.
        var dialog = GetEditorPreviewEditorDialogBounds(virtualWidth, virtualHeight);
        var timeline = GetEditorPreviewTimelineBounds(virtualWidth, virtualHeight);
        var ruler = GetEditorPreviewTimeRulerBounds(virtualWidth, virtualHeight);

        // Consume the wheel snapshot every frame so old trackpad motion cannot
        // accumulate while the modal is closed or the pointer is elsewhere.
        // SDL gives both axes in fractional notch units; X pans, Y zooms (or pans
        // with Shift). MonoGame's vertical wheel is used as a compatibility fallback.
        var (horizontalWheelDelta, verticalWheelDelta) = GlobalInput.ConsumeWheelDeltas();
        var startMarkerHit = GetEditorPreviewStartMarkerHitBounds(virtualWidth, virtualHeight);
        var endMarkerHit = GetEditorPreviewEndMarkerHitBounds(virtualWidth, virtualHeight);
        var playheadMarkerHit = GetEditorPreviewPlayheadMarkerHitBounds(virtualWidth, virtualHeight);
        var pointerOverTimeline = timeline.Contains(point) || ruler.Contains(point) ||
                                  startMarkerHit.Contains(point) || endMarkerHit.Contains(point) ||
                                  playheadMarkerHit.Contains(point);
        var hasWheelGesture = Math.Abs(horizontalWheelDelta) >= 0.0001 ||
                              Math.Abs(verticalWheelDelta) >= 0.0001;
        if (pointerOverTimeline && EditorPreviewSourceDurationSeconds > 0 && hasWheelGesture)
        {
            if (Math.Abs(horizontalWheelDelta) >= 0.0001)
                ScrollEditorPreviewTimelineHorizontal(horizontalWheelDelta);

            if (Math.Abs(verticalWheelDelta) >= 0.0001)
            {
                var shiftHeld = keyboard.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.LeftShift) ||
                                keyboard.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.RightShift);
                if (shiftHeld)
                    ScrollEditorPreviewTimeline(verticalWheelDelta);
                else
                {
                    var zoomAnchorBounds = timeline.Contains(point) || startMarkerHit.Contains(point) ||
                                           endMarkerHit.Contains(point) || playheadMarkerHit.Contains(point)
                        ? timeline
                        : ruler;
                    ZoomEditorPreviewTimeline(verticalWheelDelta, point.X, zoomAnchorBounds, timeline);
                }
            }
            // Consume every gesture over the timeline, including when the view is
            // already at a zoom or scroll limit. Never let a wheel event fall through
            // to chart seeking or another editor action.
            return;
        }

        // Middle-button dragging pans the visible time window without moving
        // any of the three playback markers.
        var middleDown = mouse.MiddleButton == ButtonState.Pressed;
        var middlePressed = middleDown && previousMouse.MiddleButton != ButtonState.Pressed;
        if (_editorPreviewDraggingTimelinePan)
        {
            if (middleDown)
            {
                PanEditorPreviewTimeline(point.X - _editorPreviewTimelinePanLastX, timeline);
                _editorPreviewTimelinePanLastX = point.X;
            }
            else
            {
                _editorPreviewDraggingTimelinePan = false;
            }
            return;
        }
        if (middlePressed && timeline.Contains(point) && EditorPreviewSourceDurationSeconds > 0)
        {
            _editorPreviewDraggingTimelinePan = true;
            _editorPreviewTimelinePanLastX = point.X;
            return;
        }

        if (_audio.IsLoading)
        {
            _editorPreviewFocusedField = -1;
            _editorPreviewDraggingStart = false;
            _editorPreviewDraggingEnd = false;
            _editorPreviewDraggingPlayhead = false;
            _editorPreviewStartDragPointerOffsetX = 0;
            _editorPreviewEndDragPointerOffsetX = 0;
            _editorPreviewWasPlayingBeforePlayheadDrag = false;
            if (leftPressed && GetEditorPreviewCloseActionButtonBounds(virtualWidth, virtualHeight).Contains(point))
            {
                CloseEditorPreviewEditor();
                return;
            }
            if (leftPressed && GetEditorPreviewPlayButtonBounds(virtualWidth, virtualHeight).Contains(point))
                _editorPreviewDialogMessage = "Music is still being prepared. Preview controls will be available shortly.";
            return;
        }

        // Losing focus is a click-away action, not a hover action. Pointer motion
        // outside the field must not commit or discard the user's in-progress text.
        if (_editorPreviewFocusedField >= 0 && leftPressed)
        {
            var sameFieldClicked = (_editorPreviewFocusedField == 0 && GetEditorPreviewStartInputBounds(virtualWidth, virtualHeight).Contains(point)) ||
                                   (_editorPreviewFocusedField == 1 && GetEditorPreviewEndInputBounds(virtualWidth, virtualHeight).Contains(point));
            if (!sameFieldClicked)
            {
                CommitEditorPreviewFocusedTime();
                if (_editorPreviewFocusedField >= 0)
                {
                    DiscardEditorPreviewFocusedTimeInput();
                    _editorPreviewDialogMessage = "Invalid time was discarded; the previous value was kept.";
                }
            }
        }

        if (_editorPreviewDraggingStart || _editorPreviewDraggingEnd || _editorPreviewDraggingPlayhead)
        {
            if (leftDown)
            {
                if (_editorPreviewDraggingPlayhead)
                    UpdateEditorPreviewPlayheadFromTimeline(point.X, virtualWidth, virtualHeight);
                else
                    UpdateEditorPreviewRangeFromSlider(point.X, virtualWidth, virtualHeight);
            }
            if (leftReleased)
            {
                if (_editorPreviewDraggingStart || _editorPreviewDraggingEnd)
                    RefreshEditorPreviewSelectionPlayerAfterRangeChange();
                if (_editorPreviewDraggingPlayhead)
                    SeekEditorPreviewPlayhead(_editorPreviewWasPlayingBeforePlayheadDrag);
                _editorPreviewDraggingStart = false;
                _editorPreviewDraggingEnd = false;
                _editorPreviewDraggingPlayhead = false;
                _editorPreviewStartDragPointerOffsetX = 0;
                _editorPreviewEndDragPointerOffsetX = 0;
                _editorPreviewWasPlayingBeforePlayheadDrag = false;
            }
            return;
        }
        if (!leftPressed)
            return;

        if (GetEditorPreviewCloseActionButtonBounds(virtualWidth, virtualHeight).Contains(point))
        {
            CloseEditorPreviewEditor();
            return;
        }
        if (GetEditorPreviewPlayButtonBounds(virtualWidth, virtualHeight).Contains(point))
        {
            if (_editorPreviewSelectionPlaying)
                return;
            if (_editorPreviewFocusedField >= 0)
            {
                CommitEditorPreviewFocusedTime();
                if (_editorPreviewFocusedField >= 0)
                    return;
            }
            try { PlayEditorPreviewSelectionFromPlayhead(); }
            catch (Exception ex)
            {
                _editorPreviewDialogMessage = $"Could not play music preview: {ex.Message}";
                _statusMessage = "Could not play music preview. See the preview editor for details.";
            }
            return;
        }
        if (GetEditorPreviewPauseButtonBounds(virtualWidth, virtualHeight).Contains(point))
        {
            if (_editorPreviewSelectionPlaying)
            {
                PauseEditorPreviewSelection();
                _editorPreviewDialogMessage = "Preview paused. The playhead position was kept.";
            }
            return;
        }
        if (GetEditorPreviewStopButtonBounds(virtualWidth, virtualHeight).Contains(point))
        {
            StopEditorPreviewSelectionPlayback();
            _editorPreviewDialogMessage = "Preview stopped. The playhead returned to the start marker.";
            return;
        }
        if (GetEditorPreviewSaveButtonBounds(virtualWidth, virtualHeight).Contains(point))
        {
            if (_editorPreviewFocusedField >= 0)
            {
                CommitEditorPreviewFocusedTime();
                if (_editorPreviewFocusedField >= 0)
                {
                    DiscardEditorPreviewFocusedTimeInput();
                    _editorPreviewDialogMessage = "Invalid time was discarded; the previous value was kept.";
                }
            }
            try { SaveEditorPreview(); }
            catch (Exception ex)
            {
                _editorPreviewDialogMessage = $"Could not save music preview: {ex.Message}";
                _statusMessage = "Could not save music preview. See the preview editor for details.";
            }
            return;
        }
        if (GetEditorPreviewPickStartButtonBounds(virtualWidth, virtualHeight).Contains(point))
        {
            if (_audio.IsLoaded && EditorPreviewSourceDurationSeconds > 0)
                BeginEditorPreviewChartTickPick(0);
            else
                _editorPreviewDialogMessage = "Import the full song audio before selecting preview times from the chart.";
            return;
        }
        if (GetEditorPreviewPickEndButtonBounds(virtualWidth, virtualHeight).Contains(point))
        {
            if (_audio.IsLoaded && EditorPreviewSourceDurationSeconds > 0)
                BeginEditorPreviewChartTickPick(1);
            else
                _editorPreviewDialogMessage = "Import the full song audio before selecting preview times from the chart.";
            return;
        }
        if (GetEditorPreviewStartInputBounds(virtualWidth, virtualHeight).Contains(point))
        {
            if (_editorPreviewFocusedField >= 0 && _editorPreviewFocusedField != 0)
            {
                CommitEditorPreviewFocusedTime();
                if (_editorPreviewFocusedField >= 0)
                    return;
            }
            var bounds = GetEditorPreviewStartInputBounds(virtualWidth, virtualHeight);
            _editorPreviewFocusedField = 0;
            _editorPreviewStartCaret = GetEditorPreviewTextCaretIndex(bounds, _editorPreviewStartText, point, GetEditorPreviewUiScale(virtualWidth, virtualHeight));
            _editorPreviewStartSelectionAnchor = _editorPreviewStartCaret;
            _editorPreviewInputReplaceOnNextKey = false;
            BeginEditorTextFieldMouseSelection("preview-start", bounds, _editorPreviewStartCaret, ref _editorPreviewStartSelectionAnchor);
            _editorPreviewDialogMessage = null;
            return;
        }
        if (GetEditorPreviewEndInputBounds(virtualWidth, virtualHeight).Contains(point))
        {
            if (_editorPreviewFocusedField >= 0 && _editorPreviewFocusedField != 1)
            {
                CommitEditorPreviewFocusedTime();
                if (_editorPreviewFocusedField >= 0)
                    return;
            }
            var bounds = GetEditorPreviewEndInputBounds(virtualWidth, virtualHeight);
            _editorPreviewFocusedField = 1;
            _editorPreviewEndCaret = GetEditorPreviewTextCaretIndex(bounds, _editorPreviewEndText, point, GetEditorPreviewUiScale(virtualWidth, virtualHeight));
            _editorPreviewEndSelectionAnchor = _editorPreviewEndCaret;
            _editorPreviewInputReplaceOnNextKey = false;
            BeginEditorTextFieldMouseSelection("preview-end", bounds, _editorPreviewEndCaret, ref _editorPreviewEndSelectionAnchor);
            _editorPreviewDialogMessage = null;
            return;
        }
        if (timeline.Contains(point) || startMarkerHit.Contains(point) ||
            endMarkerHit.Contains(point) || playheadMarkerHit.Contains(point))
        {
            if (_editorPreviewFocusedField >= 0)
            {
                CommitEditorPreviewFocusedTime();
                if (_editorPreviewFocusedField >= 0)
                    return;
            }

            var duration = EditorPreviewSourceDurationSeconds;
            if (duration > 0)
            {
                var clickedTime = GetEditorPreviewTimelineTimeAtX(point.X, timeline);
                if (startMarkerHit.Contains(point))
                {
                    _editorPreviewDraggingStart = true;
                    _editorPreviewStartDragPointerOffsetX = point.X - GetEditorPreviewMarkerScreenX(
                        _editorPreviewStartSeconds, virtualWidth, virtualHeight);
                    UpdateEditorPreviewRangeFromSlider(point.X, virtualWidth, virtualHeight);
                }
                else if (endMarkerHit.Contains(point))
                {
                    _editorPreviewDraggingEnd = true;
                    _editorPreviewEndDragPointerOffsetX = point.X - GetEditorPreviewMarkerScreenX(
                        _editorPreviewEndSeconds, virtualWidth, virtualHeight);
                    UpdateEditorPreviewRangeFromSlider(point.X, virtualWidth, virtualHeight);
                }
                else
                {
                    _editorPreviewWasPlayingBeforePlayheadDrag = _editorPreviewSelectionPlaying;
                    if (_editorPreviewWasPlayingBeforePlayheadDrag)
                        PauseEditorPreviewSelection();
                    _editorPreviewDraggingPlayhead = true;
                    _editorPreviewPlayheadSeconds = clickedTime;
                    _editorPreviewDialogMessage = null;
                }
            }
            return;
        }
        if (!dialog.Contains(point))
            return;
        _editorPreviewFocusedField = -1;
    }

    private void UpdateEditorPreviewPlayheadFromTimeline(int mouseX, int virtualWidth, int virtualHeight)
    {
        var duration = EditorPreviewSourceDurationSeconds;
        if (duration <= 0)
            return;
        var timeline = GetEditorPreviewTimelineBounds(virtualWidth, virtualHeight);
        _editorPreviewPlayheadSeconds = GetEditorPreviewTimelineTimeAtX(mouseX, timeline);
    }

    private void UpdateEditorPreviewRangeFromSlider(int mouseX, int virtualWidth, int virtualHeight)
    {
        var duration = EditorPreviewSourceDurationSeconds;
        if (duration <= 0)
            return;
        var slider = GetEditorPreviewSliderBounds(virtualWidth, virtualHeight);
        var adjustedMouseX = _editorPreviewDraggingStart
            ? mouseX - _editorPreviewStartDragPointerOffsetX
            : _editorPreviewDraggingEnd
                ? mouseX - _editorPreviewEndDragPointerOffsetX
                : mouseX;
        var value = RoundEditorPreviewMilliseconds(GetEditorPreviewTimelineTimeAtX(adjustedMouseX, slider));
        const double minimumRange = 0.001;
        if (_editorPreviewDraggingStart)
            _editorPreviewStartSeconds = Math.Clamp(value, 0, Math.Max(0, _editorPreviewEndSeconds - minimumRange));
        else if (_editorPreviewDraggingEnd)
            _editorPreviewEndSeconds = Math.Clamp(value, Math.Min(duration, _editorPreviewStartSeconds + minimumRange), duration);
        SyncEditorPreviewTimeTexts();
        NormalizeEditorPreviewPlayheadToSelection();
        _editorPreviewDialogMessage = null;
    }

    private Rectangle GetEditorMediaMusicLabelBounds(int virtualWidth, int virtualHeight)
    {
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        return new Rectangle(content.X, content.Y, content.Width, Math.Max(1, (int)MathF.Round(26f * unit)));
    }

    private Rectangle GetEditorMediaMusicStatusBounds(int virtualWidth, int virtualHeight)
    {
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var buttonWidth = Math.Max(1, (int)MathF.Round(150f * unit));
        var gap = Math.Max(4, (int)MathF.Round(8f * unit));
        var y = content.Y + (int)MathF.Round(28f * unit);
        var height = Math.Max(1, (int)MathF.Round(36f * unit));
        return new Rectangle(content.X, y, Math.Max(1, content.Width - buttonWidth - gap), height);
    }

    private Rectangle GetEditorMediaMusicButtonBounds(int virtualWidth, int virtualHeight)
    {
        var status = GetEditorMediaMusicStatusBounds(virtualWidth, virtualHeight);
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var gap = Math.Max(4, (int)MathF.Round(8f * unit));
        var width = Math.Max(1, (int)MathF.Round(150f * unit));
        return new Rectangle(status.Right + gap, status.Y, Math.Max(1, content.Right - status.Right - gap), status.Height);
    }

    private Rectangle GetEditorMediaPreviewLabelBounds(int virtualWidth, int virtualHeight)
    {
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var y = content.Y + (int)MathF.Round(140f * unit);
        return new Rectangle(content.X, y, content.Width, Math.Max(1, (int)MathF.Round(26f * unit)));
    }

    private Rectangle GetEditorMediaVolumeLabelBounds(int virtualWidth, int virtualHeight)
    {
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var y = content.Y + (int)MathF.Round(79f * unit);
        return new Rectangle(content.X, y, content.Width, Math.Max(1, (int)MathF.Round(26f * unit)));
    }

    private Rectangle GetEditorMediaVolumeSliderBounds(int virtualWidth, int virtualHeight)
    {
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var y = content.Y + (int)MathF.Round(108f * unit);
        var height = Math.Max(1, (int)MathF.Round(28f * unit));
        return new Rectangle(content.X, y, content.Width, height);
    }

    private Rectangle GetEditorMediaPreviewEditButtonBounds(int virtualWidth, int virtualHeight)
    {
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var label = GetEditorMediaPreviewLabelBounds(virtualWidth, virtualHeight);
        var gap = Math.Max(4, (int)MathF.Round(6f * unit));
        var height = Math.Max(1, (int)MathF.Round(40f * unit));
        var width = Math.Max(1, (int)MathF.Round(92f * unit));
        return new Rectangle(content.X, label.Bottom + (int)MathF.Round(2f * unit), width, height);
    }

    private Rectangle GetEditorMediaPreviewPlayButtonBounds(int virtualWidth, int virtualHeight)
    {
        var edit = GetEditorMediaPreviewEditButtonBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var gap = Math.Max(4, (int)MathF.Round(6f * unit));
        var width = Math.Max(1, (int)MathF.Round(118f * unit));
        return new Rectangle(edit.Right + gap, edit.Y, width, edit.Height);
    }

    private Rectangle GetEditorMediaPreviewProgressBounds(int virtualWidth, int virtualHeight)
    {
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var play = GetEditorMediaPreviewPlayButtonBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var gap = Math.Max(4, (int)MathF.Round(8f * unit));
        return new Rectangle(play.Right + gap, play.Y, Math.Max(1, content.Right - play.Right - gap), play.Height);
    }

    private Rectangle GetEditorMediaArtworkCardBounds(bool albumBig, int virtualWidth, int virtualHeight)
    {
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var top = content.Y + (int)MathF.Round(210f * unit);
        var gap = Math.Max(8, (int)MathF.Round(16f * unit));
        var supportsAlbumBig = _editorBundleTemplate != ChartBundleTemplate.Tsts;
        var cardWidth = supportsAlbumBig
            ? Math.Max(1, (content.Width - gap) / 2)
            : Math.Max(1, content.Width);
        var x = albumBig ? content.X + cardWidth + gap : content.X;
        return new Rectangle(x, top, cardWidth, Math.Max(1, content.Bottom - top));
    }

    private Rectangle GetEditorMediaArtworkFrameBounds(bool albumBig, int virtualWidth, int virtualHeight)
    {
        var card = GetEditorMediaArtworkCardBounds(albumBig, virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var labelAndGaps = (int)MathF.Round(34f * unit);
        var buttonAndGaps = (int)MathF.Round(58f * unit);
        var maxWidth = Math.Max(1, card.Width - (int)MathF.Round(20f * unit));
        var maxHeight = Math.Max(1, card.Height - labelAndGaps - buttonAndGaps);
        var size = Math.Max(1, (int)MathF.Round(Math.Min(220f * unit, Math.Min(maxWidth, maxHeight))));
        size = Math.Min(size, Math.Min(maxWidth, maxHeight));
        return new Rectangle(card.X + (card.Width - size) / 2,
            card.Y + labelAndGaps, size, size);
    }

    private Rectangle GetEditorMediaArtworkPickButtonBounds(bool albumBig, int virtualWidth, int virtualHeight)
    {
        var frame = GetEditorMediaArtworkFrameBounds(albumBig, virtualWidth, virtualHeight);
        var card = GetEditorMediaArtworkCardBounds(albumBig, virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var gap = Math.Max(4, (int)MathF.Round(8f * unit));
        var height = Math.Max(1, (int)MathF.Round(34f * unit));
        var y = frame.Bottom + gap;
        return new Rectangle(card.X + (int)MathF.Round(8f * unit), y,
            Math.Max(1, card.Width - (int)MathF.Round(16f * unit)), height);
    }

    private void DrawEditorMediaTab(int virtualWidth, int virtualHeight)
    {
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);

        DrawEditorPreviewLabel("MUSIC", GetEditorMediaMusicLabelBounds(virtualWidth, virtualHeight), Muted, unit);
        var musicStatus = HasEditorMediaResource("music") ? "Song audio is loaded" : "No song audio selected";
        DrawEditorMetaTextField(GetEditorMediaMusicStatusBounds(virtualWidth, virtualHeight), musicStatus,
            editing: false, editable: false, new Color(100, 100, 102, 245));
        DrawEditorMetaButton(GetEditorMediaMusicButtonBounds(virtualWidth, virtualHeight),
            HasEditorMediaResource("music") ? "CHANGE" : "IMPORT",
            new Color(28, 28, 30, 250), Active);

        var volumeLabel = _editorOriginalMusicSourceBytes is { Length: > 0 }
            ? $"MUSIC VOLUME  {_editorMusicVolumePendingPercent}%"
            : "MUSIC VOLUME  NO AUDIO";
        DrawEditorPreviewLabel(volumeLabel, GetEditorMediaVolumeLabelBounds(virtualWidth, virtualHeight), Muted, unit);
        DrawEditorMediaVolumeSlider(GetEditorMediaVolumeSliderBounds(virtualWidth, virtualHeight), unit,
            _editorOriginalMusicSourceBytes is { Length: > 0 });

        DrawEditorPreviewLabel("PREVIEW", GetEditorMediaPreviewLabelBounds(virtualWidth, virtualHeight), Muted, unit);
        DrawEditorMetaButton(GetEditorMediaPreviewEditButtonBounds(virtualWidth, virtualHeight), "EDIT",
            new Color(28, 28, 30, 250), Active);
        DrawEditorMetaButton(GetEditorMediaPreviewPlayButtonBounds(virtualWidth, virtualHeight),
            _editorPreviewPlayer?.IsPlaying == true ? "STOP" : "PLAY",
            new Color(28, 28, 30, 250), new Color(52, 211, 153));
        DrawEditorMediaPreviewProgress(GetEditorMediaPreviewProgressBounds(virtualWidth, virtualHeight), unit);

        DrawEditorMediaArtworkCard("album", virtualWidth, virtualHeight, unit);
        if (_editorBundleTemplate != ChartBundleTemplate.Tsts)
            DrawEditorMediaArtworkCard("album_big", virtualWidth, virtualHeight, unit);
    }

    private void DrawEditorMediaVolumeSlider(Rectangle bounds, float unit, bool enabled)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;
        var barHeight = Math.Max(5, (int)MathF.Round(6f * unit));
        var barY = bounds.Y + (bounds.Height - barHeight) / 2;
        var bar = new Rectangle(bounds.X, barY, bounds.Width, barHeight);
        _spriteBatch.Draw(_pixel, bar, new Color(58, 58, 60, 245));
        if (!enabled)
            return;

        var ratio = Math.Clamp(_editorMusicVolumePendingPercent / 100f, 0f, 1f);
        var fillWidth = Math.Clamp((int)MathF.Round(bar.Width * ratio), 0, bar.Width);
        if (fillWidth > 0)
            _spriteBatch.Draw(_pixel, new Rectangle(bar.X, bar.Y, fillWidth, bar.Height), Active);

        var thumbWidth = Math.Max(10, (int)MathF.Round(14f * unit));
        var thumbHeight = Math.Max(16, (int)MathF.Round(22f * unit));
        var thumbX = Math.Clamp(bar.X + fillWidth - thumbWidth / 2,
            bounds.X, Math.Max(bounds.X, bounds.Right - thumbWidth));
        var thumb = new Rectangle(thumbX, bounds.Y + (bounds.Height - thumbHeight) / 2,
            thumbWidth, thumbHeight);
        _spriteBatch.Draw(_pixel, thumb, Color.White);
    }

    private bool HandleEditorMusicVolumeFallbackPointer(
        Microsoft.Xna.Framework.Point point, bool leftPressed, bool leftDown, bool leftReleased,
        int virtualWidth, int virtualHeight)
    {
        if (_editorMusicVolumeSliderDragging)
        {
            if (leftDown || leftReleased)
                UpdateEditorMusicVolumePendingFromPointer(point, virtualWidth, virtualHeight);
            if (leftReleased)
            {
                _editorMusicVolumeSliderDragging = false;
                try { ApplyEditorMusicVolumePendingPercent(); }
                catch (Exception ex)
                {
                    _editorMusicVolumePendingPercent = _editorMusicVolumePercent;
                    ApplyEditorMusicPlaybackVolume();
                    _statusMessage = $"Could not change song volume: {ex.Message}";
                }
                return true;
            }
            return leftDown;
        }

        if (!leftPressed || !_editorMetaPanelOpen ||
            !string.Equals(_editorMetaCategory, "MEDIA", StringComparison.OrdinalIgnoreCase) ||
            _editorOriginalMusicSourceBytes is not { Length: > 0 } ||
            !GetEditorMediaVolumeSliderBounds(virtualWidth, virtualHeight).Contains(point))
            return false;

        _editorMusicVolumeSliderDragging = true;
        UpdateEditorMusicVolumePendingFromPointer(point, virtualWidth, virtualHeight);
        return true;
    }

    private void UpdateEditorMusicVolumePendingFromPointer(
        Microsoft.Xna.Framework.Point point, int virtualWidth, int virtualHeight)
    {
        var bounds = GetEditorMediaVolumeSliderBounds(virtualWidth, virtualHeight);
        if (bounds.Width <= 0)
            return;
        var ratio = Math.Clamp((point.X - bounds.X) / (double)bounds.Width, 0.0, 1.0);
        SetEditorMusicVolumePendingPercent(ratio * 100.0);
    }

    private void DrawEditorMediaPreviewProgress(Rectangle bounds, float unit)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;
        _spriteBatch.Draw(_pixel, bounds, new Color(12, 12, 13, 250));
        var horizontalPadding = Math.Max(6, (int)MathF.Round(8f * unit));
        var barHeight = Math.Max(5, (int)MathF.Round(7f * unit));
        var bar = new Rectangle(bounds.X + horizontalPadding, bounds.Y + (int)MathF.Round(5f * unit),
            Math.Max(1, bounds.Width - horizontalPadding * 2), barHeight);
        _spriteBatch.Draw(_pixel, bar, new Color(58, 58, 60, 245));
        var duration = GetEditorSavedPreviewDurationSeconds();
        var elapsed = GetEditorPreviewPlaybackElapsedSeconds();
        if (duration > 0)
        {
            var fillWidth = Math.Clamp((int)Math.Round(bar.Width * Math.Clamp(elapsed / duration, 0, 1)), 0, bar.Width);
            if (fillWidth > 0)
                _spriteBatch.Draw(_pixel, new Rectangle(bar.X, bar.Y, fillWidth, bar.Height), Active);
        }
        var timeText = duration > 0
            ? $"{FormatEditorPreviewTimestamp(elapsed)} / {FormatEditorPreviewTimestamp(duration)}"
            : "00:00.000 / --:--.---";
        DrawEditorPreviewLabel(timeText,
            new Rectangle(bounds.X + horizontalPadding, bar.Bottom + (int)MathF.Round(2f * unit),
                Math.Max(1, bounds.Width - horizontalPadding * 2), Math.Max(1, bounds.Bottom - bar.Bottom - (int)MathF.Round(2f * unit))),
            Text, unit);
    }

    private void DrawEditorMediaArtworkCard(string field, int virtualWidth, int virtualHeight, float unit)
    {
        var isBig = string.Equals(field, "album_big", StringComparison.OrdinalIgnoreCase);
        var card = GetEditorMediaArtworkCardBounds(isBig, virtualWidth, virtualHeight);
        var frame = GetEditorMediaArtworkFrameBounds(isBig, virtualWidth, virtualHeight);
        var label = isBig ? "ALBUM BIG" : "ALBUM";
        DrawEditorPreviewLabel(label,
            new Rectangle(card.X, card.Y, card.Width, Math.Max(1, (int)MathF.Round(26f * unit))), Muted, unit);

        _spriteBatch.Draw(_pixel, frame, new Color(12, 12, 13, 250));
        var edge = Math.Max(1, (int)MathF.Round(2f * unit));
        var accent = Active;
        var imageBounds = new Rectangle(frame.X + edge, frame.Y + edge,
            Math.Max(1, frame.Width - edge * 2), Math.Max(1, frame.Height - edge * 2));

        var texture = GetEditorArtworkTexture(field);
        if (texture is not null && texture.Width > 0 && texture.Height > 0)
        {
            // Fit the whole image inside the square frame. The texture's aspect
            // ratio is preserved; no source rectangle is used, so nothing crops.
            var scale = Math.Min(imageBounds.Width / (float)texture.Width, imageBounds.Height / (float)texture.Height);
            var width = Math.Max(1, (int)MathF.Round(texture.Width * scale));
            var height = Math.Max(1, (int)MathF.Round(texture.Height * scale));
            var fitted = new Rectangle(imageBounds.X + (imageBounds.Width - width) / 2,
                imageBounds.Y + (imageBounds.Height - height) / 2, width, height);
            _spriteBatch.Draw(texture, fitted, Color.White);
        }
        else
        {
            var placeholder = isBig && _editorBundleTemplate == ChartBundleTemplate.Tsts
                ? "NOT SUPPORTED BY TSTS"
                : HasEditorMediaResource(field) ? "IMAGE PREVIEW UNAVAILABLE" : "NO IMAGE SET";
            DrawEditorPreviewLabel(placeholder, frame, Muted, unit);
        }

        // Draw the frame last so a full-square image cannot cover its border.
        _spriteBatch.Draw(_pixel, new Rectangle(frame.X, frame.Y, frame.Width, edge), accent);
        _spriteBatch.Draw(_pixel, new Rectangle(frame.X, frame.Bottom - edge, frame.Width, edge), accent);
        _spriteBatch.Draw(_pixel, new Rectangle(frame.X, frame.Y, edge, frame.Height), accent);
        _spriteBatch.Draw(_pixel, new Rectangle(frame.Right - edge, frame.Y, edge, frame.Height), accent);

        if (isBig && _editorBundleTemplate == ChartBundleTemplate.Tsts)
        {
            DrawEditorMetaButton(GetEditorMediaArtworkPickButtonBounds(isBig, virtualWidth, virtualHeight),
                "NOT SUPPORTED BY TSTS", new Color(30, 30, 32, 245), new Color(100, 100, 102));
        }
        else
        {
            DrawEditorMetaButton(GetEditorMediaArtworkPickButtonBounds(isBig, virtualWidth, virtualHeight),
                HasEditorMediaResource(field) ? "CHANGE IMAGE" : "SET IMAGE",
                new Color(28, 28, 30, 250), accent);
        }
    }

    private Texture2D? GetEditorArtworkTexture(string field)
    {
        var isBig = string.Equals(field, "album_big", StringComparison.OrdinalIgnoreCase);
        if (isBig && _editorBundleTemplate == ChartBundleTemplate.Tsts)
            return null;

        var sharedBytes = isBig ? _editorSharedAlbumBigBytes : _editorSharedAlbumBytes;
        var cache = isBig ? _editorAlbumBigArtworkCache : _editorAlbumArtworkCache;
        if (sharedBytes is { Length: > 0 })
        {
            if (cache.IsMemorySource && ReferenceEquals(cache.SourceBytes, sharedBytes))
                return cache.Texture;
            cache.Reset();
            cache.IsMemorySource = true;
            cache.SourceBytes = sharedBytes;
            cache.Texture = LoadEditorArtworkTexture(sharedBytes, GetEditorArtworkExtensionHint(field));
            return cache.Texture;
        }

        var path = GetEditorArtworkAssetPath(field);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            if (cache.Texture is not null || cache.SourceBytes is not null || cache.SourcePath is not null)
                cache.Reset();
            return null;
        }

        FileInfo info;
        try { info = new FileInfo(path); }
        catch { return null; }
        if (!info.Exists)
            return null;
        if (!cache.IsMemorySource && string.Equals(cache.SourcePath, path, StringComparison.OrdinalIgnoreCase) &&
            cache.SourceLength == info.Length && cache.SourceLastWriteUtc == info.LastWriteTimeUtc)
            return cache.Texture;

        cache.Reset();
        cache.SourcePath = path;
        cache.SourceLength = info.Length;
        cache.SourceLastWriteUtc = info.LastWriteTimeUtc;
        try
        {
            // Prefer MonoGame's built-in decoder for PNG/JPEG. Some bundle templates
            // store WebP covers, which MonoGame does not decode; fall back to FFmpeg
            // only for those formats (or any other unsupported image type).
            using var stream = File.OpenRead(path);
            cache.Texture = NativeResourceDiagnostics.TrackTexture(Texture2D.FromStream(_graphics, stream), NativeResourceCategory.Editor);
        }
        catch
        {
            try
            {
                cache.Texture = LoadEditorArtworkTexture(File.ReadAllBytes(path), Path.GetExtension(path));
            }
            catch
            {
                cache.Texture?.Dispose();
                cache.Texture = null;
            }
        }
        return cache.Texture;
    }

    private string GetEditorArtworkExtensionHint(string field)
    {
        var assetPath = string.Equals(field, "album_big", StringComparison.OrdinalIgnoreCase)
            ? _chart?.AlbumBigPath
            : _chart?.AlbumPath;
        var extension = string.IsNullOrWhiteSpace(assetPath) ? string.Empty : Path.GetExtension(assetPath);
        if (!string.IsNullOrWhiteSpace(extension) && extension.Length <= 10)
            return extension;
        return _editorBundleTemplate == ChartBundleTemplate.Tsts ? ".webp" : ".png";
    }

    private Texture2D? LoadEditorArtworkTexture(byte[] bytes, string extensionHint)
    {
        if (bytes.Length == 0)
            return null;

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return NativeResourceDiagnostics.TrackTexture(Texture2D.FromStream(_graphics, stream), NativeResourceCategory.Editor);
        }
        catch
        {
            // Try the Windows image decoder next. MonoGame's decoder does not
            // support every ordinary image format (or extensionless legacy art),
            // but GDI+ can normalize PNG/JPEG/BMP/GIF/TIFF images to PNG without
            // asking the user to install FFmpeg. WebP continues to the FFmpeg path.
        }

        var normalizedPng = TryNormalizeEditorArtworkToPng(bytes);
        if (normalizedPng is { Length: > 0 })
        {
            try
            {
                using var normalizedStream = new MemoryStream(normalizedPng, writable: false);
                return NativeResourceDiagnostics.TrackTexture(Texture2D.FromStream(_graphics, normalizedStream), NativeResourceCategory.Editor);
            }
            catch
            {
                // Fall through to FFmpeg only if the normalized image still cannot load.
            }
        }

        var ffmpeg = FindTstsFfmpegExecutable();
        if (ffmpeg is null)
            return null;
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "TSC-artwork-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var extension = string.IsNullOrWhiteSpace(extensionHint) || extensionHint.Length > 10
            ? ".img"
            : extensionHint.StartsWith('.') ? extensionHint : "." + extensionHint;
        var inputPath = Path.Combine(temporaryDirectory, "input" + extension);
        var outputPath = Path.Combine(temporaryDirectory, "artwork.png");
        try
        {
            File.WriteAllBytes(inputPath, bytes);
            RunEditorPreviewFfmpeg(ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error", "-y", "-i", inputPath,
                "-map", "0:v:0", "-frames:v", "1", "-f", "image2", outputPath
            });
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                return null;
            using var stream = File.OpenRead(outputPath);
            return NativeResourceDiagnostics.TrackTexture(Texture2D.FromStream(_graphics, stream), NativeResourceCategory.Editor);
        }
        catch
        {
            return null;
        }
        finally
        {
            try { Directory.Delete(temporaryDirectory, recursive: true); } catch { }
        }
    }

    private static byte[]? TryNormalizeEditorArtworkToPng(byte[] bytes)
    {
        if (bytes.Length == 0)
            return null;

        try
        {
            using var source = new MemoryStream(bytes, writable: false);
            using var image = System.Drawing.Image.FromStream(source, useEmbeddedColorManagement: true, validateImageData: true);
            if (image.Width <= 0 || image.Height <= 0)
                return null;

            using var bitmap = new System.Drawing.Bitmap(image.Width, image.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(System.Drawing.Color.Transparent);
                graphics.DrawImage(image, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
            }

            using var output = new MemoryStream();
            bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
            return output.ToArray();
        }
        catch
        {
            // Formats such as WebP that GDI+ cannot read are handled by FFmpeg below.
            return null;
        }
    }

    private string? GetEditorArtworkAssetPath(string field)
    {
        var isBig = string.Equals(field, "album_big", StringComparison.OrdinalIgnoreCase);
        if (isBig && _editorBundleTemplate == ChartBundleTemplate.Tsts)
            return null;

        var directory = GetEditorPreviewAssetDirectory();
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        var normalizedField = isBig ? "album_big" : "album";
        var cacheKey = new EditorArtworkPathCacheKey(normalizedField, directory, _editorBundleTemplate,
            _chart?.AlbumPath, _chart?.AlbumBigPath);
        if (_editorArtworkPathCache.TryGetValue(cacheKey, out var cachedPath))
            return cachedPath;

        string? resolvedPath = null;
        if (Directory.Exists(directory))
        {
            // Honor the path actually stored in chart metadata first. Template names
            // below are only fallbacks for legacy or not-yet-populated metadata.
            var assetReference = isBig ? _chart?.AlbumBigPath : _chart?.AlbumPath;
            if (!string.IsNullOrWhiteSpace(assetReference))
            {
                try
                {
                    var referencedPath = Path.IsPathRooted(assetReference)
                        ? assetReference
                        : Path.Combine(directory, assetReference);
                    if (File.Exists(referencedPath))
                        resolvedPath = referencedPath;
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    // Continue with template default names when metadata path is invalid.
                }
            }

            if (resolvedPath is null)
            {
                var names = normalizedField switch
                {
                    "album" when _editorBundleTemplate == ChartBundleTemplate.Tsts => new[] { "cover.webp", "album.webp", "album.png", "album.jpg", "album.jpeg" },
                    "album" when _editorBundleTemplate == ChartBundleTemplate.Tsc => new[] { "album.png", "album.webp", "album.jpg", "album.jpeg" },
                    "album" => new[] { "album", "album.png", "album.webp", "album.jpg", "album.jpeg" },
                    "album_big" when _editorBundleTemplate == ChartBundleTemplate.Tsc => new[] { "album_big.png", "album_big.webp", "album_big.jpg", "album_big.jpeg" },
                    "album_big" => new[] { "album_big", "album_big.png", "album_big.webp", "album_big.jpg", "album_big.jpeg" },
                    _ => Array.Empty<string>()
                };
                foreach (var name in names)
                {
                    var candidate = Path.Combine(directory, name);
                    if (!File.Exists(candidate))
                        continue;
                    resolvedPath = candidate;
                    break;
                }
            }
        }

        if (_editorArtworkPathCache.Count >= 12)
            _editorArtworkPathCache.Clear();
        _editorArtworkPathCache[cacheKey] = resolvedPath;
        return resolvedPath;
    }

    private bool TryGetEditorPreviewSkinBounds(string id, out Rectangle bounds)
    {
        if (_chartEditorInterfaceRuntime is not null)
        {
            if (_chartEditorInterfaceRuntime.TryGetUiInputBounds(id, out var inputBounds) &&
                inputBounds.Width > 0 && inputBounds.Height > 0)
            {
                bounds = inputBounds;
                return true;
            }
            if (_chartEditorInterfaceRuntime.TryGetUiButtonBounds(id, out var skinBounds) &&
                skinBounds.Width > 0 && skinBounds.Height > 0)
            {
                bounds = skinBounds;
                return true;
            }
        }

        bounds = Rectangle.Empty;
        return false;
    }

    private static float GetEditorPreviewUiScale(int virtualWidth, int virtualHeight)
        => Math.Max(0.55f, Math.Min(virtualWidth / 1920f, virtualHeight / 1080f));

    private Rectangle GetEditorPreviewChartPlayButtonBounds(int virtualWidth, int virtualHeight)
    {
        // The Default skin registers the bottom-strip action as left_0. Use the
        // skin's actual hitbox so custom layouts keep working; the native geometry
        // is the fallback used by the emergency C# renderer.
        if (TryGetEditorPreviewSkinBounds("left_0", out var skinBounds))
            return skinBounds;

        var actionIndex = Array.IndexOf(EditorBottomLeftActionIds, EditorActionPlay);
        return GetEditorBottomLeftButtonBounds(actionIndex, virtualWidth, virtualHeight);
    }

    private Rectangle GetEditorPreviewEditorDialogBounds(int virtualWidth, int virtualHeight)
    {
        if (TryGetEditorPreviewSkinBounds("editor_preview_dialog_hitbox", out var skinBounds))
            return skinBounds;

        var unit = GetEditorPreviewUiScale(virtualWidth, virtualHeight);
        var margin = (int)MathF.Round(18f * unit);
        var width = Math.Min(virtualWidth - margin * 2, (int)MathF.Round(1280f * unit));
        var height = Math.Min(virtualHeight - margin * 2, (int)MathF.Round(820f * unit));
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        return new Rectangle((virtualWidth - width) / 2, (virtualHeight - height) / 2, width, height);
    }

    private Rectangle GetEditorPreviewCloseButtonBounds(int virtualWidth, int virtualHeight)
    {
        var panel = GetEditorPreviewEditorDialogBounds(virtualWidth, virtualHeight);
        var unit = GetEditorPreviewUiScale(virtualWidth, virtualHeight);
        var size = Math.Max(1, (int)MathF.Round(40f * unit));
        var margin = (int)MathF.Round(16f * unit);
        return new Rectangle(panel.Right - size - margin, panel.Y + margin, size, size);
    }

    private Rectangle GetEditorPreviewTimeRulerBounds(int virtualWidth, int virtualHeight)
    {
        if (TryGetEditorPreviewSkinBounds("editor_preview_ruler_hitbox", out var skinBounds))
            return skinBounds;

        var panel = GetEditorPreviewEditorDialogBounds(virtualWidth, virtualHeight);
        var unit = GetEditorPreviewUiScale(virtualWidth, virtualHeight);
        var margin = (int)MathF.Round(56f * unit);
        var y = panel.Y + (int)MathF.Round(145f * unit);
        return new Rectangle(panel.X + margin, y,
            Math.Max(1, panel.Width - margin * 2), Math.Max(1, (int)MathF.Round(48f * unit)));
    }

    private Rectangle GetEditorPreviewTimelineBounds(int virtualWidth, int virtualHeight)
    {
        if (TryGetEditorPreviewSkinBounds("editor_preview_timeline_hitbox", out var skinBounds))
            return skinBounds;

        var panel = GetEditorPreviewEditorDialogBounds(virtualWidth, virtualHeight);
        var unit = GetEditorPreviewUiScale(virtualWidth, virtualHeight);
        var margin = (int)MathF.Round(56f * unit);
        var y = panel.Y + (int)MathF.Round(198f * unit);
        var height = Math.Max(36, (int)MathF.Round(300f * unit));
        return new Rectangle(panel.X + margin, y,
            Math.Max(1, panel.Width - margin * 2), height);
    }

    // These positions come from invisible ui.button controls registered by the
    // active skin. The geometry below is only an emergency fallback for skins
    // that do not yet expose the preview-editor hitbox contract.
    private Rectangle GetEditorPreviewSliderBounds(int virtualWidth, int virtualHeight)
        => GetEditorPreviewTimelineBounds(virtualWidth, virtualHeight);

    private int GetEditorPreviewTimelineX(double seconds, int virtualWidth, int virtualHeight)
    {
        var timeline = GetEditorPreviewTimelineBounds(virtualWidth, virtualHeight);
        var duration = EditorPreviewSourceDurationSeconds;
        if (duration <= 0)
            return timeline.X;
        var viewStart = GetEditorPreviewTimelineViewStartSeconds();
        var viewDuration = Math.Max(0.000001, GetEditorPreviewTimelineViewDurationSeconds());
        var ratio = (seconds - viewStart) / viewDuration;
        return timeline.X + (int)Math.Round(ratio * timeline.Width);
    }

    private int GetEditorPreviewMarkerScreenX(double seconds, int virtualWidth, int virtualHeight)
    {
        var timeline = GetEditorPreviewTimelineBounds(virtualWidth, virtualHeight);
        return Math.Clamp(GetEditorPreviewTimelineX(seconds, virtualWidth, virtualHeight),
            timeline.X, timeline.Right);
    }

    private Rectangle GetEditorPreviewStartThumbBounds(int virtualWidth, int virtualHeight)
    {
        var timeline = GetEditorPreviewTimelineBounds(virtualWidth, virtualHeight);
        var size = Math.Max(22, (int)MathF.Round(24f * GetEditorPreviewUiScale(virtualWidth, virtualHeight)));
        var mappedX = GetEditorPreviewTimelineX(_editorPreviewStartSeconds, virtualWidth, virtualHeight);
        var centerX = mappedX < timeline.X ? timeline.X + size / 2
            : mappedX > timeline.Right ? timeline.Right - size / 2 : mappedX;
        return new Rectangle(centerX - size / 2, timeline.Y + 4, size, size);
    }

    private Rectangle GetEditorPreviewEndThumbBounds(int virtualWidth, int virtualHeight)
    {
        var timeline = GetEditorPreviewTimelineBounds(virtualWidth, virtualHeight);
        var size = Math.Max(22, (int)MathF.Round(24f * GetEditorPreviewUiScale(virtualWidth, virtualHeight)));
        var mappedX = GetEditorPreviewTimelineX(_editorPreviewEndSeconds, virtualWidth, virtualHeight);
        var centerX = mappedX < timeline.X ? timeline.X + size / 2
            : mappedX > timeline.Right ? timeline.Right - size / 2 : mappedX;
        return new Rectangle(centerX - size / 2, timeline.Bottom - size - 4, size, size);
    }

    private Rectangle GetEditorPreviewPlayheadThumbBounds(int virtualWidth, int virtualHeight)
    {
        var timeline = GetEditorPreviewTimelineBounds(virtualWidth, virtualHeight);
        var size = Math.Max(18, (int)MathF.Round(20f * GetEditorPreviewUiScale(virtualWidth, virtualHeight)));
        var mappedX = GetEditorPreviewTimelineX(GetEditorPreviewPlayheadSeconds(), virtualWidth, virtualHeight);
        var centerX = mappedX < timeline.X ? timeline.X + size / 2
            : mappedX > timeline.Right ? timeline.Right - size / 2 : mappedX;
        return new Rectangle(centerX - size / 2, timeline.Y + (timeline.Height - size) / 2, size, size);
    }

    private Rectangle GetEditorPreviewStartMarkerHitBounds(int virtualWidth, int virtualHeight)
    {
        if (TryGetEditorPreviewSkinBounds("editor_preview_start_marker_hitbox", out var skinBounds))
            return ExpandEditorPreviewMarkerHitBounds(skinBounds, virtualWidth, virtualHeight);
        return GetEditorPreviewMarkerHitBounds(_editorPreviewStartSeconds, 0, virtualWidth, virtualHeight);
    }

    private Rectangle GetEditorPreviewEndMarkerHitBounds(int virtualWidth, int virtualHeight)
    {
        if (TryGetEditorPreviewSkinBounds("editor_preview_end_marker_hitbox", out var skinBounds))
            return ExpandEditorPreviewMarkerHitBounds(skinBounds, virtualWidth, virtualHeight);
        return GetEditorPreviewMarkerHitBounds(_editorPreviewEndSeconds, 2, virtualWidth, virtualHeight);
    }

    private static Rectangle ExpandEditorPreviewMarkerHitBounds(Rectangle bounds, int virtualWidth, int virtualHeight)
    {
        var paddingX = Math.Max(8, (int)MathF.Round(10f * GetEditorPreviewUiScale(virtualWidth, virtualHeight)));
        var paddingY = Math.Max(6, (int)MathF.Round(8f * GetEditorPreviewUiScale(virtualWidth, virtualHeight)));
        return new Rectangle(bounds.X - paddingX, bounds.Y - paddingY,
            bounds.Width + paddingX * 2, bounds.Height + paddingY * 2);
    }

    private Rectangle GetEditorPreviewPlayheadMarkerHitBounds(int virtualWidth, int virtualHeight)
    {
        if (TryGetEditorPreviewSkinBounds("editor_preview_playhead_marker_hitbox", out var skinBounds))
            return skinBounds;
        return GetEditorPreviewMarkerHitBounds(GetEditorPreviewPlayheadSeconds(), 1, virtualWidth, virtualHeight);
    }

    private Rectangle GetEditorPreviewMarkerHitBounds(double seconds, int markerBand, int virtualWidth, int virtualHeight)
    {
        var timeline = GetEditorPreviewTimelineBounds(virtualWidth, virtualHeight);
        var unit = GetEditorPreviewUiScale(virtualWidth, virtualHeight);
        var centerX = Math.Clamp(GetEditorPreviewTimelineX(seconds, virtualWidth, virtualHeight),
            timeline.X, timeline.Right);
        var hitWidth = Math.Max(60, (int)MathF.Round(88f * unit));
        var bandHeight = Math.Max(24, timeline.Height / 3);
        var headExtension = Math.Max(12, (int)MathF.Round(18f * unit));
        var y = markerBand switch
        {
            0 => timeline.Y - headExtension,
            2 => timeline.Bottom - bandHeight,
            _ => timeline.Y + (timeline.Height - bandHeight) / 2
        };
        var height = markerBand switch
        {
            0 => bandHeight + headExtension,
            2 => bandHeight + headExtension,
            _ => bandHeight
        };
        return new Rectangle(centerX - hitWidth / 2, y, hitWidth, height);
    }

    private Rectangle GetEditorPreviewStartInputBounds(int virtualWidth, int virtualHeight)
    {
        if (TryGetEditorPreviewSkinBounds("editor_preview_start_input", out var skinBounds) ||
            TryGetEditorPreviewSkinBounds("editor_preview_start_input_hitbox", out skinBounds))
            return skinBounds;

        var panel = GetEditorPreviewEditorDialogBounds(virtualWidth, virtualHeight);
        var unit = GetEditorPreviewUiScale(virtualWidth, virtualHeight);
        var margin = (int)MathF.Round(76f * unit);
        var gap = (int)MathF.Round(32f * unit);
        var columnWidth = Math.Max(1, (panel.Width - margin * 2 - gap * 2) / 3);
        var pickSlot = (int)MathF.Round(76f * unit);
        var width = Math.Max(1, columnWidth - pickSlot);
        var height = Math.Max(1, (int)MathF.Round(56f * unit));
        var y = panel.Y + (int)MathF.Round(570f * unit);
        return new Rectangle(panel.X + margin, y, width, height);
    }

    private Rectangle GetEditorPreviewEndInputBounds(int virtualWidth, int virtualHeight)
    {
        if (TryGetEditorPreviewSkinBounds("editor_preview_end_input", out var skinBounds) ||
            TryGetEditorPreviewSkinBounds("editor_preview_end_input_hitbox", out skinBounds))
            return skinBounds;

        var panel = GetEditorPreviewEditorDialogBounds(virtualWidth, virtualHeight);
        var unit = GetEditorPreviewUiScale(virtualWidth, virtualHeight);
        var margin = (int)MathF.Round(76f * unit);
        var gap = (int)MathF.Round(32f * unit);
        var columnWidth = Math.Max(1, (panel.Width - margin * 2 - gap * 2) / 3);
        var pickSlot = (int)MathF.Round(76f * unit);
        var height = Math.Max(1, (int)MathF.Round(56f * unit));
        var y = panel.Y + (int)MathF.Round(570f * unit);
        return new Rectangle(panel.X + margin + columnWidth + gap, y,
            Math.Max(1, columnWidth - pickSlot), height);
    }

    private Rectangle GetEditorPreviewDurationInputBounds(int virtualWidth, int virtualHeight)
    {
        var panel = GetEditorPreviewEditorDialogBounds(virtualWidth, virtualHeight);
        var unit = GetEditorPreviewUiScale(virtualWidth, virtualHeight);
        var margin = (int)MathF.Round(76f * unit);
        var gap = (int)MathF.Round(32f * unit);
        var columnWidth = Math.Max(1, (panel.Width - margin * 2 - gap * 2) / 3);
        var height = Math.Max(1, (int)MathF.Round(56f * unit));
        var y = panel.Y + (int)MathF.Round(570f * unit);
        return new Rectangle(panel.X + margin + (columnWidth + gap) * 2, y, columnWidth, height);
    }

    private Rectangle GetEditorPreviewPickStartButtonBounds(int virtualWidth, int virtualHeight)
    {
        if (TryGetEditorPreviewSkinBounds("editor_preview_pick_start_hitbox", out var skinBounds))
            return skinBounds;
        var input = GetEditorPreviewStartInputBounds(virtualWidth, virtualHeight);
        var unit = GetEditorPreviewUiScale(virtualWidth, virtualHeight);
        var gap = Math.Max(2, (int)MathF.Round(8f * unit));
        var width = Math.Max(1, (int)MathF.Round(68f * unit));
        return new Rectangle(input.Right + gap, input.Y, width, input.Height);
    }

    private Rectangle GetEditorPreviewPickEndButtonBounds(int virtualWidth, int virtualHeight)
    {
        if (TryGetEditorPreviewSkinBounds("editor_preview_pick_end_hitbox", out var skinBounds))
            return skinBounds;
        var input = GetEditorPreviewEndInputBounds(virtualWidth, virtualHeight);
        var unit = GetEditorPreviewUiScale(virtualWidth, virtualHeight);
        var gap = Math.Max(2, (int)MathF.Round(8f * unit));
        var width = Math.Max(1, (int)MathF.Round(68f * unit));
        return new Rectangle(input.Right + gap, input.Y, width, input.Height);
    }

    private Rectangle GetEditorPreviewActionButtonBounds(int index, int virtualWidth, int virtualHeight)
    {
        var hitboxId = index switch
        {
            0 => "editor_preview_play_hitbox",
            1 => "editor_preview_pause_hitbox",
            2 => "editor_preview_stop_hitbox",
            3 => "editor_preview_save_hitbox",
            4 => "editor_preview_close_hitbox",
            _ => string.Empty,
        };
        if (!string.IsNullOrEmpty(hitboxId) && TryGetEditorPreviewSkinBounds(hitboxId, out var skinBounds))
            return skinBounds;

        var panel = GetEditorPreviewEditorDialogBounds(virtualWidth, virtualHeight);
        var unit = GetEditorPreviewUiScale(virtualWidth, virtualHeight);
        const int actionCount = 5;
        var padding = (int)MathF.Round(30f * unit);
        var gap = Math.Max(4, (int)MathF.Round(12f * unit));
        var height = Math.Max(1, (int)MathF.Round(54f * unit));
        var available = panel.Width - padding * 2 - gap * (actionCount - 1);
        var width = Math.Max(1, available / actionCount);
        var y = panel.Bottom - (int)MathF.Round(78f * unit);
        return new Rectangle(panel.X + padding + index * (width + gap), y, width, height);
    }

    private Rectangle GetEditorPreviewPlayButtonBounds(int virtualWidth, int virtualHeight)
        => GetEditorPreviewActionButtonBounds(0, virtualWidth, virtualHeight);

    private Rectangle GetEditorPreviewPauseButtonBounds(int virtualWidth, int virtualHeight)
        => GetEditorPreviewActionButtonBounds(1, virtualWidth, virtualHeight);

    private Rectangle GetEditorPreviewStopButtonBounds(int virtualWidth, int virtualHeight)
        => GetEditorPreviewActionButtonBounds(2, virtualWidth, virtualHeight);

    private Rectangle GetEditorPreviewSaveButtonBounds(int virtualWidth, int virtualHeight)
        => GetEditorPreviewActionButtonBounds(3, virtualWidth, virtualHeight);

    private Rectangle GetEditorPreviewCloseActionButtonBounds(int virtualWidth, int virtualHeight)
        => GetEditorPreviewActionButtonBounds(4, virtualWidth, virtualHeight);

    private Rectangle GetEditorPreviewRowEditButtonBounds(int rowIndex, int virtualWidth, int virtualHeight)
    {
        var bounds = GetEditorMetaFieldBounds(rowIndex, virtualWidth, virtualHeight);
        var gap = Math.Max(2, (int)MathF.Round(6f * _projection.ScalePixels(1f)));
        var editWidth = Math.Max(1, (int)MathF.Round(bounds.Width * 0.53f) - gap / 2);
        return new Rectangle(bounds.X, bounds.Y, editWidth, bounds.Height);
    }

    private Rectangle GetEditorPreviewRowPlayButtonBounds(int rowIndex, int virtualWidth, int virtualHeight)
    {
        var bounds = GetEditorMetaFieldBounds(rowIndex, virtualWidth, virtualHeight);
        var edit = GetEditorPreviewRowEditButtonBounds(rowIndex, virtualWidth, virtualHeight);
        var gap = Math.Max(2, (int)MathF.Round(6f * _projection.ScalePixels(1f)));
        return new Rectangle(edit.Right + gap, bounds.Y, Math.Max(1, bounds.Right - edit.Right - gap), bounds.Height);
    }

    private static double GetEditorPreviewTimeRulerInterval(double duration, int availableWidth)
    {
        if (!double.IsFinite(duration) || duration <= 0)
            return 1.0;
        var targetTicks = Math.Clamp(availableWidth / 105.0, 3.0, 12.0);
        var rawInterval = Math.Max(0.05, duration / targetTicks);
        var magnitude = Math.Pow(10.0, Math.Floor(Math.Log10(rawInterval)));
        var normalized = rawInterval / magnitude;
        var nice = normalized <= 1.0 ? 1.0 : normalized <= 2.0 ? 2.0 : normalized <= 5.0 ? 5.0 : 10.0;
        return nice * magnitude;
    }

    private static string FormatEditorPreviewRulerTime(double seconds, bool includeHours, double interval)
    {
        seconds = Math.Max(0, double.IsFinite(seconds) ? seconds : 0);
        if (interval < 1.0)
        {
            var decimals = interval < 0.1 ? 2 : 1;
            var totalMinutes = (long)Math.Floor(seconds / 60.0);
            var secondsInMinute = seconds - totalMinutes * 60.0;
            var secondFormat = decimals == 2 ? "00.00" : "00.0";
            var secondsText = secondsInMinute.ToString(secondFormat, CultureInfo.InvariantCulture);
            return includeHours
                ? $"{(long)Math.Floor(totalMinutes / 60.0)}:{totalMinutes % 60:00}:{secondsText}"
                : $"{totalMinutes}:{secondsText}";
        }

        var wholeSeconds = (long)Math.Floor(seconds + 0.0001);
        var minutes = wholeSeconds / 60;
        var remainingSeconds = wholeSeconds % 60;
        if (includeHours)
            return $"{wholeSeconds / 3600}:{minutes % 60:00}:{remainingSeconds:00}";
        return $"{minutes}:{remainingSeconds:00}";
    }

    private void DrawEditorPreviewTimeRuler(Rectangle ruler, double sourceDuration, float unit)
    {
        if (ruler.Width <= 0 || ruler.Height <= 0 || sourceDuration <= 0)
            return;

        var viewStart = GetEditorPreviewTimelineViewStartSeconds();
        var viewDuration = GetEditorPreviewTimelineViewDurationSeconds();
        if (viewDuration <= 0)
            return;
        var viewEnd = Math.Min(sourceDuration, viewStart + viewDuration);
        var interval = GetEditorPreviewTimeRulerInterval(viewDuration, ruler.Width);
        var includeHours = sourceDuration >= 3600.0;
        var font = _font;
        var labelScale = GetEditorDefaultFontScale(16f * unit);
        var labelColor = _chartEditorInterfaceLayout.MutedTextColor;
        var tickColor = new Color(92, 98, 106, 220);
        var firstTick = Math.Ceiling((viewStart - 0.000001) / interval) * interval;
        var tickCount = Math.Min(240, (int)Math.Ceiling((viewEnd - firstTick) / interval) + 1);
        for (var index = 0; index < tickCount; index++)
        {
            var time = firstTick + index * interval;
            if (time > viewEnd + 0.0001)
                break;
            var ratio = Math.Clamp((time - viewStart) / viewDuration, 0, 1);
            var x = ruler.X + (int)Math.Round(ratio * ruler.Width);
            _spriteBatch.Draw(_pixel,
                new Rectangle(x, ruler.Bottom - Math.Max(5, (int)MathF.Round(7f * unit)),
                    Math.Max(1, (int)MathF.Round(1f * unit)), Math.Max(5, (int)MathF.Round(7f * unit))), tickColor);

            var label = FontSafeText(font, FormatEditorPreviewRulerTime(time, includeHours, interval));
            var labelSize = UnicodeTextRenderer.MeasureString(font, label) * labelScale;
            var labelX = Math.Clamp(x - (int)MathF.Round(labelSize.X * 0.5f), ruler.X,
                Math.Max(ruler.X, ruler.Right - (int)Math.Ceiling(labelSize.X)));
            var labelY = ruler.Y + (ruler.Height - (int)Math.Ceiling(labelSize.Y)) / 2;
            UnicodeTextRenderer.DrawString(_spriteBatch, font, label,
                new Vector2(labelX, labelY), labelColor, 0f, Vector2.Zero,
                new Vector2(labelScale, labelScale), SpriteEffects.None, 0f);
        }
    }

    private float[] GetEditorPreviewWaveformRenderBars(float[] levels, int width, int step,
        int firstLevel, int afterLastLevel)
    {
        firstLevel = Math.Clamp(firstLevel, 0, Math.Max(0, levels.Length - 1));
        afterLastLevel = Math.Clamp(afterLastLevel, firstLevel + 1, levels.Length);
        if (ReferenceEquals(_editorPreviewWaveformRenderSource, levels) &&
            _editorPreviewWaveformRenderBars is not null &&
            _editorPreviewWaveformRenderWidth == width &&
            _editorPreviewWaveformRenderStep == step &&
            _editorPreviewWaveformRenderFirstLevel == firstLevel &&
            _editorPreviewWaveformRenderLastLevel == afterLastLevel)
            return _editorPreviewWaveformRenderBars;

        var barCount = (width + step - 1) / step;
        var bars = new float[barCount];
        var visibleLevelCount = Math.Max(1, afterLastLevel - firstLevel);
        for (var barIndex = 0; barIndex < barCount; barIndex++)
        {
            var x = barIndex * step;
            var nextX = Math.Min(width, x + step);
            var first = Math.Clamp(firstLevel + (int)((long)x * visibleLevelCount / Math.Max(1, width)),
                firstLevel, afterLastLevel - 1);
            var after = Math.Clamp(firstLevel + (int)(((long)nextX * visibleLevelCount + width - 1) / Math.Max(1, width)),
                first + 1, afterLastLevel);
            var count = after - first;
            var prefix = _editorPreviewWaveformRmsPrefix;
            if (prefix is { Length: > 1 } && prefix.Length == levels.Length + 1 && count > 0)
            {
                var normalizedSum = Math.Max(0f, prefix[after] - prefix[first]);
                bars[barIndex] = Math.Clamp(normalizedSum * levels.Length / count, 0f, 1f);
            }
            else
            {
                double sum = 0;
                for (var levelIndex = first; levelIndex < after; levelIndex++)
                    sum += Math.Clamp(levels[levelIndex], 0f, 1f);
                bars[barIndex] = count > 0 ? (float)(sum / count) : 0f;
            }
        }

        _editorPreviewWaveformRenderSource = levels;
        _editorPreviewWaveformRenderBars = bars;
        _editorPreviewWaveformRenderWidth = width;
        _editorPreviewWaveformRenderStep = step;
        _editorPreviewWaveformRenderFirstLevel = firstLevel;
        _editorPreviewWaveformRenderLastLevel = afterLastLevel;
        return bars;
    }

    private void DrawEditorPreviewWaveform(Rectangle timeline, double sourceDuration, float unit)
    {
        if (timeline.Width <= 0 || timeline.Height <= 0)
            return;

        var viewStart = GetEditorPreviewTimelineViewStartSeconds();
        var viewDuration = GetEditorPreviewTimelineViewDurationSeconds();
        if (sourceDuration <= 0 || viewDuration <= 0)
            return;
        var viewEnd = Math.Min(sourceDuration, viewStart + viewDuration);
        _spriteBatch.Draw(_pixel, timeline, new Color(9, 12, 15, 255));

        var startRatio = (_editorPreviewStartSeconds - viewStart) / viewDuration;
        var endRatio = (_editorPreviewEndSeconds - viewStart) / viewDuration;
        var clippedStartRatio = Math.Clamp(startRatio, 0, 1);
        var clippedEndRatio = Math.Clamp(endRatio, 0, 1);
        var selectionStart = timeline.X + (int)Math.Round(clippedStartRatio * timeline.Width);
        var selectionEnd = timeline.X + (int)Math.Round(clippedEndRatio * timeline.Width);
        if (_editorPreviewEndSeconds > viewStart && _editorPreviewStartSeconds < viewEnd && selectionEnd > selectionStart)
            _spriteBatch.Draw(_pixel, new Rectangle(selectionStart, timeline.Y,
                selectionEnd - selectionStart, timeline.Height), new Color(35, 135, 112, 62));

        var centerY = timeline.Y + timeline.Height / 2;
        _spriteBatch.Draw(_pixel, new Rectangle(timeline.X, centerY, timeline.Width, 1),
            new Color(80, 89, 99, 175));

        var levels = _editorPreviewWaveformRms;
        if (levels is { Length: > 0 })
        {
            var step = Math.Max(1, (int)MathF.Round(1f * unit));
            var halfHeight = Math.Max(1, timeline.Height / 2 - (int)MathF.Round(5f * unit));
            var firstLevel = Math.Clamp((int)Math.Floor(viewStart / sourceDuration * levels.Length), 0, levels.Length - 1);
            var afterLastLevel = Math.Clamp((int)Math.Ceiling(viewEnd / sourceDuration * levels.Length),
                firstLevel + 1, levels.Length);
            var bars = GetEditorPreviewWaveformRenderBars(levels, timeline.Width, step, firstLevel, afterLastLevel);
            for (var barIndex = 0; barIndex < bars.Length; barIndex++)
            {
                var x = barIndex * step;
                var level = Math.Clamp(bars[barIndex], 0f, 1f);
                var amplitude = Math.Clamp((int)MathF.Round(level * halfHeight), 1, halfHeight);
                var absoluteX = timeline.X + x;
                var inSelection = absoluteX >= selectionStart && absoluteX < selectionEnd;
                var color = inSelection
                    ? new Color(112, 229, 196, 235)
                    : new Color(125, 145, 164, 205);
                _spriteBatch.Draw(_pixel,
                    new Rectangle(absoluteX, centerY - amplitude, Math.Max(1, Math.Min(step, timeline.Right - absoluteX)), amplitude * 2 + 1), color);
            }
        }
        else if (_audio.IsLoaded)
        {
            var message = FontSafeText(_font, _editorPreviewWaveformTask is null
                ? "WAVEFORM UNAVAILABLE"
                : "BUILDING WAVEFORM...");
            var scale = GetEditorDefaultFontScale(18f * unit);
            var size = UnicodeTextRenderer.MeasureString(_font, message) * scale;
            UnicodeTextRenderer.DrawString(_spriteBatch, _font, message,
                new Vector2(timeline.X + (timeline.Width - size.X) / 2f,
                    timeline.Y + (timeline.Height - size.Y) / 2f),
                new Color(145, 155, 166), 0f, Vector2.Zero,
                new Vector2(scale, scale), SpriteEffects.None, 0f);
        }

        var border = new Color(61, 70, 80, 255);
        _spriteBatch.Draw(_pixel, new Rectangle(timeline.X, timeline.Y, timeline.Width, 1), border);
        _spriteBatch.Draw(_pixel, new Rectangle(timeline.X, timeline.Bottom - 1, timeline.Width, 1), border);
        _spriteBatch.Draw(_pixel, new Rectangle(timeline.X, timeline.Y, 1, timeline.Height), border);
        _spriteBatch.Draw(_pixel, new Rectangle(timeline.Right - 1, timeline.Y, 1, timeline.Height), border);
    }

    private void DrawEditorPreviewTimelineMarker(double seconds, Rectangle handleBounds, Rectangle timeline,
        Color color, bool isPlayhead, int virtualWidth, int virtualHeight)
    {
        var viewStart = GetEditorPreviewTimelineViewStartSeconds();
        var viewEnd = viewStart + GetEditorPreviewTimelineViewDurationSeconds();
        var outsideView = seconds < viewStart || seconds > viewEnd;
        var x = handleBounds.Center.X;
        var width = isPlayhead ? 4 : 6;
        var outlineWidth = width + 4;
        // Keep the start/end grab handles pinned to the nearest visible edge when
        // their actual time lies outside the zoomed window. The handle remains
        // visible and draggable back into the current view.
        if (!outsideView || !isPlayhead)
        {
            _spriteBatch.Draw(_pixel,
                new Rectangle(x - outlineWidth / 2, timeline.Y - 8, outlineWidth, timeline.Height + 16),
                new Color(4, 7, 10, 245));
            _spriteBatch.Draw(_pixel,
                new Rectangle(x - width / 2, timeline.Y - 8, width, timeline.Height + 16), color);
        }
        DrawEditorPreviewSliderThumb(handleBounds, color);
    }

    private void DrawEditorPreviewEditorOverlay(int virtualWidth, int virtualHeight)
    {
        var interfaceUi = _chartEditorInterfaceLayout;
        var headerFont = _font;
        var labelFont = _font;
        var unit = GetEditorPreviewUiScale(virtualWidth, virtualHeight);
        const string previewTitle = "MUSIC PREVIEW";
        var headerScale = GetEditorDefaultFontScale(previewTitle, 28f * unit);
        var panel = GetEditorPreviewEditorDialogBounds(virtualWidth, virtualHeight);
        _spriteBatch.Draw(_pixel, new Rectangle(0, 0, virtualWidth, virtualHeight), new Color(0, 0, 0, 185));
        _spriteBatch.Draw(_pixel, panel, interfaceUi.PanelColor);
        var edge = Math.Max(2, (int)MathF.Round(2f * unit));
        _spriteBatch.Draw(_pixel, new Rectangle(panel.X, panel.Y, panel.Width, edge), interfaceUi.AccentColor);
        _spriteBatch.Draw(_pixel, new Rectangle(panel.X, panel.Bottom - edge, panel.Width, edge), interfaceUi.HeaderColor);
        _spriteBatch.Draw(_pixel, new Rectangle(panel.X, panel.Y, edge, panel.Height), interfaceUi.AccentColor);
        _spriteBatch.Draw(_pixel, new Rectangle(panel.Right - edge, panel.Y, edge, panel.Height), interfaceUi.HeaderColor);

        var title = FontSafeText(headerFont, previewTitle);
        UnicodeTextRenderer.DrawString(_spriteBatch, headerFont, title,
            new Vector2(panel.X + 28f * unit, panel.Y + 22f * unit), interfaceUi.ButtonTextColor,
            0f, Vector2.Zero, headerScale, SpriteEffects.None, 0f);
        var sourceDuration = EditorPreviewSourceDurationSeconds;
        var subtitle = sourceDuration > 0
            ? "PICK beside START/END selects a chart tick • Drag marker heads to set range • Wheel zoom • Trackpad / Shift+wheel / middle-drag pan • Click waveform to seek."
            : _audio.IsLoading
                ? "Preparing full song audio. The waveform and preview controls will unlock when it is ready."
                : "Import the full song audio first to enable preview editing.";
        DrawEditorPreviewLabel(subtitle,
            new Rectangle(panel.X + 28, panel.Y + (int)MathF.Round(76f * unit),
                panel.Width - (int)MathF.Round(56f * unit), (int)MathF.Round(48f * unit)), Muted, unit);

        var ruler = GetEditorPreviewTimeRulerBounds(virtualWidth, virtualHeight);
        var timeline = GetEditorPreviewTimelineBounds(virtualWidth, virtualHeight);
        DrawEditorPreviewTimeRuler(ruler, sourceDuration, unit);
        DrawEditorPreviewWaveform(timeline, sourceDuration, unit);
        DrawEditorPreviewTimelineMarker(_editorPreviewStartSeconds,
            GetEditorPreviewStartThumbBounds(virtualWidth, virtualHeight), timeline,
            new Color(251, 146, 60),
            false, virtualWidth, virtualHeight);
        DrawEditorPreviewTimelineMarker(_editorPreviewEndSeconds,
            GetEditorPreviewEndThumbBounds(virtualWidth, virtualHeight), timeline,
            new Color(239, 68, 68), isPlayhead: false, virtualWidth, virtualHeight);
        DrawEditorPreviewTimelineMarker(GetEditorPreviewPlayheadSeconds(),
            GetEditorPreviewPlayheadThumbBounds(virtualWidth, virtualHeight), timeline,
            new Color(250, 204, 21), true, virtualWidth, virtualHeight);
        var viewStart = GetEditorPreviewTimelineViewStartSeconds();
        var viewDuration = GetEditorPreviewTimelineViewDurationSeconds();
        var viewEnd = Math.Min(sourceDuration, viewStart + viewDuration);
        var startOutsideLeft = _editorPreviewStartSeconds < viewStart;
        var startOutsideRight = _editorPreviewStartSeconds > viewEnd;
        var startMarkerX = Math.Clamp(GetEditorPreviewTimelineX(_editorPreviewStartSeconds, virtualWidth, virtualHeight),
            timeline.X, timeline.Right);
        var startLabelWidth = Math.Min(timeline.Width, (int)MathF.Round(104f * unit));
        DrawEditorPreviewLabel(startOutsideLeft ? "START <" : startOutsideRight ? "START >" : "START",
            new Rectangle(Math.Clamp(startMarkerX - startLabelWidth / 2, timeline.X,
                    timeline.Right - startLabelWidth),
                timeline.Y + (int)MathF.Round(8f * unit),
                startLabelWidth, (int)MathF.Round(28f * unit)), new Color(251, 146, 60), unit);

        var endOutsideLeft = _editorPreviewEndSeconds < viewStart;
        var endOutsideRight = _editorPreviewEndSeconds > viewEnd;
        var endMarkerX = Math.Clamp(GetEditorPreviewTimelineX(_editorPreviewEndSeconds, virtualWidth, virtualHeight),
            timeline.X, timeline.Right);
        var endLabelWidth = Math.Min(timeline.Width, (int)MathF.Round(84f * unit));
        DrawEditorPreviewLabel(endOutsideLeft ? "END <" : endOutsideRight ? "END >" : "END",
            new Rectangle(Math.Clamp(endMarkerX - endLabelWidth / 2, timeline.X,
                    timeline.Right - endLabelWidth),
                timeline.Bottom - (int)MathF.Round(36f * unit),
                endLabelWidth, (int)MathF.Round(28f * unit)), new Color(239, 68, 68), unit);

        var minLabel = FontSafeText(labelFont, FormatEditorProgressTimestamp(0));
        var maxLabel = FontSafeText(labelFont, FormatEditorProgressTimestamp(sourceDuration));
        var timeScale = GetEditorDefaultFontScale(18f * unit);
        var maxLabelSize = UnicodeTextRenderer.MeasureString(labelFont, maxLabel) * timeScale;
        var timeY = timeline.Bottom + (int)MathF.Round(2f * unit);
        UnicodeTextRenderer.DrawString(_spriteBatch, labelFont, minLabel,
            new Vector2(timeline.X, timeY), interfaceUi.MutedTextColor, 0f, Vector2.Zero,
            new Vector2(timeScale, timeScale), SpriteEffects.None, 0f);
        UnicodeTextRenderer.DrawString(_spriteBatch, labelFont, maxLabel,
            new Vector2(timeline.Right - maxLabelSize.X, timeY), interfaceUi.MutedTextColor, 0f, Vector2.Zero,
            new Vector2(timeScale, timeScale), SpriteEffects.None, 0f);
        var playheadLabel = FontSafeText(labelFont,
            $"{FormatEditorProgressTimestamp(GetEditorPreviewPlayheadSeconds())} / {FormatEditorProgressTimestamp(_editorPreviewEndSeconds)}");
        var playheadSize = UnicodeTextRenderer.MeasureString(labelFont, playheadLabel) * timeScale;
        UnicodeTextRenderer.DrawString(_spriteBatch, labelFont, playheadLabel,
            new Vector2(timeline.X + (timeline.Width - playheadSize.X) / 2f, timeY),
            new Color(250, 204, 21), 0f, Vector2.Zero,
            new Vector2(timeScale, timeScale), SpriteEffects.None, 0f);

        var startInput = GetEditorPreviewStartInputBounds(virtualWidth, virtualHeight);
        var endInput = GetEditorPreviewEndInputBounds(virtualWidth, virtualHeight);
        var durationInput = GetEditorPreviewDurationInputBounds(virtualWidth, virtualHeight);
        var labelHeight = (int)MathF.Round(28f * unit);
        var labelOffset = (int)MathF.Round(30f * unit);
        DrawEditorPreviewLabel("START",
            new Rectangle(startInput.X, startInput.Y - labelOffset, startInput.Width, labelHeight),
            interfaceUi.MutedTextColor, unit);
        DrawEditorPreviewLabel("END",
            new Rectangle(endInput.X, endInput.Y - labelOffset, endInput.Width, labelHeight),
            interfaceUi.MutedTextColor, unit);
        DrawEditorPreviewLabel("DURATION",
            new Rectangle(durationInput.X, durationInput.Y - labelOffset, durationInput.Width, labelHeight),
            interfaceUi.MutedTextColor, unit);
        DrawEditorPreviewInput(startInput, _editorPreviewStartText, _editorPreviewFocusedField == 0,
            _editorPreviewStartCaret, unit);
        DrawEditorPreviewInput(endInput, _editorPreviewEndText, _editorPreviewFocusedField == 1,
            _editorPreviewEndCaret, unit);
        DrawEditorPreviewInput(durationInput,
            FormatEditorPreviewSeconds(Math.Max(0, _editorPreviewEndSeconds - _editorPreviewStartSeconds)),
            false, 0, unit, editable: false);

        if (!string.IsNullOrWhiteSpace(_editorPreviewDialogMessage))
        {
            var messageY = endInput.Bottom + (int)MathF.Round(8f * unit);
            var messageBottom = GetEditorPreviewPlayButtonBounds(virtualWidth, virtualHeight).Y - (int)MathF.Round(6f * unit);
            var messageBounds = new Rectangle(panel.X + 28, messageY,
                panel.Width - (int)MathF.Round(56f * unit), Math.Max(1, messageBottom - messageY));
            var messageColor = _editorPreviewDialogMessage.StartsWith("Could not", StringComparison.OrdinalIgnoreCase)
                ? new Color(252, 165, 165)
                : new Color(167, 243, 208);
            DrawEditorPreviewLabel(_editorPreviewDialogMessage, messageBounds, messageColor, unit);
        }

        var sourceReady = _audio.IsLoaded && sourceDuration > 0;
        var rangeValid = sourceReady && TryReadEditorPreviewRange(out _, out _, out _,
            extendEndWhenStartPassesIt: _editorPreviewFocusedField == 0);
        DrawEditorMetaButton(GetEditorPreviewPickStartButtonBounds(virtualWidth, virtualHeight), "PICK",
            sourceReady ? new Color(42, 38, 30, 250) : new Color(30, 30, 32, 240),
            sourceReady ? new Color(251, 146, 60) : new Color(105, 105, 107));
        DrawEditorMetaButton(GetEditorPreviewPickEndButtonBounds(virtualWidth, virtualHeight), "PICK",
            sourceReady ? new Color(42, 32, 32, 250) : new Color(30, 30, 32, 240),
            sourceReady ? new Color(239, 68, 68) : new Color(105, 105, 107));
        DrawEditorMetaButton(GetEditorPreviewPlayButtonBounds(virtualWidth, virtualHeight), "PLAY",
            sourceReady ? new Color(28, 28, 30, 250) : new Color(30, 30, 32, 240),
            sourceReady ? Active : new Color(105, 105, 107));
        DrawEditorMetaButton(GetEditorPreviewPauseButtonBounds(virtualWidth, virtualHeight), "PAUSE",
            _editorPreviewSelectionPlaying ? new Color(38, 58, 52, 250) : new Color(30, 30, 32, 240),
            _editorPreviewSelectionPlaying ? new Color(52, 211, 153) : new Color(105, 105, 107));
        DrawEditorMetaButton(GetEditorPreviewStopButtonBounds(virtualWidth, virtualHeight), "STOP",
            new Color(28, 28, 30, 250), new Color(148, 163, 184));
        DrawEditorMetaButton(GetEditorPreviewSaveButtonBounds(virtualWidth, virtualHeight), "SAVE",
            rangeValid ? new Color(29, 65, 52, 250) : new Color(30, 30, 32, 240),
            rangeValid ? new Color(52, 211, 153) : new Color(105, 105, 107));
        DrawEditorMetaButton(GetEditorPreviewCloseActionButtonBounds(virtualWidth, virtualHeight), "CLOSE",
            new Color(28, 28, 30, 250), new Color(148, 163, 184));
    }

    private void DrawEditorPreviewSliderThumb(Rectangle bounds, Color fill)
    {
        _spriteBatch.Draw(_pixel, bounds, new Color(8, 8, 9, 245));
        var inner = new Rectangle(bounds.X + 3, bounds.Y + 3, Math.Max(1, bounds.Width - 6), Math.Max(1, bounds.Height - 6));
        _spriteBatch.Draw(_pixel, inner, fill);
    }

    private void DrawEditorPreviewLabel(string rawText, Rectangle bounds, Color color, float unit)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        // Keep preview-editor text at its normal 24-design-pixel size. Long labels,
        // subtitles, and FFmpeg errors wrap into the space available instead of
        // being squeezed down to a tiny single line.
        var font = _font;
        var textScale = GetEditorDefaultFontScale(24f * unit);
        var horizontalPadding = 2f * unit;
        var maxWidth = Math.Max(1f, bounds.Width - horizontalPadding * 2f) / textScale;
        var lines = WrapEditorHudText(font, FontSafeText(font, rawText), maxWidth);
        var lineHeight = 24f * unit;
        var lineGap = 2f * unit;
        var linePitch = lineHeight + lineGap;
        var maxLines = Math.Max(1, (int)MathF.Floor((bounds.Height + lineGap) / linePitch));
        if (lines.Count > maxLines)
        {
            lines.RemoveRange(maxLines, lines.Count - maxLines);
            lines[^1] = FitHudText(font, lines[^1].TrimEnd() + "...", maxWidth);
        }

        var blockHeight = lines.Count * lineHeight + Math.Max(0, lines.Count - 1) * lineGap;
        var startY = bounds.Y + Math.Max(0f, (bounds.Height - blockHeight) * 0.5f);
        for (var index = 0; index < lines.Count; index++)
        {
            var position = new Vector2(bounds.X + horizontalPadding, startY + index * linePitch);
            UnicodeTextRenderer.DrawString(_spriteBatch, font, lines[index], position, color,
                0f, Vector2.Zero, new Vector2(textScale, textScale), SpriteEffects.None, 0f);
        }
    }

    private void DrawEditorPreviewInput(Rectangle bounds, string text, bool focused, int caretIndex, float unit,
        bool editable = true)
    {
        var interfaceUi = _chartEditorInterfaceLayout;
        var labelFont = _font;
        var accent = focused ? Active : (editable ? new Color(100, 100, 102) : interfaceUi.HeaderColor);
        var fieldFill = editable ? interfaceUi.ButtonColor : new Color(22, 24, 28, 235);
        var fieldText = editable ? interfaceUi.ButtonTextColor : interfaceUi.MutedTextColor;
        _spriteBatch.Draw(_pixel, bounds, fieldFill);
        var edge = Math.Max(1, (int)MathF.Round(1.5f * unit));
        _spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Y, bounds.Width, edge), accent);
        _spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Bottom - edge, bounds.Width, edge), interfaceUi.HeaderColor);
        var caretPrefix = string.Empty;
        var visibleStart = 0;
        var visibleEnd = text.Length;
        var textWidth = Math.Max(1f, bounds.Width - 20f * unit);
        var textScale = GetEditorTextFieldFontScale(bounds.Height, unit);
        text = FontSafeText(labelFont, text);
        var display = focused
            ? FitEditorTextAroundCaret(labelFont, text, caretIndex, textWidth,
                _editorPreviewFocusedField == 0 ? "preview-start" : "preview-end", textScale,
                out caretPrefix, out visibleStart, out visibleEnd)
            : FitHudText(labelFont, FontSafeText(labelFont, text), textWidth);
        var measured = UnicodeTextRenderer.MeasureString(labelFont, display);
        var textPosition = new Vector2(bounds.X + 10f * unit, bounds.Y + (bounds.Height - measured.Y * textScale) * 0.5f);
        if (focused)
        {
            var anchor = _editorPreviewFocusedField == 0 ? _editorPreviewStartSelectionAnchor : _editorPreviewEndSelectionAnchor;
            var mappedAnchor = Math.Clamp(anchor, visibleStart, visibleEnd) - visibleStart;
            var mappedCaret = Math.Clamp(caretIndex, visibleStart, visibleEnd) - visibleStart;
            DrawEditorTextSelectionHighlight(bounds, display, mappedAnchor, mappedCaret, textPosition.X, textPosition.Y,
                textScale, textWidth, unit, font: labelFont);
        }
        UnicodeTextRenderer.DrawString(_spriteBatch, labelFont, display, textPosition, fieldText, 0f, Vector2.Zero, new Vector2(textScale, textScale), SpriteEffects.None, 0f);
        if (focused && editable) DrawEditorTextCaret(caretPrefix, textPosition, textScale, unit, labelFont);
    }
}
