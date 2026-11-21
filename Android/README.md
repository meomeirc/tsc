# TSC Android Prototype

## Current scope

- Landscape, fullscreen Android activity.
- Existing TSC menu, Default skin, Settings, Info, and Song Select.
- One-finger touch is translated into the existing mouse/pointer input path.
- Song gallery dragging and inertial scrolling reuse `SongSelectMenu.UpdateGalleryScroll`.
- The Info page supports direct finger dragging with a short inertial tail; its scrollbar can also be dragged.
- Main-menu `CHART EDITOR` and Song Select `EDIT` buttons are hidden on Android. Editor source is retained in the shared project but cannot be entered from these menu controls.
- Gameplay note geometry, multi-touch gameplay controls, Android file picker/import, and native Android text/Unicode fallback are not part of this first pass.

The first Android text renderer uses the bundled `SpriteFont`; characters not in that font are replaced with `?`. Android skin font rasterization is temporarily disabled so menu rendering never calls Windows GDI+.

## Build environment

Build this project on Windows. MonoGame's current requirements explicitly state that mobile builds are not supported from Linux. Install:

- .NET 10 SDK.
- Visual Studio 2026 with the **.NET Multi-platform App UI Development** workload, or the Android and MAUI workloads for command-line use.
- Microsoft OpenJDK 21 (recommended by the current .NET for Android setup guide).
- Android SDK platform 36 and Android platform-tools.

The Android app targets `net10.0-android36.0` and API 36. This is separate from the Windows application's .NET 8 target.

From the repository root:

```powershell
dotnet workload install maui android
dotnet workload restore .\Android\TSC.Android.csproj
dotnet restore .\Android\TSC.Android.csproj
dotnet build .\Android\TSC.Android.csproj -c Debug
```

Install the generated debug APK from the `Android\bin\Debug\` output directory:

```powershell
adb install -r <path-to-debug-apk>
```

## Songs and writable data

The first launch copies `gameConfig.json`, `keybinds.json`, and `Skins/Default` from packaged assets into the app-private folder:

```text
/data/user/0/com.tsc.rhythmgame/files/TSC/
├── gameConfig.json
├── keybinds.json
├── Skins/Default/
├── songs/
├── save/
├── Logs/
└── userdata/
```

The app creates an empty `songs` folder. Song importing and the Android Storage Access Framework are deferred; for the first device test, add a few test songs using a file-copy helper or a future import implementation.

## First device test checklist

1. TSC starts in landscape without a crash.
2. The Default skin and logo load.
3. Main-menu Play, Settings, Info, and Exit respond to taps.
4. The CHART EDITOR button is absent.
5. Song Select opens and the EDIT button is absent.
6. Song cards, difficulty selection, and right-rail controls respond to taps.
7. The song gallery can be dragged vertically and continues scrolling with inertia after release.
8. Android Back follows TSC's configured menu-back behavior.

No APK is included in this source package: the current build workspace is Linux, and MonoGame's Android target must be built on a supported Windows development environment.
