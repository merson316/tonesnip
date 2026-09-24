using ToneSnip.Core.Capture;
using ToneSnip.Core.Hdr;
using ToneSnip.Core.Imaging;

namespace ToneSnip.App.Capture;

/// <summary>One output as frozen for a snip: its SDR image for display and output and, for an HDR output, its HDR
/// frame (in memory or on the graphics card), which <see cref="FrameGrabber.Release"/> frees once the snip is
/// built.</summary>
public sealed record CapturedOutput(OutputInfo Info, IHdrFrame? Hdr, BgraImage Sdr)
{
    /// <summary>The HDR frame while it can still be read. Null for an SDR output, and for an HDR one whose frame was
    /// lost with its graphics device: the snip then treats the monitor as SDR and keeps the image it has.</summary>
    public IHdrFrame? ReadableHdr => Hdr is { Readable: true } h ? h : null;
}
