using ToneSnip.Core.Color;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Hdr;
using ToneSnip.Core.Imaging;
using ToneSnip.Core.Tonemap;
using Xunit;

namespace ToneSnip.Core.Tests.Hdr;

/// <summary>Edge cases in the HDR and imaging core: alpha blending, non-finite values and size overflow.</summary>
public class HdrEdgeCaseTests
{
    private static float F(ushort half) => Transfer.HalfToFloat(half);

    [Fact]
    public void A_half_transparent_mark_over_a_transparent_canvas_keeps_its_colour()
    {
        // Straight alpha on both sides: the blend divides by the output alpha, so the mark is not darkened.
        var canvas = new HalfImage(1, 1);   // transparent black
        var layer = new BgraImage(1, 1, new byte[] { 255, 255, 255, 128 });
        HdrCanvas.CompositeLayer(canvas, layer, referenceWhiteNits: 80f);
        Assert.Equal(1.0f, F(canvas.Data[0]), 2);
        Assert.Equal(128 / 255f, F(canvas.Data[3]), 2);
    }

    [Fact]
    public void A_half_transparent_mark_over_an_opaque_canvas_blends_as_before()
    {
        var canvas = new HalfImage(1, 1, new[] { Transfer.FloatToHalf(0f), Transfer.FloatToHalf(0f), Transfer.FloatToHalf(0f), Transfer.FloatToHalf(1f) });
        var layer = new BgraImage(1, 1, new byte[] { 255, 255, 255, 128 });
        HdrCanvas.CompositeLayer(canvas, layer, referenceWhiteNits: 80f);
        Assert.Equal(128 / 255f, F(canvas.Data[0]), 2);
        Assert.Equal(1f, F(canvas.Data[3]), 2);
    }

    [Fact]
    public void An_infinite_pixel_does_not_flatten_the_gain_map_or_write_infinity()
    {
        float inf = float.PositiveInfinity;
        var hdr = new HalfImage(4, 1, new[]
        {
            Transfer.FloatToHalf(1f), Transfer.FloatToHalf(1f), Transfer.FloatToHalf(1f), Transfer.FloatToHalf(1f),
            Transfer.FloatToHalf(4f), Transfer.FloatToHalf(4f), Transfer.FloatToHalf(4f), Transfer.FloatToHalf(1f),
            Transfer.FloatToHalf(10f), Transfer.FloatToHalf(10f), Transfer.FloatToHalf(10f), Transfer.FloatToHalf(1f),
            Transfer.FloatToHalf(inf), Transfer.FloatToHalf(inf), Transfer.FloatToHalf(inf), Transfer.FloatToHalf(1f),
        });
        var sdr = new BgraImage(4, 1, Enumerable.Repeat((byte)255, 16).ToArray());
        GainMapResult map = GainMap.Compute(hdr, sdr, referenceWhiteNits: 80f);
        Assert.True(float.IsFinite(map.Max) && float.IsFinite(map.Min));
        Assert.True(map.Gray[1] > map.Gray[0] && map.Gray[2] > map.Gray[1], "the finite highlights keep their order");
    }

    [Fact]
    public void Exposure_cannot_push_a_canvas_pixel_to_infinity()
    {
        var sdr = new BgraImage(1, 1, new byte[] { 0, 0, 0, 255 });
        var crop = new HalfImage(1, 1, new[] { Transfer.FloatToHalf(2000f), Transfer.FloatToHalf(2000f), Transfer.FloatToHalf(2000f), Transfer.FloatToHalf(1f) });
        var region = new IntRect(0, 0, 1, 1);
        HalfImage canvas = HdrCanvas.Build(sdr, region, new[] { new HdrLayer(region, crop) }, IntRect.Empty, exposure: 64f, lasso: null, referenceWhiteNits: 80f);
        Assert.True(float.IsFinite(F(canvas.Data[0])));
    }

    [Fact]
    public void A_layer_whose_image_is_not_its_bounds_is_refused()
    {
        var sdr = new BgraImage(4, 4, new byte[64]);
        var region = new IntRect(0, 0, 4, 4);
        var wrong = new HdrLayer(new IntRect(0, 0, 4, 4), new HalfImage(2, 2));
        Assert.Throws<ArgumentException>(() => HdrCanvas.Build(sdr, region, new[] { wrong }, IntRect.Empty, 1f, null, 80f));
    }

    [Fact]
    public void The_desktop_tonemapper_maps_an_infinite_channel_to_white_not_black()
    {
        var tm = new DesktopTonemapper(new TonemapParams { SdrWhiteNits = 80f, PeakNits = 1000f });
        float r = float.PositiveInfinity, g = 0.5f, b = 0.5f;
        tm.Map(ref r, ref g, ref b);
        Assert.True(float.IsFinite(r) && float.IsFinite(g) && float.IsFinite(b));
        Assert.Equal(1f, r, 3);
    }

    [Fact]
    public void The_desktop_tonemapper_maps_nan_to_black()
    {
        var tm = new DesktopTonemapper(new TonemapParams());
        float r = float.NaN, g = float.NaN, b = float.NaN;
        tm.Map(ref r, ref g, ref b);
        Assert.Equal((0f, 0f, 0f), (r, g, b));
    }

    [Fact]
    public void A_half_image_whose_size_overflows_is_refused()
    {
        Assert.ThrowsAny<ArgumentException>(() => new HalfImage(65536, 16384, new ushort[0]));
    }

    [Fact]
    public void A_redaction_mean_rounds_to_nearest()
    {
        var img = new BgraImage(2, 1, new byte[] { 254, 254, 254, 255, 255, 255, 255, 255 });
        (byte b, byte g, byte r) = Redaction.Mean(img, new IntRect(0, 0, 2, 1));
        Assert.Equal((255, 255, 255), ((int)b, (int)g, (int)r));
    }
}

public class FlattenOnWhiteTests
{
    [Fact]
    public void A_transparent_pixel_is_white_in_the_sdr_base_and_at_reference_white_in_the_hdr_canvas_so_its_gain_is_zero()
    {
        // The SDR JPEG and the UltraHDR base both flatten transparency on white (the HDR canvas at reference white), so
        // a freeform snip's outside matches in both files.
        var sdr = new BgraImage(2, 1, new byte[] { 0, 0, 0, 0, 255, 255, 255, 255 });            // transparent, opaque white
        const float white = 203f;
        float w = HdrCanvas.ReferenceScale(white);
        var canvas = new HalfImage(2, 1, new[] { (ushort)0, (ushort)0, (ushort)0, (ushort)0,
            Transfer.FloatToHalf(w), Transfer.FloatToHalf(w), Transfer.FloatToHalf(w), Transfer.FloatToHalf(1f) });

        BgraImage flatSdr = Flatten.OnWhite(sdr);
        HalfImage flatHdr = HdrCanvas.FlattenOnWhite(canvas, white);

        Assert.Equal(new byte[] { 255, 255, 255, 255 }, flatSdr.Data[..4]);
        Assert.Equal(w, Transfer.HalfToFloat(flatHdr.Data[0]), 2);
        Assert.Equal(1f, Transfer.HalfToFloat(flatHdr.Data[3]), 2);
        GainMapResult map = GainMap.Compute(flatHdr, flatSdr, white);
        Assert.Equal(map.Gray[1], map.Gray[0]);
    }

    [Fact]
    public void A_half_transparent_pixel_blends_toward_white_the_way_the_jpeg_encoder_did()
    {
        var sdr = new BgraImage(1, 1, new byte[] { 0, 0, 0, 128 });
        Assert.Equal(127, Flatten.OnWhite(sdr).Data[0]);   // (0*128 + 255*127) / 255
    }
}
