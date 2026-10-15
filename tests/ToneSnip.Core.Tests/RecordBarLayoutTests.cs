using ToneSnip.Core.Geometry;
using Xunit;

namespace ToneSnip.Core.Tests;

public class RecordBarLayoutTests
{
    private static readonly IntRect Monitor = new(0, 0, 1920, 1080);

    [Fact]
    public void The_bar_sits_centred_under_the_area()
        => Assert.Equal(new IntRect(450, 618, 220, 48), RecordBarLayout.Place(new IntRect(360, 200, 400, 410), Monitor, 220, 48, 8));

    [Fact]
    public void Without_room_below_it_goes_above()
        => Assert.Equal(new IntRect(450, 142, 220, 48), RecordBarLayout.Place(new IntRect(360, 198, 400, 860), Monitor, 220, 48, 8));

    [Fact]
    public void An_area_as_tall_as_its_monitor_has_the_bar_inside_its_bottom_edge()
        => Assert.Equal(new IntRect(850, 1024, 220, 48), RecordBarLayout.Place(Monitor, Monitor, 220, 48, 8));

    [Fact]
    public void The_bar_stays_on_the_monitor_beside_an_area_at_its_edge()
    {
        var right = new IntRect(1920, -200, 2560, 1440);
        IntRect bar = RecordBarLayout.Place(new IntRect(4380, 300, 100, 100), right, 220, 48, 8);
        Assert.Equal(right.Right - 8 - 220, bar.Left);
        Assert.Equal(408, bar.Top);
    }
}
