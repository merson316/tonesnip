using ToneSnip.Core.Geometry;
using Xunit;

namespace ToneSnip.Core.Tests;

public class GuidesLayoutTests
{
    private static readonly IntRect Monitor = new(0, 0, 2560, 1600);

    private static IntRect[] Lines(GuidesLayout l, IntRect sel, int cx, int cy, IntRect area)
    {
        var strips = new IntRect[GuidesLayout.MaxLines];
        return strips[..l.Lines(sel, cx, cy, area, strips)];
    }

    [Fact]
    public void At_200_percent_every_metric_is_twice_its_100_percent_size()
    {
        GuidesLayout one = GuidesLayout.For(1.0), two = GuidesLayout.For(2.0);
        Assert.Equal(one.Edge * 2, two.Edge);
        Assert.Equal(one.Dash * 2, two.Dash);
        Assert.Equal(one.Cell * 2, two.Cell);
        Assert.Equal(one.LoupeOffset * 2, two.LoupeOffset);
        Assert.Equal(one.Ring * 2, two.Ring);
        Assert.Equal(one.ChipGap * 2, two.ChipGap);
        Assert.Equal(one.Source, two.Source);
        Assert.True(one.Source % 2 == 1, "the loupe needs a centre pixel");
    }

    [Fact]
    public void A_selection_extends_its_four_edges_across_the_monitor()
    {
        GuidesLayout l = GuidesLayout.For(1.0);
        var sel = new IntRect(400, 300, 800, 500);
        IntRect[] lines = Lines(l, sel, 1200, 800, Monitor);
        Assert.Equal(4, lines.Length);
        Assert.Contains(new IntRect(0, sel.Top, Monitor.Width, l.Edge), lines);
        Assert.Contains(new IntRect(0, sel.Bottom - l.Edge, Monitor.Width, l.Edge), lines);
        Assert.Contains(new IntRect(sel.Left, 0, l.Edge, Monitor.Height), lines);
        Assert.Contains(new IntRect(sel.Right - l.Edge, 0, l.Edge, Monitor.Height), lines);
    }

    [Fact]
    public void An_edge_off_this_monitor_draws_no_line_here()
    {
        // Monitor-local: the selection's top and left edges are on the monitor above and to the left.
        GuidesLayout l = GuidesLayout.For(1.0);
        IntRect[] lines = Lines(l, new IntRect(-200, -100, 500, 400), 100, 100, Monitor);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, s => Assert.Equal(s, s.Intersect(Monitor)));
    }

    [Fact]
    public void With_nothing_selected_the_lines_cross_at_the_cursor()
    {
        GuidesLayout l = GuidesLayout.For(1.5);
        IntRect[] lines = Lines(l, IntRect.Empty, 900, 700, Monitor);
        Assert.Equal(2, lines.Length);
        Assert.Contains(new IntRect(0, 700, Monitor.Width, l.Edge), lines);
        Assert.Contains(new IntRect(900, 0, l.Edge, Monitor.Height), lines);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void The_loupe_sits_below_right_of_the_cursor_with_its_label_underneath(double scale)
    {
        GuidesLayout l = GuidesLayout.For(scale);
        (IntRect loupe, IntRect label) = l.Loupe(500, 500, labelWidth: 120, labelHeight: 22, Monitor);
        Assert.Equal(l.Source * l.Cell, loupe.Width);
        Assert.Equal(loupe.Width, loupe.Height);
        Assert.True(loupe.Left - l.Ring >= 500 + 16 * scale && loupe.Top - l.Ring >= 500 + 16 * scale, $"loupe {loupe} is under the cursor");
        Assert.True(label.Top >= loupe.Bottom + l.Ring, $"label {label} overlaps the loupe {loupe}");
        Assert.Equal(loupe.Left + loupe.Width / 2, label.Left + label.Width / 2, tolerance: 1);
    }

    [Fact]
    public void Near_the_bottom_right_corner_the_loupe_and_label_flip_above_left_and_stay_on_the_monitor()
    {
        GuidesLayout l = GuidesLayout.For(2.0);
        (IntRect loupe, IntRect label) = l.Loupe(2550, 1590, labelWidth: 500, labelHeight: 44, Monitor);
        Assert.True(loupe.Right + l.Ring <= 2550 && label.Bottom <= 1590, $"loupe {loupe} / label {label} did not flip");
        Assert.Equal(label, label.Intersect(Monitor));
        Assert.Equal(loupe, loupe.Intersect(Monitor));
    }

    public static TheoryData<double, int, int, int> LoupeReachCases => new()
    {
        { 1.0, 500, 500, 330 },
        { 2.0, 2550, 1590, 660 },
        { 2.0, 4, 4, 660 },
        { 3.0, 1280, 800, 990 },
    };

    [Theory]
    [MemberData(nameof(LoupeReachCases))]
    public void The_loupe_reach_covers_its_rings_and_a_long_label(double scale, int cx, int cy, int labelWidth)
    {
        GuidesLayout l = GuidesLayout.For(scale);
        (IntRect loupe, IntRect label) = l.Loupe(cx, cy, labelWidth, (int)(22 * scale), Monitor);
        IntRect reach = l.LoupeReach(cx, cy);
        IntRect ringed = IntRect.FromLtrb(loupe.Left - l.Ring, loupe.Top - l.Ring, loupe.Right + l.Ring, loupe.Bottom + l.Ring);
        Assert.True(reach.Intersect(ringed) == ringed, $"loupe {ringed} leaves the reach {reach}");
        Assert.True(reach.Intersect(label) == label, $"label {label} leaves the reach {reach}");
    }

    [Fact]
    public void The_size_chip_sits_below_the_bottom_right_corner()
    {
        GuidesLayout l = GuidesLayout.For(1.0);
        var sel = new IntRect(400, 300, 800, 500);
        IntRect chip = l.Chip(sel, textWidth: 70, textHeight: 16, Monitor);
        Assert.Equal(sel.Right, chip.Right);
        Assert.Equal(sel.Bottom + l.ChipGap, chip.Top);
    }

    [Fact]
    public void With_no_room_below_the_chip_moves_inside_the_corner()
    {
        GuidesLayout l = GuidesLayout.For(1.0);
        IntRect chip = l.Chip(Monitor, textWidth: 70, textHeight: 16, Monitor);
        Assert.Equal(chip, chip.Intersect(Monitor));
        Assert.True(chip.Right <= Monitor.Right - l.ChipGap && chip.Bottom <= Monitor.Bottom - l.ChipGap, $"chip {chip}");
    }

    public static TheoryData<double, IntRect> ChipReachCases => new()
    {
        { 1.0, new IntRect(400, 300, 800, 500) },
        { 2.0, new IntRect(0, 0, 2560, 1600) },
        { 2.0, new IntRect(0, 900, 40, 100) },
        { 3.0, new IntRect(2500, 1580, 20, 20) },
    };

    [Theory]
    [MemberData(nameof(ChipReachCases))]
    public void The_selection_reach_covers_the_edge_and_the_chip(double scale, IntRect sel)
    {
        GuidesLayout l = GuidesLayout.For(scale);
        IntRect reach = l.Reach(sel);
        Assert.True(reach.Intersect(sel) == sel);
        IntRect chip = l.Chip(sel, (int)(85 * scale), (int)(16 * scale), Monitor);
        Assert.True(reach.Intersect(chip) == chip, $"chip {chip} leaves the reach {reach}");
    }
}
