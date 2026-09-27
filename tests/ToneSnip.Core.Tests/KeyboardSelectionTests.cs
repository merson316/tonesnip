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
    public void A_step_across_touching_monitors_goes_straight_over()
        => Assert.Equal((2560, 500), KeyboardSelection.Step(2559, 500, 1, 0, Monitors));

    [Fact]
    public void A_step_past_the_edge_stops_on_it_first()
    {
        // Ten pixels right from five short of the edge, level with the gap under the smaller monitor.
        Assert.Equal((2559, 1400), KeyboardSelection.Step(2555, 1400, 10, 0, Monitors));
    }

    [Fact]
    public void From_the_edge_the_pointer_crosses_to_a_shorter_monitor_beside_it()
    {
        // Level with the gap: the smaller monitor's nearest pixel is its bottom-left corner.
        Assert.Equal((2560, 1079), KeyboardSelection.Step(2559, 1400, 1, 0, Monitors));
        Assert.Equal((2560, 1079), KeyboardSelection.Step(2559, 1400, 10, 0, Monitors));
        // And back: the larger monitor is under the pointer's row, so it is entered level with it.
        Assert.Equal((2559, 1079), KeyboardSelection.Step(2560, 1079, -1, 0, Monitors));
    }

    [Fact]
    public void A_gap_between_monitors_is_jumped()
    {
        // A monitor 100 px to the right of the first, lower down, and one above it with a gap too.
        IntRect[] spaced = { new(0, 0, 1920, 1080), new(2020, 500, 1920, 1080), new(0, -1200, 1920, 1080) };
        Assert.Equal((2020, 500), KeyboardSelection.Step(1919, 100, 1, 0, spaced));
        Assert.Equal((1919, 500), KeyboardSelection.Step(2020, 500, -1, 0, spaced));
        Assert.Equal((300, -121), KeyboardSelection.Step(300, 0, 0, -1, spaced));
        Assert.Equal((300, 0), KeyboardSelection.Step(300, -121, 0, 10, spaced));
    }

    [Fact]
    public void The_nearest_monitor_beyond_wins()
    {
        IntRect[] row = { new(0, 0, 1000, 1000), new(1200, 0, 1000, 1000), new(1100, 1500, 1000, 1000) };
        Assert.Equal((1200, 400), KeyboardSelection.Step(999, 400, 1, 0, row));
    }

    [Fact]
    public void With_nothing_beyond_the_pointer_stays_at_the_edge()
    {
        Assert.Equal((4479, 500), KeyboardSelection.Step(4479, 500, 1, 0, Monitors));
        Assert.Equal((0, 0), KeyboardSelection.Step(0, 0, -10, 0, Monitors));
        Assert.Equal((3000, 1079), KeyboardSelection.Step(3000, 1079, 0, 1, Monitors));
    }

    [Fact]
    public void A_pointer_off_every_monitor_is_pulled_onto_the_nearest()
        => Assert.Equal((3000, 1079), KeyboardSelection.Step(3000, 1200, 0, 1, Monitors));

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
