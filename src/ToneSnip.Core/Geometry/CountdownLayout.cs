namespace ToneSnip.Core.Geometry;

/// <summary>
/// The countdown pill's size and place: a square in the monitor's top-right corner, sized as a share of the monitor's
/// height in effective pixels (so it looks the same at any resolution and scaling), clamped to a minimum and maximum.
/// </summary>
/// <param name="Size">Width and height, in effective pixels.</param>
/// <param name="FontSize">The digit, in effective pixels.</param>
public readonly record struct CountdownLayout(double Size, double FontSize)
{
    /// <summary>The pill's share of the monitor's height.</summary>
    public const double HeightShare = 0.075;
    public const double MinSize = 80;
    public const double MaxSize = 160;
    /// <summary>The gap to the monitor's top and right edges, in effective pixels: 24 of air plus the 12 px margin the
    /// popup keeps for its drop shadow.</summary>
    public const int Inset = 36;

    /// <param name="monitor">The monitor, in physical pixels.</param>
    /// <param name="scale">Physical pixels per effective pixel on that monitor.</param>
    public static CountdownLayout For(IntRect monitor, double scale)
    {
        if (scale <= 0) scale = 1.0;
        double size = Math.Clamp(Math.Round(monitor.Height / scale * HeightShare), MinSize, MaxSize);
        return new CountdownLayout(size, Math.Round(size * 0.56));
    }

    /// <summary>The window's rectangle in physical pixels, for a window measured at
    /// <paramref name="windowWidth"/> x <paramref name="windowHeight"/> physical pixels.</summary>
    public IntRect Place(IntRect monitor, double scale, int windowWidth, int windowHeight)
    {
        int inset = (int)Math.Round(Inset * (scale <= 0 ? 1.0 : scale));
        return new IntRect(monitor.Right - inset - windowWidth, monitor.Top + inset, windowWidth, windowHeight);
    }
}
