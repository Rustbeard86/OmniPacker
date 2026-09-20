# Rebuilds the DepotDownloader sidecar (our fork) for every platform OmniPacker
# bundles and copies the resulting binaries into src-tauri/binaries/<platform>/.
#
# Source of truth is the git submodule at vendor/DepotDownloader, pinned to a
# commit in github.com/Rustbeard86/DepotDownloader (our fork of
# github.com/elgreams/DepotDownloader, itself a fork of SteamRE/DepotDownloader).
# Initialize it first if a fresh clone has not populated it:
#
#   git submodule update --init vendor/DepotDownloader
#
# Run this after changing the fork so all platforms get the updated sidecar.
# Managed (.NET) publishes for non-host RIDs work fine from any OS - they bundle
# the target runtime - though you can only *run* the one matching your host.
#
# Usage:
#   pwsh ./scripts/rebuild-depotdownloader.ps1 [-ForkPath <path-to-DepotDownloader-repo>]
#
# -ForkPath overrides the source repo (default: the vendored submodule). Use it
# only when building from a checkout other than the pinned submodule.

param(
    [string]$ForkPath
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDir
$binariesDir = Join-Path $projectRoot "src-tauri\binaries"

if (-not $ForkPath) {
    # Default: the vendored submodule pinned by this repo.
    $ForkPath = Join-Path $projectRoot "vendor\DepotDownloader"
}

$csproj = Join-Path $ForkPath "DepotDownloader\DepotDownloader.csproj"
if (-not (Test-Path $csproj)) {
    throw "Could not find DepotDownloader.csproj at '$csproj'. If using the submodule, run 'git submodule update --init vendor/DepotDownloader'; otherwise pass -ForkPath <repo>."
}

# RID -> OmniPacker binaries subfolder + output file name.
$targets = @(
    @{ Rid = "win-x64";     Dir = "win-x64";     File = "DepotDownloader.exe" },
    @{ Rid = "win-arm64";   Dir = "win-arm64";   File = "DepotDownloader.exe" },
    @{ Rid = "linux-x64";   Dir = "linux-x64";   File = "DepotDownloader" },
    @{ Rid = "linux-arm64"; Dir = "linux-arm64"; File = "DepotDownloader" },
    @{ Rid = "linux-arm";   Dir = "linux-arm";   File = "DepotDownloader" },
    @{ Rid = "osx-x64";     Dir = "macos-x64";   File = "DepotDownloader" },
    @{ Rid = "osx-arm64";   Dir = "macos-arm64"; File = "DepotDownloader" }
)

foreach ($target in $targets) {
    $rid = $target.Rid
    Write-Host "`n=== Publishing DepotDownloader for $rid ===" -ForegroundColor Cyan

    dotnet publish $csproj -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

    $publishedExe = Join-Path $ForkPath "DepotDownloader\bin\Release\net9.0\$rid\publish\$($target.File)"
    if (-not (Test-Path $publishedExe)) {
        throw "Expected published binary not found: $publishedExe"
    }

    $destDir = Join-Path $binariesDir $target.Dir
    New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    $dest = Join-Path $destDir $target.File
    Copy-Item -Path $publishedExe -Destination $dest -Force
    Write-Host "  -> $dest" -ForegroundColor Green
}

Write-Host "`nAll DepotDownloader sidecars rebuilt." -ForegroundColor Cyan
