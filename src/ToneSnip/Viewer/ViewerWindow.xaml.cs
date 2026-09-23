using System.Diagnostics;
using ToneSnip.App.Annotate;
using ToneSnip.App.Capture;
using ToneSnip.App.Interop;
using ToneSnip.App.Output;
using ToneSnip.App.Theme;
using ToneSnip.Core.Annotate;
using ToneSnip.Core.Config;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Imaging;
using ToneSnip.Windows.Imaging;
using Shell = ToneSnip.Windows.Shell;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;

namespace ToneSnip.App.Viewer;

/// <summary>Viewer and editor for one snip: the picture, the shared tool row, crop and save in place.</summary>
public sealed partial class ViewerWindow : Window
{
    private static readonly double[] Stops = { 0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4 };
    /// <summary>Escape is decoded by hand rather than as a <see cref="KeyboardAccelerator"/>: it drops one thing at a
    /// time (the overflow menu, an open picker, a marquee, a selection) and only then closes the window.</summary>
    private const int VkEscape = 0x1B;
    /// <summary>The tool row's own height: the shared 40 px row under a 1 px hairline.</summary>
    private const double ToolRowHeight = 41;
    /// <summary>How long "HDR copy written" stays up, and the fade that takes it away.</summary>
    private static readonly TimeSpan HdrDoneFor = TimeSpan.FromSeconds(2);
    private const double HdrDoneFadeMs = 300;

    private readonly CaptureResult _result;
    private readonly IntPtr _hwnd;
    private readonly uint _accent;
    private double? _zoom;   // null = fit
    private bool _annotating;
    private bool _statePending;
    private bool _closed;
    private (int W, int H) _viewSize;
    /// <summary>Exposure the file on disk was written with; the "edited" dot compares against it.</summary>
    private float _savedExposure;
    /// <summary>The window's dispatcher, read once on the UI thread for handlers raised elsewhere.</summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _ui;
    /// <summary>
    /// Serialises Save, Save as, Copy and the close prompt's save, which each render and write off the UI thread; two
    /// at once could both pass the <see cref="WritesPending"/> check and write the same file together.
    /// </summary>
    private readonly SemaphoreSlim _outputGate = new(1, 1);
    /// <summary>Last style picked in the tool row, written once on close: every palette click would otherwise rewrite
    /// settings.json, re-apply the theme and re-register the hotkeys.</summary>
    private Core.Annotate.Style? _styleToSave;
    /// <summary>The HDR sidecar write in flight, if any. Save, the exposure loop and the close all wait on it.</summary>
    private Task? _hdrWrite;
    /// <summary>The SDR encode and file write in flight, if any. Part of <see cref="WritesPending"/>, so a second Save
    /// cannot write the same file and the close waits for the bytes to land.</summary>
    private Task? _encode;
    private int _hdrGeneration;
    /// <summary>Outcome of the last finished sidecar write. The close checks it: a Save onto an HDR-only target
    /// (.jxr/.hdr.png/.hdr.jpg) has nothing else on disk to show for itself, so a failed write must not close.</summary>
    private bool _hdrWriteOk = true;
    /// <summary>True once the close prompt has been answered and the window may go for real; WinUI cannot cancel
    /// <see cref="Window.Closed"/>, so the decision is taken in <see cref="AppWindow"/>.Closing instead.</summary>
    private bool _closeApproved;
    private bool _closePending;
    /// <summary>True while the command bar is being written from the document; the slider must not write back.</summary>
    private bool _syncing;
    /// <summary>The tool row's open/close animation, held so a second toggle can cancel the first.</summary>
    private Storyboard? _toolRowAnim;
    /// <summary>Clips the tool row to its current height so the bar never paints outside it while the height animates.
    /// Updated from the row's SizeChanged.</summary>
    private readonly RectangleGeometry _toolRowClip = new();
    /// <summary>The "HDR copy written" note's dwell timer and its fade, both cancelled when the window closes.</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _hdrDoneTimer;
    /// <summary>The dwell timer's handler, kept so it can be detached: a stopped DispatcherQueueTimer still holds its
    /// Tick, and the Tick holds this window.</summary>
    private global::Windows.Foundation.TypedEventHandler<Microsoft.UI.Dispatching.DispatcherQueueTimer, object>? _hdrDoneTick;
    private Storyboard? _hdrDoneFade;
    /// <summary>The minimum-size clamp, kept so it can be detached when the window goes.</summary>
    private global::Windows.Foundation.TypedEventHandler<AppWindow, AppWindowChangedEventArgs>? _onAppWindowChanged;
    private bool _clamping;
    /// <summary>The pan in flight (a middle-button drag, or the left button with space held): where the pointer went
    /// down, in the ScrollViewer's own frame, and the offsets it started from. Null when nothing is panning.</summary>
    private global::Windows.Foundation.Point? _panFrom;
    private (double H, double V) _panOffsets;
    /// <summary>Space is held: the pointer belongs to the view rather than to the document, whichever tool is up.</summary>
    private bool _spaceDown;

    public ViewerWindow(CaptureResult result, bool annotate = false)
    {
        _ui = DispatcherQueue;
        _result = result;
        _savedExposure = result.Exposure;
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        Backdrop.Apply(this, RootGrid);   // Mica, or a solid root where Windows has none
        ResizeToDefault();
        ClampMinSize();
        ThemeManager.ApplyTitleBarTheme(AppWindow);
        ThemeManager.Changed += OnThemeChanged;
        ThemeManager.Attach(this);
        // Read once on purpose: an "accent" stroke is baked into the saved image, so a live value would repaint strokes
        // the user already drew whenever the desktop accent or theme changed.
        _accent = ThemeManager.AccentArgb;

        Surface.Load(result, _accent);
        Surface.BeforeExposure = WritesPending;   // a tonemap pass must not rewrite the image a sidecar write is reading
        _viewSize = (Surface.View.Width, Surface.View.Height);
        Surface.Rendered += OnSurfaceRendered;
        Surface.Session.TextRequested += OnTextRequested;
        Surface.Session.Changed += OnSessionChanged;
        Surface.ExposureApplied += QueueState;      // once the throttled exposure pass has actually landed
        // Exposure and zebra are in this window's command bar, so the slider talks to the surface directly.
        Bar.Attach(Surface.Session, hdr: result.Crops.Count > 0, showCrop: true, showDone: false, _accent, showHdrControls: false);
        Bar.Interacted += OnBarInteracted;          // keep the keyboard on the picture after a tool-row click
        Bar.StyleChanged += OnStyleChanged;         // written once, when the window closes
        // The HDR crops are fixed for the life of the window, so the group is shown for the whole edit or not at all.
        HdrGroup.Visibility = result.Crops.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OpenFolder.IsEnabled = result.SavedPath != null;
        ToolRow.Clip = _toolRowClip;
        Controls.AppIcon.SetWindowIcon(this);
        UpdateTitle();

        // handledEventsToo: a focused tool-row button marks Enter and space handled, and the bar's own shortcuts
        // (Enter applies a crop) must still reach HandleKey.
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: true);
        RootGrid.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(OnKeyUp), handledEventsToo: true);
        // On the surface itself: the wheel event bubbles, so a handler on an ancestor would run after the
        // ScrollViewer had already scrolled.
        Surface.PointerWheelChanged += OnWheel;
        // Also on the ScrollViewer, for the area around a picture smaller than the viewport. handledEventsToo, because
        // over the picture the surface's handler has already marked the event handled, which OnWheel checks.
        Scroll.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnWheel), handledEventsToo: true);
        // Panning (a middle-button drag, or space with the left button) bubbles up to the ScrollViewer.
        Scroll.PointerPressed += OnPanStart;
        Scroll.PointerMoved += OnPanMove;
        Scroll.PointerReleased += OnPanEnd;
        // The Win2D control covers only what is on screen (EditorSurface.SetViewport), so it follows every scroll,
        // viewport change and zoom.
        Scroll.ViewChanging += (_, e) => UpdateSurfaceViewport(e.NextView.HorizontalOffset - Scroll.HorizontalOffset, e.NextView.VerticalOffset - Scroll.VerticalOffset);
        Scroll.ViewChanged += (_, _) => UpdateSurfaceViewport();
        Surface.SizeChanged += (_, _) => UpdateSurfaceViewport();
        Scroll.PointerCaptureLost += OnPanEnd;
        // A window that loses activation never sees the key-up, so space would otherwise stay "held".
        this.WhenActivated(e => { if (e.WindowActivationState == WindowActivationState.Deactivated) ReleaseSpace(); });
        RootGrid.Loaded += (_, _) =>
        {
            // A move to a monitor with another scale changes what "100 %" is in effective pixels.
            if (RootGrid.XamlRoot is { } root && !_rootHooked) { root.Changed += OnXamlRootChanged; _rootHooked = true; }
            ApplyZoom();
            if (annotate) SetAnnotating(true);
        };
        AppWindow.Closing += OnAppWindowClosing;
        this.WhenClosed(OnClosed);
        UpdateState();
    }

    // ----- window chrome -----

    /// <summary>1100×750 at the monitor's own scale.</summary>
    private void ResizeToDefault()
    {
        double scale = Native.Scale(_hwnd);
        AppWindow.Resize(new SizeInt32((int)(1100 * scale), (int)(750 * scale)));
    }

    /// <summary>OverlappedPresenter has no minimum-size property, so a drag below 520×360 is resized straight back up.</summary>
    private void ClampMinSize()
    {
        _onAppWindowChanged = OnAppWindowChanged;
        AppWindow.Changed += _onAppWindowChanged;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs e)
    {
        // Resize below raises Changed again; without the guard the clamp re-enters itself on every drag frame.
        if (!e.DidSizeChange || _clamping) return;
        double scale = Native.Scale(_hwnd);
        int minW = (int)(520 * scale), minH = (int)(360 * scale);
        SizeInt32 size = sender.Size;
        if (size.Width >= minW && size.Height >= minH) return;
        _clamping = true;
        try { sender.Resize(new SizeInt32(Math.Max(size.Width, minW), Math.Max(size.Height, minH))); }
        finally { _clamping = false; }
    }

    /// <summary>ThemeManager.Changed may be raised on a pool thread, so the update is queued to the UI thread and skipped
    /// after the close.</summary>
    private void OnThemeChanged() => _ui.TryEnqueue(() => { if (!_closed) ThemeManager.ApplyTitleBarTheme(AppWindow); });

    /// <summary>"‹file› — ToneSnip" once the snip has a file name, shown in the title bar, taskbar and Alt+Tab.</summary>
    private void UpdateTitle()
    {
        string? name = _result.SavedPath is { } p ? Path.GetFileName(p) : null;
        Title = name == null ? "ToneSnip" : $"{name} — ToneSnip";
    }

    // ----- annotate mode -----

    private void SetAnnotating(bool on)
    {
        _annotating = on; AnnotateBtn.IsChecked = on; Surface.Editing = on;
        ShowToolRow(on);
        if (on) { Surface.Session.Tool = Tool.Pen; Bar.Refresh(); Surface.Focus(FocusState.Pointer); }
        else
        {
            Bar.ClosePopups();
            while (Surface.Session.Escape()) { }   // one Escape drops one thing: marquee, drag and selection must all go
            Surface.Session.Tool = Tool.Select;
        }
#if TONESNIP_HARNESS
        // Logged for the harness, which cannot read the pointer shape through UIA.
        if (App.Current.ScreenshotMode) App.Current.Log.Debug($"editor: annotate {(on ? "on" : "off")}, tool {Surface.Session.Tool}, cursor {Surface.CursorName}");
#endif
    }

    /// <summary>
    /// Opens and closes the tool row with a dependent Height animation, so the layout below follows. The height is
    /// returned to Auto at the end so an opening picker band can still grow the row.
    /// </summary>
    private void ShowToolRow(bool on)
    {
        _toolRowAnim?.Stop();
        _toolRowAnim = null;
        if (!ThemeManager.AnimationsEnabled)
        {
            ToolRow.Height = double.NaN;
            ToolRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        double from = ToolRow.Visibility == Visibility.Visible ? ToolRow.ActualHeight : 0;
        ToolRow.Visibility = Visibility.Visible;
        double to = 0;
        if (on)
        {
            // Its natural height, with the constant as a fallback for a measure that comes back empty.
            ToolRow.Height = double.NaN;
            ToolRow.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            to = ToolRow.DesiredSize.Height >= 1 ? ToolRow.DesiredSize.Height : ToolRowHeight;
        }
        ToolRow.Height = from;
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(167)),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, ToolRow);
        Storyboard.SetTargetProperty(anim, "Height");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_toolRowAnim, sb)) return;   // a newer toggle already owns the row
            _toolRowAnim = null;
            ToolRow.Height = double.NaN;
            if (!on) ToolRow.Visibility = Visibility.Collapsed;
        };
        _toolRowAnim = sb;
        sb.Begin();
    }

#if TONESNIP_HARNESS
    /// <summary>Screenshot harness: drives the editor into a state (annotate on, a picker band open, zebra on) without a
    /// click.</summary>
    internal void ShowState(bool annotate, string? panel = null, bool zebra = false)
    {
        if (annotate) SetAnnotating(true);
        if (zebra && HdrGroup.Visibility == Visibility.Visible) { ZebraBtn.IsChecked = true; OnZebra(ZebraBtn, new RoutedEventArgs()); }
        if (panel != null) Bar.OpenPanel(panel);
    }
#endif

    private void OnToolRowSize(object sender, SizeChangedEventArgs e)
        => _toolRowClip.Rect = new global::Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);

    private void OnAnnotate(object sender, RoutedEventArgs e) => SetAnnotating(AnnotateBtn.IsChecked == true);
    private void OnBarInteracted() => Surface.Focus(FocusState.Pointer);
    private void OnStyleChanged(Core.Annotate.Style s) => _styleToSave = s;
    private void OnSessionChanged(Core.Geometry.IntRect dirty) => QueueState();

    // ----- exposure and zebra -----

    /// <summary>Exposure is view state, not a document edit: refresh the dot ourselves.</summary>
    private void OnExposureValue(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing) return;
        float multiplier = (float)Math.Pow(2, ExposureSlider.Value);
        Surface.Session.Doc.Exposure = multiplier;
        ExposureText.Text = Ev(ExposureSlider.Value);
        Surface.SetExposure(multiplier);
        QueueState();
    }

    /// <summary>Click, not Checked: a programmatic IsChecked from <see cref="UpdateState"/> must not loop back.</summary>
    private void OnZebra(object sender, RoutedEventArgs e)
    {
        Surface.Session.Doc.Zebra = ZebraBtn.IsChecked == true;
        Surface.SetZebra(Surface.Session.Doc.Zebra);
    }

    private static string Ev(double ev) => ev == 0 ? "0 EV" : $"{ev:+0.00;-0.00} EV";

    /// <summary>Colour, width and text size stick for the next snip; the file is written once, when the window closes.</summary>
    private void SaveStyle() => App.Current.RememberAnnotateStyle(_styleToSave, App.Current.Settings.Annotate, _accent);

    /// <summary>
    /// Escape, the annotate bar's tool letters, and space (which arms the pan). The other shortcuts are
    /// <see cref="KeyboardAccelerator"/>s on their commands.
    /// </summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (FocusManager.GetFocusedElement(RootGrid.XamlRoot) is TextBox) return;
        int vk = (int)e.Key;
        bool ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Control)
                     & global::Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (vk == VkEscape)
        {
            // The overflow menu goes first. A MenuFlyout is a windowed popup that does not take activation, so an
            // Escape can still be routed through the editor's tree while the menu is up; without this check it would
            // close the window instead.
            if (MoreButton.IsFlyoutOpen) { MoreButton.CloseFlyout(); e.Handled = true; return; }
            // The bar's pickers have no focus of their own, so the host that owns the keyboard closes them.
            if (_annotating && Bar.AnyPopupOpen) { Bar.ClosePopups(); e.Handled = true; return; }
            if (_annotating && Surface.Session.Escape()) { e.Handled = true; return; }
            Close(); return;
        }
        // Not marked handled, so space still invokes a focused button. Panning is armed only when there is somewhere
        // to pan to.
        if (e.Key == global::Windows.System.VirtualKey.Space && !ctrl) { _spaceDown = true; Surface.Panning = CanPan(); }
        if (_annotating && Bar.HandleKey(vk, ctrl) != AnnotateBar.KeyHandled.No) e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Space) ReleaseSpace();
    }

    /// <summary>Space is no longer held. A pan already under way continues until the button comes up.</summary>
    private void ReleaseSpace()
    {
        _spaceDown = false;
        if (_panFrom == null) Surface.Panning = false;
    }

    /// <summary>Ctrl+S, on the window because the Save button is collapsed until there is something to save. An unsaved
    /// snip falls through to Save as.</summary>
    private void OnSaveAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        _ = SaveWhenIdle(_result.SavedPath == null);
        args.Handled = true;
    }

    /// <summary>The Annotate toggle's accelerator. Explicit because the default action runs the peer's Invoke pattern,
    /// which a ToggleButton does not implement.</summary>
    private void OnAnnotateAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SetAnnotating(!_annotating);
        args.Handled = true;
    }

    // ----- the wheel and the pan -----

    /// <summary>
    /// Plain wheel and Ctrl+wheel zoom, anchored on the pointer; Shift+wheel pans horizontally. Wired on both the
    /// surface and the ScrollViewer (see the constructor).
    /// </summary>
    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled) return;   // the surface's copy already answered this one
        int delta = e.GetCurrentPoint(Scroll).Properties.MouseWheelDelta;
        if (delta == 0) return;
        if ((e.KeyModifiers & global::Windows.System.VirtualKeyModifiers.Shift) != 0)
        {
            // Done by hand: this handler consumes the event, so the ScrollViewer's own Shift handling never runs.
            Scroll.ChangeView(Scroll.HorizontalOffset - delta, null, null, disableAnimation: true);
            e.Handled = true;
            return;
        }
        StepAt(delta > 0 ? 1 : -1, e.GetCurrentPoint(Scroll).Position);
        e.Handled = true;
    }

    /// <summary>
    /// One zoom stop that keeps the image pixel under <paramref name="at"/> (in the ScrollViewer's frame) in place. The
    /// surface is centred while smaller than the viewport, so that slack is taken out of the arithmetic.
    /// </summary>
    private void StepAt(int dir, global::Windows.Foundation.Point at)
    {
        double before = _zoom ?? FitScale();
        double slackX = Math.Max(0, (Scroll.ViewportWidth - Surface.ActualWidth) / 2);
        double slackY = Math.Max(0, (Scroll.ViewportHeight - Surface.ActualHeight) / 2);
        double ix = (Scroll.HorizontalOffset + at.X - slackX) / before;
        double iy = (Scroll.VerticalOffset + at.Y - slackY) / before;
        Step(dir);
        double after = _zoom ?? FitScale();
        if (Math.Abs(after - before) < 1e-9) return;   // already at the end of the stops
        // The ScrollViewer's extent only follows the new size on the next layout pass, and ChangeView clamps against
        // the current extent, so the pass is forced.
        Scroll.UpdateLayout();
        slackX = Math.Max(0, (Scroll.ViewportWidth - Surface.ActualWidth) / 2);
        slackY = Math.Max(0, (Scroll.ViewportHeight - Surface.ActualHeight) / 2);
        Scroll.ChangeView(slackX + ix * after - at.X, slackY + iy * after - at.Y, null, disableAnimation: true);
    }

    /// <summary>Is the picture bigger than the viewport in either direction? Nothing pans at or below fit.</summary>
    private bool CanPan() => Scroll.ScrollableWidth > 0 || Scroll.ScrollableHeight > 0;

    /// <summary>A middle-button drag, or the left button with space held, pans the view when there is something to
    /// scroll.</summary>
    private void OnPanStart(object sender, PointerRoutedEventArgs e)
    {
        if (_panFrom != null) return;
        Microsoft.UI.Input.PointerPoint p = e.GetCurrentPoint(Scroll);
        if (!p.Properties.IsMiddleButtonPressed && !(_spaceDown && p.Properties.IsLeftButtonPressed)) return;
        if (!CanPan()) return;
        _panFrom = p.Position;
        _panOffsets = (Scroll.HorizontalOffset, Scroll.VerticalOffset);
        Surface.Panning = true;
        Scroll.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPanMove(object sender, PointerRoutedEventArgs e)
    {
        if (_panFrom is not { } from) return;
        global::Windows.Foundation.Point now = e.GetCurrentPoint(Scroll).Position;
        Scroll.ChangeView(_panOffsets.H - (now.X - from.X), _panOffsets.V - (now.Y - from.Y), null, disableAnimation: true);
        e.Handled = true;
    }

    /// <summary>The button came up, or capture was lost. If space is still held the canvas stays in pan mode.</summary>
    private void OnPanEnd(object sender, PointerRoutedEventArgs e)
    {
        if (_panFrom == null) return;
        _panFrom = null;
        Surface.Panning = _spaceDown && CanPan();
        Scroll.ReleasePointerCaptures();
    }

    /// <summary>Coalesces the state refresh: the session fires Changed on every mouse move of a stroke.</summary>
    private void QueueState()
    {
        if (_statePending || _closed) return;
        _statePending = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { _statePending = false; if (!_closed) UpdateState(); });
    }

    /// <summary>Whether this editor is on <paramref name="r"/>: the same result, or a result for the same saved file.</summary>
    internal bool Shows(CaptureResult r)
        => ReferenceEquals(_result, r) || (r.SavedPath != null && string.Equals(_result.SavedPath, r.SavedPath, StringComparison.OrdinalIgnoreCase));

    private bool Edited() => Surface.Session.Doc.HasEdits || Surface.Session.Doc.Exposure != _savedExposure;

    /// <summary>
    /// Writes a snapshot of the editor's document (already translated back to the desktop frame) into
    /// <see cref="_result"/>, which the HDR sidecar is built from. <see cref="EditorSurface.Load"/> only copies out of
    /// the result, so without this the sidecar would miss editor changes, including redactions. Call on the UI thread
    /// before any call into <see cref="HdrOutput"/>.
    /// </summary>
    private void SyncResultFromSession(Rendered rendered)
    {
        _result.Doc = rendered.Doc;
        _result.Exposure = rendered.Exposure;
    }

    /// <summary>What a save's pixels were rendered from, taken together with <see cref="Output"/>. The editor stays live
    /// during the encode, so marking saved and building the HDR copy must use this snapshot, not the document's later
    /// state, or an edit made mid-write would be marked saved without being in the file.</summary>
    private readonly record struct Rendered(long Revision, float Exposure, AnnotationDoc Doc);

    private Rendered Snapshot() => new(Surface.Session.Doc.Revision, Surface.Session.Doc.Exposure,
                                       Surface.Session.Doc.Translated(_result.Region.Left, _result.Region.Top));

    private void UpdateState()
    {
        bool edited = Edited();
        Modified.Visibility = edited ? Visibility.Visible : Visibility.Collapsed;
        SaveBtn.Visibility = edited && _result.SavedPath != null ? Visibility.Visible : Visibility.Collapsed;
        Dimensions.Text = $"{Surface.View.Width} × {Surface.View.Height}";
        // The colour pipeline, for an HDR snip only: what the picture came from and how it got to SDR.
        HdrPipeline.Text = _result.AnyHdr ? $"HDR · scRGB → SDR ({TonemapName(App.Current.Settings.Tonemap)})" : "";
        HdrSeparator.Visibility = HdrPipeline.Visibility = _result.AnyHdr ? Visibility.Visible : Visibility.Collapsed;
        // The name, not the path, which would crowd the one-line strip; the path is the tooltip.
        PathText.Text = _result.SavedPath is { } saved ? Path.GetFileName(saved) : "";
        ToolTipService.SetToolTip(PathText, _result.SavedPath);
        // The tooltip is hover-only on a TextBlock that cannot take focus; the full path goes to UIA as well.
        AutomationProperties.SetHelpText(PathText, _result.SavedPath ?? "");
        PathSeparator.Visibility = _result.SavedPath != null ? Visibility.Visible : Visibility.Collapsed;
        // Exposure and zebra are this window's controls, not the bar's, so they are synced here.
        _syncing = true;
        double ev = Math.Log2(Surface.Session.Doc.Exposure);
        ExposureText.Text = Surface.Session.Doc.Exposure == 1f ? "0 EV" : Ev(ev);
        if (Math.Abs(ExposureSlider.Value - ev) > 1e-6) ExposureSlider.Value = ev;   // an ulp round-trip would re-raise ValueChanged
        _syncing = false;
        ZebraBtn.IsChecked = Surface.Session.Doc.Zebra;
        Bar.Refresh();
    }

    private void OnTextRequested() => _ = AskText();

    private async Task AskText()
    {
        try { await AskTextCore(); }
        catch (Exception ex)
        {
            // The session refuses every tool while PendingText is set, so a failed text box must cancel it.
            App.Current.Log.Warn("viewer text entry: " + ex.Message);
            Surface.Session.CancelText();
            if (!_closed) Surface.Focus(FocusState.Pointer);
        }
    }

    private async Task AskTextCore()
    {
        if (Surface.Session.PendingText is not (int x, int y)) return;
        // WinUI has no PointToScreen: combine the Win32 client origin with the visual-tree offset in physical pixels.
        global::Windows.Foundation.Point local = Surface.TransformToVisual(RootGrid).TransformPoint(
            new global::Windows.Foundation.Point((x - Surface.View.Left) * Surface.Zoom, (y - Surface.View.Top) * Surface.Zoom));
        double scale = Native.Scale(_hwnd);
        (int ox, int oy) = Native.ClientOrigin(_hwnd);
        // The box previews the text at its drawn size: the picture is shown at Zoom × scale screen pixels per pixel.
        TextEntryWindow w = TextEntryWindow.For(Surface.Session.Style, _accent, Surface.Zoom * scale);
        string? text = await w.ShowAt(ox + (int)Math.Round(local.X * scale), oy + (int)Math.Round(local.Y * scale));
        if (_closed) return;
        if (text == null) Surface.Session.CancelText(); else Surface.Session.CommitText(text);
        Surface.Focus(FocusState.Pointer);
    }

    // ----- zoom -----

    private void OnSurfaceRendered()
    {
        if (_viewSize == (Surface.View.Width, Surface.View.Height)) return;   // a crop or its undo resized the view
        _viewSize = (Surface.View.Width, Surface.View.Height);
        ApplyZoom();
    }

    private double FitScale()
    {
        double w = Scroll.ViewportWidth > 0 ? Scroll.ViewportWidth : Scroll.ActualWidth, h = Scroll.ViewportHeight > 0 ? Scroll.ViewportHeight : Scroll.ActualHeight;
        if (w <= 0 || h <= 0) return 1;
        double scale = DisplayScale();
        return Math.Min(1, Math.Min((w - 24) * scale / Surface.View.Width, (h - 24) * scale / Surface.View.Height));
    }

    /// <summary>Physical pixels per effective pixel on the monitor the window is on.</summary>
    private double DisplayScale() => RootGrid.XamlRoot?.RasterizationScale is double s and > 0 ? s : 1;

    /// <summary>
    /// The zoom level is in physical pixels: 100 % is one screen pixel per picture pixel, so it stays sharp on a scaled
    /// display. <see cref="EditorSurface.Zoom"/> is in effective pixels, which layout and the pointer use.
    /// </summary>
    private void ApplyZoom()
    {
        double z = _zoom ?? FitScale();
        double dip = z / DisplayScale();
        Surface.Zoom = dip;
        Surface.Width = Math.Round(Surface.View.Width * dip);
        Surface.Height = Math.Round(Surface.View.Height * dip);
        ZoomText.Text = $"{z * 100:F0}\u2009%";   // thin space before the sign, as the design writes it
        // The button's Name replaces its text for a screen reader, so the zoom level goes in ItemStatus.
        AutomationProperties.SetItemStatus(FitButton, ZoomText.Text);
        // A held space may start or stop meaning "pan" as the zoom crosses fit.
        if (_spaceDown && _panFrom == null) Surface.Panning = CanPan();
    }

    private void Step(int dir)
    {
        double cur = _zoom ?? FitScale();
        _zoom = dir > 0 ? Stops.FirstOrDefault(s => s > cur + 1e-6, Stops[^1]) : Stops.LastOrDefault(s => s < cur - 1e-6, Stops[0]);
        ApplyZoom();
    }

    private bool _rootHooked;
    private double _rootScale;

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_closed || sender.RasterizationScale == _rootScale) return;
        _rootScale = sender.RasterizationScale;
        ApplyZoom();
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => Step(1);
    private void OnZoomOut(object sender, RoutedEventArgs e) => Step(-1);
    private void OnFit(object sender, RoutedEventArgs e) { _zoom = null; ApplyZoom(); }
    private void OnViewportSize(object sender, SizeChangedEventArgs e) { if (_zoom == null) ApplyZoom(); UpdateSurfaceViewport(); }

    /// <summary>The surface's visible part, in its own effective pixels, from where it sits in the scroll viewport;
    /// <paramref name="dx"/>/<paramref name="dy"/> carry a scroll that is about to happen (ViewChanging).</summary>
    private void UpdateSurfaceViewport(double dx = 0, double dy = 0)
    {
        if (_closed || Surface.XamlRoot == null) return;
        try
        {
            global::Windows.Foundation.Point at = Surface.TransformToVisual(Scroll).TransformPoint(new global::Windows.Foundation.Point(0, 0));
            double w = Scroll.ViewportWidth > 0 ? Scroll.ViewportWidth : Scroll.ActualWidth, h = Scroll.ViewportHeight > 0 ? Scroll.ViewportHeight : Scroll.ActualHeight;
            Surface.SetViewport(new global::Windows.Foundation.Rect(-at.X + dx, -at.Y + dy, Math.Max(1, w), Math.Max(1, h)));
        }
        catch (Exception ex) { App.Current.Log.Debug("viewer viewport: " + ex.Message); }
    }

    // ----- output -----

    /// <summary>The pixels the viewer would hand out: the current crop with the shapes drawn in, no chrome.</summary>
    private BgraImage Output() => Surface.RenderForOutput();

    private void OnCopy(object sender, RoutedEventArgs e) => Copy();
    private void OnSave(object sender, RoutedEventArgs e) => _ = SaveWhenIdle(saveAs: false);
    private void OnSaveAs(object sender, RoutedEventArgs e) => _ = SaveWhenIdle(saveAs: true);

    /// <summary>
    /// Defers saving until the exposure loop is idle, since a pass rewrites the image in place on a worker thread. The
    /// await resumes on the dispatcher, so the save still runs on the UI thread.
    /// </summary>
    private Task<bool> SaveWhenIdle(bool saveAs) => OneOutputAtATime(async () =>
    {
        if (!await ExposureIdle()) return false;
        return saveAs ? await SaveAs() : await Save();
    });

    private async Task<T> OneOutputAtATime<T>(Func<Task<T>> work)
    {
        await _outputGate.WaitAsync();
        try { return await work(); }
        finally { _outputGate.Release(); }
    }

    /// <summary>Waits out an exposure pass in flight. False when waiting failed (already logged).</summary>
    private async Task<bool> ExposureIdle()
    {
        if (Surface.ExposureIdle.IsCompleted) return true;
        try { await Surface.ExposureIdle; return true; }
        catch (Exception ex) { App.Current.Log.Warn("viewer exposure: " + ex.Message); return false; }
    }

    private void Copy() => _ = CopyAsync();

    /// <summary>
    /// Copies the current pixels. The render stays on the UI thread (it reads the surface's buffers); the PNG encode and
    /// clipboard write run on the pool over the private copy <see cref="Output"/> produced.
    /// </summary>
    private Task CopyAsync() => OneOutputAtATime(async () => { await CopyCore(); return true; });

    private async Task CopyCore()
    {
        try
        {
            // Wait before rendering: _encode tracks one encode at a time, so starting while a Save is still writing
            // would replace its entry and let others proceed past an unfinished write.
            await WritesPending();
            // An exposure pass rewrites the picture in place on a worker thread.
            if (!await ExposureIdle() || _closed) return;
            BgraImage img = Output();
            // Both on the pool: the clipboard needs no apartment, and the DIB copy costs as much as the PNG.
            await RunEncode(() => ClipboardWriter.Set(img, Bitmaps.EncodePng(img), App.Current.Log));
        }
        catch (Exception ex) { App.Current.Log.Warn("viewer copy: " + ex.Message); }
    }

    // ----- the HDR sidecar, off the UI thread -----

    /// <summary>The tonemapper setting as the status strip names it.</summary>
    private static string TonemapName(string name) => name switch { "hable" => "Hable", "aces" => "ACES", _ => "Desktop" };

    /// <summary>Completes when neither the HDR sidecar write nor the SDR encode is running off the UI thread. The
    /// exposure loop, a second save and the close all wait on it.</summary>
    private Task WritesPending()
    {
        Task? hdr = _hdrWrite, encode = _encode;
        if (hdr == null) return encode ?? Task.CompletedTask;
        return encode == null ? hdr : Task.WhenAll(hdr, encode);
    }

    /// <summary>
    /// Runs one encode and its file write on the thread pool, tracked by <see cref="WritesPending"/>.
    /// <paramref name="work"/> should use only the private image <see cref="Output"/> rendered, not the surface's
    /// buffers.
    /// </summary>
    private async Task<T> RunEncode<T>(Func<T> work)
    {
        Task<T> task = Task.Run(work);
        // WritesPending holds a continuation rather than the task, so waiters only learn that writing stopped and a
        // faulted encode does not re-throw into them. The caller below still sees the real exception.
        Task gate = task.ContinueWith(static _ => { }, TaskScheduler.Default);
        _encode = gate;
        try { return await task; }
        finally { if (ReferenceEquals(_encode, gate)) _encode = null; }
    }

    /// <summary>The same, for a write with no result to hand back (Save as's SDR branch).</summary>
    private Task RunEncode(Action work) => RunEncode<object?>(() => { work(); return null; });

    /// <summary>
    /// Starts the sidecar write on the thread pool from the rendered SDR pixels and the result snapshot
    /// <see cref="SyncResultFromSession"/> took. Everything else that reads the result waits on
    /// <see cref="WritesPending"/> first.
    /// </summary>
    /// <param name="after">Run on the UI thread once the write has finished, with its outcome.</param>
    private void StartHdrWrite(BgraImage img, string path, string format, Action<bool>? after = null)
        => _hdrWrite = RunHdrWrite(img, path, format, after);

    private async Task RunHdrWrite(BgraImage img, string path, string format, Action<bool>? after)
    {
        int generation = ++_hdrGeneration;
        if (!_closed) { ShowNote(HdrError, false); HideHdrDone(); ShowNote(HdrBusy, true); }
        var sw = Stopwatch.StartNew();
        CaptureResult result = _result;
        SnipSettings settings = App.Current.Settings;   // snapshot: a settings change mid-write must not split the file
        uint accent = _accent;
        ILog log = App.Current.Log;
        bool ok = false;
        try { ok = await Task.Run(() => HdrOutput.WriteTo(result, img, path, format, settings, accent, log)); }
        catch (Exception ex) { log.Warn("viewer hdr: " + ex.Message); }
        if (ok) App.Current.Timing("hdr saved", sw);
        else log.Info($"hdr copy failed after {sw.ElapsedMilliseconds} ms ({path})");
        try { after?.Invoke(ok); } catch (Exception ex) { log.Warn("viewer hdr follow-up: " + ex.Message); }
        if (generation != _hdrGeneration) return;   // a newer write already owns the field and the busy note
        _hdrWrite = null;
        _hdrWriteOk = ok;
        if (_closed) return;
        ShowNote(HdrBusy, false);
        ShowNote(HdrError, !ok);
        if (ok) ShowHdrDone();
    }

    /// <summary>"HDR copy written": 2 s in Success, then a 300 ms fade out (skipped when animations are off).</summary>
    private void ShowHdrDone()
    {
        HideHdrDone();
        HdrDone.Opacity = 1;
        ShowNote(HdrDone, true);
        Microsoft.UI.Dispatching.DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = HdrDoneFor;
        timer.IsRepeating = false;
        // Kept so it can be detached when it fires or when HideHdrDone runs early: a stopped timer still holds its
        // Tick, and the Tick closes over this window.
        global::Windows.Foundation.TypedEventHandler<Microsoft.UI.Dispatching.DispatcherQueueTimer, object>? tick = null;
        tick = (t, _) =>
        {
            t.Stop();
            t.Tick -= tick;
            if (!ReferenceEquals(_hdrDoneTimer, t) || _closed) return;
            _hdrDoneTimer = null;
            _hdrDoneTick = null;
            FadeHdrDoneOut();
        };
        timer.Tick += tick;
        _hdrDoneTick = tick;
        _hdrDoneTimer = timer;
        timer.Start();
    }

    private void FadeHdrDoneOut()
    {
        if (!ThemeManager.AnimationsEnabled) { ShowNote(HdrDone, false); return; }
        var anim = new DoubleAnimation { From = 1, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(HdrDoneFadeMs)) };
        Storyboard.SetTarget(anim, HdrDone);
        Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_hdrDoneFade, sb)) return;
            _hdrDoneFade = null;
            ShowNote(HdrDone, false);
            HdrDone.Opacity = 1;
        };
        _hdrDoneFade = sb;
        sb.Begin();
    }

    /// <summary>Takes the note down at once, whatever stage it is at; the next write starts the two-second dwell over.</summary>
    private void HideHdrDone()
    {
        if (_hdrDoneTimer is { } timer)
        {
            timer.Stop();
            if (_hdrDoneTick != null) timer.Tick -= _hdrDoneTick;
        }
        _hdrDoneTimer = null;
        _hdrDoneTick = null;
        _hdrDoneFade?.Stop();
        _hdrDoneFade = null;
        HdrDone.Opacity = 1;
        ShowNote(HdrDone, false);
    }

    /// <summary>Overwrites the file the snip was saved to, in its own format. False when it failed or was cancelled.</summary>
    private async Task<bool> Save()
    {
        if (_result.SavedPath == null) return await SaveAs();
        await WritesPending();   // a second Save while a write is still going out waits for it
        try
        {
            BgraImage img = Output();
            Rendered rendered = Snapshot();
            string path = _result.SavedPath;
            bool jpeg = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
            bool clip = App.Current.Settings.CopyToClipboard;
            int quality = App.Current.Settings.JpegQuality;
            // The encode runs off the UI thread; everything after this await is the UI-thread tail.
            byte[]? png = null;
            await RunEncode(() =>
            {
                png = !jpeg || clip ? Bitmaps.EncodePng(img) : null;   // a JPEG save with no clipboard needs no PNG at all
                File.WriteAllBytes(path, jpeg ? Bitmaps.EncodeJpeg(img, quality) : png!);
                if (clip) ClipboardWriter.Set(img, png!, App.Current.Log);
            });
            // The file is written, so the Save has succeeded even if the window has since closed; only the lines that
            // touch elements are guarded.
            _result.Output = img;   // "Open last snip" and Compact both read this
            // Closing the editor compacts the result to this PNG; kept, so the close does not encode the image again.
            if (png != null) _result.CachePng(img, png);
            bool hdrData = _result.Crops.Count > 0;
            // A sidecar belonging to another file (the original name, after a Save as) is forgotten, not overwritten.
            if (_result.HdrPath != null && !HdrOutput.OwnsSidecar(path, _result.HdrPath)) _result.HdrPath = null;
            string? hdrFormat = _result.HdrPath != null ? HdrOutput.FormatOf(_result.HdrPath) : App.Current.Settings.Hdr.File == "none" ? null : App.Current.Settings.Hdr.File;
            // An exposure pass may have started during the encode, and the sidecar reads what it rewrites; if the wait
            // fails, the SDR file stands without its sidecar.
            if (hdrData && hdrFormat != null && await ExposureIdle())
            {
                SyncResultFromSession(rendered);   // the document the SDR pixels were rendered from
                string hdrPath = _result.HdrPath ?? HdrOutput.PathFor(_result.SavedPath, hdrFormat);
                // The history row is rewritten after the sidecar write, which sets _result.HdrPath; otherwise a sidecar
                // written for the first time here would never reach the row, and a delete would orphan it.
                BgraImage written = img;
                StartHdrWrite(img, hdrPath, hdrFormat, _ => App.Current.History.Replace(_result, written));
            }
            else App.Current.History.Replace(_result, img);   // rewrite thumbnail + size for this entry
            SetHdrNote(!hdrData && _result.HdrPath != null);
            _savedExposure = rendered.Exposure;
            Surface.Session.Doc.MarkSaved(rendered.Revision);
            if (!_closed) UpdateState();
            App.Current.Log.Info("viewer saved " + _result.SavedPath);
            return true;
        }
        catch (Exception ex) { App.Current.Log.Warn("viewer save: " + ex.Message); return false; }
    }

    /// <summary>The "HDR copy not updated" note next to Modified in the status bar.</summary>
    private void SetHdrNote(bool skipped) => ShowNote(HdrNote, skipped);

    /// <summary>
    /// Shows or hides one of the status strip's notes, announcing it when it appears.
    /// <c>AutomationProperties.LiveSetting</c> alone announces nothing: the framework raises no event when Visibility
    /// changes, so the note is announced through <see cref="Controls.LiveRegion"/>. Hiding is not announced.
    /// </summary>
    private void ShowNote(TextBlock note, bool on)
    {
        if (_closed) return;
        Visibility want = on ? Visibility.Visible : Visibility.Collapsed;
        if (note.Visibility == want) return;
        note.Visibility = want;
        if (on) Controls.LiveRegion.Announce(note);
    }

#if TONESNIP_HARNESS
    /// <summary>Shows the real "HDR copy written" note and its dwell timer, so the leak test can exercise the timer
    /// without an HDR sidecar write.</summary>
    internal void ShowHdrDoneForHarness() => ShowHdrDone();

    /// <summary>
    /// Shows or hides one of the status notes through the same call the sidecar write uses, so UIA tests and
    /// screenshots can see it without writing a file.
    /// </summary>
    internal void ShowHarnessNote(string which, bool on) => ShowNote(which switch
    {
        "busy" => HdrBusy,
        "done" => HdrDone,
        "error" => HdrError,
        _ => HdrNote,
    }, on);

    /// <summary>
    /// Re-runs the title update over a stand-in path, as <see cref="SaveAs"/> does after a save, so the window title can
    /// be checked without writing a file.
    /// </summary>
    internal void RetitleForHarness(string? savedPath)
    {
        _result.SavedPath = savedPath;
        UpdateTitle();
    }
#endif

    /// <summary>
    /// The folder and name the dialog opens on. The picker has no arbitrary start folder, only
    /// <see cref="PickerLocationId"/> and <c>SuggestedSaveFile</c>, so the settings' save folder is reached through a
    /// placeholder file, removed by <see cref="DropPlaceholder"/> if still empty. Null falls back to Pictures.
    /// </summary>
    private static async Task<StorageFile?> SuggestedFile(string name)
    {
        try
        {
            string folder = App.Current.Settings.ResolvedSaveFolder(AppPaths.Pictures);
            Directory.CreateDirectory(folder);
            StorageFolder dir = await StorageFolder.GetFolderFromPathAsync(folder);
            return await dir.CreateFileAsync(name, CreationCollisionOption.OpenIfExists);   // never truncates an existing file
        }
        catch (Exception ex) { App.Current.Log.Warn("viewer save as folder: " + ex.Message); return null; }
    }

    /// <summary>Deletes the placeholder when nothing was written to it — the user cancelled, or saved under another
    /// name. A placeholder that opened an existing file is not empty and is left alone.</summary>
    private static async Task DropPlaceholder(StorageFile? placeholder, string? chosenPath)
    {
        if (placeholder == null) return;
        if (chosenPath != null && string.Equals(chosenPath, placeholder.Path, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            if ((await placeholder.GetBasicPropertiesAsync()).Size == 0) await placeholder.DeleteAsync(StorageDeleteOption.PermanentDelete);
        }
        catch (Exception ex) { App.Current.Log.Warn("viewer save as placeholder: " + ex.Message); }
    }

    private FileSavePicker BuildPicker(bool hdr, string name, StorageFile? suggested)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, SuggestedFileName = name };
        picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
        picker.FileTypeChoices.Add("JPEG image", new List<string> { ".jpg" });
        if (hdr)
        {
            picker.FileTypeChoices.Add("JPEG XR (HDR)", new List<string> { ".jxr" });
            picker.FileTypeChoices.Add("PNG (HDR, 16-bit)", new List<string> { ".hdr.png" });
            picker.FileTypeChoices.Add("JPEG with gain map (HDR)", new List<string> { ".hdr.jpg" });
        }
        if (suggested != null) picker.SuggestedSaveFile = suggested;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        return picker;
    }

    /// <summary>Shows the picker. <c>FileTypeChoices.Add</c> validates nothing, so if <c>PickSaveFileAsync</c> rejects
    /// the two-part HDR extensions, retry once with the SDR types only.</summary>
    private async Task<StorageFile?> Pick(bool hdr, string name, StorageFile? suggested)
    {
        try { return await BuildPicker(hdr, name, suggested).PickSaveFileAsync(); }
        catch (Exception ex)
        {
            if (!hdr) { App.Current.Log.Warn("viewer save as: " + ex.Message); return null; }
            App.Current.Log.Warn($"viewer save as: the HDR file types were rejected ({ex.Message}); retrying with PNG and JPEG only");
            try { return await BuildPicker(hdr: false, name, suggested).PickSaveFileAsync(); }
            catch (Exception retry) { App.Current.Log.Warn("viewer save as: " + retry.Message); return null; }
        }
    }

    private async Task<bool> SaveAs()
    {
        await WritesPending();
        bool hdrData = _result.Crops.Count > 0;
        string name = Path.GetFileName(_result.SavedPath ?? Core.Output.FileNaming.Build(_result.TakenLocal, "png"));
        StorageFile? placeholder = await SuggestedFile(name);
        SetHdrNote(false);   // clear any stale note from before this call; the SDR branch below restores it if it still applies
        StorageFile? file = await Pick(hdrData, name, placeholder);
        await DropPlaceholder(placeholder, file?.Path);
        if (file == null || _closed) return false;
        if (!await ExposureIdle()) return false;
        string path = file.Path;
        try
        {
            BgraImage img = Output();
            Rendered rendered = Snapshot();
            // An HDR-format target (.jxr / .hdr.png / .hdr.jpg) is a one-off export: no SavedPath change, no history
            // entry, and _result.HdrPath is restored afterwards. Checked before the extension test below, because
            // Path.GetExtension("x.hdr.png") is ".png".
            string? hdrFmt = HdrOutput.FormatOf(path);
            if (hdrFmt != null)
            {
                string? prevHdrPath = _result.HdrPath;
                SyncResultFromSession(rendered);   // the document the pixels were rendered from
                StartHdrWrite(img, path, hdrFmt, ok => { _result.HdrPath = prevHdrPath; if (ok) App.Current.Log.Info("viewer saved " + path); });
                // True means queued, not written; the close path reads _hdrWriteOk after awaiting WritesPending.
                return true;
            }
            // The picker only returns one of the offered extensions, so the name decides the format.
            string ext = Path.GetExtension(path);
            bool jpeg = ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
            int quality = App.Current.Settings.JpegQuality;
            // Off the UI thread, as in Save; the tail below runs on the UI thread.
            byte[]? png = null;
            await RunEncode(() =>
            {
                png = jpeg ? null : Bitmaps.EncodePng(img);
                File.WriteAllBytes(path, jpeg ? Bitmaps.EncodeJpeg(img, quality) : png!);
            });
            // The file is written, so the Save as has succeeded and its history row must be recorded even if the window
            // has closed; only the lines that touch elements are guarded.
            _result.SavedPath = path;
            if (!HdrOutput.OwnsSidecar(path, _result.HdrPath)) _result.HdrPath = null;   // the old name's sidecar is not this file's
            _result.Output = img;   // as in Save: the retained result must carry the pixels that were written
            if (png != null) _result.CachePng(img, png);   // as in Save: the close keeps this PNG rather than encoding again
            App.Current.History.AddSavedCopy(_result, path, img);
            SetHdrNote(!hdrData && _result.HdrPath != null);
            _savedExposure = rendered.Exposure;
            Surface.Session.Doc.MarkSaved(rendered.Revision);
            if (!_closed)
            {
                OpenFolder.IsEnabled = true;
                UpdateTitle();   // the window is this file's from now on
                UpdateState();
            }
            App.Current.Log.Info("viewer saved " + path);
            return true;
        }
        catch (Exception ex) { App.Current.Log.Warn("viewer save as: " + ex.Message); return false; }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Shell.Reveal(_result.SavedPath, App.Current.Log);
    }

    // ----- closing -----

    /// <summary>
    /// <see cref="Window.Closed"/> cannot be cancelled, so the "save changes?" prompt hangs off
    /// <see cref="AppWindow"/>.Closing: the first close is cancelled, the prompt and any pending writes are awaited, and
    /// the window is then closed for real.
    /// </summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeApproved) return;
        if (_closePending) { args.Cancel = true; return; }   // the prompt is already up
        if (!Edited() && _hdrWrite == null && _encode == null) return;
        args.Cancel = true;
        _closePending = true;
        _ = AskThenClose();
    }

#if TONESNIP_HARNESS
    /// <summary>
    /// Closes without the "save changes?" prompt, so a test that drew on the canvas does not leave a modal dialog
    /// behind. <see cref="OnClosed"/> still runs and still awaits <see cref="WritesPending"/>.
    /// </summary>
    internal void CloseForHarness()
    {
        _closeApproved = true;
        Close();
    }
#endif

    private async Task AskThenClose()
    {
        try
        {
            if (Edited())
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    RequestedTheme = RootGrid.RequestedTheme,
                    Title = "Save changes?",
                    Content = _result.SavedPath is { } saved
                        ? $"{Path.GetFileName(saved)} has annotations that haven't been saved."
                        : "This snip has annotations that haven't been saved.",
                    PrimaryButtonText = "Save",
                    SecondaryButtonText = "Discard",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    // No explicit button styles: the dialog uses its own, and Save picks up the app accent through
                    // DefaultButton = Primary.
                };
                ContentDialogResult answer;
                // Its own catch: ShowAsync throws when another ContentDialog already owns this XamlRoot, and falling
                // through to the close would silently discard the edits. The window stays open instead.
                try { answer = await dialog.ShowAsync(); }
                catch (Exception ex) { App.Current.Log.Warn("viewer close prompt: " + ex.Message); _closePending = false; return; }
                if (answer == ContentDialogResult.None) { _closePending = false; return; }   // Cancel: the window stays
                // Let any exposure pass land before the save reads the pixels. A saved document is no longer "edited",
                // so the prompt does not repeat on the close below.
                if (answer == ContentDialogResult.Primary && !await OneOutputAtATime(async () => await ExposureIdle() && await Save())) { _closePending = false; return; }
            }
            await WritesPending();   // the sidecar write reads the result Compact is about to empty
            if (!_hdrWriteOk)
            {
                // A Save onto an HDR-only target wrote nothing else, so refuse this close once with the failure showing
                // in the status strip; a second close goes through.
                _hdrWriteOk = true;
                _closePending = false;
                return;
            }
        }
        catch (Exception ex) { App.Current.Log.Warn("viewer close: " + ex.Message); }
        _closeApproved = true;
        Close();
    }

    /// <summary>
    /// <c>WhenClosed</c> takes an <see cref="Action"/>, so the async tail is fired and forgotten explicitly under an
    /// outer try/catch rather than written <c>async void</c>, where an unexpected exception would crash the app. The
    /// synchronous head (unsubscribes and <c>Surface.Shutdown</c>) still runs before this returns.
    /// </summary>
    private void OnClosed() => _ = ClosedAsync();

    private async Task ClosedAsync()
    {
        try
        {
            _closed = true;
            ThemeManager.Changed -= OnThemeChanged;
            AppWindow.Closing -= OnAppWindowClosing;
            if (_rootHooked && RootGrid.XamlRoot is { } root) { root.Changed -= OnXamlRootChanged; _rootHooked = false; }
            _toolRowAnim?.Stop(); _toolRowAnim = null;
            HideHdrDone();   // the dwell timer would otherwise tick into a closed window
            if (_onAppWindowChanged != null) { AppWindow.Changed -= _onAppWindowChanged; _onAppWindowChanged = null; }
            // Every step is fenced: if one throws, the result must still be compacted or the whole snip stays in memory.
            // Compact drops the float crops and the decoded image the exposure loop tonemaps into; join the loop first.
            Surface.Shutdown();
            try { await Surface.ExposureIdle; }
            catch (Exception ex) { App.Current.Log.Warn("viewer exposure shutdown: " + ex.Message); }
            try { await WritesPending(); } catch (Exception ex) { App.Current.Log.Warn("viewer hdr shutdown: " + ex.Message); }
            try { Bar.Detach(); } catch (Exception ex) { App.Current.Log.Warn("viewer bar detach: " + ex.Message); }
            try { Surface.ReleaseBuffers(); } catch (Exception ex) { App.Current.Log.Warn("viewer release: " + ex.Message); }
            // Off the UI thread when it has to encode; not at all when nothing was edited or a save made the PNG.
            try { await _result.CompactAsync(); } catch (Exception ex) { App.Current.Log.Warn("viewer compact: " + ex.Message); }
            try { SaveStyle(); } catch (Exception ex) { App.Current.Log.Warn("viewer style save: " + ex.Message); }
            App.Current.ReclaimMemory("memory after edit");
        }
        // Anything thrown outside the per-step catches above.
        catch (Exception ex) { App.Current.Log.Warn("viewer closed: " + ex.Message); }
    }
}
