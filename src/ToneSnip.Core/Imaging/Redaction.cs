using ToneSnip.Core.Annotate;
using ToneSnip.Core.Geometry;

namespace ToneSnip.Core.Imaging;

/// <summary>
/// Blur and pixelate over BGRA buffers. Classic variants read the real pixels (recoverable by Depix-style attacks and
/// deconvolution); private variants read only one mean colour and a seed, so nothing under the rectangle survives.
/// </summary>
public static class Redaction
{
    public static void Apply(RedactShape s, BgraImage src, BgraImage dst, IntRect viewport)
    {
        IntRect r = s.Rect.Intersect(viewport).Offset(-viewport.Left, -viewport.Top);
        if (r.IsEmpty) return;
        // Anchor the block grid to the shape's top-left, not the clipped rectangle, so a redaction rendered one
        // viewport at a time (across monitors or a crop edge) has no half-block seam.
        (int OriginLeft, int OriginTop) origin = (s.Rect.Left - viewport.Left, s.Rect.Top - viewport.Top);
        if (s.Private)
        {
            // Both private variants start from the same generated block field; blur then softens it with a radius at
            // least the cell size so it reads as a cloud rather than a mosaic.
            int block = Style.PixelBlock(s.Strength);
            PrivateBlocks(src, dst, r, block, s.Seed, origin);
            if (s.Blur) BoxBlur(dst, dst, r, Math.Max(Style.BlurRadius(s.Strength), block));
        }
        else if (s.Blur) BoxBlur(src, dst, r, Style.BlurRadius(s.Strength));
        else Pixelate(src, dst, r, Style.PixelBlock(s.Strength), origin);
    }

    public static (byte B, byte G, byte R) Mean(BgraImage img, IntRect r)
    {
        long b = 0, g = 0, rr = 0, n = 0;
        for (int y = r.Top; y < r.Bottom; y++) for (int x = r.Left; x < r.Right; x++) { int i = (y * img.Width + x) * 4; b += img.Data[i]; g += img.Data[i + 1]; rr += img.Data[i + 2]; n++; }
        // Rounded to nearest rather than truncated, which would darken every cell.
        return n == 0 ? ((byte)0, (byte)0, (byte)0) : ((byte)((b + n / 2) / n), (byte)((g + n / 2) / n), (byte)((rr + n / 2) / n));
    }

    /// <param name="origin">Top-left the block grid is anchored to; defaults to <paramref name="r"/>'s own corner.
    /// Pass the unclipped shape corner so a rectangle split across viewports keeps one continuous grid.</param>
    public static void Pixelate(BgraImage src, BgraImage dst, IntRect r, int block, (int OriginLeft, int OriginTop)? origin = null)
    {
        (int ol, int ot) = origin ?? (r.Left, r.Top);
        for (int by = GridStart(r.Top, ot, block); by < r.Bottom; by += block)
            for (int bx = GridStart(r.Left, ol, block); bx < r.Right; bx += block)
            {
                var cell = IntRect.FromLtrb(Math.Max(bx, r.Left), Math.Max(by, r.Top), Math.Min(bx + block, r.Right), Math.Min(by + block, r.Bottom));
                if (cell.IsEmpty) continue;
                (byte b, byte g, byte rr) = Mean(src, cell);
                Fill(dst, cell, b, g, rr);
            }
    }

    /// <summary>First grid line at or before <paramref name="edge"/>, counting in <paramref name="block"/> steps from <paramref name="origin"/>.</summary>
    internal static int GridStart(int edge, int origin, int block)
    {
        int d = edge - origin;
        int cells = d >= 0 ? d / block : -((-d + block - 1) / block);
        return origin + cells * block;
    }

    // Per-thread blur scratch, reused because a redaction is re-blurred on every repaint of a drag. Buffers larger
    // than ScratchKeep are dropped after use so a full-screen blur does not stay pinned.
    private const int ScratchKeep = 4 << 20;   // 4 MB, i.e. about a 1024x1024 rectangle
    [ThreadStatic] private static byte[]? _scratchA;
    [ThreadStatic] private static byte[]? _scratchB;

    /// <summary>Three-pass box blur (close to Gaussian) confined to <paramref name="r"/>; edges clamp inside the rect. src and dst may be the same image.</summary>
    public static void BoxBlur(BgraImage src, BgraImage dst, IntRect r, int radius)
    {
        int w = r.Width, h = r.Height, need = w * h * 4;
        if (_scratchA == null || _scratchA.Length < need) _scratchA = new byte[need];
        if (_scratchB == null || _scratchB.Length < need) _scratchB = new byte[need];
        byte[] a = _scratchA, t = _scratchB;
        for (int y = 0; y < h; y++) Buffer.BlockCopy(src.Data, ((r.Top + y) * src.Width + r.Left) * 4, a, y * w * 4, w * 4);
        for (int pass = 0; pass < 3; pass++) { Pass(a, t, w, h, radius, horizontal: true); Pass(t, a, w, h, radius, horizontal: false); }
        for (int y = 0; y < h; y++) Buffer.BlockCopy(a, y * w * 4, dst.Data, ((r.Top + y) * dst.Width + r.Left) * 4, w * 4);
        if (a.Length > ScratchKeep) _scratchA = null;
        if (t.Length > ScratchKeep) _scratchB = null;
    }

    private static void Pass(byte[] src, byte[] dst, int w, int h, int radius, bool horizontal)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h;
        Parallel.For(0, lines, line =>
        {
            int Idx(int i) => horizontal ? (line * w + i) * 4 : (i * w + line) * 4;
            for (int c = 0; c < 4; c++)
            {
                int sum = 0, count = 0;
                for (int i = 0; i <= Math.Min(radius, len - 1); i++) { sum += src[Idx(i) + c]; count++; }
                for (int i = 0; i < len; i++)
                {
                    dst[Idx(i) + c] = (byte)(sum / count);
                    int add = i + radius + 1, rem = i - radius;
                    if (add < len) { sum += src[Idx(add) + c]; count++; }
                    if (rem >= 0) { sum -= src[Idx(rem) + c]; count--; }
                }
            }
        });
    }

    /// <summary>Blocks of the region's mean colour with seeded per-block jitter (±12 % value, ±6° hue). Reads nothing else from src.</summary>
    /// <param name="origin">Top-left the block grid and the jitter hash are anchored to; defaults to <paramref name="r"/>'s
    /// own corner. Pass the unclipped shape corner so a rectangle split across viewports keeps one continuous grid.</param>
    public static void PrivateBlocks(BgraImage src, BgraImage dst, IntRect r, int block, uint seed, (int OriginLeft, int OriginTop)? origin = null)
    {
        (byte mb, byte mg, byte mr) = Mean(src, r);
        (float hue, float sat, float val) = ToHsv(mr, mg, mb);
        (int ol, int ot) = origin ?? (r.Left, r.Top);
        for (int by = GridStart(r.Top, ot, block); by < r.Bottom; by += block)
            for (int bx = GridStart(r.Left, ol, block); bx < r.Right; bx += block)
            {
                uint hsh = Hash(seed, (uint)((by - ot) / block), (uint)((bx - ol) / block));
                float jv = ((hsh & 0xFFFF) / 65535f * 2 - 1) * 0.12f, jh = ((hsh >> 16) / 65535f * 2 - 1) * 6f;
                (byte rr, byte g, byte b) = FromHsv((hue + jh + 360) % 360, sat, Math.Clamp(val * (1 + jv), 0, 1));
                Fill(dst, IntRect.FromLtrb(Math.Max(bx, r.Left), Math.Max(by, r.Top), Math.Min(bx + block, r.Right), Math.Min(by + block, r.Bottom)), b, g, rr);
            }
    }

    private static void Fill(BgraImage dst, IntRect cell, byte b, byte g, byte r)
    {
        for (int y = cell.Top; y < cell.Bottom; y++) for (int x = cell.Left; x < cell.Right; x++) { int i = (y * dst.Width + x) * 4; dst.Data[i] = b; dst.Data[i + 1] = g; dst.Data[i + 2] = r; dst.Data[i + 3] = 255; }
    }

    internal static uint Hash(uint seed, uint x, uint y)
    {
        uint h = seed ^ (x * 0x9E3779B1u) ^ (y * 0x85EBCA77u);
        h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
        return h;
    }

    private static (float H, float S, float V) ToHsv(byte r8, byte g8, byte b8)
    {
        float r = r8 / 255f, g = g8 / 255f, b = b8 / 255f, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        float h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return ((h + 360) % 360, max == 0 ? 0 : d / max, max);
    }

    private static (byte R, byte G, byte B) FromHsv(float h, float s, float v)
    {
        float c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (float r, float g, float b) = h switch { < 60 => (c, x, 0f), < 120 => (x, c, 0f), < 180 => (0f, c, x), < 240 => (0f, x, c), < 300 => (x, 0f, c), _ => (c, 0f, x) };
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
