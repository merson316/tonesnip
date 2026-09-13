using ToneSnip.Core.Annotate;
using ToneSnip.Core.Geometry;
using Xunit;

namespace ToneSnip.Core.Tests.Annotate;

public class ShapeTests
{
    private static readonly Style S = new(0xFFFF0000, 4, 20);

    [Fact]
    public void Pen_bounds_pad_by_half_width_and_hit_tests_near_the_stroke()
    {
        var pen = new PenShape(1, new[] { (10, 10), (50, 10), (50, 40) }, 4, 0xFFFF0000, Highlighter: false);
        Assert.Equal(new IntRect(7, 7, 47, 37), pen.Bounds);     // 10-3 .. 50+3 inclusive => width 47
        Assert.True(pen.HitTest(30, 11));
        Assert.True(pen.HitTest(51, 25));
        Assert.False(pen.HitTest(30, 30));
    }

    [Fact]
    public void Line_hit_tests_within_tolerance_and_arrow_pads_for_the_head()
    {
        var line = new LineShape(2, 0, 0, 100, 0, 2, 0xFF00FF00, Arrow: false);
        Assert.True(line.HitTest(50, 3));
        Assert.False(line.HitTest(50, 9));
        var arrow = line with { Arrow = true };
        Assert.True(arrow.Bounds.Height >= 2 * 3 * 2);   // head is 3x width each side
    }

    [Fact]
    public void Box_hit_tests_the_outline_only_unless_filled()
    {
        var box = new BoxShape(3, new IntRect(10, 10, 100, 50), 4, 0xFF0000FF, Ellipse: false, Filled: false);
        Assert.True(box.HitTest(10, 30));
        Assert.False(box.HitTest(60, 35));
        Assert.True((box with { Filled = true }).HitTest(60, 35));
    }

    [Fact]
    public void Ellipse_hit_tests_the_ring()
    {
        var e = new BoxShape(4, new IntRect(0, 0, 100, 100), 4, 0xFF0000FF, Ellipse: true, Filled: false);
        Assert.True(e.HitTest(50, 2));      // top of ring
        Assert.False(e.HitTest(2, 2));      // corner is outside the ellipse
        Assert.False(e.HitTest(50, 50));    // centre is inside, not on the ring
    }

    [Fact]
    public void Text_and_counter_estimate_bounds_from_size()
    {
        var t = new TextShape(5, 20, 30, "Hello", 20, 0xFFFFFFFF);
        Assert.Equal(20, t.Bounds.Left); Assert.Equal(30, t.Bounds.Top);
        Assert.InRange(t.Bounds.Width, 40, 80); Assert.InRange(t.Bounds.Height, 20, 30);
        var c = new CounterShape(6, 100, 100, 3, 20, 0xFFFF0000);
        Assert.True(c.Bounds.Contains(100, 100));
        Assert.Equal(c.Bounds.Width, c.Bounds.Height);
    }

    [Fact]
    public void Moved_shifts_every_kind()
    {
        Shape[] all =
        {
            new PenShape(1, new[] { (0, 0), (5, 5) }, 2, 1, false),
            new LineShape(2, 0, 0, 5, 5, 2, 1, false),
            new BoxShape(3, new IntRect(0, 0, 5, 5), 2, 1, false, false),
            new TextShape(4, 0, 0, "x", 14, 1),
            new CounterShape(5, 0, 0, 1, 14, 1),
            new RedactShape(6, new IntRect(0, 0, 5, 5), 4, Blur: false, Private: true, Seed: 1),
        };
        foreach (Shape s in all) { Shape m = s.Moved(10, 20); Assert.Equal(s.Bounds.Offset(10, 20), m.Bounds); Assert.Equal(s.Id, m.Id); }
    }

    [Fact]
    public void Only_boxes_redactions_and_lines_resize()
    {
        Assert.NotNull(new BoxShape(1, new IntRect(0, 0, 10, 10), 2, 1, false, false).Resized(Handle.SE, 5, 5));
        Assert.NotNull(new RedactShape(2, new IntRect(0, 0, 10, 10), 4, false, true, 1).Resized(Handle.E, 5, 0));
        Assert.Equal(15, ((LineShape)new LineShape(3, 0, 0, 10, 10, 2, 1, false).Resized(Handle.End, 5, 0)!).X2);
        Assert.Null(new TextShape(4, 0, 0, "x", 14, 1).Resized(Handle.SE, 5, 5));
        Assert.Null(new PenShape(5, new[] { (0, 0) }, 2, 1, false).Resized(Handle.SE, 5, 5));
    }

    [Fact]
    public void Text_dirty_bounds_are_wider_than_its_hit_bounds_and_other_shapes_match()
    {
        var t = new TextShape(1, 20, 30, "WWWWWWW", 32, 1);
        Assert.True(t.DirtyBounds.Width > t.Bounds.Width); Assert.True(t.DirtyBounds.Height >= t.Bounds.Height);
        Assert.True(t.DirtyBounds.Left <= t.Bounds.Left && t.DirtyBounds.Top <= t.Bounds.Top);
        var b = new BoxShape(2, new IntRect(0, 0, 10, 10), 2, 1, false, false);
        Assert.Equal(b.Bounds, b.DirtyBounds);
    }

    [Fact]
    public void Style_tables_and_strengths()
    {
        Assert.Equal(9, Style.Palette.Length);
        Assert.Equal(new[] { 2, 4, 8, 12 }, Style.Widths);
        Assert.Equal(new[] { 14, 20, 28 }, Style.TextSizes);
        Assert.Equal(0xFFFF4A4Au, Style.Palette[0]);
        Assert.Equal(Style.AccentPlaceholder, Style.Palette[^1]);
        Assert.Equal(16, Style.BlurRadius(12)); Assert.Equal(24, Style.PixelBlock(12));
        Assert.Equal(8, Style.BlurRadius(4)); Assert.Equal(16, Style.BlurRadius(8));
        Assert.Equal(6, Style.PixelBlock(2)); Assert.Equal(24, Style.PixelBlock(8));
    }
}
