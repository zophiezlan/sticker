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

# Sync the sparse package's Identity version with the app (csproj <Version>) so a
# rebuilt dev package isn't stuck at a stale hand-edited value. MSIX requires a
# 4-part Major.Minor.Build.Revision while csproj is 3-part, so append ".0". This
# patches the *copy* in publish\ only; the shipped release is the Inno .exe and
# never touches AppxManifest.xml, so this is purely dev-tooling hygiene.
$ver = ([xml](Get-Content "$root\StickerApp\StickerApp.csproj")).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if ($ver) {
    $manifestCopy = Join-Path $publish "AppxManifest.xml"
    $xml = [xml](Get-Content $manifestCopy)
    $xml.Package.Identity.Version = "$ver.0"
    $xml.Save($manifestCopy)
    Write-Host "Set sparse-package Identity version to $ver.0 (from csproj)."
}

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
