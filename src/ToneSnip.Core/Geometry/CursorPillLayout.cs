namespace ToneSnip.Core.Geometry;

/// <summary>
/// The overlay's cursor pill (selection size and nits readout), in the physical pixels the overlay paints in. The
/// metrics are effective pixels scaled by the monitor's DPI: the app is PerMonitorV2, so Windows scales none of it,
/// and a fixed 12 px font reads half-size at 200% while the scaled cursor covers the pill.
/// </summary>
/// <param name="Scale">Physical pixels per effective pixel on the pill's monitor (DPI / 96).</param>
/// <param name="FontPx">Text height, as GDI's negative lfHeight.</param>
/// <param name="PadX">Space left and right of the text.</param>
/// <param name="PadY">Space above and below the text.</param>
/// <param name="Radius">Corner radius.</param>
/// <param name="OffsetX">From the cursor's hot spot to the pill, when it sits below right.</param>
/// <param name="OffsetY">From the cursor's hot spot to the pill, when it sits below right.</param>
/// <param name="Gap">From the hot spot to the pill, when an edge flips it to the left or above.</param>
public readonly record struct CursorPillLayout(double Scale, int FontPx, int PadX, int PadY, int Radius, int OffsetX, int OffsetY, int Gap)
{
    /// <summary>How far from the hot spot a pill can reach, in effective pixels: the longest readout, Normal's size and
    /// nits ("10240 × 10240   10000 nits  peak 10000  mean 10000", about 330 wide), on either side with room to spare.</summary>
    private const int ReachX = 460, ReachY = 80;

    /// <param name="scale">Physical pixels per effective pixel on the pill's monitor (DPI / 96).</param>
    public static CursorPillLayout For(double scale)
    {
        if (scale <= 0 || double.IsNaN(scale)) scale = 1.0;
        int Px(int effective) => (int)Math.Round(effective * scale);
        return new CursorPillLayout(scale, Px(12), Px(10), Px(4), Px(14), Px(16), Px(24), Px(8));
    }

    /// <summary>The pill for text measured at <paramref name="textWidth"/> x <paramref name="textHeight"/>, beside a
    /// cursor at (<paramref name="cx"/>, <paramref name="cy"/>), kept inside an area of the given size. All coordinates
    /// are relative to that area.</summary>
    public IntRect Place(int cx, int cy, int textWidth, int textHeight, int areaWidth, int areaHeight)
    {
        int w = textWidth + 2 * PadX, h = textHeight + 2 * PadY;
        int x = cx + OffsetX, y = cy + OffsetY;
        if (x + w > areaWidth) x = cx - w - Gap;
        if (y + h > areaHeight) y = cy - h - Gap;
        return new IntRect(Math.Max(0, x), Math.Max(0, y), w, h);
    }

    /// <summary>Everything a pill beside a cursor at (<paramref name="cx"/>, <paramref name="cy"/>) can cover, so a
    /// repaint of this area erases the previous pill wherever it flipped to.</summary>
    public IntRect Reach(int cx, int cy)
    {
        double s = Scale > 0 ? Scale : 1.0;   // default(CursorPillLayout) has none
        int rx = (int)Math.Ceiling(ReachX * s), ry = (int)Math.Ceiling(ReachY * s);
        return new IntRect(cx - rx, cy - ry, 2 * rx, 2 * ry);
    }
}
