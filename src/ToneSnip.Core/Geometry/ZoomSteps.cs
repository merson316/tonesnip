namespace ToneSnip.Core.Geometry;

/// <summary>
/// The editor's zoom arithmetic, kept out of the window so it is testable: a continuous zoom (1 is one screen pixel per
/// picture pixel) that the wheel multiplies by a fixed factor per notch, and a ladder of round stops that the zoom
/// buttons and the + and − keys step along.
/// </summary>
public static class ZoomSteps
{
    /// <summary>The furthest out the view goes: this, or the fit when the fit is smaller still.</summary>
    public const double MinZoom = 0.1;
    /// <summary>The furthest in: far enough to read single pixels on a 4K monitor.</summary>
    public const double MaxZoom = 32;
    /// <summary>What one wheel notch (a delta of 120) multiplies the zoom by. A high-resolution wheel or a pinch sends
    /// a fraction of a notch and gets the same fraction of the step, so it zooms smoothly.</summary>
    public const double NotchFactor = 1.15;
    /// <summary>At or above this many screen pixels per picture pixel the picture is drawn nearest-neighbour, so its
    /// pixels stay crisp squares for inspection; below it linear filtering reads better.</summary>
    public const double CrispFrom = 3;

    /// <summary>The stops the buttons and keys step between, from 10 % to 3200 %.</summary>
    public static readonly double[] Stops =
    {
        0.1, 0.125, 1.0 / 6, 0.25, 1.0 / 3, 0.5, 2.0 / 3, 0.75, 1, 1.25, 1.5, 2, 3, 4, 5, 6, 8, 10, 12, 16, 20, 24, 32,
    };

    /// <summary>The lowest zoom allowed with the picture fitting the window at <paramref name="fit"/>.</summary>
    public static double Min(double fit) => fit > 0 && fit < MinZoom ? fit : MinZoom;

    /// <summary><paramref name="zoom"/> kept inside the range for a picture that fits at <paramref name="fit"/>.</summary>
    public static double Clamp(double zoom, double fit) => Math.Clamp(zoom, Min(fit), MaxZoom);

    /// <summary>The zoom after a wheel delta of <paramref name="delta"/> (120 per notch, positive zooms in).</summary>
    public static double Wheel(double zoom, int delta, double fit) => Clamp(zoom * Math.Pow(NotchFactor, delta / 120.0), fit);

    /// <summary>
    /// The next stop from <paramref name="zoom"/> in direction <paramref name="dir"/> (positive in). The fit counts as
    /// a stop, so stepping passes through it; a zoom already at the end of the range stays there.
    /// </summary>
    public static double Step(double zoom, int dir, double fit)
    {
        const double eps = 1e-6;
        double next = dir > 0 ? MaxZoom : Min(fit);
        foreach (double s in Stops)
        {
            if (dir > 0 && s > zoom * (1 + eps) && s < next) next = s;
            if (dir < 0 && s < zoom * (1 - eps) && s > next) next = s;
        }
        if (fit > 0 && (dir > 0 ? fit > zoom * (1 + eps) && fit < next : fit < zoom * (1 - eps) && fit > next)) next = fit;
        return Clamp(next, fit);
    }

    /// <summary>
    /// Whether to draw with nearest-neighbour sampling at <paramref name="physical"/> screen pixels per picture pixel:
    /// at an exact multiple, where every pixel covers whole screen pixels, and from <see cref="CrispFrom"/> up.
    /// </summary>
    public static bool Crisp(double physical)
        => physical >= CrispFrom || (physical >= 1 && Math.Abs(physical - Math.Round(physical)) < 0.01);

    /// <summary>Ease-out cubic over <paramref name="t"/> in [0, 1], for the zoom animation.</summary>
    public static double EaseOut(double t)
    {
        t = Math.Clamp(t, 0, 1);
        double u = 1 - t;
        return 1 - u * u * u;
    }

    /// <summary>The zoom <paramref name="t"/> of the way from <paramref name="from"/> to <paramref name="to"/>, eased and
    /// interpolated in log space, so zooming in and out by the same factor take the same path in reverse.</summary>
    public static double Between(double from, double to, double t)
        => from <= 0 || to <= 0 ? to : Math.Exp(Math.Log(from) + (Math.Log(to) - Math.Log(from)) * EaseOut(t));
}
