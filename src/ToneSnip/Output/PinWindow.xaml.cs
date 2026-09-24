using System.Runtime.InteropServices.WindowsRuntime;
using ToneSnip.App.Capture;
using ToneSnip.App.Interop;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using ToneSnip.Windows.Imaging;
using ToneSnip.Windows.Interop;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace ToneSnip.App.Output;

/// <summary>
/// A snip pinned to the screen: borderless, always on top and draggable, at the snip's own pixel size to start with.
/// The wheel zooms about the pointer, Ctrl+wheel fades it, and Escape, a double-click or the close button unpins it. From
/// the keyboard the arrows move it (Shift for 10 pixels), + and - zoom about its centre, Ctrl with them fades it, and
/// Shift+F10 opens its menu.
/// <para>It holds the snip only as PNG bytes plus the picture XAML decodes from them; the full-size BGRA image is
/// decoded only for Copy, Copy text, a JPEG save or the editor, and let go straight after. An HDR snip is pinned as its
/// tonemapped SDR render. Closing drops everything, and the app reclaims.</para>
/// </summary>
public sealed partial class PinWindow : PopupWindow
{
    private readonly byte[] _png;
    private readonly DateTime _taken;
    /// <summary>Zoom limits and the step per wheel notch.</summary>
    private const double MinZoom = 0.1, MaxZoom = 8, ZoomStep = 1.1;
    /// <summary>The faintest a pin can be faded to: fainter and it is easy to lose on the desktop.</summary>
    private const double MinOpacity = 0.2, OpacityStep = 0.1;
    private const int WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x2;

    private readonly int _width, _height;
    /// <summary>Where the pin is, in physical pixels; the zoom and drags move it.</summary>
    private IntRect _rect;
    private double _zoom = 1, _opacity = 1;
    /// <summary>The drag in flight: the cursor and the pin's top-left when the button went down.</summary>
    private (int X, int Y, int Left, int Top)? _drag;
    private bool _layered;

    /// <param name="png">The snip as PNG: what the pin shows and hands out.</param>
    /// <param name="at">Where to put the pin's top-left, in physical pixels (a selection pins where it was taken), or
    /// null to centre it on the monitor under the cursor.</param>
    public PinWindow(byte[] png, int width, int height, DateTime taken, (int X, int Y)? at) : base(activate: true, smallCorners: true)
    {
        _png = png; _width = width; _height = height; _taken = taken;
        InitializeComponent();
        Theme.ThemeManager.Attach(this);
        string name = $"Pinned snip, {width} × {height}";
        Title = name;   // the window text is what UIA and the task switchers name it by
        AutomationProperties.SetName(Root, name + ". Drag or arrow keys to move, wheel or plus and minus to zoom, Ctrl with them to fade, Shift+F10 for the menu, Escape to close.");

        (int x, int y) = at ?? Centred();
        _rect = new IntRect(x, y, width, height);
        TargetArea = _rect;

        Root.PointerPressed += OnPointerPressed;
        Root.PointerMoved += OnPointerMoved;
        Root.PointerReleased += OnPointerReleased;
        Root.PointerCaptureLost += OnPointerCaptureLost;
        Root.PointerWheelChanged += OnWheel;
        Root.PointerEntered += OnPointerEntered;
        Root.PointerExited += OnPointerExited;
        Root.DoubleTapped += OnDoubleTapped;
        // handledEventsToo: the close button marks Escape handled while it has the focus.
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: true);
        CloseBtn.GotFocus += OnCloseFocus;
        CloseBtn.LostFocus += OnCloseFocus;
        this.WhenClosed(() =>
        {
            Picture.Source = null;
            Root.PointerPressed -= OnPointerPressed;
            Root.PointerMoved -= OnPointerMoved;
            Root.PointerReleased -= OnPointerReleased;
            Root.PointerCaptureLost -= OnPointerCaptureLost;
            Root.PointerWheelChanged -= OnWheel;
            Root.PointerEntered -= OnPointerEntered;
            Root.PointerExited -= OnPointerExited;
            Root.DoubleTapped -= OnDoubleTapped;
            CloseBtn.GotFocus -= OnCloseFocus;
            CloseBtn.LostFocus -= OnCloseFocus;
        });
        _ = LoadPicture();
        ShowPopup(Place);
        // Escape needs an element with the keyboard; programmatic focus shows no focus ring.
        Root.Loaded += (_, _) => CloseBtn.Focus(FocusState.Programmatic);
    }

    /// <summary>The full-size image, decoded for the one command that needs it. Thread pool.</summary>
    private BgraImage Decode() => Bitmaps.Decode(_png);

    private (int X, int Y) Centred()
    {
        (int cx, int cy) = Native.CursorPos();
        IntRect monitor = Native.Monitors().Select(m => m.Bounds).FirstOrDefault(b => b.Contains(cx, cy));
        if (monitor.IsEmpty) monitor = App.Current.PrimaryMonitor;
        return (monitor.Left + (monitor.Width - _width) / 2, monitor.Top + (monitor.Height - _height) / 2);
    }

    /// <summary>Decodes the PNG into the picture. XAML decodes on its own thread; the bytes stay with the result.</summary>
    private async Task LoadPicture()
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(_png.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (!IsClosed) Picture.Source = bitmap;
        }
        catch (Exception e) { App.Current.Log.Warn("pin picture: " + e.Message); }
    }

    private void Place() { if (!IsClosed) PlacePhysical(_rect); }

#if TONESNIP_HARNESS
    /// <summary>Leak test and UI pass: zooms in and out and fades the pin, as the wheel does, without a pointer.</summary>
    internal void ExerciseForHarness()
    {
        ZoomTo(1.5, _rect.Left, _rect.Top);
        ZoomTo(1, _rect.Left, _rect.Top);
        SetOpacity(0.6);
    }
#endif

    // ----- zoom and opacity -----

    /// <summary>Zooms to <paramref name="zoom"/> keeping the physical point (<paramref name="ax"/>, <paramref name="ay"/>)
    /// where it is on screen.</summary>
    private void ZoomTo(double zoom, int ax, int ay)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (zoom == _zoom) return;
        int w = Math.Max(16, (int)Math.Round(_width * zoom)), h = Math.Max(16, (int)Math.Round(_height * zoom));
        double fx = _rect.Width > 0 ? (double)(ax - _rect.Left) / _rect.Width : 0.5, fy = _rect.Height > 0 ? (double)(ay - _rect.Top) / _rect.Height : 0.5;
        _zoom = zoom;
        _rect = new IntRect(ax - (int)Math.Round(fx * w), ay - (int)Math.Round(fy * h), w, h);
        Place();
        SayStatus();
    }

    /// <summary>Zooms by <paramref name="notches"/> wheel steps about the pin's centre: the keyboard and the menu have no
    /// pointer to zoom about.</summary>
    private void ZoomCentred(int notches)
        => ZoomTo(_zoom * Math.Pow(ZoomStep, notches), _rect.Left + _rect.Width / 2, _rect.Top + _rect.Height / 2);

    /// <summary>The zoom and fade as a screen reader hears them, on the pin's item status; empty at actual size and fully
    /// opaque.</summary>
    private void SayStatus()
    {
        var parts = new List<string>(2);
        if (_zoom != 1) parts.Add($"{_zoom * 100:F0} % zoom");
        if (_opacity < 1) parts.Add($"{_opacity * 100:F0} % opaque");
        AutomationProperties.SetItemStatus(Root, string.Join(", ", parts));
    }

    private void SetOpacity(double opacity)
    {
        _opacity = Math.Clamp(opacity, MinOpacity, 1);
        // A layered window's alpha fades the whole window, content and shadow together. Turned on only when first
        // needed, so a pin that is never faded stays an ordinary window.
        if (!_layered) { Native.AddExStyle(Hwnd, WsExLayered); _layered = true; }
        User32.SetLayeredWindowAttributes(Hwnd, 0, (byte)Math.Round(_opacity * 255), LwaAlpha);
        SayStatus();
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(Root).Properties.MouseWheelDelta;
        if (delta == 0) return;
        e.Handled = true;
        int notches = Math.Sign(delta);
        if ((e.KeyModifiers & global::Windows.System.VirtualKeyModifiers.Control) != 0) { SetOpacity(_opacity + notches * OpacityStep); return; }
        (int cx, int cy) = Native.CursorPos();
        ZoomTo(_zoom * Math.Pow(ZoomStep, notches), cx, cy);
    }

    // ----- dragging -----

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint p = e.GetCurrentPoint(Root);
        if (!p.Properties.IsLeftButtonPressed) return;
        (int cx, int cy) = Native.CursorPos();
        _drag = (cx, cy, _rect.Left, _rect.Top);
        Root.CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } d) return;
        // Screen coordinates from the cursor, not the pointer's window-relative position, which moves with the window.
        (int cx, int cy) = Native.CursorPos();
        _rect = new IntRect(d.Left + cx - d.X, d.Top + cy - d.Y, _rect.Width, _rect.Height);
        Place();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e) { _drag = null; Root.ReleasePointerCaptures(); }
    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e) => _drag = null;

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => CloseBtn.Opacity = 1;
    private void OnPointerExited(object sender, PointerRoutedEventArgs e) { if (CloseBtn.FocusState != FocusState.Keyboard) CloseBtn.Opacity = 0; }
    private void OnCloseFocus(object sender, RoutedEventArgs e) => CloseBtn.Opacity = CloseBtn.FocusState == FocusState.Keyboard ? 1 : CloseBtn.Opacity;
    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => Unpin();

    /// <summary>The main keyboard's = / + and - keys, which VirtualKey does not name.</summary>
    private const global::Windows.System.VirtualKey OemPlus = (global::Windows.System.VirtualKey)0xBB, OemMinus = (global::Windows.System.VirtualKey)0xBD;

    /// <summary>Escape unpins; the arrows move the pin, as a drag would; + and - zoom, or with Ctrl fade, as the wheel
    /// does. Arrows repeat while held; the pin moves in physical pixels, like a drag.</summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        static bool Down(global::Windows.System.VirtualKey key)
            => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & global::Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        bool ctrl = Down(global::Windows.System.VirtualKey.Control);
        int step = Down(global::Windows.System.VirtualKey.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case global::Windows.System.VirtualKey.Escape: Unpin(); break;
            case global::Windows.System.VirtualKey.Left: Move(-step, 0); break;
            case global::Windows.System.VirtualKey.Right: Move(step, 0); break;
            case global::Windows.System.VirtualKey.Up: Move(0, -step); break;
            case global::Windows.System.VirtualKey.Down: Move(0, step); break;
            case global::Windows.System.VirtualKey.Add or OemPlus:
                if (ctrl) SetOpacity(_opacity + OpacityStep); else ZoomCentred(1);
                break;
            case global::Windows.System.VirtualKey.Subtract or OemMinus:
                if (ctrl) SetOpacity(_opacity - OpacityStep); else ZoomCentred(-1);
                break;
            default: return;
        }
        e.Handled = true;
    }

    private void Move(int dx, int dy)
    {
        _rect = _rect.Offset(dx, dy);
        Place();
    }

    // ----- commands -----

    /// <summary>Deferred: closing a WinUI window inside its own input handling breaks its content island.</summary>
    private void Unpin() => DispatcherQueue.TryEnqueue(() => { if (!IsClosed) Close(); });

    private void OnClose(object sender, RoutedEventArgs e) => Unpin();

    private void OnActualSize(object sender, RoutedEventArgs e) => ZoomTo(1, _rect.Left, _rect.Top);
    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomCentred(1);
    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomCentred(-1);
    private void OnActualSizeAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; ZoomTo(1, _rect.Left, _rect.Top); }
    private void OnOpaque(object sender, RoutedEventArgs e) { if (_layered) SetOpacity(1); }

    private void OnCopy(object sender, RoutedEventArgs e) => _ = CopyAsync();
    private void OnCopyAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; _ = CopyAsync(); }

    /// <summary>The PNG is already there, so only the DIB needs the decoded image, on the pool.</summary>
    private async Task CopyAsync()
    {
        try
        {
            await ClipboardWriter.Enqueue(() => ClipboardWriter.Set(Decode(), _png, App.Current.Log));
        }
        catch (Exception ex) { App.Current.Log.Warn("pin copy: " + ex.Message); }
    }

    private void OnCopyText(object sender, RoutedEventArgs e) => _ = CopyTextAsync();
    private void OnCopyTextAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; _ = CopyTextAsync(); }

    private async Task CopyTextAsync()
    {
        try
        {
            BgraImage img = await Task.Run(Decode);
            await App.Current.CopyTextAsync(img);
        }
        catch (Exception ex) { App.Current.Log.Warn("pin copy text: " + ex.Message); }
    }

    private void OnEdit(object sender, RoutedEventArgs e) => _ = EditAsync();

    /// <summary>A fresh result per editor, so the pin's bytes are never the ones an editor rewrites. The PNG is recorded
    /// as the image's encoding, so an editor closed with no edits does not encode it again.</summary>
    private async Task EditAsync()
    {
        try
        {
            BgraImage img = await Task.Run(Decode);
            var result = new CaptureResult { Image = img, Region = new IntRect(0, 0, img.Width, img.Height), AnyHdr = false, Crops = new(), TakenLocal = _taken };
            result.CachePng(img, _png);
            App.Current.OpenViewer(result);
        }
        catch (Exception ex) { App.Current.Log.Warn("pin edit: " + ex.Message); }
    }

    private void OnSaveAs(object sender, RoutedEventArgs e) => _ = SaveAsAsync();
    private void OnSaveAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; _ = SaveAsAsync(); }

    /// <summary>PNG or JPEG through the file picker; a PNG is the held bytes as they are.</summary>
    private async Task SaveAsAsync()
    {
        try
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, SuggestedFileName = Core.Output.FileNaming.Build(_taken, "png") };
            picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
            picker.FileTypeChoices.Add("JPEG image", new List<string> { ".jpg" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
            if (await picker.PickSaveFileAsync() is not { } file) return;
            string path = file.Path;
            int quality = App.Current.Settings.JpegQuality;
            bool jpeg = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
            await Task.Run(() => File.WriteAllBytes(path, jpeg ? Bitmaps.EncodeJpeg(Decode(), quality) : _png));
            App.Current.Log.Info("pin saved " + path);
        }
        catch (Exception ex) { App.Current.Log.Warn("pin save as: " + ex.Message); }
    }
}
