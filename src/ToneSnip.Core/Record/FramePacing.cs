namespace ToneSnip.Core.Record;

/// <summary>
/// Which captured frames a recording keeps, and when they are stamped. Windows.Graphics.Capture hands over a frame
/// whenever the screen changes, as often as the monitor refreshes, and nothing while it is still. The recording's
/// timeline is cut into slots of one frame each (1/fps); a slot takes at most one frame, the newest the recorder has
/// when the slot opens, stamped at the slot's start. So motion plays at an even cadence whatever the monitor's refresh
/// rate, a still stretch costs nothing (the file is variable frame rate), and a frame is never shown more than one
/// slot late.
/// <para>Times are on the recording's own timeline (<see cref="RecordingClock"/>). Not thread-safe.</para>
/// </summary>
public sealed class FramePacing
{
    public FramePacing(int fps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        Interval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / fps);
    }

    private long? _last;

    public TimeSpan Interval { get; }

    /// <summary>The stamp of the last frame written, or null before the first.</summary>
    public TimeSpan? Last => _last is { } slot ? Start(slot) : null;

    /// <summary>When the slot after the last frame's opens; zero before the first frame.</summary>
    public TimeSpan Next => _last is { } slot ? Start(slot + 1) : TimeSpan.Zero;

    /// <summary>Whether a frame may be written at <paramref name="elapsed"/>: its slot is after the last frame's.</summary>
    public bool IsOpen(TimeSpan elapsed) => _last is not { } last || SlotOf(elapsed) > last;

    /// <summary>Takes the slot <paramref name="elapsed"/> is in for a frame and returns the frame's stamp, the slot's
    /// start. A slot already taken gives the next one, since a video's timestamps must increase.</summary>
    public TimeSpan Take(TimeSpan elapsed)
    {
        long slot = SlotOf(elapsed);
        if (_last is { } last && slot <= last) slot = last + 1;
        _last = slot;
        return Start(slot);
    }

    private long SlotOf(TimeSpan elapsed) => Math.Max(0, elapsed.Ticks) / Interval.Ticks;

    private TimeSpan Start(long slot) => TimeSpan.FromTicks(slot * Interval.Ticks);
}
