using System.Buffers;
using ToneSnip.Core.Annotate;
using ToneSnip.Core.Color;
using ToneSnip.Core.Geometry;

namespace ToneSnip.Core.Imaging;

/// <summary>
/// The pixel passes of the emphasis shapes, on the SDR BGRA picture and on the HDR file's linear half-float canvas
/// alike. The spotlight dims every pixel outside the union of a document's <see cref="SpotlightShape"/>s by one factor
/// in linear light, so both files darken the same surroundings by the same number of stops. A
/// <see cref="MagnifierShape"/>'s lens is a nearest-pixel copy of its source, so both files hold the same pixels.
/// <para>Order, in both renders: redactions, then the lenses are read (so a lens shows redacted pixels and never the
/// ones under them), then the dim, then the lenses are written (a callout stays bright in the dark), then the drawn
/// shapes.</para>
/// </summary>
public static class Emphasis
{
    /// <summary>What a dimmed pixel keeps of its linear light: about 2.3 stops down, which takes an sRGB mid grey to
    /// roughly the crop marquee's dim (half its encoded value) and leaves the surroundings readable.</summary>
    public const float SpotlightKeep = 0.2f;

    /// <summary>sRGB byte to the byte <see cref="SpotlightKeep"/> of its linear light encodes to, so the SDR dim is exact
    /// in linear light and matches the HDR canvas's multiply.</summary>
    private static readonly byte[] DimTable = BuildDim();

    private static byte[] BuildDim()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++) t[i] = (byte)Math.Round(Transfer.SrgbEncode(Transfer.SrgbDecode(i / 255f) * SpotlightKeep) * 255f);
        return t;
    }

    /// <summary>The byte a dimmed sRGB channel value becomes.</summary>
    public static byte Dim(byte v) => DimTable[v];

    /// <summary>Below this many pixels the dim runs on the calling thread.</summary>
    private const int ParallelPixels = 64 * 1024;

    /// <summary>Dims the pixels of <paramref name="area"/> (image-local, 0,0 at the viewport's top-left) that no spotlight
    /// covers. Nothing happens when the document has no spotlight. Alpha is untouched.</summary>
    public static void Spotlight(IReadOnlyList<Shape> shapes, BgraImage img, IntRect viewport, IntRect area)
    {
        byte[] d = img.Data;
        ForEachDimmedRun(shapes, img.Width, img.Height, viewport, area, (y, x0, x1) =>
        {
            for (int i = (y * img.Width + x0) * 4, end = (y * img.Width + x1) * 4; i < end; i += 4)
            {
                d[i] = DimTable[d[i]]; d[i + 1] = DimTable[d[i + 1]]; d[i + 2] = DimTable[d[i + 2]];
            }
        });
    }

    /// <summary><see cref="Spotlight(IReadOnlyList{Shape}, BgraImage, IntRect, IntRect)"/> on a linear canvas: the colour
    /// channels are multiplied by <see cref="SpotlightKeep"/>.</summary>
    public static void Spotlight(IReadOnlyList<Shape> shapes, HalfImage img, IntRect viewport)
    {
        ushort[] d = img.Data;
        ForEachDimmedRun(shapes, img.Width, img.Height, viewport, new IntRect(0, 0, img.Width, img.Height), (y, x0, x1) =>
        {
            for (int i = (y * img.Width + x0) * 4, end = (y * img.Width + x1) * 4; i < end; i += 4)
                for (int c = 0; c < 3; c++) d[i + c] = Transfer.FloatToHalf(Transfer.HalfToFloat(d[i + c]) * SpotlightKeep);
        });
    }

    /// <summary>
    /// Calls <paramref name="run"/> with (row, first x, end x) for every horizontal run of <paramref name="area"/> that
    /// lies outside all the spotlights, in image-local pixels. A large area goes row by row in parallel: a full repaint
    /// dims most of a monitor.
    /// </summary>
    private static void ForEachDimmedRun(IReadOnlyList<Shape> shapes, int width, int height, IntRect viewport, IntRect area, Action<int, int, int> run)
    {
        area = area.Intersect(new IntRect(0, 0, width, height));
        if (area.IsEmpty) return;
        var lit = new List<IntRect>();
        foreach (Shape s in shapes) if (s is SpotlightShape sp) lit.Add(sp.Rect.Offset(-viewport.Left, -viewport.Top));
        if (lit.Count == 0) return;
        IntRect[] holes = lit.ToArray();
        // A pen stroke's dirty rectangle is a few thousand pixels, less than the cost of fanning out to the pool.
        if ((long)area.Width * area.Height < ParallelPixels)
        {
            var spans = new List<(int L, int R)>(holes.Length);
            for (int y = area.Top; y < area.Bottom; y++) Row(y, spans);
            return;
        }
        Parallel.For(area.Top, area.Bottom, () => new List<(int L, int R)>(holes.Length), (y, _, spans) => { Row(y, spans); return spans; }, _ => { });

        void Row(int y, List<(int L, int R)> spans)
        {
            spans.Clear();
            foreach (IntRect h in holes)
                if (y >= h.Top && y < h.Bottom && h.Right > area.Left && h.Left < area.Right) spans.Add((Math.Max(h.Left, area.Left), Math.Min(h.Right, area.Right)));
            spans.Sort();
            int x = area.Left;
            foreach ((int l, int r) in spans)
            {
                if (l > x) run(y, x, l);
                x = Math.Max(x, r);
            }
            if (x < area.Right) run(y, x, area.Right);
        }
    }

    /// <summary>
    /// One lens to write: the part <see cref="Dest"/> (source-frame pixels) of <see cref="Shape"/>'s lens, and its
    /// source's pixels <see cref="From"/>, taken before the dim. <see cref="Source"/> is rented from the shared pool
    /// (a repaint during a drag would otherwise allocate a large array per move and set off full collections) and goes
    /// back when the lens is written.
    /// </summary>
    public sealed record LensTile<T>(MagnifierShape Shape, IntRect Dest, IntRect From, T[] Source, IntRect Viewport);

    /// <summary>
    /// <see cref="ReadLenses{T}(IReadOnlyList{Shape}, Func{IntRect, T[]}, IntRect, IntRect)"/> from an image that holds
    /// the whole viewport already redacted (the HDR canvas): four channels a pixel, <paramref name="width"/> wide.
    /// </summary>
    public static List<LensTile<T>>? ReadLenses<T>(IReadOnlyList<Shape> shapes, T[] data, int width, IntRect viewport, IntRect area)
        => ReadLenses(shapes, r => Crop(data, width, r.Offset(-viewport.Left, -viewport.Top)), viewport, area);

    /// <summary>
    /// Takes, for every lens that reaches <paramref name="area"/> (source-frame pixels), its source's pixels.
    /// <paramref name="source"/> gives the redacted, undimmed pixels of a rectangle of the viewport (rows of its width,
    /// four channels a pixel, in an array from <see cref="Crop"/>'s pool), so a lens never depends on what else was
    /// painted where its source is: a repaint of part of a lens needs nothing but that part. Null when no lens reaches
    /// the area.
    /// </summary>
    public static List<LensTile<T>>? ReadLenses<T>(IReadOnlyList<Shape> shapes, Func<IntRect, T[]> source, IntRect viewport, IntRect area)
    {
        List<LensTile<T>>? tiles = null;
        foreach (Shape s in shapes)
        {
            if (s is not MagnifierShape m) continue;
            IntRect dest = m.Lens.Intersect(viewport).Intersect(area), from = m.Source.Intersect(viewport);
            if (dest.IsEmpty || from.IsEmpty) continue;
            (tiles ??= new()).Add(new LensTile<T>(m, dest, from, source(from), viewport));
        }
        return tiles;
    }

    /// <summary>The pixels of <paramref name="r"/> (image-local) as rows of its own width, in an array rented from the
    /// shared pool (it may be longer); <see cref="WriteLenses"/> returns it.</summary>
    public static T[] Crop<T>(T[] data, int width, IntRect r)
    {
        T[] rows = ArrayPool<T>.Shared.Rent(r.Width * r.Height * 4);
        for (int y = 0; y < r.Height; y++) Array.Copy(data, ((r.Top + y) * width + r.Left) * 4, rows, y * r.Width * 4, r.Width * 4);
        return rows;
    }

    /// <summary>
    /// Writes the lenses read by <see cref="ReadLenses{T}(IReadOnlyList{Shape}, Func{IntRect, T[]}, IntRect, IntRect)"/>
    /// into the image (the viewport, <paramref name="width"/> wide), in document order, and returns their source arrays
    /// to the pool. Each lens pixel takes the nearest source pixel inside the lens's rounded corners; a lens shows only
    /// pixels its viewport holds, and the rest of it is left as it is.
    /// </summary>
    public static void WriteLenses<T>(List<LensTile<T>>? tiles, T[] data, int width)
    {
        if (tiles == null) return;
        foreach (LensTile<T> t in tiles)
        {
            MagnifierShape m = t.Shape;
            IntRect lens = m.Lens, dest = t.Dest, from = t.From, vp = t.Viewport;
            float radius = m.LensRadius;
            // The source column of each destination column, in whole-number arithmetic so both renders pick the same one.
            int[] columns = ArrayPool<int>.Shared.Rent(dest.Width);
            try
            {
                for (int x = dest.Left; x < dest.Right; x++) columns[x - dest.Left] = m.Source.Left + (int)((long)(x - lens.Left) * m.Source.Width / lens.Width);
                Span<T> src = t.Source.AsSpan(), dst = data.AsSpan();
                for (int y = dest.Top; y < dest.Bottom; y++)
                {
                    int sy = m.Source.Top + (int)((long)(y - lens.Top) * m.Source.Height / lens.Height);
                    if (sy < from.Top || sy >= from.Bottom) continue;
                    // Only the rows the corners cut need the per-pixel test.
                    bool corner = y + 0.5f < lens.Top + radius || y + 0.5f > lens.Bottom - radius;
                    int srow = (sy - from.Top) * from.Width - from.Left, drow = (y - vp.Top) * width - vp.Left;
                    for (int x = dest.Left; x < dest.Right; x++)
                    {
                        int sx = columns[x - dest.Left];
                        if (sx < from.Left || sx >= from.Right || (corner && !InRounded(lens, radius, x, y))) continue;
                        src.Slice((srow + sx) * 4, 4).CopyTo(dst.Slice((drow + x) * 4, 4));
                    }
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(columns);
                ArrayPool<T>.Shared.Return(t.Source);
            }
        }
    }

    /// <summary>Whether the middle of pixel (<paramref name="x"/>, <paramref name="y"/>) is inside
    /// <paramref name="r"/> with corners rounded to <paramref name="radius"/>.</summary>
    public static bool InRounded(IntRect r, float radius, int x, int y)
    {
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2f);
        float px = x + 0.5f, py = y + 0.5f;
        if (px < r.Left || py < r.Top || px > r.Right || py > r.Bottom) return false;
        float cx = Math.Clamp(px, r.Left + radius, r.Right - radius), cy = Math.Clamp(py, r.Top + radius, r.Bottom - radius);
        float dx = px - cx, dy = py - cy;
        return dx * dx + dy * dy <= radius * radius;
    }
}
