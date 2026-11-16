# Troubleshooting and settings

The most common fixes are in the [README](../README.md). This page covers the rest.

## <a name="gpu-selection"></a>GPU selection

Sticker runs matting on the GPU with the most dedicated memory, so laptops with both integrated and dedicated graphics should use the dedicated one automatically.

If a heavy model still runs out of memory while your GPU clearly has room, force a specific GPU with `STICKER_DML_DEVICE` (the adapter index — try `1` if `0` is the integrated GPU):

```powershell
[Environment]::SetEnvironmentVariable('STICKER_DML_DEVICE','1','User')
```

Quit Sticker from the tray and relaunch. To clear it, set the value to `$null`. The index can change when your display setup changes, so clear it once it's no longer needed.

To see your GPUs and their memory:

```powershell
Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\*' -EA SilentlyContinue |
  Where-Object { $_.'HardwareInformation.qwMemorySize' } |
  ForEach-Object { '{0} — {1:N0} GB' -f $_.DriverDesc, ($_.'HardwareInformation.qwMemorySize'/1GB) }
```

**BiRefNet still won't fit?** Both BiRefNet models run at 1024×1024 and DirectML can need several GB more than you'd expect — on an 8 GB card neither fits. Accept the CPU retry when offered, or set `STICKER_FORCE_CPU=1` to always use the CPU.

## Environment variables

| Variable                | Effect                                                                                                    |
| ----------------------- | --------------------------------------------------------------------------------------------------------- |
| `STICKER_MODEL`         | Default model for new stickers (e.g. `birefnet-general`). Same as `--model`.                              |
| `STICKER_DML_DEVICE`    | Force a GPU by adapter index (see [GPU selection](#gpu-selection)). Normally unnecessary.                 |
| `STICKER_FORCE_CPU`     | `1` = always run matting on the CPU (slower, but not limited by GPU memory).                              |
| `STICKER_SESSION_IDLE_SECS` | Seconds before an idle model is unloaded to free memory (default 180; `0` keeps it loaded).           |
| `STICKER_BIREFNET_SIZE` | BiRefNet input size, for custom exports only — the standard downloads accept just 1024.                   |
| `U2NET_HOME`            | Model download folder (default `%USERPROFILE%\.u2net`).                                                   |
| `STICKER_SHELL_LOG`     | `1` = log right-click menu activity to `%TEMP%\sticker-shell.log`.                                       |

## Where Sticker keeps its files

Everything is per-user — nothing is written to `Program Files` or machine-wide registry keys.

| What            | Location                                    | Notes                                                                                     |
| --------------- | ------------------------------------------- | ----------------------------------------------------------------------------------------- |
| **AI models**   | `%USERPROFILE%\.u2net`                      | One `.onnx` per model. Safe to delete — re-downloads on next use.                         |
| **Cutouts**     | `%USERPROFILE%\.sticker_cache`              | Cached cutouts per image and model. Use the tray's **Clear matte cache…** to empty it.    |
| **Session**     | `%USERPROFILE%\.sticker_cache\session.json` | Your open stickers, restored by `--resume`. Deleting the whole folder loses this too.     |
| **Pasted images** | `%USERPROFILE%\.sticker_cache\clipboard`  | Images from **Paste as sticker**; old ones are pruned automatically.                      |

## Registry entries

Everything lives under `HKEY_CURRENT_USER`.

**Classic right-click menu** (installer, winget, or `install_context_menu.ps1`) — one key per image type:

```
HKCU\Software\Classes\SystemFileAssociations\.jpg\shell\OpenAsSticker
HKCU\Software\Classes\SystemFileAssociations\.png\shell\OpenAsSticker
…and the same for .jpeg, .webp, .bmp, .gif
```

Each has a `\command` subkey with the exact launch line, handy for checking the menu points at the right `Sticker.exe`.

**Top-level Windows 11 menu** (`setup_modern_menu.ps1`) is an app package, not a registry verb. List it with `Get-AppxPackage *Sticker*`; remove it with `.\setup_modern_menu.ps1 -Uninstall`.

## <a name="autostart"></a>Autostart

"Start with Windows" is a shortcut in your Startup folder, not a registry Run key:

```
%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\Sticker.lnk
```

It launches `Sticker.exe --resume`. Toggle it from the tray, or create/delete the shortcut yourself (type `shell:startup` in Explorer's address bar).

## <a name="context-menu-diagnostics"></a>Context menu diagnostics

If the top-level "Open as sticker" entry does nothing:

1. Set the user environment variable `STICKER_SHELL_LOG=1`.
2. Restart Explorer: `taskkill /f /im explorer.exe & start explorer.exe`
3. Try the menu again, then read `%TEMP%\sticker-shell.log`.

Also check the package still points somewhere real: `Get-AppxPackage *Sticker* | Format-List Name, InstallLocation`. If `InstallLocation` no longer exists, re-run `setup_modern_menu.ps1`.

## <a name="full-reset"></a>Full reset

Uninstall the app, then delete:

- `%USERPROFILE%\.u2net` (models)
- `%USERPROFILE%\.sticker_cache` (cutouts, session, pasted images)
- the `Sticker.lnk` startup shortcut, if present
- the `OpenAsSticker` registry keys above, if you used `install_context_menu.ps1` and didn't run it with `-Uninstall`
