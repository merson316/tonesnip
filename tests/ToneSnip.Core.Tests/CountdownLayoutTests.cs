using ToneSnip.Core.Geometry;
using Xunit;

namespace ToneSnip.Core.Tests;

public class CountdownLayoutTests
{
    [Fact]
    public void The_pill_is_the_same_share_of_the_screen_at_any_scaling()
    {
        // 2160 physical rows at 150% and 1440 at 100% are the same 1440 effective pixels: the same pill.
        CountdownLayout a = CountdownLayout.For(new IntRect(0, 0, 3840, 2160), scale: 1.5);
        CountdownLayout b = CountdownLayout.For(new IntRect(0, 0, 2560, 1440), scale: 1.0);
        Assert.Equal(a.Size, b.Size);
        Assert.Equal(a.FontSize, b.FontSize);
    }

    [Fact]
    public void A_taller_screen_gets_a_bigger_pill()
    {
        CountdownLayout small = CountdownLayout.For(new IntRect(0, 0, 1920, 1080), scale: 1.0);
        CountdownLayout large = CountdownLayout.For(new IntRect(0, 0, 3440, 1440), scale: 1.0);
        Assert.True(large.Size > small.Size);
        Assert.True(small.Size >= 80, $"a 1080p pill is {small.Size} px, too small to notice");
    }

    [Theory]
    [InlineData(768, 1.0)]
    [InlineData(4320, 1.0)]
    [InlineData(1080, 2.5)]
    public void The_pill_stays_within_its_floor_and_ceiling(int height, double scale)
    {
        CountdownLayout l = CountdownLayout.For(new IntRect(0, 0, 1000, height), scale);
        Assert.InRange(l.Size, CountdownLayout.MinSize, CountdownLayout.MaxSize);
        Assert.True(l.FontSize < l.Size, "the digit fits inside the pill");
    }

    [Fact]
    public void It_sits_in_the_top_right_corner_of_its_monitor()
    {
        // A second monitor to the right and above the primary: the inset is from that monitor's own corner.
        var monitor = new IntRect(3440, -422, 1440, 2560);
        CountdownLayout l = CountdownLayout.For(monitor, scale: 1.0);
        IntRect at = l.Place(monitor, scale: 1.0, windowWidth: 120, windowHeight: 120);
        Assert.Equal(monitor.Right - CountdownLayout.Inset - 120, at.Left);
        Assert.Equal(monitor.Top + CountdownLayout.Inset, at.Top);
        Assert.True(at.Right <= monitor.Right && at.Top >= monitor.Top);
    }
}
