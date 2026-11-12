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

    /// <summary>Enough for a browser, an IDE or Explorer; past it the rest of a huge tree is left out rather than walked.</summary>
    private const int MaxChildren = 2000;

    /// <summary>
    /// What window mode can pick inside <paramref name="window"/>: its frame, and with <paramref name="children"/> the
    /// visible child windows inside it (<see cref="WindowLayers"/>). Only window rectangles are read, never sent a
    /// message, so a hung app cannot stall the overlay. A window that cannot be read gives its frame alone.
    /// </summary>
    public static WindowLayers Layers(WindowInfo window, bool children)
    {
        if (!children || ClientOnScreen(window.Handle) is not { } client) return new WindowLayers(window.Bounds);
        var list = new List<ChildWindow>();
        var index = new Dictionary<IntPtr, int>();
        User32.EnumChildWindows(window.Handle, (hwnd, _) =>
        {
            if (list.Count >= MaxChildren) return false;
            // IsWindowVisible also checks the ancestors, so a hidden tab page's controls are skipped with it.
            if (!User32.IsWindowVisible(hwnd) || !User32.GetWindowRect(hwnd, out User32.Rect r)) return true;
            if (ClientOnScreen(hwnd) is not { } own) return true;
            IntPtr parent = User32.GetAncestor(hwnd, 1 /*GA_PARENT*/);
            int p = parent == window.Handle ? -1 : index.TryGetValue(parent, out int at) ? at : int.MaxValue;
            if (p == int.MaxValue) return true;   // its parent was skipped, so it is not on screen either
            index[hwnd] = list.Count;
            list.Add(new ChildWindow(IntRect.FromLtrb(r.Left, r.Top, r.Right, r.Bottom), own, p));
            return true;
        }, IntPtr.Zero);
        return new WindowLayers(window.Bounds, client, list);
    }

    /// <summary>
    /// True when the window, or a child window of it, is drawn by Chromium: Chrome, Edge, an Electron app, or a WebView2
    /// inside another app. Such a window is not asked for its UI Automation tree (<see cref="UiaTree"/>): the first
    /// question switches on the browser's accessibility support for as long as it runs, which slows it down, and its
    /// page is a child window that <see cref="Layers"/> finds anyway. Only class names are read, so nothing is sent
    /// to the app.
    /// </summary>
    public static bool DrawnByChromium(IntPtr window)
    {
        var name = new char[64];
        bool IsChromium(IntPtr hwnd)
        {
            int length = User32.GetClassNameW(hwnd, name, name.Length);
            return length > 0 && name.AsSpan(0, Math.Min(length, name.Length)).StartsWith("Chrome_", StringComparison.Ordinal);
        }
        if (IsChromium(window)) return true;
        bool found = false;
        int seen = 0;
        User32.EnumChildWindows(window, (hwnd, _) =>
        {
            if (IsChromium(hwnd)) found = true;
            return !found && ++seen < MaxChildren;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>A window's client area in screen pixels. Both corners are mapped, so a right-to-left window, whose
    /// client origin is its right edge, still gives a normal rectangle.</summary>
    private static IntRect? ClientOnScreen(IntPtr hwnd)
    {
        if (!User32.GetClientRect(hwnd, out User32.Rect c)) return null;
        var a = new User32.Point { X = c.Left, Y = c.Top };
        var b = new User32.Point { X = c.Right, Y = c.Bottom };
        if (!User32.ClientToScreen(hwnd, ref a) || !User32.ClientToScreen(hwnd, ref b)) return null;
        return IntRect.FromLtrb(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
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
