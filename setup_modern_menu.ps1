# Publishes both projects and registers the sparse package that puts
# "Open as sticker" in the TOP-LEVEL Windows 11 right-click menu.
#
# Requires Developer Mode (Settings > System > For developers) for the
# unsigned loose-package registration.
#
#   .\setup_modern_menu.ps1              build + register
#   .\setup_modern_menu.ps1 -Uninstall   remove the package

param([switch]$Uninstall)

$pkgName = "Zophie.Sticker"
$root = $PSScriptRoot
$publish = Join-Path $root "publish"

if ($Uninstall) {
    $pkg = Get-AppxPackage -Name $pkgName -ErrorAction SilentlyContinue
    if ($pkg) {
        $pkg | Remove-AppxPackage
        Write-Host "Removed '$pkgName'. (The classic-menu registry verb, if installed, is unaffected.)"
    } else {
        Write-Host "Package not registered."
    }
    exit 0
}

Write-Host "Publishing StickerApp..."
dotnet publish "$root\StickerApp\StickerApp.csproj" -c Release -o $publish
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "Publishing StickerShell (Explorer command)..."
dotnet publish "$root\StickerShell\StickerShell.csproj" -c Release -o $publish
if ($LASTEXITCODE -ne 0) { exit 1 }

if (-not (Test-Path "$publish\StickerShell.comhost.dll")) {
    Write-Error "StickerShell.comhost.dll missing from publish output - COM hosting didn't emit."
    exit 1
}

Copy-Item "$root\StickerShell\AppxManifest.xml" $publish -Force
Copy-Item "$root\StickerApp\app.png" $publish -Force

# Re-register (registration pins the manifest in place; refresh after rebuilds)
$existing = Get-AppxPackage -Name $pkgName -ErrorAction SilentlyContinue
if ($existing) { $existing | Remove-AppxPackage }

Write-Host "Registering sparse package (needs Developer Mode)..."
try {
    Add-AppxPackage -Register "$publish\AppxManifest.xml" -ErrorAction Stop
} catch {
    Write-Error ("Registration failed: $_`n" +
        "Most common cause: Developer Mode is off. Enable it under " +
        "Settings > System > For developers, then re-run.")
    exit 1
}

Write-Host ""
Write-Host "Done. 'Open as sticker' is now in the top-level right-click menu for images."
Write-Host "If it doesn't appear immediately, restart Explorer:"
Write-Host "    taskkill /f /im explorer.exe; start explorer.exe"
Write-Host ""
Write-Host "Note: the package points at $publish - re-run this script after rebuilds,"
Write-Host "and don't delete that folder while registered."
