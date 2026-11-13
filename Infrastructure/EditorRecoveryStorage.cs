using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace TSC;

/// <summary>
/// Describes how a recovery copy maps back to the file or bundle the user was
/// editing when TSC was interrupted. The chart data itself stays in the TSTS
/// archive so normal chart loading can restore it without a second serializer.
/// </summary>
internal sealed class EditorRecoveryManifest
{
    public int FormatVersion { get; set; } = 1;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string BundleTemplate { get; set; } = "Tsc";
    public string? SourcePath { get; set; }
    public string? BundlePath { get; set; }
    public string? DifficultyDiscoverySourcePath { get; set; }
    public string? SourceName { get; set; }
    public string? LastLoadedSourceName { get; set; }
    public string? FpkChartName { get; set; }
    public string ActiveDifficultyCode { get; set; } = "ex";
    public string? ActiveSeries { get; set; }
    public bool ExtraDifficultyEnabled { get; set; } = true;
    public string[] DisabledDifficultyCodes { get; set; } = Array.Empty<string>();
    public string[] DeletedDifficultyCodes { get; set; } = Array.Empty<string>();
    public string[] CommittedDeletedDifficultyCodes { get; set; } = Array.Empty<string>();
    public string[] CustomJudgementWindowEnabledDifficulties { get; set; } = Array.Empty<string>();
    public Dictionary<string, EditorRecoveryDifficulty> Difficulties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class EditorRecoveryDifficulty
{
    public string? SourcePath { get; set; }
    public string? SourceName { get; set; }
    public string? FpkChartName { get; set; }
    public string? Series { get; set; }
    public bool HasSourceChart { get; set; }
}

/// <summary>
/// Atomically stores a complete TSTS recovery bundle plus the source-path mapping
/// needed to make Quick Save target the user's original file again.
/// </summary>
internal static class EditorRecoveryStorage
{
    private const string ManifestEntryName = "__tsc/editor-recovery.json";
    private const string DisarmMarkerFileName = "editor-recovery.disarmed";
    private static readonly TimeSpan StaleTemporaryFileAge = TimeSpan.FromDays(1);
    private static readonly object Sync = new();
    private static long _generation;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string RecoveryPath => UserDataPaths.EditorRecoveryPath;
    public static string DisarmMarkerPath => Path.Combine(
        Path.GetDirectoryName(RecoveryPath) ?? UserDataPaths.RootDirectory,
        DisarmMarkerFileName);
    public static bool IsDisarmed => File.Exists(DisarmMarkerPath);
    public static bool Exists => File.Exists(RecoveryPath);
    public static long Generation => Volatile.Read(ref _generation);

    /// <summary>Invalidates in-flight background writes without deleting the last committed checkpoint.</summary>
    public static long InvalidatePendingCommits()
    {
        lock (Sync)
            return Interlocked.Increment(ref _generation);
    }

    public static bool CommitCheckpoint(string bundlePath, EditorRecoveryManifest manifest, long expectedGeneration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        ArgumentNullException.ThrowIfNull(manifest);
        if (!File.Exists(bundlePath))
            throw new FileNotFoundException("The editor recovery bundle was not created.", bundlePath);

        var destination = RecoveryPath;
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("The editor recovery directory could not be resolved.");
        Directory.CreateDirectory(directory);

        var temporaryPath = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var source = ZipFile.OpenRead(bundlePath))
            using (var outputStream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var output = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var sourceEntry in source.Entries)
                    {
                        if (string.Equals(sourceEntry.FullName, ManifestEntryName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var outputEntry = output.CreateEntry(sourceEntry.FullName, CompressionLevel.Fastest);
                        using var input = sourceEntry.Open();
                        using var target = outputEntry.Open();
                        input.CopyTo(target);
                    }

                    var manifestEntry = output.CreateEntry(ManifestEntryName, CompressionLevel.Fastest);
                    using var manifestStream = manifestEntry.Open();
                    JsonSerializer.Serialize(manifestStream, manifest, JsonOptions);
                }

                outputStream.Flush(flushToDisk: true);
            }

            // Serialize commit and clear operations. A save that invalidates this
            // generation must win over a slower background checkpoint, otherwise
            // an old dirty snapshot could reappear after the user saved.
            lock (Sync)
            {
                if (expectedGeneration != _generation)
                    return false;

                // The temporary file is in the same directory, so replacement is
                // atomic. A crash leaves the previous checkpoint intact.
                File.Move(temporaryPath, destination, overwrite: true);
                // A newly committed, complete checkpoint re-arms recovery. The
                // marker is removed only after the new archive is in place, so a
                // crash can never expose an older, intentionally discarded file.
                return TryRemoveDisarmMarker();
            }
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    /// <summary>
    /// Commits a recovery ZIP that already contains its manifest. This avoids a
    /// second ZIP rewrite on every background checkpoint.
    /// </summary>
    public static bool CommitCheckpointArchive(string bundlePath, long expectedGeneration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        if (!File.Exists(bundlePath))
            throw new FileNotFoundException("The editor recovery bundle was not created.", bundlePath);

        var destination = RecoveryPath;
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("The editor recovery directory could not be resolved.");
        Directory.CreateDirectory(directory);

        var temporaryPath = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Copy to a same-directory temporary file, then perform a generation
            // check and atomic replacement. Clear/save can invalidate this copy
            // while it is being written without waiting for the worker.
            File.Copy(bundlePath, temporaryPath, overwrite: false);
            using (var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                stream.Flush(flushToDisk: true);

            lock (Sync)
            {
                if (expectedGeneration != _generation)
                    return false;

                File.Move(temporaryPath, destination, overwrite: true);
                return TryRemoveDisarmMarker();
            }
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    public static bool TryReadManifest(string path, out EditorRecoveryManifest? manifest)
    {
        manifest = null;
        if (IsDisarmed || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry(ManifestEntryName);
            if (entry is null)
                return false;

            using var stream = entry.Open();
            var parsed = JsonSerializer.Deserialize<EditorRecoveryManifest>(stream, JsonOptions);
            if (parsed is null || parsed.FormatVersion != 1 || string.IsNullOrWhiteSpace(parsed.BundleTemplate))
                return false;

            manifest = parsed;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            Program.LogRuntime($"EDITOR_RECOVERY_MANIFEST_READ_FAILED error={ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Permanently disarms the current recovery checkpoint before attempting to
    /// delete it. The marker is authoritative if Windows refuses to delete the
    /// archive, and remains until a later complete checkpoint is committed.
    /// </summary>
    public static bool Clear()
    {
        lock (Sync)
        {
            Interlocked.Increment(ref _generation);
            var markerWritten = TryWriteDisarmMarker();
            var archiveDeleted = TryDeleteRecoveryArchive();
            var disarmed = markerWritten || archiveDeleted;

            Program.LogRuntime(
                $"EDITOR_RECOVERY_CLEARED marker_written={markerWritten} archive_deleted={archiveDeleted} disarmed={disarmed}");
            return disarmed;
        }
    }

    /// <summary>
    /// Performs best-effort startup cleanup. A disarm marker is intentionally
    /// retained; it prevents an undeletable stale archive from being restored.
    /// </summary>
    public static void CleanupStartupArtifacts()
    {
        CleanupStaleTemporaryFiles();
        if (IsDisarmed)
            CleanupDisarmedRecoveryArchive();
    }

    public static void CleanupDisarmedRecoveryArchive()
    {
        lock (Sync)
        {
            if (!IsDisarmed)
                return;
            if (TryDeleteRecoveryArchive())
                Program.LogRuntime("EDITOR_RECOVERY_DISARMED_ARCHIVE_CLEANUP completed=true");
        }
    }

    private static bool TryWriteDisarmMarker()
    {
        var markerPath = DisarmMarkerPath;
        var directory = Path.GetDirectoryName(markerPath)
            ?? throw new InvalidOperationException("The editor recovery directory could not be resolved.");
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(markerPath))
                return true;

            temporaryPath = markerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var markerText =
                "format=1\n" +
                "disarmed_utc=" + DateTimeOffset.UtcNow.ToString("O") + "\n" +
                "process_id=" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n";
            var bytes = Encoding.UTF8.GetBytes(markerText);
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, markerPath, overwrite: false);
                temporaryPath = null;
                return true;
            }
            catch (IOException) when (File.Exists(markerPath))
            {
                // Another TSC process may have written the same authoritative marker.
                return true;
            }
        }
        catch (Exception ex)
        {
            Program.LogRuntime($"EDITOR_RECOVERY_DISARM_MARKER_WRITE_FAILED error={ex}");
            return File.Exists(markerPath);
        }
        finally
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(temporaryPath) && File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch { }
        }
    }

    private static bool TryRemoveDisarmMarker()
    {
        try
        {
            if (File.Exists(DisarmMarkerPath))
                File.Delete(DisarmMarkerPath);
            return !File.Exists(DisarmMarkerPath);
        }
        catch (Exception ex)
        {
            Program.LogRuntime($"EDITOR_RECOVERY_DISARM_MARKER_REMOVE_FAILED error={ex}");
            return false;
        }
    }

    private static bool TryDeleteRecoveryArchive()
    {
        try
        {
            if (File.Exists(RecoveryPath))
                File.Delete(RecoveryPath);
            return !File.Exists(RecoveryPath);
        }
        catch (Exception ex)
        {
            Program.LogRuntime($"EDITOR_RECOVERY_ARCHIVE_DELETE_FAILED error={ex}");
            return false;
        }
    }

    private static void CleanupStaleTemporaryFiles()
    {
        try
        {
            var directory = Path.GetDirectoryName(RecoveryPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return;

            var cutoff = DateTime.UtcNow - StaleTemporaryFileAge;
            foreach (var path in Directory.EnumerateFiles(directory, "*.tmp", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var name = Path.GetFileName(path);
                    if (!IsGeneratedTemporaryFileName(name, Path.GetFileName(RecoveryPath)) &&
                        !IsGeneratedTemporaryFileName(name, DisarmMarkerFileName))
                        continue;
                    if (File.GetLastWriteTimeUtc(path) >= cutoff)
                        continue;
                    File.Delete(path);
                    Program.LogRuntime($"EDITOR_RECOVERY_STALE_TEMP_REMOVED path={path}");
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Program.LogRuntime($"EDITOR_RECOVERY_STALE_TEMP_CLEANUP_FAILED error={ex}");
        }
    }

    private static bool IsGeneratedTemporaryFileName(string name, string targetFileName)
    {
        var prefix = targetFileName + ".";
        const string suffix = ".tmp";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return false;

        var token = name[prefix.Length..^suffix.Length];
        return Guid.TryParseExact(token, "N", out _);
    }
}
