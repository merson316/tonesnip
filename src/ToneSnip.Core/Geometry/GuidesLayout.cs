namespace ToneSnip.Core.Geometry;

/// <summary>
/// The "Guides and loupe" selection frame: a white hairline edge, dashed guide lines that carry the selection's edges
/// across the monitor (or cross at the cursor while nothing is selected), a pixel loupe by the cursor with a readout
/// under it, and the size in a chip below the bottom-right corner. Physical pixels, scaled from effective pixels by
/// the monitor's DPI like <see cref="CursorPillLayout"/>.
/// </summary>
public readonly record struct GuidesLayout
{
    /// <summary>Four edges, or two crosshair lines.</summary>
    public const int MaxLines = 4;

    /// <summary>The largest readout and chip the repaint reaches allow for, in effective pixels. The readout
    /// "-3840, -2160  ·  10000 nits  peak 10000  mean 10000" is about 330 wide in 12 px Segoe UI.</summary>
    private const int LabelMaxWidth = 400, LabelMaxHeight = 32, ChipMaxWidth = 140, ChipMaxHeight = 40;

    public double Scale { get; init; }
    /// <summary>The hairline and guide lines' width.</summary>
    public int Edge { get; init; }
    /// <summary>One dash, and one gap, of a guide line.</summary>
    public int Dash { get; init; }
    /// <summary>Frame pixels shown across the loupe; odd, so the cursor's pixel is the middle one.</summary>
    public int Source { get; init; }
    /// <summary>One magnified pixel's size.</summary>
    public int Cell { get; init; }
    /// <summary>From the cursor's hot spot to the loupe's outer ring.</summary>
    public int LoupeOffset { get; init; }
    /// <summary>The loupe's white border plus its dark keyline, which together ring the magnified pixels.</summary>
    public int Ring { get; init; }
    /// <summary>From the loupe's ring to its readout.</summary>
    public int LabelGap { get; init; }
    public int LabelPadX { get; init; }
    public int LabelPadY { get; init; }
    public int LabelRadius { get; init; }
    /// <summary>From the selection's corner to the size chip.</summary>
    public int ChipGap { get; init; }
    public int ChipPadX { get; init; }
    public int ChipPadY { get; init; }
    public int ChipRadius { get; init; }
    public int ChipFontPx { get; init; }

    public int LoupeSize => Source * Cell;

    public static GuidesLayout For(double scale)
    {
        if (scale <= 0 || double.IsNaN(scale)) scale = 1.0;
        int Px(int effective) => Math.Max(1, (int)Math.Round(effective * scale));
        return new GuidesLayout
        {
            Scale = scale, Edge = Px(1), Dash = Px(4), Source = 13, Cell = Px(8), LoupeOffset = Px(20), Ring = Px(2),
            LabelGap = Px(6), LabelPadX = Px(10), LabelPadY = Px(4), LabelRadius = Px(10),
            ChipGap = Px(8), ChipPadX = Px(8), ChipPadY = Px(4), ChipRadius = Px(4), ChipFontPx = Px(12),
        };
    }

    /// <summary>
    /// Writes the guide lines, full-length strips in <paramref name="area"/>'s coordinates, and returns how many: the
    /// selection's edge rows and columns that cross the area, or with nothing selected a crosshair through the cursor
    /// at (<paramref name="cx"/>, <paramref name="cy"/>) when it is in the area.
    /// </summary>
    public int Lines(IntRect sel, int cx, int cy, IntRect area, Span<IntRect> strips)
    {
        int n = 0;
        if (!sel.IsEmpty)
        {
            n = Row(sel.Top, area, strips, n); n = Row(sel.Bottom - Edge, area, strips, n);
            n = Column(sel.Left, area, strips, n); n = Column(sel.Right - Edge, area, strips, n);
        }
        else if (area.Contains(cx, cy)) { n = Row(cy, area, strips, n); n = Column(cx, area, strips, n); }
        return n;
    }

    private int Row(int y, IntRect area, Span<IntRect> strips, int n)
    {
        if (y >= area.Top && y < area.Bottom) strips[n++] = new IntRect(area.Left, y, area.Width, Edge);
        return n;
    }

    private int Column(int x, IntRect area, Span<IntRect> strips, int n)
    {
        if (x >= area.Left && x < area.Right) strips[n++] = new IntRect(x, area.Top, Edge, area.Height);
        return n;
    }

    /// <summary>
    /// The loupe's magnified pixels (its ring lies <see cref="Ring"/> outside them) and its readout centred underneath,
    /// for a cursor at (<paramref name="cx"/>, <paramref name="cy"/>): below right of it, flipped left or up where that
    /// would leave <paramref name="area"/>.
    /// </summary>
    public (IntRect Loupe, IntRect Label) Loupe(int cx, int cy, int labelWidth, int labelHeight, IntRect area)
    {
        int size = LoupeSize, outer = size + 2 * Ring;
        int x = cx + LoupeOffset;
        if (x + outer > area.Right) x = cx - LoupeOffset - outer;
        int block = outer + LabelGap + labelHeight;
        int y = cy + LoupeOffset;
        if (y + block > area.Bottom) y = cy - LoupeOffset - block;
        x = Math.Max(area.Left, Math.Min(x, area.Right - outer));
        y = Math.Max(area.Top, Math.Min(y, area.Bottom - block));
        var loupe = new IntRect(x + Ring, y + Ring, size, size);
        int lx = loupe.Left + size / 2 - labelWidth / 2;
        lx = Math.Max(area.Left, Math.Min(lx, area.Right - labelWidth));
        return (loupe, new IntRect(lx, y + outer + LabelGap, labelWidth, labelHeight));
    }

    /// <summary>Everything a loupe and readout beside a cursor at (<paramref name="cx"/>, <paramref name="cy"/>) can
    /// cover, wherever they flipped to.</summary>
    public IntRect LoupeReach(int cx, int cy)
    {
        double s = Scale > 0 ? Scale : 1.0;
        int outer = LoupeSize + 2 * Ring;
        int rx = LoupeOffset + outer + (int)Math.Ceiling(LabelMaxWidth * s);
        int ry = LoupeOffset + outer + LabelGap + (int)Math.Ceiling(LabelMaxHeight * s);
        return new IntRect(cx - rx, cy - ry, 2 * rx, 2 * ry);
    }

    /// <summary>The size chip: right-aligned below the selection's bottom-right corner, or inside that corner when there
    /// is no room below, kept inside <paramref name="area"/>.</summary>
    public IntRect Chip(IntRect sel, int textWidth, int textHeight, IntRect area)
    {
        int w = textWidth + 2 * ChipPadX, h = textHeight + 2 * ChipPadY;
        int x = sel.Right - w, y = sel.Bottom + ChipGap;
        if (y + h > area.Bottom) { x = sel.Right - ChipGap - w; y = sel.Bottom - ChipGap - h; }
        x = Math.Max(area.Left, Math.Min(x, area.Right - w));
        return new IntRect(x, y, w, h);
    }

    /// <summary>The selection's hairline and its chip, below or inside the bottom-right corner. The guide lines are
    /// separate strips (<see cref="Lines"/>).</summary>
    public IntRect Reach(IntRect sel)
    {
        if (sel.IsEmpty) return IntRect.Empty;
        double s = Scale > 0 ? Scale : 1.0;
        int pad = Edge + 1, maxW = (int)Math.Ceiling(ChipMaxWidth * s), maxH = (int)Math.Ceiling(ChipMaxHeight * s);
        // Right: a chip pushed off the monitor's left edge ends at most its own width right of that edge, and the
        // corner is on this monitor whenever a chip is drawn.
        return IntRect.FromLtrb(
            Math.Min(sel.Left - pad, sel.Right - ChipGap - maxW),
            Math.Min(sel.Top - pad, sel.Bottom - ChipGap - maxH),
            sel.Right + Math.Max(pad, maxW),
            Math.Max(sel.Bottom + pad, sel.Bottom + ChipGap + maxH));
    }
}
