using ToneSnip.Core.Geometry;
using Xunit;

namespace ToneSnip.Core.Tests;

public class WindowLayersTests
{
    // A browser-like window: a 1200x800 frame whose client area is all of it, a compositing layer over the whole
    // client area, and the page under 120 px of tabs and toolbar, with a scroll bar child of its own.
    private static readonly IntRect Frame = new(100, 100, 1200, 800);
    private static readonly IntRect Page = IntRect.FromLtrb(100, 220, 1300, 900);
    private static readonly IntRect ScrollBar = IntRect.FromLtrb(1280, 220, 1300, 900);

    private static WindowLayers Browser() => new(Frame, Frame, new[]
    {
        new ChildWindow(Frame, Frame, -1),          // a full-size layer, listed first and so "in front" of the page
        new ChildWindow(Page, Page, -1),
        new ChildWindow(ScrollBar, ScrollBar, 1),
    });

    private static List<IntRect> At(WindowLayers layers, int x, int y)
    {
        var list = new List<IntRect>();
        layers.At(x, y, list);
        return list;
    }

    [Fact]
    public void The_page_is_picked_under_the_pointer_with_the_whole_window_one_step_out()
        => Assert.Equal(new[] { Page, Frame }, At(Browser(), 600, 500));

    [Fact]
    public void The_toolbar_has_no_child_of_its_own_so_it_gives_the_whole_window()
        => Assert.Equal(new[] { Frame }, At(Browser(), 600, 150));

    [Fact]
    public void A_nested_child_comes_first_then_each_parent_out_to_the_frame()
        => Assert.Equal(new[] { ScrollBar, Page, Frame }, At(Browser(), 1290, 500));

    [Fact]
    public void A_layer_one_border_inside_the_frame_is_not_a_step_of_its_own()
    {
        // Chrome as Windows reports it: the frame, a client area inside its 1 px border, a compositing layer filling
        // the client area, and the page under the toolbar.
        IntRect frame = IntRect.FromLtrb(1146, 0, 3440, 1440), client = IntRect.FromLtrb(1147, 0, 3439, 1439);
        IntRect page = IntRect.FromLtrb(1147, 121, 3439, 1439);
        var layers = new WindowLayers(frame, client, new[]
        {
            new ChildWindow(page, page, -1),
            new ChildWindow(client, client, -1),
        });
        Assert.Equal(new[] { page, frame }, At(layers, 2000, 700));
        Assert.Equal(new[] { frame }, At(layers, 2000, 60));
    }

    [Fact]
    public void A_part_further_in_than_a_border_is_still_a_step()
    {
        IntRect inner = IntRect.FromLtrb(100 + WindowLayers.SameStep + 1, 100, 1300, 900);
        var layers = new WindowLayers(Frame, Frame, new[] { new ChildWindow(inner, inner, -1) });
        Assert.Equal(new[] { inner, Frame }, At(layers, 600, 500));
    }

    [Fact]
    public void Outside_the_frame_there_is_nothing()
        => Assert.Empty(At(Browser(), 50, 50));

    [Fact]
    public void Without_children_only_the_frame_is_offered()
        => Assert.Equal(new[] { Frame }, At(new WindowLayers(Frame), 600, 500));

    [Fact]
    public void A_child_is_clipped_to_its_parents_client_area_and_the_frame()
    {
        // The frame is DWM's visible bounds; the window rectangle reaches 7 px past it into the invisible resize border.
        IntRect client = IntRect.FromLtrb(93, 130, 1307, 907);
        IntRect panel = IntRect.FromLtrb(93, 130, 700, 907);
        IntRect overhang = IntRect.FromLtrb(600, 300, 900, 500);   // its own child, half outside the panel's client area
        var layers = new WindowLayers(Frame, client, new[]
        {
            new ChildWindow(panel, IntRect.FromLtrb(103, 140, 690, 897), -1),
            new ChildWindow(overhang, overhang, 0),
        });
        Assert.Equal(new[] { IntRect.FromLtrb(100, 130, 700, 900), Frame }, At(layers, 150, 500));
        Assert.Equal(new[] { IntRect.FromLtrb(600, 300, 690, 500), IntRect.FromLtrb(100, 130, 700, 900), Frame }, At(layers, 650, 400));
        // The part of the overhang past its parent's client area is not there to pick.
        Assert.Equal(new[] { Frame }, At(layers, 800, 400));
    }

    [Fact]
    public void A_child_whose_parent_is_hidden_or_listed_after_it_is_left_out()
    {
        var layers = new WindowLayers(Frame, Frame, new[]
        {
            new ChildWindow(IntRect.FromLtrb(2000, 0, 2100, 100), Frame, -1),   // off the frame: nothing visible
            new ChildWindow(Page, Page, 0),                                     // so its child is not either
            new ChildWindow(Page, Page, 3),                                     // a parent that comes later
            new ChildWindow(ScrollBar, ScrollBar, -1),
        });
        Assert.Equal(new[] { Frame }, At(layers, 600, 500));
    }
}
