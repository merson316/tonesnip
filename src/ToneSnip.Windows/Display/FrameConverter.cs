using ToneSnip.Core.Imaging;

namespace ToneSnip.Windows.Display;

/// <summary>Copies a mapped Windows.Graphics.Capture frame into a caller-owned, pooled image, whole or a band of rows at
/// a time (<see cref="Capture.FrameRows"/>). The capture delivers frames already in desktop orientation, as
/// R16G16B16A16_FLOAT (scRGB) or B8G8R8A8, so both are straight row copies.</summary>
public static unsafe class FrameConverter
{
    /// <summary>Copies an HDR (R16G16B16A16_FLOAT) frame as RGBA halves into <paramref name="img"/>.</summary>
    public static HalfImage ToHalfInto(IntPtr data, int rowPitch, int width, int height, HalfImage img)
    {
        if (img.Width != width || img.Height != height) throw new ArgumentException($"target {img.Width}x{img.Height} is not {width}x{height}", nameof(img));
        HalfRowsInto(data, rowPitch, 0, height, img);
        return img;
    }

    /// <summary>Copies an SDR (B8G8R8A8) frame as BGRA8 with opaque alpha into <paramref name="img"/>. No colour math.</summary>
    public static BgraImage ToBgra8Into(IntPtr data, int rowPitch, int width, int height, BgraImage img)
    {
        if (img.Width != width || img.Height != height) throw new ArgumentException($"target {img.Width}x{img.Height} is not {width}x{height}", nameof(img));
        Bgra8RowsInto(data, rowPitch, 0, height, img);
        return img;
    }

    /// <summary>Copies <paramref name="rows"/> rows of an HDR frame, the first of them row <paramref name="top"/>, into
    /// the same rows of <paramref name="img"/>, which is the frame's width.</summary>
    public static void HalfRowsInto(IntPtr data, int rowPitch, int top, int rows, HalfImage img)
    {
        CheckRows(top, rows, img.Height);
        int width = img.Width;
        byte* basePtr = (byte*)data;
        fixed (ushort* d = img.Data)
            for (int r = 0; r < rows; r++) Buffer.MemoryCopy(basePtr + (long)r * rowPitch, d + (long)(top + r) * width * 4, (long)width * 8, (long)width * 8);
    }

    /// <summary>Copies <paramref name="rows"/> rows of an SDR frame, the first of them row <paramref name="top"/>, into
    /// the same rows of <paramref name="img"/>, which is the frame's width. With <paramref name="opaque"/> their alpha
    /// is set to 255; without it the frame's own alpha is kept (a window's rounded corners are read from it).</summary>
    public static void Bgra8RowsInto(IntPtr data, int rowPitch, int top, int rows, BgraImage img, bool opaque = true)
    {
        CheckRows(top, rows, img.Height);
        int width = img.Width;
        byte[] bytes = img.Data;
        byte* basePtr = (byte*)data;
        fixed (byte* d = bytes)
            for (int r = 0; r < rows; r++) Buffer.MemoryCopy(basePtr + (long)r * rowPitch, d + (long)(top + r) * width * 4, (long)width * 4, (long)width * 4);
        if (!opaque) return;
        int end = (top + rows) * width * 4;
        for (int i = top * width * 4 + 3; i < end; i += 4) bytes[i] = 255;
    }

    private static void CheckRows(int top, int rows, int height)
    {
        if (top < 0 || rows < 0 || top + rows > height) throw new ArgumentOutOfRangeException(nameof(rows), $"rows {top} to {top + rows} are not inside an image {height} rows tall");
    }
}
