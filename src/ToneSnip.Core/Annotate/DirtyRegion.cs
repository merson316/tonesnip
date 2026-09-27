using ToneSnip.Core.Geometry;

namespace ToneSnip.Core.Annotate;

/// <summary>
/// The rectangles to repaint when a shape changes from one state to another: the <see cref="Shape.DirtyParts"/> of
/// both, merged only where merging costs no extra area. A single bounding box would do, but for a callout whose lens
/// is across the picture from its source it would cover most of the picture on every move.
/// </summary>
public static class DirtyRegion
{
    /// <param name="before">The shape as it was, or null when it is new.</param>
    /// <param name="after">The shape as it is, or null when it is gone.</param>
    /// <param name="pad">Grown on every side, for handles and selection outlines drawn round the parts.</param>
    public static List<IntRect> Of(Shape? before, Shape? after, int pad = 0)
    {
        var rects = new List<IntRect>();
        foreach (Shape? s in new[] { before, after })
            if (s != null)
                foreach (IntRect r in s.DirtyParts)
                    if (!r.IsEmpty) rects.Add(pad == 0 ? r : IntRect.FromLtrb(r.Left - pad, r.Top - pad, r.Right + pad, r.Bottom + pad));
        return Merge(rects);
    }

    /// <summary>Joins pairs whose bounding box is no bigger than the two apart (a part and its slightly moved self),
    /// until no pair qualifies. Pieces along a diagonal stay apart.</summary>
    public static List<IntRect> Merge(List<IntRect> rects)
    {
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < rects.Count && !merged; i++)
                for (int j = i + 1; j < rects.Count; j++)
                {
                    IntRect u = rects[i].Union(rects[j]);
                    if (Area(u) > Area(rects[i]) + Area(rects[j])) continue;
                    rects[i] = u; rects.RemoveAt(j); merged = true; break;
                }
        }
        return rects;
    }

    private static long Area(IntRect r) => (long)r.Width * r.Height;
}
