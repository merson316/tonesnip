using System.Runtime.InteropServices;
using ToneSnip.Core.Geometry;
using ToneSnip.Windows.Interop;

namespace ToneSnip.App.Interop;

public static class Native
{
    private const uint SpiGetWorkArea = 0x0030;
    private const uint MonitorDefaultToNearest = 2;
    public const int WsExNoActivate = 0x08000000, WsExToolWindow = 0x00000080, GwlExStyle = -20, GwlpHwndParent = -8;

    public static (int X, int Y) CursorPos() { User32.GetCursorPos(out User32.Point p); return (p.X, p.Y); }

    /// <summary>Every monitor's GDI device name and physical bounds, without the graphics stack the frame grabber's
    /// enumeration brings up.</summary>
    public static List<(string Device, IntRect Bounds)> Monitors()
    {
        var found = new List<(string, IntRect)>();
        bool Add(IntPtr h, IntPtr hdc, IntPtr rect, IntPtr data)
        {
            var info = new User32.MonitorInfoEx { Size = (uint)Marshal.SizeOf<User32.MonitorInfoEx>() };
            if (User32.GetMonitorInfoW(h, ref info)) found.Add((info.Device, IntRect.FromLtrb(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom)));
            return true;
        }
        User32.MonitorEnumProc proc = Add;   // kept alive for the duration of the native call
        User32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);
        return found;
    }

    /// <summary>The primary monitor's work area (excluding the taskbar) in physical pixels, or empty on
    /// failure.</summary>
    public static IntRect PrimaryWorkArea()
        => User32.SystemParametersInfoW(SpiGetWorkArea, 0, out User32.Rect r, 0) ? IntRect.FromLtrb(r.Left, r.Top, r.Right, r.Bottom) : IntRect.Empty;

    /// <summary>Physical pixels per effective pixel for the monitor a window's HWND is currently on.</summary>
    public static double Scale(IntPtr hwnd) => User32.GetDpiForWindow(hwnd) / 96.0;

    /// <summary>
    /// Physical pixels per effective pixel for the monitor holding a physical-pixel rectangle. Popups need this before
    /// they are on that monitor, since they lay out parked off-screen at another monitor's scale. Falls back to 1.0.
    /// </summary>
    public static double ScaleAt(IntRect area)
    {
        try
        {
            var r = new User32.Rect { Left = area.Left, Top = area.Top, Right = area.Right, Bottom = area.Bottom };
            IntPtr monitor = User32.MonitorFromRect(ref r, MonitorDefaultToNearest);
            if (Shcore.GetDpiForMonitor(monitor, 0 /*MDT_EFFECTIVE_DPI*/, out uint dpiX, out _) == 0 && dpiX > 0) return dpiX / 96.0;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return 1.0;
    }

    /// <summary>Where a window's client area starts on the desktop, in physical pixels. WinUI has no PointToScreen,
    /// so an element's own offset (in effective pixels, scaled) is added to this instead.</summary>
    public static (int X, int Y) ClientOrigin(IntPtr hwnd) { User32.Point p = default; User32.ClientToScreen(hwnd, ref p); return (p.X, p.Y); }

    /// <summary>
    /// Sets a window's owner (zero for none). An owned window stays above its owner, even when the owner is activated.
    /// <para>GWLP_HWNDPARENT sets the owner, not the parent. Destroying a window destroys the windows it owns, so a
    /// popup that outlives its owner must be disowned first.</para>
    /// </summary>
    public static void SetOwner(IntPtr hwnd, IntPtr owner) => User32.SetWindowLongPtr(hwnd, GwlpHwndParent, owner);

    public static void AddExStyle(IntPtr hwnd, int style) => User32.SetWindowLongW(hwnd, GwlExStyle, User32.GetWindowLongW(hwnd, GwlExStyle) | style);
}
