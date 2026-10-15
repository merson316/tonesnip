using ToneSnip.Core.Geometry;
using ToneSnip.Core.Record;
using Xunit;

namespace ToneSnip.Core.Tests.Record;

public class RecordingAreaTests
{
    private static readonly IntRect Left = new(0, 0, 2560, 1440), Right = new(2560, 0, 1920, 1080);
    private static readonly IntRect[] Desk = [Left, Right];

    [Fact]
    public void A_selection_on_one_monitor_is_kept_as_it_is_when_its_sides_are_even()
    {
        var sel = new IntRect(100, 200, 640, 480);
        (IntRect monitor, IntRect area) = RecordingArea.For(sel, Desk)!.Value;
        Assert.Equal(Left, monitor);
        Assert.Equal(sel, area);
    }

    [Fact]
    public void Odd_sides_lose_their_last_pixel()
    {
        (_, IntRect area) = RecordingArea.For(new IntRect(10, 11, 995, 687), Desk)!.Value;
        Assert.Equal(new IntRect(10, 11, 994, 686), area);
    }

    [Fact]
    public void A_selection_across_monitors_keeps_the_part_on_the_one_it_covers_most()
    {
        (IntRect monitor, IntRect area) = RecordingArea.For(IntRect.FromLtrb(2400, 100, 3000, 500), Desk)!.Value;
        Assert.Equal(Right, monitor);
        Assert.Equal(IntRect.FromLtrb(2560, 100, 3000, 500), area);
    }

    [Fact]
    public void A_whole_monitor_is_the_monitor()
    {
        (IntRect monitor, IntRect area) = RecordingArea.For(Right, Desk)!.Value;
        Assert.Equal(Right, monitor);
        Assert.Equal(Right, area);
    }

    [Fact]
    public void A_tiny_selection_grows_about_its_centre_and_stays_on_its_monitor()
    {
        (_, IntRect area) = RecordingArea.For(new IntRect(500, 500, 10, 20), Desk)!.Value;
        Assert.Equal(new IntRect(473, 478, 64, 64), area);
        (_, IntRect corner) = RecordingArea.For(new IntRect(2550, 1430, 10, 10), Desk)!.Value;
        Assert.Equal(new IntRect(2496, 1376, 64, 64), corner);
    }

    [Fact]
    public void A_selection_off_every_monitor_records_nothing()
    {
        Assert.Null(RecordingArea.For(new IntRect(5000, 5000, 100, 100), Desk));
        Assert.Null(RecordingArea.For(IntRect.Empty, Desk));
    }
}
