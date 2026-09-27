using ToneSnip.Core.Annotate;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using Xunit;

namespace ToneSnip.Core.Tests.Annotate;

public class MagnifierTests
{
    private static MagnifierShape Mag(IntRect source, int lx, int ly) => new(1, source, lx, ly, 4, 0xFFFF0000);

    [Fact]
    public void The_lens_is_the_source_doubled_about_its_centre()
    {
        MagnifierShape m = Mag(new IntRect(10, 10, 20, 15), 100, 50);
        Assert.Equal(new IntRect(80, 35, 40, 30), m.Lens);
        Assert.True(m.Bounds.Left < 10 && m.Bounds.Right > 120);
    }

    /// <summary>The overlay renders each monitor from its own frame, so a lens dragged onto the next monitor showed
    /// nothing: the drag keeps it on its source's monitor, pressed against the edge.</summary>
    [Fact]
    public void A_dragged_lens_stays_inside_its_sources_area()
    {
        var left = new IntRect(0, 0, 400, 300);
        var s = new EditSession(new AnnotationDoc()) { Tool = Tool.Magnifier, LensArea = src => src.Left < 400 ? left : new IntRect(400, 0, 400, 300) };
        s.Begin(50, 50, InputMods.None); s.Move(90, 80, InputMods.None); s.End(90, 80, InputMods.None);
        var placed = Assert.IsType<MagnifierShape>(Assert.Single(s.Doc.Shapes));
        s.Tool = Tool.Select;
        (int cx, int cy) = (placed.Lens.Left + placed.Lens.Width / 2, placed.Lens.Top + placed.Lens.Height / 2);
        Assert.True(s.Begin(cx, cy, InputMods.None));            // a press in the lens drags the lens alone
        s.Move(cx + 500, cy, InputMods.None);
        s.End(cx + 500, cy, InputMods.None);
        var dragged = Assert.IsType<MagnifierShape>(Assert.Single(s.Doc.Shapes));
        Assert.Equal(placed.Source, dragged.Source);
        Assert.Equal(400, dragged.Lens.Right);
        Assert.Equal(placed.Lens.Top, dragged.Lens.Top);
    }

    [Fact]
    public void A_new_lens_goes_right_then_left_then_below_then_above_and_stays_inside_the_area()
    {
        var area = new IntRect(0, 0, 400, 300);
        var source = new IntRect(50, 50, 40, 30);
        (int x, int y) = MagnifierShape.PlaceLens(source, area);
        MagnifierShape right = Mag(source, x, y);
        Assert.True(right.Lens.Left > source.Right && right.Lens.Top < source.Bottom);
        // Against the right edge: left of it.
        var nearRight = new IntRect(330, 50, 40, 30);
        (x, y) = MagnifierShape.PlaceLens(nearRight, area);
        Assert.True(Mag(nearRight, x, y).Lens.Right < nearRight.Left);
        // Too wide for either side: below.
        var wide = new IntRect(100, 20, 150, 30);
        (x, y) = MagnifierShape.PlaceLens(wide, area);
        Assert.True(Mag(wide, x, y).Lens.Top > wide.Bottom);
        // Nowhere fits: pulled inside the area.
        var huge = new IntRect(100, 100, 200, 150);
        (x, y) = MagnifierShape.PlaceLens(huge, area);
        IntRect lens = Mag(huge, x, y).Lens;
        Assert.True(lens.Width > area.Width || (lens.Left >= 0 && lens.Right <= 400));
        foreach (IntRect s in new[] { source, nearRight, wide })
        {
            (x, y) = MagnifierShape.PlaceLens(s, area);
            IntRect l = Mag(s, x, y).Lens;
            Assert.Equal(l, l.Intersect(area));
            Assert.False(l.IntersectsWith(s));
        }
    }

    [Fact]
    public void The_connector_runs_from_the_source_edge_to_the_lens_edge_and_vanishes_when_they_overlap()
    {
        MagnifierShape m = Mag(new IntRect(0, 0, 20, 20), 100, 10);   // lens 40x40 at (80, -10)
        var ((x1, y1), (x2, y2)) = m.Connector()!.Value;
        Assert.Equal((20, 10), (x1, y1));
        Assert.Equal((80, 10), (x2, y2));
        Assert.True(m.HitTest(50, 10));
        Assert.False(m.HitTest(50, 40));
        Assert.Null(Mag(new IntRect(0, 0, 20, 20), 15, 15).Connector());
    }

    [Fact]
    public void Moving_moves_both_the_lens_handle_moves_the_lens_alone_and_the_corners_resize_the_source()
    {
        MagnifierShape m = Mag(new IntRect(10, 10, 20, 20), 100, 20);
        var moved = (MagnifierShape)m.Moved(5, 7);
        Assert.Equal(new IntRect(15, 17, 20, 20), moved.Source); Assert.Equal((105, 27), (moved.LensX, moved.LensY));
        var lens = (MagnifierShape)m.Resized(Handle.End, 30, -5)!;
        Assert.Equal(m.Source, lens.Source); Assert.Equal((130, 15), (lens.LensX, lens.LensY));
        var grown = (MagnifierShape)m.Resized(Handle.SE, 10, 0)!;
        Assert.Equal(new IntRect(10, 10, 30, 20), grown.Source); Assert.Equal(60, grown.Lens.Width);
    }

    [Fact]
    public void Dragging_makes_one_and_a_press_in_the_lens_moves_only_the_lens()
    {
        var s = new EditSession(new AnnotationDoc()) { Tool = Tool.Magnifier, LensArea = _ => new IntRect(0, 0, 500, 400) };
        s.Doc.Current = new Style(0xFF00FF00, 4, 20);
        s.Begin(20, 20, InputMods.None); s.Move(60, 50, InputMods.None);
        Assert.IsType<MagnifierShape>(s.InProgress);
        s.End(60, 50, InputMods.None);
        var m = Assert.IsType<MagnifierShape>(Assert.Single(s.Doc.Shapes));
        Assert.Equal(IntRect.FromDrag(20, 20, 60, 50), m.Source);
        Assert.Equal(0xFF00FF00u, m.Color);
        s.Tool = Tool.Select;
        IntRect lens = m.Lens;
        int px = lens.Left + lens.Width / 2, py = lens.Top + lens.Height / 2;
        Assert.True(s.Begin(px, py, InputMods.None));
        s.Move(px + 10, py + 20, InputMods.None); s.End(px + 10, py + 20, InputMods.None);
        var after = Assert.IsType<MagnifierShape>(Assert.Single(s.Doc.Shapes));
        Assert.Equal(m.Source, after.Source);
        Assert.Equal((m.LensX + 10, m.LensY + 20), (after.LensX, after.LensY));
        Assert.True(s.Doc.Undo());
        Assert.Equal(m, s.Doc.Shapes[0]);
        s.Tool = Tool.Magnifier;
        s.Begin(0, 0, InputMods.None); s.End(2, 2, InputMods.None);   // a click makes nothing
        Assert.Single(s.Doc.Shapes);
    }

    [Fact]
    public void The_lens_doubles_its_source_pixels_inside_its_rounded_corners()
    {
        const int w = 100, h = 60;
        var img = BgraImage.Blank(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { int i = (y * w + x) * 4; img.Data[i] = (byte)x; img.Data[i + 1] = (byte)y; img.Data[i + 3] = 255; }
        MagnifierShape m = Mag(new IntRect(10, 10, 16, 16), 60, 30);   // lens 32x32 at (44, 14)
        var viewport = new IntRect(0, 0, w, h);
        var tiles = Emphasis.ReadLenses(new Shape[] { m }, img.Data, w, viewport, viewport);
        Emphasis.WriteLenses(tiles, img.Data, w);
        int At(int x, int y, int c) => img.Data[(y * w + x) * 4 + c];
        Assert.Equal((10 + 8, 10 + 8), (At(60, 30, 0), At(60, 30, 1)));   // middle: source (18, 18)
        Assert.Equal((10 + 8, 10 + 8), (At(61, 31, 0), At(61, 31, 1)));   // whole-pixel doubling
        Assert.Equal((10 + 15, 10 + 15), (At(74, 44, 0), At(74, 44, 1))); // far edge, still inside
        Assert.Equal((44, 14), (At(44, 14, 0), At(44, 14, 1)));           // the rounded corner keeps what was there
        Assert.False(Emphasis.InRounded(m.Lens, m.LensRadius, 44, 14));
        Assert.True(Emphasis.InRounded(m.Lens, m.LensRadius, 60, 14));
    }

    [Fact]
    public void Part_of_a_lens_reads_the_same_pixels_as_the_whole_of_it()
    {
        const int w = 100, h = 60;
        var img = BgraImage.Blank(w, h);
        for (int i = 0; i < img.Data.Length; i++) img.Data[i] = (byte)(i * 7);
        MagnifierShape m = Mag(new IntRect(10, 10, 16, 16), 60, 30) with { Zoom = 2.5f };
        var viewport = new IntRect(0, 0, w, h);
        BgraImage whole = BgraImage.Blank(w, h), parts = BgraImage.Blank(w, h);
        Emphasis.WriteLenses(Emphasis.ReadLenses(new Shape[] { m }, img.Data, w, viewport, viewport), whole.Data, w);
        for (int y = 0; y < h; y += 7)
            Emphasis.WriteLenses(Emphasis.ReadLenses(new Shape[] { m }, img.Data, w, viewport, new IntRect(0, y, w, 7)), parts.Data, w);
        Assert.Equal(whole.Data, parts.Data);
        Assert.Null(Emphasis.ReadLenses(new Shape[] { m }, img.Data, w, viewport, new IntRect(0, 0, 5, 5)));
    }

    [Fact]
    public void A_lens_corner_changes_the_zoom_with_the_source_and_opposite_corner_fixed()
    {
        MagnifierShape m = Mag(new IntRect(10, 10, 20, 10), 200, 100);   // lens 40x20 at (180, 90)
        var bigger = (MagnifierShape)m.Resized(Handle.LensSE, 40, 0)!;   // right edge 220 -> 260: 80 wide
        Assert.Equal(4f, bigger.Zoom);
        Assert.Equal(m.Source, bigger.Source);
        Assert.Equal(new IntRect(180, 90, 80, 40), bigger.Lens);          // top-left stays, shape kept
        var smaller = (MagnifierShape)m.Resized(Handle.LensNW, 30, 30)!;  // clamped at the least zoom
        Assert.Equal(MagnifierShape.MinZoom, smaller.Zoom);
        Assert.Equal(m.Lens.Right, smaller.Lens.Right); Assert.Equal(m.Lens.Bottom, smaller.Lens.Bottom);
        Assert.Equal(MagnifierShape.MaxZoom, ((MagnifierShape)m.Resized(Handle.LensSE, 1000, 1000)!).Zoom);
        Assert.Equal(12, Handles.Of(m).Count);
        Assert.Equal(Handle.LensSE, Handles.Hit(m, m.Lens.Right - 1, m.Lens.Bottom - 1, EditSession.HandleSize));
    }

    [Fact]
    public void A_lens_is_kept_inside_its_area_zoom_first_then_position()
    {
        var area = new IntRect(0, 0, 300, 200);
        MagnifierShape m = Mag(new IntRect(10, 10, 20, 20), 290, 100);
        MagnifierShape moved = m.ClampedTo(area);
        Assert.Equal(moved.Lens, moved.Lens.Intersect(area)); Assert.Equal(m.Zoom, moved.Zoom);
        MagnifierShape huge = (Mag(new IntRect(10, 10, 50, 50), 150, 100) with { Zoom = 8f }).ClampedTo(area);
        Assert.Equal(4f, huge.Zoom);   // 200 / 50
        Assert.Equal(huge.Lens, huge.Lens.Intersect(area));
        MagnifierShape anywhere = m with { LensX = 5000 };
        Assert.Same(anywhere, anywhere.ClampedTo(IntRect.Empty));
    }

    [Fact]
    public void A_far_lens_invalidates_its_parts_not_the_picture_between()
    {
        MagnifierShape m = Mag(new IntRect(0, 0, 20, 20), 1000, 800);
        List<IntRect> parts = DirtyRegion.Of(m, m.Moved(5, 0));
        long area = parts.Sum(r => (long)r.Width * r.Height);
        Assert.True(area < m.Bounds.Width * (long)m.Bounds.Height / 5, $"{parts.Count} parts cover {area} px");
        foreach (IntRect r in new[] { m.Source, m.Lens }) Assert.Contains(parts, p => p.Intersect(r) == r);
    }

    [Fact]
    public void Dragging_a_lens_corner_in_the_session_zooms_and_undoes()
    {
        var s = new EditSession(new AnnotationDoc()) { Tool = Tool.Select, LensArea = _ => new IntRect(0, 0, 1000, 1000) };
        MagnifierShape m = Mag(new IntRect(10, 10, 20, 20), 200, 100);
        s.Doc.Add(m);
        s.Doc.Select(m.Id);
        (int x, int y) = (m.Lens.Right - 1, m.Lens.Bottom - 1);
        Assert.True(s.Begin(x, y, InputMods.None));
        s.Move(x + 20, y + 20, InputMods.None); s.End(x + 20, y + 20, InputMods.None);
        var zoomed = Assert.IsType<MagnifierShape>(Assert.Single(s.Doc.Shapes));
        Assert.Equal(3f, zoomed.Zoom);
        Assert.True(s.Doc.Undo());
        Assert.Equal(2f, ((MagnifierShape)s.Doc.Shapes[0]).Zoom);
    }
}
