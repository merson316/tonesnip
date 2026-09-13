using System.Runtime.InteropServices;
using ToneSnip.Core.Imaging;
using ToneSnip.Windows.Imaging;
using Microsoft.Win32;

namespace ToneSnip.Windows.Tray;

/// <summary>The notification-area glyph, drawn at run time: the app mark's two overlapping frames in one flat colour on
/// a transparent surface. An .ico carries one palette, so the icon is rasterised per colour and size instead.</summary>
/// <remarks>Drawn with GDI+ into a premultiplied DIB that becomes an HICON through <c>CreateIconIndirect</c> with an
/// all-zero mask, so alpha does the shaping. <see cref="TrayIcon.SetIcon"/> destroys the one it replaces.</remarks>
public static class TrayGlyph
{
    private const int SmCxSmIcon = 49;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreateIconIndirect(ref IconInfo info);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint startLine, uint lines, byte[] bits, ref BitmapInfoHeader header, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;              // BOOL: an icon, not a cursor, so the hotspot fields are ignored
        public int HotspotX, HotspotY;
        public IntPtr Mask, Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    /// <summary>The two squares at one icon size, in whole pixels. 16, 20 and 24 (the taskbar at 100 %, 125 % and 150 %)
    /// are hand-laid rather than scaled so no edge lands between pixels; the outline is a filled box with its inside
    /// cleared, not a pen stroke, so straight edges stay crisp.</summary>
    /// <param name="Square">Side of each frame, pixels.</param>
    /// <param name="BackX">Left/top pixel of the outlined frame (it sits top-right).</param>
    /// <param name="FrontX">Left/top pixel of the filled frame (it sits bottom-left).</param>
    /// <param name="Stroke">Width of the outline in pixels, inside the frame's box.</param>
    /// <param name="Keyline">The gap punched around the filled frame so the outline stops at its edge, pixels.</param>
    private readonly record struct Layout(int Square, int BackX, int BackY, int FrontX, int FrontY, int Stroke, int Keyline);

    private static Layout LayoutFor(int size) => size switch
    {
        16 => new Layout(9, 6, 2, 2, 6, 1, 1),
        20 => new Layout(11, 7, 2, 2, 7, 1, 1),
        24 => new Layout(13, 9, 2, 2, 9, 2, 2),   // 2 px stroke: 1 px looks thin beside the 13 px fill
        _ => Scaled(size),
    };

    private static Layout Scaled(int size)
    {
        float k = size / 16f;
        int Px(float v) => Math.Max(1, (int)Math.Round(v * k));
        return new Layout(Px(9), Px(6), Px(2), Px(2), Px(6), Px(1), Px(1));
    }

    /// <summary>The notification area's icon size for the taskbar's DPI: 16 at 100 %, 20 at 125 %, 24 at 150 %.</summary>
    public static int TraySize()
    {
        int s = GetSystemMetrics(SmCxSmIcon);
        return s >= 8 && s <= 256 ? s : 16;
    }

    /// <summary>Monochrome follows the taskbar's light/dark setting (<c>SystemUsesLightTheme</c>, not
    /// <c>AppsUseLightTheme</c>) unless the app's theme is set explicitly.</summary>
    /// <param name="theme">The app's Theme setting: auto, light or dark.</param>
    /// <remarks>Private so callers go through <see cref="GlyphArgb"/> and its contrast-theme override.</remarks>
    private static uint MonoArgb(string theme) => theme switch
    {
        "dark" => 0xFFFFFFFF,
        "light" => 0xFF000000,
        _ => TaskbarUsesLightTheme() ? 0xFF000000u : 0xFFFFFFFFu,
    };

    /// <summary>The colour the glyph is drawn in, for the tray-icon setting the user chose.
    /// <para>A contrast theme overrides it with COLOR_WINDOWTEXT, the colour guaranteed to contrast with the
    /// notification area's COLOR_WINDOW. The setting itself is not changed.</para></summary>
    /// <param name="trayIcon">The app's TrayIcon setting: mono or accent (colour never reaches here).</param>
    /// <param name="theme">The app's Theme setting: auto, light or dark.</param>
    /// <param name="accentArgb">The Windows accent as the system reports it.</param>
    /// <param name="highContrast">Whether the desktop is in a contrast theme.</param>
    public static uint GlyphArgb(string trayIcon, string theme, uint accentArgb, bool highContrast)
        => highContrast ? SystemTheme.SysColorArgb(SystemTheme.ColorWindowText)
         : trayIcon == "accent" ? accentArgb
         : MonoArgb(theme);

    private static bool TaskbarUsesLightTheme()
    {
        try
        {
            using RegistryKey? k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch { return false; }   // the Windows 11 default is a dark taskbar
    }

#if TONESNIP_HARNESS
    /// <summary>Harness: the glyph as the shell sees it, created through <see cref="CreateIcon"/> and read back out of
    /// the icon's colour bitmap, un-premultiplied. Null when it could not be created or read.</summary>
    public static BgraImage? RenderIcon(int size, uint argb)
    {
        size = Math.Clamp(size, 8, 256);
        IntPtr icon = CreateIcon(size, argb);
        if (icon == IntPtr.Zero) return null;
        try
        {
            if (!GetIconInfo(icon, out IconInfo info)) return null;
            try
            {
                // GetDIBits wants a BITMAPINFO; for a 32-bit BI_RGB read the header alone is the whole structure.
                var header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = size, Height = -size,   // top-down, the order BgraImage keeps its rows in
                    Planes = 1, BitCount = 32,
                };
                byte[] bits = new byte[size * size * 4];
                IntPtr dc = GetDC(IntPtr.Zero);
                int lines;
                try { lines = GetDIBits(dc, info.Color, 0, (uint)size, bits, ref header, 0 /*DIB_RGB_COLORS*/); }
                finally { ReleaseDC(IntPtr.Zero, dc); }
                if (lines != size) return null;
                // The icon's colour bitmap is PARGB (CreateIcon draws it that way for the shell); BgraImage is straight.
                for (int i = 0; i < bits.Length; i += 4)
                {
                    int a = bits[i + 3];
                    if (a == 0 || a == 255) continue;
                    bits[i] = (byte)Math.Min(255, (bits[i] * 255 + a / 2) / a);
                    bits[i + 1] = (byte)Math.Min(255, (bits[i + 1] * 255 + a / 2) / a);
                    bits[i + 2] = (byte)Math.Min(255, (bits[i + 2] * 255 + a / 2) / a);
                }
                return new BgraImage(size, size, bits);
            }
            finally
            {
                // GetIconInfo hands out copies of both bitmaps, and they are the caller's to delete.
                if (info.Color != IntPtr.Zero) DeleteObject(info.Color);
                if (info.Mask != IntPtr.Zero) DeleteObject(info.Mask);
            }
        }
        finally { DestroyIcon(icon); }
    }
#endif

    /// <summary>The glyph as an HICON the caller owns and destroys; <see cref="IntPtr.Zero"/> when GDI could not create
    /// the bitmaps, so the caller can fall back to the .ico.</summary>
    public static IntPtr CreateIcon(int size, uint argb)
    {
        size = Math.Clamp(size, 8, 256);
        var header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = size, Height = -size,   // negative: top-down, which is how GDI+ draws into it
            Planes = 1, BitCount = 32,
        };
        IntPtr colour = CreateDIBSection(IntPtr.Zero, ref header, 0 /*DIB_RGB_COLORS*/, out IntPtr bits, IntPtr.Zero, 0);
        if (colour == IntPtr.Zero) return IntPtr.Zero;
        // An all-zero AND mask means "take every pixel from the colour bitmap"; the alpha channel does the shaping.
        IntPtr mask = CreateBitmap(size, size, 1, 1, new byte[((size + 31) / 32) * 4 * size]);
        IntPtr icon = IntPtr.Zero;
        try
        {
            // Premultiplied: the shell composites an icon's colour bitmap as PARGB, so straight alpha leaves a pale fringe.
            using (var bmp = new GdiPlus.Bitmap(size, size, size * 4, bits, premultiplied: true))
            using (GdiPlus.Graphics g = bmp.CreateGraphics())
                Draw(g, size, argb);
            if (mask == IntPtr.Zero) return IntPtr.Zero;
            var info = new IconInfo { IsIcon = 1, Mask = mask, Color = colour };
            icon = CreateIconIndirect(ref info);
        }
        finally
        {
            // CreateIconIndirect copies both bitmaps, so they go either way.
            DeleteObject(colour);
            if (mask != IntPtr.Zero) DeleteObject(mask);
        }
        return icon;
    }

    /// <summary>The two frames onto an already-transparent ARGB surface, four rectangle fills and no pen.</summary>
    private static void Draw(GdiPlus.Graphics g, int size, uint argb)
    {
        Layout l = LayoutFor(size);
        // PixelOffsetMode.Half puts pixel edges on integer coordinates, so whole-number rectangles cover whole pixels
        // and antialiasing leaves them untouched.
        g.Smoothing = GdiPlus.SmoothingMode.AntiAlias;
        g.PixelOffset = GdiPlus.PixelOffsetMode.Half;
        using GdiPlus.Brush fill = GdiPlus.Brush.Solid(argb);
        using GdiPlus.Brush clear = GdiPlus.Brush.Solid(0x00000000);

        // The outlined frame: its whole box, then its inside cleared. SourceCopy writes alpha rather than blending.
        g.FillRectangle(fill, l.BackX, l.BackY, l.Square, l.Square);
        g.Compositing = GdiPlus.CompositingMode.SourceCopy;
        g.FillRectangle(clear, l.BackX + l.Stroke, l.BackY + l.Stroke, l.Square - 2 * l.Stroke, l.Square - 2 * l.Stroke);
        // The filled frame punches a keyline through the outline, so the outline reads as stopping at its edge...
        g.FillRectangle(clear, l.FrontX - l.Keyline, l.FrontY - l.Keyline, l.Square + 2 * l.Keyline, l.Square + 2 * l.Keyline);
        g.Compositing = GdiPlus.CompositingMode.SourceOver;
        // ...and then sits in the gap.
        g.FillRectangle(fill, l.FrontX, l.FrontY, l.Square, l.Square);
    }
}
