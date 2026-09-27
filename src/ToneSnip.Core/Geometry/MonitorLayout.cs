namespace ToneSnip.Core.Geometry;

/// <summary>Comparing the monitor layout a snip screen was built for with the one Windows reports now.</summary>
public static class MonitorLayout
{
    /// <summary>
    /// True when <paramref name="a"/> and <paramref name="b"/> hold the same monitor rectangles, in any order. A
    /// monitor added or removed, moved, or switched to another resolution makes them differ; a change that leaves every
    /// rectangle as it was (colour depth, refresh rate, a scaling change, which PerMonitorV2 coordinates do not see)
    /// does not. An empty <paramref name="b"/> is an enumeration that found nothing, not a layout, and counts as the
    /// same.
    /// </summary>
    public static bool Same(IReadOnlyCollection<IntRect> a, IReadOnlyCollection<IntRect> b)
    {
        if (b.Count == 0) return true;
        if (a.Count != b.Count) return false;
        var left = a.OrderBy(r => r.Left).ThenBy(r => r.Top).ThenBy(r => r.Width).ThenBy(r => r.Height);
        var right = b.OrderBy(r => r.Left).ThenBy(r => r.Top).ThenBy(r => r.Width).ThenBy(r => r.Height);
        return left.SequenceEqual(right);
    }
}
