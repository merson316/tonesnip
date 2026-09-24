using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;

namespace ToneSnip.App.Capture;

/// <summary>
/// An active-window snip's frame, captured from the window itself (<see cref="FrameGrabber.GrabWindow"/>).
/// <see cref="Output"/> is the frame as if it were a monitor at the window's place on the desktop, with the values of the
/// monitor it borrowed, so <see cref="CaptureResult.Build"/> composes it like any grab; <see cref="Region"/> is the part
/// of it that is the window, and <see cref="Corners"/> the transparency to put on the built image.
/// </summary>
/// <param name="Blank">The frame had nothing in it; <see cref="FrameGrabber.GrabWindow"/> refuses it.</param>
public sealed record WindowGrab(CapturedOutput Output, IntRect Region, WindowCorners? Corners, bool Blank)
{
    public List<CapturedOutput> Outputs => [Output];

    /// <summary>Frees the HDR frame, as <see cref="FrameGrabber.Release"/> does for a grab of every monitor.</summary>
    public void Release() => Output.Hdr?.Dispose();
}
