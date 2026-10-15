using ToneSnip.Core.Record;
using Xunit;

namespace ToneSnip.Core.Tests.Record;

public class RecordingClockTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Without_pauses_the_timeline_is_the_time_since_the_start()
    {
        var clock = new RecordingClock();
        Assert.Equal(TimeSpan.Zero, clock.Elapsed(S(5)));
        clock.Start(S(100));
        Assert.Equal(TimeSpan.Zero, clock.Elapsed(S(99)));
        Assert.Equal(S(2.5), clock.Elapsed(S(102.5)));
    }

    [Fact]
    public void A_pause_is_cut_out_and_the_timeline_stands_still_inside_it()
    {
        var clock = new RecordingClock();
        clock.Start(S(0));
        Assert.True(clock.Pause(S(3)));
        Assert.False(clock.Pause(S(4)));
        Assert.True(clock.Paused);
        Assert.Equal(S(3), clock.Elapsed(S(10)));
        Assert.True(clock.Resume(S(13)));
        Assert.False(clock.Resume(S(14)));
        Assert.Equal(S(3), clock.Elapsed(S(8)));
        Assert.Equal(S(3), clock.Elapsed(S(13)));
        Assert.Equal(S(5), clock.Elapsed(S(15)));
    }

    [Fact]
    public void A_frame_captured_before_a_pause_keeps_its_place_after_it()
    {
        var clock = new RecordingClock();
        clock.Start(S(0));
        clock.Pause(S(2)); clock.Resume(S(7));
        clock.Pause(S(9)); clock.Resume(S(20));
        Assert.Equal(S(1.5), clock.Elapsed(S(1.5)));
        Assert.Equal(S(3), clock.Elapsed(S(8)));
        Assert.Equal(S(4), clock.Elapsed(S(15)));
        Assert.Equal(S(5), clock.Elapsed(S(21)));
    }

    [Fact]
    public void Pausing_before_the_start_does_nothing()
    {
        var clock = new RecordingClock();
        Assert.False(clock.Pause(S(1)));
        Assert.False(clock.Paused);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(9.6, "0:09")]
    [InlineData(75, "1:15")]
    [InlineData(3599, "59:59")]
    [InlineData(3725, "1:02:05")]
    [InlineData(-3, "0:00")]
    public void Lengths_read_as_minutes_and_seconds_and_hours_from_an_hour(double seconds, string text)
        => Assert.Equal(text, RecordingClock.Format(TimeSpan.FromSeconds(seconds)));
}
