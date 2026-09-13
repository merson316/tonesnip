using ToneSnip.Core.Color;
using ToneSnip.Core.Tonemap;

namespace ToneSnip.Core.Imaging;

/// <summary>Tonemap + sRGB encode into 8-bit pixels. The *Rgba8 methods write R,G,B,A; the *Bgra8 methods write B,G,R,A.</summary>
public static class PixelConvert
{
    public static byte[] ToRgba8(FloatImage img, ITonemapper tm) => FromFloat(img, tm, 0, 2);
    public static byte[] ToRgba8(FloatImage img, AcesLut lut) => FromFloatLut(img, lut, 0, 2);
    public static byte[] ToRgba8Passthrough(FloatImage img) => FromFloatLut(img, null, 0, 2);

    public static BgraImage ToBgra8(FloatImage img, ITonemapper tm) => new(img.Width, img.Height, FromFloat(img, tm, 2, 0));
    public static BgraImage ToBgra8(HalfImage img, ITonemapper tm) => new(img.Width, img.Height, FromHalf(img, tm, null));
    public static BgraImage ToBgra8(HalfImage img, AcesLut lut) => new(img.Width, img.Height, FromHalf(img, null, lut));
    /// <summary>SDR output: clip linear to [0,1] and encode; no tonemapping.</summary>
    public static BgraImage ToBgra8Passthrough(HalfImage img) => new(img.Width, img.Height, FromHalf(img, null, null));

    /// <summary>Tonemaps into an existing BGRA buffer (no allocation); used by live exposure.</summary>
    public static void ToBgra8Into(HalfImage img, ITonemapper? tm, AcesLut? lut, byte[] dst)
    {
        if (dst.Length != img.Width * img.Height * 4) throw new ArgumentException("buffer size", nameof(dst));
        FromHalf(img, tm, lut, dst);
    }

    private static byte[] FromFloat(FloatImage img, ITonemapper tm, int ro, int bo)
    {
        int w = img.Width, h = img.Height;
        var out8 = new byte[w * h * 4];
        float[] src = img.Data;
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int s = (y * w + x) * 3, d = (y * w + x) * 4;
                float r = src[s], g = src[s + 1], b = src[s + 2];
                tm.Map(ref r, ref g, ref b);
                out8[d + ro] = SrgbTable.Encode(r); out8[d + 1] = SrgbTable.Encode(g); out8[d + bo] = SrgbTable.Encode(b); out8[d + 3] = 255;
            }
        });
        return out8;
    }

    private static byte[] FromFloatLut(FloatImage img, AcesLut? lut, int ro, int bo)
    {
        int w = img.Width, h = img.Height;
        var out8 = new byte[w * h * 4];
        float[] src = img.Data;
        byte[]? t = lut?.Table;
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int s = (y * w + x) * 3, d = (y * w + x) * 4;
                if (t != null)
                {
                    out8[d + ro] = t[BitConverter.HalfToUInt16Bits((Half)src[s])];
                    out8[d + 1] = t[BitConverter.HalfToUInt16Bits((Half)src[s + 1])];
                    out8[d + bo] = t[BitConverter.HalfToUInt16Bits((Half)src[s + 2])];
                }
                else { out8[d + ro] = SrgbTable.Encode(src[s]); out8[d + 1] = SrgbTable.Encode(src[s + 1]); out8[d + bo] = SrgbTable.Encode(src[s + 2]); }
                out8[d + 3] = 255;
            }
        });
        return out8;
    }

    /// <summary>Half RGBA in, BGRA8 out. With a LUT it is three table reads per pixel; otherwise per-pixel float tonemapping without an intermediate image.</summary>
    private static byte[] FromHalf(HalfImage img, ITonemapper? tm, AcesLut? lut, byte[]? into = null)
    {
        int w = img.Width, h = img.Height;
        var out8 = into ?? new byte[w * h * 4];
        ushort[] src = img.Data;
        byte[]? t = lut?.Table;
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (t != null) { out8[i] = t[src[i + 2]]; out8[i + 1] = t[src[i + 1]]; out8[i + 2] = t[src[i]]; }
                else
                {
                    float r = Transfer.HalfToFloat(src[i]), g = Transfer.HalfToFloat(src[i + 1]), b = Transfer.HalfToFloat(src[i + 2]);
                    tm?.Map(ref r, ref g, ref b);
                    out8[i] = SrgbTable.Encode(b); out8[i + 1] = SrgbTable.Encode(g); out8[i + 2] = SrgbTable.Encode(r);
                }
                out8[i + 3] = 255;
            }
        });
        return out8;
    }
}
