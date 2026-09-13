using ToneSnip.Core.Annotate;
using ToneSnip.Core.Geometry;
using Xunit;

namespace ToneSnip.Core.Tests.Annotate;

public class HandlesTests
{
    [Fact]
    public void Box_has_eight_handles_and_line_has_two()
    {
        Assert.Equal(8, Handles.Of(new BoxShape(1, new IntRect(0, 0, 100, 50), 2, 1, false, false)).Count);
        Assert.Equal(2, Handles.Of(new LineShape(2, 0, 0, 10, 10, 2, 1, false)).Count);
        Assert.Empty(Handles.Of(new TextShape(3, 0, 0, "x", 14, 1)));
    }

    [Fact]
    public void Hit_finds_the_handle_under_the_pointer()
    {
        var box = new BoxShape(1, new IntRect(0, 0, 100, 50), 2, 1, false, false);
        Assert.Equal(Handle.SE, Handles.Hit(box, 99, 49, size: 8));
        Assert.Equal(Handle.N, Handles.Hit(box, 50, 2, size: 8));
        Assert.Equal(Handle.None, Handles.Hit(box, 50, 25, size: 8));
    }

    [Fact]
    public void Resize_flips_when_an_edge_is_dragged_past_the_opposite_one()
    {
        var r = new IntRect(10, 10, 100, 50);
        Assert.Equal(new IntRect(-40, 10, 50, 50), Handles.Resize(r, Handle.E, -150, 0));     // right edge dragged 40 px left of the left edge
        Assert.Equal(new IntRect(10, 60, 100, 40), Handles.Resize(r, Handle.N, 0, 90));      // top dragged 40 px below the bottom
    }

    [Fact]
    public void Resize_moves_only_the_dragged_edges_and_never_inverts()
    {
        var r = new IntRect(10, 10, 100, 50);
        Assert.Equal(new IntRect(10, 10, 110, 60), Handles.Resize(r, Handle.SE, 10, 10));
        Assert.Equal(new IntRect(20, 10, 90, 50), Handles.Resize(r, Handle.W, 10, 0));
        Assert.Equal(new IntRect(10, 10, 100, 40), Handles.Resize(r, Handle.S, 0, -10));
        IntRect tiny = Handles.Resize(r, Handle.E, -500, 0);
        Assert.True(tiny.Width >= 2);
    }
}
