using System.Runtime.InteropServices;

namespace ToneSnip.Windows.Interop;

/// <summary>
/// The drop shadow for a window that draws its own chrome. DWM only shadows windows with a resizing frame, so the
/// window keeps <c>WS_THICKFRAME</c> and hides it: <c>WM_NCCALCSIZE</c> makes the client area the whole window and
/// <c>WM_NCHITTEST</c> answers <c>HTCLIENT</c> everywhere. <see cref="Dwm.ExtendFrame"/> completes the recipe.
/// </summary>
/// <remarks>
/// Uses comctl32 subclassing rather than <c>SetWindowLongPtr(GWLP_WNDPROC)</c>, because the XAML island installs its
/// own window procedures on these windows.
/// </remarks>
public static class Frameless
{
    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr refData);

    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback, IntPtr id, IntPtr refData);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc callback, IntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

    private const int GwlStyle = -16;
    private const long WsThickFrame = 0x00040000;
    private const uint WmNcCalcSize = 0x0083, WmNcHitTest = 0x0084, WmNcDestroy = 0x0082, WmGetMinMaxInfo = 0x0024;
    private const int HtClient = 1;
    /// <summary>MINMAXINFO.ptMinTrackSize: three POINTs in.</summary>
    private const int MinTrackSize = 24;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010,
                       SwpFrameChanged = 0x0020, SwpNoOwnerZOrder = 0x0200;

    /// <summary>One static delegate for every window, so the marshalled thunk outlives every subclass.</summary>
    private static readonly SubclassProc Proc = OnMessage;

    /// <summary>
    /// Puts the shadow on <paramref name="hwnd"/> and returns whether the resizing frame that earns it is now on the
    /// window. The subclass goes on first, so the frame is hidden before it can be painted once.
    /// </summary>
    public static bool AddShadow(IntPtr hwnd)
    {
        if (!SetWindowSubclass(hwnd, Proc, IntPtr.Zero, IntPtr.Zero)) return false;
        if (!ApplyFrame(hwnd)) return false;
        Dwm.ExtendFrame(hwnd);
        return true;
    }

    /// <summary>Adds WS_THICKFRAME if it is missing and reports whether it stuck; a presenter that rewrites the style
    /// after the window is shown removes it again.</summary>
    public static bool ApplyFrame(IntPtr hwnd)
    {
        if (HasFrame(hwnd)) return true;
        long style = (long)GetWindowLongPtr(hwnd, GwlStyle);
        SetWindowLongPtr(hwnd, GwlStyle, (IntPtr)(style | WsThickFrame));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoOwnerZOrder | SwpNoActivate | SwpFrameChanged);
        return HasFrame(hwnd);
    }

    public static bool HasFrame(IntPtr hwnd) => ((long)GetWindowLongPtr(hwnd, GwlStyle) & WsThickFrame) != 0;

    private static IntPtr OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr refData)
    {
        // Returning 0 with the rectangle untouched makes the whole window client area: no visible frame or caption.
        if (msg == WmNcCalcSize && wParam != IntPtr.Zero) return IntPtr.Zero;
        // The frame exists only for the shadow; nothing is resizable or draggable.
        if (msg == WmNcHitTest) return new IntPtr(HtClient);
        // The frame's minimum tracking size is enforced on every SetWindowPos and would inflate small windows.
        if (msg == WmGetMinMaxInfo && lParam != IntPtr.Zero)
        {
            IntPtr filled = DefSubclassProc(hwnd, msg, wParam, lParam);
            Marshal.WriteInt32(lParam, MinTrackSize, 0);
            Marshal.WriteInt32(lParam, MinTrackSize + sizeof(int), 0);
            return filled;
        }
        if (msg == WmNcDestroy) RemoveWindowSubclass(hwnd, Proc, IntPtr.Zero);
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }
}
