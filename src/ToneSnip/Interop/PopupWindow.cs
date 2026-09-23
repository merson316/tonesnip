using ToneSnip.Core.Geometry;
using ToneSnip.Windows.Interop;
using ToneSnip.Windows.Overlay;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace ToneSnip.App.Interop;

/// <summary>Base for the app's small popup windows: borderless, topmost, hidden from the taskbar and alt-tab, rounded
/// and shadowed by DWM, and never activated unless it needs the keyboard (as the text box does).</summary>
/// <remarks>A WinUI <see cref="Window"/> has no <c>SizeToContent</c>, so the window lays out parked off-screen and is
/// then sized from <see cref="ContentSize"/> through <see cref="PlacePhysical"/>.</remarks>
public class PopupWindow : Window
{
    /// <summary>Room for the first layout pass, off every monitor: the window is only moved on-screen once measured.</summary>
    private static readonly RectInt32 Offscreen = new(-32000, -32000, 2400, 900);

    private readonly bool _activate;
    private readonly bool _shadow;
    private bool _closed;
    private bool _sizeChecked;
    private bool _logged, _repairLogged;
    /// <summary>How often the window has been moved back to its placed rectangle; capped to avoid a resize loop.</summary>
    private int _repairs;
    /// <summary>The rectangle <see cref="PlacePhysical"/> last asked for; empty until the first placement.</summary>
    private IntRect _placed = IntRect.Empty;
    private IntPtr _owner;

    /// <param name="activate">Whether the window takes the foreground when shown (the text box) or never does.</param>
    /// <param name="smallCorners">DWM's small corner radius, for a root drawn at SmallCornerRadius.</param>
    protected PopupWindow(bool activate, bool smallCorners = false)
    {
        _activate = activate;
        this.WhenClosed(() => { _closed = true; App.Current.Log.Debug($"popup closed: {GetType().Name}"); });
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
        }
        AppWindow.IsShownInSwitchers = false;
        Native.AddExStyle(Hwnd, Native.WsExToolWindow | (activate ? 0 : Native.WsExNoActivate));
        Dwm.RoundCorners(Hwnd, small: smallCorners);
        // The content draws its own border; the DWM one would show as a bright outline.
        Dwm.HideBorder(Hwnd);
        // An opaque window cannot paint its own shadow, so it uses the system's.
        _shadow = Frameless.AddShadow(Hwnd);
        AppWindow.MoveAndResize(Offscreen);
        // The presenter's show/size pass can land after the placement and leave the window at the parking size, so
        // any move or resize away from the placed rectangle is undone.
        void OnAppWindowChanged(AppWindow _, AppWindowChangedEventArgs args)
        {
#if TONESNIP_HARNESS
            if (_closed || _parked || _placed.IsEmpty || _repairs >= MaxRepairs) return;
#else
            if (_closed || _placed.IsEmpty || _repairs >= MaxRepairs) return;
#endif
            if (!args.DidSizeChange && !args.DidPositionChange) return;
            SizeInt32 size = AppWindow.Size;
            PointInt32 at = AppWindow.Position;
            if (size.Width == _placed.Width && size.Height == _placed.Height && at.X == _placed.Left && at.Y == _placed.Top) return;
            _repairs++;
            IntRect want = _placed;
            // Deferred: this runs inside the window's own size pass, and placing resizes.
            DispatcherQueue.TryEnqueue(() => { if (!_closed) PlacePhysical(want); });
        }
        AppWindow.Changed += OnAppWindowChanged;
        this.WhenClosed(() => AppWindow.Changed -= OnAppWindowChanged);
    }

    /// <summary>Repairs before the guard gives up.</summary>
    private const int MaxRepairs = 4;

    public IntPtr Hwnd { get; }

    /// <summary>The owner window, or zero (see <see cref="Native.SetOwner"/>). Set it after the show, which runs the
    /// presenter's style pass, and clear it before the owner is destroyed.</summary>
    public IntPtr Owner
    {
        get => _owner;
        set
        {
            if (_owner == value || _closed) return;
            _owner = value;
            Native.SetOwner(Hwnd, value);
        }
    }

    /// <summary>The XAML root, a single <c>Border</c>.</summary>
    protected FrameworkElement Surface => (FrameworkElement)Content;

    protected bool IsClosed => _closed;

    /// <summary>True from the deferred placement after the show, the first moment the window has its own size.</summary>
    protected bool Settled { get; private set; }

    /// <summary>Runs once, when <see cref="Settled"/> becomes true.</summary>
    protected Action? OnSettled { get; set; }

    /// <summary>Where the window is about to be placed, in physical pixels. When set, <see cref="Scale"/> uses that
    /// monitor rather than the XAML root.</summary>
    protected IntRect TargetArea { get; set; } = IntRect.Empty;

    /// <summary>Physical pixels per effective pixel for the monitor this window is being placed on. While parked
    /// off-screen the XAML root reports another monitor's scale, and a later DPI change does not raise SizeChanged, so
    /// <see cref="TargetArea"/> is asked directly when set.</summary>
    protected double Scale => TargetArea.IsEmpty
        ? (Surface.XamlRoot is { } r ? r.RasterizationScale : 1.0)
        : Native.ScaleAt(TargetArea);

    /// <summary>Positions and sizes the window in physical pixels, bypassing the framework's DIP layout.</summary>
    public void PlacePhysical(IntRect r)
    {
        // MoveAndResize flashes on a frameless window, so skip it when the window already sits on this rectangle.
        SizeInt32 size = AppWindow.Size;
        PointInt32 at = AppWindow.Position;
        bool same = r == _placed;
        bool onIt = size.Width == r.Width && size.Height == r.Height && at.X == r.Left && at.Y == r.Top;
        if (same && onIt) return;
        if (same && !_repairLogged)
        {
            _repairLogged = true;
            App.Current.Log.Debug($"popup re-placed: {GetType().Name} sat at {size.Width}x{size.Height} after asking for {r.Width}x{r.Height}");
        }
        _placed = r;
        AppWindow.MoveAndResize(new RectInt32(r.Left, r.Top, r.Width, r.Height));
        if (!_logged) { _logged = true; App.Current.Log.Debug($"popup placed: {GetType().Name} {r.Width}x{r.Height} at {r.Left},{r.Top} (was {size.Width}x{size.Height})"); }
        // The client area should be the whole window (WM_NCCALCSIZE); log once if a frame has come back.
        if (_sizeChecked) return;
        _sizeChecked = true;
        SizeInt32 client = AppWindow.ClientSize;
        if (client.Width != r.Width || client.Height != r.Height)
            App.Current.Log.Warn($"popup client {client.Width}x{client.Height} differs from the {r.Width}x{r.Height} window asked for");
    }

    /// <summary>Shows the window (activating it only if requested) and runs <paramref name="placed"/> once the content
    /// is laid out, and again whenever it changes size.</summary>
    protected void ShowPopup(Action placed)
    {
        Surface.Loaded += (_, _) => placed();
        // Deferred: SizeChanged arrives inside the layout pass, and placing resizes.
        Surface.SizeChanged += (_, _) => DispatcherQueue.TryEnqueue(() => { if (!_closed) placed(); });
        if (_activate) { Activate(); Win32.ForceForeground(Hwnd, App.Current.Log); }
        else AppWindow.Show(activateWindow: false);
        // The presenter's first size pass can land after the Loaded placement, so one more low-priority pass re-asserts
        // the rectangle. It does not measure again: a second unbounded measure of a realized tree can answer
        // differently (an empty TextBox reports its last arranged extent).
        void Settle()
        {
            if (_closed) return;
            Settled = true;
            if (_placed.IsEmpty) placed(); else PlacePhysical(_placed);
            OnSettled?.Invoke();
        }
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Settle)) Settle();
        // The presenter's style pass can remove the frame, and with it the shadow; reapply and log the outcome.
        if (_shadow && !Frameless.HasFrame(Hwnd))
            App.Current.Log.Debug("popup shadow frame reapplied after show: " + (Frameless.ApplyFrame(Hwnd) ? "held" : "stripped again"));
    }

#if TONESNIP_HARNESS
    /// <summary>Harness only. Parks the window at its first-show size, waits for the XAML root to see that size, and
    /// reports what <see cref="ContentSize"/> answers there, to check a popup measures correctly while parked.</summary>
    internal async Task<string> MeasureParkedForHarnessAsync()
    {
        IntRect back = _placed;
        // Reset in finally so a throw cannot leave the repair guard off. The window may close itself on deactivation.
        _parked = true;
        try
        {
            AppWindow.MoveAndResize(Offscreen);
            double want = Offscreen.Height / Scale - 1;
            int waited = 0;
            while (!_closed && waited < 1500 && (Surface.XamlRoot?.Size.Height ?? 0) < want) { await Task.Delay(25); waited += 25; }
            if (_closed) return "the window closed while parked";
            Surface.UpdateLayout();
            double rootH = Surface.XamlRoot?.Size.Height ?? 0;
            (int w, int h) = ContentSize();
            IntRect asked = _placed;
            _parked = false;
            if (!back.IsEmpty) PlacePhysical(back);
            return $"root {rootH:0} DIP tall while parked (after {waited} ms), content answers {w}x{h}, the re-place meanwhile asked for {asked.Width}x{asked.Height}";
        }
        finally { _parked = false; }
    }
    /// <summary>Set while <see cref="MeasureParkedForHarnessAsync"/> has the window parked; disables the repair
    /// guard.</summary>
    private bool _parked;
#endif

    /// <summary>The content's desired size in physical pixels, measured unbounded as <c>SizeToContent</c> would;
    /// measuring against the client area would pin it to the previous size.</summary>
    protected (int Width, int Height) ContentSize()
    {
        Surface.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double s = Scale;
        // The window's constraint is not restored here: the placement that follows re-measures the tree.
        return ((int)Math.Ceiling(Surface.DesiredSize.Width * s), (int)Math.Ceiling(Surface.DesiredSize.Height * s));
    }
}
