using ToneSnip.Core.Geometry;
using Xunit;

namespace ToneSnip.Core.Tests;

public class KeyboardLassoTests
{
    [Fact]
    public void The_live_end_follows_the_pointer_from_the_last_corner()
    {
        var lasso = new KeyboardLasso();
        Assert.False(lasso.Active);
        lasso.Start(10, 10);
        lasso.Move(50, 10);
        Assert.Equal(new[] { (10, 10), (50, 10) }, lasso.Path);
        Assert.True(lasso.AddCorner());
        lasso.Move(50, 60);
        Assert.Equal(new[] { (10, 10), (50, 10), (50, 60) }, lasso.Path);
        Assert.Equal(2, lasso.Corners);
    }

    [Fact]
    public void Enter_closes_a_triangle_into_its_outline()
    {
        var lasso = new KeyboardLasso();
        lasso.Start(10, 10);
        lasso.Move(50, 10); lasso.AddCorner();
        lasso.Move(50, 60);
        Assert.Equal(new List<(int, int)> { (10, 10), (50, 10), (50, 60) }, lasso.Finish());
        Assert.False(lasso.Active);
    }

    [Fact]
    public void A_corner_on_the_last_one_is_not_added_twice()
    {
        var lasso = new KeyboardLasso();
        lasso.Start(0, 0);
        Assert.False(lasso.AddCorner());
        lasso.Move(5, 0);
        Assert.True(lasso.AddCorner());
        Assert.False(lasso.AddCorner());
    }

    [Fact]
    public void Backspace_takes_back_the_last_corner_but_never_the_start()
    {
        var lasso = new KeyboardLasso();
        lasso.Start(0, 0);
        Assert.False(lasso.RemoveCorner());
        lasso.Move(5, 0); lasso.AddCorner();
        lasso.Move(5, 5);
        Assert.True(lasso.RemoveCorner());
        Assert.Equal(new[] { (0, 0), (5, 5) }, lasso.Path);
        Assert.False(lasso.RemoveCorner());
    }

    [Fact]
    public void A_line_or_a_point_is_not_a_lasso_and_stays_open()
    {
        var lasso = new KeyboardLasso();
        lasso.Start(0, 0);
        Assert.Null(lasso.Finish());
        lasso.Move(10, 0); lasso.AddCorner();
        lasso.Move(20, 0);
        Assert.Null(lasso.Finish());                  // three points on one line
        Assert.True(lasso.Active);
        lasso.Move(20, 7);
        Assert.NotNull(lasso.Finish());
    }

    [Fact]
    public void Ending_on_the_start_does_not_repeat_it()
    {
        var lasso = new KeyboardLasso();
        lasso.Start(0, 0);
        lasso.Move(10, 0); lasso.AddCorner();
        lasso.Move(10, 10); lasso.AddCorner();
        lasso.Move(0, 0);
        Assert.Equal(new List<(int, int)> { (0, 0), (10, 0), (10, 10) }, lasso.Finish());
    }
}
