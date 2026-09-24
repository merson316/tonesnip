using System.Runtime.InteropServices;
using ToneSnip.Core.Geometry;
using ToneSnip.Windows.Interop;

namespace ToneSnip.Windows.Capture;

/// <summary>BitBlt fallback for outputs Windows.Graphics.Capture cannot take (VMs, Remote Desktop). Returns top-down BGRA8, pitch = width*4.</summary>
public static class GdiCapture
{
    private const uint SrcCopy = 0x00CC0020, CaptureBlt = 0x40000000;

    /// <summary>The same blit into a caller-owned buffer, so the grab can fill a pooled frame without allocating.</summary>
    public static void CaptureBgra8Into(IntRect bounds, byte[] data)
    {
        if (data.Length != bounds.Width * bounds.Height * 4) throw new ArgumentException("buffer size", nameof(data));
        IntPtr screen = User32.GetDC(IntPtr.Zero);
        IntPtr mem = Gdi32.CreateCompatibleDC(screen);
        var bmi = new Gdi32.BitmapInfoHeader { Size = (uint)Marshal.SizeOf<Gdi32.BitmapInfoHeader>(), Width = bounds.Width, Height = -bounds.Height, Planes = 1, BitCount = 32 };
        IntPtr dib = Gdi32.CreateDIBSection(mem, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero) { Gdi32.DeleteDC(mem); User32.ReleaseDC(IntPtr.Zero, screen); throw new InvalidOperationException("CreateDIBSection failed"); }
        IntPtr old = Gdi32.SelectObject(mem, dib);
        try
        {
            if (!Gdi32.BitBlt(mem, 0, 0, bounds.Width, bounds.Height, screen, bounds.Left, bounds.Top, SrcCopy | CaptureBlt)) throw new InvalidOperationException("BitBlt failed");
            Marshal.Copy(bits, data, 0, data.Length);
            for (int i = 3; i < data.Length; i += 4) data[i] = 255;
        }
        finally { Gdi32.SelectObject(mem, old); Gdi32.DeleteObject(dib); Gdi32.DeleteDC(mem); User32.ReleaseDC(IntPtr.Zero, screen); }
    }
}
