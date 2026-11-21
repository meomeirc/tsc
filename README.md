# TSC

TSC is a rhythm game and chart-editing project built with Monogame.

## Development requirements

- Windows x64
- .NET 8 SDK
- A network connection for restoring NuGet packages on the first build

## Build and run

From the project directory, run the default DesktopGL backend:

```powershell
dotnet run
```

To try MonoGame's native Direct3D 12 backend instead, pass `TSCBackend` as an MSBuild property:

```powershell
dotnet run .\TSC.csproj -c Debug -p:TSCBackend=WindowsDX12
```

`TSCBackend` accepts `DesktopGL` (the default) or `WindowsDX12`. The selected backend is also used when compiling MonoGame content. Android remains on its Android backend.

The MGCB local tool is restored from `.config/dotnet-tools.json` using an explicit manifest path, then invoked with `dotnet tool run mgcb --`. This avoids relying on the shorter `dotnet mgcb` tool alias and makes tool-restore failures visible in the build output. The Android project has its own manifest because its content build runs from the `Android` project directory.

The DX12 path currently does not support the skin system's direct GLSL shader API (`shaders.load`); those shader requests safely fall back to the regular MonoGame draw path. OpenAL-specific Windows audio-device recovery is DesktopGL-only.

To publish using the included Windows x64 profile:

```powershell
dotnet publish TSC.csproj -c Publish -p:PublishProfile=TSC-Publish
```

The profile writes to `bin/Publish/win-x64/`.
