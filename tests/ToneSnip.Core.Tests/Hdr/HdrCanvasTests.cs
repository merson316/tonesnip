using ToneSnip.Core.Color;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Hdr;
using ToneSnip.Core.Imaging;
using Xunit;

namespace ToneSnip.Core.Tests.Hdr;

public class HdrCanvasTests
{
    private static BgraImage Flat(int w, int h, byte b, byte g, byte r, byte a = 255)
    {
        var img = BgraImage.Blank(w, h);
        for (int i = 0; i < img.Data.Length; i += 4) { img.Data[i] = b; img.Data[i + 1] = g; img.Data[i + 2] = r; img.Data[i + 3] = a; }
        return img;
    }
    private static (float R, float G, float B, float A) Px(HalfImage h, int x, int y)
    {
        int i = (y * h.Width + x) * 4;
        return (Transfer.HalfToFloat(h.Data[i]), Transfer.HalfToFloat(h.Data[i + 1]), Transfer.HalfToFloat(h.Data[i + 2]), Transfer.HalfToFloat(h.Data[i + 3]));
    }

    [Fact]
    public void Sdr_pixels_are_lifted_to_the_reference_white()
    {
        BgraImage sdr = Flat(8, 8, 255, 255, 255);
        HalfImage h = HdrCanvas.Build(sdr, new IntRect(0, 0, 8, 8), Array.Empty<HdrLayer>(), IntRect.Empty, 1f, null, referenceWhiteNits: 240f);
        (float r, float g, float b, float a) = Px(h, 3, 3);
        Assert.InRange(r, 2.99f, 3.01f); Assert.InRange(g, 2.99f, 3.01f); Assert.InRange(b, 2.99f, 3.01f);   // 240 / 80
        Assert.Equal(1f, a);
        BgraImage grey = Flat(2, 2, 128, 128, 128);
        HalfImage hg = HdrCanvas.Build(grey, new IntRect(0, 0, 2, 2), Array.Empty<HdrLayer>(), IntRect.Empty, 1f, null, 80f);
        Assert.InRange(Px(hg, 0, 0).R, Transfer.SrgbDecode(128 / 255f) - 0.002f, Transfer.SrgbDecode(128 / 255f) + 0.002f);
    }

    [Fact]
    public void Hdr_layers_overwrite_their_area_and_take_the_exposure()
    {
        BgraImage sdr = Flat(10, 10, 0, 0, 0);
        var crop = new HalfImage(4, 4);
        for (int i = 0; i < crop.Data.Length; i += 4) { crop.Data[i] = Transfer.FloatToHalf(5f); crop.Data[i + 1] = Transfer.FloatToHalf(5f); crop.Data[i + 2] = Transfer.FloatToHalf(5f); crop.Data[i + 3] = 0x3C00; }
        var layers = new[] { new HdrLayer(new IntRect(102, 104, 4, 4), crop) };   // region starts at (100,100)
        HalfImage h = HdrCanvas.Build(sdr, new IntRect(100, 100, 10, 10), layers, IntRect.Empty, 2f, null, 80f);
        Assert.InRange(Px(h, 2, 4).R, 9.99f, 10.01f);     // 5 × exposure 2
        Assert.InRange(Px(h, 5, 7).R, 9.99f, 10.01f);
        Assert.Equal(0f, Px(h, 0, 0).R);                  // SDR area untouched by exposure
        Assert.Equal(0f, Px(h, 6, 4).R);                  // just outside the crop
    }

    [Fact]
    public void Viewport_crops_and_lasso_clears_alpha_outside()
    {
        BgraImage sdr = Flat(20, 20, 255, 255, 255);
        var lasso = new List<(int X, int Y)> { (0, 0), (19, 0), (0, 19) };   // upper-left triangle, region frame
        HalfImage h = HdrCanvas.Build(sdr, new IntRect(0, 0, 20, 20), Array.Empty<HdrLayer>(), new IntRect(5, 5, 10, 10), 1f, lasso, 80f);
        Assert.Equal(10, h.Width); Assert.Equal(10, h.Height);
        Assert.Equal(1f, Px(h, 0, 0).A);      // (5,5) inside the triangle
        Assert.Equal(0f, Px(h, 9, 9).A);      // (14,14) outside
        Assert.Equal(0f, Px(h, 9, 9).R);      // colour cleared too
    }

    [Fact]
    public void Lasso_is_read_in_the_desktop_frame()
    {
        // Region at a non-zero origin so a region-local reading of the lasso would miss the region entirely.
        var region = new IntRect(1000, 500, 20, 20);
        BgraImage sdr = Flat(20, 20, 255, 255, 255);
        var lassoDesktop = new List<(int X, int Y)> { (1000, 500), (1019, 500), (1000, 519) };   // upper-left triangle, desktop frame
        HalfImage h = HdrCanvas.Build(sdr, region, Array.Empty<HdrLayer>(), region, 1f, lassoDesktop, 80f);
        Assert.Equal(1f, Px(h, 2, 2).A);      // well inside the triangle
        Assert.Equal(0f, Px(h, 17, 17).A);    // well outside

        // The same triangle in region-local coordinates lands nowhere near (2,2).
        var lassoLocal = new List<(int X, int Y)> { (0, 0), (19, 0), (0, 19) };
        HalfImage hLocal = HdrCanvas.Build(sdr, region, Array.Empty<HdrLayer>(), region, 1f, lassoLocal, 80f);
        Assert.Equal(0f, Px(hLocal, 2, 2).A);
    }

    [Fact]
    public void Colour_maths_round_trip()
    {
        float r = 1f, g = 1f, b = 1f;
        ColorMath.Bt709To2020(ref r, ref g, ref b);
        Assert.InRange(r, 0.99f, 1.01f); Assert.InRange(g, 0.99f, 1.01f); Assert.InRange(b, 0.99f, 1.01f);   // white stays white
        float r2 = 1f, g2 = 0f, b2 = 0f;
        ColorMath.Bt709To2020(ref r2, ref g2, ref b2);
        Assert.InRange(r2, 0.62f, 0.64f); Assert.InRange(g2, 0.06f, 0.08f); Assert.InRange(b2, 0.01f, 0.02f);
        Assert.Equal(0x3C00, Transfer.FloatToHalf(1f));
        Assert.Equal(1f, Transfer.HalfToFloat(Transfer.FloatToHalf(1f)));
    }

    [Fact]
    public void A_white_stroke_lands_at_the_reference_white_and_alpha_blends_in_linear()
    {
        var canvas = new HalfImage(4, 1);
        for (int i = 0; i < 4; i++) { canvas.Data[i * 4] = Transfer.FloatToHalf(8f); canvas.Data[i * 4 + 1] = Transfer.FloatToHalf(8f); canvas.Data[i * 4 + 2] = Transfer.FloatToHalf(8f); canvas.Data[i * 4 + 3] = 0x3C00; }
        var layer = BgraImage.Blank(4, 1);
        // px0: opaque white; px1: 50 % white; px2: untouched; px3: opaque black
        layer.Data[0] = 255; layer.Data[1] = 255; layer.Data[2] = 255; layer.Data[3] = 255;
        layer.Data[4] = 255; layer.Data[5] = 255; layer.Data[6] = 255; layer.Data[7] = 128;
        layer.Data[12] = 0; layer.Data[13] = 0; layer.Data[14] = 0; layer.Data[15] = 255;
        HdrCanvas.CompositeLayer(canvas, layer, referenceWhiteNits: 200f);
        float w = 200f / 80f;
        Assert.InRange(Px(canvas, 0, 0).R, w - 0.01f, w + 0.01f);                          // exactly SDR white, not the 8.0 highlight
        float half = 128 / 255f; float expected = w * half + 8f * (1 - half);
        Assert.InRange(Px(canvas, 1, 0).R, expected - 0.02f, expected + 0.02f);             // linear blend
        Assert.InRange(Px(canvas, 2, 0).R, 7.99f, 8.01f);                                   // untouched
        Assert.Equal(0f, Px(canvas, 3, 0).R);                                               // opaque black covers the highlight
    }

    [Fact]
    public void Compositing_over_transparent_canvas_pixels_restores_alpha()
    {
        var canvas = new HalfImage(1, 1);   // all zero: transparent black (outside a lasso)
        var layer = BgraImage.Blank(1, 1);
        layer.Data[0] = 0; layer.Data[1] = 0; layer.Data[2] = 255; layer.Data[3] = 255;   // opaque red
        HdrCanvas.CompositeLayer(canvas, layer, 80f);
        Assert.Equal(1f, Px(canvas, 0, 0).A);
        Assert.InRange(Px(canvas, 0, 0).R, 0.99f, 1.01f);
    }

    [Fact]
    public void Region_viewport_and_layer_bounds_all_share_the_desktop_frame()
    {
        // Non-zero region origin, a viewport sub-rect at a different offset, and a layer partly overlapping it, so a
        // region-local reading of the viewport or layer bounds would fail.
        var region = new IntRect(1000, 500, 40, 30);
        var viewport = new IntRect(1010, 505, 10, 10);
        BgraImage sdr = Flat(40, 30, 255, 255, 255);   // region-sized, indexed relative to region's own top-left
        var crop = new HalfImage(10, 10);
        for (int i = 0; i < crop.Data.Length; i += 4) { crop.Data[i] = Transfer.FloatToHalf(5f); crop.Data[i + 1] = Transfer.FloatToHalf(5f); crop.Data[i + 2] = Transfer.FloatToHalf(5f); crop.Data[i + 3] = 0x3C00; }
        var layers = new[] { new HdrLayer(new IntRect(1015, 508, 10, 10), crop) };   // desktop frame; overlaps viewport columns 5-9, rows 3-9
        HalfImage h = HdrCanvas.Build(sdr, region, layers, viewport, 1f, null, referenceWhiteNits: 240f);
        Assert.Equal(10, h.Width); Assert.Equal(10, h.Height);
        Assert.InRange(Px(h, 7, 7).R, 4.99f, 5.01f);   // inside both viewport and layer: the layer's own value
        Assert.InRange(Px(h, 0, 0).R, 2.99f, 3.01f);   // inside the viewport but outside the layer: SDR lifted (255/255 × 240/80)
        Assert.InRange(Px(h, 2, 8).R, 2.99f, 3.01f);   // still outside the layer's overlap
    }

    [Fact]
    public void A_viewport_that_misses_the_region_throws()
    {
        BgraImage sdr = Flat(10, 10, 0, 0, 0);
        var region = new IntRect(0, 0, 10, 10);
        var viewport = new IntRect(20, 20, 5, 5);   // does not intersect region at all
        Assert.Throws<ArgumentException>(() => HdrCanvas.Build(sdr, region, Array.Empty<HdrLayer>(), viewport, 1f, null, 80f));
    }

    [Fact]
    public void Per_layer_exposure_multiplies_with_the_global_one()
    {
        BgraImage sdr = Flat(4, 4, 0, 0, 0);
        var crop = new HalfImage(4, 4);
        for (int i = 0; i < crop.Data.Length; i += 4) { crop.Data[i] = Transfer.FloatToHalf(1f); crop.Data[i + 1] = Transfer.FloatToHalf(1f); crop.Data[i + 2] = Transfer.FloatToHalf(1f); crop.Data[i + 3] = 0x3C00; }
        HalfImage h = HdrCanvas.Build(sdr, new IntRect(0, 0, 4, 4), new[] { new HdrLayer(new IntRect(0, 0, 4, 4), crop, 1.5f) }, IntRect.Empty, 2f, null, 80f);
        Assert.InRange(Px(h, 1, 1).R, 2.99f, 3.01f);
    }
}
