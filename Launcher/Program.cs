using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var installRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var requestedApi = ReadGraphicsApi(installRoot);
        var gamePath = Path.Combine(installRoot, "data", requestedApi, "TSC.exe");

        // Older single-backend installs used data/TSC.exe. Keep them launchable until
        // the user installs a multi-backend package.
        if (!File.Exists(gamePath))
        {
            var legacyGamePath = Path.Combine(installRoot, "data", "TSC.exe");
            var backendDirectoriesExist = Directory.Exists(Path.Combine(installRoot, "data", "DesktopGL")) ||
                                          Directory.Exists(Path.Combine(installRoot, "data", "DesktopVK")) ||
                                          Directory.Exists(Path.Combine(installRoot, "data", "WindowsDX12"));
            if (!backendDirectoriesExist && File.Exists(legacyGamePath))
                gamePath = legacyGamePath;
            else
            {
                MessageBoxW(IntPtr.Zero,
                    $"The selected graphics backend ({DisplayName(requestedApi)}) is not installed. Reinstall TSC using the full package to restore all graphics backends.",
                    "TSC", 0x10);
                return 1;
            }
        }

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = gamePath,
                WorkingDirectory = installRoot,
                UseShellExecute = false
            };
            start.Environment["TSC_INSTALL_ROOT"] = installRoot;
            var bundledRuntime = Path.Combine(installRoot, "runtime");
            if (Directory.Exists(Path.Combine(bundledRuntime, "host", "fxr")))
            {
                start.Environment["DOTNET_ROOT"] = bundledRuntime;
                start.Environment["DOTNET_ROOT_X64"] = bundledRuntime;
            }
            foreach (var arg in args)
                start.ArgumentList.Add(arg);

            _ = Process.Start(start);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBoxW(IntPtr.Zero,
                "Could not start the TSC game. Make sure the selected backend and shared runtime are present.\n\n" + ex.Message,
                "TSC", 0x10);
            return 1;
        }
    }

    private static string ReadGraphicsApi(string installRoot)
    {
        var configPath = Path.Combine(installRoot, "gameConfig.json");
        if (!File.Exists(configPath))
            return "DesktopGL";

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return "DesktopGL";

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals("graphicsApi", StringComparison.OrdinalIgnoreCase) ||
                    property.Value.ValueKind != JsonValueKind.String)
                    continue;

                var value = property.Value.GetString();
                return value?.Trim().ToLowerInvariant() switch
                {
                    "windowsdx12" or "dx12" or "direct3d12" or "directx12" => "WindowsDX12",
                    "desktopvk" or "vk" or "vulkan" => "DesktopVK",
                    _ => "DesktopGL"
                };
            }
        }
        catch
        {
            // A damaged or older settings file should not prevent the game from opening.
        }

        return "DesktopGL";
    }

    private static string DisplayName(string backend)
        => backend switch
        {
            "WindowsDX12" => "Direct3D 12",
            "DesktopVK" => "Vulkan",
            _ => "OpenGL"
        };

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);
}
