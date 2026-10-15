using System.Runtime.InteropServices;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Geometry;
using ToneSnip.Windows.Interop;

namespace ToneSnip.Windows.Overlay;

/// <summary>
/// The outline around an area being recorded: a ring of solid colour just outside the area, topmost, click-through,
/// never activated, and left out of every capture (WDA_EXCLUDEFROMCAPTURE), so it shows what is recorded without being
/// in the video. Where the area meets its monitor's edge the ring runs just inside it instead, where the capture leaves
/// it out all the same.
/// <para>Plain Win32, like the overlay's frames: one window, shaped to the ring by its region and painted in one
/// colour. Create and dispose it on the UI thread.</para>
/// </summary>
public sealed class RecordingFrame : IDisposable
{
    private const string ClassName = "tonesnip-recording-frame";
    private const int WsExTransparent = 0x20, WsExLayered = 0x80000, WsExNoActivate = 0x08000000;
    private const uint LwaAlpha = 0x2, SwpNoActivate = 0x10, SwpNoMove = 0x2, SwpNoSize = 0x1;
    private const int HtTransparent = -1, WmNcHitTest = 0x0084;
    private static readonly IntPtr HwndTopmost = new(-1);

    private static readonly Win32.WndProcDelegate StaticProc = Proc;   // static field: the class holds a raw function pointer to it
    private static readonly Dictionary<IntPtr, RecordingFrame> Live = new();
    private static bool _registered;

    private readonly IntPtr _brush;
    private readonly ILog _log;
    private bool _disposed;

    public IntPtr Hwnd { get; }

    /// <param name="area">The recorded area and <paramref name="monitor"/> its monitor, in virtual-desktop pixels.</param>
    /// <param name="rgb">The ring's colour, 0xRRGGBB.</param>
    /// <param name="thickness">The ring's width in pixels.</param>
    public RecordingFrame(IntRect area, IntRect monitor, uint rgb, int thickness, ILog log)
    {
        _log = log;
        IntRect outer = IntRect.FromLtrb(Math.Max(area.Left - thickness, monitor.Left), Math.Max(area.Top - thickness, monitor.Top),
                                         Math.Min(area.Right + thickness, monitor.Right), Math.Min(area.Bottom + thickness, monitor.Bottom));
        _brush = Gdi32.CreateSolidBrush(((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF));
        IntPtr instance = Kernel32.GetModuleHandleW(null);
        lock (Live)
        {
            if (!_registered) Register(instance);
            Hwnd = User32.CreateWindowExW(Win32.WsExToolWindow | Win32.WsExTopmost | WsExTransparent | WsExLayered | WsExNoActivate, ClassName, "ToneSnip recording", Win32.WsPopup,
                                          outer.Left, outer.Top, outer.Width, outer.Height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (Hwnd == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                Gdi32.DeleteObject(_brush);
                throw new InvalidOperationException("CreateWindowExW failed: " + error);
            }
            Live[Hwnd] = this;
        }
        User32.SetLayeredWindowAttributes(Hwnd, 0, 255, LwaAlpha);
        if (!User32.SetWindowDisplayAffinity(Hwnd, User32.WdaExcludeFromCapture))
            log.Warn($"record: the outline could not be left out of the capture ({Marshal.GetLastWin32Error()}); it may show in the video");
        IntPtr ring = Gdi32.CreateRectRgn(0, 0, outer.Width, outer.Height);
        IntPtr hole = Gdi32.CreateRectRgn(thickness, thickness, outer.Width - thickness, outer.Height - thickness);
        _ = Gdi32.CombineRgn(ring, ring, hole, Gdi32.RgnDiff);
        Gdi32.DeleteObject(hole);
        if (User32.SetWindowRgn(Hwnd, ring, false) == 0) Gdi32.DeleteObject(ring);
        User32.ShowWindow(Hwnd, Win32.SwShowNoActivate);
    }

    /// <summary>Puts the outline back on top of the topmost windows, after one that opened since covered it.</summary>
    public void Raise()
    {
        if (!_disposed) User32.SetWindowPos(Hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private static void Register(IntPtr instance)
    {
        IntPtr className = Marshal.StringToHGlobalUni(ClassName);
        try
        {
            var wc = new User32.WndClassEx
            {
                Size = (uint)Marshal.SizeOf<User32.WndClassEx>(),
                WndProc = Marshal.GetFunctionPointerForDelegate(StaticProc),
                Instance = instance,
                ClassName = className,
            };
            if (User32.RegisterClassExW(ref wc) == 0) throw new InvalidOperationException("RegisterClassExW failed: " + Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(className); }
        _registered = true;
    }

    private static IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        RecordingFrame? self;
        lock (Live) Live.TryGetValue(hwnd, out self);
        if (self == null) return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
        try
        {
            switch (msg)
            {
                case WmNcHitTest: return HtTransparent;
                case Win32.WmEraseBkgnd: return 1;
                case Win32.WmPaint:
                    IntPtr dc = User32.BeginPaint(hwnd, out User32.PaintStruct ps);
                    if (dc == IntPtr.Zero) return IntPtr.Zero;
                    try { User32.FillRect(dc, ref ps.Paint, self._brush); }
                    finally { User32.EndPaint(hwnd, ref ps); }
                    return IntPtr.Zero;
                default: return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
            }
        }
        catch (Exception e)
        {
            // A raw function pointer from user32: an exception must not unwind through DispatchMessage.
            self._log.Error($"record: outline msg 0x{msg:X4}: {e}");
            return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        User32.DestroyWindow(Hwnd);
        lock (Live)
        {
            Live.Remove(Hwnd);
            if (Live.Count == 0 && _registered) _registered = !User32.UnregisterClassW(ClassName, Kernel32.GetModuleHandleW(null));
        }
        Gdi32.DeleteObject(_brush);
    }
}
