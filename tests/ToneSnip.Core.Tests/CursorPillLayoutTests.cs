using ToneSnip.Core.Geometry;
using Xunit;

namespace ToneSnip.Core.Tests;

public class CursorPillLayoutTests
{
    [Fact]
    public void At_200_percent_every_metric_is_twice_its_100_percent_size()
    {
        CursorPillLayout one = CursorPillLayout.For(1.0), two = CursorPillLayout.For(2.0);
        Assert.Equal(one.FontPx * 2, two.FontPx);
        Assert.Equal(one.PadX * 2, two.PadX);
        Assert.Equal(one.PadY * 2, two.PadY);
        Assert.Equal(one.Radius * 2, two.Radius);
        Assert.Equal(one.OffsetX * 2, two.OffsetX);
        Assert.Equal(one.OffsetY * 2, two.OffsetY);
    }

    [Fact]
    public void A_nonsense_scale_falls_back_to_100_percent() => Assert.Equal(CursorPillLayout.For(1.0), CursorPillLayout.For(0));

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void It_sits_below_right_of_the_cursor_clear_of_the_scaled_cursor(double scale)
    {
        // The system cross cursor is 32 effective pixels with its hot spot in the middle, so it reaches 16 * scale.
        CursorPillLayout l = CursorPillLayout.For(scale);
        IntRect pill = l.Place(500, 500, textWidth: 100, textHeight: l.FontPx + 2, areaWidth: 3000, areaHeight: 2000);
        Assert.True(pill.Left >= 500 + 16 * scale, $"pill left {pill.Left} under the cursor at {scale}x");
        Assert.True(pill.Top >= 500 + 16 * scale, $"pill top {pill.Top} under the cursor at {scale}x");
        Assert.Equal(100 + 2 * l.PadX, pill.Width);
    }

    [Fact]
    public void Near_the_right_and_bottom_edges_it_flips_and_stays_on_the_monitor()
    {
        CursorPillLayout l = CursorPillLayout.For(2.0);
        IntRect pill = l.Place(2990, 1990, textWidth: 400, textHeight: 34, areaWidth: 3000, areaHeight: 2000);
        Assert.True(pill.Right <= 2990 && pill.Bottom <= 1990, $"pill {pill} did not flip to the cursor's other side");
        Assert.True(pill.Left >= 0 && pill.Top >= 0);
    }

    [Theory]
    [InlineData(1.0, false)]
    [InlineData(2.0, false)]
    [InlineData(2.0, true)]
    [InlineData(3.5, true)]
    public void The_repaint_reach_covers_a_long_readout_on_either_side_of_the_cursor(double scale, bool flipped)
    {
        // "3840 × 2160   1000 nits  peak 10000  mean 1000" is about 300 effective pixels wide in 12 px Segoe UI.
        CursorPillLayout l = CursorPillLayout.For(scale);
        int textWidth = (int)(300 * scale), textHeight = l.FontPx + (int)(4 * scale);
        (int cx, int cy) = flipped ? (2990, 1990) : (1500, 500);
        IntRect pill = l.Place(cx, cy, textWidth, textHeight, areaWidth: 3000, areaHeight: 2000);
        IntRect reach = l.Reach(cx, cy);
        Assert.True(reach.Intersect(pill) == pill, $"pill {pill} leaves the repaint reach {reach} at {scale}x, so it would smear");
    }
}
