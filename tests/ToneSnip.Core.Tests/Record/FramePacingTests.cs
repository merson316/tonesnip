using ToneSnip.Core.Record;
using Xunit;

namespace ToneSnip.Core.Tests.Record;

public class FramePacingTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>
    /// The recorder's use of the pacing: a frame that arrives in an open slot is written at once; one that arrives in a
    /// taken slot is held, replacing the one held before, and written when the next slot opens unless a newer frame
    /// has arrived by then. Returns the stamps written.
    /// </summary>
    private static List<TimeSpan> Run(FramePacing pacing, List<TimeSpan> arrivals)
    {
        var written = new List<TimeSpan>();
        bool held = false;
        for (int i = 0; i < arrivals.Count; i++)
        {
            TimeSpan at = arrivals[i];
            if (pacing.IsOpen(at)) { written.Add(pacing.Take(at)); held = false; }
            else held = true;
            TimeSpan next = i + 1 < arrivals.Count ? arrivals[i + 1] : TimeSpan.MaxValue;
            if (held && pacing.Next < next) { written.Add(pacing.Take(pacing.Next)); held = false; }
        }
        return written;
    }

    [Fact]
    public void The_first_frame_takes_the_first_slot()
    {
        var pacing = new FramePacing(30);
        Assert.Null(pacing.Last);
        Assert.True(pacing.IsOpen(TimeSpan.Zero));
        Assert.Equal(TimeSpan.Zero, pacing.Take(Ms(12)));
    }

    [Fact]
    public void The_next_slot_opens_where_the_last_frames_slot_ends()
    {
        var pacing = new FramePacing(60);
        pacing.Take(Ms(5));
        Assert.False(pacing.IsOpen(Ms(16)));
        Assert.True(pacing.IsOpen(Ms(16.7)));
        Assert.Equal(TimeSpan.FromTicks(166_666), pacing.Next);
    }

    [Fact]
    public void A_monitor_refreshing_at_the_frame_rate_keeps_every_frame_through_jitter()
    {
        double interval = 1000.0 / 60;
        List<TimeSpan> written = Run(new FramePacing(60), Enumerable.Range(0, 600).Select(i => Ms(i * interval + (i % 2 == 0 ? 1.5 : -1.5) + 2)).ToList());
        Assert.Equal(600, written.Count);
    }

    [Fact]
    public void A_fast_monitor_is_thinned_to_the_frame_rate_at_an_even_cadence()
    {
        var pacing = new FramePacing(30);
        List<TimeSpan> written = Run(pacing, Enumerable.Range(0, 2400).Select(i => Ms(i * 1000.0 / 240)).ToList());
        Assert.InRange(written.Count, 300, 301);
        Assert.All(written.Zip(written.Skip(1)), p => Assert.Equal(pacing.Interval, p.Second - p.First));
    }

    [Fact]
    public void The_last_change_before_a_quiet_stretch_is_written_when_its_slot_opens()
    {
        List<TimeSpan> written = Run(new FramePacing(30), [Ms(0), Ms(10)]);
        Assert.Equal([TimeSpan.Zero, TimeSpan.FromTicks(333_333)], written);
    }

    [Fact]
    public void A_late_frame_takes_the_slot_it_is_in_and_stamps_always_increase()
    {
        var pacing = new FramePacing(30);
        Assert.Equal(TimeSpan.FromTicks(999_999), pacing.Take(Ms(100)));
        Assert.Equal(TimeSpan.FromTicks(1_333_332), pacing.Take(Ms(100)));
        Assert.Equal(TimeSpan.FromTicks(1_666_665), pacing.Take(Ms(50)));
        Assert.Equal(TimeSpan.FromTicks(9_999_990), pacing.Take(Ms(1000)));
    }
}
