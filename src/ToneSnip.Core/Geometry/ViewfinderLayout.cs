namespace ToneSnip.Core.Geometry;

/// <summary>
/// The overlay's selection frame: a hairline edge, an accent bracket on each corner, and the size in a chip above the
/// top-left corner. Physical pixels, scaled from effective pixels by the monitor's DPI (see <see cref="CursorPillLayout"/>
/// for why). Brackets hug their corner from outside, and turn inside wherever outside would leave the monitor, so a
/// whole-monitor or window-edge selection still shows all four.
/// </summary>
/// <param name="Scale">Physical pixels per effective pixel (DPI / 96).</param>
/// <param name="Edge">The hairline's width.</param>
/// <param name="Thickness">A bracket arm's thickness.</param>
/// <param name="Length">A bracket arm's full length, before short selections shorten it.</param>
/// <param name="Keyline">The dark line around each arm, so a pale accent still reads over bright content.</param>
/// <param name="ChipGap">From the bracket to the chip.</param>
/// <param name="ChipPadX">Space left and right of the chip's text.</param>
/// <param name="ChipPadY">Space above and below the chip's text.</param>
/// <param name="ChipRadius">The chip's corner radius.</param>
/// <param name="ChipFontPx">The chip's text height, as GDI's negative lfHeight.</param>
public readonly record struct ViewfinderLayout(double Scale, int Edge, int Thickness, int Length, int Keyline,
                                               int ChipGap, int ChipPadX, int ChipPadY, int ChipRadius, int ChipFontPx)
{
    /// <summary>Two arms per corner.</summary>
    public const int MaxArms = 8;

    /// <summary>The largest chip the repaint reach allows for, in effective pixels: "10240 × 10240" is about 85 wide.</summary>
    private const int ChipMaxWidth = 140, ChipMaxHeight = 40;

    public static ViewfinderLayout For(double scale)
    {
        if (scale <= 0 || double.IsNaN(scale)) scale = 1.0;
        int Px(int effective) => Math.Max(1, (int)Math.Round(effective * scale));
        return new ViewfinderLayout(scale, Px(1), Px(3), Px(20), Px(1), Px(8), Px(8), Px(4), Px(4), Px(12));
    }

    /// <summary>
    /// Writes the bracket arms for the corners of <paramref name="sel"/> that lie inside <paramref name="area"/> (the
    /// painting monitor, in the same coordinates) and returns how many were written. A corner on another monitor is
    /// that monitor's to draw, so a selection spanning two never shows a bracket twice.
    /// </summary>
    public int Brackets(IntRect sel, IntRect area, Span<IntRect> arms)
    {
        if (sel.IsEmpty) return 0;
        int t = Thickness, len = Math.Clamp(Math.Min(sel.Width, sel.Height) / 3, t, Math.Max(t, Length));
        int n = 0;
        for (int corner = 0; corner < 4; corner++)
        {
            bool right = (corner & 1) != 0, bottom = (corner & 2) != 0;
            int cornerX = right ? sel.Right - 1 : sel.Left, cornerY = bottom ? sel.Bottom - 1 : sel.Top;
            if (!area.Contains(cornerX, cornerY)) continue;
            // The thickness band on each axis: outside the edge, or inside where outside leaves the area.
            int bandX = right ? (sel.Right + t <= area.Right ? sel.Right : sel.Right - t) : (sel.Left - t >= area.Left ? sel.Left - t : sel.Left);
            int bandY = bottom ? (sel.Bottom + t <= area.Bottom ? sel.Bottom : sel.Bottom - t) : (sel.Top - t >= area.Top ? sel.Top - t : sel.Top);
            // Each arm runs from the band's outer end along its edge, towards the selection's middle.
            int armX = right ? bandX + t - len : bandX, armY = bottom ? bandY + t - len : bandY;
            arms[n++] = new IntRect(armX, bandY, len, t);   // horizontal
            arms[n++] = new IntRect(bandX, armY, t, len);   // vertical
        }
        return n;
    }

    /// <summary>The size chip for text measured at <paramref name="textWidth"/> x <paramref name="textHeight"/>: above
    /// the top-left bracket, or inside that corner when there is no room above, kept inside <paramref name="area"/>.</summary>
    public IntRect Chip(IntRect sel, int textWidth, int textHeight, IntRect area)
    {
        int w = textWidth + 2 * ChipPadX, h = textHeight + 2 * ChipPadY;
        int x = sel.Left - Thickness >= area.Left ? sel.Left - Thickness : sel.Left;
        int y = sel.Top - Thickness - ChipGap - h;
        if (y < area.Top) { x = sel.Left + Thickness + ChipGap; y = sel.Top + Thickness + ChipGap; }
        x = Math.Max(area.Left, Math.Min(x, area.Right - w));
        return new IntRect(x, y, w, h);
    }

    /// <summary>Everything the frame for <paramref name="sel"/> can cover — edge, brackets with their keylines, and the
    /// chip above or inside its corner — so repainting this area erases the previous frame.</summary>
    public IntRect Reach(IntRect sel)
    {
        if (sel.IsEmpty) return IntRect.Empty;
        double s = Scale > 0 ? Scale : 1.0;
        int pad = Thickness + Keyline, maxW = (int)Math.Ceiling(ChipMaxWidth * s), maxH = (int)Math.Ceiling(ChipMaxHeight * s);
        int inset = Thickness + ChipGap;
        // Left: a chip pushed back from the monitor's right edge starts at most its own width left of the corner.
        return IntRect.FromLtrb(
            Math.Min(sel.Left - pad, sel.Left - maxW),
            sel.Top - inset - maxH,
            Math.Max(sel.Right + pad, sel.Left + inset + maxW),
            Math.Max(sel.Bottom + pad, sel.Top + inset + maxH));
    }
}
