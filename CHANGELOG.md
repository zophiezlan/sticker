# Changelog

All notable changes to Sticker are documented here.

## [1.0.4] — 2026-06-14

### Added

- **Model-download integrity.** Each downloaded model is verified against a
  pinned SHA-256 before it's installed, so a corrupted or substituted file is
  rejected and re-fetched instead of being loaded into the runtime.
- **Per-monitor DPI awareness (Per-Monitor V2).** Stickers stay sharp and
  hit-test correctly when dragged across monitors with different display
  scaling, instead of being bitmap-stretched.
- **Stickers remember their model across a restore.** A restored sticker comes
  back with the model it was matted with when that cutout is still cached
  (instant); otherwise it falls back to the default model, so `--resume` never
  kicks off a slow re-matte at login.

### Fixed

- **Matte no longer races session teardown.** All matting is serialized through
  one gate, so switching models, trimming GPU memory, or evicting an idle
  session can no longer dispose an ONNX session mid-inference (which could crash
  the app). Model load/download also no longer holds the session lock, so
  quitting during a first-run download won't hang.
- **Single-instance pipe is isolated per session**, with a current-user-only
  ACL and a backoff on the listener — a second login session on the same machine
  can no longer collide with it or spin the CPU.
- **Clipboard captures are pruned** instead of piling up forever under
  `~/.sticker_cache/clipboard`.
- **Session writes are debounced** off the UI thread, so rapid resize/opacity
  changes no longer rewrite `session.json` on every notch; the session is
  flushed synchronously on exit.
- **Crash resilience.** A stray UI-thread exception is now surfaced and survived
  instead of taking down the tray and every open sticker; a fatal background
  error still flushes the session first so nothing is lost.
- **Rotate menu labels** no longer advertise `R` / `Shift+R` next to the 90°
  items (those keys rotate 15°, the menu items 90°).
- **"Close all stickers" can't get stuck** suppressing session saves if a window
  throws while closing.

## [1.0.3] — 2026-06-14

### Added

- **Automatic GPU selection.** DirectML now targets the highest-VRAM adapter
  instead of blindly using DXGI adapter 0, so machines with switchable graphics
  (laptop iGPU + discrete GPU, or a desktop with the iGPU enabled) use the right
  card automatically. Override with `STICKER_DML_DEVICE` if needed.
- **CPU fallback for heavy models.** When a model runs out of GPU memory — during
  inference *or* while loading — Sticker first frees other warm GPU sessions and
  retries on the GPU, then offers to retry on the CPU (same result, slower) before
  giving up. `STICKER_FORCE_CPU=1` always skips the GPU.
- **`STICKER_BIREFNET_SIZE`** to lower BiRefNet's input resolution (e.g. `768`,
  `512`) and cut its memory footprint.
- **Model download progress.** The first-use download now streams with a live
  progress bar (MB / percent) instead of an opaque wait.
- **On-sticker busy spinner** shown while (re-)matting — on GPU and during the
  slower CPU pass — so a long run never looks like a frozen app.
- **"Clear matte cache…" tray item** that frees cached cutouts without touching
  your session, clipboard captures, or downloaded models.
- **Cache-aware model picker.** The right-click "Matte with" entries reuse a
  previously computed result instantly; "Re-process current (ignore cache)"
  forces a fresh pass.

### Changed

- **Context menu now covers all supported image types** (`.jpg .jpeg .png .webp
  .bmp .gif`). Previously it registered under the `image` PerceivedType, which
  `.webp` (and others) often lack — so it only appeared for `.jpg`. It's now
  registered per extension.
- README: rewritten GPU / troubleshooting guidance, a full environment-variable
  reference, and clearer context-menu and caching docs.

### Fixed

- **Atomic model downloads.** Downloads land via a temp file and rename, so an
  interrupted download no longer leaves a corrupt `.onnx` that crashes on every
  launch.
- **Failed matte no longer poisons later ones.** A model that errors mid-run is
  now disposed and evicted, freeing its reserved VRAM so subsequent re-mattes
  (even of other models) aren't starved.
- **Oversized images are rejected** with a clear message instead of overflowing
  the pixel-buffer allocation and crashing.
- **Concurrent re-mattes** of the same image+model are serialized — no more
  duplicate inference or racing writes to the same cache file.
- **Clearer clipboard errors.** "Paste as sticker" distinguishes a busy/locked
  clipboard from a genuinely empty one.
- **Session restore is resilient** to a malformed entry — it skips the bad one
  and restores the rest instead of dropping the whole session.

## [1.0.2] and earlier

See the Git history.
