using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TSC;

internal enum ChartZipBundleKind
{
    Unknown,
    Tsc,
    Tsts
}

/// <summary>
/// Shared safe ZIP extraction and structure detection for chart picker and Song Select.
/// ZIP contents are extracted into a cache keyed by archive path, size, and write time.
/// </summary>
internal static class ChartZipBundleSupport
{
    public static string ExtractToCache(string archivePath, string cacheRoot)
    {
        var fullArchivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullArchivePath))
            throw new FileNotFoundException("Chart ZIP was not found.", fullArchivePath);

        var info = new FileInfo(fullArchivePath);
        var identity = fullArchivePath.ToUpperInvariant() + "|" +
                       info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" +
                       info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var root = Path.Combine(Path.GetFullPath(cacheRoot), hash);
        var marker = root + ".source-key";

        try
        {
            if (Directory.Exists(root) && File.Exists(marker) &&
                string.Equals(File.ReadAllText(marker, Encoding.UTF8), identity, StringComparison.Ordinal) &&
                ArchiveContentsPresent(fullArchivePath, root))
                return root;
        }
        catch { }

        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        try { if (File.Exists(marker)) File.Delete(marker); } catch { }

        Directory.CreateDirectory(root);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;

        try
        {
            using var archive = ZipFile.OpenRead(fullArchivePath);
            foreach (var entry in archive.Entries)
            {
                var entryNameRaw = entry.FullName.Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(entryNameRaw))
                    continue;
                if (entryNameRaw.StartsWith("/", StringComparison.Ordinal) ||
                    (entryNameRaw.Length >= 2 && char.IsLetter(entryNameRaw[0]) && entryNameRaw[1] == ':'))
                    throw new InvalidDataException("The ZIP contains an absolute path.");

                var segments = entryNameRaw.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Where(segment => segment != ".")
                    .ToArray();
                if (segments.Length == 0) continue;
                if (segments.Any(segment => segment == ".."))
                    throw new InvalidDataException("The ZIP contains an invalid path.");
                var entryName = Path.Combine(segments);
                var destination = Path.GetFullPath(Path.Combine(root, entryName));
                if (!destination.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The ZIP contains an invalid path.");

                if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, identity, new UTF8Encoding(false));
            return root;
        }
        catch
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Extracts an archive into a new, caller-owned directory. Unlike ExtractToCache,
    /// this is intended for importing a bundle into the songs library, where the
    /// extracted files become the permanent song files rather than a cache copy.
    /// The destination must be absent or empty. Any extraction failure removes the
    /// partial destination so callers can safely retain the original archive.
    /// </summary>
    public static void ExtractToDirectory(string archivePath, string destinationRoot)
    {
        var fullArchivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullArchivePath))
            throw new FileNotFoundException("Chart bundle was not found.", fullArchivePath);

        var root = Path.GetFullPath(destinationRoot);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("The extraction destination is not empty.");
        if (File.Exists(root))
            throw new IOException("The extraction destination is an existing file.");

        Directory.CreateDirectory(root);
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var archive = ZipFile.OpenRead(fullArchivePath);
            foreach (var entry in archive.Entries)
            {
                var rawName = entry.FullName.Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(rawName))
                    continue;
                if (rawName.StartsWith("/", StringComparison.Ordinal) ||
                    (rawName.Length >= 2 && char.IsLetter(rawName[0]) && rawName[1] == ':'))
                    throw new InvalidDataException("The bundle contains an absolute path.");

                var segments = rawName.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Where(segment => segment != ".")
                    .ToArray();
                if (segments.Length == 0)
                    continue;
                if (segments.Any(segment => segment == ".."))
                    throw new InvalidDataException("The bundle contains an invalid path.");

                var destination = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
                if (!destination.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The bundle contains an invalid path.");

                if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || string.IsNullOrEmpty(entry.Name))
                {
                    if (File.Exists(destination))
                        throw new InvalidDataException("The bundle contains conflicting file and directory paths.");
                    Directory.CreateDirectory(destination);
                    continue;
                }

                if (!seenFiles.Add(destination))
                    throw new InvalidDataException("The bundle contains duplicate file paths.");
                if (Directory.Exists(destination))
                    throw new InvalidDataException("The bundle contains conflicting file and directory paths.");

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = entry.Open();
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }
        }
        catch
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch { }
            throw;
        }
    }

    private static bool ArchiveContentsPresent(string archivePath, string extractedRoot)
    {
        try
        {
            var normalizedRoot = Path.GetFullPath(extractedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                 + Path.DirectorySeparatorChar;
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                var raw = entry.FullName.Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var segments = raw.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Where(segment => segment != ".")
                    .ToArray();
                if (segments.Length == 0) continue;
                if (segments.Any(segment => segment == "..")) return false;
                var destination = Path.GetFullPath(Path.Combine(extractedRoot, Path.Combine(segments)));
                if (!destination.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) return false;
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || string.IsNullOrEmpty(entry.Name))
                {
                    if (!Directory.Exists(destination)) return false;
                }
                else if (!File.Exists(destination))
                    return false;
            }
            return true;
        }
        catch { return false; }
    }

    public static string[] EnumerateTscCharts(string extractedRoot)
    {
        if (!Directory.Exists(extractedRoot)) return Array.Empty<string>();
        return Directory.EnumerateFiles(extractedRoot, "*.tsc", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string[] EnumerateAresCharts(string extractedRoot)
    {
        if (!Directory.Exists(extractedRoot)) return Array.Empty<string>();
        return Directory.EnumerateFiles(extractedRoot, "*.ares", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string? FindTstsSongRootForChart(string chartPath, string extractedRoot)
    {
        try
        {
            if (!IsRecognizedTstsDifficultyChart(chartPath)) return null;
            var fullExtractedRoot = Path.GetFullPath(extractedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = Path.GetFullPath(Path.GetDirectoryName(chartPath) ?? string.Empty);
            for (var depth = 0; depth < 20; depth++)
            {
                if (!IsWithinRoot(current, fullExtractedRoot)) break;
                if (File.Exists(Path.Combine(current, "song.json")) && IsTstsLayoutChart(chartPath, current))
                    return current;
                if (string.Equals(current, fullExtractedRoot, StringComparison.OrdinalIgnoreCase)) break;
                current = Directory.GetParent(current)?.FullName ?? string.Empty;
                if (current.Length == 0) break;
            }
        }
        catch { }
        return null;
    }

    public static ChartZipBundleKind Detect(string extractedRoot)
    {
        // A .tsc chart is an unambiguous signal of a TSC bundle.
        if (EnumerateTscCharts(extractedRoot).Length > 0)
            return ChartZipBundleKind.Tsc;

        foreach (var chartPath in EnumerateAresCharts(extractedRoot))
            if (FindTstsSongRootForChart(chartPath, extractedRoot) is not null)
                return ChartZipBundleKind.Tsts;

        return ChartZipBundleKind.Unknown;
    }

    public static bool IsRecognizedTstsDifficultyChart(string chartPath)
    {
        if (!string.Equals(Path.GetExtension(chartPath), ".ares", StringComparison.OrdinalIgnoreCase))
            return false;
        var stem = Path.GetFileNameWithoutExtension(chartPath).Trim().ToLowerInvariant();
        if (stem.StartsWith("pattern_", StringComparison.OrdinalIgnoreCase))
            stem = stem[8..];
        return stem is "ez" or "easy" or "nm" or "normal" or "hd" or "hard" or
            "ex" or "expert" or "xt" or "extra" or "sc" or "secret";
    }

    private static bool IsTstsLayoutChart(string chartPath, string songRoot)
    {
        try
        {
            var fullRoot = Path.GetFullPath(songRoot);
            var chartDirectory = Path.GetFullPath(Path.GetDirectoryName(chartPath) ?? string.Empty);
            var relativeDirectory = Path.GetRelativePath(fullRoot, chartDirectory);
            var isRootChart = relativeDirectory == ".";
            var isChartsDirectory = relativeDirectory.Equals("charts", StringComparison.OrdinalIgnoreCase);
            if (!isRootChart && !isChartsDirectory) return false;

            return !File.Exists(Path.Combine(fullRoot, "music.opus")) &&
                   ((File.Exists(Path.Combine(fullRoot, "song.opus")) || File.Exists(Path.Combine(fullRoot, "cover.webp"))) || isChartsDirectory);
        }
        catch { return false; }
    }

    private static bool IsWithinRoot(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) return true;
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
