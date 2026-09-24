using ToneSnip.Core.Geometry;
using Xunit;

namespace ToneSnip.Core.Tests;

public class ZoomStepsTests
{
    [Fact]
    public void One_notch_multiplies_by_the_factor_and_back()
    {
        double z = ZoomSteps.Wheel(1, 120, fit: 0.5);
        Assert.Equal(ZoomSteps.NotchFactor, z, 9);
        Assert.Equal(1, ZoomSteps.Wheel(z, -120, fit: 0.5), 9);
    }

    [Fact]
    public void A_fraction_of_a_notch_zooms_a_fraction_of_the_step()
    {
        double quarter = ZoomSteps.Wheel(1, 30, fit: 0.5);
        Assert.True(quarter > 1 && quarter < ZoomSteps.NotchFactor);
        // Four quarter notches land where one whole notch does.
        double z = 1;
        for (int i = 0; i < 4; i++) z = ZoomSteps.Wheel(z, 30, fit: 0.5);
        Assert.Equal(ZoomSteps.NotchFactor, z, 9);
    }

    [Fact]
    public void The_range_is_ten_percent_or_the_fit_up_to_thirty_two_times()
    {
        Assert.Equal(ZoomSteps.MaxZoom, ZoomSteps.Wheel(30, 1200, fit: 0.5));
        Assert.Equal(ZoomSteps.MinZoom, ZoomSteps.Wheel(0.2, -1200, fit: 0.5));
        // A picture so large that fitting it needs less than 10 % may still be fitted.
        Assert.Equal(0.05, ZoomSteps.Wheel(0.2, -12000, fit: 0.05), 9);
        Assert.Equal(0.05, ZoomSteps.Min(0.05));
        Assert.Equal(ZoomSteps.MinZoom, ZoomSteps.Min(0.8));
    }

    [Fact]
    public void Steps_are_finer_than_the_old_eight_stops_and_reach_32x()
    {
        Assert.True(ZoomSteps.Stops.Length >= 20);
        Assert.Equal(ZoomSteps.MaxZoom, ZoomSteps.Stops[^1]);
        for (int i = 1; i < ZoomSteps.Stops.Length; i++)
        {
            Assert.True(ZoomSteps.Stops[i] > ZoomSteps.Stops[i - 1]);
            Assert.True(ZoomSteps.Stops[i] / ZoomSteps.Stops[i - 1] <= 1.6, "no stop is more than 60 % past the one before");
        }
    }

    [Theory]
    [InlineData(1, 1, 1.25)]
    [InlineData(1, -1, 0.75)]
    [InlineData(1.1, 1, 1.25)]      // from between two stops, the next one in that direction
    [InlineData(1.1, -1, 1)]
    [InlineData(32, 1, 32)]         // the ends stay put
    [InlineData(0.1, -1, 0.1)]
    public void Step_goes_to_the_next_stop(double from, int dir, double expected)
        => Assert.Equal(expected, ZoomSteps.Step(from, dir, fit: 0.4), 9);

    [Fact]
    public void Stepping_passes_through_the_fit()
    {
        Assert.Equal(0.62, ZoomSteps.Step(0.5, 1, fit: 0.62), 9);
        Assert.Equal(0.62, ZoomSteps.Step(2.0 / 3, -1, fit: 0.62), 9);
        Assert.Equal(2.0 / 3, ZoomSteps.Step(0.62, 1, fit: 0.62), 9);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(1.5, false)]
    [InlineData(2.4, false)]
    [InlineData(3.3, true)]
    [InlineData(17.2, true)]
    [InlineData(0.5, false)]
    public void Pixels_go_crisp_at_whole_multiples_and_from_3x(double physical, bool crisp)
        => Assert.Equal(crisp, ZoomSteps.Crisp(physical));

    [Fact]
    public void The_animation_starts_and_ends_on_its_values_and_eases_out()
    {
        Assert.Equal(1, ZoomSteps.Between(1, 4, 0), 9);
        Assert.Equal(4, ZoomSteps.Between(1, 4, 1), 9);
        // Half way in time is more than half way in log space: the move slows as it lands.
        Assert.True(ZoomSteps.Between(1, 4, 0.5) > 2);
        // Log space: zooming in and out by the same factor mirror each other.
        Assert.Equal(4, ZoomSteps.Between(1, 4, 0.3) * ZoomSteps.Between(4, 1, 0.3), 9);
    }
}
