using ToneSnip.Core.Annotate;
using ToneSnip.Core.Color;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Hdr;
using ToneSnip.Core.Imaging;
using Xunit;

namespace ToneSnip.Core.Tests.Hdr;

public class HdrRedactionTests
{
    private static HalfImage Gradient(int w, int h, float peak)   // R ramps with x up to peak, G with y, B constant 0.2
    {
        var img = new HalfImage(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            img.Data[i] = Transfer.FloatToHalf(peak * x / (w - 1)); img.Data[i + 1] = Transfer.FloatToHalf(peak * y / (h - 1)); img.Data[i + 2] = Transfer.FloatToHalf(0.2f); img.Data[i + 3] = 0x3C00;
        }
        return img;
    }
    private static HalfImage Copy(HalfImage h) => new(h.Width, h.Height, (ushort[])h.Data.Clone());
    private static BgraImage BgraGradient(int w, int h)   // B ramps with x, G with y, R constant: mirrors Gradient above
    {
        var img = BgraImage.Blank(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            img.Data[i] = (byte)(255 * x / (w - 1)); img.Data[i + 1] = (byte)(255 * y / (h - 1)); img.Data[i + 2] = 60; img.Data[i + 3] = 255;
        }
        return img;
    }
    private static HalfImage Checkerboard(int w, int h, float v0, float v1)   // Alternating v0 and v1 in all three channels
    {
        var img = new HalfImage(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            float v = ((x + y) & 1) == 0 ? v0 : v1;
            img.Data[i] = Transfer.FloatToHalf(v); img.Data[i + 1] = Transfer.FloatToHalf(v); img.Data[i + 2] = Transfer.FloatToHalf(v); img.Data[i + 3] = 0x3C00;
        }
        return img;
    }

    [Fact]
    public void Classic_pixelate_averages_in_linear_and_blur_flattens()
    {
        HalfImage img = Gradient(64, 64, 4f);
        HalfImage pix = Copy(img);
        HdrRedaction.Pixelate(pix, new IntRect(8, 8, 32, 32), 8, (8, 8));
        Assert.Equal(pix.Sample(8, 8), pix.Sample(15, 15));
        Assert.NotEqual(pix.Sample(8, 8), pix.Sample(16, 8));
        (float mr, _, _) = HdrRedaction.Mean(img, new IntRect(8, 8, 8, 8));
        Assert.InRange(pix.Sample(10, 10).R, mr - 0.01f, mr + 0.01f);
        Assert.Equal(img.Sample(0, 0), pix.Sample(0, 0));   // outside untouched
        HalfImage blur = Copy(img);
        HdrRedaction.BoxBlur(blur, new IntRect(16, 16, 32, 32), 8);
        Assert.InRange(Math.Abs(blur.Sample(20, 30).R - blur.Sample(21, 30).R), 0f, 0.04f);
        Assert.Equal(img.Sample(15, 31), blur.Sample(15, 31));
    }

    [Fact]
    public void Private_blocks_depend_only_on_mean_and_seed_and_never_exceed_the_reference_white()
    {
        // Checkerboard with mean 2.0 (exactly representable in half-float)
        HalfImage a = Checkerboard(64, 64, 1.0f, 3.0f);
        HalfImage b = Checkerboard(64, 64, 2.0f, 2.0f);   // flat 2.0
        var r = new IntRect(0, 0, 64, 64);
        HalfImage da = Copy(a), db = Copy(b);
        HdrRedaction.PrivateBlocks(da, r, 8, 42, (0, 0), clampWhite: 2.5f);
        HdrRedaction.PrivateBlocks(db, r, 8, 42, (0, 0), clampWhite: 2.5f);
        Assert.Equal(da.Data, db.Data);   // same mean, same seed → identical
        HalfImage dc = Copy(a);
        HdrRedaction.PrivateBlocks(dc, r, 8, 43, (0, 0), clampWhite: 2.5f);   // different seed
        Assert.NotEqual(da.Data, dc.Data);
        Assert.NotEqual(da.Sample(0, 0), da.Sample(8, 0));   // different blocks differ
        Assert.Equal(da.Sample(0, 0), da.Sample(7, 7));   // same block is identical
        // Test clamping with a higher mean (6.0): mean 6.0 clamped to 2.5 then jittered
        HalfImage c = Checkerboard(64, 64, 5.0f, 7.0f);   // mean 6.0
        HalfImage dc2 = Copy(c);
        HdrRedaction.PrivateBlocks(dc2, r, 8, 42, (0, 0), clampWhite: 2.5f);
        float maxChannel = 0;
        for (int i = 0; i < dc2.Data.Length; i += 4) for (int c2 = 0; c2 < 3; c2++) maxChannel = Math.Max(maxChannel, Transfer.HalfToFloat(dc2.Data[i + c2]));
        Assert.True(maxChannel <= 2.5f * 1.13f, $"private block reached {maxChannel}");   // clamp before ±12 % jitter
    }

    // Hue of a linear RGB triple, same formula as HdrRedaction's private ToHsv (that method isn't exposed, so the
    // jitter can only be observed by recomputing hue from the redacted pixels).
    private static float Hue(float r, float g, float b)
    {
        float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        float h = d <= 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return (h + 360) % 360;
    }

    private static float CircularDiff(float a, float b) { float d = Math.Abs(a - b) % 360; return Math.Min(d, 360 - d); }

    [Fact]
    public void Private_blocks_jitter_hue_around_a_saturated_source_colour()
    {
        // A saturated colour, since hue jitter has no effect on grey.
        const float baseR = 0.8f, baseG = 0.1f, baseB = 0.1f;
        var img = new HalfImage(64, 64);
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
        {
            int i = (y * 64 + x) * 4;
            img.Data[i] = Transfer.FloatToHalf(baseR); img.Data[i + 1] = Transfer.FloatToHalf(baseG); img.Data[i + 2] = Transfer.FloatToHalf(baseB); img.Data[i + 3] = 0x3C00;
        }
        var r = new IntRect(0, 0, 64, 64);
        HdrRedaction.PrivateBlocks(img, r, 8, seed: 99, (0, 0), clampWhite: 2.5f);

        float baseHue = Hue(baseR, baseG, baseB);
        float baseVal = Math.Max(baseR, Math.Max(baseG, baseB));   // V component of HSV, i.e. the max channel

        var hues = new List<float>();
        var vals = new List<float>();
        for (int by = 0; by < 64; by += 8) for (int bx = 0; bx < 64; bx += 8)
        {
            (float cr, float cg, float cb) = img.Sample(bx, by);
            hues.Add(Hue(cr, cg, cb));
            vals.Add(Math.Max(cr, Math.Max(cg, cb)));
        }

        Assert.True(hues.Distinct().Count() > 1, "hue should differ between at least two blocks");
        foreach (float h in hues) Assert.InRange(CircularDiff(h, baseHue), 0f, 6.5f);   // ±6° jitter, half-float slack
        foreach (float v in vals) Assert.InRange(v, baseVal * 0.87f, baseVal * 1.13f);   // ±12 % jitter, ±13 % slack
    }

    [Fact]
    public void Private_block_boundaries_match_the_sdr_filters_grid()
    {
        // The SDR and HDR files must show the same blocks: both filters share Redaction.GridStart and Redaction.Hash,
        // so the same rect/seed/origin/block must put boundaries at the same pixels.
        var r = new IntRect(0, 0, 64, 64);
        const int block = 8; const uint seed = 42; (int, int) origin = (0, 0);

        HalfImage halfOut = Gradient(64, 64, 6f);
        HdrRedaction.PrivateBlocks(halfOut, r, block, seed, origin, clampWhite: 1000f);

        BgraImage bgra = BgraGradient(64, 64);
        var bgraOut = new BgraImage(64, 64, (byte[])bgra.Data.Clone());
        Redaction.PrivateBlocks(bgra, bgraOut, r, block, seed, origin);

        static List<int> Transitions(Func<int, bool> equalToPrevious, int n)
        {
            var xs = new List<int>();
            for (int i = 1; i < n; i++) if (!equalToPrevious(i)) xs.Add(i);
            return xs;
        }
        List<int> halfRowXs = Transitions(x => halfOut.Sample(x - 1, 30) == halfOut.Sample(x, 30), 64);
        List<int> bgraRowXs = Transitions(x => PixelEqual(bgraOut, x - 1, 30, x, 30), 64);
        Assert.Equal(halfRowXs, bgraRowXs);
        Assert.Equal(new[] { 8, 16, 24, 32, 40, 48, 56 }, halfRowXs);   // sanity: boundaries actually exist, every 8 px from 0

        List<int> halfColYs = Transitions(y => halfOut.Sample(30, y - 1) == halfOut.Sample(30, y), 64);
        List<int> bgraColYs = Transitions(y => PixelEqual(bgraOut, 30, y - 1, 30, y), 64);
        Assert.Equal(halfColYs, bgraColYs);
        Assert.Equal(new[] { 8, 16, 24, 32, 40, 48, 56 }, halfColYs);
    }

    private static bool PixelEqual(BgraImage img, int x1, int y1, int x2, int y2)
    {
        int i1 = (y1 * img.Width + x1) * 4, i2 = (y2 * img.Width + x2) * 4;
        return img.Data[i1] == img.Data[i2] && img.Data[i1 + 1] == img.Data[i2 + 1] && img.Data[i1 + 2] == img.Data[i2 + 2];
    }

    [Fact]
    public void Apply_matches_the_sdr_block_grid_and_honours_the_flags()
    {
        HalfImage img = Gradient(64, 64, 3f);
        var viewport = new IntRect(100, 100, 64, 64);
        var pix = new RedactShape(1, new IntRect(93, 93, 30, 30), 4, Blur: false, Private: true, Seed: 7);   // starts 7 px before the viewport
        HalfImage d1 = Copy(img); HdrRedaction.Apply(pix, d1, viewport, 200f);
        // Block size 12 anchored at the shape's corner (93): boundaries in viewport-local x at 5, 17
        Assert.Equal(d1.Sample(0, 0), d1.Sample(4, 4));
        Assert.NotEqual(d1.Sample(4, 4), d1.Sample(5, 5));
        Assert.Equal(d1.Sample(5, 5), d1.Sample(16, 16));
        HalfImage d2 = Copy(img); HdrRedaction.Apply(pix, d2, viewport, 200f);
        Assert.Equal(d1.Data, d2.Data);   // deterministic
        var blur = new RedactShape(2, new IntRect(100, 100, 64, 64), 8, Blur: true, Private: false, Seed: 0);
        HalfImage d3 = Copy(img); HdrRedaction.Apply(blur, d3, viewport, 200f);
        Assert.NotEqual(img.Data, d3.Data);
        Assert.InRange(Math.Abs(d3.Sample(30, 30).R - d3.Sample(31, 30).R), 0f, 0.05f);
    }
}
