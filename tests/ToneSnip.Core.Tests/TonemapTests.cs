using ToneSnip.Core.Tonemap;
using Xunit;

namespace ToneSnip.Core.Tests;

public class TonemapTests
{
    private static (float r, float g, float b) Map(ITonemapper tm, float r, float g, float b)
    {
        tm.Map(ref r, ref g, ref b);
        return (r, g, b);
    }

    // scRGB value that equals SDR white when the SDR white level is 200 nits: 200/80 = 2.5
    private static readonly TonemapParams P200 = new() { SdrWhiteNits = 200f, PeakNits = 1000f };

    [Fact]
    public void Desktop_clip_keeps_sdr_range_exact()
    {
        var tm = new DesktopTonemapper(P200);
        Assert.Equal((1f, 1f, 1f), Map(tm, 2.5f, 2.5f, 2.5f));
        var half = Map(tm, 1.25f, 1.25f, 1.25f);
        Assert.Equal(0.5, half.r, 0.0001);
        Assert.Equal((0f, 0f, 0f), Map(tm, 0f, 0f, 0f));
        var c = Map(tm, 1.0f, 0.5f, 0.25f);          // 0.4, 0.2, 0.1 in SDR units
        Assert.Equal(0.4, c.r, 0.0001);
        Assert.Equal(0.2, c.g, 0.0001);
        Assert.Equal(0.1, c.b, 0.0001);
    }

    [Fact]
    public void Desktop_clip_limits_highlights_to_one()
    {
        var tm = new DesktopTonemapper(P200);
        Assert.Equal((1f, 1f, 1f), Map(tm, 25f, 25f, 25f));
        var pink = Map(tm, 7.5f, 2.5f, 2.5f);          // (3,1,1) SDR units, luminance 1.425
        Assert.Equal(1f, pink.r);
        Assert.Equal(pink.g, pink.b);
        Assert.InRange(pink.g, 0.6f, 0.8f);            // scaled by 1/1.425 => 0.702
    }

    [Fact]
    public void Desktop_negative_components_clamp_to_zero()
    {
        var tm = new DesktopTonemapper(P200);
        var c = Map(tm, -0.5f, 1.25f, 1.25f);
        Assert.Equal(0f, c.r);
        Assert.Equal(0.5, c.g, 0.0001);
    }

    [Fact]
    public void Desktop_exposure_scales_input()
    {
        var tm = new DesktopTonemapper(P200 with { Exposure = 2f });
        Assert.Equal(1.0, Map(tm, 1.25f, 1.25f, 1.25f).r, 0.0001);
    }

    [Fact]
    public void Desktop_knee_rolls_off_smoothly()
    {
        var tm = new DesktopTonemapper(P200 with { Knee = 0.5f });
        // far below the knee: unchanged
        Assert.Equal(0.3, Map(tm, 0.75f, 0.75f, 0.75f).r, 0.0001);
        // SDR white is now compressed a little but stays bright
        float white = Map(tm, 2.5f, 2.5f, 2.5f).r;
        Assert.InRange(white, 0.7f, 0.999f);
        // peak and beyond map to 1.0
        Assert.Equal(1.0, Map(tm, 12.5f, 12.5f, 12.5f).r, 0.001);   // 1000 nits
        Assert.Equal(1f, Map(tm, 50f, 50f, 50f).r);
        // monotonic and grey stays grey
        float prev = -1f;
        for (float v = 0f; v <= 15f; v += 0.05f)
        {
            var c = Map(tm, v, v, v);
            Assert.Equal(c.r, c.g);
            Assert.Equal(c.g, c.b);
            Assert.True(c.r >= prev - 1e-5f, $"not monotonic at {v}: {c.r} < {prev}");
            prev = c.r;
        }
    }

    [Fact]
    public void Hable_maps_black_to_black_and_white_point_to_one()
    {
        var tm = new HableTonemapper(P200);
        Assert.Equal((0f, 0f, 0f), Map(tm, 0f, 0f, 0f));
        Assert.Equal(1.0, Map(tm, 12.5f, 12.5f, 12.5f).r, 0.001);   // peak = white point
        Assert.True(Map(tm, 2.5f, 2.5f, 2.5f).r < 1f);
        float prev = -1f;
        for (float v = 0f; v <= 15f; v += 0.05f)
        {
            float r = Map(tm, v, v, v).r;
            Assert.True(r >= prev, $"not monotonic at {v}");
            prev = r;
        }
    }

    [Fact]
    public void Aces_maps_black_to_black_and_saturates_to_one()
    {
        var tm = new AcesTonemapper(P200);
        Assert.Equal((0f, 0f, 0f), Map(tm, 0f, 0f, 0f));
        Assert.Equal(1.0, Map(tm, 500f, 500f, 500f).r, 0.001);
        float prev = -1f;
        for (float v = 0f; v <= 15f; v += 0.05f)
        {
            float r = Map(tm, v, v, v).r;
            Assert.True(r >= prev, $"not monotonic at {v}");
            Assert.InRange(r, 0f, 1f);
            prev = r;
        }
    }

    [Fact]
    public void Factory_creates_by_name_and_rejects_unknown()
    {
        Assert.IsType<DesktopTonemapper>(TonemapperFactory.Create("desktop", P200));
        Assert.IsType<HableTonemapper>(TonemapperFactory.Create("Hable", P200));
        Assert.IsType<AcesTonemapper>(TonemapperFactory.Create("ACES", P200));
        Assert.Throws<ArgumentException>(() => TonemapperFactory.Create("reinhard", P200));
        Assert.Equal(new[] { "desktop", "hable", "aces" }, TonemapperFactory.Names);
    }
}
