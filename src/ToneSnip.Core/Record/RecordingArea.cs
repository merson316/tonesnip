using ToneSnip.Core.Geometry;

namespace ToneSnip.Core.Record;

/// <summary>
/// The part of the screen a selection records. A recording captures one monitor, so a selection that spans several
/// keeps the part on the monitor it covers most. The area's sides are even, since 4:2:0 video keeps one colour sample
/// per 2 x 2 pixels, and at least <see cref="MinSide"/>, which every encoder takes; a smaller selection grows about
/// its centre, inside its monitor.
/// </summary>
public static class RecordingArea
{
    public const int MinSide = 64;

    /// <summary>The monitor and the area on it, in the same virtual-desktop pixels as the arguments; null when the
    /// selection touches no monitor, or the monitor is smaller than <see cref="MinSide"/>.</summary>
    public static (IntRect Monitor, IntRect Area)? For(IntRect selection, IReadOnlyList<IntRect> monitors)
    {
        IntRect best = IntRect.Empty;
        long covered = 0;
        foreach (IntRect m in monitors)
        {
            IntRect hit = selection.Intersect(m);
            long area = hit.IsEmpty ? 0 : (long)hit.Width * hit.Height;
            if (area > covered) { covered = area; best = m; }
        }
        if (covered == 0 || best.Width < MinSide || best.Height < MinSide) return null;
        IntRect cut = selection.Intersect(best);
        (int left, int width) = Side(cut.Left, cut.Width, best.Left, best.Width);
        (int top, int height) = Side(cut.Top, cut.Height, best.Top, best.Height);
        return (best, new IntRect(left, top, width, height));
    }

    /// <summary>One axis: grown to <see cref="MinSide"/> about its centre and kept inside the monitor, then made even by
    /// dropping its last pixel.</summary>
    private static (int Start, int Length) Side(int start, int length, int monitorStart, int monitorLength)
    {
        if (length < MinSide)
        {
            start -= (MinSide - length) / 2;
            length = MinSide;
            start = Math.Clamp(start, monitorStart, monitorStart + monitorLength - length);
        }
        return (start, length & ~1);
    }
}
