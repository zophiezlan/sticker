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

    // Which model produced the matte currently shown — drives the menu checkmark
    // and the "re-process" action. Starts as the app default used at open time.
    private string _currentModel = App.ActiveModel;

    private BitmapImage _matted;
    private BitmapImage? _original;
    private bool _showingOriginal;

    private double _scale;
    private double _rotation;
    private bool _flipped;
    private bool _pinned;

    private MenuItem? _toggleItem;

    private BitmapImage Current => _showingOriginal ? _original! : _matted;

    public StickerWindow(string imagePath, string sourcePath,
                         bool noMatte = false, StickerState? state = null,
                         string? matteModel = null)
    {
        InitializeComponent();
        _sourcePath = sourcePath;
        _noMatte = noMatte;
        if (!string.IsNullOrEmpty(matteModel))
            _currentModel = matteModel;   // the model that actually produced this matte

        _matted = LoadBitmap(imagePath);
        Img.Source = _matted;

        // Initial size: fit within 40% of the work area.
        var area = SystemParameters.WorkArea;
        _scale = Math.Min(1.0, Math.Min(area.Width * 0.4 / _matted.PixelWidth,
                                        area.Height * 0.4 / _matted.PixelHeight));

        if (state is not null)
        {
            _scale = state.Scale;
            _rotation = state.Rotation;
            _flipped = state.Flipped;
            _pinned = state.Pinned;
            Opacity = Math.Clamp(state.Opacity, 0.15, 1.0);
            Topmost = state.OnTop;
        }

        ApplyVisuals();

        if (state is not null)
        {
            // Rescue stickers whose monitor was unplugged or resolution changed.
            double rad = _rotation * Math.PI / 180;
            double bw = Math.Abs(Img.Width * Math.Cos(rad)) + Math.Abs(Img.Height * Math.Sin(rad));
            double bh = Math.Abs(Img.Width * Math.Sin(rad)) + Math.Abs(Img.Height * Math.Cos(rad));
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
        Model = _noMatte ? null : _currentModel,
    };

    // --- pin (click-through) ---

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;

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

    private static BitmapImage LoadBitmap(string path, bool ignoreCache = false)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(path);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        if (ignoreCache)
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Applies scale/rotation/flip, keeping the on-screen center put.</summary>
    private void ApplyVisuals(bool keepCenter = false)
    {
        double cx = Left + ActualWidth / 2, cy = Top + ActualHeight / 2;

        double w = Math.Clamp(Current.PixelWidth * _scale, MinW, MaxW);
        _scale = w / Current.PixelWidth;
        Img.Width = w;
        Img.Height = Current.PixelHeight * _scale;
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
        var devicePos = PointToScreen(e.GetPosition(this));
        var dipPos = PresentationSource.FromVisual(this)!
            .CompositionTarget!.TransformFromDevice.Transform(devicePos);
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

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.Middle)
            Close();
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
            double factor = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 1.05 : 1.15;
            _scale *= up ? factor : 1 / factor;
            ApplyVisuals(keepCenter: true);
        }
        e.Handled = true;
    }

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
                _scale *= 1.15;
                ApplyVisuals(keepCenter: true);
                break;
            case Key.OemMinus or Key.Subtract:
                _scale /= 1.15;
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
        }
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
        _original ??= LoadBitmap(_sourcePath);
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

        // Render the full-resolution matte with current flip/rotation applied.
        var src = _matted;
        double w = src.PixelWidth, h = src.PixelHeight;
        double rad = _rotation * Math.PI / 180;
        double bw = Math.Abs(w * Math.Cos(rad)) + Math.Abs(h * Math.Sin(rad));
        double bh = Math.Abs(w * Math.Sin(rad)) + Math.Abs(h * Math.Cos(rad));

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

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var fs = File.Create(dialog.FileName);
        encoder.Save(fs);
    }

    private async void Rematte(string? model = null, bool force = false)
    {
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
        _matted = LoadBitmap(path, ignoreCache: true);
        if (!_showingOriginal)
            Img.Source = _matted;
        ApplyVisuals(keepCenter: true);
        if (model != null)
            _currentModel = model;   // track the active model for the menu/checkmark
    }

    // --- context menu ---

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();

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

        if (!_noMatte)
        {
            // Model picker — checkable items, cache-aware. Switching to a model
            // you've already run loads instantly from the on-disk cache (no
            // re-inference); a model you haven't tried yet runs once (downloading
            // first if needed — BiRefNet is ~900 MB), then it's cached too.
            // "Re-process" forces a fresh run of the current model, ignoring cache.
            menu.Items.Add(new Separator());
            AddModelChoice(menu.Items, "Matte: ISNet — general (default)", "isnet-general-use");
            AddModelChoice(menu.Items, "Matte: U2Net — people & portraits", "u2net_human_seg");
            AddModelChoice(menu.Items, "Matte: BiRefNet — best (slow)", "birefnet-general");
            AddItem(menu, "Re-process current (ignore cache)", "", () => Rematte(_currentModel, force: true));

            // Checkmarks are set at build time; refresh them to the active model
            // each time the menu opens, since the selection changes at runtime.
            menu.Opened += (_, _) =>
            {
                foreach (var obj in menu.Items)
                    if (obj is MenuItem mi && mi.Tag is string tag)
                        mi.IsChecked = tag == _currentModel;
            };
        }

        menu.Items.Add(new Separator());
        AddItem(menu, "Close", "Esc", Close);
        AddItem(menu, "Close all stickers", "", App.CloseAll);

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
