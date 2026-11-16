using System.Text;

namespace TSC;

/// <summary>Small Android entry-point support; the Windows launcher remains separate.</summary>
public static class Program
{
    private static readonly object LogWriteLock = new();
    private const long MaxLogBytes = 2 * 1024 * 1024;

    internal static void InstallGlobalExceptionLogging()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Game1.TryCaptureEditorRecoveryForUnhandledException();
            var details = args.ExceptionObject is Exception exception
                ? exception.ToString()
                : Convert.ToString(args.ExceptionObject) ?? "<unknown>";
            LogFatalText("UNHANDLED_EXCEPTION", details);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogFatal("UNOBSERVED_TASK_EXCEPTION", args.Exception);
            args.SetObserved();
        };
    }

    internal static void LogRuntime(string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            Console.Error.WriteLine(line);
            AppendBoundedLog(UserDataPaths.CrashLogPath, line);
        }
        catch
        {
            // Logging must not prevent the game from opening.
        }
    }

    internal static void LogFatal(string source, Exception exception)
        => LogFatalText(source, exception?.ToString() ?? "<null exception>");

    private static void LogFatalText(string source, string details)
        => LogRuntime($"FATAL source={source}{Environment.NewLine}{details}");

    internal static void AppendBoundedLog(string path, string text)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text + Environment.NewLine);
            lock (LogWriteLock)
            {
                var fullPath = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                using (var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    if (stream.Length + bytes.Length <= MaxLogBytes)
                    {
                        stream.Write(bytes);
                        return;
                    }
                }

                var existing = File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : Array.Empty<byte>();
                var keep = Math.Min(existing.Length, (int)(MaxLogBytes * 3 / 4));
                var tail = new byte[keep + bytes.Length];
                if (keep > 0)
                    Buffer.BlockCopy(existing, existing.Length - keep, tail, 0, keep);
                Buffer.BlockCopy(bytes, 0, tail, keep, bytes.Length);
                File.WriteAllBytes(fullPath, tail);
            }
        }
        catch
        {
            // Crash logging is best-effort.
        }
    }
}
