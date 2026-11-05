using System.Diagnostics;
using System.Globalization;
using TSC;

namespace TSC.Rendering;

public sealed partial class Playfield
{
    private static string NormalizeEditorAudioSourceExtension(string? path)
    {
        var extension = Path.GetExtension(path ?? string.Empty);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 12 ||
            extension.Any(character => !char.IsLetterOrDigit(character) && character != '.'))
            return ".opus";
        return extension.ToLowerInvariant();
    }

    private void SetEditorMusicVolumePendingPercent(double value)
    {
        if (!double.IsFinite(value))
            return;
        _editorMusicVolumePendingPercent = Math.Clamp(
            (int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 100);

        // The slider controls the live editor playback level. It never changes
        // the encoded song bytes; those are baked only when an audio file is
        // actually saved or exported.
        ApplyEditorMusicPlaybackVolume();
    }

    private float GetEditorMusicPlaybackVolume()
    {
        var editorScale = _chartEditorMode
            ? Math.Clamp(_editorMusicVolumePendingPercent / 100f, 0f, 1f)
            : 1f;
        return _skin.GameConfig.MusicVolume * editorScale;
    }

    private void ApplyEditorMusicPlaybackVolume()
    {
        var volume = GetEditorMusicPlaybackVolume();
        _audio.Volume = volume;
        if (_editorPreviewPlayer is not null)
            _editorPreviewPlayer.Volume = volume;
    }

    private void ApplyEditorMusicVolumePendingPercent()
    {
        if (!_chartEditorMode || _chart is null ||
            _editorOriginalMusicSourceBytes is not { Length: > 0 } ||
            _editorOriginalMusicOpusBytes is not { Length: > 0 })
        {
            _editorMusicVolumePendingPercent = _editorMusicVolumePercent;
            ApplyEditorMusicPlaybackVolume();
            _statusMessage = "Import song audio before changing its volume.";
            return;
        }

        var targetPercent = Math.Clamp(_editorMusicVolumePendingPercent, 0, 100);
        if (targetPercent == _editorMusicVolumePercent)
        {
            ApplyEditorMusicPlaybackVolume();
            return;
        }

        // Commit the slider value only. Playback is attenuated in-engine through
        // SoundEffectInstance.Volume; the pristine audio copies remain untouched.
        // CreateEditorMusicVolumeOpusBytes is called later by save/export paths.
        _editorMusicVolumePercent = targetPercent;
        _editorMusicVolumePendingPercent = targetPercent;
        ApplyEditorMusicPlaybackVolume();
        MarkEditorDirty(auxiliaryChange: true);
        _statusMessage = targetPercent == 100
            ? "Editor music volume restored to 100%. Original audio is unchanged."
            : $"Editor music volume set to {targetPercent}%. It will be baked when saving/exporting.";
    }

    private byte[]? GetEditorSessionOriginalMusicBytes()
    {
        if (_editorOriginalMusicOpusBytes is { Length: > 0 } originalOpus)
            return originalOpus.ToArray();
        if (_editorSharedMusicBytes is { Length: > 0 } shared)
            return shared.ToArray();
        if (_loadedMusicBytes is { Length: > 0 } loaded)
            return loaded.ToArray();
        return _editorDifficultyStates.Values
            .Select(state => state.MusicBytes)
            .FirstOrDefault(bytes => bytes is { Length: > 0 })?.ToArray();
    }

    private byte[]? GetEditorMusicBytesForExport()
    {
        if (_editorMusicVolumePercent < 100)
        {
            if (_editorOriginalMusicSourceBytes is not { Length: > 0 } ||
                _editorOriginalMusicOpusBytes is not { Length: > 0 })
                throw new InvalidOperationException("The original session audio is unavailable; the selected volume cannot be baked safely.");
            return CreateEditorMusicVolumeOpusBytes(_editorMusicVolumePercent);
        }

        if (_editorOriginalMusicOpusBytes is { Length: > 0 } originalOpus)
            return originalOpus.ToArray();
        if (_editorSharedMusicBytes is { Length: > 0 } shared)
            return shared.ToArray();
        if (_loadedMusicBytes is { Length: > 0 } loaded)
            return loaded.ToArray();

        var stateBytes = _editorDifficultyStates.Values
            .Select(state => state.MusicBytes)
            .FirstOrDefault(bytes => bytes is { Length: > 0 });
        if (stateBytes is { Length: > 0 })
            return stateBytes.ToArray();

        if (string.IsNullOrWhiteSpace(_loadedSourcePath))
            return null;

        var sourceDirectory = Path.GetDirectoryName(_loadedSourcePath);
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
            return null;

        if (!string.IsNullOrWhiteSpace(_chart?.MusicPath))
        {
            var candidate = Path.GetFullPath(Path.Combine(sourceDirectory, _chart.MusicPath));
            if (File.Exists(candidate))
                return File.ReadAllBytes(candidate);
        }

        foreach (var name in new[] { "music.opus", "song.opus" })
        {
            var candidate = Path.Combine(sourceDirectory, name);
            if (File.Exists(candidate))
                return File.ReadAllBytes(candidate);
        }
        return null;
    }

    private byte[] CreateEditorMusicVolumeOpusBytes(int volumePercent)
    {
        if (_editorOriginalMusicSourceBytes is not { Length: > 0 } sourceBytes)
            throw new InvalidOperationException("The original song audio is no longer available in this editor session.");

        var ffmpeg = FindTstsFfmpegExecutable();
        if (ffmpeg is null)
            throw ExternalDependencyPrompts.CreateFfmpegNotFoundException("bake the selected song volume into the audio file");

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"tsc_music_volume_{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var extension = NormalizeEditorAudioSourceExtension(_editorOriginalMusicSourceExtension);
            var inputPath = Path.Combine(temporaryDirectory, "original" + extension);
            var outputPath = Path.Combine(temporaryDirectory, "volume.opus");
            File.WriteAllBytes(inputPath, sourceBytes);

            var gain = (volumePercent / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
            var start = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-i", inputPath,
                "-map", "0:a:0", "-vn",
                "-af", $"volume={gain}:precision=double",
                "-ac", "2", "-ar", "48000",
                "-c:a", "libopus", "-b:a", "160k", "-application", "audio", "-vbr", "on", "-f", "opus",
                outputPath
            })
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start FFmpeg.");
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? "FFmpeg did not produce a volume-adjusted Opus file." : stderr.Trim();
                if (detail.Length > 1000) detail = detail[..1000];
                throw new InvalidDataException($"Could not bake song volume: {detail}");
            }

            return File.ReadAllBytes(outputPath);
        }
        finally
        {
            try { Directory.Delete(temporaryDirectory, recursive: true); } catch { }
        }
    }
}
