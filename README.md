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
- **Start with Windows** — your stickers are always there when you log in

### Command Line

```
Sticker.exe photo.jpg [more-images...]
Sticker.exe --no-matte logo.png          # already transparent, skip AI
Sticker.exe --restore                     # reopen last session
Sticker.exe --model birefnet-general img.jpg
```

---

## 🧠 AI Models

Not every background removal is perfect on the first try — so Sticker ships with three models you can swap between instantly (right-click → "Re-matte"):

| Model               | Best for             | Speed     | Size    |
| ------------------- | -------------------- | --------- | ------- |
| `isnet-general-use` | Everything (default) | ⚡ Fast   | ~180 MB |
| `u2net_human_seg`   | People & portraits   | ⚡ Fast   | ~180 MB |
| `birefnet-general`  | Maximum quality      | 🐢 Slower | ~900 MB |

Each model's result is cached separately — switching between them is instant after the first run. Set a different default with `--model` or the `STICKER_MODEL` environment variable.

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

| Problem                               | Fix                                                                                                                 |
| ------------------------------------- | ------------------------------------------------------------------------------------------------------------------- |
| **NU1100 on first build**             | Your SDK is missing the NuGet feed. Run: `dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org` |
| **Package registration fails**        | Developer Mode is off. Turn it on in Settings → System → For developers.                                            |
| **Menu entry does nothing**           | Set env var `STICKER_SHELL_LOG=1`, restart Explorer, try again, then check `%TEMP%\sticker-shell.log`               |
| **Menu entry vanished after rebuild** | Re-run `.\setup_modern_menu.ps1` — the registration points at `publish\`, so don't move that folder                 |
| **Stickers die when terminal closes** | Don't use `dotnet run` — use the published exe (or launch via the context menu)                                     |

---

## 📄 License

© 2026 Zophie
