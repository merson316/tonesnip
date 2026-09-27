using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using ToneSnip.App.Capture;
using ToneSnip.App.Interop;
using ToneSnip.App.Theme;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using ToneSnip.Windows;
using ToneSnip.Windows.Imaging;
using ToneSnip.Windows.Interop;
using ToneSnip.Windows.Tray;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.UI;

namespace ToneSnip.App;

/// <summary>The tray glyph and the tray menu, rendered to bitmaps since neither is a XAML window.</summary>
internal static partial class Screenshots
{
    /// <summary>The tray glyph at 16, 20 and 24 px (100 %, 125 %, 150 %) in each colour setting, read back from the
    /// HICON itself (<see cref="RenderIcon"/>) so the PNG is what the shell composites.
    /// <para>The high-contrast variant is drawn on the COLOR_WINDOW ground it is paired with, using
    /// <see cref="TrayGlyph.GlyphArgb"/> with high contrast forced on.</para></summary>
    private static void CaptureTrayGlyphs(string dir)
    {
        uint accent = ThemeManager.SystemAccentArgb;
        foreach ((string name, uint argb, uint ground) in new[]
        {
            ("tray-mono-dark", 0xFFFFFFFFu, 0u),
            ("tray-mono-light", 0xFF000000u, 0u),
            ("tray-accent", accent, 0u),
            ("tray-highcontrast", TrayGlyph.GlyphArgb("accent", "auto", accent, highContrast: true), SystemTheme.SysColorArgb(SystemTheme.ColorWindow)),
        })
            foreach (int size in new[] { 16, 20, 24 })
            {
                BgraImage? icon = RenderIcon(size, argb);
                if (icon == null) { App.Current.Log.Warn($"screenshots: {name}-{size}: the HICON could not be read back"); _failures++; continue; }
                BgraImage img = OnGround(icon, ground);
                string file = $"{name}-{size}.png";
                File.WriteAllBytes(Path.Combine(dir, file), Bitmaps.EncodePng(img));
                Index.Add(new Shot(file, "tray", $"{name["tray-".Length..]}-{size}", "n/a", "Screenshots.RenderIcon", img.Width, img.Height, 1.0));
                App.Current.Log.Info($"screenshots: {file} {img.Width}x{img.Height} read back from the HICON, glyph {argb:X8} on {ground:X8}");
            }
    }

    /// <summary>The tray glyph as the shell sees it, created through <see cref="TrayGlyph.CreateIcon"/> and read back
    /// out of the icon's colour bitmap, un-premultiplied. Null when it could not be created or read. Here rather than in
    /// TrayGlyph so the shipped ToneSnip.Windows carries none of it; the two calls it needs beyond TrayGlyph's are
    /// internal to that assembly and visible to the debug build only.</summary>
    private static BgraImage? RenderIcon(int size, uint argb)
    {
        size = Math.Clamp(size, 8, 256);
        IntPtr icon = TrayGlyph.CreateIcon(size, argb);
        if (icon == IntPtr.Zero) return null;
        try
        {
            if (!User32.GetIconInfo(icon, out User32.IconInfo info)) return null;
            try
            {
                // GetDIBits wants a BITMAPINFO; for a 32-bit BI_RGB read the header alone is the whole structure.
                var header = new Gdi32.BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<Gdi32.BitmapInfoHeader>(),
                    Width = size, Height = -size,   // top-down, the order BgraImage keeps its rows in
                    Planes = 1, BitCount = 32,
                };
                byte[] bits = new byte[size * size * 4];
                IntPtr dc = User32.GetDC(IntPtr.Zero);
                int lines;
                try { lines = Gdi32.GetDIBits(dc, info.Color, 0, (uint)size, bits, ref header, 0 /*DIB_RGB_COLORS*/); }
                finally { _ = User32.ReleaseDC(IntPtr.Zero, dc); }
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
                if (info.Color != IntPtr.Zero) Gdi32.DeleteObject(info.Color);
                if (info.Mask != IntPtr.Zero) Gdi32.DeleteObject(info.Mask);
            }
        }
        finally { User32.DestroyIcon(icon); }
    }

    /// <summary>A transparent glyph composited over one opaque colour; the image itself when there is no ground (0).</summary>
    private static BgraImage OnGround(BgraImage img, uint groundArgb)
    {
        if (groundArgb == 0) return img;
        byte gb = (byte)groundArgb, gg = (byte)(groundArgb >> 8), gr = (byte)(groundArgb >> 16);
        for (int i = 0; i < img.Data.Length; i += 4)
        {
            int a = img.Data[i + 3];
            img.Data[i] = (byte)((img.Data[i] * a + gb * (255 - a)) / 255);
            img.Data[i + 1] = (byte)((img.Data[i + 1] * a + gg * (255 - a)) / 255);
            img.Data[i + 2] = (byte)((img.Data[i + 2] * a + gr * (255 - a)) / 255);
            img.Data[i + 3] = 255;
        }
        return img;
    }

    /// <summary>
    /// The tray context menu. It exists only inside <c>TrackPopupMenuEx</c>'s modal loop, so it cannot be captured;
    /// instead <see cref="TrayMenu.DrawSheet"/> renders the rows with the real menu's palette, measurements and text
    /// path (without the system border, shadow and corners).
    /// <para>Dark, light and high-contrast sheets. The entries cover every row role (header, plain, checked,
    /// accelerator, separator) and row 3 is drawn hovered.</para></summary>
    private static void CaptureTrayMenu(string dir)
    {
        using var window = new MessageWindow(App.Current.Log);
        var menu = new TrayMenu(window, App.Current.Log);
        menu.AddHeader("New snip");
        menu.Add("Rectangle", () => { }, accelerator: () => "Ctrl+Shift+S");
        menu.Add("Window", () => { });
        menu.AddSeparator();
        menu.AddHeader("Delay");
        menu.Add("None", () => { }, () => true);
        menu.Add("3 seconds", () => { });
        menu.AddSeparator();
        menu.Add("Settings…", () => { });
        menu.Add("Quit ToneSnip", () => { });

        foreach ((string name, bool dark, bool highContrast) in new[]
        {
            ("tray-menu-dark", true, false),
            ("tray-menu-light", false, false),
            ("tray-menu-highcontrast", true, true),
        })
        {
            menu.IsDark = () => dark;
            // Larger than the menu can measure to; cropped to what DrawSheet reports it filled.
            BgraImage? sheet = DrawMenuSheet(menu, 480, 640, highContrast);
            if (sheet == null) { _failures++; App.Current.Log.Error($"screenshots: {name} could not be drawn"); continue; }
            string file = $"{name}.png";
            File.WriteAllBytes(Path.Combine(dir, file), Bitmaps.EncodePng(sheet));
            Index.Add(new Shot(file, "tray", name["tray-".Length..], highContrast ? "highcontrast" : dark ? "dark" : "light", "TrayMenu.DrawSheet", sheet.Width, sheet.Height, 1.0));
            App.Current.Log.Info($"screenshots: {file} {sheet.Width}x{sheet.Height} via TrayMenu.DrawSheet");
        }
    }

    /// <summary>A top-down 32-bpp DIB for <see cref="TrayMenu.DrawSheet"/> to fill, cropped to what it filled.</summary>
    private static BgraImage? DrawMenuSheet(TrayMenu menu, int width, int height, bool highContrast)
    {
        IntPtr screen = User32.GetDC(IntPtr.Zero);
        IntPtr dc = Gdi32.CreateCompatibleDC(screen);
        IntPtr dib = IntPtr.Zero, old = IntPtr.Zero;
        BgraImage? img = null;
        // GDI handles are released in the finally so no path can leak them.
        try
        {
            var header = new Gdi32.BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<Gdi32.BitmapInfoHeader>(),
                Width = width, Height = -height,   // negative: top-down, so the rows line up with BgraImage's
                Planes = 1, BitCount = 32,
            };
            dib = Gdi32.CreateDIBSection(dc, ref header, 0 /*DIB_RGB_COLORS*/, out IntPtr bits, IntPtr.Zero, 0);
            if (dib != IntPtr.Zero)
            {
                old = Gdi32.SelectObject(dc, dib);
                (int w, int h) = menu.DrawSheet(dc, 96, highContrast, hoveredId: 3);
                Gdi32.GdiFlush();
                var full = BgraImage.Blank(width, height);
                Marshal.Copy(bits, full.Data, 0, full.Data.Length);
                // GDI leaves alpha at zero; the sheet is opaque, so set it.
                for (int i = 3; i < full.Data.Length; i += 4) full.Data[i] = 255;
                img = full.Crop(new IntRect(0, 0, Math.Min(w, width), Math.Min(h, height)));
            }
        }
        catch (Exception ex) { App.Current.Log.Error("screenshots: tray menu sheet: " + ex); }
        finally
        {
            if (old != IntPtr.Zero) Gdi32.SelectObject(dc, old);
            if (dib != IntPtr.Zero) Gdi32.DeleteObject(dib);
            Gdi32.DeleteDC(dc);
            _ = User32.ReleaseDC(IntPtr.Zero, screen);
        }
        return img;
    }
}
