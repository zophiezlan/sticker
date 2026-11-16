# Developing Sticker

## Build from source

**You'll need:** Windows 11 • [.NET 10 SDK](https://dotnet.microsoft.com/download) • Developer Mode (Settings → System → For developers) for the top-level menu

```powershell
.\setup_modern_menu.ps1
```

This publishes the app and registers **"Open as sticker"** in the top-level Windows 11 right-click menu. Remove it with `.\setup_modern_menu.ps1 -Uninstall`.

**No Developer Mode?** Use the classic menu instead (appears under "Show more options"):

```powershell
dotnet publish StickerApp\StickerApp.csproj -c Release
.\install_context_menu.ps1
```

Remove it with `.\install_context_menu.ps1 -Uninstall`.

## Tests and CI

```powershell
dotnet test Sticker.slnx
```

CI runs the same build and tests on every push and pull request, and checks that the version number and the supported image extensions agree across the csproj, installer and MSIX manifest (`tools/check-version.ps1`, `tools/check-extensions.ps1`).

Releases: bump the version in `StickerApp.csproj`, `installer/Sticker.iss` and `StickerShell/AppxManifest.xml`, date the changelog, then push a `vX.Y.Z` tag — `release.yml` builds the installer and publishes the GitHub release with its SHA-256. Then submit to winget (this also saves the manifests into `winget/`):

```powershell
wingetcreate update Zophie.Sticker --version X.Y.Z --urls 'https://github.com/zophiezlan/sticker/releases/download/vX.Y.Z/StickerSetup-X.Y.Z.exe|x64|user' --release-notes-url https://github.com/zophiezlan/sticker/releases/tag/vX.Y.Z --out winget --submit
```

The `|x64|user` suffix is needed because Inno Setup's bootstrapper is detected as x86. Check the generated installer manifest still has `ReleaseDate` before submitting — wingetcreate drops it, and winget's validator flags the omission. The `winget/` folder trails the newest build until that release is published.

## Project layout

```
sticker/
├── StickerApp/        → WPF tray app (the main show)
├── StickerApp.Tests/  → xunit tests for the pure helpers
├── StickerShell/      → Explorer right-click menu integration (IExplorerCommand)
├── installer/         → Inno Setup script for the .exe installer
├── winget/            → submitted winget manifests
├── tools/             → CI consistency checks
└── prototype/         → original Python version (still works!)
```

**StickerApp** — each sticker is a borderless, transparent WPF window, so clicks pass through transparent pixels for free. Background removal uses ONNX Runtime with DirectML (any DX12 GPU, automatic CPU fallback). A single tray process keeps the model warm so later stickers open instantly.

**StickerShell** — a COM server implementing `IExplorerCommand` that puts "Open as sticker" in the top-level Windows 11 menu, registered through a sparse app package.

**prototype/** — the original Python version (rembg + PyQt6). It shares the model folder (`~/.u2net`), cutout cache and `session.json` with the C# app.

## <a name="context-menu-placement"></a>Context menu placement

| Install method                                    | Where "Open as sticker" appears         | How it's registered                                   |
| ------------------------------------------------- | --------------------------------------- | ----------------------------------------------------- |
| **`.exe` installer / winget**                     | Under **"Show more options"** (classic) | A per-user registry verb                              |
| **`setup_modern_menu.ps1`** (build from source)   | **Top-level** Windows 11 menu           | An unsigned sparse app package (needs Developer Mode) |
| **`install_context_menu.ps1`** (classic fallback) | Under **"Show more options"** (classic) | A per-user registry verb                              |

Windows 11 only allows an entry in the *top-level* menu if it comes from an app package implementing `IExplorerCommand`, and that package has to be **code-signed** to install normally. An unsigned package only registers with Developer Mode on — fine for development, not something to ship. Sticker isn't code-signed yet, so the shippable installers use the classic verb. A signed MSIX (e.g. through the Microsoft Store) would make the top-level entry the default everywhere.

The two registrations are independent: you can run `setup_modern_menu.ps1` alongside the installed app to get the top-level entry too (you'll see both until you remove one).

## Build and setup problems

| Problem                                     | Fix                                                                                                         |
| ------------------------------------------- | ----------------------------------------------------------------------------------------------------------- |
| **NU1100 on first build**                   | The SDK is missing the NuGet feed: `dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org` |
| **Package registration fails**              | Developer Mode is off — turn it on in Settings → System → For developers.                                    |
| **Menu entry vanished after a rebuild**     | Re-run `.\setup_modern_menu.ps1`. The registration points at `publish\`, so don't move or delete that folder. |
| **Stickers die when the terminal closes**   | Don't use `dotnet run` — launch the published exe (or use the context menu).                                 |
| **Menu entry does nothing**                 | See [Context menu diagnostics](TROUBLESHOOTING.md#context-menu-diagnostics).                                 |
