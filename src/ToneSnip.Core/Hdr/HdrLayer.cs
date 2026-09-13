using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;

namespace ToneSnip.Core.Hdr;

/// <summary>A captured half-float area (an HDR monitor's crop).</summary>
/// <param name="Bounds">Virtual-desktop pixels — the same frame as <see cref="HdrCanvas.Build"/>'s region and viewport, not
/// region-local.</param>
/// <param name="Exposure">Multiplied with <see cref="HdrCanvas.Build"/>'s global exposure for this layer only.</param>
public sealed record HdrLayer(IntRect Bounds, HalfImage Image, float Exposure = 1f);
