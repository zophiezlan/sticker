using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace StickerApp;

public partial class StickerWindow : Window
{
    private const double MinW = 64;
    private const double MaxW = 4096;

    private readonly string _sourcePath;
    private readonly bool _noMatte;

    /// <summary>Path to the original image this sticker was created from.</summary>
    public string SourcePath => _sourcePath;

    /// <summary>Short name for menus: the file name, or "Pasted image (time)" for a clipboard capture.</summary>
    public string DisplayName => StickerNames.Display(_sourcePath, DateTime.Now);

    // Which model produced the matte currently shown — drives the menu checkmark
    // and the "re-process" action. Starts as the app default used at open time.
    private string _currentModel = App.ActiveModel;

    // The model intent persisted to session.json. Usually equals _currentModel, but
    // on a --resume cache-miss the *shown* cutout falls back to the default while
    // this preserves the model the sticker was originally matted with, so a later
    // restore (once that cutout is cached again) reuses it instead of the fallback.
    private string? _recordedModel;

    // _matted/_original are decoded no larger than the biggest monitor (see
    // DisplayMaxSide) to save memory; _fullW/_fullH are the full-resolution upright
    // size, which _scale is measured against (so session.json sizes are unaffected).
    // _mattedPath points at the full-res cutout on disk, used when saving.
    private string _mattedPath;
    private BitmapSource _matted;
    private BitmapSource? _original;
    private int _fullW, _fullH;
    private bool _showingOriginal;

    private double _scale;
    private double _rotation;
    private bool _flipped;
    private bool _pinned;
    private bool _rematting;   // guards Rematte against reentrant model picks
    private DateTimeOffset? _expiresAt;   // auto-expiry deadline; null = keep

    /// <summary>When this sticker removes itself (null = never). Checked by App's expiry timer.</summary>
    public DateTimeOffset? ExpiresAt => _expiresAt;

    private MenuItem? _toggleItem;

    private BitmapSource Current => _showingOriginal ? _original! : _matted;

    public StickerWindow(string imagePath, string sourcePath,
                         bool noMatte = false, StickerState? state = null,
                         string? matteModel = null)
    {
        InitializeComponent();
        _sourcePath = sourcePath;
        _noMatte = noMatte;
        if (!string.IsNullOrEmpty(matteModel))
            _currentModel = matteModel;   // the model that actually produced the shown cutout
        // Persist the *intended* model: on a --resume cache-miss the shown cutout is
        // the fallback default (matteModel), but state.Model still holds what this
        // sticker was originally matted with — keep recording that so the preference
        // survives. New stickers record what they were matted with.
        _recordedModel = state?.Model is { Length: > 0 } intent ? intent : matteModel;

        _mattedPath = imagePath;
        var loaded = ImageLoader.Load(imagePath, DisplayMaxSide());
        _matted = loaded.Image;
        _fullW = loaded.FullWidth;
        _fullH = loaded.FullHeight;
        Img.Source = _matted;

        // Initial size: fit within 40% of the work area.
        var area = SystemParameters.WorkArea;
        _scale = Math.Min(1.0, Math.Min(area.Width * 0.4 / _fullW,
                                        area.Height * 0.4 / _fullH));

        if (state is not null)
        {
            _scale = state.Scale;
            _rotation = state.Rotation;
            _flipped = state.Flipped;
            _pinned = state.Pinned;
            Opacity = Math.Clamp(state.Opacity, 0.15, 1.0);
            Topmost = state.OnTop;
            _expiresAt = state.ExpiresAt;
        }

        ApplyVisuals();

        if (state is not null)
        {
            // Rescue stickers whose monitor was unplugged or resolution changed.
            var (bw, bh) = RotatedBounds(Img.Width, Img.Height, _rotation);
            (Left, Top) = ClampToVirtualScreen(state.X, state.Y, bw, bh);
        }
        else
        {
            UpdateLayout();
            double offset = 30 * (App.Stickers.Count % 10);
            Left = area.Left + (area.Width - ActualWidth) / 2 + offset;
            Top = area.Top + (area.Height - ActualHeight) / 2 + offset;
        }

        BuildContextMenu();
    }

    /// <summary>Keeps at least a grabbable sliver of the sticker on some monitor.</summary>
    private static (double X, double Y) ClampToVirtualScreen(double x, double y, double w, double h)
    {
        const double margin = 48;
        double left = SystemParameters.VirtualScreenLeft;
        double top = SystemParameters.VirtualScreenTop;
        double right = left + SystemParameters.VirtualScreenWidth;
        double bottom = top + SystemParameters.VirtualScreenHeight;
        x = Math.Min(Math.Max(x, left - w + margin), right - margin);
        y = Math.Min(Math.Max(y, top - h + margin), bottom - margin);
        return (x, y);
    }

    /// <summary>Axis-aligned bounding box (width, height) of a w×h rectangle rotated by <paramref name="degrees"/>.</summary>
    private static (double W, double H) RotatedBounds(double w, double h, double degrees)
    {
        double rad = degrees * Math.PI / 180;
        return (Math.Abs(w * Math.Cos(rad)) + Math.Abs(h * Math.Sin(rad)),
                Math.Abs(w * Math.Sin(rad)) + Math.Abs(h * Math.Cos(rad)));
    }

    public StickerState CaptureState() => new()
    {
        Source = _sourcePath,
        NoMatte = _noMatte,
        X = (long)Math.Round(Left),
        Y = (long)Math.Round(Top),
        Scale = _scale,
        Rotation = _rotation,
        Flipped = _flipped,
        Opacity = Opacity,
        OnTop = Topmost,
        Pinned = _pinned,
        Model = _noMatte ? null : (_recordedModel ?? _currentModel),
        ExpiresAt = _expiresAt,
    };

    // --- pin (click-through) ---

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;

    /// <summary>True while this sticker is pinned (click-through). Drives the tray's per-sticker unpin list.</summary>
    public bool IsPinned => _pinned;

    /// <summary>Pinned stickers ignore the mouse entirely. Unpin via the tray menu.</summary>
    public void SetPinned(bool pinned)
    {
        _pinned = pinned;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE,
                pinned ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT);
        }
        App.SaveSession();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (_pinned)
            SetPinned(true);  // hwnd exists now; reapply restored pin
    }

    /// <summary>
    /// Longest side, in physical pixels, of the largest connected monitor. A sticker
    /// can't usefully show more detail than that, so display copies are decoded no
    /// larger (0 = no cap, if the screens can't be read). The process is
    /// Per-Monitor-V2 DPI aware, so Screen.Bounds are physical pixels.
    /// </summary>
    private static int DisplayMaxSide()
    {
        try
        {
            return System.Windows.Forms.Screen.AllScreens
                .Max(s => Math.Max(s.Bounds.Width, s.Bounds.Height));
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Applies scale/rotation/flip, keeping the on-screen center put.</summary>
    private void ApplyVisuals(bool keepCenter = false)
    {
        double cx = Left + ActualWidth / 2, cy = Top + ActualHeight / 2;

        double w = Math.Clamp(_fullW * _scale, MinW, MaxW);
        _scale = w / _fullW;
        Img.Width = w;
        Img.Height = _fullH * _scale;
        Rot.Angle = _rotation;
        Flip.ScaleX = _flipped ? -1 : 1;

        if (keepCenter)
        {
            UpdateLayout();  // SizeToContent settles synchronously so we can re-center
            Left = cx - ActualWidth / 2;
            Top = cy - ActualHeight / 2;
        }
        App.SaveSession();
    }

    // --- mouse ---
    // Manual drag instead of DragMove(): the OS move loop clamps windows at
    // the top screen edge (to keep a title bar reachable) and triggers Snap
    // previews — neither makes sense for stickers.

    private Point _grabOffset;
    private bool _dragging;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
        {
            ToggleOriginal();
            return;
        }
        _grabOffset = e.GetPosition(this);
        _dragging = true;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
            return;
        // If the window was closed mid-drag (Esc, middle-click, programmatic Close),
        // its visual loses its PresentationSource while a dispatched move may still be
        // queued. PointToScreen would then throw and pop a spurious error balloon, so
        // bail once the source is gone. (Check BEFORE PointToScreen — that's the throw
        // site — not just the CompositionTarget deref below.)
        if (PresentationSource.FromVisual(this) is not { } source)
            return;
        var devicePos = PointToScreen(e.GetPosition(this));
        var dipPos = source.CompositionTarget!.TransformFromDevice.Transform(devicePos);
        Left = dipPos.X - _grabOffset.X;
        Top = dipPos.Y - _grabOffset.Y;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
            App.SaveSession();
        }
    }

    /// <summary>
    /// Capture can be revoked without a left-button-up — Alt+Tab, a modal dialog
    /// (e.g. the Save dialog from 'S' pressed mid-drag), or another window taking
    /// the foreground. Clear the drag flag so the next mouse-move doesn't teleport
    /// the sticker to follow a button-up cursor. No-ops on a normal release, which
    /// already clears _dragging before releasing capture.
    /// </summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging)
        {
            _dragging = false;
            App.SaveSession();
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.Middle)
            Close();
        else if (e.ChangedButton == MouseButton.Right)
            // Borderless, ShowInTaskbar=False overlays only get keyboard focus when
            // clicked. Take focus on right-click so the R/F/S/D/+/-/arrow shortcuts
            // keep working after the context menu closes (and after focusing another app).
            Activate();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        bool up = e.Delta > 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            Opacity = Math.Clamp(Opacity + (up ? 0.08 : -0.08), 0.15, 1.0);
            App.SaveSession();
        }
        else
        {
            double factor = ResizeStep();
            _scale *= up ? factor : 1 / factor;
            ApplyVisuals(keepCenter: true);
        }
        e.Handled = true;
    }

    /// <summary>Resize multiplier per step: a fine 1.05 when Ctrl is held, else 1.15. Shared by the wheel and the +/- keys.</summary>
    private static double ResizeStep() =>
        Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 1.05 : 1.15;

    // --- keyboard ---

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.OemPlus or Key.Add:
                _scale *= ResizeStep();   // Ctrl = fine step, matching the wheel
                ApplyVisuals(keepCenter: true);
                break;
            case Key.OemMinus or Key.Subtract:
                _scale /= ResizeStep();
                ApplyVisuals(keepCenter: true);
                break;
            case Key.R:
                Rotate(shift ? -15 : 15);
                break;
            case Key.F:
                FlipHorizontal();
                break;
            case Key.D:
                ToggleOriginal();
                break;
            case Key.S:
                SaveCutout();
                break;
            case Key.C when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                CopyCutout();
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                Nudge(e.Key, shift);
                e.Handled = true;   // don't let arrows bubble to default focus navigation
                break;
        }
    }

    /// <summary>
    /// Arrow-key fine positioning — 1 DIP per press, 10 with Shift. Mouse drag is the
    /// only other move path (and deliberately bypasses the OS move loop), so this is
    /// the keyboard equivalent. Position only: no scale/rotation, so no ApplyVisuals.
    /// </summary>
    private void Nudge(Key key, bool shift)
    {
        double step = shift ? 10 : 1;
        switch (key)
        {
            case Key.Left: Left -= step; break;
            case Key.Right: Left += step; break;
            case Key.Up: Top -= step; break;
            case Key.Down: Top += step; break;
        }
        App.SaveSession();
    }

    // --- actions ---

    private void Rotate(double degrees)
    {
        _rotation = (_rotation + degrees) % 360;
        ApplyVisuals(keepCenter: true);
    }

    private void FlipHorizontal()
    {
        _flipped = !_flipped;
        ApplyVisuals(keepCenter: true);
    }

    private void ToggleOriginal()
    {
        if (_noMatte)
            return;
        _showingOriginal = !_showingOriginal;
        _original ??= ImageLoader.Load(_sourcePath, DisplayMaxSide()).Image;
        Img.Source = Current;
        if (_toggleItem is not null)
            _toggleItem.Header = _showingOriginal ? "Show cutout" : "Show original";
        ApplyVisuals(keepCenter: true);
    }

    private void SaveCutout()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = Path.GetFileNameWithoutExtension(_sourcePath) + "-cutout.png",
            InitialDirectory = Path.GetDirectoryName(_sourcePath),
            Filter = "PNG image|*.png",
        };
        if (dialog.ShowDialog() != true)
            return;

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(RenderCutout()));
        using var fs = File.Create(dialog.FileName);
        encoder.Save(fs);
    }

    /// <summary>
    /// Put the full-resolution cutout (with flip/rotation) on the clipboard. Two
    /// formats: "PNG" keeps transparency in apps that read it (browsers, Office,
    /// Paint.NET, Discord, Slack…); the standard bitmap is the fallback everything
    /// else understands, though most of those flatten the transparent areas.
    /// </summary>
    private void CopyCutout()
    {
        var image = RenderCutout();
        var png = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(png);

        var data = new DataObject();
        data.SetData("PNG", png, autoConvert: false);
        data.SetImage(image);

        // The clipboard is shared and singly-owned: another app holding it open makes
        // the write fail transiently, so retry briefly before giving up.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                App.ShowTip("Cutout copied to the clipboard.");
                return;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                App.ShowTip("Couldn't copy — another app is using the clipboard. Try again.", warning: true);
                return;
            }
        }
    }

    /// <summary>
    /// The full-resolution matte with the current flip/rotation applied — what Save
    /// and Copy export. The on-screen copy may be downscaled, so reload the full-res
    /// cutout from disk; if it's gone (e.g. "Clear matte cache"), fall back to the
    /// display copy.
    /// </summary>
    private BitmapSource RenderCutout()
    {
        BitmapSource src;
        try { src = ImageLoader.Load(_mattedPath).Image; }
        catch { src = _matted; }
        double w = src.PixelWidth, h = src.PixelHeight;
        var (bw, bh) = RotatedBounds(w, h, _rotation);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var t = new TransformGroup();
            if (_flipped)
                t.Children.Add(new ScaleTransform(-1, 1, w / 2, h / 2));
            t.Children.Add(new RotateTransform(_rotation, w / 2, h / 2));
            t.Children.Add(new TranslateTransform((bw - w) / 2, (bh - h) / 2));
            dc.PushTransform(t);
            dc.DrawImage(src, new Rect(0, 0, w, h));
            dc.Pop();
        }
        var target = new RenderTargetBitmap(
            (int)Math.Ceiling(bw), (int)Math.Ceiling(bh), 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>Set (or clear, with null) when this sticker removes itself.</summary>
    private void SetExpiry(Expiry.Choice? choice)
    {
        bool wasSet = _expiresAt is not null;
        _expiresAt = choice is { } c ? Expiry.Resolve(c, DateTimeOffset.Now) : null;
        App.SaveSession();
        if (_expiresAt is { } at)
            App.ShowTip($"This sticker will be removed in {Expiry.FormatRemaining(at - DateTimeOffset.Now)}.");
        else if (wasSet)
            App.ShowTip("Timer cleared — this sticker will stay.");
    }

    private async void Rematte(string? model = null, bool force = false)
    {
        // Ignore a second model pick / re-process while one is already running.
        // Both would serialize on _matteGate, but each Rematte runs its own finally,
        // so the first to finish would clear the shared busy spinner + wait cursor
        // while the second is still inferring behind the gate — the window would look
        // idle mid-matte. One at a time keeps the spinner and _currentModel coherent
        // and avoids redundant inference. The flag also covers the GPU-OOM prompt
        // inside RematteWithFallback, so a second matte can't launch while it's up.
        if (_rematting)
            return;
        _rematting = true;
        Mouse.OverrideCursor = Cursors.Wait;
        SetBusy(true);
        try
        {
            await RematteWithFallback(model, force);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Re-matte failed:\n{ex.Message}", "Sticker");
        }
        finally
        {
            Mouse.OverrideCursor = null;
            SetBusy(false);
            _rematting = false;
        }
    }

    /// <summary>
    /// GPU first; on a GPU-memory failure, free other warm GPU sessions and try
    /// the GPU once more (the model may simply not fit alongside the others on a
    /// limited card). Only if it still won't fit do we offer the slower CPU path.
    /// </summary>
    private async Task RematteWithFallback(string? model, bool force)
    {
        try
        {
            await RematteCore(model, forceCpu: false, force);
            return;
        }
        catch (Exception ex) when (Matting.IsGpuMemoryError(ex))
        {
            App.TrimGpuSessions();   // free VRAM held by other warm models, then retry GPU
        }

        try
        {
            await RematteCore(model, forceCpu: false, force);
            return;
        }
        catch (Exception ex) when (Matting.IsGpuMemoryError(ex))
        {
            // Still won't fit on the GPU — offer the (slower) CPU. Ask first and
            // warn it's slow, so a long run reads as expected work rather than a
            // frozen app the user force-quits.
        }

        Mouse.OverrideCursor = null;
        SetBusy(false);
        var choice = MessageBox.Show(
            "This model ran out of GPU memory.\n\n"
            + "Retry on the CPU instead? It will produce the same result, but "
            + "can take anywhere from several seconds to a few minutes depending "
            + "on your machine. The app stays responsive while it works.",
            "Sticker — not enough GPU memory",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (choice != MessageBoxResult.Yes)
            return;

        // Keep the spinner + wait cursor through the CPU pass so there's the same
        // "working" feedback the GPU path gives.
        Mouse.OverrideCursor = Cursors.Wait;
        SetBusy(true);
        await RematteCore(model, forceCpu: true, force);
    }

    /// <summary>Show/hide the on-sticker busy spinner (and start/stop its rotation).</summary>
    private void SetBusy(bool on)
    {
        Busy.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
            SpinRot.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9))
                {
                    RepeatBehavior = RepeatBehavior.Forever,
                });
        else
            SpinRot.BeginAnimation(RotateTransform.AngleProperty, null);   // stop animating
    }

    /// <summary>Run the matte and apply the result; throws on failure for the caller to handle.</summary>
    private async Task RematteCore(string? model, bool forceCpu, bool force)
    {
        string path = await App.RematteAsync(_sourcePath, model, forceCpu, force);
        var loaded = ImageLoader.Load(path, DisplayMaxSide(), ignoreCache: true);
        _mattedPath = path;
        _matted = loaded.Image;
        _fullW = loaded.FullWidth;
        _fullH = loaded.FullHeight;
        if (!_showingOriginal)
            Img.Source = _matted;
        ApplyVisuals(keepCenter: true);
        if (model != null)
        {
            // The user actively chose this model: it's now both what's shown and the
            // recorded intent, superseding any prior --resume fallback preference.
            _currentModel = model;
            _recordedModel = model;
        }
    }

    // --- context menu ---

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();

        // Legend for the wheel gestures, which have no other menu/keyboard surface.
        // Disabled so it's a non-interactive hint (dimmed via App.xaml's IsEnabled
        // trigger). The keyboard moves — arrows, R/F/S/D, +/- — appear as gesture
        // text on their own items below.
        menu.Items.Add(new MenuItem
        {
            Header = "Scroll: resize   ·   Shift+scroll: opacity   ·   Ctrl+scroll: fine",
            IsEnabled = false,
        });
        menu.Items.Add(new Separator());

        var onTop = new MenuItem
        {
            Header = "Always on top",
            IsCheckable = true,
            IsChecked = Topmost,
        };
        onTop.Click += (_, _) =>
        {
            Topmost = onTop.IsChecked;
            App.SaveSession();
        };
        menu.Items.Add(onTop);

        AddItem(menu, "Pin — click-through (unpin via tray)", "", () => SetPinned(true));

        if (!_noMatte)
        {
            _toggleItem = new MenuItem { Header = "Show original", InputGestureText = "D" };
            _toggleItem.Click += (_, _) => ToggleOriginal();
            menu.Items.Add(_toggleItem);
        }

        // No gesture text: the R / Shift+R keys rotate 15° (fine adjust), while
        // these menu items do a coarse 90°, so showing "R" here would mislabel it.
        AddItem(menu, "Rotate 90° right", "", () => Rotate(90));
        AddItem(menu, "Rotate 90° left", "", () => Rotate(-90));
        AddItem(menu, "Flip horizontal", "F", FlipHorizontal);
        AddItem(menu, "Save cutout as PNG…", "S", SaveCutout);
        AddItem(menu, "Copy cutout", "Ctrl+C", CopyCutout);

        if (!_noMatte)
        {
            // Model picker — checkable items, cache-aware. Switching to a model
            // you've already run loads instantly from the on-disk cache (no
            // re-inference); a model you haven't tried yet runs once (downloading
            // first if needed — BiRefNet is ~900 MB), then it's cached too.
            // "Re-process" forces a fresh run of the current model, ignoring cache.
            // All of it lives in a "Change matte" submenu whose header names the
            // model in use, since the checkmarks are hidden until it opens.
            var matte = new MenuItem();
            foreach (var info in Models.Pickable)
                AddModelChoice(matte.Items, info.MenuLabel, info.Id);
            matte.Items.Add(new Separator());
            var reprocess = new MenuItem { Header = "Re-process current (ignore cache)" };
            reprocess.Click += (_, _) => Rematte(_currentModel, force: true);
            matte.Items.Add(reprocess);
            menu.Items.Add(new Separator());
            menu.Items.Add(matte);

            // Checkmarks and the header are refreshed each time the menu opens,
            // since the model changes at runtime.
            menu.Opened += (_, _) =>
            {
                foreach (var obj in matte.Items)
                    if (obj is MenuItem mi && mi.Tag is string tag)
                        mi.IsChecked = tag == _currentModel;
                matte.Header = $"Change matte ({Models.ShortName(_currentModel)})";
            };
        }

        // Removal: now, later (auto-expiry), or everything. "Remove later" shows the
        // countdown in its header, refreshed each time the menu opens.
        menu.Items.Add(new Separator());
        AddItem(menu, "Remove sticker", "Esc", Close);

        var later = new MenuItem();
        foreach (var (choice, label) in Expiry.Choices)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => SetExpiry(choice);
            later.Items.Add(item);
        }
        later.Items.Add(new Separator());
        var keep = new MenuItem { Header = "Don't remove", IsCheckable = true };
        keep.Click += (_, _) => SetExpiry(null);
        later.Items.Add(keep);
        menu.Items.Add(later);
        menu.Opened += (_, _) =>
        {
            keep.IsChecked = _expiresAt is null;
            later.Header = _expiresAt is { } at
                ? $"Remove later ({Expiry.FormatRemaining(at - DateTimeOffset.Now)} left)"
                : "Remove later";
        };

        AddItem(menu, "Remove all stickers", "", App.CloseAll);

        ContextMenu = menu;
    }

    private static void AddItem(ContextMenu menu, string header, string gesture, Action action)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    /// <summary>A checkable model entry that switches model cache-aware (no forced re-run).</summary>
    private void AddModelChoice(ItemCollection items, string label, string model)
    {
        var item = new MenuItem
        {
            Header = label,
            Tag = model,
            IsCheckable = true,
            IsChecked = _currentModel == model,
        };
        item.Click += (_, _) => Rematte(model);   // force:false → uses cache if present
        items.Add(item);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        App.NotifyClosed(this);
    }
}
