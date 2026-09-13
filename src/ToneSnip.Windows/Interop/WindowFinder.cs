using System.Runtime.InteropServices;
using System.Text;
using ToneSnip.Core.Geometry;

namespace ToneSnip.Windows.Interop;

public sealed record WindowInfo(IntPtr Handle, IntRect Bounds, string Title);

/// <summary>Top-level windows in z-order (front first) with their DWM extended frame bounds, for window snips.</summary>
public static class WindowFinder
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr hwnd, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out Rect value, int size);
    private const int DwmaCloaked = 14, DwmaExtendedFrameBounds = 9, GwlExStyle = -20, WsExToolWindow = 0x80;

    public static List<WindowInfo> TopLevel(ISet<IntPtr>? exclude = null)
    {
        var list = new List<WindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            if (exclude != null && exclude.Contains(hwnd)) return true;
            WindowInfo? w = Describe(hwnd);
            if (w != null) list.Add(w);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static WindowInfo? Foreground() => Describe(GetForegroundWindow());

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    /// <summary>
    /// The window an active-window snip means: the foreground window when a snip can take it, otherwise the front-most
    /// such window of another process. From the tray menu the foreground is this app's own tool window, which
    /// <see cref="Describe"/> refuses, while the window the user was in is still next in z-order.
    /// </summary>
    public static WindowInfo? ActiveForSnip()
    {
        if (Foreground() is { } fg) return fg;
        uint self = (uint)Environment.ProcessId;
        return TopLevel().FirstOrDefault(w => GetWindowThreadProcessId(w.Handle, out uint pid) != 0 && pid != self);
    }


    private static WindowInfo? Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return null;
        if (DwmGetWindowAttribute(hwnd, DwmaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return null;
        if ((GetWindowLongW(hwnd, GwlExStyle) & WsExToolWindow) != 0) return null;
        if (DwmGetWindowAttribute(hwnd, DwmaExtendedFrameBounds, out Rect r, Marshal.SizeOf<Rect>()) != 0) return null;
        var bounds = IntRect.FromLtrb(r.Left, r.Top, r.Right, r.Bottom);
        if (bounds.IsEmpty) return null;
        var sb = new StringBuilder(256);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return new WindowInfo(hwnd, bounds, sb.ToString());
    }
}
