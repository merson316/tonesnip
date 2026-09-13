using ToneSnip.Core.Annotate;
using ToneSnip.Core.Geometry;

namespace ToneSnip.App.Overlay;

public sealed record OverlayOutcome(IntRect Region, IReadOnlyList<(int X, int Y)>? Freeform, int RestartWithDelay = -1, AnnotationDoc? Doc = null, float Exposure = 1f)
{
    public static readonly OverlayOutcome Cancelled = new(IntRect.Empty, null);
    /// <summary>An exposure preview rewrote the frozen SDR frames at some point, so they may not be at
    /// <see cref="Exposure"/>: a pending slider value is dropped when the overlay finishes, and the result is then
    /// tonemapped from the half-float frames rather than taken from the SDR ones.</summary>
    public bool ExposurePreviewed { get; init; }
}
