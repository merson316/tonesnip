using System.Runtime.InteropServices;

namespace ToneSnip.Windows.Overlay;

/// <summary>
/// The user32 declarations the overlay windows are built from. Everything here is physical pixels: the app's
/// manifest is PerMonitorV2 and the overlay never goes through a framework's DIP layout.
/// </summary>
public static class Win32
{
    // ----- virtual keys (the overlay's whole keyboard vocabulary) -----
    public const int VkReturn = 0x0D, VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkEscape = 0x1B,
                     VkLeft = 0x25, VkUp = 0x26, VkRight = 0x27, VkDown = 0x28,
                     VkA = 0x41, VkF = 0x46, VkL = 0x4C, VkR = 0x52, VkW = 0x57;

    /// <summary>True while <paramref name="vk"/> is held.</summary>
    public static bool KeyDown(int vk) => (GetKeyState(vk) & 0x8000) != 0;

    // ----- messages -----
    internal const uint WmDestroy = 0x0002, WmActivate = 0x0006, WmPaint = 0x000F, WmEraseBkgnd = 0x0014,
                        WmSetCursor = 0x0020, WmKeyDown = 0x0100, WmSysKeyDown = 0x0104,
                        WmMouseMove = 0x0200, WmLButtonDown = 0x0201, WmLButtonUp = 0x0202, WmRButtonUp = 0x0205,
                        WmCaptureChanged = 0x0215;

    // ----- styles -----
    internal const int WsPopup = unchecked((int)0x80000000);
    internal const int WsExToolWindow = 0x00000080, WsExTopmost = 0x00000008;
    internal const uint CsOwnDc = 0x0020;
    internal const int SwShowNoActivate = 4;

    /// <summary>IDC_ARROW, IDC_IBEAM, IDC_CROSS.</summary>
    internal const int IdcArrow = 32512, IdcIBeam = 32513, IdcCross = 32515;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct PaintStruct
    {
        public IntPtr Hdc;
        public int Erase;
        public Rect Paint;
        public int Restore, IncUpdate;
        public fixed byte Reserved[32];   // rgbReserved, at native offset 36: bytes, so no alignment padding creeps in
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WndClassEx
    {
        public uint Size, Style;
        public IntPtr WndProc;
        public int ClsExtra, WndExtra;
        public IntPtr Instance, Icon, Cursor, Background, MenuName, ClassName, IconSm;
    }

    internal delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClassExW(ref WndClassEx c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool UnregisterClassW(string className, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr CreateWindowExW(int exStyle, string className, string? windowName, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] internal static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] internal static extern bool InvalidateRect(IntPtr hwnd, ref Rect r, bool erase);
    [DllImport("user32.dll")] internal static extern bool InvalidateRect(IntPtr hwnd, IntPtr all, bool erase);
    [DllImport("user32.dll")] internal static extern IntPtr BeginPaint(IntPtr hwnd, out PaintStruct ps);
    [DllImport("user32.dll")] internal static extern bool EndPaint(IntPtr hwnd, ref PaintStruct ps);
    [DllImport("user32.dll")] internal static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] internal static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint to, bool flag);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandleW(string? name);

    /// <summary>
    /// Brings a window to the foreground and gives it the keyboard even though this process is not the foreground one
    /// (a hotkey from the low-level hook does not count as user input): attaches to the foreground thread's input
    /// queue for the duration of the call. Used by the overlay and the WinUI popups alike.
    /// </summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        IntPtr fg = GetForegroundWindow();
        uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, IntPtr.Zero) : 0, me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        try { BringWindowToTop(hwnd); SetForegroundWindow(hwnd); SetFocus(hwnd); }
        finally { if (attached) AttachThreadInput(me, fgThread, false); }
    }

    /// <summary>The low and high 16-bit halves of a message parameter, signed (mouse coordinates go negative).</summary>
    internal static int LoWord(IntPtr v) => (short)(v.ToInt64() & 0xFFFF);
    internal static int HiWord(IntPtr v) => (short)((v.ToInt64() >> 16) & 0xFFFF);
}
