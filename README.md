# TSC

TSC is a rhythm game and chart-editing project built with Monogame.

## Development requirements

- Windows x64
- .NET 8 SDK
- A network connection for restoring NuGet packages on the first build

## Build and run

From the project directory, run:

```powershell
dotnet run
```

To publish using the included Windows x64 profile:

```powershell
dotnet publish TSC.csproj -c Publish -p:PublishProfile=TSC-Publish
```

The profile writes to `bin/Publish/win-x64/`.
