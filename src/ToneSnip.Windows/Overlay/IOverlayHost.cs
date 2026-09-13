using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;

namespace ToneSnip.Windows.Overlay;

/// <summary>Which cursor an overlay window shows; the host picks it, the window owns the handle.</summary>
public enum OverlayCursor { Cross, Arrow, IBeam }

/// <summary>
/// The selection state an <see cref="OverlayWindow"/> paints and the input it reports back. Implemented by the app's
/// OverlaySession: everything the window would otherwise need to know about annotation documents, settings or the
/// luminance readout stays on that side, and this library keeps out of WinUI and GDI+.
/// All coordinates are physical virtual-desktop pixels unless a member says otherwise.
/// </summary>
public interface IOverlayHost
{
    /// <summary>The committed selection; <see cref="Hover"/> stands in while it is empty (window and full-screen modes).</summary>
    IntRect Selection { get; }
    IntRect Hover { get; }
    /// <summary>The freeform outline being drawn, in screen pixels.</summary>
    IReadOnlyList<(int X, int Y)> Path { get; }
    (int X, int Y) Cursor { get; }
    /// <summary>The annotate state: with it on, the dim depends on whether anything is selected and the chrome is painted.</summary>
    bool Annotating { get; }
    /// <summary>True while a drawing tool owns the mouse: no cursor pill, and the dim follows the annotate rules.</summary>
    bool DrawingActive { get; }
    /// <summary>True once an annotation document exists, so the annotated back buffer replaces the frozen frame.</summary>
    bool HasDocument { get; }

    /// <summary>The cursor pill's text for one monitor (size, nits), or null for no pill.</summary>
    string? PillText(IntRect monitor);

    void OnMouseMove(int x, int y);
    void OnMouseDown(int x, int y);
    void OnMouseUp(int x, int y);
    void OnRightClick();
    /// <summary>The mouse capture went to something else mid-drag: the matching button-up will never arrive.</summary>
    void OnCaptureLost();
    /// <summary>A key went down: a virtual-key code plus the modifier state read from the keyboard, not from a framework.</summary>
    void OnKey(int vk, bool ctrl, bool shift, bool alt);
    /// <summary>An overlay window took the foreground; <paramref name="hwnd"/> says which, so the host can refocus it later.</summary>
    void OnActivated(IntPtr hwnd);
    /// <summary>The first WM_PAINT of any overlay window: how long the frozen desktop took to appear.</summary>
    void OnFirstPaint();

    /// <summary>
    /// Something threw inside a window procedure. The procedure is a reverse P/Invoke, so the exception cannot be
    /// allowed to unwind through <c>user32!DispatchMessage</c>; the host logs it and ends the session, because the
    /// alternative is a frozen copy of the desktop left on screen with nothing driving it.
    /// </summary>
    void Fail(Exception error);

    /// <summary>
    /// Renders the annotation document for one monitor: (base frame, viewport, target, viewport-relative dirty area)
    /// and returns the viewport-relative rectangle actually painted, which may be larger than the dirty area.
    /// </summary>
    Func<BgraImage, IntRect, BgraImage, IntRect?, IntRect> RenderShapes { get; }

    /// <summary>Paints the in-progress shape, the handles and the crop marquee straight onto the overlay's device context.</summary>
    Action<IntPtr, IntRect> DrawChrome { get; }

    /// <summary>Optional clipping stripes over the annotated buffer: (back buffer, monitor, painted area).</summary>
    Action<BgraImage, IntRect, IntRect>? Zebra { get; }
}
