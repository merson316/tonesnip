using ToneSnip.Core.Color;
using ToneSnip.Core.Geometry;
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

    [Fact]
    public void ToBgra8Into_encodes_srgb_in_bgra_order_and_sets_alpha()
    {
        var img = new HalfImage(3, 1);
        float[] rgb = { 1f, 1f, 1f,  0.5f, 0.5f, 0.5f,  0f, 0.5f, 1f };
        for (int px = 0; px < 3; px++)
        {
            for (int c = 0; c < 3; c++) img.Data[px * 4 + c] = Transfer.FloatToHalf(rgb[px * 3 + c]);
            img.Data[px * 4 + 3] = Transfer.FloatToHalf(0.25f);   // the frame's alpha is not carried over
        }
        var bgra = new byte[12];
        PixelConvert.ToBgra8Into(img, new ClampTonemapper(), null, bgra);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, bgra[0..4]);
        Assert.Equal(new byte[] { 188, 188, 188, 255 }, bgra[4..8]);   // sRGB(0.5) = 0.7354 * 255 = 187.5 -> 188
        Assert.Equal(new byte[] { 255, 188, 0, 255 }, bgra[8..12]);    // R=0, G=0.5, B=1 written as B, G, R
    }

    [Fact]
    public void ToBgra8Into_rejects_a_buffer_of_the_wrong_size()
        => Assert.Throws<ArgumentException>(() => PixelConvert.ToBgra8Into(new HalfImage(2, 2), null, null, new byte[15]));

    [Theory]
    [InlineData("aces")]
    [InlineData("hable")]
    public void A_rectangle_tonemapped_into_place_matches_its_crop_tonemapped_and_copied(string tonemap)
    {
        var rng = new Random(5);
        var frame = new HalfImage(37, 23);
        for (int i = 0; i < frame.Data.Length; i++) frame.Data[i] = Transfer.FloatToHalf(rng.NextSingle() * 6f);
        var rect = new IntRect(5, 3, 20, 11);
        var p = new TonemapParams { SdrWhiteNits = 200f };
        ITonemapper? tm = tonemap == "aces" ? null : TonemapperFactory.Create(tonemap, p);
        AcesLut? lut = tonemap == "aces" ? new AcesLut(p) : null;
        HalfImage crop = frame.Crop(rect);
        var cropped = new byte[crop.Width * crop.Height * 4];
        PixelConvert.ToBgra8Into(crop, tm, lut, cropped);
        BgraImage target = BgraImage.Blank(30, 20);
        Array.Fill(target.Data, (byte)7);
        PixelConvert.ToBgra8Into(frame, rect, tm, lut, target, 4, 6);
        for (int y = 0; y < target.Height; y++)
            for (int x = 0; x < target.Width; x++)
            {
                int t = (y * target.Width + x) * 4;
                bool inside = x >= 4 && x < 4 + rect.Width && y >= 6 && y < 6 + rect.Height;
                byte[] want = inside ? cropped[(((y - 6) * rect.Width + x - 4) * 4)..(((y - 6) * rect.Width + x - 4) * 4 + 4)] : new byte[] { 7, 7, 7, 7 };
                Assert.Equal(want, target.Data[t..(t + 4)]);
            }
    }

    [Fact]
    public void A_rectangle_that_does_not_fit_its_target_is_refused()
        => Assert.Throws<ArgumentOutOfRangeException>(() => PixelConvert.ToBgra8Into(new HalfImage(8, 8), new IntRect(0, 0, 4, 4), null, null, BgraImage.Blank(6, 6), 3, 0));
}
