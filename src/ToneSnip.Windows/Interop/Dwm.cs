using System.Runtime.InteropServices;

namespace ToneSnip.Windows.Interop;

public static class Dwm
{
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    private const int WindowCornerPreference = 33, BorderColor = 34;
    private const int ColorNone = unchecked((int)0xFFFFFFFE);

    public static void Flush() => DwmFlush();



    /// <summary>DWMWCP_ROUND (8 px), or DWMWCP_ROUNDSMALL (4 px) for a window drawn at the small radius.</summary>
    public static void RoundCorners(IntPtr hwnd, bool small = false) { int v = small ? 3 : 2; DwmSetWindowAttribute(hwnd, WindowCornerPreference, ref v, sizeof(int)); }

    /// <summary>
    /// Turns off the 1 px system border around a top-level window, which would sit outside a card's own hairline as a
    /// bright outline. Windows 11 22H2+; older builds ignore the attribute.
    /// </summary>
    public static void HideBorder(IntPtr hwnd) { int v = ColorNone; DwmSetWindowAttribute(hwnd, BorderColor, ref v, sizeof(int)); }

    /// <summary>
    /// Asks DWM for the standard drop shadow. Only drawn for windows with a resizing frame, so the caller pairs it with
    /// <see cref="ToneSnip.Windows.Interop.Frameless"/>, which hides that frame.
    /// </summary>
    public static void ExtendFrame(IntPtr hwnd) { var m = new Margins { Bottom = 1 }; DwmExtendFrameIntoClientArea(hwnd, ref m); }
}
