using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TSC;

/// <summary>
/// Owns TSC-created temporary workspaces and conservatively removes leftovers
/// from interrupted operations. Original songs, exported bundles, and arbitrary
/// files in user folders are deliberately outside its cleanup scope.
/// </summary>
internal static class TscTemporaryStorage
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, FileStream> ActiveWorkspaceLocks = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] ShortLivedDirectoryPrefixes =
    {
        "TSC-TSC-zip-",
        "TSC-songjson-",
        "TSC-FPK-json-",
        "TSC-preview-convert-",
        "TSC-preview-bake-",
        "TSC-artwork-preview-",
        "TSC-SkinArtwork-"
    };

    private static readonly TimeSpan StaleUnmarkedWorkspaceAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan StaleOperationAge = TimeSpan.FromDays(1);

    public static string CreateOwnedDirectory(string prefix)
    {
        if (!IsTstsWorkspacePrefix(prefix))
            throw new ArgumentException("Unsupported TSC temporary workspace prefix.", nameof(prefix));

        var root = Path.GetFullPath(Path.GetTempPath());
        var path = Path.Combine(root, prefix + Guid.NewGuid().ToString("N"));
        var lockPath = GetWorkspaceLockPath(path);
        Directory.CreateDirectory(path);

        FileStream? stream = null;
        try
        {
            stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            var owner = Encoding.UTF8.GetBytes($"pid={Environment.ProcessId}{Environment.NewLine}");
            stream.Write(owner, 0, owner.Length);
            stream.Flush(flushToDisk: true);
            lock (Sync)
                ActiveWorkspaceLocks.Add(Path.GetFullPath(path), stream);
            stream = null; // The active-workspace registry now owns the handle.
            return path;
        }
        catch
        {
            try { stream?.Dispose(); } catch { }
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
            try { if (File.Exists(lockPath)) File.Delete(lockPath); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Deletes only a folder with one of the exact generated TSTS names directly
    /// under the system temp directory. Also releases its live-session lock.
    /// </summary>
    public static bool DeleteOwnedDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !TryNormalizeManagedWorkspacePath(path, out var fullPath))
            return false;

        FileStream? activeLock = null;
        lock (Sync)
            ActiveWorkspaceLocks.Remove(fullPath, out activeLock);

        var lockPath = GetWorkspaceLockPath(fullPath);
        if (activeLock is null && File.Exists(lockPath))
        {
            // If another running TSC owns this workspace, do not remove it.
            try { activeLock = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch { return false; }
        }

        try
        {
            if (Directory.Exists(fullPath))
                Directory.Delete(fullPath, recursive: true);
        }
        catch
        {
            try { activeLock?.Dispose(); } catch { }
            return false;
        }
        finally
        {
            // The marker is a sibling of the workspace, so keep the exclusive
            // lock until the directory is gone to prevent another process from
            // claiming it during deletion.
            try { activeLock?.Dispose(); } catch { }
        }

        if (Directory.Exists(fullPath))
            return false;

        try { if (File.Exists(GetWorkspaceLockPath(fullPath))) File.Delete(GetWorkspaceLockPath(fullPath)); } catch { }
        return true;
    }

    /// <summary>
    /// Startup cleanup for TSC-owned temporary work. Marked TSTS workspaces are
    /// removed only when their exclusive lock is available; unmarked folders
    /// from older TSC versions use a longer age threshold for safety.
    /// </summary>
    public static void CleanupStaleTemporaryData()
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var now = DateTime.UtcNow;
        var staleOperationCutoff = now - StaleOperationAge;
        var staleUnmarkedCutoff = now - StaleUnmarkedWorkspaceAge;

        try
        {
            if (Directory.Exists(tempRoot))
            {
                foreach (var directory in Directory.EnumerateDirectories(tempRoot))
                {
                    try
                    {
                        var name = Path.GetFileName(directory);
                        if (TryGetTstsWorkspacePrefix(name, out _))
                        {
                            CleanupTstsWorkspace(directory, staleUnmarkedCutoff);
                            continue;
                        }

                        if (MatchesAnyGeneratedName(name, ShortLivedDirectoryPrefixes) &&
                            Directory.GetCreationTimeUtc(directory) < staleOperationCutoff)
                        {
                            try { Directory.Delete(directory, recursive: true); } catch { }
                        }
                    }
                    catch { }
                }

                CleanupOrphanWorkspaceLocks(tempRoot, now - TimeSpan.FromHours(1));
                CleanupTemporaryFiles(tempRoot, staleOperationCutoff);
            }
        }
        catch { }

        CleanupKnownAtomicWriteTemps(staleOperationCutoff);
        CleanupFfmpegExtractionTemps(staleOperationCutoff);
    }

    private static void CleanupTstsWorkspace(string directory, DateTime unmarkedCutoff)
    {
        var lockPath = GetWorkspaceLockPath(directory);
        if (!File.Exists(lockPath))
        {
            // Legacy workspaces did not have locks. Avoid touching recent ones,
            // since another process running an older build may still own them.
            try
            {
                if (Directory.GetCreationTimeUtc(directory) < unmarkedCutoff)
                    Directory.Delete(directory, recursive: true);
            }
            catch { }
            return;
        }

        FileStream? staleLock = null;
        try
        {
            // A live TSC process holds this marker with FileShare.None.
            staleLock = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        catch { return; }

        try
        {
            // Keep the lock held while removing the directory so another process
            // cannot claim the workspace between the ownership check and delete.
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch { }
        finally
        {
            try { staleLock.Dispose(); } catch { }
        }

        try
        {
            if (!Directory.Exists(directory) && File.Exists(lockPath))
                File.Delete(lockPath);
        }
        catch { }
    }

    private static void CleanupOrphanWorkspaceLocks(string tempRoot, DateTime cutoff)
    {
        try
        {
            foreach (var lockPath in Directory.EnumerateFiles(tempRoot, "*.tsc-lock", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var workspacePath = lockPath[..^".tsc-lock".Length];
                    if (Directory.Exists(workspacePath) || !TryNormalizeManagedWorkspacePath(workspacePath, out _))
                        continue;
                    if (File.GetLastWriteTimeUtc(lockPath) >= cutoff)
                        continue;

                    using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        if (Directory.Exists(workspacePath))
                            continue;
                    }
                    if (!Directory.Exists(workspacePath))
                        File.Delete(lockPath);
                }
                catch { }
            }
        }
        catch { }
    }

    private static void CleanupTemporaryFiles(string tempRoot, DateTime cutoff)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(tempRoot, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var name = Path.GetFileName(file);
                    if (!IsRecognizedTemporaryFileName(name) || File.GetLastWriteTimeUtc(file) >= cutoff)
                        continue;
                    File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }

    private static bool IsRecognizedTemporaryFileName(string name)
    {
        return MatchesGeneratedFileName(name, "tsc-ffmpeg-", ".7z") ||
               MatchesGeneratedFileName(name, "tsc_cover_", ".png") ||
               MatchesGeneratedFileName(name, "tsc_import_", ".opus");
    }

    private static void CleanupKnownAtomicWriteTemps(DateTime cutoff)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            UserDataPaths.ScoresPath + ".tmp",
            UserDataPaths.CrashLogPath + ".trim.tmp",
            UserDataPaths.GetPath("Logs", "song_select_debug.log.trim.tmp"),
            UserDataPaths.GetPath("Logs", "superFlick_debug.log.trim.tmp"),
            Path.Combine(UserDataPaths.RootDirectory, "Logs", "tsc_crash.log.trim.tmp"),
            Path.Combine(UserDataPaths.RootDirectory, "Logs", "song_select_debug.log.trim.tmp"),
            Path.Combine(UserDataPaths.RootDirectory, "Logs", "superFlick_debug.log.trim.tmp"),
            Path.Combine(UserDataPaths.RootDirectory, "Logs", "ffmpeg-download.log.trim.tmp")
        };

        var stateRoot = Path.Combine(UserDataPaths.RootDirectory, "State");
        try
        {
            if (Directory.Exists(stateRoot))
            {
                foreach (var file in Directory.EnumerateFiles(stateRoot, "last-selected-song.json.tmp", SearchOption.AllDirectories))
                    known.Add(file);
                foreach (var file in Directory.EnumerateFiles(stateRoot, "songlist.json.tmp", SearchOption.AllDirectories))
                    known.Add(file);
            }
        }
        catch { }

        foreach (var file in known)
            TryDeleteOldFile(file, cutoff);
    }

    private static void CleanupFfmpegExtractionTemps(DateTime cutoff)
    {
        try
        {
            var toolsDirectory = UserDataPaths.GetPath("tools");
            if (!Directory.Exists(toolsDirectory))
                return;

            foreach (var file in Directory.EnumerateFiles(toolsDirectory, "ffmpeg-*.tmp", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (MatchesGeneratedFileName(Path.GetFileName(file), "ffmpeg-", ".tmp"))
                        TryDeleteOldFile(file, cutoff);
                }
                catch { }
            }
        }
        catch { }
    }

    private static void TryDeleteOldFile(string path, DateTime cutoff)
    {
        try
        {
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) < cutoff)
                File.Delete(path);
        }
        catch { }
    }

    private static bool IsTstsWorkspacePrefix(string prefix)
        => string.Equals(prefix, "TSC-TSTS-", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(prefix, "TSC-TSTS-save-", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetTstsWorkspacePrefix(string name, out string prefix)
    {
        // The save prefix must be tested first because it begins with TSC-TSTS-.
        if (MatchesGeneratedName(name, "TSC-TSTS-save-"))
        {
            prefix = "TSC-TSTS-save-";
            return true;
        }
        if (MatchesGeneratedName(name, "TSC-TSTS-"))
        {
            prefix = "TSC-TSTS-";
            return true;
        }
        prefix = string.Empty;
        return false;
    }

    private static bool MatchesAnyGeneratedName(string name, IEnumerable<string> prefixes)
        => prefixes.Any(prefix => MatchesGeneratedName(name, prefix));

    private static bool MatchesGeneratedName(string name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var suffix = name[prefix.Length..];
        return suffix.Length == 32 && suffix.All(Uri.IsHexDigit);
    }

    private static bool MatchesGeneratedFileName(string name, string prefix, string extension)
    {
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            return false;
        var suffix = name[prefix.Length..^extension.Length];
        return suffix.Length == 32 && suffix.All(Uri.IsHexDigit);
    }

    private static bool TryNormalizeManagedWorkspacePath(string path, out string fullPath)
    {
        fullPath = string.Empty;
        try
        {
            fullPath = Path.GetFullPath(path);
            var tempRoot = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Path.GetDirectoryName(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(parent, tempRoot, StringComparison.OrdinalIgnoreCase) &&
                   TryGetTstsWorkspacePrefix(Path.GetFileName(fullPath), out _);
        }
        catch { return false; }
    }

    private static string GetWorkspaceLockPath(string path) => path + ".tsc-lock";

    /// <summary>Builds the same cache identity hash used by TSTS and ZIP extraction.</summary>
    public static string BundleCacheHash(string bundlePath)
    {
        var fullPath = Path.GetFullPath(bundlePath);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            throw new FileNotFoundException("Bundle file was not found while preparing its cache identity.", fullPath);
        var identity = fullPath.ToUpperInvariant() + "|" +
                       info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" +
                       info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    public static void CleanupObsoleteBundleCacheRoot(string cacheRoot, ISet<string> expectedHashes, DateTime cutoffUtc)
    {
        try
        {
            if (!Directory.Exists(cacheRoot))
                return;

            foreach (var directory in Directory.EnumerateDirectories(cacheRoot, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var name = Path.GetFileName(directory);
                    if (!IsSha256Hash(name) || expectedHashes.Contains(name) ||
                        Directory.GetLastWriteTimeUtc(directory) >= cutoffUtc)
                        continue;
                    Directory.Delete(directory, recursive: true);
                }
                catch { }
            }

            // ZIP cache source markers live beside the extracted folder. TSTS
            // markers live inside their folder and are deleted with it.
            foreach (var marker in Directory.EnumerateFiles(cacheRoot, "*.source-key", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var hash = Path.GetFileName(marker)[..^".source-key".Length];
                    if (IsSha256Hash(hash) && !expectedHashes.Contains(hash) && File.GetLastWriteTimeUtc(marker) < cutoffUtc)
                        File.Delete(marker);
                }
                catch { }
            }
        }
        catch { }
    }

    private static bool IsSha256Hash(string value)
        => value.Length == 64 && value.All(ch => (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'));
}
