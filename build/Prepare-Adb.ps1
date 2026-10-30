param(
    [Parameter(Mandatory = $true)]
    [string]$ArchivePath,
    [Parameter(Mandatory = $true)]
    [string]$DestinationPath
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$version = '37.0.1'
$archiveUrl = "https://dl.google.com/android/repository/platform-tools_r$version-win.zip"
$expectedSha256 = '45f4d63113e895ebde0c90f194099a4676b6ac653bd28d54314a9e022bbc1a99'

$archiveDirectory = Split-Path -Parent $ArchivePath
if (-not (Test-Path -LiteralPath $archiveDirectory)) {
    New-Item -ItemType Directory -Path $archiveDirectory -Force | Out-Null
}

if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
    Write-Host "Downloading Android SDK Platform-Tools $version..."
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $archiveUrl -OutFile $ArchivePath -UseBasicParsing
}

# Use .NET directly instead of Get-FileHash so this also works in older
# Windows PowerShell environments where that cmdlet is unavailable.
$sha256 = [System.Security.Cryptography.SHA256]::Create()
try {
    $archiveStream = [System.IO.File]::OpenRead($ArchivePath)
    try {
        $hashBytes = $sha256.ComputeHash($archiveStream)
        $actualSha256 = ([System.BitConverter]::ToString($hashBytes)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $archiveStream.Dispose()
    }
}
finally {
    $sha256.Dispose()
}
if ($actualSha256 -ne $expectedSha256) {
    Remove-Item -LiteralPath $ArchivePath -Force -ErrorAction SilentlyContinue
    throw "Platform-Tools archive SHA-256 mismatch. Expected $expectedSha256, received $actualSha256."
}

$extractRoot = Join-Path ([IO.Path]::GetTempPath()) ("TSC-PlatformTools-" + [Guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -LiteralPath $ArchivePath -DestinationPath $extractRoot -Force
    $platformTools = Join-Path $extractRoot 'platform-tools'
    $requiredFiles = @('adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll')
    foreach ($file in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $platformTools $file) -PathType Leaf)) {
            throw "The verified Platform-Tools archive did not contain platform-tools/$file."
        }
    }

    New-Item -ItemType Directory -Path $DestinationPath -Force | Out-Null
    # Remove DLLs from a previously bundled Platform-Tools version first so an old
    # runtime library cannot accidentally be loaded beside the newly copied adb.exe.
    Get-ChildItem -LiteralPath $DestinationPath -Filter '*.dll' -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
    Copy-Item -LiteralPath (Join-Path $platformTools 'adb.exe') -Destination $DestinationPath -Force
    # Keep every DLL shipped with this pinned ADB build next to adb.exe, including
    # any new runtime libraries introduced by later compatible Platform-Tools builds.
    Get-ChildItem -LiteralPath $platformTools -Filter '*.dll' -File |
        Copy-Item -Destination $DestinationPath -Force

    $adbExecutable = Join-Path $DestinationPath 'adb.exe'
    $adbVersionOutput = (& $adbExecutable version 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or $adbVersionOutput -notmatch [regex]::Escape($version)) {
        throw "The bundled adb.exe did not report the expected Platform-Tools version $version. Output: $adbVersionOutput"
    }

    # Preserve Google's package-level notices alongside the ADB-specific AOSP
    # license and notice already included in the source project.
    $platformToolsNotice = Join-Path $platformTools 'NOTICE.txt'
    if (Test-Path -LiteralPath $platformToolsNotice -PathType Leaf) {
        Copy-Item -LiteralPath $platformToolsNotice `
            -Destination (Join-Path $DestinationPath 'PLATFORM-TOOLS-NOTICE.txt') -Force
    }

    if (-not (Test-Path -LiteralPath (Join-Path $DestinationPath 'adb.exe') -PathType Leaf)) {
        throw 'ADB was not copied into the publish tools/adb folder.'
    }
    Write-Host "Bundled verified Android SDK Platform-Tools $version at $DestinationPath"
}
finally {
    if (Test-Path -LiteralPath $extractRoot) {
        Remove-Item -LiteralPath $extractRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
