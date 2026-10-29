using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var installRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var gamePath = Path.Combine(installRoot, "data", "TSC.exe");

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = gamePath,
                WorkingDirectory = installRoot,
                UseShellExecute = false
            };
            start.Environment["TSC_INSTALL_ROOT"] = installRoot;
            foreach (var arg in args)
                start.ArgumentList.Add(arg);

            _ = Process.Start(start);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBoxW(IntPtr.Zero,
                "Could not start the TSC game. Make sure the data folder is present.\n\n" + ex.Message,
                "TSC", 0x10);
            return 1;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);
}
