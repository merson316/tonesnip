using System.Runtime.InteropServices;
using ToneSnip.Core.Geometry;

namespace ToneSnip.Windows.Interop;

public sealed record WindowInfo(IntPtr Handle, IntRect Bounds, string Title);

/// <summary>Top-level windows in z-order (front first) with their DWM extended frame bounds, for window snips.</summary>
public static class WindowFinder
{
    private const int DwmaCloaked = 14, DwmaExtendedFrameBounds = 9, DwmaWindowCornerPreference = 33, GwlExStyle = -20, WsExToolWindow = 0x80;

    public static List<WindowInfo> TopLevel(ISet<IntPtr>? exclude = null)
    {
        var list = new List<WindowInfo>();
        User32.EnumWindows((hwnd, _) =>
        {
            if (exclude != null && exclude.Contains(hwnd)) return true;
            WindowInfo? w = Describe(hwnd);
            if (w != null) list.Add(w);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static WindowInfo? Foreground() => Describe(User32.GetForegroundWindow());


    /// <summary>
    /// The window an active-window snip means: the foreground window when a snip can take it, otherwise the front-most
    /// such window of another process. From the tray menu the foreground is this app's own tool window, which
    /// <see cref="Describe"/> refuses, while the window the user was in is still next in z-order.
    /// </summary>
    public static WindowInfo? ActiveForSnip()
    {
        if (Foreground() is { } fg) return fg;
        uint self = (uint)Environment.ProcessId;
        return TopLevel().FirstOrDefault(w => User32.GetWindowThreadProcessId(w.Handle, out uint pid) != 0 && pid != self);
    }


    /// <summary>A window's visible frame (DWM's extended frame bounds) and its full rectangle, invisible resize borders
    /// included, in physical pixels (the app is per-monitor DPI aware); empty rectangles when either cannot be read.</summary>
    public static (IntRect Frame, IntRect Window) Rects(IntPtr hwnd)
    {
        if (Dwmapi.DwmGetWindowAttribute(hwnd, DwmaExtendedFrameBounds, out User32.Rect f, Marshal.SizeOf<User32.Rect>()) != 0
            || !User32.GetWindowRect(hwnd, out User32.Rect w)) return (IntRect.Empty, IntRect.Empty);
        return (IntRect.FromLtrb(f.Left, f.Top, f.Right, f.Bottom), IntRect.FromLtrb(w.Left, w.Top, w.Right, w.Bottom));
    }

    /// <summary>
    /// The radius, in physical pixels, DWM rounds <paramref name="hwnd"/>'s corners with at <paramref name="dpi"/>: from
    /// its corner preference (DWMWCP_ROUND by default, 4 px at 96 DPI for DWMWCP_ROUNDSMALL), and zero when it is not
    /// rounded: it opted out, it is maximised, or this is Windows 10, which rounds nothing. A snapped window is square
    /// too but cannot be told apart here; <see cref="Core.Imaging.WindowCorners"/> uses this only when no corner of the
    /// capture says otherwise.
    /// </summary>
    public static double CornerRadius(IntPtr hwnd, int dpi)
    {
        if (Environment.OSVersion.Version.Build < 22000 || User32.IsZoomed(hwnd)) return 0;
        int preference = Dwmapi.DwmGetWindowAttribute(hwnd, DwmaWindowCornerPreference, out int v, sizeof(int)) == 0 ? v : 0;
        int radius96 = preference switch { 1 => 0, 3 => 4, _ => Core.Imaging.WindowCorners.Radius96 };
        return radius96 * Math.Max(dpi, 96) / 96.0;
    }

    private const int WsExLayered = 0x80000;
    private const uint LwaColorKey = 0x1, LwaAlpha = 0x2;

    /// <summary>
    /// Why a window should be snipped from the screen rather than captured on its own, or null when it can be:
    /// <list type="bullet">
    /// <item>it is gone or minimised: a minimised window has no picture to capture;</item>
    /// <item>it is layered and see-through (per-pixel alpha, a colour key, or less than full opacity): what it looks like
    /// depends on what is behind it, which a capture of the window alone does not have;</item>
    /// <item>it belongs to an elevated process and this one is not: Windows.Graphics.Capture may refuse it or hand back a
    /// blank frame, and the screen shows it as it is.</item>
    /// </list>
    /// </summary>
    public static string? CaptureRefusal(IntPtr hwnd)
    {
        if (!User32.IsWindow(hwnd)) return "the window is gone";
        if (User32.IsIconic(hwnd)) return "the window is minimised";
        if ((User32.GetWindowLongW(hwnd, GwlExStyle) & WsExLayered) != 0)
        {
            // An UpdateLayeredWindow window fails this call: its per-pixel alpha is always see-through somewhere.
            if (!User32.GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out uint flags)) return "the window is layered with per-pixel transparency";
            if ((flags & LwaColorKey) != 0 || ((flags & LwaAlpha) != 0 && alpha < 255)) return "the window is layered and see-through";
        }
        if (!Elevation.Self && User32.GetWindowThreadProcessId(hwnd, out uint pid) != 0 && Elevation.OfProcess(pid) != false)
            return "the window belongs to an elevated or protected process";
        return null;
    }

    private static WindowInfo? Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !User32.IsWindowVisible(hwnd) || User32.IsIconic(hwnd)) return null;
        if (Dwmapi.DwmGetWindowAttribute(hwnd, DwmaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return null;
        if ((User32.GetWindowLongW(hwnd, GwlExStyle) & WsExToolWindow) != 0) return null;
        if (Dwmapi.DwmGetWindowAttribute(hwnd, DwmaExtendedFrameBounds, out User32.Rect r, Marshal.SizeOf<User32.Rect>()) != 0) return null;
        var bounds = IntRect.FromLtrb(r.Left, r.Top, r.Right, r.Bottom);
        if (bounds.IsEmpty) return null;
        var title = new char[256];
        int length = User32.GetWindowTextW(hwnd, title, title.Length);
        return new WindowInfo(hwnd, bounds, new string(title, 0, Math.Clamp(length, 0, title.Length)));
    }
}
