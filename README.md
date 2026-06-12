# Sticker

Right-click any image in Explorer → **Open as sticker** → it floats on your desktop as a draggable cut-out with the background removed. Like pulling a sticker off the photo.

Background removal runs locally (ONNX, GPU via DirectML), clicks pass through the transparent parts to whatever's underneath, and stickers survive reboots.

## Features

- **Top-level Explorer context menu** entry on Windows 11 (plus classic-menu fallback)
- **Paste as sticker** — global `Ctrl+Alt+V` turns screenshots/copied images into stickers
- **Local AI matting** — ISNet by default; re-matte any sticker with U2Net-Human (portraits) or BiRefNet (highest quality) if the result isn't right
- **Pin mode** — make a sticker click-through, a pure overlay
- **Session persistence** — positions, sizes, rotation, opacity all restored via `--restore` or "Start with Windows"
- **Tray app** — single resident process, model stays warm, stickers open instantly
- Rotate, flip, opacity, original↔cutout toggle, save cutout as PNG

## Install

Prereqs: Windows 11, [.NET 10 SDK](https://dotnet.microsoft.com/download), Developer Mode (Settings → System → For developers — needed for the context-menu package).

```powershell
.\setup_modern_menu.ps1
```

That publishes both projects into `publish\` and registers the Explorer extension. Re-run it after any rebuild. Remove with `-Uninstall`.

No Developer Mode? Use the classic menu instead (entry lands under "Show more options"):

```powershell
dotnet publish StickerApp\StickerApp.csproj -c Release
.\install_context_menu.ps1
```

First sticker ever downloads the matting model (~180 MB) to `~/.u2net`.

## Controls

Cursor must be over the **subject** — transparent areas click through. If scrolling seems dead, use `+`/`-`.

| Action              | Effect                                                             |
| ------------------- | ------------------------------------------------------------------ |
| Drag                | Move                                                               |
| Scroll or `+` / `-` | Resize (Ctrl+scroll = fine)                                        |
| Shift+scroll        | Opacity                                                            |
| `R` / Shift+`R`     | Rotate 15°                                                         |
| `F`                 | Flip horizontal                                                    |
| `D` / double-click  | Toggle original ↔ cutout                                           |
| `S`                 | Save cutout as PNG                                                 |
| Esc / middle-click  | Close                                                              |
| Right-click         | Menu: pin, always-on-top, rotate 90°, re-matte with another model… |

**Tray menu:** Paste as sticker (`Ctrl+Alt+V`), Open images, Restore last session, Unpin all, Close all, Start with Windows, Exit.

**CLI:** `Sticker.exe img.jpg [...]` · `--no-matte` (image already transparent) · `--restore` · `--model <name>`

## Models

| Model               | Best for                  | Notes                  |
| ------------------- | ------------------------- | ---------------------- |
| `isnet-general-use` | everything (default)      | good edges, fast       |
| `u2net_human_seg`   | people/portraits          | often better on hair   |
| `birefnet-general`  | when quality matters most | ~900 MB download, slow |

Right-click a sticker → "Re-matte — …" to compare; each model's result is cached separately so switching back is instant. Default model: `--model` flag or `STICKER_MODEL` env var.

## How it works

- `StickerApp/` — WPF tray app. Each sticker is a borderless `AllowsTransparency` window (layered window → per-pixel alpha click-through for free). Matting via ONNX Runtime + DirectML, CPU fallback. Single instance via mutex + named pipe.
- `StickerShell/` — tiny `IExplorerCommand` COM server (C#, `EnableComHosting`) giving the top-level Win11 menu entry, registered through a loose sparse package (`AppxManifest.xml`).
- `prototype/` — the original Python version (rembg + PyQt6), kept as a test bench. It shares the model folder, matte cache, _and_ `session.json` with the C# app.
- Caches live in `~/.sticker_cache` (mattes keyed on path+mtime+model, `session.json`, clipboard pastes); models in `~/.u2net`.

## Troubleshooting

- **NU1100 on first build** — your SDK has no NuGet feed: `dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org`
- **Package registration fails** — Developer Mode is off.
- **Menu entry does nothing** — set user env var `STICKER_SHELL_LOG=1`, restart Explorer, click the entry, read `%TEMP%\sticker-shell.log`.
- **Menu entry missing after rebuild** — re-run `setup_modern_menu.ps1`; the registration pins `publish\`, so don't move/delete that folder.
- **Stickers died with the terminal** — you launched via `dotnet run`; use the published exe (`start` it, or the context menu).
