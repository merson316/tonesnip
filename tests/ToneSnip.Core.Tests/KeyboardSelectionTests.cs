using ToneSnip.Core.Geometry;
using Xunit;

namespace ToneSnip.Core.Tests;

public class KeyboardSelectionTests
{
    // A 2560x1440 monitor with a 1920x1080 one to its right, top-aligned: the desktop's bounding box has a gap under the
    // smaller one.
    private static readonly IntRect[] Monitors = { new(0, 0, 2560, 1440), new(2560, 0, 1920, 1080) };

    [Fact]
    public void A_point_on_a_monitor_stays_where_it_is()
    {
        Assert.Equal((100, 200), KeyboardSelection.ClampToMonitors(100, 200, Monitors));
        Assert.Equal((3000, 1079), KeyboardSelection.ClampToMonitors(3000, 1079, Monitors));
    }

    [Fact]
    public void A_point_in_the_gap_goes_to_the_nearest_monitor_edge()
    {
        // Just under the smaller monitor: its bottom row is nearer than the larger one's right column.
        Assert.Equal((3000, 1079), KeyboardSelection.ClampToMonitors(3000, 1090, Monitors));
        // Near the larger one's right edge, low down: that edge is nearer.
        Assert.Equal((2559, 1400), KeyboardSelection.ClampToMonitors(2570, 1400, Monitors));
    }

    [Fact]
    public void A_point_past_the_outer_edges_is_pulled_back_on()
    {
        Assert.Equal((0, 0), KeyboardSelection.ClampToMonitors(-5, -5, Monitors));
        Assert.Equal((4479, 500), KeyboardSelection.ClampToMonitors(4600, 500, Monitors));
    }

    [Fact]
    public void With_no_monitors_the_point_is_unchanged()
        => Assert.Equal((7, 8), KeyboardSelection.ClampToMonitors(7, 8, Array.Empty<IntRect>()));

    [Fact]
    public void Tab_starts_at_the_first_item_and_Shift_Tab_at_the_last()
    {
        Assert.Equal(0, KeyboardSelection.Cycle(-1, 1, 5));
        Assert.Equal(4, KeyboardSelection.Cycle(-1, -1, 5));
    }

    [Fact]
    public void Cycling_wraps_both_ways()
    {
        Assert.Equal(0, KeyboardSelection.Cycle(4, 1, 5));
        Assert.Equal(4, KeyboardSelection.Cycle(0, -1, 5));
        Assert.Equal(2, KeyboardSelection.Cycle(1, 1, 5));
        // A list that shrank since the last press starts over.
        Assert.Equal(0, KeyboardSelection.Cycle(7, 1, 3));
        Assert.Equal(-1, KeyboardSelection.Cycle(0, 1, 0));
    }
}
