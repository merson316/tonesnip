using ToneSnip.Core.Geometry;
using Xunit;

namespace ToneSnip.Core.Tests;

public class MonitorLayoutTests
{
    private static readonly IntRect Main = new(0, 0, 2560, 1440), Side = new(2560, 0, 1920, 1080);

    [Fact]
    public void The_same_monitors_in_another_order_are_the_same_layout()
        => Assert.True(MonitorLayout.Same(new[] { Main, Side }, new[] { Side, Main }));

    [Fact]
    public void A_monitor_unplugged_or_added_changes_the_layout()
    {
        Assert.False(MonitorLayout.Same(new[] { Main, Side }, new[] { Main }));
        Assert.False(MonitorLayout.Same(new[] { Main }, new[] { Main, Side }));
    }

    [Fact]
    public void A_new_resolution_or_position_changes_the_layout()
    {
        Assert.False(MonitorLayout.Same(new[] { Main, Side }, new[] { Main, new IntRect(2560, 0, 2560, 1440) }));
        Assert.False(MonitorLayout.Same(new[] { Main, Side }, new[] { Main, new IntRect(-1920, 0, 1920, 1080) }));
    }

    [Fact]
    public void An_enumeration_that_found_nothing_is_not_a_change()
        => Assert.True(MonitorLayout.Same(new[] { Main }, Array.Empty<IntRect>()));
}
