# TSC Updater

The Windows x64 updater is built into each game backend. It checks GitHub Releases for the newest stable release, verifies package manifests and supported GitHub SHA-256 asset digests, and updates application files without overwriting player settings, scores, logs, songs, custom skins, or `tools/ffmpeg.exe`.

## Make release assets

First update `version.txt` yourself to the release version. Then build all three backends and the shared runtime from a Windows machine with the .NET 8 SDK and Windows Desktop Runtime installed:

```powershell
.\build\Publish-DualBackend.ps1
.\Updater\BuildReleaseAssets.ps1 -Version 0.0.17
```

The publish script creates `bin/Publish/win-x64/` with all three backends and the single shared runtime. The asset builder checks that the requested version matches `version.txt`.

For later releases, pass the previous release's Full asset ZIP to generate the patch too:

```powershell
.\Updater\BuildReleaseAssets.ps1 -Version 0.0.18 -PreviousFullZip .\release-assets\TSC-win-x64-0.0.17.zip
```

Upload `TSC-win-x64-<version>.zip` and, when generated, `TSC-win-x64-<version>-patch.zip` together with the matching combined manifest.

## Build the updater by itself

```powershell
dotnet publish .\Updater\TSC.Updater.csproj -c Release -r win-x64 --self-contained true
```
