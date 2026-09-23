using ToneSnip.Core.Geometry;

namespace ToneSnip.Core.Capture;

/// <summary>One monitor as a snip sees it. FriendlyName (EDID) is shown to the user; DeviceName is the GDI name used as
/// the key and in logs.</summary>
public sealed record OutputInfo(int Index, string DeviceName, int Left, int Top, int Width, int Height, bool Hdr, float SdrWhiteNits, float PeakNits, string? FriendlyName = null)
{
    public IntRect Bounds => new(Left, Top, Width, Height);
}
