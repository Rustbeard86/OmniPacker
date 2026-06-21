# OmniPacker Windows x64 Build Script

param([switch]$NoBump)

$ErrorActionPreference = "Stop"

Write-Host "========================================"
Write-Host "Building OmniPacker for Windows x64"
Write-Host "Target: x86_64-pc-windows-msvc"
Write-Host "========================================"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDir
$binariesDir = Join-Path $projectRoot "src-tauri\binaries"
$configFile = Join-Path $projectRoot "src-tauri\tauri.conf.json"

# Bump the patch version BEFORE backing up the config, so the new version persists
# (Restore-Files restores tauri.conf.json from the backup taken below). Use -NoBump
# to rebuild the same version.
if (-not $NoBump) {
    & (Join-Path $scriptDir "bump-version.ps1") -Part patch | Out-Null
}

$tempBackup = New-Item -ItemType Directory -Path ([System.IO.Path]::GetTempPath()) -Name "omnipacker-binaries-backup-$(Get-Random)"

Write-Host "Creating backup of binaries in $tempBackup"
Copy-Item -Path $binariesDir -Destination $tempBackup -Recurse

Write-Host "Creating backup of tauri.conf.json"
Copy-Item -Path $configFile -Destination "$configFile.backup"

function Restore-Files {
    Write-Host "Restoring original files..."
    Remove-Item -Path $binariesDir -Recurse -Force -ErrorAction SilentlyContinue
    Move-Item -Path "$tempBackup\binaries" -Destination (Split-Path $binariesDir) -Force
    Remove-Item -Path $tempBackup -Recurse -Force -ErrorAction SilentlyContinue

    if (Test-Path "$configFile.backup") {
        Move-Item -Path "$configFile.backup" -Destination $configFile -Force
    }
}

try {
    Write-Host "Modifying tauri.conf.json to only include win-x64 resources..."
    $config = Get-Content $configFile -Raw | ConvertFrom-Json
    $config.bundle.resources = @("binaries/win-x64/*")
    $config | ConvertTo-Json -Depth 10 | Set-Content $configFile

    Write-Host "Removing non-target platform binaries..."
    Get-ChildItem -Path $binariesDir -Directory | Where-Object { $_.Name -ne "win-x64" } | ForEach-Object {
        Write-Host "  Removing $($_.Name)"
        Remove-Item -Path $_.FullName -Recurse -Force
    }

    Write-Host "`nRemaining binaries:"
    Get-ChildItem -Path $binariesDir

    Set-Location $projectRoot
    Write-Host "`nStarting Tauri build..."
    npm run tauri build -- --target x86_64-pc-windows-msvc
    if ($LASTEXITCODE -ne 0) {
        throw "Tauri build failed with exit code $LASTEXITCODE"
    }

    Write-Host "`n========================================"
    Write-Host "Build complete!"
    Write-Host "Artifacts:"
    Write-Host "  MSI: src-tauri\target\x86_64-pc-windows-msvc\release\bundle\msi\"
    Write-Host "  NSIS: src-tauri\target\x86_64-pc-windows-msvc\release\bundle\nsis\"
    Write-Host "========================================"

    Write-Host "Copying installers to release\ ..."
    $version = (Get-Content $configFile -Raw | ConvertFrom-Json).version
    $releaseDir = Join-Path $projectRoot "release"
    New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
    $bundleDir = Join-Path $projectRoot "src-tauri\target\x86_64-pc-windows-msvc\release\bundle"
    Get-ChildItem -Path (Join-Path $bundleDir "msi\OmniPacker_${version}_*.msi"), (Join-Path $bundleDir "nsis\OmniPacker_${version}_*-setup.exe") -ErrorAction SilentlyContinue |
        ForEach-Object {
            Copy-Item -Path $_.FullName -Destination $releaseDir -Force
            Write-Host "  -> release\$($_.Name)"
        }
}
finally {
    Restore-Files
}
