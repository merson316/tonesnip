using System.Runtime.InteropServices;
using ToneSnip.Core.Geometry;

namespace ToneSnip.Windows.Overlay;

/// <summary>
/// The overlay's GDI plumbing: a top-down 32-bpp DIB section as the double buffer, the row copy out of the frozen
/// frame's pinned BGRA buffer, the software dim, and the pens, brushes and font the paint path reuses.
/// <para>
/// The double buffer is a DIB section so the dim can be applied to its bytes in place (GDI has no alpha blend for
/// FillRect, and a translucent GDI+ fill per mouse move is expensive), and so dirty rows can be copied straight in.
/// </para>
/// </summary>
internal static class Gdi
{
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

    [StructLayout(LayoutKind.Sequential)] internal struct Size { public int Cx, Cy; }

    [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] internal static extern IntPtr GetStockObject(int index);
    [DllImport("gdi32.dll")] internal static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int srcX, int srcY, uint rop);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreatePen(int style, int width, uint color);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [DllImport("gdi32.dll")] internal static extern bool Rectangle(IntPtr dc, int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] internal static extern bool RoundRect(IntPtr dc, int left, int top, int right, int bottom, int ellipseW, int ellipseH);
    [DllImport("gdi32.dll")] internal static extern bool Polyline(IntPtr dc, [In] Point[] points, int count);
    [DllImport("gdi32.dll")] internal static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] internal static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetTextExtentPoint32W(IntPtr dc, string text, int length, out Size size);
    [DllImport("gdi32.dll")] internal static extern bool SelectClipRgn(IntPtr dc, IntPtr region);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    /// <summary>The region as an RGNDATA header plus RECTs; 0 when <paramref name="size"/> bytes are too few.</summary>
    [DllImport("gdi32.dll")] internal static extern unsafe uint GetRegionData(IntPtr region, uint size, byte* data);
    [DllImport("gdi32.dll")] internal static extern int IntersectClipRect(IntPtr dc, int left, int top, int right, int bottom);
    /// <summary>Completes GDI's batched drawing before the DIB section's bytes are touched directly (and after).</summary>
    [DllImport("gdi32.dll")] internal static extern bool GdiFlush();
    [DllImport("user32.dll")] internal static extern int FillRect(IntPtr dc, ref Win32.Rect rect, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int DrawTextW(IntPtr dc, string text, int length, ref Win32.Rect rect, uint format);

    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }

    internal const uint SrcCopy = 0x00CC0020;
    internal const int PsSolid = 0, NullBrush = 5, NullPen = 8, TransparentBk = 1, DefaultCharSet = 1, ClearTypeQuality = 5;
    internal const uint DtLeft = 0x0000, DtTop = 0x0000, DtSingleLine = 0x0020, DtNoPrefix = 0x0800;
    internal const int FwNormal = 400, FwSemibold = 600;

    /// <summary>A top-down 32-bpp BI_RGB DIB section and the pointer to its pixels; the stride is always width * 4.</summary>
    internal static IntPtr CreateDib(IntPtr referenceDc, int width, int height, out IntPtr bits)
    {
        var header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width, Height = -height,   // negative: top-down, so its rows line up with BgraImage's
            Planes = 1, BitCount = 32,
        };
        return CreateDIBSection(referenceDc, ref header, 0 /*DIB_RGB_COLORS*/, out bits, IntPtr.Zero, 0);
    }

    /// <summary>0xAARRGGBB to a GDI COLORREF (0x00BBGGRR).</summary>
    internal static uint Ref(uint argb) => ((argb & 0xFF) << 16) | (argb & 0xFF00) | ((argb >> 16) & 0xFF);

    /// <summary>Copies <paramref name="area"/>'s rows between two same-size top-down BGRA buffers. No allocation.</summary>
    internal static unsafe void CopyRows(byte* src, byte* dst, int widthPixels, IntRect area)
    {
        if (area.IsEmpty) return;
        uint bytes = (uint)area.Width * 4;
        for (int y = area.Top; y < area.Bottom; y++)
        {
            long offset = ((long)y * widthPixels + area.Left) * 4;
            Buffer.MemoryCopy(src + offset, dst + offset, bytes, bytes);
        }
    }

    /// <summary>
    /// Darkens <paramref name="area"/> in place by 40 % (a 0x66-alpha black fill), without touching alpha.
    /// </summary>
    internal static unsafe void Darken(byte* bits, int widthPixels, IntRect area) => Shade(bits, widthPixels, area, keep: 153);

    /// <summary>Scales <paramref name="area"/>'s colour channels in place to <paramref name="keep"/>/255 (0 is black),
    /// without touching alpha.</summary>
    internal static unsafe void Shade(byte* bits, int widthPixels, IntRect area, int keep)
    {
        if (area.IsEmpty) return;
        for (int y = area.Top; y < area.Bottom; y++)
        {
            byte* p = bits + ((long)y * widthPixels + area.Left) * 4;
            for (int x = 0; x < area.Width; x++, p += 4)
            {
                p[0] = (byte)((p[0] * keep + 127) / 255);
                p[1] = (byte)((p[1] * keep + 127) / 255);
                p[2] = (byte)((p[2] * keep + 127) / 255);
            }
        }
    }

    /// <summary>Lifts <paramref name="area"/> in place towards white by <paramref name="amount"/>/255 (255 is white), as
    /// a white fill of that alpha would, without touching alpha.</summary>
    internal static unsafe void Tint(byte* bits, int widthPixels, IntRect area, int amount)
    {
        if (area.IsEmpty) return;
        for (int y = area.Top; y < area.Bottom; y++)
        {
            byte* p = bits + ((long)y * widthPixels + area.Left) * 4;
            for (int x = 0; x < area.Width; x++, p += 4)
            {
                p[0] = (byte)(p[0] + ((255 - p[0]) * amount + 127) / 255);
                p[1] = (byte)(p[1] + ((255 - p[1]) * amount + 127) / 255);
                p[2] = (byte)(p[2] + ((255 - p[2]) * amount + 127) / 255);
            }
        }
    }

    /// <summary>
    /// <see cref="Tint"/> in dashes of <paramref name="dash"/> pixels on and off along a guide line. The pattern is
    /// counted from the buffer's own origin, so a moving line's dashes do not crawl.
    /// </summary>
    internal static unsafe void TintDashed(byte* bits, int widthPixels, IntRect area, int dash, bool horizontal, int amount)
    {
        if (area.IsEmpty || dash <= 0) return;
        for (int y = area.Top; y < area.Bottom; y++)
        {
            byte* p = bits + ((long)y * widthPixels + area.Left) * 4;
            for (int x = area.Left; x < area.Right; x++, p += 4)
            {
                if (((horizontal ? x : y) / dash & 1) != 0) continue;
                p[0] = (byte)(p[0] + ((255 - p[0]) * amount + 127) / 255);
                p[1] = (byte)(p[1] + ((255 - p[1]) * amount + 127) / 255);
                p[2] = (byte)(p[2] + ((255 - p[2]) * amount + 127) / 255);
            }
        }
    }

    /// <summary>Fills <paramref name="area"/> with one BGRA pixel (a magnified loupe cell).</summary>
    internal static unsafe void Fill(byte* bits, int widthPixels, IntRect area, uint bgra)
    {
        if (area.IsEmpty) return;
        for (int y = area.Top; y < area.Bottom; y++)
        {
            uint* p = (uint*)(bits + ((long)y * widthPixels + area.Left) * 4);
            for (int x = 0; x < area.Width; x++) p[x] = bgra;
        }
    }
}
