using ToneSnip.Core.Imaging;

namespace ToneSnip.Windows.Display;

/// <summary>Copies a mapped Windows.Graphics.Capture frame into a caller-owned, pooled image. The capture delivers
/// frames already in desktop orientation, as R16G16B16A16_FLOAT (scRGB) or B8G8R8A8, so both are straight row copies.</summary>
public static unsafe class FrameConverter
{
    /// <summary>Copies an HDR (R16G16B16A16_FLOAT) frame as RGBA halves into <paramref name="img"/>.</summary>
    public static HalfImage ToHalfInto(IntPtr data, int rowPitch, int width, int height, HalfImage img)
    {
        if (img.Width != width || img.Height != height) throw new ArgumentException($"target {img.Width}x{img.Height} is not {width}x{height}", nameof(img));
        byte* basePtr = (byte*)data;
        fixed (ushort* d = img.Data)
            for (int y = 0; y < height; y++) Buffer.MemoryCopy(basePtr + (long)y * rowPitch, d + (long)y * width * 4, (long)width * 8, (long)width * 8);
        return img;
    }

    /// <summary>Copies an SDR (B8G8R8A8) frame as BGRA8 with opaque alpha into <paramref name="img"/>. No colour math.</summary>
    public static BgraImage ToBgra8Into(IntPtr data, int rowPitch, int width, int height, BgraImage img)
    {
        if (img.Width != width || img.Height != height) throw new ArgumentException($"target {img.Width}x{img.Height} is not {width}x{height}", nameof(img));
        byte[] bytes = img.Data;
        byte* basePtr = (byte*)data;
        fixed (byte* d = bytes)
            for (int y = 0; y < height; y++) Buffer.MemoryCopy(basePtr + (long)y * rowPitch, d + (long)y * width * 4, (long)width * 4, (long)width * 4);
        for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
        return img;
    }
}
