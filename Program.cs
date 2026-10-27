using System.Text;
namespace TSC;

public static class Program
{
    private static readonly object CrashLogLock = new();

    [STAThread]
    public static void Main(string[] args)
    {
#if !TSC_PUBLISH
        SkinDebugConsole.Initialize();
#endif
        InstallGlobalExceptionLogging();
        UserDataPaths.Initialize();
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
            Console.Error.WriteLine("[TSC] FATAL EXCEPTION — see tsc_crash.log in the game folder");
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

    internal const int MaxLogBytes = 2 * 1024 * 1024;

    internal static void AppendBoundedLog(string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + Environment.NewLine);
        lock (CrashLogLock)
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using (var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
                if (stream.Length <= MaxLogBytes)
                    return;
            }

            TrimLogToLimit(fullPath);
        }
    }

    private static void TrimLogToLimit(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length <= MaxLogBytes)
                return;

            var start = bytes.Length - MaxLogBytes;
            // Start at the next complete line so truncation removes old entries,
            // not the beginning of a UTF-8 character or the middle of a line.
            while (start < bytes.Length && bytes[start] != (byte)'\n')
                start++;
            if (start < bytes.Length)
                start++;
            if (start >= bytes.Length)
                start = bytes.Length - MaxLogBytes;

            var length = Math.Min(MaxLogBytes, bytes.Length - start);
            var temp = path + ".trim.tmp";
            File.WriteAllBytes(temp, bytes.AsSpan(start, length).ToArray());
            File.Move(temp, path, true);
        }
        catch
        {
            // Best effort only; logging must never crash the game.
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
