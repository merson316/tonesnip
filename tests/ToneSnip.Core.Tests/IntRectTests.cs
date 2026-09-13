using ToneSnip.Core.Capture;
using ToneSnip.Core.Geometry;
using Xunit;

namespace ToneSnip.Core.Tests;

public class IntRectTests
{
    [Fact]
    public void FromDrag_normalizes_and_is_inclusive()
    {
        Assert.Equal(new IntRect(10, 20, 6, 4), IntRect.FromDrag(15, 23, 10, 20));
        Assert.Equal(new IntRect(5, 5, 1, 1), IntRect.FromDrag(5, 5, 5, 5));
    }

    [Fact]
    public void Intersect_and_union()
    {
        var a = new IntRect(0, 0, 100, 50);
        var b = new IntRect(80, 40, 100, 100);
        Assert.Equal(new IntRect(80, 40, 20, 10), a.Intersect(b));
        Assert.True(a.Intersect(new IntRect(200, 200, 5, 5)).IsEmpty);
        Assert.Equal(new IntRect(0, 0, 180, 140), a.Union(b));
        Assert.Equal(b, IntRect.Empty.Union(b));
    }

    [Fact]
    public void Clamp_keeps_rect_inside_bounds()
    {
        var bounds = new IntRect(-1000, 0, 4440, 1440);
        Assert.Equal(new IntRect(-1000, 0, 100, 100), new IntRect(-1200, -50, 300, 150).Clamp(bounds));
        Assert.True(new IntRect(5000, 5000, 10, 10).Clamp(bounds).IsEmpty);
    }

    [Fact]
    public void Nudge_moves_but_stays_inside_bounds()
    {
        var bounds = new IntRect(0, 0, 100, 100);
        Assert.Equal(new IntRect(11, 10, 20, 20), new IntRect(10, 10, 20, 20).Nudge(1, 0, bounds));
        Assert.Equal(new IntRect(80, 10, 20, 20), new IntRect(75, 10, 20, 20).Nudge(10, 0, bounds));
        Assert.Equal(new IntRect(0, 0, 20, 20), new IntRect(3, 3, 20, 20).Nudge(-10, -10, bounds));
    }

    [Fact]
    public void Contains_uses_exclusive_right_bottom()
    {
        var r = new IntRect(0, 0, 10, 10);
        Assert.True(r.Contains(9, 9));
        Assert.False(r.Contains(10, 9));
    }

    [Theory]
    [InlineData("region", SnipMode.Rectangle, false)]
    [InlineData("Window", SnipMode.Window, false)]
    [InlineData("fullscreen", SnipMode.FullScreen, false)]
    [InlineData("freeform", SnipMode.Freeform, false)]
    [InlineData("fullScreenAll", SnipMode.FullScreenAll, true)]
    [InlineData("activewindow", SnipMode.ActiveWindow, true)]
    public void Parses_modes(string text, SnipMode mode, bool instant)
    {
        Assert.True(SnipModes.TryParse(text, out SnipMode m));
        Assert.Equal(mode, m);
        Assert.Equal(instant, SnipModes.IsInstant(m));
        Assert.True(SnipModes.TryParse(SnipModes.Name(m), out SnipMode again) && again == m);
    }

    [Fact]
    public void Rejects_unknown_mode() => Assert.False(SnipModes.TryParse("video", out _));
}
