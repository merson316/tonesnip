using ToneSnip.App.Interop;
using Style = ToneSnip.Core.Annotate.Style;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Geometry;
using ToneSnip.Windows.Overlay;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace ToneSnip.App.Annotate;

/// <summary>Borderless text box at a screen point. Enter commits (Shift+Enter for a newline), Escape cancels, losing focus commits.</summary>
public sealed partial class TextEntryWindow : PopupWindow
{
    private readonly TaskCompletionSource<string?> _done = new();
    private readonly double _sizePx;
    private int _x, _y;
    private bool _focused;

    /// <summary>
    /// Builds a box for a session's style, with the colour resolved as the renderer resolves it. The overlay, editor
    /// and harness all use this, so the harness sees the same box a real snip does.
    /// </summary>
    /// <param name="zoom">Screen pixels per picture pixel where the text will land: 1 over the overlay's frozen desktop,
    /// the editor's zoom times the display scale in the editor.</param>
    public static TextEntryWindow For(Style style, uint accentArgb, double zoom = 1) => new(ShapeRenderer.ResolveColor(style.Color, accentArgb), style.TextSize * zoom);

    // Small DWM corners (4 px) so the window's clip traces the same curve as the Frame's SmallCornerRadius hairline.
    public TextEntryWindow(uint accentArgb, double sizePx) : base(activate: true, smallCorners: true)
    {
        _sizePx = sizePx;
        InitializeComponent();
        // Invisible until placed, so the parking-size frame is never shown. PopupWindow calls OnSettled once the window
        // is at its own size.
        Surface.Opacity = 0;
        OnSettled = () => { if (!IsClosed) Surface.Opacity = 1; };
        Theme.ThemeManager.Attach(this);
        var ink = new SolidColorBrush(new global::Windows.UI.Color { A = 255, R = (byte)(accentArgb >> 16), G = (byte)(accentArgb >> 8), B = (byte)accentArgb });
        Box.Foreground = ink;
        // The template swaps the foreground per visual state, so the state tokens are overridden too.
        foreach (string key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused", "TextControlForegroundDisabled" })
            Box.Resources[key] = ink;
        Box.FontSize = sizePx;
        Box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape) { Finish(null); e.Handled = true; }
            else if (e.Key == VirtualKey.Enter && !Win32.KeyDown(Win32.VkShift)) { Finish(Box.Text); e.Handled = true; }
        };
        // The window follows the box as it grows. Placing measures and resizes, so it is queued clear of the current
        // layout or input pass.
        Box.TextChanged += (_, _) => DispatcherQueue.TryEnqueue(Place);
        // The frame carries the focus visual. These handlers are on the window's own content, so nothing needs
        // unsubscribing on close.
        Box.GotFocus += (_, _) => ShowFocusFrame(true);
        Box.LostFocus += (_, _) => ShowFocusFrame(false);
        // The frame brush is copied from a Tok swatch, so it is re-read after the framework re-resolves the swatches.
        Frame.ActualThemeChanged += (_, _) => ShowFocusFrame(_frameFocused);
        this.WhenActivated(e => { if (e.WindowActivationState == WindowActivationState.Deactivated) Finish(Box.Text); });
        // A window closed from outside (OverlaySession.Finish) must still complete the task the caller awaits.
        this.WhenClosed(() => _done.TrySetResult(null));
    }

    /// <summary>Which frame brush is showing. Unlike <c>_focused</c>, which records that the box was given focus once,
    /// this follows focus both ways.</summary>
    private bool _frameFocused;

    /// <summary>Accent while the box has focus, Stroke otherwise.</summary>
    private void ShowFocusFrame(bool focused)
    {
        _frameFocused = focused;
        Frame.BorderBrush = focused ? TokAccent.Background : TokStroke.Background;
    }

    /// <summary>
    /// Shows the box with its text origin on the click point and returns the typed text (null on cancel). The owner is
    /// set after showing, because showing runs the presenter's style pass and an owner set earlier does not survive it.
    /// </summary>
    public Task<string?> ShowAt(int physX, int physY, IntPtr owner = default)
    {
        _x = physX; _y = physY;
        TargetArea = new IntRect(physX, physY, 1, 1);   // the click point's monitor sets the scale, and so the font size
        ShowPopup(Place);
        if (owner != IntPtr.Zero) Owner = owner;
        return _done.Task;
    }

#if TONESNIP_HARNESS
    /// <summary>Screenshot harness: fills the box without a keyboard, so the "typed" state can be screenshotted.</summary>
    internal void SetText(string text) => Box.Text = text;
#endif

    /// <summary>Sizes the window to the box and puts the box's text origin on the click point, in physical pixels.</summary>
    private void Place()
    {
        if (IsClosed) return;
        double s = Scale;
        // The shape's Size is device pixels and XAML font sizes are effective pixels, so convert for a true preview.
        double want = Math.Max(6, _sizePx / s);
        if (Math.Abs(Box.FontSize - want) > 0.01) Box.FontSize = want;
        Surface.UpdateLayout();
        (int w, int h) = ContentSize();
        // Diagnostic for an unexpectedly tall measure, logging the state it was measured in.
        if (h > 6 * want * s + 40)
            App.Current.Log.Warn($"text box: measured {w}x{h} for {Box.Text.Length} chars at {want:0} px (root {(Surface.XamlRoot?.Size.Height ?? 0):0} DIP tall, settled {Settled}, focused {_focused})");
        global::Windows.Foundation.Point origin = Box.TransformToVisual(Surface).TransformPoint(new global::Windows.Foundation.Point(Box.Padding.Left + 2, Box.Padding.Top + 1));
        IntRect work = App.Current.KnownOutputs.FirstOrDefault(o => o.Bounds.Contains(_x, _y))?.Bounds
            ?? App.Current.KnownOutputs.Aggregate(IntRect.Empty, (r, o) => r.Union(o.Bounds));
        if (work.IsEmpty) work = new IntRect(_x - 200, _y - 200, 400, 400);
        PlacePhysical(new IntRect(_x - (int)Math.Round(origin.X * s), _y - (int)Math.Round(origin.Y * s), w, h).Nudge(0, 0, work));
        if (Settled) Surface.Opacity = 1;
        if (_focused) return;
        _focused = true;
        Box.Focus(FocusState.Programmatic);
    }

    // Both callers run inside this window's own input dispatch, so the close is queued until that input is done.
    private void Finish(string? text) { if (_done.TrySetResult(text)) DispatcherQueue.TryEnqueue(Close); }
}
