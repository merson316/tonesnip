using ToneSnip.Core.Imaging;
using ToneSnip.Core.Record;
using Xunit;

namespace ToneSnip.Core.Tests.Record;

public class Nv12Tests
{
    private static BgraImage Solid(int w, int h, byte r, byte g, byte b)
    {
        var img = BgraImage.Blank(w, h);
        for (int i = 0; i < img.Data.Length; i += 4) { img.Data[i] = b; img.Data[i + 1] = g; img.Data[i + 2] = r; img.Data[i + 3] = 255; }
        return img;
    }

    [Theory]
    [InlineData(0, 0, 0, 16, 128, 128)]
    [InlineData(255, 255, 255, 235, 128, 128)]
    [InlineData(255, 0, 0, 63, 102, 240)]
    [InlineData(0, 255, 0, 173, 42, 26)]
    [InlineData(0, 0, 255, 32, 240, 118)]
    public void The_bt709_reference_colours_land_on_their_limited_range_codes(byte r, byte g, byte b, byte y, byte cb, byte cr)
    {
        byte[] nv12 = new byte[Nv12.Size(2, 2)];
        Nv12.FromBgra(Solid(2, 2, r, g, b), nv12);
        Assert.All(nv12[..4], v => Assert.Equal(y, v));
        Assert.Equal(cb, nv12[4]);
        Assert.Equal(cr, nv12[5]);
    }

    [Fact]
    public void Each_block_carries_the_mean_colour_of_its_four_pixels()
    {
        var img = Solid(4, 2, 0, 0, 0);
        for (int x = 0; x < 2; x++) { int i = x * 4; img.Data[i] = img.Data[i + 1] = img.Data[i + 2] = 255; }
        byte[] nv12 = new byte[Nv12.Size(4, 2)];
        Nv12.FromBgra(img, nv12);
        Assert.Equal(new byte[] { 235, 235, 16, 16, 16, 16, 16, 16 }, nv12[..8]);
        Assert.Equal(128, nv12[8]); Assert.Equal(128, nv12[9]);
        Assert.Equal(128, nv12[10]); Assert.Equal(128, nv12[11]);
    }

    [Fact]
    public void Odd_sides_are_refused()
        => Assert.Throws<ArgumentException>(() => Nv12.FromBgra(BgraImage.Blank(3, 2), new byte[64]));
}
