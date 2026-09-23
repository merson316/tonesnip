using ToneSnip.Core.Color;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using Xunit;

namespace ToneSnip.Core.Tests;

public class HalfImageTests
{
    /// <summary>R ramps by 0.25 per pixel in row-major order, G is 1 and B is -0.5.</summary>
    private static HalfImage Ramp(int w, int h)
    {
        var img = new HalfImage(w, h);
        for (int i = 0; i < w * h; i++)
        {
            img.Data[i * 4] = Transfer.FloatToHalf(i * 0.25f); img.Data[i * 4 + 1] = Transfer.FloatToHalf(1f);
            img.Data[i * 4 + 2] = Transfer.FloatToHalf(-0.5f); img.Data[i * 4 + 3] = 0x3C00;
        }
        return img;
    }

    [Fact]
    public void Crop_copies_the_right_pixels()
    {
        HalfImage h = Ramp(5, 4);
        HalfImage c = h.Crop(new IntRect(1, 1, 3, 2));
        Assert.Equal((3, 2), (c.Width, c.Height));
        Assert.Equal(h.Sample(1, 1), c.Sample(0, 0));
        Assert.Equal(h.Sample(3, 2), c.Sample(2, 1));
    }

    [Fact]
    public void Crop_out_of_bounds_throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Ramp(4, 4).Crop(new IntRect(2, 2, 3, 3)));

    [Fact]
    public void Sample_and_downsample()
    {
        HalfImage h = Ramp(8, 8);
        Assert.Equal(0.25f * 9, h.Sample(1, 1).R, 3);
        Assert.Equal((1f, -0.5f), (h.Sample(1, 1).G, h.Sample(1, 1).B));
        HalfImage small = h.Downsample(4);
        Assert.Equal((2, 2), (small.Width, small.Height));
        Assert.Equal(h.Sample(4, 4).R, small.Sample(1, 1).R);
    }
}
