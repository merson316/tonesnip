namespace ToneSnip.Core.Diagnostics;

/// <summary>
/// The memory harness's verdict on a run of snip cycles: does the footprint level off (a plateau), or keep climbing (a
/// ramp, which is a leak)? It compares the median of the last cycles with the median of the early ones, cycle 1 left
/// out as warm-up. A single cycle each side was fooled by the random 40–60 MB spikes a real capture or the GC can put on
/// any one cycle; a median ignores one spike in a window of three.
/// </summary>
public static class MemoryPlateau
{
    /// <summary>The largest window. Five cycles each side is plenty for a 30-cycle run; a longer run only moves the
    /// late window further out.</summary>
    public const int MaxWindow = 5;

    /// <param name="EarlyMedian">Median of the early window, bytes.</param>
    /// <param name="LateMedian">Median of the late window, bytes.</param>
    /// <param name="Window">Cycles in each window.</param>
    /// <param name="EarlyFirst">First cycle of the early window (1-based).</param>
    /// <param name="LateFirst">First cycle of the late window (1-based).</param>
    /// <param name="GrowthPercent">Late over early, in percent of early.</param>
    /// <param name="Plateau">Growth within the allowance.</param>
    public readonly record struct Verdict(long EarlyMedian, long LateMedian, int Window, int EarlyFirst, int LateFirst, double GrowthPercent, bool Plateau);

    /// <summary>
    /// Judges <paramref name="perCycle"/> (cycle 1 first, at least two cycles). Each window is half the cycles after
    /// the first, rounded up and capped at <see cref="MaxWindow"/>, so short runs still get a three-cycle median; there
    /// the two windows may share a middle cycle, which only makes a ramp look smaller, never a plateau look like one.
    /// </summary>
    public static Verdict Judge(IReadOnlyList<long> perCycle, double allowedPercent)
    {
        if (perCycle.Count < 2) throw new ArgumentException("a verdict needs at least two cycles", nameof(perCycle));
        int after = perCycle.Count - 1;                         // cycle 1 is warm-up: the pool and the JIT fill it
        int window = Math.Clamp((after + 1) / 2, 1, MaxWindow);
        int earlyFirst = 2, lateFirst = perCycle.Count - window + 1;
        long early = Median(perCycle, earlyFirst - 1, window), late = Median(perCycle, lateFirst - 1, window);
        double growth = early == 0 ? 0 : (late - early) * 100.0 / early;
        return new Verdict(early, late, window, earlyFirst, lateFirst, growth, growth <= allowedPercent);
    }

    /// <summary>The median of <paramref name="count"/> values from <paramref name="start"/>; the mean of the middle
    /// two for an even count.</summary>
    public static long Median(IReadOnlyList<long> values, int start, int count)
    {
        long[] w = new long[count];
        for (int i = 0; i < count; i++) w[i] = values[start + i];
        Array.Sort(w);
        return count % 2 == 1 ? w[count / 2] : (w[count / 2 - 1] + w[count / 2]) / 2;
    }
}
