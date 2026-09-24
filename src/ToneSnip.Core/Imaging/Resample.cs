namespace ToneSnip.Core.Imaging;

/// <summary>Bilinear resizing of a BGRA image; used to enlarge small selections before text recognition.</summary>
public static class Resample
{
    /// <summary>A copy of <paramref name="src"/> scaled by <paramref name="scale"/> (at least 1 × 1), sampled
    /// bilinearly at pixel centres. A scale of 1 returns the image itself, not a copy.</summary>
    public static BgraImage Scale(BgraImage src, double scale)
    {
        if (scale == 1) return src;
        if (!(scale > 0) || double.IsInfinity(scale)) throw new ArgumentOutOfRangeException(nameof(scale));
        int w = Math.Max(1, (int)Math.Round(src.Width * scale)), h = Math.Max(1, (int)Math.Round(src.Height * scale));
        var dst = BgraImage.Blank(w, h);
        byte[] s = src.Data, d = dst.Data;
        int sw = src.Width, sh = src.Height;
        double fx = (double)sw / w, fy = (double)sh / h;
        for (int y = 0; y < h; y++)
        {
            double sy = Math.Clamp((y + 0.5) * fy - 0.5, 0, sh - 1);
            int y0 = (int)sy, y1 = Math.Min(y0 + 1, sh - 1);
            double ty = sy - y0;
            for (int x = 0; x < w; x++)
            {
                double sx = Math.Clamp((x + 0.5) * fx - 0.5, 0, sw - 1);
                int x0 = (int)sx, x1 = Math.Min(x0 + 1, sw - 1);
                double tx = sx - x0;
                int a = (y0 * sw + x0) * 4, b = (y0 * sw + x1) * 4, c = (y1 * sw + x0) * 4, e = (y1 * sw + x1) * 4, o = (y * w + x) * 4;
                for (int k = 0; k < 4; k++)
                {
                    double top = s[a + k] + (s[b + k] - s[a + k]) * tx, bottom = s[c + k] + (s[e + k] - s[c + k]) * tx;
                    d[o + k] = (byte)Math.Round(top + (bottom - top) * ty);
                }
            }
        }
        return dst;
    }
}
