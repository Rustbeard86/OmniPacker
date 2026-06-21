# Bumps the OmniPacker version and keeps it in sync across the three files that
# carry it: src-tauri/tauri.conf.json (drives bundle/artifact names), Cargo.toml,
# and package.json. tauri.conf.json is the source of truth.
#
# Usage:
#   pwsh ./scripts/bump-version.ps1            # patch bump (default)
#   pwsh ./scripts/bump-version.ps1 -Part minor
#   pwsh ./scripts/bump-version.ps1 -Set 1.3.0 # set an explicit version

param(
    [ValidateSet("patch", "minor", "major")]
    [string]$Part = "patch",
    [string]$Set
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptDir
$confFile = Join-Path $root "src-tauri\tauri.conf.json"
$cargoFile = Join-Path $root "src-tauri\Cargo.toml"
$pkgFile = Join-Path $root "package.json"

# Current version is read from tauri.conf.json.
$conf = Get-Content $confFile -Raw | ConvertFrom-Json
$current = [string]$conf.version
if ($current -notmatch '^\d+\.\d+\.\d+$') {
    throw "Unexpected version '$current' in tauri.conf.json (expected MAJOR.MINOR.PATCH)."
}

if ($Set) {
    if ($Set -notmatch '^\d+\.\d+\.\d+$') {
        throw "-Set value '$Set' must be MAJOR.MINOR.PATCH."
    }
    $new = $Set
}
else {
    $p = $current.Split('.') | ForEach-Object { [int]$_ }
    switch ($Part) {
        "major" { $p[0]++; $p[1] = 0; $p[2] = 0 }
        "minor" { $p[1]++; $p[2] = 0 }
        "patch" { $p[2]++ }
    }
    $new = "$($p[0]).$($p[1]).$($p[2])"
}

Write-Host "Version: $current -> $new"

# Targeted regex replacements preserve each file's formatting (no JSON reflow).
# tauri.conf.json: the single top-level "version".
$confText = Get-Content $confFile -Raw
$confText = [regex]::new('("version"\s*:\s*")\d+\.\d+\.\d+(")').Replace($confText, "`${1}$new`${2}", 1)
Set-Content $confFile $confText -NoNewline

# package.json: the single top-level "version".
$pkgText = Get-Content $pkgFile -Raw
$pkgText = [regex]::new('("version"\s*:\s*")\d+\.\d+\.\d+(")').Replace($pkgText, "`${1}$new`${2}", 1)
Set-Content $pkgFile $pkgText -NoNewline

# Cargo.toml: the first line-anchored `version = "x.y.z"` is the [package] one;
# dependency versions are inline (not line-anchored) and use 1-2 part numbers.
$cargoText = Get-Content $cargoFile -Raw
$cargoText = [regex]::new('(?m)^(version\s*=\s*")\d+\.\d+\.\d+(")').Replace($cargoText, "`${1}$new`${2}", 1)
Set-Content $cargoFile $cargoText -NoNewline

# Emit the new version on the last line so build scripts can capture it.
Write-Output $new
