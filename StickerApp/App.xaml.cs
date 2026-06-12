using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
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
    private const string PipeName = "sticker-csharp-single-instance";
    private const string MutexName = "StickerApp.SingleInstance";

    public static readonly List<StickerWindow> Stickers = new();
    private static Mutex? _mutex;
    private static bool _suppressSave;

    private readonly Dictionary<string, Matting> _mattings = new();
    private WinForms.NotifyIcon? _tray;
    private string _model =
        Environment.GetEnvironmentVariable("STICKER_MODEL") ?? Matting.DefaultModel;

    private Matting GetMatting(string model)
    {
        lock (_mattings)
        {
            if (!_mattings.TryGetValue(model, out var m))
                _mattings[model] = m = new Matting(model);
            return m;
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = e.Args.ToList();
        bool noMatte = args.Remove("--no-matte");
        bool restore = args.Remove("--restore");
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

        if (paths.Count > 0 || restore)
            OpenStickers(paths, noMatte, restore);
        else
            _tray?.ShowBalloonTip(3000, "Sticker",
                "Running in the tray. Right-click any image → Open as sticker.",
                WinForms.ToolTipIcon.Info);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_hotkeySource is not null)
        {
            UnregisterHotKey(_hotkeySource.Handle, HotkeyId);
            _hotkeySource.Dispose();
        }
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
                OpenStickers(new() { file }, false, false);
                return;
            }
        }
        catch
        {
            // Clipboard is flaky by nature; fall through to the balloon.
        }
        _tray?.ShowBalloonTip(2000, "Sticker", "No image on the clipboard.",
            WinForms.ToolTipIcon.Info);
    }

    private static bool IsImageFile(string? path) =>
        Path.GetExtension(path ?? "").ToLowerInvariant()
            is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".gif";

    // --- startup toggle ---

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static bool IsStartupEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue("Sticker") is not null;
    }

    private static void SetStartup(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
            key.SetValue("Sticker", $"\"{Environment.ProcessPath}\" --restore");
        else
            key.DeleteValue("Sticker", throwOnMissingValue: false);
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
        {
            notice = new Window
            {
                Title = "Sticker",
                Width = 420, Height = 100,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
                Content = new TextBlock
                {
                    Text = $"Downloading matting model '{_model}'… first run only.",
                    Margin = new Thickness(16),
                    TextWrapping = TextWrapping.Wrap,
                },
            };
            notice.Show();
        }

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
                if (!nm)
                {
                    // Model load/download happens off the UI thread
                    image = await Task.Run(() => GetMatting(_model).RemoveBackground(file));
                }
                notice?.Close();
                notice = null;

                var w = new StickerWindow(image, file, nm, state);
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

        SaveSession();
    }

    /// <summary>Re-run background removal, bypassing the cache, optionally with a different model.</summary>
    public static Task<string> RematteAsync(string source, string? model = null)
    {
        var app = (App)Current;
        string m = model ?? app._model;
        if (!Matting.ModelFileExists(m))
            app._tray?.ShowBalloonTip(4000, "Sticker",
                $"Downloading model '{m}' — first use can take a while (BiRefNet is ~900 MB).",
                WinForms.ToolTipIcon.Info);
        return Task.Run(() => app.GetMatting(m).RemoveBackground(source, force: true));
    }

    public static void SaveSession()
    {
        if (_suppressSave)
            return;
        Session.Save(Stickers.Select(w => w.CaptureState()));
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
        foreach (var w in Stickers.ToList())
            w.Close();
        Stickers.Clear();
        _suppressSave = false;
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
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
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
                    // Malformed message or broken pipe — keep serving.
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
