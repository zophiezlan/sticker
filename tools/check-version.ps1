#!/usr/bin/env pwsh
# Fails if the product version drifts across the three places that must agree:
#   - StickerApp/StickerApp.csproj   <Version>
#   - installer/Sticker.iss          #define AppVersion
#   - StickerShell/AppxManifest.xml  Identity Version (4-part; normalized to 3 here)
#
# winget/ is intentionally NOT checked — it tracks the last *published* release and
# is documented to trail the newest build (see README).
#
# Run locally:  pwsh ./tools/check-version.ps1   (also wired into CI)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
function Fail($msg) { Write-Error $msg; exit 1 }

$csprojPath   = Join-Path $root 'StickerApp/StickerApp.csproj'
$issPath      = Join-Path $root 'installer/Sticker.iss'
$manifestPath = Join-Path $root 'StickerShell/AppxManifest.xml'

# csproj <Version>
$csproj = ([xml](Get-Content $csprojPath)).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $csproj) { Fail "No <Version> found in $csprojPath" }

# Inno Setup #define AppVersion "x.y.z"
if ((Get-Content $issPath -Raw) -notmatch '(?m)^\s*#define\s+AppVersion\s+"([^"]+)"') {
    Fail "No '#define AppVersion' found in $issPath"
}
$iss = $Matches[1]

# AppxManifest Identity Version="x.y.z.w" -> normalize to x.y.z
if ((Get-Content $manifestPath -Raw) -notmatch '<Identity\b[^>]*\bVersion="([0-9.]+)"') {
    Fail "No Identity Version found in $manifestPath"
}
$manifest = (($Matches[1] -split '\.')[0..2]) -join '.'

$versions = [ordered]@{
    'StickerApp.csproj'         = $csproj
    'installer/Sticker.iss'     = $iss
    'AppxManifest.xml (3-part)' = $manifest
}
$versions.GetEnumerator() | ForEach-Object { Write-Host ("  {0,-28} {1}" -f $_.Key, $_.Value) }

if (($versions.Values | Select-Object -Unique).Count -ne 1) {
    Fail "Version drift detected — the values above disagree. Align them before release."
}
Write-Host "OK: all product versions agree on $csproj."
