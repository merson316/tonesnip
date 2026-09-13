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
}
