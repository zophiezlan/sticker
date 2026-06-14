# ✂️ Sticker

**Turn any image into a floating desktop sticker — background magically removed, in one right-click.**

> Right-click a photo in Explorer → _Open as sticker_ → boom, the subject peels off and hovers on your desktop. Drag it anywhere. It clicks through the transparent parts. It survives reboots. It's just… fun.

---

## ✨ Why Sticker?

|                                |                                                                                                                     |
| ------------------------------ | ------------------------------------------------------------------------------------------------------------------- |
| 🖱️ **One click**               | Right-click any image → it's a sticker. No extra apps, no export steps.                                             |
| 🧠 **AI-powered**              | Background removal runs 100% locally on your GPU (or CPU). No uploads, no API keys, no internet needed after setup. |
| 👻 **Click-through**           | Transparent areas are truly transparent — your mouse passes right through to whatever's underneath.                 |
| 💾 **Remembers everything**    | Close your laptop, reboot, whatever — your stickers come back exactly where you left them.                          |
| ⚡ **Instant after first use** | The AI model stays warm in a tiny tray app. Second sticker onwards? Sub-second.                                     |

---

## 🚀 Get Started

### Install the app (recommended)

Grab the installer from [the latest release](https://github.com/zophiezlan/sticker/releases/latest).

It's a per-user install (no admin needed) and adds **"Open as sticker"** to your right-click menu automatically — under **"Show more options"** on Windows 11.

> 🪟 **Why "Show more options" and not the top-level menu?** Putting an entry in the Win11 top-level menu requires a *signed* app package (MSIX). Sticker isn't code-signed yet, so the installer registers the older "classic" menu verb instead — which Win11 tucks under "Show more options". It works identically, just one extra click. If you want the top-level entry, [build from source](#build-from-source) with `setup_modern_menu.ps1` (it registers an unsigned package, which needs Developer Mode). See [Context menu placement](#context-menu-placement) for the full story.

> 📦 **winget — coming soon.** The package is working its way into the official winget repo via [microsoft/winget-pkgs#387213](https://github.com/microsoft/winget-pkgs/pull/387213) (in progress). Once that PR merges, you'll be able to install with:
>
> ```powershell
> winget install Zophie.Sticker
> ```
>
> Until then, use the release installer above.

> ⚠️ **"Windows protected your PC"?** Sticker isn't code-signed yet — it's a solo project and signing certificates are pricey. Windows **SmartScreen** warns on _any_ new unsigned app regardless of what it does, so this is expected and harmless. Click **More info → Run anyway**. Every release is built in the open by [GitHub Actions](https://github.com/zophiezlan/sticker/actions) straight from this source, so you can verify exactly what's in it — and the prompt fades as more people install. (This is a reputation prompt, not a virus warning; Windows Defender is happy with it.)

<a name="build-from-source"></a>### Build from source

**You'll need:** Windows 11 • [.NET 10 SDK](https://dotnet.microsoft.com/download) • Developer Mode turned on (Settings → System → For developers)

Then just run:

```powershell
.\setup_modern_menu.ps1
```

That's it! You'll see **"Open as sticker"** in your right-click menu on any image file.

> 💡 **No Developer Mode?** No problem — use the classic menu fallback instead:
>
> ```powershell
> dotnet publish StickerApp\StickerApp.csproj -c Release
> .\install_context_menu.ps1
> ```
>
> The entry will appear under "Show more options" in the context menu.

The first time you create a sticker, the AI model downloads automatically (~180 MB, one-time). After that, everything is instant and fully offline.

To uninstall, run `.\setup_modern_menu.ps1 -Uninstall`.

---

## 🎮 Using Stickers

Your cursor needs to be over the **visible subject** (transparent areas pass clicks through). Here's what you can do:

|     | Action             | What it does                              |
| --- | ------------------ | ----------------------------------------- |
| 🖱️  | Drag               | Move the sticker around                   |
| 🔍  | Scroll / `+` `-`   | Resize (hold Ctrl for fine-tuning)        |
| 🌫️  | Shift + scroll     | Adjust opacity                            |
| 🔄  | `R` / Shift+`R`    | Rotate 15°                                |
| ↔️  | `F`                | Flip horizontal                           |
| 👁️  | `D` / double-click | Toggle between cutout and original        |
| 💾  | `S`                | Save cutout as PNG                        |
| ❌  | Esc / middle-click | Close sticker                             |
| 📋  | Right-click        | Full menu (pin, always-on-top, re-matte…) |

### Tray Menu

Sticker lives in your system tray with quick access to:

- **Paste as sticker** (`Ctrl+Alt+V`) — screenshot something, hit the hotkey, instant sticker
- **Open images…** — pick files manually
- **Restore last session** — bring back all your stickers from last time
- **Clear matte cache…** — free up disk by deleting cached cutouts (open stickers and downloaded models are untouched)
- **Start with Windows** — your stickers are always there when you log in

### Command Line

```
Sticker.exe photo.jpg [more-images...]
Sticker.exe --no-matte logo.png          # already transparent, skip AI
Sticker.exe --resume                      # reopen last session
Sticker.exe --model birefnet-general img.jpg
```

---

## 🧠 AI Models

Not every background removal is perfect on the first try — so Sticker ships with three models you can swap between from the right-click menu (**"Matte with"**):

| Model               | Best for             | Speed     | Size    |
| ------------------- | -------------------- | --------- | ------- |
| `isnet-general-use` | Everything (default) | ⚡ Fast   | ~180 MB |
| `u2net_human_seg`   | People & portraits   | ⚡ Fast   | ~180 MB |
| `birefnet-general`  | Maximum quality      | 🐢 Slower | ~900 MB |

Each model's result is cached separately (`~/.sticker_cache`, keyed by image + model). **Switching to a model you've already run loads instantly from that cache** — no re-inference; a model you haven't tried yet runs once, then it's cached too. If you ever want a genuinely fresh pass, use **"Re-process current (ignore cache)."** Set a different default model with `--model` or the `STICKER_MODEL` environment variable.

> 🐢 **BiRefNet is heavy.** It runs at 1024×1024 through a transformer backbone, so peak memory is several GB and it's much slower than the others. Two things can make it fail with an ONNX Runtime "out of memory" / "Failed to allocate" error even on a capable card:
>
> 1. **DirectML is selecting the wrong GPU.** This is the most common cause on machines with switchable graphics — see [GPU selection](#gpu-selection-hybrid-graphics) in Troubleshooting. Fix that first; it's usually the whole problem.
> 2. **DirectML genuinely overflows VRAM.** DML's memory planning is less efficient than CUDA, so BiRefNet can need more than its nominal footprint. If you've confirmed the right GPU and it still won't fit, you can shrink the input resolution with `STICKER_BIREFNET_SIZE=768` (or `512`) to trade some edge detail for a smaller footprint, or accept the **retry-on-CPU** prompt (same result, just slower). `STICKER_FORCE_CPU=1` always skips the GPU.
>
> The lighter `isnet`/`u2net` models run at 320–1024 px and rarely hit any of this.

---

## 🏗️ How It Works

```
sticker/
├── StickerApp/       → WPF tray app (the main show)
├── StickerShell/     → Explorer right-click menu integration
└── prototype/        → Original Python version (still works!)
```

**StickerApp** is a WPF app where each sticker is a borderless, transparent window with per-pixel alpha hit-testing — your clicks pass through transparent pixels for free. Background removal uses ONNX Runtime with DirectML (GPU acceleration on any DX12 graphics card, automatic CPU fallback). A single tray process keeps the model warm so subsequent stickers open instantly.

**StickerShell** is a COM server (`IExplorerCommand`) that puts "Open as sticker" in the top-level Windows 11 context menu — no "Show more options" submenu needed. Registered via a sparse package.

**prototype/** is the original Python version (rembg + PyQt6). It shares the model folder (`~/.u2net`), matte cache (`~/.sticker_cache`), and session file with the C# app — useful as a test bench or if you prefer Python.

---

## 🛠️ Troubleshooting

### Common fixes

| Problem                               | Fix                                                                                                                 |
| ------------------------------------- | ------------------------------------------------------------------------------------------------------------------- |
| **NU1100 on first build**             | Your SDK is missing the NuGet feed. Run: `dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org` |
| **Package registration fails**        | Developer Mode is off. Turn it on in Settings → System → For developers.                                            |
| **Menu entry does nothing**           | Set env var `STICKER_SHELL_LOG=1`, restart Explorer, try again, then check `%TEMP%\sticker-shell.log`               |
| **No top-level entry, only "Show more options"** | Expected for the `.exe`/winget install — see [Context menu placement](#context-menu-placement) below                |
| **Menu entry vanished after rebuild** | Re-run `.\setup_modern_menu.ps1` — the registration points at `publish\`, so don't move that folder                 |
| **Stickers die when terminal closes** | Don't use `dotnet run` — use the published exe (or launch via the context menu)                                     |
| **Model download stalls / corrupt**   | Delete the offending `.onnx` in `%USERPROFILE%\.u2net` and re-create a sticker to re-download                       |
| **Cutout looks stale after re-matte** | Use the tray's **"Clear matte cache…"** (deletes only cached cutouts, keeps your session + models). Or use **"Re-process current (ignore cache)"** on the sticker itself. |
| **Menu only shows for some image types** | Old versions registered the classic menu under the `image` PerceivedType, which `.webp` (and others) often lack. Re-run `install_context_menu.ps1` / reinstall — it now registers per-extension (`.jpg .jpeg .png .webp .bmp .gif`). |
| **BiRefNet "out of memory" / "Failed to allocate"** | Usually DirectML on the **wrong GPU** — see [GPU selection](#gpu-selection-hybrid-graphics). If the right GPU still won't fit: `STICKER_BIREFNET_SIZE=768`, take the **CPU retry** prompt, or `STICKER_FORCE_CPU=1`. |
| **Heavy model fails, then lighter models fail too** | Fixed in current builds — a failed session used to hold its VRAM. Update; if on an old build, restart Sticker to clear it.            |
| **"Start with Windows" won't stick**  | Check the shortcut exists — see [autostart](#autostart) below                                                       |

<a name="context-menu-placement"></a>### Context menu placement (top-level vs. "Show more options")

Sticker can install its right-click entry two different ways, and which one you get depends on how you installed:

| Install method                              | Where "Open as sticker" appears        | How it's registered                                  |
| ------------------------------------------- | -------------------------------------- | ---------------------------------------------------- |
| **`.exe` installer / winget**               | Under **"Show more options"** (classic) | A plain `HKCU` registry verb                          |
| **`setup_modern_menu.ps1`** (build from source) | **Top-level** Win11 menu                | An unsigned sparse app package (needs Developer Mode) |
| **`install_context_menu.ps1`** (classic fallback) | Under **"Show more options"** (classic) | A plain `HKCU` registry verb                          |

**Why the difference?** Windows 11 only lets an entry into the *top-level* context menu if it comes from an app package that implements `IExplorerCommand` (Microsoft deliberately closed the top-level menu to the old registry verbs to stop the Win10 clutter). That package has to be **code-signed** to install normally — an *unsigned* package only registers via `Add-AppxPackage -Register` with **Developer Mode** on, which is a developer convenience, not something you can ship to end users.

Sticker isn't code-signed yet (solo project, certs are pricey), so:

- The shippable installers (the `.exe`, and winget once [#387213](https://github.com/microsoft/winget-pkgs/pull/387213) merges) fall back to the **classic** registry verb → lands under "Show more options". Installs cleanly for everyone, no Developer Mode needed.
- The **top-level** menu currently only works via `setup_modern_menu.ps1`, which registers the unsigned package locally and therefore needs Developer Mode.

This is a limitation of *not being signed*, not a deliberate choice — both menus launch the exact same app. If/when Sticker ships a signed MSIX (e.g. via the Microsoft Store, which signs packages for you), the top-level menu can be the default everywhere.

If you've installed the `.exe` and want the top-level entry too, you can run `setup_modern_menu.ps1` alongside it; the two registrations are independent (you'd then see both entries until you remove one).

<a name="gpu-selection-hybrid-graphics"></a>### GPU selection (hybrid graphics)

Sticker **auto-selects the highest-VRAM adapter** for DirectML, so hybrid-graphics machines should use the discrete GPU without any setup. (Earlier builds blindly used DXGI adapter 0 — and *whichever GPU drives your primary display is normally adapter 0* — so on switchable-graphics systems they'd land on a low-memory integrated GPU and heavy models would OOM while the real card sat idle. That's now handled automatically.)

If a heavy model still fails with "out of memory" while your GPU clearly has free VRAM, the auto-pick may have chosen wrong — here's how to take control:

**Force a specific adapter** with `STICKER_DML_DEVICE` (the DXGI adapter index). Find the index of the GPU you want and set it explicitly — try `1` if `0` is the iGPU:

```powershell
[Environment]::SetEnvironmentVariable('STICKER_DML_DEVICE','1','User')
```

Then fully quit Sticker from the tray and relaunch. If `1` is also wrong, try `2`, etc. To clear it: set the value to `$null`.

To check what you're working with, see your adapters and their dedicated VRAM:

```powershell
Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\*' -EA SilentlyContinue |
  Where-Object { $_.'HardwareInformation.qwMemorySize' } |
  ForEach-Object { '{0} — {1:N0} GB' -f $_.DriverDesc, ($_.'HardwareInformation.qwMemorySize'/1GB) }
```

> Note the adapter index can change when your display setup changes. For example, after moving a monitor onto the dGPU, the dGPU becomes adapter 0 — so a previously-needed `STICKER_DML_DEVICE=1` would then point *back* at the iGPU. Clear the variable once the dGPU is your primary display.

### Environment variables

| Variable               | Effect                                                                                          |
| ---------------------- | ----------------------------------------------------------------------------------------------- |
| `STICKER_MODEL`        | Default matting model for new stickers (e.g. `birefnet-general`). Same as `--model`.            |
| `STICKER_DML_DEVICE`   | **Override** the auto-selected DirectML adapter (DXGI index). Normally unnecessary — Sticker picks the highest-VRAM GPU automatically. |
| `STICKER_FORCE_CPU`    | `1` = skip the GPU entirely and run matting on the CPU (slow, but unlimited memory).            |
| `STICKER_BIREFNET_SIZE`| BiRefNet input resolution (e.g. `768`, `512`); lower = less memory, less edge detail. Default 1024. |
| `U2NET_HOME`           | Override the model download folder (default `%USERPROFILE%\.u2net`).                            |
| `STICKER_SHELL_LOG`    | `1` = write Explorer context-menu diagnostics to `%TEMP%\sticker-shell.log`.                    |

### Where Sticker keeps its files

Everything is per-user, under your profile folder — nothing is written to `Program Files` or `HKLM`. Paste these straight into Explorer's address bar:

| What                  | Location                                  | Notes                                                              |
| --------------------- | ----------------------------------------- | ------------------------------------------------------------------ |
| **AI models**         | `%USERPROFILE%\.u2net`                    | One `.onnx` per model. Override the folder with the `U2NET_HOME` env var. Safe to delete — re-downloads on next use. |
| **Matte cache**       | `%USERPROFILE%\.sticker_cache`            | Cached cutout PNGs, keyed per image+model. Prefer the tray's **"Clear matte cache…"** over deleting by hand — the same folder also holds your session and clipboard captures. |
| **Session**           | `%USERPROFILE%\.sticker_cache\session.json` | Positions/sizes of your open stickers — what `--resume` restores. Shared with the Python prototype. (Don't delete the whole `.sticker_cache` folder to clear cutouts — you'd lose this.) |
| **Shell debug log**   | `%TEMP%\sticker-shell.log`                | Only written when `STICKER_SHELL_LOG=1` is set.                    |

### Registry entries

Sticker touches very little of the registry, and everything lives under **`HKEY_CURRENT_USER`** (no admin, no machine-wide keys). Open `regedit` and paste a path into its address bar to inspect.

**Classic context-menu verb** (only present if you used `install_context_menu.ps1` / the classic fallback):

```
HKCU\Software\Classes\SystemFileAssociations\image\shell\OpenAsSticker
HKCU\Software\Classes\SystemFileAssociations\image\shell\OpenAsSticker\command
```

The `\command` subkey holds the exact launch line (`"...\Sticker.exe" "%1"`) — handy for confirming the menu points at the right executable. Delete the `OpenAsSticker` key to remove the classic verb by hand.

**Modern (Windows 11) context menu** is *not* a plain registry verb — it's a sparse MSIX package registering an `IExplorerCommand` COM server. So you won't find it under `shell\`. Instead:

- List it: `Get-AppxPackage *Sticker*` in PowerShell
- Remove it: `.\setup_modern_menu.ps1 -Uninstall` (or `Remove-AppxPackage <PackageFullName>`)
- The COM registration is owned by the package and disappears with it — don't hand-edit it.

<a name="autostart"></a>**Autostart** is deliberately *not* a `...\CurrentVersion\Run` key (that location is malware's favourite, so Defender scores writes to it harshly). Instead it's a plain shortcut in your Startup folder:

```
%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\Sticker.lnk
```

The shortcut launches `Sticker.exe --resume`. To toggle it: use the tray menu's **"Start with Windows"**, or just create/delete that `.lnk` yourself (type `shell:startup` in Explorer to open the folder).

### Deeper diagnostics

- **Right-click menu misbehaving?** Set the user env var `STICKER_SHELL_LOG=1`, restart Explorer (`taskkill /f /im explorer.exe & start explorer.exe`), reproduce, then read `%TEMP%\sticker-shell.log` — it traces each `IExplorerCommand` call.
- **Verify the modern package is actually registered:** `Get-AppxPackage *Sticker* | Format-List Name, PackageFullName, InstallLocation`. If `InstallLocation` no longer exists (e.g. you moved/deleted `publish\`), the menu silently breaks — re-run `setup_modern_menu.ps1`.
- **GPU vs CPU matting:** Sticker uses ONNX Runtime with DirectML and falls back to CPU automatically. If matting is unexpectedly slow, your GPU path may be falling back — check the tray app's console/output, and confirm a DX12-capable GPU and current drivers.
- **Force a model from scratch:** delete its `.onnx` from `%USERPROFILE%\.u2net` *and* clear `%USERPROFILE%\.sticker_cache` so no stale cutouts are served.
- **Full reset (nuke everything user-side):** uninstall the app, then delete `%USERPROFILE%\.u2net`, `%USERPROFILE%\.sticker_cache`, the `Sticker.lnk` startup shortcut, and (if you used the classic menu) the `HKCU\...\OpenAsSticker` registry key.

---

## 📄 License

[MIT](LICENSE) — © 2026 Zophie
