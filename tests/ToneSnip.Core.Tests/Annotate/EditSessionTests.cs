using ToneSnip.Core.Annotate;
using ToneSnip.Core.Geometry;
using Xunit;

namespace ToneSnip.Core.Tests.Annotate;

public class EditSessionTests
{
    private static EditSession NewSession(Tool t) { var s = new EditSession(new AnnotationDoc()) { Tool = t }; s.Doc.Current = new Style(0xFFFF0000, 4, 20); return s; }

    [Fact]
    public void Pen_drag_adds_one_shape_with_all_points_on_end()
    {
        EditSession s = NewSession(Tool.Pen);
        Assert.True(s.Begin(10, 10, InputMods.None));
        s.Move(20, 10, InputMods.None); s.Move(20, 20, InputMods.None);
        Assert.NotNull(s.InProgress); Assert.Empty(s.Doc.Shapes);
        s.End(20, 20, InputMods.None);
        var pen = Assert.IsType<PenShape>(Assert.Single(s.Doc.Shapes));
        Assert.Equal(3, pen.Points.Count); Assert.False(pen.Highlighter); Assert.Null(s.InProgress);
        Assert.True(s.Doc.HasEdits);
    }

    [Fact]
    public void Selecting_a_pen_stroke_and_letting_go_where_it_started_is_not_an_edit()
    {
        // A pen stroke's Moved(0, 0) is a new points array that does not Equal the original.
        EditSession s = NewSession(Tool.Pen);
        s.Begin(10, 10, InputMods.None); s.Move(40, 10, InputMods.None); s.End(40, 10, InputMods.None);
        s.Doc.MarkSaved(s.Doc.Revision);
        s.Tool = Tool.Select;
        Assert.True(s.Begin(20, 10, InputMods.None));
        s.Move(25, 10, InputMods.None); s.Move(20, 10, InputMods.None);
        s.End(20, 10, InputMods.None);
        Assert.False(s.Doc.HasEdits);
    }

    [Fact]
    public void Highlighter_sets_the_flag_and_click_without_drag_makes_a_dot()
    {
        EditSession s = NewSession(Tool.Highlighter);
        s.Begin(5, 5, InputMods.None); s.End(5, 5, InputMods.None);
        var pen = Assert.IsType<PenShape>(Assert.Single(s.Doc.Shapes));
        Assert.True(pen.Highlighter); Assert.Single(pen.Points);
    }

    [Fact]
    public void Rect_and_ellipse_drag_in_any_direction_and_shift_makes_squares()
    {
        EditSession s = NewSession(Tool.Rect);
        s.Begin(50, 50, InputMods.None); s.Move(10, 30, InputMods.None); s.End(10, 30, InputMods.None);
        var box = Assert.IsType<BoxShape>(s.Doc.Shapes[0]);
        Assert.Equal(IntRect.FromDrag(50, 50, 10, 30), box.Rect); Assert.False(box.Ellipse);
        s.Tool = Tool.Ellipse;
        s.Begin(0, 0, InputMods.Shift); s.Move(40, 10, InputMods.Shift); s.End(40, 10, InputMods.Shift);
        var e = Assert.IsType<BoxShape>(s.Doc.Shapes[1]);
        Assert.True(e.Ellipse); Assert.Equal(e.Rect.Width, e.Rect.Height);
    }

    [Fact]
    public void Tiny_drags_of_boxes_and_lines_are_discarded()
    {
        EditSession s = NewSession(Tool.Rect);
        s.Begin(0, 0, InputMods.None); s.End(1, 1, InputMods.None);
        s.Tool = Tool.Line;
        s.Begin(0, 0, InputMods.None); s.End(2, 0, InputMods.None);
        Assert.Empty(s.Doc.Shapes);
    }

    [Fact]
    public void Line_arrow_and_shift_snapping()
    {
        EditSession s = NewSession(Tool.Arrow);
        s.Begin(0, 0, InputMods.Shift); s.Move(100, 30, InputMods.Shift); s.End(100, 30, InputMods.Shift);
        var a = Assert.IsType<LineShape>(s.Doc.Shapes[0]);
        Assert.True(a.Arrow); Assert.Equal(0, a.Y2);   // snapped to horizontal
        s.Tool = Tool.Line;
        s.Begin(0, 0, InputMods.Shift); s.Move(50, 45, InputMods.Shift); s.End(50, 45, InputMods.Shift);
        var l = Assert.IsType<LineShape>(s.Doc.Shapes[1]);
        Assert.False(l.Arrow); Assert.Equal(l.X2, l.Y2);   // snapped to 45°
    }

    [Fact]
    public void Text_tool_requests_input_then_commits_or_cancels()
    {
        EditSession s = NewSession(Tool.Text);
        bool asked = false; s.TextRequested += () => asked = true;
        s.Begin(30, 40, InputMods.None); s.End(30, 40, InputMods.None);
        Assert.True(asked); Assert.Equal((30, 40), s.PendingText);
        s.CommitText("");
        Assert.Empty(s.Doc.Shapes); Assert.Null(s.PendingText);
        s.Begin(30, 40, InputMods.None); s.End(30, 40, InputMods.None);
        s.CommitText("hi");
        var t = Assert.IsType<TextShape>(Assert.Single(s.Doc.Shapes));
        Assert.Equal("hi", t.Text); Assert.Equal(20, t.Size);
    }

    [Fact]
    public void Counter_click_numbers_up_and_redactions_capture_privacy_mode()
    {
        EditSession s = NewSession(Tool.Counter);
        s.Begin(1, 1, InputMods.None); s.End(1, 1, InputMods.None);
        s.Begin(9, 9, InputMods.None); s.End(9, 9, InputMods.None);
        Assert.Equal(2, ((CounterShape)s.Doc.Shapes[1]).Number);
        s.Tool = Tool.Blur; s.Doc.Private = false;
        s.Begin(0, 0, InputMods.None); s.Move(30, 30, InputMods.None); s.End(30, 30, InputMods.None);
        s.Tool = Tool.Pixelate; s.Doc.Private = true;
        s.Begin(0, 0, InputMods.None); s.Move(30, 30, InputMods.None); s.End(30, 30, InputMods.None);
        var blur = Assert.IsType<RedactShape>(s.Doc.Shapes[2]); var pix = Assert.IsType<RedactShape>(s.Doc.Shapes[3]);
        Assert.True(blur.Blur); Assert.False(blur.Private); Assert.Equal(4, blur.Strength);
        Assert.False(pix.Blur); Assert.True(pix.Private); Assert.NotEqual(blur.Seed, pix.Seed);
    }

    [Fact]
    public void Select_tool_picks_moves_resizes_and_deletes()
    {
        EditSession s = NewSession(Tool.Rect);
        s.Begin(10, 10, InputMods.None); s.Move(60, 40, InputMods.None); s.End(60, 40, InputMods.None);
        s.Tool = Tool.Select;
        Assert.False(s.Begin(200, 200, InputMods.None));   // nothing there: clears selection, not consumed as a drag
        Assert.True(s.Begin(35, 10, InputMods.None));       // on the outline
        Assert.Equal(1, s.Doc.SelectedId);
        s.Move(45, 20, InputMods.None); s.End(45, 20, InputMods.None);
        Assert.Equal(new IntRect(20, 20, 51, 31), ((BoxShape)s.Doc.Shapes[0]).Rect);
        Assert.Equal(Handle.SE, s.HandleAt(70, 50));
        Assert.True(s.Begin(70, 50, InputMods.None));
        s.Move(80, 60, InputMods.None); s.End(80, 60, InputMods.None);
        Assert.Equal(new IntRect(20, 20, 61, 41), ((BoxShape)s.Doc.Shapes[0]).Rect);
        Assert.True(s.DeleteSelected()); Assert.Empty(s.Doc.Shapes);
        Assert.False(s.DeleteSelected());
    }

    [Fact]
    public void Select_drag_accumulates_across_many_moves()
    {
        EditSession s = NewSession(Tool.Rect);
        s.Begin(0, 0, InputMods.None); s.Move(50, 50, InputMods.None); s.End(50, 50, InputMods.None);
        s.Tool = Tool.Select;
        Assert.True(s.Begin(25, 0, InputMods.None));
        s.Move(30, 2, InputMods.None); s.Move(35, 5, InputMods.None); s.Move(45, 10, InputMods.None);
        s.End(45, 10, InputMods.None);
        Assert.Equal(new IntRect(20, 10, 51, 51), ((BoxShape)s.Doc.Shapes[0]).Rect);
        Assert.True(s.Begin(70, 60, InputMods.None));   // SE handle of the moved box (right=70, bottom=60)
        s.Move(75, 62, InputMods.None); s.Move(80, 70, InputMods.None);
        s.End(80, 70, InputMods.None);
        Assert.Equal(new IntRect(20, 10, 61, 61), ((BoxShape)s.Doc.Shapes[0]).Rect);
    }

    [Fact]
    public void Style_setter_restyles_the_selection_as_an_edit()
    {
        EditSession s = NewSession(Tool.Rect);
        s.Begin(0, 0, InputMods.None); s.Move(50, 50, InputMods.None); s.End(50, 50, InputMods.None);
        s.Tool = Tool.Select; s.Begin(25, 0, InputMods.None); s.End(25, 0, InputMods.None);
        s.Style = new Style(0xFF00FF00, 8, 32);
        Assert.Equal(0xFF00FF00u, ((BoxShape)s.Doc.Shapes[0]).Color); Assert.Equal(8, ((BoxShape)s.Doc.Shapes[0]).Width);
        Assert.True(s.Doc.Undo()); Assert.Equal(0xFFFF0000u, ((BoxShape)s.Doc.Shapes[0]).Color);
    }

    [Fact]
    public void Crop_tool_draws_a_marquee_and_apply_sets_the_doc_crop()
    {
        EditSession s = NewSession(Tool.Crop);
        s.Begin(10, 10, InputMods.None); s.Move(110, 60, InputMods.None); s.End(110, 60, InputMods.None);
        Assert.Equal(IntRect.FromDrag(10, 10, 110, 60), s.CropMarquee);
        Assert.True(s.Doc.Crop.IsEmpty);
        s.ApplyCrop();
        Assert.Equal(IntRect.FromDrag(10, 10, 110, 60), s.Doc.Crop); Assert.True(s.CropMarquee.IsEmpty);
        Assert.Equal(Tool.Select, s.Tool);
    }

    [Fact]
    public void A_second_crop_dragged_past_the_first_stays_inside_it()
    {
        // The captured pointer can drag past the canvas edge; the new crop must not restore pixels the first removed.
        EditSession s = NewSession(Tool.Crop);
        s.Begin(100, 100, InputMods.None); s.Move(300, 200, InputMods.None); s.End(300, 200, InputMods.None); s.ApplyCrop();
        IntRect first = s.Doc.Crop;
        Assert.False(first.IsEmpty);
        s.Tool = Tool.Crop;
        s.Begin(150, 120, InputMods.None); s.Move(900, 900, InputMods.None); s.End(900, 900, InputMods.None);
        s.ApplyCrop();
        Assert.Equal(IntRect.FromLtrb(150, 120, first.Right, first.Bottom), s.Doc.Crop);
        Assert.Equal(first, first.Union(s.Doc.Crop));
    }

    [Fact]
    public void Picking_the_style_a_selected_shape_already_has_adds_no_undo_step()
    {
        EditSession s = NewSession(Tool.Rect);
        s.Begin(0, 0, InputMods.None); s.Move(50, 50, InputMods.None); s.End(50, 50, InputMods.None);
        s.Tool = Tool.Select; s.Begin(25, 0, InputMods.None); s.End(25, 0, InputMods.None);
        Assert.NotNull(s.Doc.Selected);
        s.Doc.MarkSaved();
        s.Style = s.Doc.Current;
        Assert.False(s.Doc.HasEdits);
    }

    [Fact]
    public void Escape_unwinds_one_layer_at_a_time()
    {
        EditSession s = NewSession(Tool.Text);
        s.Begin(0, 0, InputMods.None); s.End(0, 0, InputMods.None);
        Assert.True(s.Escape()); Assert.Null(s.PendingText);
        s.Tool = Tool.Rect; s.Begin(0, 0, InputMods.None); s.Move(50, 50, InputMods.None); s.End(50, 50, InputMods.None);
        s.Tool = Tool.Select; s.Begin(25, 0, InputMods.None); s.End(25, 0, InputMods.None);
        Assert.NotNull(s.Doc.Selected);
        Assert.True(s.Escape()); Assert.Null(s.Doc.Selected);
        Assert.False(s.Escape());
    }

    [Fact]
    public void Cancel_drag_drops_the_stroke_so_a_later_end_adds_nothing()
    {
        EditSession s = NewSession(Tool.Rect);
        s.Begin(10, 10, InputMods.None); s.Move(60, 40, InputMods.None);
        Assert.True(s.Busy); Assert.NotNull(s.InProgress);
        s.CancelDrag();
        Assert.False(s.Busy); Assert.Null(s.InProgress);
        s.End(60, 40, InputMods.None);
        Assert.Empty(s.Doc.Shapes);
    }
}
