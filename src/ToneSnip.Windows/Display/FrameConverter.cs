using ToneSnip.Core.Color;
using ToneSnip.Core.Imaging;
using Vortice.DXGI;

namespace ToneSnip.Windows.Display;

public static unsafe class FrameConverter
{
    /// <summary>
    /// Copies an HDR frame as RGBA halves in desktop orientation. R16G16B16A16_FLOAT rows are a straight copy;
    /// R10G10B10A2 (HDR10 swap chain) is decoded to scRGB and rounded to half.
    /// </summary>
    public static HalfImage ToHalf(IntPtr data, int rowPitch, int width, int height, Format format, ModeRotation rotation)
        => Rotate(ToHalfInto(data, rowPitch, width, height, format, new HalfImage(width, height)), rotation);

    /// <summary>The same decode into a caller-owned, unrotated buffer, so the grab can write straight into a pooled
    /// frame. Rotation is the caller's job: it needs a second buffer, and only the caller knows where that comes from.</summary>
    public static HalfImage ToHalfInto(IntPtr data, int rowPitch, int width, int height, Format format, HalfImage img)
    {
        if (img.Width != width || img.Height != height) throw new ArgumentException($"target {img.Width}x{img.Height} is not {width}x{height}", nameof(img));
        ushort[] dst = img.Data;
        byte* basePtr = (byte*)data;
        if (format == Format.R16G16B16A16_Float)
        {
            fixed (ushort* d = dst)
                for (int y = 0; y < height; y++) Buffer.MemoryCopy(basePtr + (long)y * rowPitch, d + (long)y * width * 4, (long)width * 8, (long)width * 8);
        }
        else if (format == Format.R10G10B10A2_UNorm)
        {
            Parallel.For(0, height, y =>
            {
                uint* p = (uint*)(basePtr + (long)y * rowPitch);
                int di = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    uint v = p[x];
                    float r = Transfer.PqDecode((v & 0x3FF) / 1023f), g = Transfer.PqDecode(((v >> 10) & 0x3FF) / 1023f), b = Transfer.PqDecode(((v >> 20) & 0x3FF) / 1023f);
                    Transfer.Bt2020To709(ref r, ref g, ref b);
                    dst[di + x * 4] = BitConverter.HalfToUInt16Bits((Half)(r / 80f));
                    dst[di + x * 4 + 1] = BitConverter.HalfToUInt16Bits((Half)(g / 80f));
                    dst[di + x * 4 + 2] = BitConverter.HalfToUInt16Bits((Half)(b / 80f));
                    dst[di + x * 4 + 3] = 0x3C00;
                }
            });
        }
        else throw new ArgumentException($"{format} is not an HDR frame format; use ToBgra8", nameof(format));
        return img;
    }

    /// <summary>Copies an SDR (B8G8R8A8) frame as BGRA8 with opaque alpha, in desktop orientation. No colour math.</summary>
    public static BgraImage ToBgra8(IntPtr data, int rowPitch, int width, int height, ModeRotation rotation)
        => Rotate(ToBgra8Into(data, rowPitch, width, height, new BgraImage(width, height, new byte[width * height * 4])), rotation);

    /// <summary>The same copy into a caller-owned, unrotated buffer; see <see cref="ToHalfInto"/> on rotation.</summary>
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

    private static HalfImage Rotate(HalfImage img, ModeRotation r) => r switch { ModeRotation.Rotate90 => img.RotateClockwise(1), ModeRotation.Rotate180 => img.RotateClockwise(2), ModeRotation.Rotate270 => img.RotateClockwise(3), _ => img };
    private static BgraImage Rotate(BgraImage img, ModeRotation r) => r switch { ModeRotation.Rotate90 => img.RotateClockwise(1), ModeRotation.Rotate180 => img.RotateClockwise(2), ModeRotation.Rotate270 => img.RotateClockwise(3), _ => img };
}
