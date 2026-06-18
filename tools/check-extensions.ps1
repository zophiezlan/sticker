#!/usr/bin/env pwsh
# The set of image extensions Sticker opens is declared once in code
# (App.ImageExtensions) but must be mirrored in three install-time artifacts that
# register the classic/MSIX context-menu verbs per extension. This was the failure
# mode behind the original ".webp didn't show up" bug, so guard it: extract all four
# lists and fail if any drifts from the canonical one.
#
# Run locally:  pwsh ./tools/check-extensions.ps1   (also wired into CI)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
function Fail($msg) { Write-Error $msg; exit 1 }
function Norm($items) { ,@($items | ForEach-Object { $_.ToLowerInvariant() } | Sort-Object -Unique) }
function Exts($text, $pattern, $group = 0) {
    Norm ([regex]::Matches($text, $pattern) | ForEach-Object {
        if ($group -gt 0) { $_.Groups[$group].Value } else { $_.Value }
    })
}

# Canonical source of truth: the App.ImageExtensions array literal.
$appCs = Get-Content (Join-Path $root 'StickerApp/App.xaml.cs') -Raw
if ($appCs -notmatch 'ImageExtensions\s*=\s*\{([^}]*)\}') { Fail "Couldn't find App.ImageExtensions in App.xaml.cs" }
$canonical = Exts $Matches[1] '\.\w+'
if ($canonical.Count -eq 0) { Fail "App.ImageExtensions parsed as empty" }
Write-Host "Canonical (App.ImageExtensions): $($canonical -join ' ')"

$sources = [ordered]@{}

$ps1 = Get-Content (Join-Path $root 'install_context_menu.ps1') -Raw
if ($ps1 -notmatch '\$extensions\s*=\s*@\(([^)]*)\)') { Fail "Couldn't find `$extensions in install_context_menu.ps1" }
$sources['install_context_menu.ps1'] = Exts $Matches[1] '\.\w+'

$iss = Get-Content (Join-Path $root 'installer/Sticker.iss') -Raw
$sources['installer/Sticker.iss'] = Exts $iss 'SystemFileAssociations\\(\.\w+)\\' 1

$man = Get-Content (Join-Path $root 'StickerShell/AppxManifest.xml') -Raw
$sources['StickerShell/AppxManifest.xml'] = Exts $man 'ItemType Type="(\.\w+)"' 1

$ok = $true
foreach ($name in $sources.Keys) {
    $set = $sources[$name]
    if (($set -join ',') -ne ($canonical -join ',')) {
        $ok = $false
        Write-Host "MISMATCH  ${name}: $($set -join ' ')"
    } else {
        Write-Host "OK        ${name}: $($set -join ' ')"
    }
}
if (-not $ok) { Fail "Extension lists drift from App.ImageExtensions — align the flagged file(s)." }
Write-Host "OK: all four extension lists agree."
