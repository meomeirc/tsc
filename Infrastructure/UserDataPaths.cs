using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TSC;

/// <summary>
/// Scores use per-user application data; settings and logs stay beside the game.
/// </summary>
public static class UserDataPaths
{
    private static readonly object Sync = new();
    private static string? _rootDirectory;

    public static string RootDirectory
    {
        get
        {
            if (_rootDirectory is not null)
                return _rootDirectory;

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(local))
                _rootDirectory = Path.Combine(local, "TSC");
            else
            {
                var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrWhiteSpace(roaming))
                    _rootDirectory = Path.Combine(roaming, "TSC");
                else
                {
                    var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    _rootDirectory = !string.IsNullOrWhiteSpace(profile)
                        ? Path.Combine(profile, ".local", "share", "TSC")
                        : Path.Combine(AppContext.BaseDirectory, "TSC-UserData");
                }
            }

            return _rootDirectory;
        }
    }

    public static string GameRootDirectory
    {
        get
        {
#if TSC_PUBLISH
            return Path.GetFullPath(AppContext.BaseDirectory);
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

    public static string GameConfigPath => Path.Combine(GameRootDirectory, "gameConfig.json");
    public static string KeybindsPath => Path.Combine(GameRootDirectory, "keybinds.json");
    public static string CrashLogPath => Path.Combine(GameRootDirectory, "tsc_crash.log");
    public static string ScoresPath => Path.Combine(RootDirectory, "save", "scores.json");

    /// <summary>Resolve non-score writable files relative to the game folder.</summary>
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

            // TSC-268 through TSC-270 stored editable files and logs in app data. Bring
            // newer app-data copies back to the game folder so existing user changes and
            // diagnostic history survive this location change; leave old copies intact.
            MigrateUserFileToGameRootIfNewer("gameConfig.json");
            MigrateUserFileToGameRootIfNewer("keybinds.json");
            MigrateUserFileToGameRootIfNewer(Path.Combine("Logs", "tsc_crash.log"), Path.GetFileName(CrashLogPath));
            MigrateUserFileToGameRootIfNewer(Path.Combine("Logs", "song_select_debug.log"), Path.Combine("Logs", "song_select_debug.log"));
            MigrateUserFileToGameRootIfNewer(Path.Combine("Logs", "superFlick_debug.log"), Path.Combine("Logs", "superFlick_debug.log"));

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
