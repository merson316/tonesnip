using ToneSnip.Core.Geometry;
using Xunit;

namespace ToneSnip.Core.Tests;

public class ViewfinderLayoutTests
{
    private static readonly IntRect Monitor = new(0, 0, 2560, 1600);

    private static IntRect[] Arms(ViewfinderLayout l, IntRect sel, IntRect area)
    {
        var arms = new IntRect[ViewfinderLayout.MaxArms];
        int n = l.Brackets(sel, area, arms);
        return arms[..n];
    }

    [Fact]
    public void At_200_percent_every_metric_is_twice_its_100_percent_size()
    {
        ViewfinderLayout one = ViewfinderLayout.For(1.0), two = ViewfinderLayout.For(2.0);
        Assert.Equal(one.Edge * 2, two.Edge);
        Assert.Equal(one.Thickness * 2, two.Thickness);
        Assert.Equal(one.Length * 2, two.Length);
        Assert.Equal(one.Keyline * 2, two.Keyline);
        Assert.Equal(one.ChipGap * 2, two.ChipGap);
        Assert.Equal(one.ChipFontPx * 2, two.ChipFontPx);
    }

    [Fact]
    public void A_nonsense_scale_falls_back_to_100_percent() => Assert.Equal(ViewfinderLayout.For(1.0), ViewfinderLayout.For(-3));

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void Away_from_the_edges_the_brackets_hug_each_corner_from_outside(double scale)
    {
        ViewfinderLayout l = ViewfinderLayout.For(scale);
        var sel = new IntRect(400, 300, 800, 500);
        IntRect[] arms = Arms(l, sel, Monitor);
        Assert.Equal(8, arms.Length);
        foreach (IntRect arm in arms)
        {
            Assert.True(arm.Width == l.Thickness || arm.Height == l.Thickness, $"arm {arm} is not {l.Thickness} thick");
            Assert.True(Math.Max(arm.Width, arm.Height) == l.Length, $"arm {arm} is not {l.Length} long");
            // An outside arm never covers the selection's inner pixels, only its outer ring.
            IntRect inner = IntRect.FromLtrb(sel.Left, sel.Top, sel.Right, sel.Bottom).Intersect(arm);
            Assert.True(inner.IsEmpty || inner.Width <= l.Length && inner.Height <= l.Length, $"arm {arm}");
        }
        // The top-left arms start one thickness outside the corner.
        Assert.Contains(arms, a => a.Left == sel.Left - l.Thickness && a.Top == sel.Top - l.Thickness);
        // And the bottom-right arms end one thickness outside theirs.
        Assert.Contains(arms, a => a.Right == sel.Right + l.Thickness && a.Bottom == sel.Bottom + l.Thickness);
    }

    [Fact]
    public void A_whole_monitor_selection_turns_every_bracket_inside()
    {
        ViewfinderLayout l = ViewfinderLayout.For(1.5);
        IntRect[] arms = Arms(l, Monitor, Monitor);
        Assert.Equal(8, arms.Length);
        foreach (IntRect arm in arms) Assert.Equal(arm, arm.Intersect(Monitor));
        Assert.Contains(arms, a => a.Left == 0 && a.Top == 0);
        Assert.Contains(arms, a => a.Right == Monitor.Right && a.Bottom == Monitor.Bottom);
    }

    [Fact]
    public void A_corner_on_another_monitor_is_left_to_that_monitor()
    {
        // Monitor-local coordinates: the selection starts 300 px left of this monitor, so only its right corners are here.
        ViewfinderLayout l = ViewfinderLayout.For(1.0);
        IntRect[] arms = Arms(l, new IntRect(-300, 200, 700, 400), Monitor);
        Assert.Equal(4, arms.Length);
        Assert.All(arms, a => Assert.True(a.Left >= 0, $"arm {a} belongs to the other monitor"));
    }

    [Fact]
    public void A_small_selection_gets_short_arms_that_do_not_meet()
    {
        ViewfinderLayout l = ViewfinderLayout.For(1.0);
        var sel = new IntRect(500, 500, 24, 60);
        IntRect[] arms = Arms(l, sel, Monitor);
        Assert.All(arms, a => Assert.True(Math.Max(a.Width, a.Height) <= Math.Max(l.Thickness, sel.Width / 3 + l.Thickness)));
        Assert.All(arms, a => Assert.True(Math.Max(a.Width, a.Height) >= l.Thickness));
    }

    [Fact]
    public void The_size_chip_sits_above_the_top_left_corner_in_line_with_the_bracket()
    {
        ViewfinderLayout l = ViewfinderLayout.For(1.0);
        var sel = new IntRect(400, 300, 800, 500);
        IntRect chip = l.Chip(sel, textWidth: 70, textHeight: 16, Monitor);
        Assert.Equal(sel.Left - l.Thickness, chip.Left);
        Assert.Equal(sel.Top - l.Thickness - l.ChipGap, chip.Bottom);
        Assert.Equal(70 + 2 * l.ChipPadX, chip.Width);
    }

    [Fact]
    public void With_no_room_above_the_chip_moves_inside_the_corner()
    {
        ViewfinderLayout l = ViewfinderLayout.For(2.0);
        IntRect chip = l.Chip(Monitor, textWidth: 140, textHeight: 32, Monitor);
        Assert.Equal(chip, chip.Intersect(Monitor));
        Assert.True(chip.Left >= l.Thickness && chip.Top >= l.Thickness, $"chip {chip} sits on the bracket");
    }

    [Fact]
    public void Near_the_right_edge_the_chip_slides_left_to_stay_on_the_monitor()
    {
        ViewfinderLayout l = ViewfinderLayout.For(1.0);
        IntRect chip = l.Chip(new IntRect(2540, 400, 20, 100), textWidth: 70, textHeight: 16, Monitor);
        Assert.Equal(Monitor.Right, chip.Right);
    }

    public static TheoryData<double, IntRect> ReachCases => new()
    {
        { 1.0, new IntRect(400, 300, 800, 500) },
        { 2.0, new IntRect(0, 0, 2560, 1600) },
        { 2.0, new IntRect(2500, 900, 60, 700) },
        { 3.0, new IntRect(30, 90, 12, 12) },
    };

    [Theory]
    [MemberData(nameof(ReachCases))]
    public void The_repaint_reach_covers_the_brackets_their_keylines_and_the_chip(double scale, IntRect sel)
    {
        ViewfinderLayout l = ViewfinderLayout.For(scale);
        IntRect reach = l.Reach(sel);
        foreach (IntRect arm in Arms(l, sel, Monitor))
        {
            IntRect keyed = IntRect.FromLtrb(arm.Left - l.Keyline, arm.Top - l.Keyline, arm.Right + l.Keyline, arm.Bottom + l.Keyline);
            Assert.True(reach.Intersect(keyed) == keyed, $"bracket {keyed} leaves the reach {reach}");
        }
        // "10240 × 10240" in 12 px semibold Segoe UI is about 85 effective pixels wide and 16 tall.
        IntRect chip = l.Chip(sel, (int)(85 * scale), (int)(16 * scale), Monitor);
        Assert.True(reach.Intersect(chip) == chip, $"chip {chip} leaves the reach {reach}");
    }
}
