using ToneSnip.App.Annotate;
using ToneSnip.App.Capture;
using ToneSnip.App.Interop;
using ToneSnip.App.Output;
using ToneSnip.App.Theme;
using ToneSnip.Core.Annotate;
using ToneSnip.Core.Config;
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
using Windows.UI;

namespace ToneSnip.App.Viewer;

/// <summary>Viewer and editor for one snip: the picture, the shared tool row, crop and save in place.</summary>
public sealed partial class ViewerWindow : Window
{
    /// <summary>Escape is decoded by hand rather than as a <see cref="KeyboardAccelerator"/>: it drops one thing at a
    /// time (the overflow menu, an open picker, a marquee, a selection) and only then closes the window.</summary>
    private const int VkEscape = 0x1B;
    /// <summary>The tool row's own height: the shared 40 px row under a 1 px hairline.</summary>
    private const double ToolRowHeight = 41;

    private readonly CaptureResult _result;
    private readonly IntPtr _hwnd;
    private readonly uint _accent;
    private bool _annotating;
    private bool _statePending;
    private bool _closed;
    private (int W, int H) _viewSize;
    /// <summary>Exposure the file on disk was written with; the "edited" dot compares against it.</summary>
    private float _savedExposure;
    /// <summary>The window's dispatcher, read once on the UI thread for handlers raised elsewhere.</summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _ui;
    /// <summary>Last style picked in the tool row, written once on close: every palette click would otherwise rewrite
    /// settings.json, re-apply the theme and re-register the hotkeys.</summary>
    private Core.Annotate.Style? _styleToSave;
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
    /// <summary>The minimum-size clamp, kept so it can be detached when the window goes.</summary>
    private global::Windows.Foundation.TypedEventHandler<AppWindow, AppWindowChangedEventArgs>? _onAppWindowChanged;
    private bool _clamping;

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
        Surface.PointerNits += ShowNits;
        Surface.ColourPicked += OnColourPicked;
        Surface.TextAreaSelected += OnTextAreaSelected;
        Surface.PickerPointer += ShowLoupe;
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
        DebugHooks.EditorAnnotateChanged(on, Surface);
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
            if (Surface.Picking) { SetPicking(false); e.Handled = true; return; }
            if (Surface.SelectingText) { SetSelectingText(false); e.Handled = true; return; }
            if (_annotating && Surface.Session.Escape()) { e.Handled = true; return; }
            Close(); return;
        }
        if (ZoomKey(e.Key)) { e.Handled = true; return; }
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
    /// The status bar's readout of the luminance under the pointer, as the overlay's pill shows it and behind the same
    /// setting. Hidden rather than blank off the HDR part of the picture, so the strip does not keep an empty gap.
    /// </summary>
    private void ShowNits(float? nits)
    {
        if (nits is float n && App.Current.Settings.ShowNitsReadout)
        {
            NitsReadout.Text = $"{n:F0} nits";
            NitsReadout.Visibility = Visibility.Visible;
        }
        else NitsReadout.Visibility = Visibility.Collapsed;
    }

    // ----- the colour picker -----

    /// <summary>How long a passing note (the picker's "Copied …") stays in the status strip.</summary>
    private static readonly TimeSpan NoteFor = TimeSpan.FromSeconds(2);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _noteTimer;

    private void OnPick(object sender, RoutedEventArgs e) => SetPicking(PickBtn.IsChecked == true);

    /// <summary>Explicit, as for Annotate: a ToggleButton has no Invoke pattern for the default action to run.</summary>
    private void OnPickAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SetPicking(!Surface.Picking);
        args.Handled = true;
    }

    private void SetPicking(bool on)
    {
        if (on && Surface.SelectingText) SetSelectingText(false, quiet: true);   // one armed pointer mode at a time
        Surface.Picking = on;
        PickBtn.IsChecked = on;
        if (on) Surface.Focus(FocusState.Pointer);
        else ReleaseLoupe();
    }

    // ----- the picker's loupe -----

    /// <summary>The loupe, built when the picker first shows it and dropped when the picker is put away: its bitmap,
    /// the BGRA the surface renders into it, the metrics it was built for, and its elements.</summary>
    private Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap? _loupeBitmap;
    private byte[]? _loupePixels;
    private Core.Geometry.GuidesLayout _loupeLayout;
    private Border? _loupeRing, _loupeLabel;
    private TextBlock? _loupeText;

    /// <summary>
    /// The Guides frame's pixel loupe by the pointer while the picker is armed: the pixels around the one under the
    /// pointer, magnified on a grid with that one outlined, and under it the colour a click would copy (and its nits on
    /// an HDR snip). Below right of the pointer, flipped where that would leave the viewport, as the overlay's does.
    /// It is laid out in physical pixels at the monitor's scale, so each magnified pixel is a crisp square.
    /// </summary>
    private void ShowLoupe((int X, int Y)? at)
    {
        if (_closed || at is not (int x, int y) || !Surface.Picking) { if (_loupeRing != null) LoupeLayer.Visibility = Visibility.Collapsed; return; }
        double s = DisplayScale();
        if (_loupeRing == null || _loupeLayout.Scale != s) BuildLoupe(s);
        Core.Geometry.GuidesLayout g = _loupeLayout;
        if (!Surface.RenderLoupe(x, y, g, _loupePixels!)) return;
        System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.CopyTo(_loupePixels!, _loupeBitmap!.PixelBuffer);
        _loupeBitmap.Invalidate();
        uint argb = Surface.ColourAt(x, y) ?? 0xFF000000;
        _loupeText!.Text = Core.Extract.ColorText.Loupe(argb, App.Current.Settings.ColorFormat, Surface.NitsAt(x, y));
        _loupeLabel!.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        int labelW = (int)Math.Ceiling(_loupeLabel.DesiredSize.Width * s), labelH = (int)Math.Ceiling(_loupeLabel.DesiredSize.Height * s);
        global::Windows.Foundation.Point p = Surface.TransformToVisual(LoupeLayer).TransformPoint(Surface.PixelCentre(x, y));
        var area = new Core.Geometry.IntRect(0, 0, (int)(LoupeLayer.ActualWidth * s), (int)(LoupeLayer.ActualHeight * s));
        (Core.Geometry.IntRect loupe, Core.Geometry.IntRect label) = g.Loupe((int)Math.Round(p.X * s), (int)Math.Round(p.Y * s), labelW, labelH, area);
        Canvas.SetLeft(_loupeRing, (loupe.Left - g.Ring) / s);
        Canvas.SetTop(_loupeRing, (loupe.Top - g.Ring) / s);
        Canvas.SetLeft(_loupeLabel, label.Left / s);
        Canvas.SetTop(_loupeLabel, label.Top / s);
        LoupeLayer.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// The loupe's elements for scale <paramref name="s"/>: a white border inside a dark keyline around the magnified
    /// pixels, and the readout in a dark pill. Fixed colours, not theme brushes, as in the overlay's loupe: it sits on
    /// arbitrary picture content, where only white on black reads everywhere.
    /// </summary>
    private void BuildLoupe(double s)
    {
        ReleaseLoupe();
        Core.Geometry.GuidesLayout g = Core.Geometry.GuidesLayout.For(s);
        _loupeLayout = g;
        int size = g.LoupeSize;
        _loupePixels = new byte[size * size * 4];
        _loupeBitmap = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(size, size);
        double ring = g.Ring / s, half = ring / 2;
        var image = new Image { Source = _loupeBitmap, Width = size / s, Height = size / s, Stretch = Stretch.Fill };
        var white = new Border { BorderBrush = new SolidColorBrush(Colors.White), BorderThickness = new Thickness(half), Child = image };
        _loupeRing = new Border { BorderBrush = new SolidColorBrush(Colors.Black), BorderThickness = new Thickness(ring - half), Child = white };
        _loupeText = new TextBlock { Style = (Microsoft.UI.Xaml.Style)Application.Current.Resources["Caption"], Foreground = new SolidColorBrush(Colors.White), TextWrapping = TextWrapping.NoWrap };
        _loupeLabel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(g.LabelRadius / s),
            Padding = new Thickness(g.LabelPadX / s, g.LabelPadY / s, g.LabelPadX / s, g.LabelPadY / s),
            Child = _loupeText,
        };
        LoupeLayer.Children.Add(_loupeRing);
        LoupeLayer.Children.Add(_loupeLabel);
    }

    /// <summary>Takes the loupe down and lets its bitmap go.</summary>
    private void ReleaseLoupe()
    {
        if (_loupeRing == null) return;
        LoupeLayer.Children.Clear();
        LoupeLayer.Visibility = Visibility.Collapsed;
        _loupeRing = _loupeLabel = null;
        _loupeText = null;
        _loupeBitmap = null;
        _loupePixels = null;
    }

    /// <summary>
    /// One click of the picker: copies the colour as the Colour format setting writes it (with the nits too on
    /// Shift+click, where the snip has HDR data), shows what was copied in the status strip, and puts the picker away.
    /// </summary>
    private void OnColourPicked(uint argb, float? nits, bool withNits)
    {
        SetPicking(false);
        string colour = Core.Extract.ColorText.Format(argb, App.Current.Settings.ColorFormat);
        string copied = withNits ? Core.Extract.ColorText.WithNits(colour, nits) : colour;
        // Queued, so two quick picks land in the order they were made.
        _ = ClipboardWriter.SetTextQueued(copied, App.Current.Log).ContinueWith(
            t => App.Current.Log.Warn("viewer pick colour: " + t.Exception!.GetBaseException().Message),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        ShowNote(Core.Extract.ColorText.Confirmation(colour, nits, withNits), dwell: true);
    }

    /// <summary>
    /// Shows <paramref name="text"/> in the status strip and announces it: for <see cref="NoteFor"/> when
    /// <paramref name="dwell"/>, or until the next note or <see cref="HideNote"/> otherwise.
    /// </summary>
    private void ShowNote(string text, bool dwell)
    {
        StatusNote.Text = text;
        AutomationProperties.SetName(StatusNote, text);
        StatusNote.Visibility = Visibility.Visible;
        Controls.LiveRegion.Announce(StatusNote);
        _noteTimer?.Stop();
        if (!dwell) return;
        if (_noteTimer == null)
        {
            _noteTimer = DispatcherQueue.CreateTimer();
            _noteTimer.IsRepeating = false;
            _noteTimer.Interval = NoteFor;
            _noteTimer.Tick += OnNoteElapsed;
        }
        _noteTimer.Start();
    }

    private void HideNote()
    {
        _noteTimer?.Stop();
        StatusNote.Visibility = Visibility.Collapsed;
    }

    private void OnNoteElapsed(Microsoft.UI.Dispatching.DispatcherQueueTimer timer, object args)
    {
        if (!_closed) StatusNote.Visibility = Visibility.Collapsed;
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
            StopZoomAnimation();
            HideHdrDone();   // the dwell timer would otherwise tick into a closed window
            // Stopped and detached: a stopped timer still holds its Tick, and the Tick holds this window.
            if (_noteTimer is { } noteTimer) { noteTimer.Stop(); noteTimer.Tick -= OnNoteElapsed; _noteTimer = null; }
            Surface.ColourPicked -= OnColourPicked;
            Surface.TextAreaSelected -= OnTextAreaSelected;
            Surface.PickerPointer -= ShowLoupe;
            ReleaseLoupe();
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
