using ToneSnip.Core.Geometry;

namespace ToneSnip.Core.Annotate;

public enum Handle { None, N, NE, E, SE, S, SW, W, NW, Start, End }

public static class Handles
{
    public const int MinSize = 2;

    public static IReadOnlyList<(Handle Kind, int X, int Y)> Of(Shape s) => s switch
    {
        LineShape l => new[] { (Handle.Start, l.X1, l.Y1), (Handle.End, l.X2, l.Y2) },
        BoxShape b => OfRect(b.Rect),
        RedactShape r => OfRect(r.Rect),
        _ => Array.Empty<(Handle, int, int)>(),
    };

    private static (Handle, int, int)[] OfRect(IntRect r)
    {
        int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2, right = r.Right - 1, bottom = r.Bottom - 1;
        return new[]
        {
            (Handle.NW, r.Left, r.Top), (Handle.N, cx, r.Top), (Handle.NE, right, r.Top), (Handle.E, right, cy),
            (Handle.SE, right, bottom), (Handle.S, cx, bottom), (Handle.SW, r.Left, bottom), (Handle.W, r.Left, cy),
        };
    }

    /// <summary>The handle whose square of side <paramref name="size"/> contains the point, corners first.</summary>
    public static Handle Hit(Shape s, int x, int y, int size)
    {
        int half = size / 2;
        foreach ((Handle kind, int hx, int hy) in Of(s)) if (Math.Abs(hx - x) <= half && Math.Abs(hy - y) <= half) return kind;
        return Handle.None;
    }

    public static IntRect Resize(IntRect r, Handle h, int dx, int dy)
    {
        int l = r.Left, t = r.Top, rt = r.Right, b = r.Bottom;
        if (h is Handle.NW or Handle.W or Handle.SW) l += dx;
        if (h is Handle.NE or Handle.E or Handle.SE) rt += dx;
        if (h is Handle.NW or Handle.N or Handle.NE) t += dy;
        if (h is Handle.SW or Handle.S or Handle.SE) b += dy;
        // Dragging an edge past the opposite one flips the rectangle rather than pinning it at the minimum size.
        if (rt < l) (l, rt) = (rt, l);
        if (b < t) (t, b) = (b, t);
        if (rt - l < MinSize) rt = l + MinSize;
        if (b - t < MinSize) b = t + MinSize;
        return IntRect.FromLtrb(l, t, rt, b);
    }
}
