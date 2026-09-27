using ToneSnip.Core.Annotate;
using ToneSnip.Core.Color;
using ToneSnip.Core.Geometry;

namespace ToneSnip.Core.Imaging;

/// <summary>
/// The spotlight: every pixel outside the union of a document's <see cref="SpotlightShape"/>s is dimmed, on the SDR
/// BGRA picture and on the HDR file's linear half-float canvas alike. The dim is one factor in linear light, so both
/// files darken the same surroundings by the same number of stops.
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
}
