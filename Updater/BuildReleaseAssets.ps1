[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$PublishDirectory,
    [string]$OutputDirectory,
    [string]$PreviousFullZip,
    [string]$PreviousManifestPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

# Automatic script-path variables can be empty while PowerShell evaluates param()
# default expressions. Resolve paths in the script body instead, where -File has
# established PSCommandPath, and derive all defaults from the script location.
$scriptFilePath = $PSCommandPath
if ([string]::IsNullOrWhiteSpace($scriptFilePath)) {
    $scriptFilePath = $MyInvocation.MyCommand.Path
}
if ([string]::IsNullOrWhiteSpace($scriptFilePath)) {
    throw 'Unable to determine the script path. Run BuildReleaseAssets.ps1 using PowerShell -File.'
}
$scriptDirectory = Split-Path -Parent $scriptFilePath
$projectRoot = [IO.Path]::GetFullPath((Join-Path $scriptDirectory '..'))

if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $PublishDirectory = Join-Path $projectRoot 'bin\Publish\win-x64'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot 'release-assets'
}

$Version = $Version.Trim()
if ($Version.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) { $Version = $Version.Substring(1) }
if ($Version -notmatch '^\d+(\.\d+){1,3}$') { throw "Version must be numeric, like 0.0.17. Got '$Version'." }

$sourceVersionPath = Join-Path $projectRoot 'version.txt'
if (-not (Test-Path -LiteralPath $sourceVersionPath -PathType Leaf)) { throw "version.txt was not found: $sourceVersionPath" }
$sourceVersion = (Get-Content -LiteralPath $sourceVersionPath -Raw).Trim()
if ($sourceVersion -ne $Version) {
    throw "The requested package version ($Version) does not match version.txt ($sourceVersion). Update version.txt yourself, rebuild Publish, and rerun this script. The script never edits version.txt or info.md."
}

$publish = [IO.Path]::GetFullPath($PublishDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $publish -PathType Container)) { throw "Publish directory does not exist: $publish" }
New-Item -ItemType Directory -Path $output -Force | Out-Null

$work = Join-Path ([IO.Path]::GetTempPath()) ("TSC-release-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null

function Test-ProtectedPath([string]$RelativePath) {
    $p = $RelativePath.Replace('\', '/')
    $parts = $p.Split('/')
    if ($parts[0] -in @('songs', 'save', 'logs', 'state')) { return $true }
    if ($parts[0] -ieq 'Skins' -and ($parts.Count -lt 3 -or $parts[1] -ine 'Default')) { return $true }
    if ($parts.Count -eq 1 -and $parts[0] -in @('gameConfig.json', 'keybinds.json', 'tsc_crash.log', 'info.md', 'version.txt', '.tsc-update-state.json', '.tsc-skin-state.json')) { return $true }
    if ($parts[0] -ieq 'tools' -and $parts[-1] -ieq 'ffmpeg.exe') { return $true }
    return $false
}

function Get-RelativePath([string]$Root, [string]$Path) {
    # Works with both Windows PowerShell 5.1 (.NET Framework) and PowerShell 7+.
    $rootFull = [IO.Path]::GetFullPath($Root)
    if (-not $rootFull.EndsWith([IO.Path]::DirectorySeparatorChar.ToString(), [StringComparison]::OrdinalIgnoreCase)) {
        $rootFull += [IO.Path]::DirectorySeparatorChar
    }
    $rootUri = [System.Uri]::new($rootFull)
    $pathUri = [System.Uri]::new([IO.Path]::GetFullPath($Path))
    return [Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString()).Replace('/', [IO.Path]::DirectorySeparatorChar)
}

function Copy-DirectoryContents([string]$SourceRoot, [string]$DestinationRoot) {
    foreach ($file in Get-ChildItem -LiteralPath $SourceRoot -File -Recurse -Force) {
        $relative = Get-RelativePath $SourceRoot $file.FullName
        $target = Join-Path $DestinationRoot $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
}

function Get-ManagedManifest([string]$Root, [string]$PackVersion, [string]$Kind) {
    $files = @()
    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse -Force) {
        $relative = (Get-RelativePath $Root $file.FullName).Replace('\', '/')
        if ($relative -ieq '.tsc-package-manifest.json' -or (Test-ProtectedPath $relative)) { continue }
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        $files += [pscustomobject]@{ path = $relative; size = [long]$file.Length; sha256 = $hash }
    }
    return [pscustomobject]@{ schemaVersion = 1; version = $PackVersion; kind = $Kind; files = @($files | Sort-Object path) }
}

function Remove-ExcludedFiles([string]$Root, [bool]$Minimal) {
    # The update mode lives in data/TSC.exe; only the small Native AOT launcher is at root.
    # Remove leftovers if this publish directory was previously built by an older version.
    foreach ($legacyName in @('updater', 'updater.next', 'updater.old', 'data/updater', 'data/updater.next', 'data/updater.old')) {
        $legacyPath = Join-Path $Root $legacyName
        if (Test-Path -LiteralPath $legacyPath) {
            Remove-Item -LiteralPath $legacyPath -Recurse -Force
        }
    }

    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse -Force) {
        $relative = (Get-RelativePath $Root $file.FullName).Replace('\', '/')
        if ($relative -ieq 'info.md' -or $relative -ieq 'version.txt' -or $relative -ieq '.tsc-package-manifest.json' -or $relative -ieq 'patch-manifest.json' -or (Test-ProtectedPath $relative)) {
            # Never ship manifests inside a ZIP, user settings, scores, logs, songs,
            # custom skins, or any user's FFmpeg executable.
            Remove-Item -LiteralPath $file.FullName -Force
            continue
        }
        if ($Minimal -and $relative.StartsWith('Skins/Default/', [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $file.FullName -Force
        }
    }
    Get-ChildItem -LiteralPath $Root -Directory -Recurse | Sort-Object FullName -Descending | ForEach-Object {
        if (-not (Get-ChildItem -LiteralPath $_.FullName -Force | Select-Object -First 1)) {
            Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
        }
    }
}

function Write-AssetZip([string]$Root, [string]$ZipPath) {
    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
    [IO.Compression.ZipFile]::CreateFromDirectory($Root, $ZipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
}

function Assert-NoEmbeddedManifest([string]$ZipPath, [string]$ManifestEntryName) {
    $archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        if ($archive.GetEntry($ManifestEntryName)) {
            throw "Release ZIP '$ZipPath' unexpectedly contains '$ManifestEntryName'. Manifests must be separate release assets."
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Expand-Zip([string]$ZipPath, [string]$Destination) {
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Recurse -Force }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $Destination)
}

try {
    foreach ($required in @('TSC.exe', 'data/TSC.exe', 'data/TSC.dll', 'data/TSC.deps.json', 'data/TSC.runtimeconfig.json', 'data/coreclr.dll', 'Skins/Default/skin.lua')) {
        $requiredPath = Join-Path $publish ($required.Replace('/', [IO.Path]::DirectorySeparatorChar))
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Publish output is incomplete; missing '$requiredPath'. Run the TSC-Publish profile successfully before packaging."
        }
    }
    $launcherSize = (Get-Item -LiteralPath (Join-Path $publish 'TSC.exe')).Length
    if ($launcherSize -gt 5MB) { throw "Launcher is too large ($launcherSize bytes). The release package must use the small Native AOT launcher." }

    $fullRoot = Join-Path $work 'full'
    $minimalRoot = Join-Path $work 'minimal'
    New-Item -ItemType Directory -Path $fullRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $minimalRoot -Force | Out-Null
    Copy-DirectoryContents $publish $fullRoot
    Copy-DirectoryContents $publish $minimalRoot
    Remove-ExcludedFiles $fullRoot $false
    Remove-ExcludedFiles $minimalRoot $true

    $fullManifest = Get-ManagedManifest $fullRoot $Version 'full'
    $minimalManifest = Get-ManagedManifest $minimalRoot $Version 'minimal'
    $releaseManifestPath = Join-Path $output "TSC-win-x64-$Version-manifest.json"
    $legacyMinimalManifestPath = Join-Path $output "TSC-win-x64-$Version-minimal-manifest.json"
    $legacyPatchManifestPath = Join-Path $output "TSC-win-x64-$Version-patch-manifest.json"

    $fullZip = Join-Path $output "TSC-win-x64-$Version.zip"
    $minimalZip = Join-Path $output "TSC-win-x64-$Version-minimal.zip"
    Write-AssetZip $fullRoot $fullZip
    Write-AssetZip $minimalRoot $minimalZip
    Assert-NoEmbeddedManifest $fullZip '.tsc-package-manifest.json'
    Assert-NoEmbeddedManifest $minimalZip '.tsc-package-manifest.json'
    Write-Host "Created $fullZip"
    Write-Host "Created $minimalZip"

    # The release manifest is written once at the end and contains the metadata
    # for both package variants and, when available, the patch. The older
    # per-package/patch manifest filenames are only read for migration purposes.
    $patchManifest = $null

    if (-not [string]::IsNullOrWhiteSpace($PreviousFullZip)) {
        $previousZip = [IO.Path]::GetFullPath($PreviousFullZip)
        if (-not (Test-Path -LiteralPath $previousZip -PathType Leaf)) { throw "Previous full ZIP not found: $previousZip" }
        $previousRoot = Join-Path $work 'previous'
        Expand-Zip $previousZip $previousRoot
        $embeddedPreviousManifest = Join-Path $previousRoot '.tsc-package-manifest.json'
        if (Test-Path -LiteralPath $embeddedPreviousManifest -PathType Leaf) {
            # Compatibility with previous releases that embedded the manifest in the ZIP.
            $previousManifest = Get-Content -LiteralPath $embeddedPreviousManifest -Raw | ConvertFrom-Json
        }
        else {
            $candidateManifestPaths = @()
            if (-not [string]::IsNullOrWhiteSpace($PreviousManifestPath)) {
                $candidateManifestPaths += [IO.Path]::GetFullPath($PreviousManifestPath)
            }
            $zipBase = [IO.Path]::GetFileName($previousZip)
            if ($zipBase -match '^TSC-win-x64-(?<version>\d+(?:\.\d+){1,3})(?:-minimal)?\.zip$') {
                $previousVersionFromName = $Matches['version']
                $manifestName = "TSC-win-x64-$previousVersionFromName-manifest.json"
                $candidateManifestPaths += (Join-Path (Split-Path -Parent $previousZip) $manifestName)
                $candidateManifestPaths += (Join-Path $output $manifestName)
            }
            $foundPreviousManifest = $candidateManifestPaths | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
            if (-not $foundPreviousManifest) {
                throw 'The previous ZIP has no embedded manifest and no matching external manifest was found. Keep TSC-win-x64-<previous-version>-manifest.json beside the ZIP or pass -PreviousManifestPath.'
            }
            $previousDocument = Get-Content -LiteralPath $foundPreviousManifest -Raw | ConvertFrom-Json
            # New releases wrap package manifests under `full` and `minimal`.
            # Older releases stored the full package manifest at the JSON root.
            if ($null -ne $previousDocument.full -and $null -ne $previousDocument.full.files) {
                $previousManifest = $previousDocument.full
            }
            else {
                $previousManifest = $previousDocument
            }
        }
        $fromVersion = [string]$previousManifest.version
        if ([version]$fromVersion -ge [version]$Version) { throw "Previous package version ($fromVersion) must be lower than target version ($Version)." }

        $patchRoot = Join-Path $work 'patch'
        $payloadRoot = Join-Path $patchRoot 'files'
        New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
        $targetByPath = @{}
        foreach ($entry in $fullManifest.files) { $targetByPath[[string]$entry.path] = $entry }
        $previousByPath = @{}
        foreach ($entry in $previousManifest.files) { $previousByPath[[string]$entry.path] = $entry }

        $changed = @()
        foreach ($entry in $fullManifest.files) {
            $rel = [string]$entry.path
            $old = $previousByPath[$rel]
            if ($null -ne $old -and [string]$old.sha256 -ieq [string]$entry.sha256) { continue }
            $source = Join-Path $fullRoot ($rel.Replace('/', [IO.Path]::DirectorySeparatorChar))
            $target = Join-Path $payloadRoot ($rel.Replace('/', [IO.Path]::DirectorySeparatorChar))
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $target -Force
            $changed += [pscustomobject]@{
                path = $rel
                fromSha256 = if ($null -ne $old) { [string]$old.sha256 } else { $null }
                size = [long]$entry.size
                sha256 = [string]$entry.sha256
            }
        }

        $deleted = @()
        foreach ($entry in $previousManifest.files) {
            $rel = [string]$entry.path
            if (-not $targetByPath.ContainsKey($rel)) {
                $deleted += [pscustomobject]@{ path = $rel; fromSha256 = [string]$entry.sha256 }
            }
        }
        $patchManifest = [pscustomobject]@{
            schemaVersion = 1
            fromVersion = $fromVersion
            toVersion = $Version
            files = @($changed | Sort-Object path)
            deletedFiles = @($deleted | Sort-Object path)
        }
        $patchZip = Join-Path $output "TSC-win-x64-$Version-patch.zip"
        Write-AssetZip $patchRoot $patchZip
        Assert-NoEmbeddedManifest $patchZip 'patch-manifest.json'
        Write-Host "Created $patchZip"
    }
    else {
        # Avoid accidentally leaving an old patch for this version in the output
        # folder when building the first release or rebuilding without a base ZIP.
        $stalePatch = Join-Path $output "TSC-win-x64-$Version-patch.zip"
        if (Test-Path -LiteralPath $stalePatch) { Remove-Item -LiteralPath $stalePatch -Force }
        Write-Host 'No patch ZIP created: pass -PreviousFullZip when a prior release package exists.'
    }

    $releaseManifest = [pscustomobject]@{
        schemaVersion = 2
        version = $Version
        full = $fullManifest
        minimal = $minimalManifest
        patch = $patchManifest
    }
    [IO.File]::WriteAllText($releaseManifestPath, ($releaseManifest | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    # Clean up outputs left by older packaging versions so the release directory
    # has exactly one manifest for this version.
    foreach ($legacyManifest in @($legacyMinimalManifestPath, $legacyPatchManifestPath)) {
        if (Test-Path -LiteralPath $legacyManifest) { Remove-Item -LiteralPath $legacyManifest -Force }
    }
    Write-Host "Created $releaseManifestPath"
}
finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}
