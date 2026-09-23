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
        HdrCanvas.FlattenOnWhite(canvas, white);
        HalfImage flatHdr = canvas;

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

    [Fact]
    public void Opacity_is_detected_from_alpha_alone()
    {
        Assert.True(Flatten.IsOpaque(new BgraImage(2, 1, new byte[] { 0, 0, 0, 255, 9, 9, 9, 255 })));
        Assert.False(Flatten.IsOpaque(new BgraImage(2, 1, new byte[] { 0, 0, 0, 255, 255, 255, 255, 254 })));
    }
}

/// <summary>The UltraHDR JPEG's inputs are built with fewer buffers (the canvas flattened in place, an opaque SDR
/// render not copied, the gain map in two passes); these pin them to what the copying code produced.</summary>
public class UltraHdrInputTests
{
    /// <summary>The gain map as it was: every gain kept in a float array between the range and the quantisation.</summary>
    private static GainMapResult OneArrayGainMap(HalfImage hdr, BgraImage sdr, float referenceWhiteNits)
    {
        int n = hdr.Width * hdr.Height;
        float scale = HdrCanvas.ReferenceScale(referenceWhiteNits);
        var gain = new float[n];
        float min = 0f, max = 0f;
        for (int i = 0; i < n; i++)
        {
            float hr = Transfer.HalfToFloat(hdr.Data[i * 4]), hg = Transfer.HalfToFloat(hdr.Data[i * 4 + 1]), hb = Transfer.HalfToFloat(hdr.Data[i * 4 + 2]);
            float yh = Transfer.Luminance709(hr, hg, hb) / scale;
            yh = float.IsNaN(yh) ? 0f : Math.Min(yh, GainMap.MaxNits / Math.Max(referenceWhiteNits, 1f));
            ColorMath.LiftSrgb(sdr.Data[i * 4], sdr.Data[i * 4 + 1], sdr.Data[i * 4 + 2], 1f, out float sr, out float sg, out float sb);
            float ys = Transfer.Luminance709(sr, sg, sb);
            float g = MathF.Log2((Math.Max(yh, 0f) + GainMap.Offset) / (Math.Max(ys, 0f) + GainMap.Offset));
            gain[i] = g; if (g < min) min = g; if (g > max) max = g;
        }
        if (max - min < 1e-3f) max = min + 1e-3f;
        var gray = new byte[n];
        float range = max - min;
        for (int i = 0; i < n; i++) gray[i] = (byte)Math.Round(Math.Clamp((gain[i] - min) / range, 0f, 1f) * 255f);
        return new GainMapResult(gray, hdr.Width, hdr.Height, min, max);
    }

    /// <summary>The HDR flatten as it was: on a copy.</summary>
    private static HalfImage CopyFlattened(HalfImage canvas, float referenceWhiteNits)
    {
        var o = new HalfImage(canvas.Width, canvas.Height, (ushort[])canvas.Data.Clone());
        float white = HdrCanvas.ReferenceScale(referenceWhiteNits);
        ushort opaque = Transfer.FloatToHalf(1f);
        for (int i = 0; i < o.Data.Length; i += 4)
        {
            float a = Math.Clamp(Transfer.HalfToFloat(o.Data[i + 3]), 0f, 1f);
            if (a >= 1f) continue;
            for (int c = 0; c < 3; c++) o.Data[i + c] = Transfer.FloatToHalf(HdrCanvas.Finite(Transfer.HalfToFloat(o.Data[i + c]) * a + white * (1f - a)));
            o.Data[i + 3] = opaque;
        }
        return o;
    }

    private static (HalfImage Hdr, BgraImage Sdr) Random(int w, int h, int seed, bool transparent)
    {
        var rng = new Random(seed);
        var hdr = new HalfImage(w, h);
        var sdr = BgraImage.Blank(w, h);
        rng.NextBytes(sdr.Data);
        for (int p = 0; p < w * h; p++)
        {
            for (int c = 0; c < 3; c++) hdr.Data[p * 4 + c] = Transfer.FloatToHalf(rng.Next(40) == 0 ? rng.NextSingle() * 200f : rng.NextSingle() * 3f);
            bool clear = transparent && rng.Next(3) == 0;
            hdr.Data[p * 4 + 3] = Transfer.FloatToHalf(clear ? rng.NextSingle() : 1f);
            if (!clear) sdr.Data[p * 4 + 3] = 255;
        }
        return (hdr, sdr);
    }

    [Theory]
    [InlineData(false, 80f)]
    [InlineData(true, 203f)]
    public void The_gain_map_and_its_inputs_match_the_copying_code(bool transparent, float white)
    {
        (HalfImage canvas, BgraImage sdr) = Random(97, 61, transparent ? 11 : 12, transparent);
        HalfImage before = CopyFlattened(canvas, white);
        GainMapResult expected = OneArrayGainMap(before, Flatten.OnWhite(sdr), white);

        BgraImage flat = Flatten.IsOpaque(sdr) ? sdr : Flatten.OnWhite(sdr);
        Assert.Equal(!transparent, ReferenceEquals(flat, sdr));
        HdrCanvas.FlattenOnWhite(canvas, white);
        Assert.Equal(before.Data, canvas.Data);
        Assert.Equal(Flatten.OnWhite(sdr).Data, flat.Data);
        GainMapResult actual = GainMap.Compute(canvas, flat, white);
        Assert.Equal(expected.Gray, actual.Gray);
        Assert.Equal((expected.Min, expected.Max), (actual.Min, actual.Max));
    }
}
