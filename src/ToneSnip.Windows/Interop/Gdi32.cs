using System.Runtime.InteropServices;

namespace ToneSnip.Windows.Interop;

/// <summary>
/// The gdi32 imports shared across the app: memory DCs, DIB sections, pens, brushes, fonts and regions. Declared once
/// here rather than per file, and source-generated (<c>[LibraryImport]</c>) so the marshalling stubs are built at
/// compile time.
/// </summary>
public static partial class Gdi32
{
    private const string Dll = "gdi32.dll";

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)] public struct Size { public int Cx, Cy; }

    // ----- DCs and bitmaps -----
    [LibraryImport(Dll)] public static partial IntPtr CreateCompatibleDC(IntPtr dc);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteDC(IntPtr dc);
    [LibraryImport(Dll)] public static partial IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [LibraryImport(Dll)] public static partial IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);
    [LibraryImport(Dll)] public static partial int GetDIBits(IntPtr dc, IntPtr bitmap, uint startLine, uint lines, [Out] byte[] bits, ref BitmapInfoHeader header, uint usage);
    [LibraryImport(Dll)] public static partial IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteObject(IntPtr obj);
    [LibraryImport(Dll)] public static partial IntPtr GetStockObject(int index);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int srcX, int srcY, uint rop);
    /// <summary>Completes GDI's batched drawing before a DIB section's bytes are touched directly (and after).</summary>
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GdiFlush();

    // ----- pens, brushes, shapes and text -----
    [LibraryImport(Dll)] public static partial IntPtr CreatePen(int style, int width, uint color);
    [LibraryImport(Dll)] public static partial IntPtr CreateSolidBrush(uint color);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool Rectangle(IntPtr dc, int left, int top, int right, int bottom);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool RoundRect(IntPtr dc, int left, int top, int right, int bottom, int ellipseW, int ellipseH);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool Polyline(IntPtr dc, [In] User32.Point[] points, int count);
    [LibraryImport(Dll)] public static partial int SetBkMode(IntPtr dc, int mode);
    [LibraryImport(Dll)] public static partial uint SetTextColor(IntPtr dc, uint color);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetTextExtentPoint32W(IntPtr dc, string text, int length, out Size size);

    // ----- regions and clipping -----
    [LibraryImport(Dll)] public static partial int SelectClipRgn(IntPtr dc, IntPtr region);
    [LibraryImport(Dll)] public static partial IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    /// <summary>The region as an RGNDATA header plus RECTs; 0 when <paramref name="size"/> bytes are too few.</summary>
    [LibraryImport(Dll)] public static unsafe partial uint GetRegionData(IntPtr region, uint size, byte* data);
    [LibraryImport(Dll)] public static partial int IntersectClipRect(IntPtr dc, int left, int top, int right, int bottom);
}
