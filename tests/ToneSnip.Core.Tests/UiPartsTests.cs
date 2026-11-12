using ToneSnip.Core.Geometry;
using Xunit;

namespace ToneSnip.Core.Tests;

public class UiPartsTests
{
    private static readonly IntRect Frame = new(100, 100, 1200, 800);
    private static readonly IntRect Page = IntRect.FromLtrb(100, 220, 1300, 900);

    private static List<IntRect> At(WindowLayers layers, int x, int y)
    {
        var list = new List<IntRect>();
        layers.At(x, y, list);
        return list;
    }

    [Fact]
    public void A_page_and_a_pane_are_parts()
    {
        Assert.True(UiParts.IsPart(new UiElement(Page, UiParts.Document, false), Frame));
        Assert.True(UiParts.IsPart(new UiElement(Page, UiParts.Pane, false), Frame));
    }

    [Fact]
    public void A_control_is_not_a_part_however_large()
    {
        const int Button = 50000, Text = 50020;
        Assert.False(UiParts.IsPart(new UiElement(Page, Button, false), Frame));
        Assert.False(UiParts.IsPart(new UiElement(Page, Text, false), Frame));
    }

    [Fact]
    public void An_element_scrolled_out_of_view_is_not_a_part()
        => Assert.False(UiParts.IsPart(new UiElement(Page, UiParts.Pane, true), Frame));

    [Fact]
    public void A_part_is_measured_by_what_shows_inside_the_frame()
    {
        // 200 px wide, but only 20 px of it is inside the window.
        IntRect mostlyOutside = IntRect.FromLtrb(1280, 300, 1480, 600);
        Assert.False(UiParts.IsPart(new UiElement(mostlyOutside, UiParts.Pane, false), Frame));
        IntRect thin = new(200, 300, 400, UiParts.MinSide - 1);
        Assert.False(UiParts.IsPart(new UiElement(thin, UiParts.Pane, false), Frame));
        Assert.True(UiParts.IsPart(new UiElement(thin with { Height = UiParts.MinSide }, UiParts.Pane, false), Frame));
    }

    [Fact]
    public void A_toolbar_is_a_part_but_its_buttons_are_not_looked_at()
    {
        var toolbar = new UiElement(new IntRect(100, 140, 1200, 80), UiParts.ToolBar, false);
        Assert.True(UiParts.IsPart(toolbar, Frame));
        Assert.False(UiParts.HasPartsInside(toolbar, Frame));
        Assert.True(UiParts.HasPartsInside(new UiElement(Page, UiParts.Pane, false), Frame));
    }

    [Fact]
    public void A_window_of_one_piece_has_no_parts_until_some_are_added()
    {
        var layers = new WindowLayers(Frame);
        Assert.False(layers.HasParts);
        Assert.Equal(new[] { Frame }, At(layers, 600, 500));

        WindowLayers with = layers.WithParts(new[] { Page });
        Assert.True(with.HasParts);
        Assert.Equal(new[] { Page, Frame }, At(with, 600, 500));
        Assert.Equal(new[] { Frame }, At(with, 600, 150));
    }

    [Fact]
    public void A_child_window_that_fills_the_frame_is_not_a_part()
    {
        IntRect client = IntRect.FromLtrb(101, 100, 1299, 899);
        Assert.False(new WindowLayers(Frame, client, new[] { new ChildWindow(client, client, -1) }).HasParts);
        Assert.True(new WindowLayers(Frame, Frame, new[] { new ChildWindow(Page, Page, -1) }).HasParts);
    }

    [Fact]
    public void An_added_part_shows_only_inside_the_frame()
    {
        IntRect spills = IntRect.FromLtrb(50, 220, 1400, 1000);
        Assert.Equal(new[] { IntRect.FromLtrb(100, 220, 1300, 900), Frame }, At(new WindowLayers(Frame).WithParts(new[] { spills }), 600, 500));
    }
}
