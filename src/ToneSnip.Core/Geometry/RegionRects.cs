namespace ToneSnip.Core.Geometry;

/// <summary>
/// A GDI region's rectangles, turned back into something worth painting one by one. A region is stored in horizontal
/// bands, so every rectangle is cut wherever any other rectangle starts or ends: a monitor-tall guide line beside a box
/// arrives as three slivers. Joining slivers that touch and share both side edges leaves roughly one rectangle per strip
/// or box that was invalidated.
/// </summary>
public static class RegionRects
{
    /// <summary>
    /// Joins <paramref name="bands"/> (a region's disjoint rectangles, in its top-to-bottom band order) into
    /// <paramref name="into"/> and returns how many were written. The result covers exactly the same pixels, still with
    /// no overlap. <paramref name="into"/> may be <paramref name="bands"/> itself: a rectangle is only ever written at or
    /// before the index it was read from.
    /// </summary>
    public static int Join(ReadOnlySpan<IntRect> bands, Span<IntRect> into)
    {
        int n = 0;
        for (int i = 0; i < bands.Length; i++)
        {
            IntRect r = bands[i];
            // The latest piece of the same column that ends where this one starts. Two pieces with the same columns
            // and the same bottom would overlap, so there is at most one.
            int j = n - 1;
            while (j >= 0 && !(into[j].Left == r.Left && into[j].Right == r.Right && into[j].Bottom == r.Top)) j--;
            if (j >= 0) into[j] = IntRect.FromLtrb(r.Left, into[j].Top, r.Right, r.Bottom);
            else into[n++] = r;
        }
        return n;
    }
}
