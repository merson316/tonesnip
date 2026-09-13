using ToneSnip.App.Interop;
using ToneSnip.App.Theme;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Config;
using ToneSnip.Core.Geometry;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace ToneSnip.App.Overlay;

/// <summary>
/// The capture bar: modes, delay, the annotate switch and (expanded) the shared tool row. Borderless, topmost and
/// never activated, so every click on it leaves the keyboard with the overlay underneath.
/// </summary>
public sealed partial class ToolbarWindow : PopupWindow
{
    /// <summary>Fade-in duration for a band's content and for the window itself. Heights are not animated: the window is
    /// resized per layout pass, so content can only appear in place.</summary>
    private const double FadeMs = 120;

    private readonly OverlaySession _session;
    private readonly IntRect _monitor;
    private Storyboard? _fade;
    private int _delay;
    private bool _ready;
    private bool _firstPlace = true;
    private bool _attached;

    public ToolbarWindow(OverlaySession session, IntRect monitor) : base(activate: false)
    {
        _session = session; _monitor = monitor;
        TargetArea = monitor;                 // measure and place at this monitor's scale, not the parking corner's
        InitializeComponent();
        // The bar, its bands and the tool row follow the app theme; the GDI chrome the overlay draws itself does not.
        Theme.ThemeManager.Attach(this);
        // The delay pill's brushes are assigned from code, so they are re-read on a theme change.
        Root.ActualThemeChanged += OnThemeChanged;
        this.WhenClosed(() => Root.ActualThemeChanged -= OnThemeChanged);
        _delay = SnipSettings.Delays.Contains(session.Settings.DefaultDelay) ? session.Settings.DefaultDelay : 0;
        DelayRow.Seconds = _delay;
        DelayRow.Picked += OnDelayPicked;
        Modes.ModeClicked += OnModeClicked;
        Modes.ShowShortcuts = true;   // the overlay bar is the only place the letters mean anything
        ShowDelay(open: false);
        Bar.PanelOpened += CollapseDelayRow;           // one picker open at a time, across both bars
        // The root Border's SizeChanged does not fire when the tool row resizes inside a window that has not resized
        // yet, so the window follows the bar's SizeChanged instead.
        Bar.SizeChanged += (_, _) => DispatcherQueue.TryEnqueue(Reposition);
        this.WhenClosed(Bar.Detach);
        ShowPopup(OnPlaced);
    }

    private void OnPlaced()
    {
        if (IsClosed) return;
        _ready = true;
        if (!_firstPlace) { Reposition(); return; }
        _firstPlace = false;
        Refresh();
        Surface.UpdateLayout();
        Reposition();
        // An opaque window cannot slide or grow into place, so the bar fades in where it lands.
        FadeIn(Root);
    }

    /// <summary>Centres the toolbar near the top of its monitor, in physical pixels. The 20 px inset is the design's
    /// 8 px plus the 12 px margin the window keeps for its drop shadow.</summary>
    private void Reposition()
    {
        if (!_ready || IsClosed) return;
        (int w, int h) = ContentSize();
        double s = Scale;
        PlacePhysical(new IntRect(_monitor.Left + (_monitor.Width - w) / 2, _monitor.Top + (int)(20 * s), w, h));
    }

    public void Refresh()
    {
        Modes.Mode = _session.Mode;
        BtnAnnotate.IsChecked = _session.Annotating;
        if (_session.Annotating && _session.Edit != null && !_attached)
        {
            Bar.Attach(_session.Edit, _session.AnyHdr, showCrop: false, showDone: false, _session.Accent, "No HDR monitor in this snip");
            Bar.Interacted += () => { _session.UseDrawTool(); _session.RefocusOverlay(); };
            Bar.StyleChanged += _session.PersistStyle;
            Bar.ExposureChanged += _session.SetExposure;
            Bar.ZebraChanged += _ => _session.RefreshBackBuffers();
            // Deferred: Done closes this window, and a window torn down inside its own button's click takes the XAML
            // island with it.
            Bar.DoneClicked += () => DispatcherQueue.TryEnqueue(_session.Done);
            // Auto exposure picks the multiplier per output when the snip is built, so an exposure set here could not
            // match the saved file. (The viewer's slider multiplies the auto value, so it stays enabled there.)
#if TONESNIP_HARNESS
            // The screenshot harness's synthetic HDR session must render the same regardless of local settings.
            if (_session.AnyHdr && App.Current.Settings.AutoExposure && !App.Current.ScreenshotMode)
#else
            if (_session.AnyHdr && App.Current.Settings.AutoExposure)
#endif
                Bar.SetExposureEnabled(false, "Turn off auto exposure to adjust exposure here");
            _attached = true;
        }
        ShowBand(ToolRow, _session.Annotating);
        Bar.SetDone(_session.ShowDone);
        Bar.Refresh();
        Reposition();
    }

    /// <summary>The toolbar window never activates, so a click on the frozen desktop closes its pickers from here.</summary>
    public void ClosePopups()
    {
        Bar.ClosePopups();
        CollapseDelayRow();
    }

#if TONESNIP_HARNESS
    /// <summary>Logs the window's measured size. A PrintWindow shot can be a frame stale, so the harness asserts band
    /// geometry from this log line instead.</summary>
    internal void LogSize(string tag)
    {
        (int w, int h) = ContentSize();
        App.Current.Log.Debug($"toolbar \"{tag}\": {w} x {h} physical at scale {Scale:0.##}");
        Console.WriteLine($"toolbar \"{tag}\": {w} x {h}");
    }

    /// <summary>Screenshot harness: opens the delay band, or one of the tool row's picker bands, without a click.</summary>
    internal void OpenBand(string name)
    {
        if (name == "delay") { DelayRow.Seconds = _delay; ShowBand(DelayRow, true); ShowDelay(open: true); }
        else Bar.OpenPanel(name);
        Reposition();
    }
#endif

    private void CollapseDelayRow()
    {
        if (DelayRow.Visibility != Visibility.Visible) return;
        ShowBand(DelayRow, false);
        ShowDelay(open: false);
        Reposition();
    }

    /// <summary>Tool letters and Ctrl+Z/Y while the overlay has focus; the tool row owns them.</summary>
    public Annotate.AnnotateBar.KeyHandled HandleAnnotateKey(int vk, bool ctrl) => Bar.HandleKey(vk, ctrl);

    private void OnAnnotate(object sender, RoutedEventArgs e)
    {
        ClosePopups();
        _session.Annotating = BtnAnnotate.IsChecked == true;
        _session.RefocusOverlay();
    }

    /// <summary>No Refresh here: the session's Mode setter raises ToolbarChanged when anything needs redrawing, and
    /// ModeGroup has already restored its checked state.</summary>
    private void OnModeClicked(SnipMode mode)
    {
        ClosePopups();
        _session.Mode = mode;
        _session.RefocusOverlay();
    }

    private void OnDelayPicker(object sender, RoutedEventArgs e)
    {
        Bar.ClosePopups();
        bool open = DelayRow.Visibility != Visibility.Visible;
        DelayRow.Seconds = _delay;
        ShowBand(DelayRow, open);
        ShowDelay(open);
        Reposition();
    }

    private void OnDelayPicked(int seconds)
    {
        _delay = seconds;
        DelayRow.Seconds = seconds;
        ShowBand(DelayRow, false);
        ShowDelay(open: false);
        // Restarting closes the overlay and this window with it; let the click finish going through the island first.
        if (seconds > 0) { DispatcherQueue.TryEnqueue(() => _session.RestartWithDelay(seconds)); return; }
        Reposition();
        _session.RefocusOverlay();
    }

    /// <summary>
    /// Re-reads the delay pill's brushes, which were copied from the Tok swatches and go stale on a theme change.
    /// <para>ActualThemeChanged rather than <c>ThemeManager.Changed</c>, because Changed fires before the framework has
    /// re-resolved the swatches' ThemeResources.</para>
    /// </summary>
    private void OnThemeChanged(FrameworkElement sender, object args) => ShowDelay(DelayRow.Visibility == Visibility.Visible);

    /// <summary>The pill carries the delay a restart would use: secondary while there is none, primary once one is set,
    /// and filled while its band is open.</summary>
    private void ShowDelay(bool open)
    {
        DelayBtn.Content = _delay == 0 ? "No delay" : $"{_delay} s";
        DelayBtn.Foreground = (_delay == 0 ? TokSecondary : TokPrimary).Background;
        // Cleared rather than set to null: the style's Transparent is what keeps the pill's padding clickable.
        if (open) DelayBtn.Background = TokLayer.Background;
        else DelayBtn.ClearValue(Microsoft.UI.Xaml.Controls.Control.BackgroundProperty);
    }

    /// <summary>Shows or hides a band, fading its content in when it appears. The caller re-places the window.</summary>
    private void ShowBand(FrameworkElement band, bool show)
    {
        bool was = band.Visibility == Visibility.Visible;
        band.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && !was) FadeIn(band);
    }

    private void FadeIn(FrameworkElement element)
    {
        // One fade at a time. Stopping drops the element back to its local Opacity of 1, so a fade cut short leaves its
        // band fully drawn.
        _fade?.Stop();
        _fade = null;
        element.Opacity = 1;
        if (!ThemeManager.AnimationsEnabled) return;
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(FadeMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        _fade = new Storyboard();
        _fade.Children.Add(fade);
        _fade.Begin();
    }

    // Deferred: Finish destroys this window, so it waits for the click to be delivered.
    private void OnClose(object sender, RoutedEventArgs e) => DispatcherQueue.TryEnqueue(() => _session.Finish(OverlayOutcome.Cancelled));
}
