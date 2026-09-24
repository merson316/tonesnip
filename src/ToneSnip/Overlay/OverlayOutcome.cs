using ToneSnip.Core.Annotate;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Geometry;

namespace ToneSnip.App.Overlay;

public sealed record OverlayOutcome(IntRect Region, IReadOnlyList<(int X, int Y)>? Freeform, int RestartWithDelay = -1, AnnotationDoc? Doc = null, float Exposure = 1f)
{
    public static readonly OverlayOutcome Cancelled = new(IntRect.Empty, null);
    /// <summary>An exposure preview rewrote the frozen SDR frames at some point, so they may not be at
    /// <see cref="Exposure"/>: a pending slider value is dropped when the overlay finishes, and the result is then
    /// tonemapped from the half-float frames rather than taken from the SDR ones.</summary>
    public bool ExposurePreviewed { get; init; }
    /// <summary>What the selection is for: armed from the toolbar or with T or P before (or after) selecting.</summary>
    public SnipAction Action { get; init; }
}
