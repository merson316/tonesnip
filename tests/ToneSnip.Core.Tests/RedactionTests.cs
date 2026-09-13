using ToneSnip.Core.Annotate;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using Xunit;

namespace ToneSnip.Core.Tests;

public class RedactionTests
{
    private static BgraImage Gradient(int w, int h)   // B ramps with x, G with y, R constant 100
    {
        var img = BgraImage.Blank(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { int i = (y * w + x) * 4; img.Data[i] = (byte)(x * 255 / (w - 1)); img.Data[i + 1] = (byte)(y * 255 / (h - 1)); img.Data[i + 2] = 100; img.Data[i + 3] = 255; }
        return img;
    }
    private static (byte B, byte G, byte R) Px(BgraImage img, int x, int y) { int i = (y * img.Width + x) * 4; return (img.Data[i], img.Data[i + 1], img.Data[i + 2]); }

    [Fact]
    public void Classic_pixelate_averages_each_block_from_the_source()
    {
        BgraImage src = Gradient(64, 64), dst = BgraImage.Blank(64, 64);
        Redaction.Pixelate(src, dst, new IntRect(8, 8, 32, 32), 8);
        Assert.Equal(Px(dst, 8, 8), Px(dst, 15, 15));
        Assert.NotEqual(Px(dst, 8, 8), Px(dst, 16, 8));
        Assert.Equal(Redaction.Mean(src, new IntRect(8, 8, 8, 8)), Px(dst, 10, 10));
        Assert.Equal((0, 0, 0), Px(dst, 0, 0));   // outside the rect untouched
    }

    [Fact]
    public void Box_blur_flattens_a_gradient_and_stays_inside_the_rect()
    {
        BgraImage src = Gradient(64, 64), dst = BgraImage.Blank(64, 64);
        Redaction.BoxBlur(src, dst, new IntRect(16, 16, 32, 32), 8);
        (byte b1, _, _) = Px(dst, 20, 30); (byte b2, _, _) = Px(dst, 21, 30);
        Assert.InRange(Math.Abs(b1 - b2), 0, 2);            // neighbouring pixels nearly equal after blur
        Assert.InRange(Px(dst, 31, 31).B, 100, 150);        // still around the local mean
        Assert.Equal((0, 0, 0), Px(dst, 15, 31));           // untouched outside
    }

    [Fact]
    public void Private_blocks_depend_only_on_the_mean_and_seed()
    {
        BgraImage a = Gradient(64, 64), b = BgraImage.Blank(64, 64);
        (byte mb, byte mg, byte mr) = Redaction.Mean(a, new IntRect(0, 0, 64, 64));
        for (int i = 0; i < b.Data.Length; i += 4) { b.Data[i] = mb; b.Data[i + 1] = mg; b.Data[i + 2] = mr; b.Data[i + 3] = 255; }   // flat image with the same mean
        BgraImage da = BgraImage.Blank(64, 64), db = BgraImage.Blank(64, 64);
        var r = new IntRect(0, 0, 64, 64);
        Redaction.PrivateBlocks(a, da, r, 8, seed: 42);
        Redaction.PrivateBlocks(b, db, r, 8, seed: 42);
        Assert.Equal(da.Data, db.Data);
        BgraImage dc = BgraImage.Blank(64, 64);
        Redaction.PrivateBlocks(a, dc, r, 8, seed: 43);
        Assert.NotEqual(da.Data, dc.Data);
        Assert.NotEqual(Px(da, 0, 0), Px(da, 8, 0));   // blocks differ from each other (jitter)
        Assert.Equal(Px(da, 0, 0), Px(da, 7, 7));      // and are flat inside
    }

    [Fact]
    public void Private_blocks_keep_the_overall_tone()
    {
        BgraImage src = Gradient(64, 64), dst = BgraImage.Blank(64, 64);
        var r = new IntRect(0, 0, 64, 64);
        Redaction.PrivateBlocks(src, dst, r, 8, 7);
        (byte mb, byte mg, byte mr) = Redaction.Mean(src, r); (byte ob, byte og, byte orr) = Redaction.Mean(dst, r);
        Assert.InRange(Math.Abs(mb - ob), 0, 20); Assert.InRange(Math.Abs(mg - og), 0, 20); Assert.InRange(Math.Abs(mr - orr), 0, 20);
    }

    [Fact]
    public void The_block_grid_is_anchored_to_the_shape_not_to_the_viewport()
    {
        // A redaction spanning two monitors is rendered once per viewport. The grid must count from the shape's own
        // left edge, so every block that lies wholly inside one viewport comes out exactly as in a single full render.
        BgraImage src = Gradient(64, 32);
        var full = new IntRect(0, 0, 64, 32);
        var leftVp = new IntRect(0, 0, 32, 32);
        var rightVp = new IntRect(32, 0, 32, 32);
        var shape = new RedactShape(1, new IntRect(3, 0, 58, 32), 2, Blur: false, Private: false, Seed: 1);   // block 6, grid at 3, 9, 15 …
        BgraImage fullDst = BgraImage.Blank(64, 32), leftDst = BgraImage.Blank(32, 32), rightDst = BgraImage.Blank(32, 32);
        Redaction.Apply(shape, src, fullDst, full);
        Redaction.Apply(shape, src.Crop(leftVp), leftDst, leftVp);
        Redaction.Apply(shape, src.Crop(rightVp), rightDst, rightVp);

        for (int y = 0; y < 32; y++)
        {
            for (int x = 3; x < 27; x++) Assert.Equal(Px(fullDst, x, y), Px(leftDst, x, y));            // blocks 3-27, all inside the left half
            for (int x = 33; x < 61; x++) Assert.Equal(Px(fullDst, x, y), Px(rightDst, x - 32, y));     // blocks 33-61, all inside the right half
        }
        // Structure: the right half's first whole block starts at 33, not at its own left edge (32).
        Assert.NotEqual(Px(rightDst, 0, 0), Px(rightDst, 1, 0));
        Assert.Equal(Px(rightDst, 1, 0), Px(rightDst, 6, 0));
        Assert.NotEqual(Px(rightDst, 6, 0), Px(rightDst, 7, 0));
    }

    [Fact]
    public void Apply_honours_the_shape_flags_and_clamps_to_the_viewport()
    {
        BgraImage src = Gradient(64, 64), dst = BgraImage.Blank(64, 64);
        var viewport = new IntRect(100, 100, 64, 64);   // source frame offset
        var pix = new RedactShape(1, new IntRect(90, 90, 30, 30), 4, Blur: false, Private: false, Seed: 1);
        Redaction.Apply(pix, src, dst, viewport);
        Assert.NotEqual((0, 0, 0), Px(dst, 0, 0)); Assert.Equal((0, 0, 0), Px(dst, 20, 20));
        var privBlur = new RedactShape(2, new IntRect(100, 100, 64, 64), 8, Blur: true, Private: true, Seed: 9);
        BgraImage d2 = BgraImage.Blank(64, 64), d3 = BgraImage.Blank(64, 64);
        Redaction.Apply(privBlur, src, d2, viewport); Redaction.Apply(privBlur, src, d3, viewport);
        Assert.Equal(d2.Data, d3.Data);   // deterministic
        Assert.NotEqual(src.Data, d2.Data);
    }
}
