using ToneSnip.Core.Color;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using ToneSnip.Core.Tonemap;
using Xunit;

namespace ToneSnip.Core.Tests;

public class AutoExposureTests
{
    private static HalfImage Gray(int w, int h, float v)
    {
        var img = new HalfImage(w, h);
        Array.Fill(img.Data, Transfer.FloatToHalf(v));
        return img;
    }

    [Fact]
    public void Bright_frame_gets_less_exposure_dark_frame_more()
    {
        // 4.0 in scRGB = 320 nits; with SDR white 200 the 99th percentile maps to white at exposure 200/320.
        Assert.Equal(200f / 320f, AutoExposure.Compute(Gray(64, 64, 4f), 200f, 1f), 3);
        // 0.5 = 40 nits: would need 5x, clamped to 4x.
        Assert.Equal(4f, AutoExposure.Compute(Gray(64, 64, 0.5f), 200f, 1f), 3);
        // 100 = 8000 nits: clamped to 0.5x, scaled by the base exposure.
        Assert.Equal(1f, AutoExposure.Compute(Gray(64, 64, 100f), 200f, 2f), 3);
    }

    [Fact]
    public void Black_frame_keeps_base_exposure() => Assert.Equal(1.5f, AutoExposure.Compute(Gray(8, 8, 0f), 200f, 1.5f));

    [Fact]
    public void Percentile_ignores_a_few_hot_pixels()
    {
        HalfImage img = Gray(100, 100, 2.5f);           // 200 nits
        for (int i = 0; i < 50; i++) img.Data[i * 4] = img.Data[i * 4 + 1] = img.Data[i * 4 + 2] = Transfer.FloatToHalf(100f);   // 0.5% of pixels
        Assert.Equal(1f, AutoExposure.Compute(img, 200f, 1f), 2);
    }

    /// <summary>The percentile as it was first written: every step-th sample of a crop in a list, sorted.</summary>
    private static float SortedPercentile99(HalfImage crop)
    {
        int pixels = crop.Width * crop.Height;
        int step = Math.Max(1, pixels / 1_000_000);
        var lum = new List<float>();
        for (int i = 0; i < pixels; i += step)
            lum.Add(Transfer.Luminance709(Transfer.HalfToFloat(crop.Data[i * 4]), Transfer.HalfToFloat(crop.Data[i * 4 + 1]), Transfer.HalfToFloat(crop.Data[i * 4 + 2])) * 80f);
        lum.Sort();
        return lum[Math.Min(lum.Count - 1, (int)(lum.Count * 0.99f))];
    }

    /// <summary>Random scRGB with some structure: flat runs (as in UI), highlights, negatives (out-of-gamut colour)
    /// and the odd NaN.</summary>
    private static HalfImage Random(int w, int h, int seed)
    {
        var rng = new Random(seed);
        var img = new HalfImage(w, h);
        float run = 1f;
        for (int p = 0; p < w * h; p++)
        {
            if (rng.Next(16) == 0) run = rng.Next(8) switch { 0 => 0f, 1 => -0.05f, 2 => rng.NextSingle() * 60f, _ => rng.NextSingle() * 4f };
            for (int c = 0; c < 3; c++)
                img.Data[p * 4 + c] = Transfer.FloatToHalf(rng.Next(50) == 0 ? rng.NextSingle() * 12f - 1f : run * (0.9f + rng.NextSingle() * 0.2f));
            if (rng.Next(5000) == 0) img.Data[p * 4] = 0x7E00;   // NaN
            img.Data[p * 4 + 3] = 0x3C00;
        }
        return img;
    }

    [Theory]
    [InlineData(64, 64, 1)]
    [InlineData(333, 177, 2)]
    [InlineData(1500, 700, 3)]      // just over a million pixels: every sample still taken
    [InlineData(2100, 1000, 4)]     // two million: every second pixel
    public void Selection_matches_sorting_exactly(int w, int h, int seed)
    {
        HalfImage img = Random(w, h, seed);
        Assert.Equal(SortedPercentile99(img), AutoExposure.Percentile99(img, new IntRect(0, 0, w, h)));
    }

    [Theory]
    [InlineData(10, 20, 900, 500)]
    [InlineData(0, 0, 1, 1)]
    [InlineData(1233, 7, 4, 1300)]
    public void A_rectangle_of_the_frame_matches_its_crop(int x, int y, int w, int h)
    {
        HalfImage frame = Random(1400, 1400, 7);
        var rect = new IntRect(x, y, w, h);
        HalfImage crop = frame.Crop(rect);
        float expected = SortedPercentile99(crop);
        Assert.Equal(expected > 0f ? expected : 0f, AutoExposure.Percentile99(frame, rect));
        Assert.Equal(AutoExposure.Compute(crop, 203f, 1.25f), AutoExposure.Compute(frame, rect, 203f, 1.25f));
    }

    [Fact]
    public void Flat_and_mostly_black_frames_match_sorting()
    {
        HalfImage flat = Gray(1200, 900, 3.3f);
        Assert.Equal(SortedPercentile99(flat), AutoExposure.Percentile99(flat, new IntRect(0, 0, 1200, 900)));
        HalfImage dark = Gray(100, 100, 0f);
        for (int i = 0; i < 200; i++) dark.Data[i * 4 + 1] = Transfer.FloatToHalf(0.25f + i * 0.01f);   // 2 % lit
        Assert.Equal(SortedPercentile99(dark), AutoExposure.Percentile99(dark, new IntRect(0, 0, 100, 100)));
        Assert.Equal(0f, AutoExposure.Percentile99(Gray(100, 100, 0f), new IntRect(0, 0, 100, 100)));
    }
}
