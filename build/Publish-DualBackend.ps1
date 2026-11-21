[CmdletBinding()]
param(
    [string]$InstallRoot,
    [string]$DotnetRoot
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Join-Path $projectRoot 'bin\Publish\win-x64'
}
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)

$dotnetCommand = Get-Command dotnet -ErrorAction Stop
if ([string]::IsNullOrWhiteSpace($DotnetRoot)) {
    $dotnetPath = $dotnetCommand.Source
    if ([string]::IsNullOrWhiteSpace($dotnetPath)) { $dotnetPath = $dotnetCommand.Path }
    $DotnetRoot = Split-Path -Parent $dotnetPath
}
$DotnetRoot = [IO.Path]::GetFullPath($DotnetRoot)

$frameworkRoot = Join-Path $DotnetRoot 'shared\Microsoft.NETCore.App'
if (-not (Test-Path -LiteralPath $frameworkRoot -PathType Container)) {
    throw "Microsoft.NETCore.App runtime folder was not found under '$DotnetRoot'. Install the .NET 8 SDK/runtime or pass -DotnetRoot."
}
$frameworkVersion = Get-ChildItem -LiteralPath $frameworkRoot -Directory |
    Where-Object { $_.Name -match '^8\.0\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1
if (-not $frameworkVersion) {
    throw "No .NET 8 runtime is installed under '$frameworkRoot'."
}

$windowsDesktopRoot = Join-Path $DotnetRoot 'shared\Microsoft.WindowsDesktop.App'
if (-not (Test-Path -LiteralPath $windowsDesktopRoot -PathType Container)) {
    throw "Microsoft.WindowsDesktop.App runtime folder was not found under '$DotnetRoot'. The Windows Desktop Runtime for .NET 8 is required."
}
$windowsDesktopVersion = Get-ChildItem -LiteralPath $windowsDesktopRoot -Directory |
    Where-Object { $_.Name -match '^8\.0\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1
if (-not $windowsDesktopVersion) {
    throw "No .NET 8 Windows Desktop runtime is installed under '$windowsDesktopRoot'."
}

$fxrRoot = Join-Path $DotnetRoot 'host\fxr'
$fxrVersion = Get-ChildItem -LiteralPath $fxrRoot -Directory |
    Where-Object { $_.Name -match '^8\.0\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1
if (-not $fxrVersion) {
    throw "No .NET 8 hostfxr is installed under '$fxrRoot'."
}

$dataRoot = Join-Path $InstallRoot 'data'
if (Test-Path -LiteralPath $dataRoot) {
    # data/ is reserved for backend binaries and compiled game content, not player files.
    Remove-Item -LiteralPath $dataRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
foreach ($backend in @('DesktopGL', 'DesktopVK', 'WindowsDX12')) {
    $publishDirectory = Join-Path $dataRoot $backend
    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

    Write-Host "Publishing $backend (framework-dependent app; shared runtime will be installed once)..."
    & dotnet publish (Join-Path $projectRoot 'TSC.csproj') `
        --configuration Publish `
        --runtime win-x64 `
        --self-contained false `
        --output $publishDirectory `
        "-p:TSCBackend=$backend" `
        "-p:TSCInstallRoot=$InstallRoot\" `
        '-p:UseAppHost=true'
    if ($LASTEXITCODE -ne 0) {
        throw "TSC publish failed for $backend (exit code $LASTEXITCODE)."
    }

    foreach ($requiredName in @('TSC.exe', 'TSC.dll', 'TSC.deps.json', 'TSC.runtimeconfig.json')) {
        $requiredPath = Join-Path $publishDirectory $requiredName
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "$backend publish is incomplete; missing '$requiredPath'."
        }
    }
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $publishDirectory 'TSC.runtimeconfig.json') -Raw | ConvertFrom-Json
    $frameworkNames = @()
    if ($null -ne $runtimeConfig.runtimeOptions.framework) {
        $frameworkNames += [string]$runtimeConfig.runtimeOptions.framework.name
    }
    foreach ($framework in @($runtimeConfig.runtimeOptions.frameworks)) {
        if ($null -ne $framework.name) { $frameworkNames += [string]$framework.name }
    }
    if ($frameworkNames -notcontains 'Microsoft.NETCore.App' -or
        $frameworkNames -notcontains 'Microsoft.WindowsDesktop.App' -or
        $null -ne $runtimeConfig.runtimeOptions.includedFrameworks) {
        throw "$backend must be a framework-dependent build that uses the shared .NET 8 and Windows Desktop runtimes. Check --self-contained false."
    }
}

# Use one shared framework-dependent runtime layout. The launcher sets DOTNET_ROOT
# before starting either backend, so we do not ship two copies of coreclr/System.*.
$runtimeRoot = Join-Path $InstallRoot 'runtime'
if (Test-Path -LiteralPath $runtimeRoot) {
    Remove-Item -LiteralPath $runtimeRoot -Recurse -Force
}
$runtimeFxrDestination = Join-Path $runtimeRoot (Join-Path 'host\fxr' $fxrVersion.Name)
$runtimeFrameworkDestination = Join-Path $runtimeRoot (Join-Path 'shared\Microsoft.NETCore.App' $frameworkVersion.Name)
$runtimeWindowsDesktopDestination = Join-Path $runtimeRoot (Join-Path 'shared\Microsoft.WindowsDesktop.App' $windowsDesktopVersion.Name)
New-Item -ItemType Directory -Path $runtimeFxrDestination -Force | Out-Null
New-Item -ItemType Directory -Path $runtimeFrameworkDestination -Force | Out-Null
New-Item -ItemType Directory -Path $runtimeWindowsDesktopDestination -Force | Out-Null
Copy-Item -Path (Join-Path $fxrVersion.FullName '*') -Destination $runtimeFxrDestination -Recurse -Force
Copy-Item -Path (Join-Path $frameworkVersion.FullName '*') -Destination $runtimeFrameworkDestination -Recurse -Force
Copy-Item -Path (Join-Path $windowsDesktopVersion.FullName '*') -Destination $runtimeWindowsDesktopDestination -Recurse -Force

if (-not (Test-Path -LiteralPath (Join-Path $runtimeFxrDestination 'hostfxr.dll'))) {
    throw 'The shared .NET hostfxr was not copied correctly.'
}
if (-not (Test-Path -LiteralPath (Join-Path $runtimeFrameworkDestination 'coreclr.dll'))) {
    throw 'The shared .NET runtime was not copied correctly.'
}
if (-not (Test-Path -LiteralPath (Join-Path $runtimeWindowsDesktopDestination 'System.Windows.Forms.dll'))) {
    throw 'The shared .NET Windows Desktop runtime was not copied correctly.'
}

$launcherPath = Join-Path $InstallRoot 'TSC.exe'
if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
    throw "The root Native AOT launcher was not created: $launcherPath"
}

$runtimeSize = (Get-ChildItem -LiteralPath $runtimeRoot -File -Recurse | Measure-Object -Property Length -Sum).Sum
$desktopSize = (Get-ChildItem -LiteralPath (Join-Path $dataRoot 'DesktopGL') -File -Recurse | Measure-Object -Property Length -Sum).Sum
$vkSize = (Get-ChildItem -LiteralPath (Join-Path $dataRoot 'DesktopVK') -File -Recurse | Measure-Object -Property Length -Sum).Sum
$dx12Size = (Get-ChildItem -LiteralPath (Join-Path $dataRoot 'WindowsDX12') -File -Recurse | Measure-Object -Property Length -Sum).Sum
Write-Host "Shared .NET runtime: $([math]::Round($runtimeSize / 1MB, 1)) MB (.NET $($frameworkVersion.Name), Windows Desktop $($windowsDesktopVersion.Name))"
Write-Host "DesktopGL (OpenGL) backend: $([math]::Round($desktopSize / 1MB, 1)) MB"
Write-Host "DesktopVK (Vulkan) backend: $([math]::Round($vkSize / 1MB, 1)) MB"
Write-Host "WindowsDX12 backend:        $([math]::Round($dx12Size / 1MB, 1)) MB"
Write-Host "Three-backend install prepared at: $InstallRoot"
