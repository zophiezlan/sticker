using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace StickerApp;

public partial class App : Application
{
    // Per-session pipe name (the single-instance mutex below is session-local
    // too), so a second login session on the same machine gets its own instance
    // instead of fighting the first over the machine-global pipe namespace.
    private static readonly string PipeName =
        $"sticker-csharp-single-instance-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
    private const string MutexName = "StickerApp.SingleInstance";

    public static readonly List<StickerWindow> Stickers = new();
    private static Mutex? _mutex;
    private static bool _suppressSave;

    private readonly Dictionary<string, Matting> _mattings = new();

    // Warm sessions make back-to-back mattes fast, but a kept-warm session pins
    // its model + intra-op thread pool (and, for heavy models, a DirectML arena
    // that never shrinks) for as long as it lives. So after this long with no
    // matting activity, drop every warm session — the next matte just reloads.
    // Tunable via STICKER_SESSION_IDLE_SECS (0 keeps sessions warm forever).
    private static readonly TimeSpan IdleEvictAfter = ResolveIdleEvict();
    private System.Threading.Timer? _idleEvict;
    private int _matteInFlight;

    // Only one matte runs at a time. The session-lifecycle code (model-switch
    // eviction in GetMatting, TrimGpuSessions, idle/exit disposal) assumes no
    // InferenceSession.Run is in flight when it disposes a session — this gate
    // makes that true rather than merely hoped-for. Every matte acquires it;
    // anything that disposes a session must hold it too, OR try-acquire and skip
    // when a matte is running. (Not reentrant: code already under the gate, i.e.
    // inside a matte's Task.Run, must NOT re-acquire — see EvictMatting.)
    private static readonly SemaphoreSlim _matteGate = new(1, 1);

    private static TimeSpan ResolveIdleEvict()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("STICKER_SESSION_IDLE_SECS"), out int s))
            return s <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(s);
        return TimeSpan.FromMinutes(3);
    }

    /// <summary>Restart the idle clock after a matte; when it elapses, warm sessions are freed.</summary>
    private void BumpIdleTimer() =>
        _idleEvict?.Change(IdleEvictAfter, Timeout.InfiniteTimeSpan);

    /// <summary>
    /// Dispose every warm session. Fires on the idle timer (background thread) and on
    /// exit. If a matte is still running — a CPU birefnet pass can take minutes — skip
    /// and re-arm rather than dispose a session mid-Run, which would crash.
    /// </summary>
    private void DisposeWarmSessions(bool fromTimer = false)
    {
        if (fromTimer && Volatile.Read(ref _matteInFlight) > 0)
        {
            BumpIdleTimer();
            return;
        }
        // Never dispose a session mid-Run. Take the matte gate first; if a matte
        // holds it (a CPU birefnet pass can run for minutes), skip — the timer
        // re-arms and retries, and on exit the OS reclaims everything anyway.
        if (!_matteGate.Wait(0))
        {
            if (fromTimer)
                BumpIdleTimer();
            return;
        }
        try
        {
            lock (_mattings)
            {
                foreach (var m in _mattings.Values)
                    m.Dispose();
                _mattings.Clear();
            }
        }
        finally { _matteGate.Release(); }
    }

    private WinForms.NotifyIcon? _tray;
    private string _model =
        Environment.GetEnvironmentVariable("STICKER_MODEL") ?? Matting.DefaultModel;

    private static string MattingKey(string model, bool forceCpu) =>
        forceCpu ? model + "|cpu" : model;

    /// <summary>
    /// Heavy models (BiRefNet) run at 1024² and commit gigabytes; on DirectML the
    /// allocator's arena only grows and is never returned to the OS while the
    /// session lives. So heavy sessions are dropped as soon as a run finishes
    /// rather than kept warm — the light models (~180 MB) stay warm as before.
    /// </summary>
    private static bool IsHeavyModel(string model) =>
        model.StartsWith("birefnet", StringComparison.OrdinalIgnoreCase);

    private Matting GetMatting(string model, bool forceCpu = false)
    {
        // CPU and GPU sessions for the same model are cached separately so a
        // VRAM-driven CPU fallback doesn't evict the working GPU session.
        string key = MattingKey(model, forceCpu);
        lock (_mattings)
        {
            if (_mattings.TryGetValue(key, out var existing))
                return existing;

            // Cap warm GPU sessions at one. Stacking warm GPU sessions is what
            // drove runaway commit (each holds its model weights + a DirectML
            // arena that never shrinks), so before standing up a new GPU session,
            // dispose any other warm GPU sessions — and do it *before* loading the
            // new one so a heavy model gets the freed VRAM. CPU sessions (plain
            // RAM, used only for the VRAM fallback) are left untouched. _matteGate
            // serialises mattes, so nothing evicted here is one mid-Run.
            if (!forceCpu)
                foreach (var k in _mattings.Keys.Where(k => !k.EndsWith("|cpu")).ToList())
                    if (_mattings.Remove(k, out var old))
                        old.Dispose();
        }

        // Construct OUTSIDE the lock: a first-use download can take minutes and a
        // heavy model's VRAM load is slow — holding _mattings across that would
        // stall the idle timer, exit, and other lookups. _matteGate ensures only
        // one matte (hence one GetMatting) runs at a time, so there's no racing
        // constructor; _matteInFlight is already raised, so the idle timer defers.
        var created = new Matting(model, forceCpu);
        lock (_mattings)
        {
            _mattings[key] = created;
            return created;
        }
    }

    /// <summary>
    /// Drop a session and release its resources. A run that fails part-way
    /// (e.g. a heavy model OOM-ing on the GPU) leaves the ONNX Runtime session
    /// holding its weights and allocator arena — on DirectML that's reserved
    /// VRAM that never comes back while the session lives, which then starves
    /// *other* models' re-mattes. Evicting the failed session frees it so the
    /// next attempt — even of a different, lighter model — starts clean.
    /// Caller must already hold <see cref="_matteGate"/> (it's called from inside a
    /// matte's gated Task.Run) so it never frees a session another matte is running.
    /// </summary>
    private void EvictMatting(string model, bool forceCpu)
    {
        lock (_mattings)
        {
            if (_mattings.Remove(MattingKey(model, forceCpu), out var m))
                m.Dispose();
        }
    }

    /// <summary>
    /// Dispose all warm GPU sessions, freeing their VRAM. Kept-warm sessions make
    /// switching models fast, but they accumulate — a heavy model (BiRefNet) can
    /// then fail to even *load* onto the GPU because the lighter ones are still
    /// resident. Called as a last resort before a CPU fallback so the heavy model
    /// gets a clean GPU to itself. CPU sessions (RAM, not VRAM) are left alone.
    /// </summary>
    public static void TrimGpuSessions()
    {
        var app = (App)Current;
        // Best-effort VRAM reclaim before a CPU fallback. Only dispose when no
        // matte is running (gate free) — otherwise we'd risk freeing a live
        // session. The caller (RematteWithFallback) invokes this between gated
        // attempts, so the gate is normally free here.
        if (!_matteGate.Wait(0))
            return;
        try
        {
            lock (app._mattings)
            {
                foreach (var key in app._mattings.Keys.Where(k => !k.EndsWith("|cpu")).ToList())
                    if (app._mattings.Remove(key, out var m))
                        m.Dispose();
            }
        }
        finally { _matteGate.Release(); }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A stray UI-thread exception shouldn't take the tray (and every open
        // sticker) down with it — surface it and stay running. On a fatal
        // background-thread crash we can't keep running, but we can at least try
        // to persist the session so stickers come back on relaunch.
        DispatcherUnhandledException += (_, ex) =>
        {
            try
            {
                _tray?.ShowBalloonTip(5000, "Sticker",
                    "Something went wrong, but Sticker is still running.\n" + ex.Exception.Message,
                    WinForms.ToolTipIcon.Warning);
            }
            catch { /* notification is best-effort */ }
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, _) =>
        {
            try { FlushSessionNow(); } catch { /* last-gasp; swallow */ }
        };

        var args = e.Args.ToList();
        bool noMatte = args.Remove("--no-matte");
        bool restore = args.Remove("--resume");
        restore |= args.Remove("--restore");  // legacy flag — kept for pre-existing autostart entries
        int mi = args.IndexOf("--model");
        if (mi >= 0 && mi + 1 < args.Count)
        {
            _model = args[mi + 1];
            args.RemoveRange(mi, 2);
        }
        var paths = args.Where(a => !a.StartsWith("--")).ToList();

        _mutex = new Mutex(true, MutexName, out bool isPrimary);
        if (!isPrimary)
        {
            HandOff(paths, noMatte, restore);  // running instance takes over
            Shutdown();
            return;
        }

        StartPipeServer();
        CreateTray();
        RegisterPasteHotkey();

        // Created idle (Infinite) and armed by BumpIdleTimer() after each matte.
        if (IdleEvictAfter != Timeout.InfiniteTimeSpan)
            _idleEvict = new System.Threading.Timer(
                _ => DisposeWarmSessions(fromTimer: true), null, Timeout.Infinite, Timeout.Infinite);

        if (paths.Count > 0 || restore)
            OpenStickers(paths, noMatte, restore);
        else
            _tray?.ShowBalloonTip(3000, "Sticker",
                "Running in the tray. Right-click any image → Open as sticker.",
                WinForms.ToolTipIcon.Info);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        FlushSessionNow();   // persist any debounced session write before we go
        if (_hotkeySource is not null)
        {
            UnregisterHotKey(_hotkeySource.Handle, HotkeyId);
            _hotkeySource.Dispose();
        }
        _idleEvict?.Dispose();
        DisposeWarmSessions();
        _tray?.Dispose();
        base.OnExit(e);
    }

    // --- global paste hotkey (Ctrl+Alt+V) ---

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    private const int HotkeyId = 0x571C;
    private const int WM_HOTKEY = 0x0312;
    private HwndSource? _hotkeySource;

    private void RegisterPasteHotkey()
    {
        // Message-only window to receive WM_HOTKEY
        var p = new HwndSourceParameters("Sticker.Hotkey")
        {
            ParentWindow = new IntPtr(-3),  // HWND_MESSAGE
        };
        _hotkeySource = new HwndSource(p);
        _hotkeySource.AddHook(HotkeyHook);
        const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2;
        const uint VK_V = 0x56;
        if (!RegisterHotKey(_hotkeySource.Handle, HotkeyId, MOD_CONTROL | MOD_ALT, VK_V))
        {
            // Some other app owns Ctrl+Alt+V — tray item still works.
        }
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            PasteAsSticker();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void PasteAsSticker()
    {
        try
        {
            // Image *files* copied in Explorer
            if (Clipboard.ContainsFileDropList())
            {
                var images = Clipboard.GetFileDropList().Cast<string>()
                    .Where(IsImageFile).ToList();
                if (images.Count > 0)
                {
                    OpenStickers(images, false, false);
                    return;
                }
            }

            // Bitmap content (screenshots, copied web images)
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".sticker_cache", "clipboard");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, $"clip-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var fs = File.Create(file))
                    encoder.Save(fs);
                PruneClipboardCaptures(dir, file);
                OpenStickers(new() { file }, false, false);
                return;
            }
        }
        catch (COMException)
        {
            // The clipboard is a shared, singly-owned resource; another app
            // holding it open makes reads fail transiently. That's not "no
            // image" — tell the user to retry rather than implying it's empty.
            _tray?.ShowBalloonTip(3000, "Sticker",
                "Couldn't read the clipboard — another app may be using it. Try again in a moment.",
                WinForms.ToolTipIcon.Warning);
            return;
        }
        catch (Exception ex)
        {
            _tray?.ShowBalloonTip(3000, "Sticker",
                $"Couldn't paste from the clipboard: {ex.Message}",
                WinForms.ToolTipIcon.Warning);
            return;
        }
        // No exception and nothing usable found — the clipboard really is empty
        // (of images), so this message is now accurate.
        _tray?.ShowBalloonTip(2000, "Sticker", "No image on the clipboard.",
            WinForms.ToolTipIcon.Info);
    }

    private static bool IsImageFile(string? path) =>
        Path.GetExtension(path ?? "").ToLowerInvariant()
            is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".gif";

    /// <summary>
    /// Bound the clipboard-capture folder. Pasted screenshots/images are saved as
    /// throwaway clip-*.png; left alone they accumulate forever (and "Clear matte
    /// cache" only touches the top-level cache, not this subfolder). Keep any that
    /// an open sticker still references (so a close+restore round-trip survives),
    /// plus the most recent few; delete the rest.
    /// </summary>
    private static void PruneClipboardCaptures(string dir, string justWrote)
    {
        const int KeepRecent = 20;
        try
        {
            var referenced = new HashSet<string>(
                Stickers.Select(w => w.SourcePath), StringComparer.OrdinalIgnoreCase)
            {
                Path.GetFullPath(justWrote),
            };
            foreach (var fi in Directory.EnumerateFiles(dir, "clip-*.png")
                         .Where(f => !referenced.Contains(Path.GetFullPath(f)))
                         .Select(f => new FileInfo(f))
                         .OrderByDescending(fi => fi.LastWriteTimeUtc)
                         .Skip(KeepRecent))
                try { fi.Delete(); } catch { /* locked or already gone — skip */ }
        }
        catch { /* best effort; never block a paste over cleanup */ }
    }

    // --- startup toggle ---
    //
    // Autostart is a per-user Startup-folder shortcut, NOT a write to
    // ...\CurrentVersion\Run. Both achieve login autostart, but the Run key is
    // the textbook persistence location malware uses, so Defender's ML heuristics
    // score a direct write to it harshly. A .lnk in shell:startup is the
    // Explorer-managed, user-visible mechanism and reads as benign. The installer
    // drops the same shortcut (same name/args), so the toggle stays in sync with it.

    private static string StartupShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Sticker.lnk");

    private static bool IsStartupEnabled() => File.Exists(StartupShortcut);

    private static void SetStartup(bool enabled)
    {
        if (enabled)
        {
            string exe = Environment.ProcessPath!;
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(exe);
            link.SetArguments("--resume");
            link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? "");
            link.SetIconLocation(exe, 0);
            ((IPersistFile)link).Save(StartupShortcut, true);
        }
        else if (File.Exists(StartupShortcut))
        {
            File.Delete(StartupShortcut);
        }
    }

    // Minimal IShellLink / IPersistFile interop to write a .lnk without pulling in
    // the Windows Script Host (WScript.Shell), which would itself look script-y.
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    // --- tray ---

    private void CreateTray()
    {
        var menu = new WinForms.ContextMenuStrip
        {
            Renderer = new WinForms.ToolStripProfessionalRenderer(new DarkColorTable()),
            ForeColor = Drawing.Color.FromArgb(240, 240, 240),
            // Image margin stays on: WinForms draws check glyphs there.
        };
        var paste = new WinForms.ToolStripMenuItem("Paste as sticker")
        {
            ShortcutKeyDisplayString = "Ctrl+Alt+V",
        };
        paste.Click += (_, _) => PasteAsSticker();
        menu.Items.Add(paste);

        menu.Items.Add("Open images…", null, (_, _) => PickAndOpen());
        menu.Items.Add("Restore last session", null, (_, _) => OpenStickers(new(), false, true));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Unpin all stickers", null, (_, _) =>
        {
            foreach (var w in Stickers)
                w.SetPinned(false);
        });
        menu.Items.Add("Close all stickers", null, (_, _) => CloseAll());
        menu.Items.Add("Clear matte cache…", null, (_, _) => ClearMatteCache());
        menu.Items.Add(new WinForms.ToolStripSeparator());

        var startup = new WinForms.ToolStripMenuItem("Start with Windows")
        {
            Checked = IsStartupEnabled(),
            CheckOnClick = true,
        };
        startup.Click += (_, _) => SetStartup(startup.Checked);
        menu.Items.Add(startup);

        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        Drawing.Icon? icon = null;
        try { icon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!); }
        catch { /* fall through */ }

        _tray = new WinForms.NotifyIcon
        {
            Icon = icon ?? Drawing.SystemIcons.Application,
            Text = "Sticker",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => PickAndOpen();
    }

    private void ClearMatteCache()
    {
        var (files, bytes) = Matting.ClearMatteCache();
        string freed = bytes >= (1L << 20) ? $"{bytes >> 20} MB" : $"{(bytes + 1023) >> 10} KB";
        _tray?.ShowBalloonTip(3000, "Sticker",
            files == 0
                ? "Matte cache is already empty."
                : $"Cleared {files} cached cutout(s), freeing {freed}. Open stickers are unaffected; "
                  + "models stay downloaded.",
            WinForms.ToolTipIcon.Info);
    }

    private sealed class DarkColorTable : WinForms.ProfessionalColorTable
    {
        private static readonly Drawing.Color Bg = Drawing.Color.FromArgb(43, 43, 43);
        private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(66, 66, 66);
        private static readonly Drawing.Color Line = Drawing.Color.FromArgb(69, 69, 69);
        public override Drawing.Color ToolStripDropDownBackground => Bg;
        public override Drawing.Color ImageMarginGradientBegin => Bg;
        public override Drawing.Color ImageMarginGradientMiddle => Bg;
        public override Drawing.Color ImageMarginGradientEnd => Bg;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemBorder => Hover;
        public override Drawing.Color MenuBorder => Line;
        public override Drawing.Color SeparatorDark => Line;
        public override Drawing.Color SeparatorLight => Line;
        public override Drawing.Color CheckBackground => Hover;
        public override Drawing.Color CheckSelectedBackground => Hover;
        public override Drawing.Color CheckPressedBackground => Hover;
    }

    private void PickAndOpen()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = "Images|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif|All files|*.*",
        };
        if (dialog.ShowDialog() == true)
            OpenStickers(dialog.FileNames.ToList(), false, false);
    }

    private void ExitApp()
    {
        if (Stickers.Count > 0)
            SaveSession();      // keep session.json so --restore brings them back
        _suppressSave = true;
        Shutdown();
    }

    // --- opening ---

    private async void OpenStickers(List<string> paths, bool noMatte, bool restore)
    {
        var jobs = new List<(string Path, bool NoMatte, StickerState? State)>();
        if (restore)
        {
            var saved = Session.Load();
            if (saved.Count == 0 && paths.Count == 0)
            {
                _tray?.ShowBalloonTip(3000, "Sticker", "No saved session to restore.",
                    WinForms.ToolTipIcon.Info);
                return;
            }
            jobs.AddRange(saved.Select(s => (s.Source, s.NoMatte, (StickerState?)s)));
        }
        jobs.AddRange(paths.Select(p => (p, noMatte, (StickerState?)null)));

        // One-time "downloading model" notice (first run only)
        Window? notice = null;
        bool needsMatting = jobs.Any(j => !j.NoMatte);
        if (needsMatting && !Matting.ModelFileExists(_model))
            notice = ShowDownloadNotice(_model);

        foreach (var (p, nm, state) in jobs)
        {
            try
            {
                string file = Path.GetFullPath(p);
                if (!File.Exists(file))
                {
                    MessageBox.Show($"Not found: {file}", "Sticker");
                    continue;
                }
                string image = file;
                string? usedModel = null;
                if (!nm)
                {
                    // Restored stickers remember the model they were matted with,
                    // but only reuse it when that cutout is already cached (instant)
                    // — otherwise we'd kick off a slow re-matte (e.g. BiRefNet) at
                    // restore/login. New stickers and cache-misses use the default.
                    string m = _model;
                    if (state?.Model is { Length: > 0 } saved
                        && Matting.CachedMatteExists(file, saved))
                        m = saved;
                    usedModel = m;

                    if (Matting.CachedMatteExists(file, m))
                    {
                        // Already on disk — show it without loading any model.
                        image = Matting.CachePathFor(file, m);
                    }
                    else
                    {
                        // One matte at a time (see _matteGate); model load/download
                        // and inference all happen off the UI thread.
                        await _matteGate.WaitAsync();
                        try
                        {
                            image = await Task.Run(() =>
                            {
                                Interlocked.Increment(ref _matteInFlight);
                                try { return GetMatting(m).RemoveBackground(file); }
                                finally { Interlocked.Decrement(ref _matteInFlight); }
                            });
                        }
                        finally { _matteGate.Release(); }
                    }
                }
                notice?.Close();
                notice = null;

                var w = new StickerWindow(image, file, nm, state, usedModel);
                Stickers.Add(w);
                w.Show();
                w.Activate();
            }
            catch (Exception ex)
            {
                notice?.Close();
                notice = null;
                MessageBox.Show($"Failed to open {p}:\n{ex.Message}", "Sticker");
            }
        }

        // The batch above shares one warm session so a multi-sticker restore
        // doesn't reload the model per image; once it's done, release the heavy
        // model's arena (light models stay warm — they're cheap). Take the gate
        // so we don't evict a session a re-matte just started on another path.
        if (IsHeavyModel(_model))
        {
            await _matteGate.WaitAsync();
            try { EvictMatting(_model, forceCpu: false); }
            finally { _matteGate.Release(); }
        }
        if (needsMatting)
            BumpIdleTimer();

        SaveSession();
    }

    /// <summary>The matting model new stickers use by default (STICKER_MODEL or the built-in default).</summary>
    public static string ActiveModel => ((App)Current)._model;

    /// <summary>Rough on-disk size of a model, for download notices.</summary>
    private static int ModelSizeMb(string model) => model.StartsWith("birefnet") ? 900 : 180;

    /// <summary>
    /// A small centred "downloading…" window. Models download lazily on first use,
    /// which can stall the matte for a while (BiRefNet is ~900 MB) — without this,
    /// a first-time model switch would just spin with no explanation. Returns the
    /// window so the caller can close it once the matte finishes.
    /// </summary>
    private Window ShowDownloadNotice(string model)
    {
        var text = new TextBlock
        {
            Text = $"Downloading the '{model}' model (~{ModelSizeMb(model)} MB) — first use only.",
            TextWrapping = TextWrapping.Wrap,
        };
        var bar = new ProgressBar
        {
            Height = 18,
            Minimum = 0,
            Maximum = 1,
            IsIndeterminate = true,   // until the first progress tick gives us a total
            Margin = new Thickness(0, 12, 0, 0),
        };
        var notice = new Window
        {
            Title = "Sticker",
            Width = 460,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Topmost = true,
            Content = new StackPanel { Margin = new Thickness(16), Children = { text, bar } },
        };

        // Live progress. DownloadProgress fires on the download thread, so marshal
        // to the UI. Throttle to whole-percent (or 2 MB when length is unknown)
        // changes so we don't flood the dispatcher with ~11k updates for BiRefNet.
        int lastMarker = -1;
        void OnProgress(string m, long read, long? total)
        {
            if (m != model) return;
            int marker = total is > 0 ? (int)(read * 100 / total.Value) : (int)(read >> 21);
            if (marker == lastMarker) return;
            lastMarker = marker;
            notice.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (total is > 0)
                {
                    bar.IsIndeterminate = false;
                    bar.Value = (double)read / total.Value;
                    text.Text = $"Downloading '{model}' — {read >> 20} / {total.Value >> 20} MB ({marker}%)";
                }
                else
                {
                    text.Text = $"Downloading '{model}' — {read >> 20} MB";
                }
            }));
        }
        Matting.DownloadProgress += OnProgress;
        notice.Closed += (_, _) => Matting.DownloadProgress -= OnProgress;
        notice.Show();
        return notice;
    }

    /// <summary>Produce the matte for <paramref name="source"/>, optionally with a different model.</summary>
    /// <param name="force">Re-run inference even if a cached result for this model exists.
    /// When false (the default), an already-computed model loads instantly from cache.</param>
    /// <param name="forceCpu">Run on CPU instead of the GPU — slow, but survives VRAM shortages.</param>
    public static async Task<string> RematteAsync(string source, string? model = null,
                                                  bool forceCpu = false, bool force = false)
    {
        var app = (App)Current;
        string m = model ?? app._model;

        // Visible notice if this model still needs downloading (matches first-open UX).
        Window? notice = Matting.ModelFileExists(m) ? null : app.ShowDownloadNotice(m);
        if (forceCpu)
            app._tray?.ShowBalloonTip(6000, "Sticker",
                "Re-matting on the CPU — this can take a while. The app is still working, "
                + "give it a minute or two before assuming it's stuck.",
                WinForms.ToolTipIcon.Info);
        try
        {
            await _matteGate.WaitAsync();   // one matte at a time
            try
            {
                return await Task.Run(() =>
                {
                    Interlocked.Increment(ref app._matteInFlight);
                    try
                    {
                        string result = app.GetMatting(m, forceCpu).RemoveBackground(source, force);
                        // Heavy models pin gigabytes for the life of the process if kept
                        // warm, so drop the session now that this run is done. Light
                        // sessions stay warm but the idle clock starts ticking.
                        if (IsHeavyModel(m))
                            app.EvictMatting(m, forceCpu);
                        return result;
                    }
                    catch
                    {
                        // Don't let a failed session squat on VRAM/RAM and poison the
                        // next re-matte (even of another model). Drop it, then rethrow
                        // so the UI can still surface the error / offer a CPU retry.
                        app.EvictMatting(m, forceCpu);
                        throw;
                    }
                    finally
                    {
                        Interlocked.Decrement(ref app._matteInFlight);
                        app.BumpIdleTimer();   // (re)start the idle clock once work settles
                    }
                });
            }
            finally { _matteGate.Release(); }
        }
        finally
        {
            notice?.Close();
        }
    }

    // session.json is touched on many small UI events (every resize/opacity notch,
    // every drag end). Capture synchronously on the calling (UI) thread — reading
    // window state off-thread isn't safe — but debounce the disk write so a burst
    // coalesces into a single off-UI-thread I/O instead of rewriting the file per
    // notch. Shutdown flushes synchronously (see FlushSessionNow / OnExit).
    private static readonly object _saveLock = new();
    private static List<StickerState>? _pendingSave;
    private static System.Threading.Timer? _saveTimer;

    public static void SaveSession()
    {
        if (_suppressSave)
            return;
        var snapshot = Stickers.Select(w => w.CaptureState()).ToList();
        lock (_saveLock)
        {
            _pendingSave = snapshot;
            _saveTimer ??= new System.Threading.Timer(_ => FlushSession());
            _saveTimer.Change(400, Timeout.Infinite);
        }
    }

    private static void FlushSession()
    {
        List<StickerState>? snapshot;
        lock (_saveLock)
        {
            snapshot = _pendingSave;
            _pendingSave = null;
        }
        if (snapshot is not null)
            Session.Save(snapshot);
    }

    /// <summary>Write any debounced session immediately — used on shutdown.</summary>
    private static void FlushSessionNow()
    {
        lock (_saveLock)
            _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        FlushSession();
    }

    public static void NotifyClosed(StickerWindow w)
    {
        if (_suppressSave)
            return;
        Stickers.Remove(w);
        SaveSession();
        // App stays resident in the tray even with zero stickers.
    }

    public static void CloseAll()
    {
        SaveSession();          // snapshot first so --restore brings everything back
        _suppressSave = true;
        try
        {
            foreach (var w in Stickers.ToList())
                w.Close();
            Stickers.Clear();
        }
        finally
        {
            // Always re-enable saving, even if a Close() throws — otherwise the
            // session would silently stop persisting for the rest of the process.
            _suppressSave = false;
        }
    }

    // --- single instance plumbing ---

    private sealed class HandoffMessage
    {
        public List<string> Paths { get; set; } = new();
        public bool NoMatte { get; set; }
        public bool Restore { get; set; }
    }

    private void StartPipeServer()
    {
        // Restrict the pipe to the current user: combined with the per-session
        // name, that isolates this instance from any other user/session and stops
        // another account from injecting handoff messages.
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.FullControl, AccessControlType.Allow));

        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = NamedPipeServerStreamAcl.Create(
                        PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                        0, 0, security);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string? line = await reader.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    var msg = JsonSerializer.Deserialize<HandoffMessage>(line);
                    if (msg is not null && (msg.Paths.Count > 0 || msg.Restore))
                        await Dispatcher.InvokeAsync(
                            () => OpenStickers(msg.Paths, msg.NoMatte, msg.Restore));
                }
                catch
                {
                    // Malformed message or broken pipe — keep serving, but pause
                    // briefly so a persistent failure can't spin the CPU.
                    await Task.Delay(500);
                }
            }
        });
    }

    private static void HandOff(List<string> paths, bool noMatte, bool restore)
    {
        if (paths.Count == 0 && !restore)
            return;
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.WriteLine(JsonSerializer.Serialize(new HandoffMessage
            {
                Paths = paths.Select(Path.GetFullPath).ToList(),
                NoMatte = noMatte,
                Restore = restore,
            }));
            writer.Flush();
        }
        catch
        {
            // Primary vanished between mutex check and connect — rare; user can relaunch.
        }
    }
}
