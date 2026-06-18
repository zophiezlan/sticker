# Adds "Open as sticker" to the right-click menu for all image files (current user only).
# Lands under "Show more options" on Win11 (the modern top-level menu needs a sparse MSIX package).
#
#   .\install_context_menu.ps1                  install (auto-detects published Sticker.exe, falls back to Python)
#   .\install_context_menu.ps1 -Exe <path>      install pointing at a specific Sticker.exe
#   .\install_context_menu.ps1 -Uninstall       remove

param(
    [switch]$Uninstall,
    [string]$Exe
)

# Register per-extension rather than under the "image" PerceivedType. PerceivedType
# is set inconsistently (e.g. .webp often lacks it, so the entry only showed for
# .jpg), so a per-extension verb is the reliable way to cover every type the app
# actually opens. Keep this list in sync with App.ImageExtensions in StickerApp.
$extensions = @(".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif")
$keys = $extensions | ForEach-Object {
    "HKCU:\Software\Classes\SystemFileAssociations\$_\shell\OpenAsSticker"
}

if ($Uninstall) {
    $removed = 0
    foreach ($k in $keys) {
        if (Test-Path $k) { Remove-Item $k -Recurse -Force; $removed++ }
    }
    # Also clean up the old single "image" PerceivedType key from prior versions.
    $legacy = "HKCU:\Software\Classes\SystemFileAssociations\image\shell\OpenAsSticker"
    if (Test-Path $legacy) { Remove-Item $legacy -Recurse -Force; $removed++ }
    Write-Host (($removed -gt 0) ? "Removed 'Open as sticker' from $removed location(s)." : "Nothing to remove.")
    exit 0
}

$cmd = $null
$icon = $null

# 1. Explicit -Exe wins
if ($Exe) {
    $target = (Resolve-Path $Exe -ErrorAction Stop).Path
    $cmd = "`"$target`" `"%1`""
    $icon = $target
}

# 2. Auto-detect a built/published C# Sticker.exe next to this script
if (-not $cmd) {
    $candidates = @(
        "$PSScriptRoot\StickerApp\bin\Release\net10.0-windows\publish\Sticker.exe",
        "$PSScriptRoot\StickerApp\bin\Release\net10.0-windows\Sticker.exe",
        "$PSScriptRoot\StickerApp\bin\Debug\net10.0-windows\Sticker.exe"
    )
    $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($found) {
        $cmd = "`"$found`" `"%1`""
        $icon = $found
    }
}

# 3. Fall back to the Python prototype
if (-not $cmd) {
    $script = Join-Path $PSScriptRoot "prototype\sticker.py"
    if (-not (Test-Path $script)) {
        Write-Error "No Sticker.exe found and sticker.py is missing. Build StickerApp or pass -Exe."
        exit 1
    }
    $python = (Get-Command pythonw.exe -ErrorAction SilentlyContinue).Source
    if (-not $python) { $python = (Get-Command python.exe -ErrorAction SilentlyContinue).Source }
    if (-not $python) {
        Write-Error "No Sticker.exe found and Python is not on PATH."
        exit 1
    }
    $cmd = "`"$python`" `"$script`" `"%1`""
    $icon = $python
}

# Drop the legacy single "image" key so we don't get a duplicate menu entry on
# types that *do* have PerceivedType=image (e.g. .jpg would otherwise show twice).
$legacy = "HKCU:\Software\Classes\SystemFileAssociations\image\shell\OpenAsSticker"
if (Test-Path $legacy) { Remove-Item $legacy -Recurse -Force }

foreach ($key in $keys) {
    New-Item -Path "$key\command" -Force | Out-Null
    Set-ItemProperty -Path $key -Name "(default)" -Value "Open as sticker"
    Set-ItemProperty -Path $key -Name "Icon" -Value $icon
    Set-ItemProperty -Path "$key\command" -Name "(default)" -Value $cmd
}

Write-Host "Installed for: $($extensions -join ', '). Right-click an image -> Show more options -> Open as sticker."
Write-Host "Command: $cmd"
