<div align="center">
  
[![Sticker][repo_logo_img]][repo_url]
# Sticker
**Turn any image into a floating desktop sticker with the background magically removed**

![Latest release][release_shield]
![WinGet Package Version][winget_shield]
![CI Workflow Status][ci_status_shield]
![Release Workflow Status][release_status_shield]
![License][license_shield]
</div>

---

## ✨ Why Sticker?
|                                |                                                                               |
| ------------------------------ | ----------------------------------------------------------------------------- |
| 🖱️ **One click**               | Right-click any image → Open as sticker. No extra apps, no export steps.      |
| 🧠 **AI-powered**              | Background removal runs 100% locally on your GPU (or CPU). No uploads.        |
| 👻 **Click-through**           | Clicks pass straight through the transparent parts of a sticker.             |
| 💾 **Remembers everything**    | Reboot, close your laptop, whatever — stickers come back where you left them. |
| ⚡ **Instant after first use** | The AI model stays warm in a tiny tray app.                                   |

---

## 🚀 Get Started

```powershell
winget install Zophie.Sticker
```

Or grab the **[latest release][latest_release]** installer. Every release is built in the open by [GitHub Actions][actions] straight from this source.

Then right-click any image → **Open as sticker**. On Windows 11 it's under **Show more options** ([why?](docs/DEVELOPMENT.md#context-menu-placement)). The first sticker downloads the AI model once (~180 MB); after that everything works offline.

Want to build it yourself? See **[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)**.

---

## 🎮 Using Stickers

Your cursor needs to be over the **visible subject** (transparent areas pass clicks through).

|     | Action             | What it does                              |
| --- | ------------------ | ----------------------------------------- |
| 🖱️  | Drag               | Move the sticker around                   |
| ⬆️  | Arrow keys         | Nudge position 1px (Shift = bigger steps) |
| 🔍  | Scroll / `+` `-`   | Resize (hold Ctrl for fine-tuning)        |
| 🌫️  | Shift + scroll     | Adjust opacity                            |
| 🔄  | `R` / Shift+`R`    | Rotate 15°                                |
| ↔️  | `F`                | Flip horizontal                           |
| 👁️  | `D` / double-click | Toggle between cutout and original        |
| 💾  | `S`                | Save cutout as PNG                        |
| 📎  | `Ctrl+C`           | Copy cutout (keeps transparency)          |
| ❌  | Esc / middle-click | Remove sticker                            |
| 📋  | Right-click        | Full menu (pin, always-on-top, models…)   |

**Temporary stickers:** right-click → **Remove later** → in 1 hour, at end of today, in 1 day or in 1 week. The sticker removes itself when time's up, even across restarts.

**Pinning** makes a sticker click-through so it can't be moved by accident. Unpin it from the tray.

### Tray menu

- **Paste as sticker** (`Ctrl+Alt+V`) — screenshot something, hit the hotkey, instant sticker
- **Open images…** — pick files manually
- **Restore last session** — bring back your stickers from last time
- **Hide all stickers** (`Ctrl+Alt+H`) — clear the desktop for a screen share; press again to bring them back
- **Unpin sticker / Unpin all stickers** — release pinned stickers
- **Clear matte cache…** — free disk space used by cached cutouts
- **Start with Windows** — your stickers are there when you log in

### Command line

```
Sticker.exe photo.jpg [more-images...]
Sticker.exe --no-matte logo.png          # already transparent, skip AI
Sticker.exe --resume                      # reopen last session
Sticker.exe --model birefnet-general img.jpg
```

---

## 🧠 AI Models

Not every cutout is perfect first time, so you can switch models per sticker from right-click → **Change matte**:

| Model                   | Best for             | Speed     | Size    |
| ----------------------- | -------------------- | --------- | ------- |
| `isnet-general-use`     | Everything (default) | ⚡ Fast   | ~180 MB |
| `u2net_human_seg`       | People & portraits   | ⚡ Fast   | ~180 MB |
| `birefnet-general-lite` | Sharp edges, lighter | 🐇 Medium | ~225 MB |
| `birefnet-general`      | Maximum quality      | 🐢 Slower | ~900 MB |

Each model downloads on first use, and every result is cached, so switching back to a model you've already tried is instant. Change the default with `--model` or the `STICKER_MODEL` environment variable.

> 🐢 **BiRefNet needs a lot of GPU memory.** If it runs out, Sticker offers to retry on the CPU (same result, slower — BiRefNet Lite is the quicker of the two there). On laptops with two GPUs, see [GPU selection](docs/TROUBLESHOOTING.md#gpu-selection).

---

## 🛠️ Troubleshooting

| Problem                                          | Fix                                                                                                                         |
| ------------------------------------------------ | --------------------------------------------------------------------------------------------------------------------------- |
| **"Open as sticker" is missing from the menu**   | On Windows 11 it's under **Show more options** for the installer/winget version. Reinstalling re-registers it.              |
| **BiRefNet "out of memory"**                     | Accept the CPU retry, try BiRefNet Lite, or check [GPU selection](docs/TROUBLESHOOTING.md#gpu-selection).                   |
| **Model download failed or got corrupted**       | Delete the `.onnx` file in `%USERPROFILE%\.u2net` and open a sticker again — downloads are checked and re-fetched.           |
| **A cutout looks wrong or stale**                | Right-click → **Change matte** → **Re-process current**, or try another model.                                              |
| **"Start with Windows" won't stick**             | See [Autostart](docs/TROUBLESHOOTING.md#autostart).                                                                         |

More fixes, settings and where Sticker keeps its files: **[docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)**.

### Uninstalling

Uninstall from **Settings → Apps** (or `winget uninstall Zophie.Sticker`). Downloaded models and cached cutouts stay in your profile so a reinstall is instant — see [Full reset](docs/TROUBLESHOOTING.md#full-reset) to remove them too.

---

## 📄 License

[MIT](LICENSE) — © 2026 Zophie

[repo_logo_img]: https://github.com/zophiezlan/sticker/blob/main/StickerApp/app.png
[repo_url]: https://github.com/zophiezlan/sticker
[actions]: https://github.com/zophiezlan/sticker/actions
[latest_release]: https://github.com/zophiezlan/sticker/releases/latest
[release_shield]: https://img.shields.io/github/v/release/zophiezlan/sticker?label=Latest
[winget_shield]: https://img.shields.io/winget/v/Zophie.Sticker?label=Winget
[ci_status_shield]: https://img.shields.io/github/actions/workflow/status/zophiezlan/sticker/.github%2Fworkflows%2Fci.yml?label=CI
[release_status_shield]: https://img.shields.io/github/actions/workflow/status/zophiezlan/sticker/.github%2Fworkflows%2Frelease.yml?label=Release
[license_shield]: https://img.shields.io/github/license/zophiezlan/sticker
