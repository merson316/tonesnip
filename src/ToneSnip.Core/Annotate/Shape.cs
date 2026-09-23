using ToneSnip.Core.Geometry;

namespace ToneSnip.Core.Annotate;

/// <summary>One annotation in source-frame pixels. Immutable; edits produce new records with the same Id.</summary>
public abstract record Shape(int Id)
{
    public abstract IntRect Bounds { get; }
    /// <summary>Bounds generous enough for invalidation; hit-testing uses <see cref="Bounds"/>.</summary>
    public virtual IntRect DirtyBounds => Bounds;
    public abstract bool HitTest(int x, int y);
    public abstract Shape Moved(int dx, int dy);
    /// <summary>Null for kinds without handles.</summary>
    public virtual Shape? Resized(Handle h, int dx, int dy) => null;
    public virtual bool IsRedaction => false;

    protected static double DistToSegment(int px, int py, int x1, int y1, int x2, int y2)
    {
        double dx = x2 - x1, dy = y2 - y1, len2 = dx * dx + dy * dy;
        double t = len2 == 0 ? 0 : Math.Clamp(((px - x1) * dx + (py - y1) * dy) / len2, 0, 1);
        double cx = x1 + t * dx - px, cy = y1 + t * dy - py;
        return Math.Sqrt(cx * cx + cy * cy);
    }

    protected static IntRect BoundsOf(IEnumerable<(int X, int Y)> pts, int pad)
    {
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        foreach ((int x, int y) in pts) { l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); b = Math.Max(b, y); }
        if (l == int.MaxValue) return IntRect.Empty;
        return IntRect.FromLtrb(l - pad, t - pad, r + pad + 1, b + pad + 1);
    }
}

public sealed record PenShape(int Id, IReadOnlyList<(int X, int Y)> Points, int Width, uint Color, bool Highlighter) : Shape(Id)
{
    /// <summary>The points' extent, remembered: the renderer, hit-testing and invalidation ask for the bounds many times
    /// a paint, and a stroke can have thousands of points.</summary>
    private readonly Extent _extent = new();

    /// <summary>A copy made by <c>with</c> gets its own memo rather than sharing the original's, which would thrash
    /// between the two while a stroke is dragged. It copies every property by hand, so a new one must be added
    /// here.</summary>
    private PenShape(PenShape original) : base(original)
    {
        Points = original.Points; Width = original.Width; Color = original.Color; Highlighter = original.Highlighter;
        _extent = new();   // field initialisers do not run in a record's copy constructor
    }

    public override IntRect Bounds => _extent.Of(Points) is var (l, t, r, b) ? IntRect.FromLtrb(l - Pad, t - Pad, r + Pad + 1, b + Pad + 1) : IntRect.Empty;
    private int Pad => Width / 2 + 1;
    public override bool HitTest(int x, int y)
    {
        double tol = Width / 2.0 + 3;
        if (Points.Count == 1) return Math.Abs(Points[0].X - x) <= tol && Math.Abs(Points[0].Y - y) <= tol;
        for (int i = 1; i < Points.Count; i++) if (DistToSegment(x, y, Points[i - 1].X, Points[i - 1].Y, Points[i].X, Points[i].Y) <= tol) return true;
        return false;
    }
    public override Shape Moved(int dx, int dy) => this with { Points = Points.Select(p => (p.X + dx, p.Y + dy)).ToArray() };

    /// <summary>
    /// The inclusive extent of a point list, computed once per list. The in-progress stroke's list only ever grows, so
    /// more points on the same list extend the extent by the new ones. Swapped as one immutable snapshot, since output
    /// renders read shapes off the UI thread. Every memo equals every other, so it never affects the record's equality.
    /// </summary>
    private sealed class Extent
    {
        private sealed record Snapshot(IReadOnlyList<(int X, int Y)> Points, int Count, int L, int T, int R, int B);
        private volatile Snapshot? _last;

        public (int L, int T, int R, int B)? Of(IReadOnlyList<(int X, int Y)> points)
        {
            if (points.Count == 0) return null;
            Snapshot? s = _last;
            if (s == null || !ReferenceEquals(s.Points, points) || s.Count > points.Count)
                s = new Snapshot(points, 0, int.MaxValue, int.MaxValue, int.MinValue, int.MinValue);
            if (s.Count < points.Count)
            {
                int l = s.L, t = s.T, r = s.R, b = s.B;
                for (int i = s.Count; i < points.Count; i++)
                {
                    (int x, int y) = points[i];
                    if (x < l) l = x;
                    if (y < t) t = y;
                    if (x > r) r = x;
                    if (y > b) b = y;
                }
                _last = s = new Snapshot(points, points.Count, l, t, r, b);
            }
            return (s.L, s.T, s.R, s.B);
        }

        public override bool Equals(object? obj) => obj is Extent;
        public override int GetHashCode() => 0;
    }
}

public sealed record LineShape(int Id, int X1, int Y1, int X2, int Y2, int Width, uint Color, bool Arrow) : Shape(Id)
{
    public int HeadSize => Width * 3;
    public override IntRect Bounds => BoundsOf(new[] { (X1, Y1), (X2, Y2) }, (Arrow ? HeadSize : Width / 2) + 1);
    public override bool HitTest(int x, int y) => DistToSegment(x, y, X1, Y1, X2, Y2) <= Width / 2.0 + 3;
    public override Shape Moved(int dx, int dy) => this with { X1 = X1 + dx, Y1 = Y1 + dy, X2 = X2 + dx, Y2 = Y2 + dy };
    public override Shape? Resized(Handle h, int dx, int dy) => h switch
    {
        Handle.Start => this with { X1 = X1 + dx, Y1 = Y1 + dy },
        Handle.End => this with { X2 = X2 + dx, Y2 = Y2 + dy },
        _ => null,
    };
}

public sealed record BoxShape(int Id, IntRect Rect, int Width, uint Color, bool Ellipse, bool Filled) : Shape(Id)
{
    public override IntRect Bounds => IntRect.FromLtrb(Rect.Left - Width / 2 - 1, Rect.Top - Width / 2 - 1, Rect.Right + Width / 2 + 1, Rect.Bottom + Width / 2 + 1);
    public override bool HitTest(int x, int y)
    {
        double tol = Width / 2.0 + 3;
        if (!Ellipse)
        {
            bool inOuter = x >= Rect.Left - tol && x < Rect.Right + tol && y >= Rect.Top - tol && y < Rect.Bottom + tol;
            bool inInner = x >= Rect.Left + tol && x < Rect.Right - tol && y >= Rect.Top + tol && y < Rect.Bottom - tol;
            return Filled ? inOuter : inOuter && !inInner;
        }
        double cx = Rect.Left + Rect.Width / 2.0, cy = Rect.Top + Rect.Height / 2.0, rx = Rect.Width / 2.0, ry = Rect.Height / 2.0;
        if (rx < 1 || ry < 1) return false;
        double nx = (x + 0.5 - cx) / rx, ny = (y + 0.5 - cy) / ry, d = Math.Sqrt(nx * nx + ny * ny);
        double band = tol / Math.Min(rx, ry);
        return Filled ? d <= 1 + band : Math.Abs(d - 1) <= band;
    }
    public override Shape Moved(int dx, int dy) => this with { Rect = Rect.Offset(dx, dy) };
    public override Shape? Resized(Handle h, int dx, int dy) => this with { Rect = Handles.Resize(Rect, h, dx, dy) };
}

public sealed record TextShape(int Id, int X, int Y, string Text, int Size, uint Color) : Shape(Id)
{
    /// <summary>Core cannot measure fonts; this estimate is for hit-testing. <see cref="DirtyBounds"/> pads it further for invalidation.</summary>
    public override IntRect Bounds
    {
        get
        {
            string[] lines = Text.Split('\n');
            int w = (int)Math.Ceiling(lines.Max(l => l.Length) * Size * 0.6) + 4, h = (int)Math.Ceiling(lines.Length * Size * 1.3) + 4;
            return new IntRect(X, Y, Math.Max(w, 8), Math.Max(h, 8));
        }
    }
    /// <summary>Wider and taller than <see cref="Bounds"/>, since the real glyph metrics (which Core cannot compute) can
    /// overrun the estimate; used for invalidation, not hit-testing.</summary>
    public override IntRect DirtyBounds
    {
        get
        {
            string[] lines = Text.Split('\n');
            int w = (int)Math.Ceiling(lines.Max(l => l.Length) * Size * 1.0) + 8, h = (int)Math.Ceiling(lines.Length * Size * 1.4) + 8;
            return new IntRect(X - 4, Y - 4, Math.Max(w, 8), Math.Max(h, 8));
        }
    }
    public override bool HitTest(int x, int y) => Bounds.Contains(x, y);
    public override Shape Moved(int dx, int dy) => this with { X = X + dx, Y = Y + dy };
}

public sealed record CounterShape(int Id, int X, int Y, int Number, int Size, uint Color) : Shape(Id)
{
    public int Radius => Size;
    public override IntRect Bounds => new(X - Radius - 1, Y - Radius - 1, 2 * Radius + 3, 2 * Radius + 3);
    public override bool HitTest(int x, int y) { int dx = x - X, dy = y - Y; return dx * dx + dy * dy <= (Radius + 3) * (Radius + 3); }
    public override Shape Moved(int dx, int dy) => this with { X = X + dx, Y = Y + dy };
}

/// <summary>Blur or pixelate. <see cref="Strength"/> is the style width the shape was made with; the renderer maps it to a radius or block.</summary>
public sealed record RedactShape(int Id, IntRect Rect, int Strength, bool Blur, bool Private, uint Seed) : Shape(Id)
{
    public override IntRect Bounds => Rect;
    public override bool IsRedaction => true;
    public override bool HitTest(int x, int y) => Rect.Contains(x, y);
    public override Shape Moved(int dx, int dy) => this with { Rect = Rect.Offset(dx, dy) };
    public override Shape? Resized(Handle h, int dx, int dy) => this with { Rect = Handles.Resize(Rect, h, dx, dy) };
}
