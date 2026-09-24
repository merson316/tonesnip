namespace ToneSnip.Windows.Interop;

public static class Dwm
{
    private const int WindowCornerPreference = 33, BorderColor = 34;
    private const int ColorNone = unchecked((int)0xFFFFFFFE);

    /// <summary>How long two DWM compositions (<see cref="FlushTwice"/>) may be waited for before going ahead anyway. Two
    /// frames at even 24 Hz are about 83 ms.</summary>
    public static readonly TimeSpan CompositionBudget = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Two DWM compositions, so a window just hidden or closed is off the screen, waited for on a pool thread. DwmFlush
    /// returns at the next present, and with the displays off or still waking there may not be one until some window
    /// forces a composition, so callers wait no longer than <see cref="CompositionBudget"/>. A late flush finishes on
    /// its pool thread without holding anything.
    /// </summary>
    public static Task FlushTwice() => Task.Run(() => { Dwmapi.DwmFlush(); Dwmapi.DwmFlush(); });


    /// <summary>DWMWCP_ROUND (8 px), or DWMWCP_ROUNDSMALL (4 px) for a window drawn at the small radius.</summary>
    public static void RoundCorners(IntPtr hwnd, bool small = false) { int v = small ? 3 : 2; Dwmapi.DwmSetWindowAttribute(hwnd, WindowCornerPreference, ref v, sizeof(int)); }

    /// <summary>
    /// Turns off the 1 px system border around a top-level window, which would sit outside a card's own hairline as a
    /// bright outline. Windows 11 22H2+; older builds ignore the attribute.
    /// </summary>
    public static void HideBorder(IntPtr hwnd) { int v = ColorNone; Dwmapi.DwmSetWindowAttribute(hwnd, BorderColor, ref v, sizeof(int)); }

    /// <summary>
    /// Asks DWM for the standard drop shadow. Only drawn for windows with a resizing frame, so the caller pairs it with
    /// <see cref="ToneSnip.Windows.Interop.Frameless"/>, which hides that frame.
    /// </summary>
    public static void ExtendFrame(IntPtr hwnd) { var m = new Dwmapi.Margins { Bottom = 1 }; Dwmapi.DwmExtendFrameIntoClientArea(hwnd, ref m); }
}
