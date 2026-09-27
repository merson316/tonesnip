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
    /// <summary>The shape changes pixels far outside its own bounds (a spotlight dims everything around it). Moving or
    /// resizing one still changes only its old and new areas; adding the first or removing the last repaints the whole
    /// picture (see <see cref="AnnotationDoc"/>).</summary>
    public virtual bool AffectsWholeImage => false;
    /// <summary>The areas whose pixels the shape sets, for invalidation. One rectangle for most kinds; a shape spread
    /// over the picture (a magnifier's source, lens and the line between them) gives several, so moving it does not
    /// repaint the untouched picture between its parts.</summary>
    public virtual IEnumerable<IntRect> DirtyParts => new[] { DirtyBounds };

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

/// <param name="Boxed">Drawn on a filled rounded box of <paramref name="Color"/> with contrasting letters, for text over
/// a busy picture, rather than as coloured letters with a dark outline.</param>
public sealed record TextShape(int Id, int X, int Y, string Text, int Size, uint Color, bool Boxed = false) : Shape(Id)
{
    /// <summary>How far a box reaches past the letters' line box on the left and right; 0 unboxed.</summary>
    public int BoxPadX => Boxed ? (int)Math.Ceiling(Size * 0.35) : 0;
    /// <summary>How far a box reaches above and below the letters' line box; 0 unboxed.</summary>
    public int BoxPadY => Boxed ? (int)Math.Ceiling(Size * 0.12) : 0;

    /// <summary>Core cannot measure fonts; this estimate is for hit-testing. <see cref="DirtyBounds"/> pads it further for invalidation.</summary>
    public override IntRect Bounds
    {
        get
        {
            string[] lines = Text.Split('\n');
            int w = (int)Math.Ceiling(lines.Max(l => l.Length) * Size * 0.6) + 4, h = (int)Math.Ceiling(lines.Length * Size * 1.3) + 4;
            return new IntRect(X - BoxPadX, Y - BoxPadY, Math.Max(w, 8) + 2 * BoxPadX, Math.Max(h, 8) + 2 * BoxPadY);
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
            return new IntRect(X - 4 - BoxPadX, Y - 4 - BoxPadY, Math.Max(w, 8) + 2 * BoxPadX, Math.Max(h, 8) + 2 * BoxPadY);
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

/// <summary>A lit rectangle: everything outside the union of a document's spotlights is dimmed (see
/// <see cref="Imaging.Emphasis"/>). It draws nothing of its own.</summary>
public sealed record SpotlightShape(int Id, IntRect Rect) : Shape(Id)
{
    public override IntRect Bounds => Rect;
    public override bool AffectsWholeImage => true;
    public override bool HitTest(int x, int y) => Rect.Contains(x, y);
    public override Shape Moved(int dx, int dy) => this with { Rect = Rect.Offset(dx, dy) };
    public override Shape? Resized(Handle h, int dx, int dy) => this with { Rect = Handles.Resize(Rect, h, dx, dy) };
}

/// <summary>
/// A magnifier callout: the pixels of <paramref name="Source"/>, enlarged <paramref name="Zoom"/> times, in a rounded
/// lens centred on (<paramref name="LensX"/>, <paramref name="LensY"/>), with a border round both and a line between
/// them. The lens copies pixels (see <see cref="Imaging.Emphasis"/>); the border and the line are drawn like any
/// stroke.
/// </summary>
public sealed record MagnifierShape(int Id, IntRect Source, int LensX, int LensY, int Width, uint Color, float Zoom = MagnifierShape.DefaultZoom) : Shape(Id)
{
    public const float DefaultZoom = 2f, MinZoom = 1.5f, MaxZoom = 8f;

    /// <summary>The source scaled by <see cref="Zoom"/> in both directions, so a lens never distorts; each lens pixel
    /// takes the nearest source pixel (Emphasis), which keeps screenshot detail crisp.</summary>
    public IntRect Lens
    {
        get
        {
            int w = Math.Max(1, (int)Math.Round(Source.Width * Zoom)), h = Math.Max(1, (int)Math.Round(Source.Height * Zoom));
            return new IntRect(LensX - w / 2, LensY - h / 2, w, h);
        }
    }
    /// <summary>The lens's corner radius: a fraction of its shorter side, up to a cap.</summary>
    public int LensRadius => Math.Clamp(Math.Min(Lens.Width, Lens.Height) / 8, 2, 16);
    /// <summary>The source outline and the connector are lighter than the lens border.</summary>
    public float ThinWidth => Math.Max(1.5f, Width / 2f);

    public override IntRect Bounds
    {
        get
        {
            int pad = Width / 2 + 1;
            IntRect u = Source.Union(Lens);
            return IntRect.FromLtrb(u.Left - pad, u.Top - pad, u.Right + pad, u.Bottom + pad);
        }
    }

    /// <summary>The source and the lens, each with room for its border, and the connector in short pieces: a callout
    /// whose lens is far from its source covers a sliver of the rectangle around both.</summary>
    public override IEnumerable<IntRect> DirtyParts
    {
        get
        {
            int pad = Width / 2 + 2;
            var parts = new List<IntRect> { Grow(Source, pad), Grow(Lens, pad) };
            if (Connector() is var ((x1, y1), (x2, y2)))
            {
                int len = Math.Max(Math.Abs(x2 - x1), Math.Abs(y2 - y1)), steps = Math.Max(1, (len + ConnectorPiece - 1) / ConnectorPiece);
                for (int i = 0; i < steps; i++)
                {
                    int ax = x1 + (x2 - x1) * i / steps, ay = y1 + (y2 - y1) * i / steps;
                    int bx = x1 + (x2 - x1) * (i + 1) / steps, by = y1 + (y2 - y1) * (i + 1) / steps;
                    parts.Add(IntRect.FromLtrb(Math.Min(ax, bx) - pad, Math.Min(ay, by) - pad, Math.Max(ax, bx) + pad + 1, Math.Max(ay, by) + pad + 1));
                }
            }
            return parts;
        }
    }

    /// <summary>The longest stretch of connector one dirty rectangle covers.</summary>
    private const int ConnectorPiece = 96;

    private static IntRect Grow(IntRect r, int by) => IntRect.FromLtrb(r.Left - by, r.Top - by, r.Right + by, r.Bottom + by);

    public override bool HitTest(int x, int y)
    {
        if (Lens.Contains(x, y) || Source.Contains(x, y)) return true;
        return Connector() is var ((x1, y1), (x2, y2)) && DistToSegment(x, y, x1, y1, x2, y2) <= ThinWidth / 2 + 3;
    }

    public override Shape Moved(int dx, int dy) => this with { Source = Source.Offset(dx, dy), LensX = LensX + dx, LensY = LensY + dy };

    /// <summary>
    /// The source's handles resize the source, the lens following at the same zoom about its own centre. The lens's
    /// corner handles change the zoom with the source fixed and the opposite corner staying put; the lens keeps the
    /// source's shape, so the corner follows whichever way it was dragged further. <see cref="Handle.End"/> (what a
    /// press inside the lens takes, see <see cref="EditSession"/>) moves the lens alone.
    /// </summary>
    public override Shape? Resized(Handle h, int dx, int dy)
    {
        switch (h)
        {
            case Handle.End: return this with { LensX = LensX + dx, LensY = LensY + dy };
            case Handle.LensNW or Handle.LensNE or Handle.LensSE or Handle.LensSW:
            {
                IntRect l = Lens;
                bool west = h is Handle.LensNW or Handle.LensSW, north = h is Handle.LensNW or Handle.LensNE;
                int ax = west ? l.Right : l.Left, ay = north ? l.Bottom : l.Top;   // the corner that stays
                int cx = (west ? l.Left : l.Right) + dx, cy = (north ? l.Top : l.Bottom) + dy;
                float zoom = Math.Max(Math.Abs(cx - ax) / (float)Math.Max(1, Source.Width), Math.Abs(cy - ay) / (float)Math.Max(1, Source.Height));
                zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
                int w = Math.Max(1, (int)Math.Round(Source.Width * zoom)), hgt = Math.Max(1, (int)Math.Round(Source.Height * zoom));
                int left = west ? ax - w : ax, top = north ? ay - hgt : ay;
                return this with { Zoom = zoom, LensX = left + w / 2, LensY = top + hgt / 2 };
            }
            default: return this with { Source = Handles.Resize(Source, h, dx, dy) };
        }
    }

    /// <summary>
    /// The callout with its lens inside <paramref name="area"/>: the zoom lowered (not below <see cref="MinZoom"/>)
    /// until the lens fits, then the lens slid in. The source stays where it is. Unchanged for an empty area.
    /// </summary>
    public MagnifierShape ClampedTo(IntRect area)
    {
        if (area.IsEmpty) return this;
        MagnifierShape m = this;
        float fit = Math.Min(area.Width / (float)Math.Max(1, Source.Width), area.Height / (float)Math.Max(1, Source.Height));
        if (Zoom > fit) m = m with { Zoom = Math.Max(MinZoom, fit) };
        IntRect l = m.Lens;
        int dx = l.Width >= area.Width ? area.Left + area.Width / 2 - (l.Left + l.Width / 2)
               : l.Left < area.Left ? area.Left - l.Left : l.Right > area.Right ? area.Right - l.Right : 0;
        int dy = l.Height >= area.Height ? area.Top + area.Height / 2 - (l.Top + l.Height / 2)
               : l.Top < area.Top ? area.Top - l.Top : l.Bottom > area.Bottom ? area.Bottom - l.Bottom : 0;
        return dx == 0 && dy == 0 && ReferenceEquals(m, this) ? this : m with { LensX = m.LensX + dx, LensY = m.LensY + dy };
    }

    /// <summary>The line from the source to the lens: along the line between their centres, from where it leaves the
    /// source to where it meets the lens. Null when they overlap, where a line would only add clutter.</summary>
    public ((int X, int Y) From, (int X, int Y) To)? Connector()
    {
        IntRect lens = Lens;
        if (Source.IntersectsWith(lens)) return null;
        double sx = Source.Left + Source.Width / 2.0, sy = Source.Top + Source.Height / 2.0;
        double dx = lens.Left + lens.Width / 2.0 - sx, dy = lens.Top + lens.Height / 2.0 - sy;
        if (dx == 0 && dy == 0) return null;
        // How far along the centre line each rectangle's edge is: 0 at the source's centre, 1 at the lens's.
        double exit = Edge(Source.Width / 2.0, Source.Height / 2.0), enter = 1 - Edge(lens.Width / 2.0, lens.Height / 2.0);
        if (enter <= exit) return null;
        return (((int)Math.Round(sx + dx * exit), (int)Math.Round(sy + dy * exit)), ((int)Math.Round(sx + dx * enter), (int)Math.Round(sy + dy * enter)));

        double Edge(double halfW, double halfH) => Math.Min(dx == 0 ? double.MaxValue : halfW / Math.Abs(dx), dy == 0 ? double.MaxValue : halfH / Math.Abs(dy));
    }

    /// <summary>
    /// Where a new lens goes for <paramref name="source"/> at the default zoom: a small gap to the right of it, or else
    /// to the left, below or above, whichever first fits inside <paramref name="area"/> (the picture, or the monitor
    /// it is on). When none fits, the side with the most room, pulled inside the area. Returns the lens centre.
    /// </summary>
    public static (int X, int Y) PlaceLens(IntRect source, IntRect area)
    {
        int w = (int)Math.Round(source.Width * DefaultZoom), h = (int)Math.Round(source.Height * DefaultZoom);
        int gap = Math.Clamp(Math.Min(source.Width, source.Height) / 3, 8, 32);
        int cx = source.Left + source.Width / 2, cy = source.Top + source.Height / 2;
        // Lens centres as Lens computes them: its left edge is the centre less half its width.
        (int X, int Y)[] sides =
        {
            (source.Right + gap + w / 2, cy), (source.Left - gap - w + w / 2, cy),
            (cx, source.Bottom + gap + h / 2), (cx, source.Top - gap - h + h / 2),
        };
        if (area.IsEmpty) return sides[0];
        foreach ((int x, int y) in sides)
            if (Fits(x, y)) return (x, y);
        int[] room = { area.Right - source.Right, source.Left - area.Left, area.Bottom - source.Bottom, source.Top - area.Top };
        (int bx, int by) = sides[Array.IndexOf(room, room.Max())];
        // Pulled in as far as it goes; a lens bigger than the area is centred on it.
        bx = w >= area.Width ? area.Left + area.Width / 2 : Math.Clamp(bx, area.Left + w / 2, area.Right - w + w / 2);
        by = h >= area.Height ? area.Top + area.Height / 2 : Math.Clamp(by, area.Top + h / 2, area.Bottom - h + h / 2);
        return (bx, by);

        bool Fits(int x, int y)
        {
            var lens = new IntRect(x - w / 2, y - h / 2, w, h);
            return lens.Left >= area.Left && lens.Top >= area.Top && lens.Right <= area.Right && lens.Bottom <= area.Bottom;
        }
    }
}
