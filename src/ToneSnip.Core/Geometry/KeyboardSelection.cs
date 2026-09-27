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
    /// Where an arrow key moves the pointer from (<paramref name="x"/>, <paramref name="y"/>) by
    /// (<paramref name="dx"/>, <paramref name="dy"/>), across the gaps between monitors of different sizes or
    /// positions. A step that lands on a monitor goes there, so side-by-side monitors are crossed as one surface. A step
    /// that would leave the monitor stops at its edge first. From the edge, the next step jumps to the nearest monitor
    /// beyond it in that direction (its nearest pixel, so the pointer enters it as close to where it left as it can):
    /// clamping alone would leave a monitor that is offset or shorter than its neighbour unreachable from the
    /// keyboard. With nothing beyond, the pointer stays at the edge. A point on no monitor is pulled onto the nearest.
    /// </summary>
    public static (int X, int Y) Step(int x, int y, int dx, int dy, IReadOnlyList<IntRect> monitors)
    {
        int tx = x + dx, ty = y + dy;
        IntRect? here = null;
        foreach (IntRect m in monitors)
        {
            if (m.IsEmpty) continue;
            if (m.Contains(tx, ty)) return (tx, ty);
            if (m.Contains(x, y)) here ??= m;
        }
        if (here is not { } on) return ClampToMonitors(tx, ty, monitors);
        (int X, int Y) edge = (Math.Clamp(tx, on.Left, on.Right - 1), Math.Clamp(ty, on.Top, on.Bottom - 1));
        if (edge != (x, y)) return edge;
        (int X, int Y) best = (x, y);
        long bestDistance = long.MaxValue;
        foreach (IntRect m in monitors)
        {
            if (m.IsEmpty || m == on) continue;
            bool beyond = (dx > 0 && m.Left > x) || (dx < 0 && m.Right - 1 < x) || (dy > 0 && m.Top > y) || (dy < 0 && m.Bottom - 1 < y);
            if (!beyond) continue;
            int cx = Math.Clamp(x, m.Left, m.Right - 1), cy = Math.Clamp(y, m.Top, m.Bottom - 1);
            long ex = cx - x, ey = cy - y, distance = ex * ex + ey * ey;
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
