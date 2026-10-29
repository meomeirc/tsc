using System.Text;
namespace TSC;

public static class Program
{
    private static readonly object LogWriteLock = new();

    [STAThread]
    public static void Main(string[] args)
    {
        // The updater is a mode of the self-contained game executable in data/. It runs
        // from a temporary copy so Windows can replace game files and the root launcher.
        if (args.Length > 0 && string.Equals(args[0], "--updater", StringComparison.OrdinalIgnoreCase))
        {
            TSC.Updater.UpdaterEntry.Run(args.Skip(1).ToArray());
            return;
        }

#if !TSC_PUBLISH
        SkinDebugConsole.Initialize();
#endif
        InstallGlobalExceptionLogging();
        UserDataPaths.Initialize();
        try { TscTemporaryStorage.CleanupStaleTemporaryData(); } catch { /* Cleanup is best-effort and must not block startup. */ }
        EnforceExistingLogSizeLimit(UserDataPaths.CrashLogPath);
        EnforceExistingLogSizeLimit(UserDataPaths.GetPath("Logs", "song_select_debug.log"));
        EnforceExistingLogSizeLimit(UserDataPaths.GetPath("Logs", "superFlick_debug.log"));
        EnforceExistingLogSizeLimit(Path.Combine(UserDataPaths.RootDirectory, "Logs", "tsc_crash.log"));
        EnforceExistingLogSizeLimit(Path.Combine(UserDataPaths.RootDirectory, "Logs", "song_select_debug.log"));
        EnforceExistingLogSizeLimit(Path.Combine(UserDataPaths.RootDirectory, "Logs", "superFlick_debug.log"));
        EnforceExistingLogSizeLimit(Path.Combine(UserDataPaths.RootDirectory, "Logs", "ffmpeg-download.log"));
        LogRuntime("START");

        try
        {
            using var game = new Game1(args.FirstOrDefault());
            LogRuntime("GAME1_CREATED");
            game.Run();
            LogRuntime("GAME_RUN_RETURNED");
        }
        catch (Exception ex)
        {
            LogFatal("MAIN_CATCH", ex);
            Console.Error.WriteLine("[TSC] FATAL EXCEPTION — see tsc_crash.log in the install folder");
            Console.Error.WriteLine(ex);
            throw;
        }
    }

    private static void InstallGlobalExceptionLogging()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                LogFatal("UNHANDLED_EXCEPTION", ex);
            else
                LogFatalText("UNHANDLED_EXCEPTION", Convert.ToString(e.ExceptionObject) ?? "<unknown>");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogFatal("UNOBSERVED_TASK_EXCEPTION", e.Exception);
            e.SetObserved();
        };
    }

    internal static void LogRuntime(string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            Console.Error.WriteLine(line);
            AppendCrashLog(line);
        }
        catch
        {
            // Logging must never be allowed to crash the game.
        }
    }

    internal static void LogFatal(string source, Exception ex)
    {
        LogFatalText(source, ex.ToString());
    }

    private static void LogFatalText(string source, string details)
    {
        try
        {
            var text =
                $"============================================================{Environment.NewLine}" +
                $"TSC FATAL ERROR{Environment.NewLine}" +
                $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}{Environment.NewLine}" +
                $"Source: {source}{Environment.NewLine}" +
                $"Process: {Environment.ProcessId}{Environment.NewLine}" +
                $"Base directory: {AppContext.BaseDirectory}{Environment.NewLine}" +
                $"Current directory: {Environment.CurrentDirectory}{Environment.NewLine}" +
                $"OS: {Environment.OSVersion}{Environment.NewLine}" +
                $"Runtime: {Environment.Version}{Environment.NewLine}" +
                $"{details}{Environment.NewLine}" +
                $"============================================================{Environment.NewLine}";

            Console.Error.WriteLine(text);
            AppendCrashLog(text);
        }
        catch
        {
            // Last-resort logging must never throw.
        }
    }

    // All file-based TSC logs share one hard limit, including crash and debug logs.
    internal const int MaxLogBytes = 2 * 1024 * 1024;

    internal static void AppendBoundedLog(string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + Environment.NewLine);
        lock (LogWriteLock)
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Normal writes stay on the fast append path. Check the actual file
            // length after opening it so the limit decision is not based on a
            // stale FileInfo value from before another process finished a write.
            if (bytes.Length < MaxLogBytes)
            {
                try
                {
                    using (var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                    {
                        if (stream.Length <= MaxLogBytes - bytes.Length)
                        {
                            stream.Write(bytes, 0, bytes.Length);
                            stream.Flush();
                            return;
                        }
                    }
                }
                catch
                {
                    // Fall through to the bounded path, which will skip safely if
                    // the current log cannot be read or replaced.
                }
            }

            byte[] combined;
            if (bytes.Length >= MaxLogBytes)
            {
                combined = KeepRecentLogBytes(bytes, MaxLogBytes);
            }
            else
            {
                if (!TryReadLogTail(fullPath, MaxLogBytes, out var existing))
                    return; // Preserve the existing log if it cannot be read safely.
                combined = new byte[existing.Length + bytes.Length];
                Buffer.BlockCopy(existing, 0, combined, 0, existing.Length);
                Buffer.BlockCopy(bytes, 0, combined, existing.Length, bytes.Length);
                combined = KeepRecentLogBytes(combined, MaxLogBytes);
            }

            WriteLogReplacement(fullPath, combined);
        }
    }

    /// <summary>Clamps logs from older builds during startup, even if not written this session.</summary>
    internal static void EnforceExistingLogSizeLimit(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            lock (LogWriteLock)
            {
                if (!File.Exists(fullPath) || new FileInfo(fullPath).Length <= MaxLogBytes)
                    return;

                if (!TryReadLogTail(fullPath, MaxLogBytes + 64 * 1024, out var tail))
                    return;
                WriteLogReplacement(fullPath, KeepRecentLogBytes(tail, MaxLogBytes));
            }
        }
        catch
        {
            // Log maintenance must never prevent game startup.
        }
    }

    private static bool TryReadLogTail(string path, int maxBytes, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        try
        {
            if (!File.Exists(path))
                return true;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = (int)Math.Min(Math.Max(0, stream.Length), maxBytes);
            if (length == 0)
                return true;

            stream.Seek(-length, SeekOrigin.End);
            bytes = new byte[length];
            var read = 0;
            while (read < length)
            {
                var count = stream.Read(bytes, read, length - read);
                if (count <= 0)
                    break;
                read += count;
            }
            if (read != length)
                Array.Resize(ref bytes, read);
            return true;
        }
        catch
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }

    private static byte[] KeepRecentLogBytes(byte[] bytes, int maxBytes)
    {
        if (bytes.Length <= maxBytes)
            return bytes;

        var start = bytes.Length - maxBytes;
        // Prefer a complete line boundary so retained logs start at a useful
        // entry rather than halfway through an older diagnostic.
        var lineStart = start;
        while (lineStart < bytes.Length && bytes[lineStart] != (byte)'\n')
            lineStart++;
        if (lineStart < bytes.Length)
            start = lineStart + 1;

        // Do not begin on a UTF-8 continuation byte.
        while (start < bytes.Length && (bytes[start] & 0xC0) == 0x80)
            start++;
        if (start >= bytes.Length)
        {
            start = bytes.Length - maxBytes;
            while (start < bytes.Length && (bytes[start] & 0xC0) == 0x80)
                start++;
        }

        return bytes.AsSpan(start).ToArray();
    }

    private static void WriteLogReplacement(string path, byte[] bytes)
    {
        var tempPath = path + ".trim.tmp";
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    private static void AppendCrashLog(string text)
    {
        try
        {
            AppendBoundedLog(UserDataPaths.CrashLogPath, text);
        }
        catch
        {
            try
            {
                AppendBoundedLog(UserDataPaths.CrashLogPath, text);
            }
            catch
            {
                // Nothing else to do.
            }
        }
    }
}
