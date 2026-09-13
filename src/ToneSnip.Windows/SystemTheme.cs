using System.Runtime.InteropServices;

namespace ToneSnip.Windows;

/// <summary>
/// The system's contrast-theme state and colours, shared by the app's theme dictionaries and the owner-drawn Win32
/// surfaces (tray menu, tray glyph) that cannot ask XAML.
/// </summary>
public static class SystemTheme
{
    private const uint SpiGetHighContrast = 0x0042;
    private const uint HcfHighContrastOn = 0x01;

    /// <summary>HIGHCONTRASTW. <c>lpszDefaultScheme</c> is left null: SPI_GETHIGHCONTRAST only copies the scheme name
    /// into a caller-supplied buffer, so the flags come back on their own.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrast
    {
        public uint Size;
        public uint Flags;
        public IntPtr DefaultScheme;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfoW(uint action, uint param, ref HighContrast data, uint update);

    [DllImport("user32.dll")] private static extern uint GetSysColor(int index);

    /// <summary>
    /// The <c>GetSysColor</c> indices the owner-drawn tray surfaces paint from while <see cref="IsHighContrast"/> is
    /// true. Each is half of a pair the contrast theme guarantees: MENU/MENUTEXT for the popup, HIGHLIGHT/HIGHLIGHTTEXT
    /// for the hovered row, WINDOW/WINDOWTEXT for the notification area, GRAYTEXT for non-interactive content.
    /// </summary>
    public const int ColorMenu = 4, ColorWindow = 5, ColorMenuText = 7, ColorWindowText = 8,
                     ColorHighlight = 13, ColorHighlightText = 14, ColorGrayText = 17;

    /// <summary>
    /// One system colour as opaque 0xAARRGGBB. GetSysColor returns a COLORREF (0x00BBGGRR) and has no failure return
    /// (an unknown index comes back black), so only the constants above should be passed.
    /// </summary>
    /// <remarks>
    /// Not <c>GetSysColorBrush</c>: those brushes are system-owned and must never be deleted, while every brush the
    /// tray menu creates is deleted after painting.
    /// </remarks>
    public static uint SysColorArgb(int index)
    {
        try
        {
            uint bgr = GetSysColor(index);
            return 0xFF000000u | ((bgr & 0xFF) << 16) | (bgr & 0xFF00u) | ((bgr >> 16) & 0xFF);
        }
        catch (DllNotFoundException) { return 0xFF000000u; }
        catch (EntryPointNotFoundException) { return 0xFF000000u; }
    }

    /// <summary>True when a colour wants light text on it (Rec. 709 luma below mid-grey). The tray menu uses this rather
    /// than the app's light/dark setting, which says nothing about the palette a contrast theme supplies.</summary>
    public static bool IsDarkColour(uint argb)
        => (2126 * ((argb >> 16) & 0xFF) + 7152 * ((argb >> 8) & 0xFF) + 722 * (argb & 0xFF)) / 10000 < 128;

    /// <summary>True while Windows is in a contrast theme. Not cached, since it can change at any time; false if the
    /// call fails, so the app keeps its own palette.</summary>
    public static bool IsHighContrast()
    {
        var data = new HighContrast { Size = (uint)Marshal.SizeOf<HighContrast>() };
        try
        {
            if (!SystemParametersInfoW(SpiGetHighContrast, data.Size, ref data, 0)) return false;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        return (data.Flags & HcfHighContrastOn) != 0;
    }
}
