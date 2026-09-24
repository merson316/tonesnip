namespace ToneSnip.Core.Geometry;

/// <summary>The pure parts of driving the snip overlay from the keyboard: where the arrow keys may put the cursor, and
/// where Tab and Shift+Tab go in a list.</summary>
public static class KeyboardSelection
{
    /// <summary>
    /// The point nearest (<paramref name="x"/>, <paramref name="y"/>) on any of <paramref name="monitors"/>: the point
    /// itself when a monitor has it, otherwise the closest edge pixel of the nearest monitor. Clamping to the desktop's
    /// bounding box instead would let the cursor sit in a gap between monitors of different sizes, where nothing is drawn
    /// and nothing can be selected. The point as it is when there are no monitors.
    /// </summary>
    public static (int X, int Y) ClampToMonitors(int x, int y, IReadOnlyList<IntRect> monitors)
    {
        (int X, int Y) best = (x, y);
        long bestDistance = long.MaxValue;
        foreach (IntRect m in monitors)
        {
            if (m.IsEmpty) continue;
            if (m.Contains(x, y)) return (x, y);
            int cx = Math.Clamp(x, m.Left, m.Right - 1), cy = Math.Clamp(y, m.Top, m.Bottom - 1);
            long dx = cx - x, dy = cy - y, distance = dx * dx + dy * dy;
            if (distance < bestDistance) { best = (cx, cy); bestDistance = distance; }
        }
        return best;
    }

    /// <summary>
    /// The next place in a list of <paramref name="count"/> after <paramref name="current"/>, <paramref name="step"/>
    /// places on and wrapping round. <paramref name="current"/> is -1 before the first press, so Tab lands on the first
    /// item and Shift+Tab on the last. -1 for an empty list.
    /// </summary>
    public static int Cycle(int current, int step, int count)
    {
        if (count <= 0) return -1;
        if (current < 0 || current >= count) current = step < 0 ? 0 : -1;
        return ((current + step) % count + count) % count;
    }
}
