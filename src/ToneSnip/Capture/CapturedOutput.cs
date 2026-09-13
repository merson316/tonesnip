using ToneSnip.Core.Capture;
using ToneSnip.Core.Imaging;

namespace ToneSnip.App.Capture;

/// <summary>One output as frozen for a snip: its SDR image for display/output and, for HDR outputs, the half-float frame.</summary>
public sealed record CapturedOutput(OutputInfo Info, HalfImage? Half, BgraImage Sdr);
