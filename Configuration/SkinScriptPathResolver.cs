using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TSC;

/// <summary>
/// Resolves skin folders and standalone Lua scripts while supporting both the
/// current Skins/<name>/scripts layout and older root-level script layouts.
/// </summary>
internal static class SkinScriptPathResolver
{
    public static string SkinsDirectory(string rootDirectory)
        => ExistingChildDirectory(rootDirectory, "Skins");

    public static string DefaultSkinDirectory(string rootDirectory)
        => Path.Combine(SkinsDirectory(rootDirectory), "Default");

    public static string Preferred(string skinDirectory, string fileName)
    {
        var scriptsPath = Path.Combine(skinDirectory, "scripts", fileName);
        var legacyPath = Path.Combine(skinDirectory, fileName);

        // Prefer the new layout whenever it exists. If neither exists, return the
        // new path so first-run config creation and diagnostics use that layout.
        return File.Exists(scriptsPath) || !File.Exists(legacyPath)
            ? scriptsPath
            : legacyPath;
    }

    public static bool HasDefaultSkinEntryPoint(string rootDirectory)
    {
        try
        {
            var defaultDirectory = DefaultSkinDirectory(rootDirectory);
            return File.Exists(Preferred(defaultDirectory, "skin.lua"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Finds the developer content root. Prefer a root that actually contains the
    /// Default skin entry point over a working directory that merely contains a
    /// gameConfig.json or an empty Skins folder.
    /// </summary>
    public static string FindDeveloperRootDirectory(string currentDirectory, string applicationDirectory)
    {
        var candidates = new List<string>();
        AddUniqueRoot(candidates, currentDirectory);
        AddUniqueRoot(candidates, applicationDirectory);

        foreach (var candidate in candidates)
        {
            if (HasDefaultSkinEntryPoint(candidate))
                return candidate;
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "gameConfig.json")) ||
                Directory.Exists(SkinsDirectory(candidate)))
                return candidate;
        }

        return candidates.Count > 0 ? candidates[0] : Path.GetFullPath(applicationDirectory);
    }

    private static string ExistingChildDirectory(string parentDirectory, string childName)
    {
        var expectedPath = Path.Combine(parentDirectory, childName);
        if (Directory.Exists(expectedPath))
            return expectedPath;

        // Keep the actual on-disk casing on case-sensitive filesystems. Windows
        // accepts either spelling, but the project ships the folder as "Skins".
        if (Directory.Exists(parentDirectory))
        {
            try
            {
                var existing = Directory.EnumerateDirectories(parentDirectory)
                    .FirstOrDefault(path => string.Equals(
                        Path.GetFileName(path), childName, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                    return existing;
            }
            catch
            {
                // Return the expected path; the caller will surface any real IO error.
            }
        }

        return expectedPath;
    }

    private static void AddUniqueRoot(List<string> roots, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var fullPath = Path.GetFullPath(path);
        if (!roots.Any(existing => string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase)))
            roots.Add(fullPath);
    }
}
