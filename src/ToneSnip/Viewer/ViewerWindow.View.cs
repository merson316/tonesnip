using ToneSnip.App.Theme;
using ToneSnip.Core.Geometry;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace ToneSnip.App.Viewer;

/// <summary>The editor's view of the picture: continuous animated zoom, its stops and fit, the wheel, the pan and the
/// Win2D viewport that follows them.</summary>
public sealed partial class ViewerWindow
{
    private double? _zoom;   // null = fit
    /// <summary>How long an animated zoom (a wheel notch, a button, a key) takes to land.</summary>
    private const double ZoomAnimMs = 160;
    /// <summary>The zoom animation in flight: where it started and is going (null = the fit), when it started, the
    /// point in the ScrollViewer's frame it zooms about and the picture point held under it. Stepped from
    /// <see cref="CompositionTarget.Rendering"/>, which is subscribed only while an animation runs.</summary>
    private double _animFrom, _animTo;
    private bool _animToFit, _animating;
    private long _animStart;
    private global::Windows.Foundation.Point _animAt;
    private (double X, double Y) _animImage;
    /// <summary>The pan in flight (a middle-button drag, or the left button with space held): where the pointer went
    /// down, in the ScrollViewer's own frame, and the offsets it started from. Null when nothing is panning.</summary>
    private global::Windows.Foundation.Point? _panFrom;
    private (double H, double V) _panOffsets;
    /// <summary>Space is held: the pointer belongs to the view rather than to the document, whichever tool is up.</summary>
    private bool _spaceDown;
    private bool _rootHooked;
    private double _rootScale;

    // ----- the wheel and the pan -----

    /// <summary>
    /// The mouse wheel zooms about the pointer, as in Windows Photos: each notch by <see cref="ZoomSteps.NotchFactor"/>,
    /// animated, and a high-resolution wheel by its fraction of a notch. A precision touchpad's two-finger scroll pans
    /// instead, and its pinch (which arrives as Ctrl+wheel) zooms with no animation, since it already moves in small
    /// steps. Shift+wheel and a tilted wheel pan sideways. Wired on both the surface and the ScrollViewer (see the
    /// constructor).
    /// </summary>
    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled) return;   // the surface's copy already answered this one
        Microsoft.UI.Input.PointerPoint p = e.GetCurrentPoint(Scroll);
        int delta = p.Properties.MouseWheelDelta;
        if (delta == 0) return;
        bool ctrl = (e.KeyModifiers & global::Windows.System.VirtualKeyModifiers.Control) != 0;
        bool touchpad = p.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touchpad;
        e.Handled = true;
        // Done by hand: this handler consumes the event, so the ScrollViewer's own scrolling never runs.
        if (p.Properties.IsHorizontalMouseWheel) { StopZoomAnimation(); Scroll.ChangeView(Scroll.HorizontalOffset + delta, null, null, disableAnimation: true); return; }
        if ((e.KeyModifiers & global::Windows.System.VirtualKeyModifiers.Shift) != 0) { StopZoomAnimation(); Scroll.ChangeView(Scroll.HorizontalOffset - delta, null, null, disableAnimation: true); return; }
        if (touchpad && !ctrl) { StopZoomAnimation(); Scroll.ChangeView(null, Scroll.VerticalOffset - delta, null, disableAnimation: true); return; }
        // Notches in quick succession add up: each one goes on from where the animation is heading.
        double from = _animating && !_animToFit ? _animTo : CurrentZoom;
        ZoomTo(ZoomSteps.Wheel(from, delta, FitScale()), p.Position, animate: !touchpad);
    }

    private double CurrentZoom => _zoom ?? FitScale();

    /// <summary>The picture point under <paramref name="at"/> (the ScrollViewer's frame), in effective pixels at zoom 1
    /// on this monitor. The surface is centred while smaller than the viewport, so that slack is taken out.</summary>
    private (double X, double Y) ImagePointAt(global::Windows.Foundation.Point at)
    {
        double z = CurrentZoom;
        double slackX = Math.Max(0, (Scroll.ViewportWidth - Surface.ActualWidth) / 2);
        double slackY = Math.Max(0, (Scroll.ViewportHeight - Surface.ActualHeight) / 2);
        return ((Scroll.HorizontalOffset + at.X - slackX) / z, (Scroll.VerticalOffset + at.Y - slackY) / z);
    }

    /// <summary>Applies a zoom (null = the fit) and scrolls so the picture point <paramref name="image"/> stays under
    /// <paramref name="at"/>.</summary>
    private void SetZoomAnchored(double? zoom, global::Windows.Foundation.Point at, (double X, double Y) image)
    {
        double before = CurrentZoom;
        _zoom = zoom;
        ApplyZoom();
        double after = CurrentZoom;
        if (Math.Abs(after - before) < 1e-9) return;
        // The ScrollViewer's extent only follows the new size on the next layout pass, and ChangeView clamps against
        // the current extent, so the pass is forced.
        Scroll.UpdateLayout();
        double slackX = Math.Max(0, (Scroll.ViewportWidth - Surface.ActualWidth) / 2);
        double slackY = Math.Max(0, (Scroll.ViewportHeight - Surface.ActualHeight) / 2);
        Scroll.ChangeView(slackX + image.X * after - at.X, slackY + image.Y * after - at.Y, null, disableAnimation: true);
    }

    /// <summary>
    /// Zooms to <paramref name="target"/> (or the fit) about <paramref name="at"/>, in the ScrollViewer's frame: at once,
    /// or eased over <see cref="ZoomAnimMs"/> when animations are on. The picture point under <paramref name="at"/> is
    /// read once, at the start, rather than per frame from the scroll offsets, which lag a ChangeView by a frame.
    /// </summary>
    private void ZoomTo(double target, global::Windows.Foundation.Point at, bool animate, bool fit = false)
    {
        if (_closed) return;
        // Retargeting mid-animation about the same point keeps the picture point already held there.
        bool samePoint = _animating && Math.Abs(at.X - _animAt.X) < 2 && Math.Abs(at.Y - _animAt.Y) < 2;
        (double X, double Y) image = samePoint ? _animImage : ImagePointAt(at);
        if (!animate || !ThemeManager.AnimationsEnabled)
        {
            StopZoomAnimation();
            SetZoomAnchored(fit ? null : target, at, image);
            return;
        }
        _animFrom = CurrentZoom;
        _animTo = target;
        _animToFit = fit;
        _animAt = at;
        _animImage = image;
        _animStart = System.Diagnostics.Stopwatch.GetTimestamp();
        // The Win2D control is sized to the part of the picture on screen, and a new size is a new swap chain. Held at
        // the larger of what is on screen now and at the end, it keeps one size for the whole animation instead of
        // resizing every frame, which left several megabytes of native heap behind each editor.
        double dip = target / DisplayScale();
        Surface.HoldCanvasSize(Math.Min(Scroll.ViewportWidth, Surface.View.Width * dip), Math.Min(Scroll.ViewportHeight, Surface.View.Height * dip));
        if (_animating) return;
        _animating = true;
        CompositionTarget.Rendering += OnZoomFrame;
    }

    private void OnZoomFrame(object? sender, object e)
    {
        if (_closed) { StopZoomAnimation(); return; }
        double t = System.Diagnostics.Stopwatch.GetElapsedTime(_animStart).TotalMilliseconds / ZoomAnimMs;
        if (t >= 1)
        {
            StopZoomAnimation();
            SetZoomAnchored(_animToFit ? null : _animTo, _animAt, _animImage);
            return;
        }
        SetZoomAnchored(ZoomSteps.Between(_animFrom, _animTo, t), _animAt, _animImage);
    }

    /// <summary>Ends the animation where it is. CompositionTarget.Rendering is static, so a handler left on it would
    /// keep this window alive and run every frame.</summary>
    private void StopZoomAnimation()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnZoomFrame;
        Surface.ReleaseCanvasSize();
        UpdateSurfaceViewport();
    }

    /// <summary>The middle of the viewport, which the buttons and keys zoom about.</summary>
    private global::Windows.Foundation.Point ViewportCentre()
        => new(Math.Max(1, Scroll.ViewportWidth) / 2, Math.Max(1, Scroll.ViewportHeight) / 2);

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
        StopZoomAnimation();   // the zoom's anchor would fight the drag
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
        double z = CurrentZoom;
        double dip = z / DisplayScale();
        Surface.Zoom = dip;
        Surface.Width = Math.Round(Surface.View.Width * dip);
        Surface.Height = Math.Round(Surface.View.Height * dip);
        // Whole percent from 10 % up; a fit below that shows a decimal, so it never reads "0 %".
        ZoomText.Text = z >= 0.1 ? $"{z * 100:F0}\u2009%" : $"{z * 100:0.#}\u2009%";   // thin space before the sign, as the design writes it
        // The button's Name replaces its text for a screen reader, so the zoom level goes in ItemStatus.
        AutomationProperties.SetItemStatus(FitButton, ZoomText.Text);
        // A held space may start or stop meaning "pan" as the zoom crosses fit.
        if (_spaceDown && _panFrom == null) Surface.Panning = CanPan();
    }

    /// <summary>One stop in or out (<see cref="ZoomSteps.Stops"/>), about the middle of the view.</summary>
    private void Step(int dir)
    {
        double from = _animating && !_animToFit ? _animTo : CurrentZoom;
        ZoomTo(ZoomSteps.Step(from, dir, FitScale()), ViewportCentre(), animate: true);
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_closed || sender.RasterizationScale == _rootScale) return;
        _rootScale = sender.RasterizationScale;
        ApplyZoom();
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => Step(1);
    private void OnZoomOut(object sender, RoutedEventArgs e) => Step(-1);
    private void OnFit(object sender, RoutedEventArgs e) => ZoomTo(FitScale(), ViewportCentre(), animate: true, fit: true);

    /// <summary>Ctrl+1: actual size, one screen pixel per picture pixel.</summary>
    private void OnActualSizeAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ZoomTo(1, ViewportCentre(), animate: true);
        args.Handled = true;
    }

    private void OnViewportSize(object sender, SizeChangedEventArgs e) { if (_zoom == null) ApplyZoom(); UpdateSurfaceViewport(); }

    /// <summary>+ and − (with or without Ctrl, main keyboard or keypad) step the zoom; true when the key was one of
    /// them.</summary>
    private bool ZoomKey(global::Windows.System.VirtualKey key)
    {
        const int oemPlus = 0xBB, oemMinus = 0xBD;
        int vk = (int)key;
        if (vk == oemPlus || key == global::Windows.System.VirtualKey.Add) { Step(1); return true; }
        if (vk == oemMinus || key == global::Windows.System.VirtualKey.Subtract) { Step(-1); return true; }
        return false;
    }

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

#if TONESNIP_HARNESS
    /// <summary>Leak test: arms Copy text, then the picker with its loupe up, and starts a zoom animation, so a close
    /// straight after must let go of the loupe and unhook the animation from CompositionTarget.Rendering.</summary>
    internal void ExerciseViewForHarness()
    {
        SetSelectingText(true);
        SetPicking(true);
        ShowLoupe((Surface.View.Left + 5, Surface.View.Top + 5));
        Step(1);
    }
#endif
}
