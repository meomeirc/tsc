using System.Diagnostics;

namespace TSC.Updater;

/// <summary>
/// Starts the update mode of TSC.exe from a temporary copy of its apphost and
/// runtime files. This keeps the installed executable free to be replaced.
/// </summary>
internal static class UpdateRunnerLauncher
{
    public static Process StartTemporaryHost(string gameDirectory, string installRoot, string repository, string currentVersion, int waitPid)
    {
        gameDirectory = Path.GetFullPath(gameDirectory);
        installRoot = Path.GetFullPath(installRoot);
        var installedExe = Path.Combine(gameDirectory, "TSC.exe");
        if (!File.Exists(installedExe))
            throw new FileNotFoundException("The game executable was not found under data/.", installedExe);

        var hostRoot = Path.Combine(Path.GetTempPath(), "TSC-update-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(hostRoot);
        try
        {
            CopyHostFiles(gameDirectory, hostRoot);
            var hostExe = Path.Combine(hostRoot, "TSC.exe");
            if (!File.Exists(hostExe) || !File.Exists(Path.Combine(hostRoot, "TSC.dll")))
                throw new InvalidOperationException("The installed game does not contain the required self-contained app files. Reinstall it from a complete release ZIP.");

            var start = new ProcessStartInfo
            {
                FileName = hostExe,
                WorkingDirectory = hostRoot,
                UseShellExecute = false
            };
            start.ArgumentList.Add("--updater");
            start.ArgumentList.Add("--repository"); start.ArgumentList.Add(repository);
            start.ArgumentList.Add("--install-dir"); start.ArgumentList.Add(installRoot);
            start.ArgumentList.Add("--current-version"); start.ArgumentList.Add(currentVersion);
            start.ArgumentList.Add("--wait-pid"); start.ArgumentList.Add(waitPid.ToString(System.Globalization.CultureInfo.InvariantCulture));

            return Process.Start(start) ?? throw new InvalidOperationException("Windows could not start the update process.");
        }
        catch
        {
            try { Directory.Delete(hostRoot, recursive: true); } catch { }
            throw;
        }
    }

    private static void CopyHostFiles(string gameDirectory, string hostRoot)
    {
        // The actual self-contained game lives in data/. Copy its apphost, assemblies,
        // and runtime DLLs to a temporary folder so Windows can replace /data during the update.
        foreach (var source in Directory.EnumerateFiles(gameDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(source);
            var extension = Path.GetExtension(source);
            var isApplicationMetadata = name.Equals("TSC.deps.json", StringComparison.OrdinalIgnoreCase)
                || name.Equals("TSC.runtimeconfig.json", StringComparison.OrdinalIgnoreCase)
                || name.Equals("TSC.exe.config", StringComparison.OrdinalIgnoreCase);
            if (!isApplicationMetadata
                && !extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                continue;

            File.Copy(source, Path.Combine(hostRoot, name), overwrite: true);
        }

        // Some packages place native dependencies under runtimes/<rid>/native.
        var runtimesSource = Path.Combine(gameDirectory, "runtimes");
        if (Directory.Exists(runtimesSource))
            CopyDirectory(runtimesSource, Path.Combine(hostRoot, "runtimes"));
    }

    public static void ScheduleCleanupAfterExit()
    {
        var hostRoot = Path.GetFullPath(AppContext.BaseDirectory);
        var hostName = Path.GetFileName(Path.TrimEndingDirectorySeparator(hostRoot));
        if (!hostName.StartsWith("TSC-update-host-", StringComparison.OrdinalIgnoreCase))
            return;

        var scriptPath = Path.Combine(Path.GetTempPath(), "TSC-update-host-cleanup-" + Guid.NewGuid().ToString("N") + ".ps1");
        var script = "param([int]$ProcessId,[string]$HostRoot,[string]$ScriptPath)\n" +
                     "while (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 250 }\n" +
                     "Remove-Item -LiteralPath $HostRoot -Recurse -Force -ErrorAction SilentlyContinue\n" +
                     "Remove-Item -LiteralPath $ScriptPath -Force -ErrorAction SilentlyContinue\n";
        File.WriteAllText(scriptPath, script, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) powershell = "powershell.exe";
        var start = new ProcessStartInfo { FileName = powershell, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy"); start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File"); start.ArgumentList.Add(scriptPath);
        start.ArgumentList.Add("-ProcessId"); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("-HostRoot"); start.ArgumentList.Add(hostRoot);
        start.ArgumentList.Add("-ScriptPath"); start.ArgumentList.Add(scriptPath);
        _ = Process.Start(start);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
