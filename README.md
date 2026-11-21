# TSC

TSC is a rhythm game and chart-editing project built with Monogame.

## Development requirements

- Windows x64
- .NET 8 SDK
- A network connection for restoring NuGet packages on the first build
- The Vulkan SDK when building the `DesktopVK` target (MonoGame requirement)

## Build and run

From the project directory, run the default DesktopGL backend:

```powershell
dotnet run
```

To try MonoGame's native Vulkan backend, pass `TSCBackend=DesktopVK`. To try the native Direct3D 12 backend, pass `TSCBackend=WindowsDX12`.

```powershell
dotnet run .\TSC.csproj -c Debug -p:TSCBackend=DesktopVK
# or
dotnet run .\TSC.csproj -c Debug -p:TSCBackend=WindowsDX12
```

`TSCBackend` accepts `DesktopGL` (the default), `DesktopVK`, or `WindowsDX12`. The selected backend is also used when compiling MonoGame content. The Vulkan target uses MonoGame's `MonoGame.Framework.Native` and `MonoGame.Runtime.Windows.Vulkan` 3.8.6.1 packages. Android remains on its Android backend.

To prepare a Windows x64 install with all three graphics backends and one shared .NET runtime:

```powershell
.\build\Publish-DualBackend.ps1
```

This creates `bin/Publish/win-x64/` with `data/DesktopGL/`, `data/DesktopVK/`, `data/WindowsDX12/`, one shared `runtime/`, and the root launcher. The launcher reads `graphicsApi` from `gameConfig.json`. Change it in Settings → Display; restart TSC to use the other backend.
