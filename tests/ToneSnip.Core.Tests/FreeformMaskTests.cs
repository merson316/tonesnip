using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using Xunit;

namespace ToneSnip.Core.Tests;

public class FreeformMaskTests
{
    [Fact]
    public void Bounding_box_is_inclusive()
    {
        var pts = new List<(int, int)> { (10, 10), (30, 12), (20, 40) };
        Assert.Equal(new IntRect(10, 10, 21, 31), FreeformMask.BoundingBox(pts));
    }

    [Fact]
    public void Triangle_keeps_inside_and_clears_outside()
    {
        var pts = new List<(int, int)> { (0, 0), (39, 0), (0, 39) };
        var img = new BgraImage(40, 40, new byte[40 * 40 * 4]);
        Array.Fill(img.Data, (byte)255);
        FreeformMask.Apply(img, new IntRect(0, 0, 40, 40), pts);
        Assert.Equal(255, img.Data[(5 * 40 + 5) * 4 + 3]);     // inside
        Assert.Equal(0, img.Data[(35 * 40 + 35) * 4 + 3]);     // outside the hypotenuse
        Assert.Equal(0, img.Data[(35 * 40 + 35) * 4]);         // colour cleared too (premultiplied-safe)
    }

    [Fact]
    public void Polygon_is_relative_to_image_bounds()
    {
        var pts = new List<(int, int)> { (100, 100), (110, 100), (110, 110), (100, 110) };
        var img = new BgraImage(20, 20, new byte[20 * 20 * 4]);
        Array.Fill(img.Data, (byte)255);
        FreeformMask.Apply(img, new IntRect(95, 95, 20, 20), pts);
        Assert.Equal(255, img.Data[(10 * 20 + 10) * 4 + 3]);
        Assert.Equal(0, img.Data[(1 * 20 + 1) * 4 + 3]);
    }

    [Fact]
    public void Byte_mask_overload_agrees_with_bgra_path_on_a_triangle()
    {
        var pts = new List<(int, int)> { (0, 0), (39, 0), (0, 39) };
        var bounds = new IntRect(0, 0, 40, 40);

        var img = new BgraImage(40, 40, new byte[40 * 40 * 4]);
        Array.Fill(img.Data, (byte)255);
        FreeformMask.Apply(img, bounds, pts);

        var mask = new byte[40 * 40];
        Array.Fill(mask, (byte)255);
        FreeformMask.Apply(mask, 40, 40, bounds, pts);

        for (int i = 0; i < mask.Length; i++)
            Assert.Equal(img.Data[i * 4 + 3] != 0, mask[i] != 0);
    }

    /// <summary>The per-pixel scanline clear FreeformMask used before it went through FreeformSpans, kept as the
    /// reference the spans must reproduce byte for byte.</summary>
    private static void ReferenceApply(byte[] data, int width, int height, int bpp, IntRect imageBounds, IReadOnlyList<(int X, int Y)> polygon)
    {
        if (polygon.Count < 3) return;
        int n = polygon.Count;
        var xs = new List<float>(n);
        for (int row = 0; row < height; row++)
        {
            float py = imageBounds.Top + row + 0.5f;
            xs.Clear();
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                (int xi, int yi) = polygon[i]; (int xj, int yj) = polygon[j];
                if ((yi + 0.5f > py) != (yj + 0.5f > py))
                    xs.Add(xi + (py - (yi + 0.5f)) * (xj - xi) / (float)(yj - yi));
            }
            xs.Sort();
            int rowStart = row * width * bpp;
            int col = 0;
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int inL = Math.Clamp((int)MathF.Ceiling(xs[k] - 0.5f) - imageBounds.Left, 0, width);
                int inR = Math.Clamp((int)MathF.Floor(xs[k + 1] - 0.5f) + 1 - imageBounds.Left, 0, width);
                for (; col < inL; col++) for (int b = 0; b < bpp; b++) data[rowStart + col * bpp + b] = 0;
                col = Math.Max(col, inR);
            }
            for (; col < width; col++) for (int b = 0; b < bpp; b++) data[rowStart + col * bpp + b] = 0;
        }
    }

    /// <summary>Random lassos, self-crossing and running off the image, some with collinear and repeated points.</summary>
    private static List<(int X, int Y)> RandomLasso(Random rng, IntRect bounds)
    {
        int count = rng.Next(3, 40);
        var pts = new List<(int X, int Y)>(count);
        for (int i = 0; i < count; i++)
            pts.Add(i > 0 && rng.Next(8) == 0 ? pts[^1] : (bounds.Left + rng.Next(-10, bounds.Width + 10), bounds.Top + rng.Next(-10, bounds.Height + 10)));
        return pts;
    }

    [Fact]
    public void Apply_matches_the_per_pixel_scanline_it_replaced()
    {
        var rng = new Random(7);
        var bounds = new IntRect(40, 25, 61, 47);
        for (int t = 0; t < 300; t++)
        {
            List<(int X, int Y)> pts = RandomLasso(rng, bounds);
            var expected = new byte[bounds.Width * bounds.Height * 4];
            rng.NextBytes(expected);
            var img = new BgraImage(bounds.Width, bounds.Height, (byte[])expected.Clone());
            ReferenceApply(expected, bounds.Width, bounds.Height, 4, bounds, pts);
            FreeformMask.Apply(img, bounds, pts);
            Assert.Equal(expected, img.Data);
        }
    }

    [Fact]
    public void Spans_clear_a_rectangle_exactly_as_the_whole_image_clear_does_there_and_nothing_else()
    {
        var rng = new Random(11);
        var bounds = new IntRect(0, 0, 53, 38);
        for (int t = 0; t < 300; t++)
        {
            List<(int X, int Y)> pts = RandomLasso(rng, bounds);
            FreeformSpans spans = FreeformSpans.Build(bounds.Width, bounds.Height, bounds, pts);
            var source = new byte[bounds.Width * bounds.Height * 4];
            rng.NextBytes(source);
            var whole = (byte[])source.Clone();
            ReferenceApply(whole, bounds.Width, bounds.Height, 4, bounds, pts);
            var area = IntRect.FromLtrb(rng.Next(-5, 50), rng.Next(-5, 35), rng.Next(0, 60), rng.Next(0, 45));
            var img = new BgraImage(bounds.Width, bounds.Height, (byte[])source.Clone());
            spans.ClearOutside(img, area);
            IntRect inside = area.Intersect(bounds);
            for (int y = 0; y < bounds.Height; y++)
                for (int x = 0; x < bounds.Width; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        int i = (y * bounds.Width + x) * 4 + c;
                        Assert.Equal(inside.Contains(x, y) ? whole[i] : source[i], img.Data[i]);
                    }
        }
    }

    [Fact]
    public void Spans_of_fewer_than_three_points_cut_nothing()
    {
        FreeformSpans spans = FreeformSpans.Build(4, 4, new IntRect(0, 0, 4, 4), new List<(int, int)> { (0, 0), (3, 3) });
        var img = new BgraImage(4, 4, Enumerable.Repeat((byte)9, 64).ToArray());
        spans.ClearOutside(img, new IntRect(0, 0, 4, 4));
        Assert.All(img.Data, b => Assert.Equal(9, b));
    }
}
