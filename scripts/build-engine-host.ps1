# Publishes the OmniPacker engine daemon (engine/src/OmniPacker.EngineHost) as a
# self-contained single-file binary for every platform OmniPacker bundles, into
# src-tauri/binaries/<platform>/OmniPackerEngine(.exe) - alongside the
# DepotDownloader and 7-Zip sidecars. Self-contained so a shipped app needs no
# .NET SDK/runtime installed.
#
# Usage:
#   pwsh ./scripts/build-engine-host.ps1 [-Rid win-x64]   # one RID
#   pwsh ./scripts/build-engine-host.ps1                    # all RIDs
#
# Managed cross-RID publishes work from any host; you can only *run* the one
# matching your host.

param(
    [string]$Rid
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDir
$csproj = Join-Path $projectRoot "engine\src\OmniPacker.EngineHost\OmniPacker.EngineHost.csproj"
$binariesDir = Join-Path $projectRoot "src-tauri\binaries"

if (-not (Test-Path $csproj)) {
    throw "Could not find OmniPacker.EngineHost.csproj at '$csproj'."
}

# RID -> OmniPacker binaries subfolder + output file name (matches the DD layout).
$allTargets = @(
    @{ Rid = "win-x64";     Dir = "win-x64";     File = "OmniPackerEngine.exe" },
    @{ Rid = "win-arm64";   Dir = "win-arm64";   File = "OmniPackerEngine.exe" },
    @{ Rid = "linux-x64";   Dir = "linux-x64";   File = "OmniPackerEngine" },
    @{ Rid = "linux-arm64"; Dir = "linux-arm64"; File = "OmniPackerEngine" },
    @{ Rid = "linux-arm";   Dir = "linux-arm";   File = "OmniPackerEngine" },
    @{ Rid = "osx-x64";     Dir = "macos-x64";   File = "OmniPackerEngine" },
    @{ Rid = "osx-arm64";   Dir = "macos-arm64"; File = "OmniPackerEngine" }
)

$targets = if ($Rid) { $allTargets | Where-Object { $_.Rid -eq $Rid } } else { $allTargets }
if (-not $targets) { throw "Unknown RID '$Rid'." }

foreach ($target in $targets) {
    $rid = $target.Rid
    Write-Host "`n=== Publishing OmniPackerEngine for $rid ===" -ForegroundColor Cyan

    dotnet publish $csproj -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

    $ext = if ($rid -like "win-*") { ".exe" } else { "" }
    $published = Join-Path (Split-Path $csproj) "bin\Release\net10.0\$rid\publish\OmniPacker.EngineHost$ext"
    if (-not (Test-Path $published)) {
        throw "Expected published binary not found: $published"
    }

    $destDir = Join-Path $binariesDir $target.Dir
    New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    $dest = Join-Path $destDir $target.File
    Copy-Item -Path $published -Destination $dest -Force
    Write-Host "  -> $dest" -ForegroundColor Green
}

Write-Host "`nEngine daemon sidecar(s) published." -ForegroundColor Cyan
