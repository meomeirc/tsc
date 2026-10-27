using System.Runtime.InteropServices;

namespace TSC;

/// <summary>
/// Creates a dedicated Windows console for runtime/debug output.
/// This is especially useful when the game is launched from an IDE or when
/// the caller's console is not convenient to inspect.
/// </summary>
internal static class SkinDebugConsole
{
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetConsoleTitle(string lpConsoleTitle);

    public static void Initialize()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            // Always use a dedicated window. This makes Skin Lua output visible
            // even when the game was started with `dotnet run`, from an IDE,
            // or by double-clicking the executable.
            FreeConsole();
            if (!AllocConsole())
                return;

            SetConsoleTitle("TSC - Skin Debug Console");

            var stdout = Console.OpenStandardOutput();
            var stderr = Console.OpenStandardError();
            Console.SetOut(new StreamWriter(stdout) { AutoFlush = true });
            Console.SetError(new StreamWriter(stderr) { AutoFlush = true });

            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Error.WriteLine("============================================================");
            Console.Error.WriteLine(" TSC - Skin Debug Console");
            Console.Error.WriteLine(" Lua output, skin errors, and runtime diagnostics appear here.");
            Console.Error.WriteLine("============================================================");
        }
        catch
        {
            // Debug logging must never prevent the game itself from starting.
        }
    }
}
