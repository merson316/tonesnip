using ToneSnip.Core.Imaging;
using ToneSnip.Core.Tonemap;
using Xunit;

namespace ToneSnip.Core.Tests;

public class AutoExposureTests
{
    private static FloatImage Gray(int w, int h, float v)
    {
        var img = new FloatImage(w, h);
        Array.Fill(img.Data, v);
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
        FloatImage img = Gray(100, 100, 2.5f);          // 200 nits
        for (int i = 0; i < 50; i++) img.Data[i * 3] = img.Data[i * 3 + 1] = img.Data[i * 3 + 2] = 100f;   // 0.5% of pixels
        Assert.Equal(1f, AutoExposure.Compute(img, 200f, 1f), 2);
    }
}
