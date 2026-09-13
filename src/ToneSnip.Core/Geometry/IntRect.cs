namespace ToneSnip.Core.Geometry;

/// <summary>Integer rectangle in virtual-desktop pixels. Right and Bottom are exclusive.</summary>
public readonly record struct IntRect(int Left, int Top, int Width, int Height)
{
    public static readonly IntRect Empty = default;
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static IntRect FromLtrb(int left, int top, int right, int bottom) => new(left, top, right - left, bottom - top);

    /// <summary>Rectangle covering both drag corners inclusively, in any drag direction.</summary>
    public static IntRect FromDrag(int x1, int y1, int x2, int y2)
        => FromLtrb(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2) + 1, Math.Max(y1, y2) + 1);

    public bool Contains(int x, int y) => x >= Left && y >= Top && x < Right && y < Bottom;
    public bool IntersectsWith(IntRect o) => !Intersect(o).IsEmpty;

    public IntRect Intersect(IntRect o)
    {
        int l = Math.Max(Left, o.Left), t = Math.Max(Top, o.Top), r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
        return r > l && b > t ? FromLtrb(l, t, r, b) : Empty;
    }

    public IntRect Union(IntRect o)
    {
        if (IsEmpty) return o;
        if (o.IsEmpty) return this;
        return FromLtrb(Math.Min(Left, o.Left), Math.Min(Top, o.Top), Math.Max(Right, o.Right), Math.Max(Bottom, o.Bottom));
    }

    public IntRect Offset(int dx, int dy) => this with { Left = Left + dx, Top = Top + dy };

    /// <summary>The part of this rectangle inside <paramref name="bounds"/>.</summary>
    public IntRect Clamp(IntRect bounds) => Intersect(bounds);

    /// <summary>Moves by (dx,dy) without leaving <paramref name="bounds"/>; size is kept.</summary>
    public IntRect Nudge(int dx, int dy, IntRect bounds)
    {
        int l = Math.Clamp(Left + dx, bounds.Left, Math.Max(bounds.Left, bounds.Right - Width));
        int t = Math.Clamp(Top + dy, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - Height));
        return new IntRect(l, t, Width, Height);
    }

    public override string ToString() => $"{Width}x{Height} at ({Left},{Top})";
}
