# TSC

TSC is a rhythm game and chart-editing project built with Monogame.

## Development requirements

- Windows x64
- .NET 8 SDK
- A network connection for restoring NuGet packages on the first build

## Build and run

From the project directory, run:

```powershell
dotnet restore .\TSC.csproj
dotnet run --project .\TSC.csproj
```

To publish using the included Windows x64 profile:

```powershell
dotnet publish .\TSC.csproj -p:PublishProfile=Properties\PublishProfiles\TSC-Publish.pubxml
```

The profile writes to `bin/Publish/win-x64/`.
