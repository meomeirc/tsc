# TSC Updater

The Windows x64 updater is published by the `TSC-Publish` profile into `updater/` beside the game. It checks the GitHub Releases API for the newest stable release in the configured repository, shows a small progress window, verifies package manifests and supported GitHub SHA-256 asset digests, and updates files without overwriting player settings, scores, logs, songs, custom skins, or `tools/ffmpeg.exe`.

## Release assets

For a release tagged `v0.0.17`, upload:

- `TSC-win-x64-0.0.17.zip` — complete package, including `Skins/Default/` and `updater/`.
- `TSC-win-x64-0.0.17-minimal.zip` — complete package without `Skins/Default/`.
- `TSC-win-x64-0.0.17-patch.zip` — transition from the immediately preceding release. This is omitted for the first release because there is no patch base.

The Full and Minimal ZIPs include a generated `.tsc-package-manifest.json`. The patch ZIP contains `patch-manifest.json`, changed payloads under `files/`, deletion entries, and a target package manifest. Do not upload a separate manifest asset.

The updater chooses a complete patch chain only when the summed compressed size is less than or equal to the fallback package size. Minimal is the fallback when present; otherwise Full is the fallback. If Minimal is selected, the updater refreshes `Skins/Default/` from one pinned commit of `main`. Full and patch updates use the Default skin included in their selected release assets and do not fetch skin files from `main`.

## Make release assets

First update `version.txt` yourself to the release version, then publish the game and updater. The asset builder checks that the requested version matches `version.txt`; it does not edit `version.txt` or `info.md`.

```powershell
dotnet publish .\TSC.csproj --configuration Publish --property:PublishProfile=TSC-Publish
.\Updater\BuildReleaseAssets.ps1 -Version 0.0.17
```

That creates the Full and Minimal assets in `release-assets/`. For later releases, pass the previous release's Full asset ZIP to generate the patch too:

```powershell
.\Updater\BuildReleaseAssets.ps1 -Version 0.0.18 -PreviousFullZip .\release-assets\TSC-win-x64-0.0.17.zip
```

Publish the GitHub release only after its assets are uploaded. The game checks the latest stable release once during startup and prompts when its embedded game version is older than the release tag.

## Build the updater by itself

```powershell
dotnet publish .\Updater\TSC.Updater.csproj -c Release -r win-x64 --self-contained true
```

The updater is a Windows Forms application and uses the Windows PowerShell handoff only after the update is staged and validated, so it can replace its own runtime files after its process exits.
