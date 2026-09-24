using ToneSnip.Core.Geometry;

namespace ToneSnip.Core.Imaging;

/// <summary>
/// The colour picker's pixel loupe as a picture: <see cref="GuidesLayout.Source"/> pixels square around a point, each
/// magnified to a cell, on a faint cell grid with the middle pixel outlined white inside black. It draws what the
/// overlay's Guides loupe draws straight into its window, for the editor, which shows it as an image.
/// </summary>
public static class LoupeRaster
{
    /// <summary>How much of each channel the grid lines keep, of 255, as in the overlay's loupe.</summary>
    public const int GridKeep = 200;

    /// <summary>
    /// Renders <paramref name="source"/> × <paramref name="source"/> pixels of <paramref name="image"/> centred on
    /// (<paramref name="cx"/>, <paramref name="cy"/>) into <paramref name="into"/>, BGRA rows of
    /// <c>source * cell</c> pixels, every pixel opaque. Pixels off the image are black. <paramref name="edge"/> is the
    /// width of each line of the middle pixel's outline.
    /// </summary>
    public static void Render(BgraImage image, int cx, int cy, int source, int cell, int edge, Span<byte> into)
    {
        int size = source * cell, stride = size * 4, half = source / 2;
        if (source <= 0 || cell <= 0 || into.Length < stride * size) throw new ArgumentException("the target is smaller than the loupe", nameof(into));
        byte[] px = image.Data;
        for (int j = 0; j < source; j++)
        {
            int sy = cy - half + j;
            for (int i = 0; i < source; i++)
            {
                int sx = cx - half + i;
                byte b = 0, g = 0, r = 0;
                if (sx >= 0 && sy >= 0 && sx < image.Width && sy < image.Height)
                {
                    int s = (sy * image.Width + sx) * 4;
                    b = px[s]; g = px[s + 1]; r = px[s + 2];
                }
                Fill(into, stride, new IntRect(i * cell, j * cell, cell, cell), b, g, r);
            }
        }
        // The grid: one line at the leading edge of every cell but the first, darkened like the overlay's.
        for (int k = 1; k < source; k++)
        {
            for (int y = 0; y < size; y++) PixelShade.Shade(into.Slice(y * stride + k * cell * 4, 4), GridKeep);
            PixelShade.Shade(into.Slice(k * cell * stride, stride), GridKeep);
        }
        var centre = new IntRect(half * cell, half * cell, cell, cell);
        Ring(into, stride, IntRect.FromLtrb(centre.Left - edge, centre.Top - edge, centre.Right + edge, centre.Bottom + edge), edge, 0);
        Ring(into, stride, centre, edge, 255);
    }

    /// <summary>A ring <paramref name="thickness"/> wide just inside <paramref name="outer"/>, in one grey level.</summary>
    private static void Ring(Span<byte> into, int stride, IntRect outer, int thickness, byte level)
    {
        Fill(into, stride, IntRect.FromLtrb(outer.Left, outer.Top, outer.Right, outer.Top + thickness), level, level, level);
        Fill(into, stride, IntRect.FromLtrb(outer.Left, outer.Bottom - thickness, outer.Right, outer.Bottom), level, level, level);
        Fill(into, stride, IntRect.FromLtrb(outer.Left, outer.Top, outer.Left + thickness, outer.Bottom), level, level, level);
        Fill(into, stride, IntRect.FromLtrb(outer.Right - thickness, outer.Top, outer.Right, outer.Bottom), level, level, level);
    }

    private static void Fill(Span<byte> into, int stride, IntRect r, byte b, byte g, byte red)
    {
        for (int y = r.Top; y < r.Bottom; y++)
            for (int x = r.Left; x < r.Right; x++)
            {
                int d = y * stride + x * 4;
                into[d] = b; into[d + 1] = g; into[d + 2] = red; into[d + 3] = 255;
            }
    }
}
