using ToneSnip.Core.Annotate;
using ToneSnip.Core.Color;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Hdr;
using ToneSnip.Core.Imaging;
using Xunit;

namespace ToneSnip.Core.Tests.Annotate;

public class EmphasisTests
{
    private static BgraImage Grey(int w, int h, byte v)
    {
        var img = BgraImage.Blank(w, h);
        for (int i = 0; i < img.Data.Length; i += 4) { img.Data[i] = img.Data[i + 1] = img.Data[i + 2] = v; img.Data[i + 3] = 255; }
        return img;
    }
    private static byte B(BgraImage img, int x, int y) => img.Data[(y * img.Width + x) * 4];

    [Fact]
    public void Spotlight_dims_outside_the_union_of_its_rectangles_and_leaves_alpha()
    {
        BgraImage img = Grey(40, 20, 200);
        var shapes = new Shape[] { new SpotlightShape(1, new IntRect(2, 2, 6, 6)), new SpotlightShape(2, new IntRect(6, 4, 10, 4)) };
        Emphasis.Spotlight(shapes, img, new IntRect(0, 0, 40, 20), new IntRect(0, 0, 40, 20));
        Assert.Equal(200, B(img, 3, 3));     // in the first
        Assert.Equal(200, B(img, 12, 5));    // in the second only
        Assert.Equal(Emphasis.Dim(200), B(img, 30, 10));
        Assert.Equal(Emphasis.Dim(200), B(img, 12, 2));   // above the second, beside the first
        Assert.Equal(255, img.Data[(10 * 40 + 30) * 4 + 3]);
        Assert.True(Emphasis.Dim(200) < 120);
    }

    [Fact]
    public void Without_a_spotlight_nothing_is_dimmed()
    {
        BgraImage img = Grey(8, 8, 90);
        Emphasis.Spotlight(new Shape[] { new BoxShape(1, new IntRect(0, 0, 4, 4), 2, 1, false, false) }, img, new IntRect(0, 0, 8, 8), new IntRect(0, 0, 8, 8));
        Assert.All(Enumerable.Range(0, 64), i => Assert.Equal(90, img.Data[i * 4]));
    }

    [Fact]
    public void A_partial_repaint_dims_exactly_what_a_full_one_does()
    {
        var shapes = new Shape[] { new SpotlightShape(1, new IntRect(110, 60, 30, 20)) };
        var viewport = new IntRect(100, 50, 64, 48);   // shapes are in source pixels; the image is the viewport
        BgraImage full = Grey(64, 48, 180), part = Grey(64, 48, 180);
        Emphasis.Spotlight(shapes, full, viewport, new IntRect(0, 0, 64, 48));
        Emphasis.Spotlight(shapes, part, viewport, new IntRect(0, 0, 64, 24));
        Emphasis.Spotlight(shapes, part, viewport, new IntRect(0, 24, 64, 24));
        Assert.Equal(full.Data, part.Data);
        Assert.Equal(180, B(full, 15, 15));     // source (115, 65) is lit
        Assert.Equal(Emphasis.Dim(180), B(full, 5, 5));
    }

    [Fact]
    public void The_sdr_and_hdr_dims_agree_in_linear_light()
    {
        // The HDR canvas lifts an sRGB byte to linear light; dimming it there must land where lifting the dimmed SDR
        // byte does, so the gain map and the two files agree.
        foreach (byte v in new byte[] { 16, 64, 128, 200, 255 })
        {
            ColorMath.LiftSrgb(v, v, v, 1f, out float lin, out _, out _);
            var half = new HalfImage(1, 1);
            half.Data[0] = half.Data[1] = half.Data[2] = Transfer.FloatToHalf(lin); half.Data[3] = Transfer.FloatToHalf(1f);
            Emphasis.Spotlight(new Shape[] { new SpotlightShape(1, new IntRect(5, 5, 1, 1)) }, half, new IntRect(0, 0, 1, 1));
            byte d = Emphasis.Dim(v);
            ColorMath.LiftSrgb(d, d, d, 1f, out float sdr, out _, out _);
            Assert.Equal(sdr, Transfer.HalfToFloat(half.Data[0]), 0.004f);
        }
    }

    [Fact]
    public void Only_the_first_and_last_spotlight_repaint_everything_and_a_move_repaints_its_old_and_new_areas()
    {
        var d = new AnnotationDoc();
        var seen = new List<IntRect>();
        d.Changed += seen.Add;
        d.Add(new SpotlightShape(d.NewId(), new IntRect(10, 10, 20, 20)));     // the first: everything darkens
        Assert.True(Assert.Single(seen).IsEmpty);
        seen.Clear();
        d.Add(new SpotlightShape(d.NewId(), new IntRect(200, 10, 20, 20)));    // a second: only its own rectangle
        Assert.Equal(new IntRect(200, 10, 20, 20), Assert.Single(seen));
        seen.Clear();
        d.Replace(new SpotlightShape(1, new IntRect(100, 100, 20, 20)));       // moved far: both ends, not the box round them
        Assert.Equal(2, seen.Count);
        Assert.Contains(new IntRect(10, 10, 20, 20), seen); Assert.Contains(new IntRect(100, 100, 20, 20), seen);
        seen.Clear();
        d.Remove(2);
        Assert.Equal(new IntRect(200, 10, 20, 20), Assert.Single(seen));
        seen.Clear();
        d.Remove(1);                                                           // the last: everything brightens
        Assert.True(Assert.Single(seen).IsEmpty);
    }

    [Fact]
    public void A_spotlight_in_hand_repaints_everything_only_when_it_is_the_first()
    {
        var s = new EditSession(new AnnotationDoc()) { Tool = Tool.Spotlight };
        var seen = new List<IntRect>();
        s.Changed += seen.Add;
        s.Begin(10, 10, InputMods.None); s.Move(50, 40, InputMods.None);
        Assert.True(seen[0].IsEmpty);                     // the press already holds a spotlight
        Assert.Single(seen, r => r.IsEmpty);
        seen.Clear();
        s.Move(60, 45, InputMods.None);
        Assert.DoesNotContain(seen, r => r.IsEmpty);
        s.End(60, 45, InputMods.None);
        seen.Clear();
        s.Begin(200, 200, InputMods.None); s.Move(260, 240, InputMods.None);   // a second: its own area
        Assert.DoesNotContain(seen, r => r.IsEmpty);
    }

    [Fact]
    public void Spotlight_tool_drags_a_rectangle_with_handles()
    {
        var s = new EditSession(new AnnotationDoc()) { Tool = Tool.Spotlight };
        s.Begin(40, 30, InputMods.None); s.Move(10, 10, InputMods.None);
        Assert.IsType<SpotlightShape>(s.InProgress);
        s.End(10, 10, InputMods.None);
        var sp = Assert.IsType<SpotlightShape>(Assert.Single(s.Doc.Shapes));
        Assert.Equal(IntRect.FromDrag(40, 30, 10, 10), sp.Rect);
        Assert.Equal(8, Handles.Of(sp).Count);
        Assert.IsType<SpotlightShape>(sp.Resized(Handle.SE, 5, 5));
        s.Begin(0, 0, InputMods.None); s.End(2, 2, InputMods.None);   // a click makes nothing
        Assert.Single(s.Doc.Shapes);
    }
}
