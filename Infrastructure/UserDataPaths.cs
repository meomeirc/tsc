using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TSC;

/// <summary>
/// In published installs, user data stays beside the root launcher. Developer builds keep
/// their historic per-user application-data location for scores and temporary state.
/// </summary>
public static class UserDataPaths
{
    private static readonly object Sync = new();
    public static string RootDirectory
    {
        get
        {
#if TSC_PUBLISH
            return GameRootDirectory;
#else
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(local))
                return Path.Combine(local, "TSC");

            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(roaming))
                return Path.Combine(roaming, "TSC");

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return !string.IsNullOrWhiteSpace(profile)
                ? Path.Combine(profile, ".local", "share", "TSC")
                : Path.Combine(AppContext.BaseDirectory, "TSC-UserData");
#endif
        }
    }

    /// <summary>Folder containing the installed launcher, skins, songs, and user files.</summary>
    public static string GameRootDirectory
    {
        get
        {
            var configuredRoot = Environment.GetEnvironmentVariable("TSC_INSTALL_ROOT");
            if (!string.IsNullOrWhiteSpace(configuredRoot) && Path.IsPathRooted(configuredRoot))
                return Path.GetFullPath(configuredRoot);

#if TSC_PUBLISH
            // Directly launching data/TSC.exe is also supported: infer the install root
            // from the standard data/ layout when the tiny launcher wasn't used.
            var appDirectory = Path.GetFullPath(AppContext.BaseDirectory);
            var appFolderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(appDirectory));
            if (appFolderName.Equals("data", StringComparison.OrdinalIgnoreCase))
                return Directory.GetParent(Path.TrimEndingDirectorySeparator(appDirectory))?.FullName ?? appDirectory;
            return appDirectory;
#else
            var cwd = Environment.CurrentDirectory;
            if (File.Exists(Path.Combine(cwd, "gameConfig.json")) || Directory.Exists(Path.Combine(cwd, "skins")))
                return Path.GetFullPath(cwd);

            var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
            if (File.Exists(Path.Combine(baseDirectory, "gameConfig.json")) || Directory.Exists(Path.Combine(baseDirectory, "skins")))
                return baseDirectory;

            return Path.GetFullPath(cwd);
#endif
        }
    }

    /// <summary>The folder containing the game's executable and internal runtime/content files.</summary>
    public static string ApplicationDirectory => Path.GetFullPath(AppContext.BaseDirectory);

    public static string GameConfigPath => Path.Combine(GameRootDirectory, "gameConfig.json");
    public static string KeybindsPath => Path.Combine(GameRootDirectory, "keybinds.json");
    public static string CrashLogPath => Path.Combine(GameRootDirectory, "tsc_crash.log");
    public static string ScoresPath => Path.Combine(RootDirectory, "save", "scores.json");

    /// <summary>Resolve writable user files relative to the install root.</summary>
    public static string GetPath(params string[] segments)
    {
        var path = GameRootDirectory;
        foreach (var segment in segments)
            path = Path.Combine(path, segment);
        return path;
    }

    /// <summary>Creates user data folders and migrates legacy files, keeping backups.</summary>
    public static void Initialize(string? legacyRoot = null)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(RootDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(ScoresPath)!);

#if TSC_PUBLISH
            // Older installs kept settings, scores, logs, and song-list state in LocalAppData.
            // Copy those files beside the launcher without deleting the original copies.
            MigrateLegacyAppDataToInstallRoot();
#else
            // TSC-268 through TSC-270 stored editable files and logs in app data. Bring
            // newer app-data copies back to the project/game folder in developer runs.
            MigrateUserFileToGameRootIfNewer("gameConfig.json");
            MigrateUserFileToGameRootIfNewer("keybinds.json");
            MigrateUserFileToGameRootIfNewer(Path.Combine("Logs", "tsc_crash.log"), Path.GetFileName(CrashLogPath));
            MigrateUserFileToGameRootIfNewer(Path.Combine("Logs", "song_select_debug.log"), Path.Combine("Logs", "song_select_debug.log"));
            MigrateUserFileToGameRootIfNewer(Path.Combine("Logs", "superFlick_debug.log"), Path.Combine("Logs", "superFlick_debug.log"));
#endif

            var roots = new List<string>();
            if (!string.IsNullOrWhiteSpace(legacyRoot)) roots.Add(legacyRoot);
#if TSC_PUBLISH
            // A published install may be launched with an unrelated working directory.
            roots.Add(AppContext.BaseDirectory);
            roots.Add(Environment.CurrentDirectory);
#else
            // Developer runs normally launch with the project root as their working directory.
            roots.Add(Environment.CurrentDirectory);
            roots.Add(AppContext.BaseDirectory);
#endif
            roots = roots
                .Where(Directory.Exists)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var root in roots)
            {
                MigrateIfMissing(Path.Combine(root, "save", "scores.json"), ScoresPath);
            }

        }
    }

#if TSC_PUBLISH
    private static string LegacyAppDataDirectory
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(local)) return Path.Combine(local, "TSC");
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return !string.IsNullOrWhiteSpace(roaming) ? Path.Combine(roaming, "TSC") : string.Empty;
        }
    }

    private static void MigrateLegacyAppDataToInstallRoot()
    {
        var legacyRoot = LegacyAppDataDirectory;
        if (string.IsNullOrWhiteSpace(legacyRoot) || !Directory.Exists(legacyRoot)) return;

        foreach (var relative in new[]
        {
            "gameConfig.json", "keybinds.json", "save/scores.json",
            "Logs/tsc_crash.log", "Logs/song_select_debug.log",
            "Logs/superFlick_debug.log", "Logs/ffmpeg-download.log"
        })
            MigrateLegacyFileIfNewer(Path.Combine(legacyRoot, relative), Path.Combine(GameRootDirectory, relative));

        var legacyState = Path.Combine(legacyRoot, "State");
        if (!Directory.Exists(legacyState)) return;
        foreach (var source in Directory.EnumerateFiles(legacyState, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(legacyState, source);
            MigrateLegacyFileIfNewer(source, Path.Combine(GameRootDirectory, "State", relative));
        }
    }

    private static void MigrateLegacyFileIfNewer(string source, string destination)
    {
        try
        {
            if (!File.Exists(source)) return;
            if (File.Exists(destination) && File.GetLastWriteTimeUtc(destination) >= File.GetLastWriteTimeUtc(source)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: File.Exists(destination));
        }
        catch (Exception ex)
        {
            Program.LogRuntime($"LEGACY_USER_DATA_MIGRATION_FAILED source={source} destination={destination} error={ex.Message}");
        }
    }
#endif

    private static void MigrateUserFileToGameRootIfNewer(string fileName)
        => MigrateUserFileToGameRootIfNewer(fileName, fileName);

    private static void MigrateUserFileToGameRootIfNewer(string sourceRelativePath, string destinationFileName)
    {
        try
        {
            var source = Path.Combine(RootDirectory, sourceRelativePath);
            var destination = Path.Combine(GameRootDirectory, destinationFileName);
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(source))
                return;

            if (File.Exists(destination) && File.GetLastWriteTimeUtc(destination) >= File.GetLastWriteTimeUtc(source))
                return;

            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.Copy(source, destination, overwrite: File.Exists(destination));
        }
        catch (Exception ex)
        {
            Program.LogRuntime($"USER_FILE_MIGRATION_FAILED source={sourceRelativePath} destination={destinationFileName} error={ex.Message}");
        }
    }

    private static void MigrateIfMissing(string legacyPath, string destinationPath)
    {
        try
        {
            if (string.Equals(Path.GetFullPath(legacyPath), Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(legacyPath) || File.Exists(destinationPath))
                return;

            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
                Directory.CreateDirectory(destinationDirectory);
            File.Copy(legacyPath, destinationPath, overwrite: false);
        }
        catch (Exception ex)
        {
            Program.LogRuntime($"USER_DATA_MIGRATION_FAILED source={legacyPath} destination={destinationPath} error={ex.Message}");
        }
    }
}
