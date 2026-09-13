using ToneSnip.Core.Annotate;
using ToneSnip.Core.Color;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;

namespace ToneSnip.Core.Hdr;

/// <summary>
/// The SDR redaction filters, on linear half-float pixels, for the HDR file. Same grid anchoring, same hash, same
/// jitter, so both files show the same blocks; private means are clamped to the reference white so a redaction over a
/// highlight never glows.
/// </summary>
public static class HdrRedaction
{
    public static void Apply(RedactShape s, HalfImage img, IntRect viewport, float referenceWhiteNits)
    {
        IntRect r = s.Rect.Intersect(viewport).Offset(-viewport.Left, -viewport.Top);
        if (r.IsEmpty) return;
        (int, int) origin = (s.Rect.Left - viewport.Left, s.Rect.Top - viewport.Top);
        float white = HdrCanvas.ReferenceScale(referenceWhiteNits);
        if (s.Private)
        {
            int block = Style.PixelBlock(s.Strength);
            PrivateBlocks(img, r, block, s.Seed, origin, white);
            if (s.Blur) BoxBlur(img, r, Math.Max(Style.BlurRadius(s.Strength), block));
        }
        else if (s.Blur) BoxBlur(img, r, Style.BlurRadius(s.Strength));
        else Pixelate(img, r, Style.PixelBlock(s.Strength), origin);
    }

    public static (float R, float G, float B) Mean(HalfImage img, IntRect r)
    {
        double sr = 0, sg = 0, sb = 0; long n = 0;
        for (int y = r.Top; y < r.Bottom; y++) for (int x = r.Left; x < r.Right; x++)
        { int i = (y * img.Width + x) * 4; sr += Transfer.HalfToFloat(img.Data[i]); sg += Transfer.HalfToFloat(img.Data[i + 1]); sb += Transfer.HalfToFloat(img.Data[i + 2]); n++; }
        return n == 0 ? (0f, 0f, 0f) : ((float)(sr / n), (float)(sg / n), (float)(sb / n));
    }

    public static void Pixelate(HalfImage img, IntRect r, int block, (int OriginLeft, int OriginTop) origin)
    {
        // Means must come from the untouched pixels, so read them all before writing any block.
        var cells = new List<(IntRect Cell, float R, float G, float B)>();
        for (int by = Redaction.GridStart(r.Top, origin.OriginTop, block); by < r.Bottom; by += block)
            for (int bx = Redaction.GridStart(r.Left, origin.OriginLeft, block); bx < r.Right; bx += block)
            {
                var cell = IntRect.FromLtrb(Math.Max(bx, r.Left), Math.Max(by, r.Top), Math.Min(bx + block, r.Right), Math.Min(by + block, r.Bottom));
                if (cell.IsEmpty) continue;
                (float cr, float cg, float cb) = Mean(img, cell);
                cells.Add((cell, cr, cg, cb));
            }
        foreach ((IntRect cell, float cr, float cg, float cb) in cells) Fill(img, cell, cr, cg, cb);
    }

    /// <summary>Three-pass box blur of the colour channels inside <paramref name="r"/>, edges clamped inside the rect; alpha untouched.</summary>
    public static void BoxBlur(HalfImage img, IntRect r, int radius)
    {
        int w = r.Width, h = r.Height;
        var a = new float[w * h * 3]; var t = new float[w * h * 3];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        { int s = ((r.Top + y) * img.Width + r.Left + x) * 4, d = (y * w + x) * 3; a[d] = Transfer.HalfToFloat(img.Data[s]); a[d + 1] = Transfer.HalfToFloat(img.Data[s + 1]); a[d + 2] = Transfer.HalfToFloat(img.Data[s + 2]); }
        for (int pass = 0; pass < 3; pass++) { Pass(a, t, w, h, radius, true); Pass(t, a, w, h, radius, false); }
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        { int s = ((r.Top + y) * img.Width + r.Left + x) * 4, d = (y * w + x) * 3; img.Data[s] = Transfer.FloatToHalf(a[d]); img.Data[s + 1] = Transfer.FloatToHalf(a[d + 1]); img.Data[s + 2] = Transfer.FloatToHalf(a[d + 2]); }
    }

    private static void Pass(float[] src, float[] dst, int w, int h, int radius, bool horizontal)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h;
        Parallel.For(0, lines, line =>
        {
            int Idx(int i) => horizontal ? (line * w + i) * 3 : (i * w + line) * 3;
            for (int c = 0; c < 3; c++)
            {
                float sum = 0; int count = 0;
                for (int i = 0; i <= Math.Min(radius, len - 1); i++) { sum += src[Idx(i) + c]; count++; }
                for (int i = 0; i < len; i++)
                {
                    dst[Idx(i) + c] = sum / count;
                    int add = i + radius + 1, rem = i - radius;
                    if (add < len) { sum += src[Idx(add) + c]; count++; }
                    if (rem >= 0) { sum -= src[Idx(rem) + c]; count--; }
                }
            }
        });
    }

    /// <summary>Blocks of the (clamped) region mean with the SDR filter's seeded jitter: ±12 % value, ±6° hue. Reads nothing else.</summary>
    public static void PrivateBlocks(HalfImage img, IntRect r, int block, uint seed, (int OriginLeft, int OriginTop) origin, float clampWhite)
    {
        (float mr, float mg, float mb) = Mean(img, r);
        float peak = Math.Max(mr, Math.Max(mg, mb));
        if (peak > clampWhite) { float k = clampWhite / peak; mr *= k; mg *= k; mb *= k; }   // never brighter than SDR white
        (float hue, float sat, float val) = ToHsv(mr, mg, mb);
        for (int by = Redaction.GridStart(r.Top, origin.OriginTop, block); by < r.Bottom; by += block)
            for (int bx = Redaction.GridStart(r.Left, origin.OriginLeft, block); bx < r.Right; bx += block)
            {
                var cell = IntRect.FromLtrb(Math.Max(bx, r.Left), Math.Max(by, r.Top), Math.Min(bx + block, r.Right), Math.Min(by + block, r.Bottom));
                if (cell.IsEmpty) continue;
                uint hsh = Redaction.Hash(seed, (uint)((by - origin.OriginTop) / block), (uint)((bx - origin.OriginLeft) / block));
                float jv = ((hsh & 0xFFFF) / 65535f * 2 - 1) * 0.12f, jh = ((hsh >> 16) / 65535f * 2 - 1) * 6f;
                (float cr, float cg, float cb) = FromHsv((hue + jh + 360) % 360, sat, Math.Max(0f, val * (1 + jv)));
                Fill(img, cell, cr, cg, cb);
            }
    }

    // Alpha is forced opaque (half 1.0 = 0x3C00) to match the SDR Redaction.Fill. With clipToLasso off, a redaction
    // outside the lasso is opaque in the SDR file; leaving it transparent here would skew the gain map (no alpha).
    private static void Fill(HalfImage img, IntRect cell, float r, float g, float b)
    {
        ushort hr = Transfer.FloatToHalf(r), hg = Transfer.FloatToHalf(g), hb = Transfer.FloatToHalf(b);
        for (int y = cell.Top; y < cell.Bottom; y++) for (int x = cell.Left; x < cell.Right; x++) { int i = (y * img.Width + x) * 4; img.Data[i] = hr; img.Data[i + 1] = hg; img.Data[i + 2] = hb; img.Data[i + 3] = 0x3C00; }
    }

    // HSV on unbounded linear values: V is the max channel (may exceed 1), hue and saturation are ratios, so the
    // jitter is the same shape as the SDR filter's regardless of brightness.
    private static (float H, float S, float V) ToHsv(float r, float g, float b)
    {
        float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        float h = d <= 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return ((h + 360) % 360, max <= 0 ? 0 : d / max, max);
    }

    private static (float R, float G, float B) FromHsv(float h, float s, float v)
    {
        float c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (float r, float g, float b) = h switch { < 60 => (c, x, 0f), < 120 => (x, c, 0f), < 180 => (0f, c, x), < 240 => (0f, x, c), < 300 => (x, 0f, c), _ => (c, 0f, x) };
        return (r + m, g + m, b + m);
    }
}
