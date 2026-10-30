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
    private string? _editorPreviewDialogMessage;
    private byte[]? _editorSharedPreviewBytes;
    private OpusAudioPlayer? _editorPreviewPlayer;
    private long _editorPreviewPlaybackStartTimestamp;
    private double _editorPreviewPlaybackDurationSeconds;
    private double? _editorSavedPreviewDurationSeconds;

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
    {
        get
        {
            if (double.IsFinite(_audio.DurationSeconds) && _audio.DurationSeconds > 0)
                return _audio.DurationSeconds;

            var music = GetEditorPreviewMusicBytes();
            if (music is not { Length: > 0 })
                return 0;
            try
            {
                using var probe = new OpusAudioPlayer();
                probe.LoadOpus(music);
                return Math.Max(0, probe.DurationSeconds);
            }
            catch
            {
                return 0;
            }
        }
    }

    private static string FormatEditorPreviewSeconds(double seconds)
        => Math.Max(0, double.IsFinite(seconds) ? seconds : 0).ToString("0.000", CultureInfo.InvariantCulture);

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

    private void OpenEditorPreviewEditor()
    {
        if (!_chartEditorMode || _chart is null)
            return;

        PauseChartPlaybackForPreviewEditor();
        StopEditorPreviewPlayback();
        var duration = EditorPreviewSourceDurationSeconds;
        if (!_editorPreviewRangeInitialized)
        {
            _editorPreviewStartSeconds = 0;
            _editorPreviewEndSeconds = Math.Min(30.0, duration);
            _editorPreviewRangeInitialized = true;
        }
        _editorPreviewStartSeconds = Math.Clamp(_editorPreviewStartSeconds, 0, duration);
        _editorPreviewEndSeconds = Math.Clamp(_editorPreviewEndSeconds, _editorPreviewStartSeconds, duration);
        if (_editorPreviewEndSeconds <= _editorPreviewStartSeconds && duration > 0)
        {
            _editorPreviewStartSeconds = 0;
            _editorPreviewEndSeconds = Math.Min(30.0, duration);
        }
        SyncEditorPreviewTimeTexts();
        _editorPreviewFocusedField = -1;
        _editorPreviewDialogMessage = duration > 0
            ? null
            : "Import the full song audio before creating a preview.";
        _editorPreviewEditorOpen = true;
        _editorMetaEditingField = null;
        _statusMessage = "Music preview editor opened. Playback is paused.";
    }

    private void CloseEditorPreviewEditor()
    {
        _editorPreviewEditorOpen = false;
        _editorPreviewFocusedField = -1;
        _editorPreviewDraggingStart = false;
        _editorPreviewDraggingEnd = false;
        _editorPreviewDialogMessage = null;
        EndEditorTextFieldMouseSelection();
        _editorPreviewInputReplaceOnNextKey = false;
        StopEditorPreviewPlayback();
        // Deliberately leave chart playback paused. The user can resume it explicitly.
        PauseChartPlaybackForPreviewEditor();
        _statusMessage = "Music preview editor closed. Playback remains paused.";
    }

    private void SyncEditorPreviewTimeTexts()
    {
        _editorPreviewStartText = FormatEditorPreviewSeconds(_editorPreviewStartSeconds);
        _editorPreviewEndText = FormatEditorPreviewSeconds(_editorPreviewEndSeconds);
        _editorPreviewStartCaret = _editorPreviewStartText.Length;
        _editorPreviewEndCaret = _editorPreviewEndText.Length;
    }

    private string? GetEditorPreviewAssetDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_tstsWorkingDirectory) && Directory.Exists(_tstsWorkingDirectory))
            return _tstsWorkingDirectory;
        if (string.IsNullOrWhiteSpace(_loadedSourcePath))
            return null;
        try
        {
            if (string.Equals(Path.GetExtension(_loadedSourcePath), ".fpk", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(Path.GetFullPath(_loadedSourcePath));
            return GetSongAssetDirectoryForChartPath(_loadedSourcePath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
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
                var package = FpkPackage.Load(_loadedSourcePath);
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
                var package = FpkPackage.Load(_loadedSourcePath);
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
                _statusMessage = message;
                return;
            }
            _editorPreviewPlayer = new OpusAudioPlayer();
            _editorPreviewPlayer.LoadOpus(bytes);
            _editorPreviewPlayer.Volume = _skin.GameConfig.MusicVolume;
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
            _statusMessage = message;
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
        return Math.Clamp(elapsed, 0, _editorPreviewPlaybackDurationSeconds);
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
        var fadeSeconds = Math.Min(0.18, clipDuration / 4.0);
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
                "-af", $"afade=t=in:st=0:d={fadeSeconds.ToString("0.######", CultureInfo.InvariantCulture)},afade=t=out:st={fadeOutStart.ToString("0.######", CultureInfo.InvariantCulture)}:d={fadeSeconds.ToString("0.######", CultureInfo.InvariantCulture)}",
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

    private bool PlayEditorPreviewSelection()
    {
        if (_chart is null || !_chartEditorMode)
            return false;
        var duration = EditorPreviewSourceDurationSeconds;
        if (duration <= 0)
            throw new InvalidOperationException("Import the full song audio before creating a preview.");
        if (!TryReadEditorPreviewRange(out var startSeconds, out var endSeconds, out var validationMessage))
            throw new InvalidOperationException(validationMessage);

        // PLAY uses a temporary in-memory clip made from the current selection.
        // It deliberately does not update PreviewPath, shared export bytes, or the
        // source folder. Only SAVE below persists the generated audio.
        var bakedBytes = CreateBakedEditorPreviewBytes(startSeconds, endSeconds);
        StopEditorPreviewPlayback();
        _editorPreviewPlayer = new OpusAudioPlayer();
        _editorPreviewPlayer.LoadOpus(bakedBytes);
        _editorPreviewPlayer.Volume = _skin.GameConfig.MusicVolume;
        _editorPreviewPlayer.PrepareInitialPlayback(retainPcmForSeeking: true);
        _editorPreviewPlayer.SyncToChartTime(0.0, 0.0);
        _editorPreviewPlaybackDurationSeconds = _editorPreviewPlayer.DurationSeconds;
        _editorPreviewPlaybackStartTimestamp = Stopwatch.GetTimestamp();
        _editorPreviewDialogMessage = $"Playing selected range ({FormatEditorPreviewTimestamp(startSeconds)}–{FormatEditorPreviewTimestamp(endSeconds)}). Not saved yet.";
        _statusMessage = _editorPreviewDialogMessage;
        return true;
    }

    private bool SaveEditorPreview()
    {
        if (_chart is null || !_chartEditorMode)
            return false;

        var duration = EditorPreviewSourceDurationSeconds;
        if (duration <= 0)
            throw new InvalidOperationException("Import the full song audio before creating a preview.");
        if (!TryReadEditorPreviewRange(out var startSeconds, out var endSeconds, out var validationMessage))
            throw new InvalidOperationException(validationMessage);

        StopEditorPreviewPlayback();
        var bakedBytes = CreateBakedEditorPreviewBytes(startSeconds, endSeconds);

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
        MarkEditorDirty();
        _editorPreviewStartSeconds = startSeconds;
        _editorPreviewEndSeconds = endSeconds;
        SyncEditorPreviewTimeTexts();
        _editorPreviewPlaybackDurationSeconds = Math.Max(0.0, endSeconds - startSeconds);
        _editorPreviewDialogMessage = assetWriteWarning is null
            ? string.IsNullOrWhiteSpace(targetDirectory)
                ? $"Saved preview in memory ({FormatEditorPreviewTimestamp(startSeconds)}–{FormatEditorPreviewTimestamp(endSeconds)}). It will be included on export."
                : $"Saved preview.opus ({FormatEditorPreviewTimestamp(startSeconds)}–{FormatEditorPreviewTimestamp(endSeconds)})."
            : $"Saved preview in memory. It will be included on export; writing beside the source failed: {assetWriteWarning}";
        _statusMessage = _editorPreviewDialogMessage;
        return true;
    }

    private bool TryReadEditorPreviewRange(out double startSeconds, out double endSeconds, out string message)
    {
        startSeconds = _editorPreviewStartSeconds;
        endSeconds = _editorPreviewEndSeconds;
        message = string.Empty;
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
        if (startSeconds < 0 || endSeconds < 0 || startSeconds >= endSeconds)
        {
            message = "Preview start must be at least 0 and earlier than preview end.";
            return false;
        }
        if (endSeconds > sourceDuration + 0.001)
        {
            message = $"Preview end cannot exceed the song length ({sourceDuration:0.###} seconds).";
            return false;
        }
        if (endSeconds - startSeconds < 0.05)
        {
            message = "Preview must be at least 0.05 seconds long.";
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
        if (!TryReadEditorPreviewRange(out var start, out var end, out var message))
        {
            _editorPreviewDialogMessage = message;
            return;
        }
        _editorPreviewStartSeconds = start;
        _editorPreviewEndSeconds = end;
        SyncEditorPreviewTimeTexts();
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

    private void HandleEditorPreviewEditorInput(KeyboardState keyboard, KeyboardState previousKeyboard)
    {
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
                _editorPreviewFocusedField = 1 - _editorPreviewFocusedField;
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

        var mouse = GlobalInput.Mouse;
        var previousMouse = GlobalInput.PreviousMouse;
        var leftDown = mouse.LeftButton == ButtonState.Pressed;
        var leftPressed = leftDown && previousMouse.LeftButton != ButtonState.Pressed;
        var leftReleased = !leftDown && previousMouse.LeftButton == ButtonState.Pressed;
        var (virtualWidth, virtualHeight) = GetVirtualResolution(_graphics.Viewport.Width, _graphics.Viewport.Height);
        var point = new Microsoft.Xna.Framework.Point(
            (int)MathF.Round(mouse.X * virtualWidth / (float)Math.Max(1, _graphics.Viewport.Width)),
            (int)MathF.Round(mouse.Y * virtualHeight / (float)Math.Max(1, _graphics.Viewport.Height)));

        if (_chartEditorInterfaceRuntime.IsLoaded && _chartEditorInterfaceRuntime.LastError is null)
        {
            _chartEditorInterfaceRuntime.UpdateUiPointer(point.X, point.Y, leftDown);
            if (leftPressed)
            {
                var hit = _chartEditorInterfaceRuntime.HandleUiPointerPressed(point.X, point.Y);
                var action = _chartEditorInterfaceRuntime.ConsumeUiAction();
                if (hit)
                {
                    DispatchChartEditorInterfaceAction(action, virtualWidth, virtualHeight);
                    if (action?.Name == "editor.preview.focus_start" &&
                        _chartEditorInterfaceRuntime.TryGetUiButtonBounds("editor_preview_start_input", out var startBounds))
                    {
                        _editorPreviewStartCaret = GetEditorPreviewTextCaretIndex(startBounds, _editorPreviewStartText, point, _projection.ScalePixels(1f));
                        _editorPreviewStartSelectionAnchor = _editorPreviewStartCaret;
                    }
                    else if (action?.Name == "editor.preview.focus_end" &&
                             _chartEditorInterfaceRuntime.TryGetUiButtonBounds("editor_preview_end_input", out var endBounds))
                    {
                        _editorPreviewEndCaret = GetEditorPreviewTextCaretIndex(endBounds, _editorPreviewEndText, point, _projection.ScalePixels(1f));
                        _editorPreviewEndSelectionAnchor = _editorPreviewEndCaret;
                    }
                    return;
                }
            }
            else if (leftDown && _chartEditorInterfaceRuntime.HandleUiPointerDrag(point.X, point.Y))
            {
                DispatchChartEditorInterfaceAction(_chartEditorInterfaceRuntime.ConsumeUiAction(), virtualWidth, virtualHeight);
                return;
            }
            if (leftDown && (_editorPreviewDraggingStart || _editorPreviewDraggingEnd))
                return;
            if (leftReleased)
            {
                _editorPreviewDraggingStart = false;
                _editorPreviewDraggingEnd = false;
            }
        }
        var dialog = GetEditorPreviewEditorDialogBounds(virtualWidth, virtualHeight);

        if (_editorPreviewFocusedField >= 0)
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

        if (_editorPreviewDraggingStart || _editorPreviewDraggingEnd)
        {
            if (leftDown)
                UpdateEditorPreviewRangeFromSlider(point.X, virtualWidth, virtualHeight);
            if (leftReleased)
            {
                _editorPreviewDraggingStart = false;
                _editorPreviewDraggingEnd = false;
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
            if (_editorPreviewFocusedField >= 0)
            {
                CommitEditorPreviewFocusedTime();
                if (_editorPreviewFocusedField >= 0)
                    return;
            }
            if (_editorPreviewPlayer?.IsPlaying == true)
            {
                StopEditorPreviewPlayback();
                _editorPreviewDialogMessage = "Preview playback stopped. Chart playback remains paused.";
            }
            else
            {
                try { PlayEditorPreviewSelection(); }
                catch (Exception ex)
                {
                    _editorPreviewDialogMessage = ex.Message;
                    _statusMessage = $"Could not play music preview: {ex.Message}";
                }
            }
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
                _editorPreviewDialogMessage = ex.Message;
                _statusMessage = $"Could not save music preview: {ex.Message}";
            }
            return;
        }
        if (GetEditorPreviewStartInputBounds(virtualWidth, virtualHeight).Contains(point))
        {
            var bounds = GetEditorPreviewStartInputBounds(virtualWidth, virtualHeight);
            _editorPreviewFocusedField = 0;
            _editorPreviewStartCaret = GetEditorPreviewTextCaretIndex(bounds, _editorPreviewStartText, point, _projection.ScalePixels(1f));
            _editorPreviewStartSelectionAnchor = _editorPreviewStartCaret;
            _editorPreviewInputReplaceOnNextKey = false;
            BeginEditorTextFieldMouseSelection("preview-start", bounds, _editorPreviewStartCaret, ref _editorPreviewStartSelectionAnchor);
            _editorPreviewDialogMessage = null;
            return;
        }
        if (GetEditorPreviewEndInputBounds(virtualWidth, virtualHeight).Contains(point))
        {
            var bounds = GetEditorPreviewEndInputBounds(virtualWidth, virtualHeight);
            _editorPreviewFocusedField = 1;
            _editorPreviewEndCaret = GetEditorPreviewTextCaretIndex(bounds, _editorPreviewEndText, point, _projection.ScalePixels(1f));
            _editorPreviewEndSelectionAnchor = _editorPreviewEndCaret;
            _editorPreviewInputReplaceOnNextKey = false;
            BeginEditorTextFieldMouseSelection("preview-end", bounds, _editorPreviewEndCaret, ref _editorPreviewEndSelectionAnchor);
            _editorPreviewDialogMessage = null;
            return;
        }
        var slider = GetEditorPreviewSliderBounds(virtualWidth, virtualHeight);
        if (slider.Contains(point))
        {
            _editorPreviewFocusedField = -1;
            var duration = EditorPreviewSourceDurationSeconds;
            if (duration > 0)
            {
                var ratio = Math.Clamp((point.X - slider.X) / (double)Math.Max(1, slider.Width), 0, 1);
                var clickedTime = ratio * duration;
                var startDistance = Math.Abs(clickedTime - _editorPreviewStartSeconds);
                var endDistance = Math.Abs(clickedTime - _editorPreviewEndSeconds);
                if (startDistance <= endDistance)
                    _editorPreviewDraggingStart = true;
                else
                    _editorPreviewDraggingEnd = true;
                UpdateEditorPreviewRangeFromSlider(point.X, virtualWidth, virtualHeight);
            }
            return;
        }
        if (!dialog.Contains(point))
            return;
        _editorPreviewFocusedField = -1;
    }

    private void UpdateEditorPreviewRangeFromSlider(int mouseX, int virtualWidth, int virtualHeight)
    {
        var duration = EditorPreviewSourceDurationSeconds;
        if (duration <= 0)
            return;
        var slider = GetEditorPreviewSliderBounds(virtualWidth, virtualHeight);
        var ratio = Math.Clamp((mouseX - slider.X) / (double)Math.Max(1, slider.Width), 0, 1);
        var value = ratio * duration;
        const double minimumRange = 0.05;
        if (_editorPreviewDraggingStart)
            _editorPreviewStartSeconds = Math.Clamp(value, 0, Math.Max(0, _editorPreviewEndSeconds - minimumRange));
        else if (_editorPreviewDraggingEnd)
            _editorPreviewEndSeconds = Math.Clamp(value, Math.Min(duration, _editorPreviewStartSeconds + minimumRange), duration);
        SyncEditorPreviewTimeTexts();
        _editorPreviewDialogMessage = null;
    }

    private Rectangle GetEditorMediaMusicLabelBounds(int virtualWidth, int virtualHeight)
    {
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        return new Rectangle(content.X, content.Y, content.Width, Math.Max(1, (int)MathF.Round(20f * unit)));
    }

    private Rectangle GetEditorMediaMusicStatusBounds(int virtualWidth, int virtualHeight)
    {
        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var buttonWidth = Math.Max(1, (int)MathF.Round(150f * unit));
        var gap = Math.Max(4, (int)MathF.Round(8f * unit));
        var y = content.Y + (int)MathF.Round(22f * unit);
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
        var y = content.Y + (int)MathF.Round(66f * unit);
        return new Rectangle(content.X, y, content.Width, Math.Max(1, (int)MathF.Round(20f * unit)));
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
        var top = content.Y + (int)MathF.Round(139f * unit);
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
                Math.Max(1, bounds.Width - horizontalPadding * 2), Math.Max(1, bounds.Bottom - bar.Bottom - (int)MathF.Round(3f * unit))),
            Text, unit);
    }

    private void DrawEditorMediaArtworkCard(string field, int virtualWidth, int virtualHeight, float unit)
    {
        var isBig = string.Equals(field, "album_big", StringComparison.OrdinalIgnoreCase);
        var card = GetEditorMediaArtworkCardBounds(isBig, virtualWidth, virtualHeight);
        var frame = GetEditorMediaArtworkFrameBounds(isBig, virtualWidth, virtualHeight);
        var label = isBig ? "ALBUM BIG" : "ALBUM";
        DrawEditorPreviewLabel(label,
            new Rectangle(card.X, card.Y, card.Width, Math.Max(1, (int)MathF.Round(24f * unit))), Muted, unit);

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

    private void DrawEditorMediaArtworkPreviewOverlay(int virtualWidth, int virtualHeight)
    {
        if (_pixel is null || !_editorMetaPanelOpen ||
            !string.Equals(_editorMetaCategory, "MEDIA", StringComparison.OrdinalIgnoreCase) ||
            !_chartEditorInterfaceRuntime.IsLoaded || _chartEditorInterfaceRuntime.LastError is not null)
            return;

        if (!_chartEditorInterfaceRuntime.TryGetUiButtonBounds("editor_meta_album", out var albumButton) ||
            albumButton.IsEmpty)
            return;

        var content = GetEditorMetaContentBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        _spriteBatch.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);
        DrawFrame("album", albumButton);
        if (_editorBundleTemplate != ChartBundleTemplate.Tsts &&
            _chartEditorInterfaceRuntime.TryGetUiButtonBounds("editor_meta_album_big", out var albumBigButton) &&
            !albumBigButton.IsEmpty)
            DrawFrame("album_big", albumBigButton);
        _spriteBatch.End();

        void DrawFrame(string field, Rectangle button)
        {
            // chartEditorInterface.lua places the album card after the two media rows:
            // content top + 2 * (20 label + 5 gap + 46 field + 8 row gap), then a
            // 34-unit label/header allowance and an 8-unit gap above the button.
            // Derive the frame from the registered button bounds so Lua and C# hit boxes
            // stay aligned at every UI scale.
            var sizeFromHeight = button.Y - content.Y - (int)MathF.Round(200f * unit);
            var sizeFromWidth = button.Width - (int)MathF.Round(4f * unit);
            var size = Math.Max(1, Math.Min((int)MathF.Round(220f * unit), Math.Min(sizeFromHeight, sizeFromWidth)));
            var gap = Math.Max(1, (int)MathF.Round(8f * unit));
            var frame = new Rectangle(button.X + (button.Width - size) / 2,
                button.Y - gap - size, size, size);
            var edge = Math.Max(1, (int)MathF.Round(2f * unit));
            var interfaceUi = _chartEditorInterfaceLayout;
            _spriteBatch.Draw(_pixel, frame, interfaceUi.ButtonColor);

            var imageBounds = new Rectangle(frame.X + edge, frame.Y + edge,
                Math.Max(1, frame.Width - edge * 2), Math.Max(1, frame.Height - edge * 2));
            var texture = GetEditorArtworkTexture(field);
            if (texture is not null && texture.Width > 0 && texture.Height > 0)
            {
                var scale = Math.Min(imageBounds.Width / (float)texture.Width,
                    imageBounds.Height / (float)texture.Height);
                var width = Math.Max(1, (int)MathF.Round(texture.Width * scale));
                var height = Math.Max(1, (int)MathF.Round(texture.Height * scale));
                var fitted = new Rectangle(imageBounds.X + (imageBounds.Width - width) / 2,
                    imageBounds.Y + (imageBounds.Height - height) / 2, width, height);
                _spriteBatch.Draw(texture, fitted, Color.White);
            }
            else
            {
                var placeholder = HasEditorMediaResource(field)
                    ? "IMAGE PREVIEW UNAVAILABLE"
                    : "NO IMAGE SET";
                DrawEditorPreviewLabel(placeholder, imageBounds, interfaceUi.MutedTextColor, unit);
            }

            var border = interfaceUi.AccentColor;
            _spriteBatch.Draw(_pixel, new Rectangle(frame.X, frame.Y, frame.Width, edge), border);
            _spriteBatch.Draw(_pixel, new Rectangle(frame.X, frame.Bottom - edge, frame.Width, edge), border);
            _spriteBatch.Draw(_pixel, new Rectangle(frame.X, frame.Y, edge, frame.Height), border);
            _spriteBatch.Draw(_pixel, new Rectangle(frame.Right - edge, frame.Y, edge, frame.Height), border);
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
            cache.Texture = Texture2D.FromStream(_graphics, stream);
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
            return Texture2D.FromStream(_graphics, stream);
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
                return Texture2D.FromStream(_graphics, normalizedStream);
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
            return Texture2D.FromStream(_graphics, stream);
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
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

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
                    return referencedPath;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Continue with template default names when metadata path is invalid.
            }
        }

        var names = field switch
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
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    private Rectangle GetEditorPreviewEditorDialogBounds(int virtualWidth, int virtualHeight)
    {
        var unit = _projection.ScalePixels(1f);
        var margin = (int)MathF.Round(18f * unit);
        var width = Math.Min(virtualWidth - margin * 2, (int)MathF.Round(860f * unit));
        var height = Math.Min(virtualHeight - margin * 2, (int)MathF.Round(500f * unit));
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        return new Rectangle((virtualWidth - width) / 2, (virtualHeight - height) / 2, width, height);
    }

    private Rectangle GetEditorPreviewCloseButtonBounds(int virtualWidth, int virtualHeight)
    {
        var panel = GetEditorPreviewEditorDialogBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var size = Math.Max(1, (int)MathF.Round(36f * unit));
        var margin = (int)MathF.Round(14f * unit);
        return new Rectangle(panel.Right - size - margin, panel.Y + margin, size, size);
    }

    private Rectangle GetEditorPreviewSliderBounds(int virtualWidth, int virtualHeight)
    {
        var panel = GetEditorPreviewEditorDialogBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var margin = (int)MathF.Round(62f * unit);
        var y = panel.Y + (int)MathF.Round(205f * unit);
        return new Rectangle(panel.X + margin, y, Math.Max(1, panel.Width - margin * 2), Math.Max(12, (int)MathF.Round(18f * unit)));
    }

    private Rectangle GetEditorPreviewStartThumbBounds(int virtualWidth, int virtualHeight)
    {
        var slider = GetEditorPreviewSliderBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var duration = EditorPreviewSourceDurationSeconds;
        var ratio = duration <= 0 ? 0 : _editorPreviewStartSeconds / duration;
        var centerX = slider.X + (int)Math.Round(Math.Clamp(ratio, 0, 1) * slider.Width);
        var size = Math.Max(16, (int)MathF.Round(24f * unit));
        return new Rectangle(centerX - size / 2, slider.Y + slider.Height / 2 - size / 2, size, size);
    }

    private Rectangle GetEditorPreviewEndThumbBounds(int virtualWidth, int virtualHeight)
    {
        var slider = GetEditorPreviewSliderBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var duration = EditorPreviewSourceDurationSeconds;
        var ratio = duration <= 0 ? 1 : _editorPreviewEndSeconds / duration;
        var centerX = slider.X + (int)Math.Round(Math.Clamp(ratio, 0, 1) * slider.Width);
        var size = Math.Max(16, (int)MathF.Round(24f * unit));
        return new Rectangle(centerX - size / 2, slider.Y + slider.Height / 2 - size / 2, size, size);
    }

    private Rectangle GetEditorPreviewStartInputBounds(int virtualWidth, int virtualHeight)
    {
        var panel = GetEditorPreviewEditorDialogBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var margin = (int)MathF.Round(62f * unit);
        var gap = (int)MathF.Round(24f * unit);
        var width = Math.Max(1, (panel.Width - margin * 2 - gap) / 2);
        var height = Math.Max(1, (int)MathF.Round(44f * unit));
        var y = panel.Y + (int)MathF.Round(295f * unit);
        return new Rectangle(panel.X + margin, y, width, height);
    }

    private Rectangle GetEditorPreviewEndInputBounds(int virtualWidth, int virtualHeight)
    {
        var panel = GetEditorPreviewEditorDialogBounds(virtualWidth, virtualHeight);
        var start = GetEditorPreviewStartInputBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var gap = (int)MathF.Round(24f * unit);
        return new Rectangle(start.Right + gap, start.Y, Math.Max(1, panel.Right - (int)MathF.Round(62f * unit) - start.Right - gap), start.Height);
    }

    private Rectangle GetEditorPreviewPlayButtonBounds(int virtualWidth, int virtualHeight)
    {
        var panel = GetEditorPreviewEditorDialogBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var padding = (int)MathF.Round(22f * unit);
        var gap = (int)MathF.Round(10f * unit);
        var height = Math.Max(1, (int)MathF.Round(38f * unit));
        var width = Math.Max(1, (panel.Width - padding * 2 - gap * 2) / 3);
        var y = panel.Bottom - padding - height;
        return new Rectangle(panel.X + padding, y, width, height);
    }

    private Rectangle GetEditorPreviewSaveButtonBounds(int virtualWidth, int virtualHeight)
    {
        var play = GetEditorPreviewPlayButtonBounds(virtualWidth, virtualHeight);
        var gap = (int)MathF.Round(10f * _projection.ScalePixels(1f));
        return new Rectangle(play.Right + gap, play.Y, play.Width, play.Height);
    }

    private Rectangle GetEditorPreviewCloseActionButtonBounds(int virtualWidth, int virtualHeight)
    {
        var panel = GetEditorPreviewEditorDialogBounds(virtualWidth, virtualHeight);
        var unit = _projection.ScalePixels(1f);
        var padding = (int)MathF.Round(22f * unit);
        var gap = (int)MathF.Round(10f * unit);
        var play = GetEditorPreviewPlayButtonBounds(virtualWidth, virtualHeight);
        var save = GetEditorPreviewSaveButtonBounds(virtualWidth, virtualHeight);
        var x = save.Right + gap;
        return new Rectangle(x, play.Y, Math.Max(1, panel.Right - padding - x), play.Height);
    }

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

    private void DrawEditorPreviewEditorOverlay(int virtualWidth, int virtualHeight)
    {
        var interfaceUi = _chartEditorInterfaceLayout;
        var headerFont = GetChartEditorFont(interfaceUi.HeaderFont);
        var labelFont = GetChartEditorFont(interfaceUi.LabelFont);
        var unit = _projection.ScalePixels(1f);
        var panel = GetEditorPreviewEditorDialogBounds(virtualWidth, virtualHeight);
        _spriteBatch.Draw(_pixel, new Rectangle(0, 0, virtualWidth, virtualHeight), new Color(0, 0, 0, 185));
        _spriteBatch.Draw(_pixel, panel, interfaceUi.PanelColor);
        var edge = Math.Max(2, (int)MathF.Round(2f * unit));
        _spriteBatch.Draw(_pixel, new Rectangle(panel.X, panel.Y, panel.Width, edge), interfaceUi.AccentColor);
        _spriteBatch.Draw(_pixel, new Rectangle(panel.X, panel.Bottom - edge, panel.Width, edge), interfaceUi.HeaderColor);
        _spriteBatch.Draw(_pixel, new Rectangle(panel.X, panel.Y, edge, panel.Height), interfaceUi.AccentColor);
        _spriteBatch.Draw(_pixel, new Rectangle(panel.Right - edge, panel.Y, edge, panel.Height), interfaceUi.HeaderColor);

        var title = FontSafeText(headerFont, "MUSIC PREVIEW");
        UnicodeTextRenderer.DrawString(_spriteBatch, headerFont, title, new Vector2(panel.X + 28f * unit, panel.Y + 22f * unit), interfaceUi.ButtonTextColor);
        var sourceDuration = EditorPreviewSourceDurationSeconds;
        var subtitle = sourceDuration > 0
            ? $"Choose a section of the full song. Length: {FormatEditorPreviewTimestamp(sourceDuration)}. A subtle fade is added at each end."
            : "Import the full song audio first to enable preview editing.";
        DrawEditorPreviewLabel(subtitle, new Rectangle(panel.X + 28, panel.Y + (int)MathF.Round(76f * unit), panel.Width - (int)MathF.Round(56f * unit), (int)MathF.Round(42f * unit)), Muted, unit);

        var slider = GetEditorPreviewSliderBounds(virtualWidth, virtualHeight);
        var sliderY = slider.Y + slider.Height / 2;
        var full = new Rectangle(slider.X, sliderY - Math.Max(2, (int)MathF.Round(3f * unit)), slider.Width, Math.Max(4, (int)MathF.Round(6f * unit)));
        _spriteBatch.Draw(_pixel, full, new Color(65, 65, 68, 245));
        var startRatio = sourceDuration <= 0 ? 0 : Math.Clamp(_editorPreviewStartSeconds / sourceDuration, 0, 1);
        var endRatio = sourceDuration <= 0 ? 0 : Math.Clamp(_editorPreviewEndSeconds / sourceDuration, 0, 1);
        var selectionStart = slider.X + (int)Math.Round(startRatio * slider.Width);
        var selectionEnd = slider.X + (int)Math.Round(endRatio * slider.Width);
        if (selectionEnd > selectionStart)
            _spriteBatch.Draw(_pixel, new Rectangle(selectionStart, sliderY - Math.Max(3, (int)MathF.Round(5f * unit)), selectionEnd - selectionStart, Math.Max(6, (int)MathF.Round(10f * unit))), Active);
        DrawEditorPreviewSliderThumb(GetEditorPreviewStartThumbBounds(virtualWidth, virtualHeight), Active);
        DrawEditorPreviewSliderThumb(GetEditorPreviewEndThumbBounds(virtualWidth, virtualHeight), new Color(52, 211, 153));

        var minLabel = FontSafeText(labelFont, "00:00.000");
        var maxLabel = FontSafeText(labelFont, FormatEditorPreviewTimestamp(sourceDuration));
        UnicodeTextRenderer.DrawString(_spriteBatch, labelFont, minLabel, new Vector2(slider.X, slider.Bottom + 8f * unit), interfaceUi.MutedTextColor);
        var maxSize = UnicodeTextRenderer.MeasureString(labelFont, maxLabel);
        UnicodeTextRenderer.DrawString(_spriteBatch, labelFont, maxLabel, new Vector2(slider.Right - maxSize.X, slider.Bottom + 8f * unit), interfaceUi.MutedTextColor);

        DrawEditorPreviewLabel("START TIME (SECONDS)", new Rectangle(GetEditorPreviewStartInputBounds(virtualWidth, virtualHeight).X, GetEditorPreviewStartInputBounds(virtualWidth, virtualHeight).Y - (int)MathF.Round(30f * unit), GetEditorPreviewStartInputBounds(virtualWidth, virtualHeight).Width, (int)MathF.Round(26f * unit)), interfaceUi.MutedTextColor, unit);
        DrawEditorPreviewLabel("END TIME (SECONDS)", new Rectangle(GetEditorPreviewEndInputBounds(virtualWidth, virtualHeight).X, GetEditorPreviewEndInputBounds(virtualWidth, virtualHeight).Y - (int)MathF.Round(30f * unit), GetEditorPreviewEndInputBounds(virtualWidth, virtualHeight).Width, (int)MathF.Round(26f * unit)), interfaceUi.MutedTextColor, unit);
        DrawEditorPreviewInput(GetEditorPreviewStartInputBounds(virtualWidth, virtualHeight), _editorPreviewStartText, _editorPreviewFocusedField == 0, _editorPreviewStartCaret, unit);
        DrawEditorPreviewInput(GetEditorPreviewEndInputBounds(virtualWidth, virtualHeight), _editorPreviewEndText, _editorPreviewFocusedField == 1, _editorPreviewEndCaret, unit);

        if (!string.IsNullOrWhiteSpace(_editorPreviewDialogMessage))
        {
            var messageBounds = new Rectangle(panel.X + 28, GetEditorPreviewStartInputBounds(virtualWidth, virtualHeight).Bottom + (int)MathF.Round(12f * unit), panel.Width - (int)MathF.Round(56f * unit), (int)MathF.Round(42f * unit));
            DrawEditorPreviewLabel(_editorPreviewDialogMessage!, messageBounds, _editorPreviewDialogMessage!.StartsWith("Could not", StringComparison.OrdinalIgnoreCase) || _editorPreviewDialogMessage!.StartsWith("Preview ", StringComparison.OrdinalIgnoreCase) ? new Color(252, 165, 165) : new Color(167, 243, 208), unit);
        }

        DrawEditorMetaButton(GetEditorPreviewPlayButtonBounds(virtualWidth, virtualHeight),
            _editorPreviewPlayer?.IsPlaying == true ? "STOP" : "PLAY",
            new Color(28, 28, 30, 250), Active);
        var rangeValid = sourceDuration > 0 && TryReadEditorPreviewRange(out _, out _, out _);
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
        var maxWidth = Math.Max(1f, bounds.Width - 4f * unit);
        var fitted = FitChartEditorText(rawText, _chartEditorInterfaceLayout.LabelFont, 24f,
            maxWidth, bounds.Height - 2f * unit);
        var size = fitted.Measured * fitted.Scale;
        UnicodeTextRenderer.DrawString(_spriteBatch, fitted.Font, fitted.Text,
            new Vector2(bounds.X + 2f * unit, bounds.Y + (bounds.Height - size.Y) * 0.5f),
            color, 0f, Vector2.Zero, fitted.Scale, SpriteEffects.None, 0f);
    }

    private void DrawEditorPreviewInput(Rectangle bounds, string text, bool focused, int caretIndex, float unit)
    {
        var interfaceUi = _chartEditorInterfaceLayout;
        var labelFont = GetChartEditorFont(interfaceUi.LabelFont);
        var accent = focused ? Active : new Color(100, 100, 102);
        _spriteBatch.Draw(_pixel, bounds, interfaceUi.ButtonColor);
        var edge = Math.Max(1, (int)MathF.Round(1.5f * unit));
        _spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Y, bounds.Width, edge), accent);
        _spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Bottom - edge, bounds.Width, edge), interfaceUi.HeaderColor);
        var caretPrefix = string.Empty;
        var visibleStart = 0;
        var visibleEnd = text.Length;
        var textWidth = Math.Max(1f, bounds.Width - 20f * unit);
        var textScale = Math.Min(1f, Math.Max(0.55f,
            (bounds.Height - 8f * unit) / Math.Max(1f, UnicodeTextRenderer.MeasureString(labelFont, "Ag").Y)));
        labelFont = GetChartEditorFont(interfaceUi.LabelFont, 24f * textScale);
        textScale = Math.Min(1f, (bounds.Height - 8f * unit) /
            Math.Max(1f, UnicodeTextRenderer.MeasureString(labelFont, "Ag").Y));
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
        UnicodeTextRenderer.DrawString(_spriteBatch, labelFont, display, textPosition, interfaceUi.ButtonTextColor, 0f, Vector2.Zero, new Vector2(textScale, textScale), SpriteEffects.None, 0f);
        if (focused) DrawEditorTextCaret(caretPrefix, textPosition, textScale, unit, labelFont);
    }
}
