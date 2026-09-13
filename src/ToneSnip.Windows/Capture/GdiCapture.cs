using System.Runtime.InteropServices;
using ToneSnip.Core.Geometry;

namespace ToneSnip.Windows.Capture;

/// <summary>BitBlt fallback for outputs Windows.Graphics.Capture cannot take (VMs, Remote Desktop). Returns top-down BGRA8, pitch = width*4.</summary>
public static class GdiCapture
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader { public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes; public ushort biBitCount; public uint biCompression; public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter; public uint biClrUsed; public uint biClrImportant; }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int x1, int y1, uint rop);
    private const uint SrcCopy = 0x00CC0020, CaptureBlt = 0x40000000;

    /// <summary>The same blit into a caller-owned buffer, so the grab can fill a pooled frame without allocating.</summary>
    public static void CaptureBgra8Into(IntRect bounds, byte[] data)
    {
        if (data.Length != bounds.Width * bounds.Height * 4) throw new ArgumentException("buffer size", nameof(data));
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        var bmi = new BitmapInfoHeader { biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(), biWidth = bounds.Width, biHeight = -bounds.Height, biPlanes = 1, biBitCount = 32 };
        IntPtr dib = CreateDIBSection(mem, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero) { DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen); throw new InvalidOperationException("CreateDIBSection failed"); }
        IntPtr old = SelectObject(mem, dib);
        try
        {
            if (!BitBlt(mem, 0, 0, bounds.Width, bounds.Height, screen, bounds.Left, bounds.Top, SrcCopy | CaptureBlt)) throw new InvalidOperationException("BitBlt failed");
            Marshal.Copy(bits, data, 0, data.Length);
            for (int i = 3; i < data.Length; i += 4) data[i] = 255;
        }
        finally { SelectObject(mem, old); DeleteObject(dib); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen); }
    }
}
