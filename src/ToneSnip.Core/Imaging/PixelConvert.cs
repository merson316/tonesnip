using ToneSnip.Core.Color;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Tonemap;

namespace ToneSnip.Core.Imaging;

/// <summary>Tonemap + sRGB encode of half-float frames into 8-bit BGRA pixels.</summary>
public static class PixelConvert
{
    /// <summary>Tonemaps into an existing BGRA buffer (no allocation). With neither a tonemapper nor a LUT, linear is
    /// clipped to [0,1] and encoded. Used by the grab and by live exposure.</summary>
    public static void ToBgra8Into(HalfImage img, ITonemapper? tm, AcesLut? lut, byte[] dst)
    {
        if (dst.Length != img.Width * img.Height * 4) throw new ArgumentException("buffer size", nameof(dst));
        FromHalf(img, new IntRect(0, 0, img.Width, img.Height), tm, lut, dst, img.Width, 0, 0);
    }

    /// <summary>Tonemaps the part of <paramref name="img"/> inside <paramref name="source"/> into
    /// <paramref name="dst"/> with its top-left at (<paramref name="dstX"/>, <paramref name="dstY"/>), leaving the rest of
    /// <paramref name="dst"/> as it was: a snip's HDR area goes straight into its composite, with no crop or
    /// intermediate image.</summary>
    public static void ToBgra8Into(HalfImage img, IntRect source, ITonemapper? tm, AcesLut? lut, BgraImage dst, int dstX, int dstY)
    {
        if (source.IsEmpty || source.Left < 0 || source.Top < 0 || source.Right > img.Width || source.Bottom > img.Height)
            throw new ArgumentOutOfRangeException(nameof(source), $"{source} is outside {img.Width}x{img.Height}");
        if (dstX < 0 || dstY < 0 || dstX + source.Width > dst.Width || dstY + source.Height > dst.Height)
            throw new ArgumentOutOfRangeException(nameof(dstX), $"{source.Width}x{source.Height} at ({dstX}, {dstY}) is outside {dst.Width}x{dst.Height}");
        FromHalf(img, source, tm, lut, dst.Data, dst.Width, dstX, dstY);
    }

    /// <summary>Half RGBA in, BGRA8 out. With a LUT it is three table reads per pixel; otherwise per-pixel float tonemapping without an intermediate image.</summary>
    private static void FromHalf(HalfImage img, IntRect source, ITonemapper? tm, AcesLut? lut, byte[] out8, int outWidth, int outX, int outY)
    {
        int w = source.Width;
        ushort[] src = img.Data;
        byte[]? t = lut?.Table;
        Parallel.For(0, source.Height, y =>
        {
            int s = ((source.Top + y) * img.Width + source.Left) * 4, o = ((outY + y) * outWidth + outX) * 4;
            for (int x = 0; x < w; x++, s += 4, o += 4)
            {
                if (t != null) { out8[o] = t[src[s + 2]]; out8[o + 1] = t[src[s + 1]]; out8[o + 2] = t[src[s]]; }
                else
                {
                    float r = Transfer.HalfToFloat(src[s]), g = Transfer.HalfToFloat(src[s + 1]), b = Transfer.HalfToFloat(src[s + 2]);
                    tm?.Map(ref r, ref g, ref b);
                    out8[o] = SrgbTable.Encode(b); out8[o + 1] = SrgbTable.Encode(g); out8[o + 2] = SrgbTable.Encode(r);
                }
                out8[o + 3] = 255;
            }
        });
    }
}
