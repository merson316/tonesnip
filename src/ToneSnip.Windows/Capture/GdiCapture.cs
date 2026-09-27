using System.Runtime.InteropServices;
using ToneSnip.Core.Geometry;
using ToneSnip.Windows.Interop;

namespace ToneSnip.Windows.Capture;

/// <summary>BitBlt fallback for outputs Windows.Graphics.Capture cannot take (VMs, Remote Desktop), or that did not deliver
/// a frame in time. Returns top-down BGRA8, pitch = width*4.</summary>
public static class GdiCapture
{
    private const uint SrcCopy = 0x00CC0020, CaptureBlt = 0x40000000;

    /// <summary>The same blit into a caller-owned buffer, so the grab can fill a pooled frame without allocating.</summary>
    /// <param name="cursor">Draws the mouse pointer in, as Windows.Graphics.Capture does with its cursor setting: a
    /// blit of the screen never has it.</param>
    public static void CaptureBgra8Into(IntRect bounds, byte[] data, bool cursor = false)
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
            if (cursor) DrawCursor(mem, bounds);
            Gdi32.GdiFlush();   // the pointer is GDI drawing, and the bits are read directly below
            Marshal.Copy(bits, data, 0, data.Length);
            for (int i = 3; i < data.Length; i += 4) data[i] = 255;
        }
        finally { Gdi32.SelectObject(mem, old); Gdi32.DeleteObject(dib); Gdi32.DeleteDC(mem); User32.ReleaseDC(IntPtr.Zero, screen); }
    }

    private const uint CursorShowing = 0x1, DiNormal = 0x3;

    /// <summary>Draws the pointer, as it is now, at its place on the monitor <paramref name="bounds"/> covers: placed by
    /// its hotspot, clipped by the bitmap's edges when it is on another monitor. Nothing when it is hidden (a game, a
    /// touch session) or cannot be read.</summary>
    private static void DrawCursor(IntPtr dc, IntRect bounds)
    {
        var info = new User32.CursorInfo { Size = (uint)Marshal.SizeOf<User32.CursorInfo>() };
        if (!User32.GetCursorInfo(ref info) || (info.Flags & CursorShowing) == 0 || info.Cursor == IntPtr.Zero) return;
        int hotX = 0, hotY = 0;
        if (User32.GetIconInfo(info.Cursor, out User32.IconInfo icon))
        {
            hotX = icon.HotspotX; hotY = icon.HotspotY;
            // GetIconInfo hands over copies of the cursor's bitmaps, which are the caller's to delete.
            if (icon.Mask != IntPtr.Zero) Gdi32.DeleteObject(icon.Mask);
            if (icon.Color != IntPtr.Zero) Gdi32.DeleteObject(icon.Color);
        }
        User32.DrawIconEx(dc, info.ScreenPos.X - hotX - bounds.Left, info.ScreenPos.Y - hotY - bounds.Top, info.Cursor, 0, 0, 0, IntPtr.Zero, DiNormal);
    }
}
