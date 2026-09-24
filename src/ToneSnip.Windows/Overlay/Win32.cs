using System.Diagnostics;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Windows.Interop;

namespace ToneSnip.Windows.Overlay;

/// <summary>
/// The user32 vocabulary the overlay windows are built from: keys, messages, styles and the foreground helper. The
/// imports themselves are in <see cref="User32"/>. Everything here is physical pixels: the app's manifest is
/// PerMonitorV2 and the overlay never goes through a framework's DIP layout.
/// </summary>
public static class Win32
{
    // ----- virtual keys (the overlay's whole keyboard vocabulary) -----
    public const int VkTab = 0x09, VkReturn = 0x0D, VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkEscape = 0x1B, VkSpace = 0x20,
                     VkLeft = 0x25, VkUp = 0x26, VkRight = 0x27, VkDown = 0x28,
                     VkA = 0x41, VkC = 0x43, VkF = 0x46, VkL = 0x4C, VkP = 0x50, VkR = 0x52, VkT = 0x54, VkW = 0x57;

    /// <summary>True while <paramref name="vk"/> is held.</summary>
    public static bool KeyDown(int vk) => (User32.GetKeyState(vk) & 0x8000) != 0;

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

    internal delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Brings a window to the foreground and gives it the keyboard even though this process is not the foreground one
    /// (a hotkey from the low-level hook does not count as user input): attaches to the foreground thread's input
    /// queue for the duration of the call. Used by the overlay and the WinUI popups alike.
    /// <para>Attaching waits on the foreground thread, so a hung foreground app stalls the caller: a call slower than
    /// <see cref="SlowForeground"/> is logged, since from outside it looks like a hotkey that did nothing.</para>
    /// </summary>
    public static void ForceForeground(IntPtr hwnd, ILog? log = null)
    {
        long started = Stopwatch.GetTimestamp();
        IntPtr fg = User32.GetForegroundWindow();
        uint fgThread = fg != IntPtr.Zero ? User32.GetWindowThreadProcessId(fg, out _) : 0, me = Kernel32.GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && User32.AttachThreadInput(me, fgThread, true);
        try { User32.BringWindowToTop(hwnd); User32.SetForegroundWindow(hwnd); User32.SetFocus(hwnd); }
        finally
        {
            if (attached) User32.AttachThreadInput(me, fgThread, false);
            TimeSpan took = Stopwatch.GetElapsedTime(started);
            if (took > SlowForeground) log?.Warn($"foreground: taking the foreground took {took.TotalMilliseconds:F0} ms{(attached ? " with the foreground app's input attached" : "")}");
        }
    }

    /// <summary>The tick count (<c>GetTickCount</c>'s clock) of the last keyboard or mouse input anywhere in the session,
    /// or 0 when Windows would not say.</summary>
    public static uint LastInputTick()
    {
        var info = new User32.LastInputInfo { Size = 8 };
        return User32.GetLastInputInfo(ref info) ? info.Time : 0;
    }

    private static readonly TimeSpan SlowForeground = TimeSpan.FromMilliseconds(200);

    /// <summary>The low and high 16-bit halves of a message parameter, signed (mouse coordinates go negative).</summary>
    internal static int LoWord(IntPtr v) => (short)(v.ToInt64() & 0xFFFF);
    internal static int HiWord(IntPtr v) => (short)((v.ToInt64() >> 16) & 0xFFFF);
}
