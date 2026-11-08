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

    // Leave room after compaction so the logger does not read/rewrite a full 2 MB
    // file for every subsequent line. This was especially costly during skin load,
    // where every assets.load() emits nested timing entries.
    private const int LogCompactionHeadroomBytes = MaxLogBytes / 4;
    private const int LogRetainedAfterCompactionBytes = MaxLogBytes - LogCompactionHeadroomBytes;
    private static readonly byte[] LogCopyBuffer = new byte[64 * 1024];

    internal static void AppendBoundedLog(string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + Environment.NewLine);
        lock (LogWriteLock)
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Normal writes stay on the fast append path. Read the length from the
            // opened stream, then compact only after the file would exceed its cap.
            // The compacted file is deliberately smaller than the cap, avoiding a
            // full-file rewrite on every log entry after the first rollover.
            long existingLength;
            using (var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                existingLength = stream.Length;
                if (bytes.Length < MaxLogBytes && existingLength <= MaxLogBytes - bytes.Length)
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                    return;
                }
            }

            RewriteRecentLogTail(fullPath, bytes, existingLength);
        }
    }

    /// <summary>
    /// Rewrites the recent log tail using a small reusable buffer. The previous
    /// implementation allocated two multi-megabyte arrays and rewrote the full log
    /// on every line once the 2 MB cap was reached. That logging overhead polluted
    /// the allocation profiler and created real LOH churn during asset loading.
    /// </summary>
    private static void RewriteRecentLogTail(string path, byte[] appendedBytes, long existingLength)
    {
        var combinedLength = existingLength + appendedBytes.LongLength;
        var desiredStart = Math.Max(0L, combinedLength - LogRetainedAfterCompactionBytes);
        var tempPath = path + ".trim.tmp";

        try
        {
            using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (desiredStart < existingLength && existingLength > 0 && File.Exists(path))
                {
                    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var copyStart = FindNextLogLineStart(input, desiredStart, existingLength);
                    if (copyStart < existingLength)
                    {
                        input.Position = copyStart;
                        CopyLogRange(input, output, existingLength - copyStart);
                        output.Write(appendedBytes, 0, appendedBytes.Length);
                    }
                    else if (appendedBytes.Length > 0)
                    {
                        // The retained portion of the old file had no complete
                        // line. Start at the next complete line in the new bytes.
                        WriteAppendedLogTail(output, appendedBytes, 0);
                    }
                    else
                    {
                        // Startup cleanup can encounter an older log consisting
                        // of one very long line. Preserve its tail rather than
                        // replacing it with an empty file.
                        var fallbackStart = FindUtf8SafeLogStart(input, desiredStart, existingLength);
                        input.Position = fallbackStart;
                        CopyLogRange(input, output, existingLength - fallbackStart);
                    }
                }
                else
                {
                    var appendedStart = (int)Math.Clamp(desiredStart - existingLength, 0L, appendedBytes.LongLength);
                    WriteAppendedLogTail(output, appendedBytes, appendedStart);
                }

                output.Flush();
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    private static long FindNextLogLineStart(Stream stream, long start, long end)
    {
        if (start <= 0)
            return 0;

        stream.Position = start - 1;
        if (stream.ReadByte() == (byte)'\n')
            return start;

        stream.Position = start;
        var position = start;
        while (position < end)
        {
            var requested = (int)Math.Min(LogCopyBuffer.Length, end - position);
            var read = stream.Read(LogCopyBuffer, 0, requested);
            if (read <= 0)
                break;

            var newline = Array.IndexOf(LogCopyBuffer, (byte)'\n', 0, read);
            if (newline >= 0)
                return position + newline + 1;

            position += read;
        }

        return end;
    }

    private static long FindUtf8SafeLogStart(Stream input, long start, long end)
    {
        input.Position = start;
        while (start < end)
        {
            var value = input.ReadByte();
            if (value < 0 || (value & 0xC0) != 0x80)
                break;
            start++;
        }
        return start;
    }

    private static void CopyLogRange(Stream input, Stream output, long bytesToCopy)
    {
        while (bytesToCopy > 0)
        {
            var requested = (int)Math.Min(LogCopyBuffer.Length, bytesToCopy);
            var read = input.Read(LogCopyBuffer, 0, requested);
            if (read <= 0)
                break;
            output.Write(LogCopyBuffer, 0, read);
            bytesToCopy -= read;
        }
    }

    private static void WriteAppendedLogTail(Stream output, byte[] bytes, int start)
    {
        start = Math.Clamp(start, 0, bytes.Length);
        if (start >= bytes.Length)
            return;

        if (start == 0 || bytes[start - 1] == (byte)'\n')
        {
            output.Write(bytes, start, bytes.Length - start);
            return;
        }

        var newline = Array.IndexOf(bytes, (byte)'\n', start);
        if (newline >= 0 && newline < bytes.Length - 1)
        {
            var lineStart = newline + 1;
            output.Write(bytes, lineStart, bytes.Length - lineStart);
            return;
        }

        // A single unusually large log entry may exceed the retention window.
        // Preserve its tail while avoiding a UTF-8 continuation-byte start.
        var fallbackStart = Math.Max(0, bytes.Length - MaxLogBytes);
        while (fallbackStart < bytes.Length && (bytes[fallbackStart] & 0xC0) == 0x80)
            fallbackStart++;
        output.Write(bytes, fallbackStart, bytes.Length - fallbackStart);
    }

    /// <summary>Clamps logs from older builds during startup without allocating multi-megabyte arrays.</summary>
    internal static void EnforceExistingLogSizeLimit(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            lock (LogWriteLock)
            {
                if (!File.Exists(fullPath))
                    return;

                var length = new FileInfo(fullPath).Length;
                if (length <= MaxLogBytes)
                    return;

                RewriteRecentLogTail(fullPath, Array.Empty<byte>(), length);
            }
        }
        catch
        {
            // Log maintenance is best-effort and must never prevent startup.
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
