using SharpCompress.Archives.SevenZip;

namespace TSC;

/// <summary>
/// Finds FFmpeg and, when present, installs ffmpeg.exe from the optional
/// tools/ffmpeg.7z archive. Only the executable is extracted from the archive.
/// </summary>
internal static class FfmpegExecutableResolver
{
    private const long MinimumExecutableBytes = 5L * 1024 * 1024;
    private const long MaximumExecutableBytes = 512L * 1024 * 1024;
    private static readonly object ArchiveLock = new();
    private static readonly HashSet<string> AttemptedArchives = new(StringComparer.OrdinalIgnoreCase);

    public static string? FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured))
                return Path.GetFullPath(configured);

            if (Directory.Exists(configured))
            {
                var candidate = Path.Combine(configured, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        foreach (var candidate in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe")
        })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        // The bundled Essentials archive is optional. When it is included, extract
        // just ffmpeg.exe beside the archive instead of requiring a separate install.
        var appArchive = TryExtractAppLocalArchive();
        if (appArchive is not null)
            return appArchive;

#if !TSC_PUBLISH
        // Developer builds may run from bin/Debug/... while tools/ sits at the
        // project root. Publish builds intentionally search only beside the app.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            foreach (var candidate in new[]
            {
                Path.Combine(directory.FullName, "tools", "ffmpeg.exe"),
                Path.Combine(directory.FullName, "Tools", "ffmpeg.exe")
            })
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            foreach (var archivePath in new[]
            {
                Path.Combine(directory.FullName, "tools", "ffmpeg.7z"),
                Path.Combine(directory.FullName, "Tools", "ffmpeg.7z")
            })
            {
                if (!File.Exists(archivePath))
                    continue;

                var extracted = TryExtractArchive(archivePath);
                if (extracted is not null)
                    return extracted;
            }
        }
#endif

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string? TryExtractAppLocalArchive()
    {
        foreach (var archivePath in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.7z"),
            Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.7z")
        })
        {
            if (!File.Exists(archivePath))
                continue;

            var extracted = TryExtractArchive(archivePath);
            if (extracted is not null)
                return extracted;
        }

        return null;
    }

    private static string? TryExtractArchive(string archivePath)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        lock (ArchiveLock)
        {
            var fullArchivePath = Path.GetFullPath(archivePath);
            var archiveDirectory = Path.GetDirectoryName(fullArchivePath);
            if (string.IsNullOrEmpty(archiveDirectory))
                return null;

            var destinationPath = Path.Combine(archiveDirectory, "ffmpeg.exe");
            if (File.Exists(destinationPath))
                return destinationPath;

            // Avoid repeatedly parsing a broken archive if several game features
            // attempt to use FFmpeg during the same session.
            if (!AttemptedArchives.Add(fullArchivePath))
                return null;

            var temporaryPath = Path.Combine(archiveDirectory, $"ffmpeg-{Guid.NewGuid():N}.tmp");
            try
            {
                using var archive = SevenZipArchive.OpenArchive(fullArchivePath);
                var entry = archive.Entries.FirstOrDefault(candidate =>
                {
                    var key = candidate.Key;
                    return !candidate.IsDirectory &&
                           !string.IsNullOrWhiteSpace(key) &&
                           string.Equals(Path.GetFileName(key.Replace('\\', '/')), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase);
                });

                if (entry is null)
                    throw new InvalidDataException("The archive does not contain ffmpeg.exe.");
                if (entry.Size < MinimumExecutableBytes || entry.Size > MaximumExecutableBytes)
                    throw new InvalidDataException($"The archived ffmpeg.exe has an unexpected size ({entry.Size} bytes).");

                using (var input = entry.OpenEntryStream())
                using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.SequentialScan))
                {
                    input.CopyTo(output, 128 * 1024);
                    output.Flush();
                }

                var extractedLength = new FileInfo(temporaryPath).Length;
                if (extractedLength < MinimumExecutableBytes || extractedLength > MaximumExecutableBytes)
                    throw new InvalidDataException($"The extracted ffmpeg.exe has an unexpected size ({extractedLength} bytes).");

                File.Move(temporaryPath, destinationPath, overwrite: true);
                Program.LogRuntime($"FFMPEG_BUNDLED_ARCHIVE_EXTRACTED archive={fullArchivePath} output={destinationPath}");
                return destinationPath;
            }
            catch (Exception ex)
            {
                Program.LogRuntime($"FFMPEG_BUNDLED_ARCHIVE_EXTRACT_FAILED archive={fullArchivePath} error={ex}");
                return null;
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch
                {
                    // Best-effort cleanup; keep the original extraction result.
                }
            }
        }
    }
}
