namespace ToneSnip.Core.Record;

/// <summary>
/// A recording's timeline: the time spent recording, with the pauses cut out. Every time given to it is on one
/// monotonic clock, the one Windows.Graphics.Capture stamps its frames with (QPC, as a <see cref="TimeSpan"/>), so a
/// frame is placed by when it was captured rather than by when the recorder got round to it.
/// <para>A pause is remembered once it ends, so a frame captured before a pause and handled after it still lands
/// before it. A time inside a pause lands on the pause itself: the timeline does not move while paused, and the first
/// frame after a resume shows the screen as it was when recording resumed.</para>
/// <para>Not thread-safe: the recorder's own thread owns it.</para>
/// </summary>
public sealed class RecordingClock
{
    private readonly List<(TimeSpan From, TimeSpan To)> _pauses = new();
    private TimeSpan _start;
    private TimeSpan? _pausedAt;

    public bool Started { get; private set; }
    public bool Paused => _pausedAt != null;

    public void Start(TimeSpan now)
    {
        _start = now;
        _pauses.Clear();
        _pausedAt = null;
        Started = true;
    }

    /// <summary>False when there was nothing to pause: not started, or already paused.</summary>
    public bool Pause(TimeSpan now)
    {
        if (!Started || _pausedAt != null) return false;
        _pausedAt = now < _start ? _start : now;
        return true;
    }

    /// <summary>False when it was not paused.</summary>
    public bool Resume(TimeSpan now)
    {
        if (_pausedAt is not { } from) return false;
        _pauses.Add((from, now < from ? from : now));
        _pausedAt = null;
        return true;
    }

    /// <summary>A recording's length as the bar and the notification show it: m:ss, or h:mm:ss from an hour.</summary>
    public static string Format(TimeSpan length)
    {
        if (length < TimeSpan.Zero) length = TimeSpan.Zero;
        return length.ToString(length.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Where <paramref name="at"/> falls on the recording: zero before the start, and the pause's own position
    /// for a time inside a pause, the one still running included.</summary>
    public TimeSpan Elapsed(TimeSpan at)
    {
        if (!Started || at <= _start) return TimeSpan.Zero;
        TimeSpan cut = TimeSpan.Zero;
        foreach ((TimeSpan from, TimeSpan to) in _pauses)
        {
            if (at <= from) break;
            cut += (at < to ? at : to) - from;
        }
        if (_pausedAt is { } open && at > open) cut += at - open;
        return at - _start - cut;
    }
}
