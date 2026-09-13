using ToneSnip.Core.Geometry;

namespace ToneSnip.Core.Capture;

/// <summary>One monitor as a snip sees it. Rotation is quarter turns clockwise, informational only: frames arrive in
/// desktop orientation. FriendlyName (EDID) is shown to the user; DeviceName is the GDI name used as the key and in logs.</summary>
public sealed record OutputInfo(int Index, string DeviceName, int Left, int Top, int Width, int Height, int Rotation, bool Hdr, float SdrWhiteNits, float PeakNits, string? FriendlyName = null)
{
    public IntRect Bounds => new(Left, Top, Width, Height);
}
