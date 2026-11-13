using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TSC.Ares;
using TSC.Rendering;

namespace TSC;

internal sealed record EditorRecoveryChartSnapshot(
    string DifficultyCode,
    AresChart Chart,
    byte[]? AresHeaderTemplate,
    bool Disabled);

/// <summary>
/// Immutable editor data captured on the game thread. All expensive chart
/// serialization, media conversion, ZIP compression, and disk writes happen
/// after this object has been handed to a worker thread.
/// </summary>
internal sealed class EditorRecoveryCheckpointSnapshot
{
    public required EditorRecoveryManifest Manifest { get; init; }
    public required IReadOnlyList<EditorRecoveryChartSnapshot> Charts { get; init; }
    public required long Revision { get; init; }
    public byte[]? MusicBytes { get; init; }
    public string? MusicSourcePath { get; init; }
    public byte[]? OriginalMusicSourceBytes { get; init; }
    public string OriginalMusicSourceExtension { get; init; } = ".opus";
    public int MusicVolumePercent { get; init; } = 100;
    public byte[]? SharedPreviewBytes { get; init; }
    public byte[]? SharedAlbumBytes { get; init; }
    public string? AssetSourceDirectory { get; init; }
    public string? PreviewHint { get; init; }
    public string? AlbumHint { get; init; }
    public bool ExtraDifficultyEnabled { get; init; } = true;
    public string[] DisabledDifficultyCodes { get; init; } = Array.Empty<string>();
    public string[] CustomJudgementWindowEnabledDifficulties { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Builds a recovery TSTS bundle from an immutable snapshot. This class must
/// never access a live Playfield or a chart still owned by the editor thread.
/// </summary>
internal static class EditorRecoveryCheckpointWriter
{
    private static readonly string[] DifficultyCodes = { "ez", "nm", "hd", "ex", "xt" };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static bool Write(
        EditorRecoveryCheckpointSnapshot snapshot,
        long expectedStorageGeneration,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        error = null;
        var startedAt = Stopwatch.GetTimestamp();
        string? staging = null;
        string? bundlePath = null;
        try
        {
            staging = Path.Combine(Path.GetTempPath(), "TSC-EditorRecoveryBuild-" + Guid.NewGuid().ToString("N"));
            bundlePath = Path.Combine(Path.GetTempPath(), "TSC-EditorRecovery-" + Guid.NewGuid().ToString("N") + ".tsts");
            BuildBundle(snapshot, staging, bundlePath);
            var committed = EditorRecoveryStorage.CommitCheckpointArchive(bundlePath, expectedStorageGeneration);
            var durationMs = (Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency;
            Program.LogRuntime($"EDITOR_RECOVERY_BACKGROUND_WRITE_FINISHED committed={committed} duration_ms={durationMs:0.###}");
            return committed;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Program.LogRuntime($"EDITOR_RECOVERY_BACKGROUND_WRITE_FAILED error={ex}");
            return false;
        }
        finally
        {
            try { if (!string.IsNullOrWhiteSpace(staging) && Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
            try { if (!string.IsNullOrWhiteSpace(bundlePath) && File.Exists(bundlePath)) File.Delete(bundlePath); } catch { }
        }
    }

    private static void BuildBundle(EditorRecoveryCheckpointSnapshot snapshot, string staging, string bundlePath)
    {
        if (snapshot.Charts.Count == 0)
            throw new InvalidOperationException("There are no supplied or authored difficulty charts to recover.");

        var chartsDirectory = Path.Combine(staging, "charts");
        Directory.CreateDirectory(chartsDirectory);

        var musicBytes = snapshot.MusicBytes;
        if (snapshot.MusicVolumePercent < 100)
        {
            if (snapshot.OriginalMusicSourceBytes is not { Length: > 0 } sourceBytes)
                throw new InvalidOperationException("The original session audio is unavailable; the selected volume cannot be preserved in recovery.");
            musicBytes = CreateVolumeAdjustedOpusBytes(
                sourceBytes,
                snapshot.OriginalMusicSourceExtension,
                snapshot.MusicVolumePercent);
        }
        else if (musicBytes is not { Length: > 0 } &&
                 !string.IsNullOrWhiteSpace(snapshot.MusicSourcePath) &&
                 File.Exists(snapshot.MusicSourcePath))
        {
            // A chart can reference a sidecar music file without keeping it in
            // the editor's audio cache. Read it here, not during the game frame.
            musicBytes = File.ReadAllBytes(snapshot.MusicSourcePath);
        }
        var hasBundledMusic = musicBytes is { Length: > 0 };
        if (hasBundledMusic)
            File.WriteAllBytes(Path.Combine(staging, "song.opus"), musicBytes!);

        var previewSource = FindSourceAsset(
            snapshot.PreviewHint,
            snapshot.AssetSourceDirectory,
            "preview.opus", "preview.ogg", "preview.mp3", "preview.wav");
        var coverSource = FindSourceAsset(
            snapshot.AlbumHint,
            snapshot.AssetSourceDirectory,
            "cover.webp", "album.png", "album.jpg", "album.jpeg");
        var previewOutput = Path.Combine(staging, "preview.opus");
        var coverOutput = Path.Combine(staging, "cover.webp");

        if (snapshot.SharedPreviewBytes is { Length: > 0 } previewBytes)
            File.WriteAllBytes(previewOutput, previewBytes);
        else if (previewSource is not null)
            CopyOrConvertAsset(previewSource, previewOutput, outputWebp: false);

        if (coverSource is not null)
            CopyOrConvertAsset(coverSource, coverOutput, outputWebp: true);
        if (snapshot.SharedAlbumBytes is { Length: > 0 } albumBytes)
            WriteImportedCover(albumBytes, coverOutput);

        foreach (var item in snapshot.Charts)
        {
            var chart = item.Chart;
            chart.Series = null;
            chart.MusicPath = "song.opus";
            chart.PreviewPath = "preview.opus";
            chart.AlbumPath = "cover.webp";
            chart.AlbumBigPath = null;

            var chartFileName = GetTstsBundleChartFileName(item.DifficultyCode) + (item.Disabled ? ".disabled" : string.Empty);
            var chartPath = Path.Combine(chartsDirectory, chartFileName);
            File.WriteAllBytes(chartPath, AresWriter.BuildBytes(chart, item.AresHeaderTemplate));
        }

        // Passing root-level chart paths makes TscLoader create one shared
        // song.json beside the charts/ directory, matching normal TSTS exports.
        foreach (var item in snapshot.Charts)
        {
            // Match the regular exporter: the optional extra difficulty chart is
            // still included as chart data, but it must not create song.json
            // difficulty metadata while the extra difficulty feature is disabled.
            if (!snapshot.ExtraDifficultyEnabled &&
                string.Equals(item.DifficultyCode, "xt", StringComparison.OrdinalIgnoreCase))
                continue;

            TscLoader.SaveSongJsonSidecar(
                Path.Combine(staging, GetTstsBundleChartFileName(item.DifficultyCode)),
                item.Chart,
                hasBundledMusic ? "song.opus" : null,
                supportsSeries: false,
                schema: 3,
                includeJudgementWindows: snapshot.CustomJudgementWindowEnabledDifficulties.Contains(
                    item.DifficultyCode, StringComparer.OrdinalIgnoreCase));
        }

        var songJsonPath = Path.Combine(staging, "song.json");
        var songJson = JsonNode.Parse(File.ReadAllText(songJsonPath)) as JsonObject
            ?? throw new InvalidDataException("Could not create TSTS song.json metadata.");
        songJson["schema"] = 3;
        if (!snapshot.ExtraDifficultyEnabled)
            ApplyExtraDifficultyDisabledMetadata(songJson);
        ApplyDisabledDifficultyMetadata(songJson, snapshot.DisabledDifficultyCodes);
        songJson.Remove("series");
        songJson.Remove("album_big");
        songJson.Remove("music");
        songJson.Remove("preview");
        songJson.Remove("album");
        File.WriteAllText(songJsonPath,
            songJson.ToJsonString(JsonOptions) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        using (var output = ZipFile.Open(bundlePath, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(staging, file).Replace('\\', '/');
                output.CreateEntryFromFile(file, relative, CompressionLevel.Fastest);
            }

            // Put the recovery manifest into the initial archive. The commit path
            // can then copy the already-complete ZIP atomically instead of
            // decompressing and recompressing every chart/media entry a second time.
            var manifestEntry = output.CreateEntry("__tsc/editor-recovery.json", CompressionLevel.Fastest);
            using var manifestStream = manifestEntry.Open();
            JsonSerializer.Serialize(manifestStream, snapshot.Manifest, JsonOptions);
        }
    }

    private static string GetTstsBundleChartFileName(string? difficultyCode)
        => Playfield.GetEditorDifficultyCode(difficultyCode) switch
        {
            "ez" => "easy.ares",
            "nm" => "normal.ares",
            "hd" => "hard.ares",
            "xt" => "sc.ares",
            _ => "expert.ares"
        };

    private static void ApplyExtraDifficultyDisabledMetadata(JsonObject root)
    {
        RemoveMissingDifficultyLevels(root);
        if (root["difficulty_levels"] is JsonObject levels)
        {
            foreach (var key in levels.Select(pair => pair.Key)
                         .Where(key => key.Equals("sc", StringComparison.OrdinalIgnoreCase) ||
                                       key.Equals("secret", StringComparison.OrdinalIgnoreCase))
                         .ToArray())
                levels.Remove(key);
        }
        if (root["judgment_windows_ms"] is JsonObject windows)
            windows.Remove("sc");
        if (root["judgement_windows_ms"] is JsonObject britishWindows)
            britishWindows.Remove("sc");
        root.Remove("sc_passcode");
    }

    private static void ApplyDisabledDifficultyMetadata(JsonObject root, IReadOnlyCollection<string> disabledCodes)
    {
        RemoveMissingDifficultyLevels(root);
        foreach (var code in DifficultyCodes.Where(code => disabledCodes.Contains(code, StringComparer.OrdinalIgnoreCase)))
        {
            var aliases = code switch
            {
                "ez" => new[] { "ez", "easy" },
                "nm" => new[] { "nm", "normal" },
                "hd" => new[] { "hd", "hard" },
                "ex" => new[] { "ex", "expert" },
                _ => new[] { "sc", "xt", "extra", "secret" }
            };
            foreach (var sectionName in new[] { "difficulty_levels", "judgment_windows_ms", "judgement_windows_ms" })
            {
                if (root[sectionName] is not JsonObject values)
                    continue;
                foreach (var alias in aliases)
                    values.Remove(alias);
            }
        }
    }

    private static void RemoveMissingDifficultyLevels(JsonObject root)
    {
        if (root["difficulty_levels"] is not JsonObject levels)
            return;
        foreach (var key in levels.Where(pair => pair.Value is null).Select(pair => pair.Key).ToArray())
            levels.Remove(key);
    }

    private static string? FindSourceAsset(string? requestedPath, string? sourceDirectory, params string[] fallbackNames)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            try
            {
                if (Path.IsPathRooted(requestedPath) && File.Exists(requestedPath))
                    return Path.GetFullPath(requestedPath);
                if (!string.IsNullOrWhiteSpace(sourceDirectory))
                {
                    var candidate = Path.GetFullPath(Path.Combine(sourceDirectory, requestedPath));
                    if (File.Exists(candidate)) return candidate;
                }
                if (File.Exists(requestedPath))
                    return Path.GetFullPath(requestedPath);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Try well-known sidecar names below.
            }
        }

        if (!string.IsNullOrWhiteSpace(sourceDirectory) && Directory.Exists(sourceDirectory))
        {
            foreach (var name in fallbackNames)
            {
                var candidate = Path.Combine(sourceDirectory, name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static void CopyOrConvertAsset(string sourcePath, string outputPath, bool outputWebp)
    {
        var expectedExtension = outputWebp ? ".webp" : ".opus";
        if (string.Equals(Path.GetExtension(sourcePath), expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourcePath, outputPath, overwrite: true);
            return;
        }

        var ffmpeg = FfmpegExecutableResolver.FindExecutable();
        if (ffmpeg is null)
            throw new InvalidOperationException(outputWebp
                ? "FFmpeg is required to convert the recovery cover image to WebP."
                : "FFmpeg is required to convert the recovery preview audio to Opus.");
        var start = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", sourcePath })
            start.ArgumentList.Add(argument);
        if (outputWebp)
        {
            // Match the normal TSTS exporter: preserve album art losslessly.
            foreach (var argument in new[] { "-frames:v", "1", "-c:v", "libwebp", "-lossless", "1", "-quality", "100", "-f", "webp", outputPath })
                start.ArgumentList.Add(argument);
        }
        else
        {
            foreach (var argument in new[] { "-map", "0:a:0", "-vn", "-ac", "2", "-ar", "48000", "-c:a", "libopus", "-b:a", "160k", "-application", "audio", "-vbr", "on", "-f", "opus", outputPath })
                start.ArgumentList.Add(argument);
        }
        RunFfmpeg(start, outputPath);
    }

    private static void WriteImportedCover(byte[] imageBytes, string outputPath)
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), "TSC-EditorRecoveryCover-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            File.WriteAllBytes(temporaryPath, imageBytes);
            CopyOrConvertAsset(temporaryPath, outputPath, outputWebp: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private static byte[] CreateVolumeAdjustedOpusBytes(byte[] sourceBytes, string? sourceExtension, int volumePercent)
    {
        var ffmpeg = FfmpegExecutableResolver.FindExecutable();
        if (ffmpeg is null)
            throw new InvalidOperationException("FFmpeg is required to preserve the selected song volume in the recovery archive.");

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "TSC-EditorRecoveryAudio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var extension = NormalizeAudioExtension(sourceExtension);
            var inputPath = Path.Combine(temporaryDirectory, "original" + extension);
            var outputPath = Path.Combine(temporaryDirectory, "volume.opus");
            File.WriteAllBytes(inputPath, sourceBytes);
            var gain = (Math.Clamp(volumePercent, 0, 100) / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
            var start = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
            {
                "-hide_banner", "-loglevel", "error", "-y", "-i", inputPath,
                "-map", "0:a:0", "-vn", "-af", $"volume={gain}:precision=double",
                "-ac", "2", "-ar", "48000", "-c:a", "libopus", "-b:a", "160k",
                "-application", "audio", "-vbr", "on", "-f", "opus", outputPath
            })
                start.ArgumentList.Add(argument);
            RunFfmpeg(start, outputPath);
            return File.ReadAllBytes(outputPath);
        }
        finally
        {
            try { Directory.Delete(temporaryDirectory, recursive: true); } catch { }
        }
    }

    private static string NormalizeAudioExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 12 ||
            extension.Any(character => !char.IsLetterOrDigit(character) && character != '.'))
            return ".opus";
        return extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
    }

    private static void RunFfmpeg(ProcessStartInfo start, string outputPath)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start FFmpeg for recovery.");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
            var detail = string.IsNullOrWhiteSpace(stderr) ? "FFmpeg did not create the requested recovery asset." : stderr.Trim();
            if (detail.Length > 1000) detail = detail[..1000];
            throw new InvalidDataException($"Recovery media conversion failed: {detail}");
        }
    }
}
