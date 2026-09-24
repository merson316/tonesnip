using ToneSnip.Core.Color;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Hdr;
using ToneSnip.Core.Imaging;
using ToneSnip.Core.Tonemap;
using Xunit;

namespace ToneSnip.Core.Tests.Hdr;

/// <summary>The CPU <see cref="IHdrFrame"/>, which the GPU one is checked against (the self-test), and its helpers.</summary>
public class HalfFrameTests
{
    /// <summary>Luminance climbing along x and y past SDR white, with NaN and a negative pixel in the first row.</summary>
    private static HalfImage Scene(int w, int h)
    {
        var img = new HalfImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                float v = (x + 2 * y) / 16f;
                img.Data[i] = Transfer.FloatToHalf(v); img.Data[i + 1] = Transfer.FloatToHalf(v * 0.8f);
                img.Data[i + 2] = Transfer.FloatToHalf(v * 0.5f); img.Data[i + 3] = 0x3C00;
            }
        img.Data[0] = 0x7E00;                          // NaN red
        img.Data[4] = Transfer.FloatToHalf(-3f);       // negative red
        return img;
    }

    [Fact]
    public void Sample_crop_and_downsample_are_the_images()
    {
        HalfImage img = Scene(40, 30);
        using var f = new HalfFrame(img);
        Assert.True(f.TrySample(7, 5, out float r, out float g, out float b));
        Assert.Equal(img.Sample(7, 5), (r, g, b));
        var rect = new IntRect(3, 4, 20, 10);
        Assert.Equal(img.Crop(rect).Data, f.Crop(rect).Data);
        Assert.Equal(img.Downsample(3).Data, f.Downsample(3).Data);
    }

    [Fact]
    public void Stats_read_every_pixel_of_a_small_rectangle()
    {
        HalfImage img = Scene(40, 30);
        using var f = new HalfFrame(img);
        var rect = new IntRect(5, 5, 10, 8);
        Assert.True(f.TryStats(rect, out float peak, out float mean));
        float p = 0, sum = 0;
        for (int y = rect.Top; y < rect.Bottom; y++)
            for (int x = rect.Left; x < rect.Right; x++)
            {
                (float r, float g, float b) = img.Sample(x, y);
                float v = Transfer.Luminance709(r, g, b) * 80f;
                p = Math.Max(p, v); sum += v;
            }
        Assert.Equal(p, peak);
        Assert.Equal(sum / rect.Width / rect.Height, mean, 3);
    }

    /// <summary>A large rectangle is read on a grid of at most <see cref="HalfFrame.StatsSamples"/> pixels, as the
    /// overlay's readout always was.</summary>
    [Fact]
    public void Stats_sample_a_large_rectangle_on_a_grid()
    {
        var img = new HalfImage(400, 300);
        // One bright pixel off the grid (step 2 for 120,000 pixels) cannot be seen; one on it can.
        img.Data[(1 * 400 + 1) * 4] = Transfer.FloatToHalf(50f);
        using var f = new HalfFrame(img);
        Assert.True(f.TryStats(new IntRect(0, 0, 400, 300), out float peak, out _));
        Assert.Equal(0f, peak);
        img.Data[(2 * 400 + 2) * 4] = Transfer.FloatToHalf(50f);
        Assert.True(f.TryStats(new IntRect(0, 0, 400, 300), out peak, out _));
        Assert.Equal(Transfer.Luminance709(50f, 0, 0) * 80f, peak);
    }

    [Fact]
    public void Tonemap_into_a_region_matches_pixel_convert_and_leaves_the_rest()
    {
        HalfImage img = Scene(40, 30);
        using var f = new HalfFrame(img);
        foreach (string curve in TonemapperFactory.Names)
        {
            var p = new TonemapParams { SdrWhiteNits = 203f, PeakNits = 800f, Exposure = 1.5f, Knee = 0.6f };
            var source = new IntRect(4, 3, 12, 9);
            var target = BgraImage.Blank(30, 20);
            Array.Fill(target.Data, (byte)0x5A);
            f.Tonemap(new TonemapCurve(curve, p), source, target, 6, 7);
            var want = BgraImage.Blank(30, 20);
            Array.Fill(want.Data, (byte)0x5A);
            (ITonemapper? tm, AcesLut? lut) = curve == "aces" ? (null, new AcesLut(p)) : (TonemapperFactory.Create(curve, p), (AcesLut?)null);
            PixelConvert.ToBgra8Into(img, source, tm, lut, want, 6, 7);
            Assert.Equal(want.Data, target.Data);
        }
    }

    [Fact]
    public void Aces_table_is_cached_per_parameters()
    {
        var a = new TonemapParams { Exposure = 1.25f };
        Assert.Same(AcesLut.For(a), AcesLut.For(a with { }));
        Assert.NotSame(AcesLut.For(a), AcesLut.For(a with { Exposure = 2f }));
    }

    [Fact]
    public void A_disposed_frame_is_unreadable()
    {
        var f = new HalfFrame(Scene(8, 8));
        f.Dispose();
        Assert.False(f.Readable);
        Assert.False(f.TrySample(1, 1, out _, out _, out _));
        Assert.False(f.TryStats(new IntRect(0, 0, 4, 4), out _, out _));
        Assert.Throws<ObjectDisposedException>(() => f.Crop(new IntRect(0, 0, 2, 2)));
    }

    [Fact]
    public void Zebra_mask_marks_what_the_zebra_pass_stripes()
    {
        HalfImage img = Scene(70, 9);   // not a multiple of 32 wide: the row padding must stay clear
        const float white = 203f / 80f, exposure = 1.7f;
        ZebraMask mask = new HalfFrame(img).Zebra(white, exposure);
        Assert.Equal(3, mask.Stride);
        int over = 0;
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
            {
                (float r, float g, float b) = img.Sample(x, y);
                bool want = !(Transfer.Luminance709(r, g, b) * exposure <= white);
                Assert.Equal(want, mask.Over(x, y));
                if (want) over++;
            }
        Assert.InRange(over, 1, img.Width * img.Height - 1);
        Assert.True(mask.Over(0, 0));   // NaN is striped, as it always was
        for (int y = 0; y < img.Height; y++) Assert.Equal(0u, mask.Bits[y * mask.Stride + 2] >> (70 - 64));
    }
}
