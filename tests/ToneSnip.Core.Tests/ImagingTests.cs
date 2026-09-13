using ToneSnip.Core.Imaging;
using ToneSnip.Core.Tonemap;
using Xunit;

namespace ToneSnip.Core.Tests;

public class ImagingTests
{
    /// <summary>Identity tonemapper for tests: clamps to [0,1] only.</summary>
    private sealed class ClampTonemapper : ITonemapper
    {
        public void Map(ref float r, ref float g, ref float b)
        {
            r = Math.Clamp(r, 0f, 1f); g = Math.Clamp(g, 0f, 1f); b = Math.Clamp(b, 0f, 1f);
        }
    }

    private static FloatImage Gradient(int w, int h)
    {
        var img = new FloatImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 3;
                img.Data[i] = x; img.Data[i + 1] = y; img.Data[i + 2] = x * 10 + y;
            }
        return img;
    }

    [Fact]
    public void Crop_copies_the_right_pixels()
    {
        var img = Gradient(6, 5);
        var c = img.Crop(2, 1, 3, 2);
        Assert.Equal(3, c.Width);
        Assert.Equal(2, c.Height);
        // pixel (0,0) of the crop is (2,1) of the source
        Assert.Equal(2f, c.Data[0]);
        Assert.Equal(1f, c.Data[1]);
        // pixel (2,1) of the crop is (4,2) of the source
        int i = (1 * 3 + 2) * 3;
        Assert.Equal(4f, c.Data[i]);
        Assert.Equal(2f, c.Data[i + 1]);
    }

    [Fact]
    public void Crop_out_of_bounds_throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Gradient(4, 4).Crop(2, 2, 3, 3));

    [Fact]
    public void Rotate_clockwise_moves_top_left_to_top_right()
    {
        var img = Gradient(3, 2);            // width 3, height 2
        var r = img.RotateClockwise(1);       // becomes width 2, height 3
        Assert.Equal(2, r.Width);
        Assert.Equal(3, r.Height);
        // source (x=0,y=0) -> dest (x = H-1-y = 1, y = x = 0)
        int d = (0 * 2 + 1) * 3;
        Assert.Equal(0f, r.Data[d]);
        Assert.Equal(0f, r.Data[d + 1]);
        // source (x=2,y=1) -> dest (x = 0, y = 2)
        d = (2 * 2 + 0) * 3;
        Assert.Equal(2f, r.Data[d]);
        Assert.Equal(1f, r.Data[d + 1]);
        // four quarter turns is identity
        Assert.Equal(img.Data, img.RotateClockwise(4).Data);
    }



    [Fact]
    public void ToRgba8_encodes_srgb_and_sets_alpha()
    {
        var img = new FloatImage(3, 1);
        img.Data[0] = 1f; img.Data[1] = 1f; img.Data[2] = 1f;
        img.Data[3] = 0.5f; img.Data[4] = 0.5f; img.Data[5] = 0.5f;
        img.Data[6] = 0f; img.Data[7] = 0f; img.Data[8] = 0f;
        byte[] rgba = PixelConvert.ToRgba8(img, new ClampTonemapper());
        Assert.Equal(12, rgba.Length);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, rgba[0..4]);
        Assert.Equal(new byte[] { 188, 188, 188, 255 }, rgba[4..8]);   // sRGB(0.5) = 0.7354 * 255 = 187.5 -> 188
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, rgba[8..12]);
    }

    [Fact]
    public void ToBgra8Into_matches_ToBgra8()
    {
        var half = new HalfImage(4, 2);
        for (int i = 0; i < half.Data.Length; i++) half.Data[i] = BitConverter.HalfToUInt16Bits((Half)(i * 0.1f));
        var tm = ToneSnip.Core.Tonemap.TonemapperFactory.Create("desktop", new ToneSnip.Core.Tonemap.TonemapParams { SdrWhiteNits = 200, PeakNits = 400 });
        BgraImage a = PixelConvert.ToBgra8(half, tm);
        var b = new byte[4 * 2 * 4];
        PixelConvert.ToBgra8Into(half, tm, null, b);
        Assert.Equal(a.Data, b);

        var lut = new ToneSnip.Core.Tonemap.AcesLut(new ToneSnip.Core.Tonemap.TonemapParams { SdrWhiteNits = 200, PeakNits = 400 });
        BgraImage c = PixelConvert.ToBgra8(half, lut);
        var d = new byte[4 * 2 * 4];
        PixelConvert.ToBgra8Into(half, null, lut, d);
        Assert.Equal(c.Data, d);
    }

}
