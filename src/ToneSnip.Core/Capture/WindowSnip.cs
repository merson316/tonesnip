using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;

namespace ToneSnip.Core.Capture;

/// <summary>
/// The pure decisions behind an active-window snip taken from the window itself (Windows.Graphics.Capture's
/// CreateForWindow) rather than cropped from a grab of every monitor: which monitor's HDR state and white level the
/// window's frame is captured and tonemapped with, and where on the desktop the frame lands.
/// </summary>
public static class WindowSnip
{
    /// <summary>
    /// The monitor whose values a window's frame is captured and tonemapped with. A window capture is one frame in one
    /// pixel format, so a window has to borrow one monitor's HDR state, SDR white level and peak:
    /// <list type="bullet">
    /// <item>the monitor it overlaps most, as <c>MonitorFromWindow</c> picks it, when every monitor it touches is HDR or
    /// every one is SDR;</item>
    /// <item>the primary monitor (the one at the desktop origin) when it spans HDR and SDR monitors and the primary is one
    /// of them. Neither half is "right" for the other then, and the primary's values are the ones Windows itself treats
    /// as the desktop's, so the choice is at least predictable. A window that does not touch the primary takes the
    /// largest overlap: the primary's values would belong to none of its pixels.</item>
    /// </list>
    /// Null when the window touches no monitor at all (entirely off screen).
    /// </summary>
    public static OutputInfo? OutputFor(IntRect window, IReadOnlyList<OutputInfo> outputs)
    {
        OutputInfo? most = null;
        long mostArea = 0;
        bool anyHdr = false, anySdr = false;
        foreach (OutputInfo o in outputs)
        {
            IntRect hit = o.Bounds.Intersect(window);
            if (hit.IsEmpty) continue;
            if (o.Hdr) anyHdr = true; else anySdr = true;
            long area = (long)hit.Width * hit.Height;
            if (area > mostArea) { most = o; mostArea = area; }
        }
        if (most == null) return null;
        if (anyHdr && anySdr) return outputs.FirstOrDefault(o => o.Left == 0 && o.Top == 0 && !o.Bounds.Intersect(window).IsEmpty) ?? most;
        return most;
    }

    /// <summary>
    /// Where a window's captured frame of <paramref name="width"/> x <paramref name="height"/> sits on the desktop, and
    /// which part of it is the window. <paramref name="frame"/> is the window's visible frame (DWM's extended frame
    /// bounds, which is what today's window snips crop to) and <paramref name="window"/> its full window rectangle,
    /// invisible resize borders included; both are read after the frame arrived, so a window that moved or resized
    /// since the capture began is placed where it is now.
    /// <para>The capture normally delivers just the visible frame. Should it deliver the full window rectangle instead
    /// (the resize borders transparent), the frame is placed at that rectangle and cut to the visible part. Any other
    /// size, a window still resizing, is placed at the visible frame's top-left at the frame's own size.</para>
    /// Returns the frame's desktop rectangle and the part of it to keep, in desktop coordinates.
    /// </summary>
    public static (IntRect Frame, IntRect Keep) Place(IntRect frame, IntRect window, int width, int height)
    {
        if (width <= 0 || height <= 0) return (IntRect.Empty, IntRect.Empty);
        if (width == window.Width && height == window.Height && (window.Width != frame.Width || window.Height != frame.Height))
        {
            IntRect at = new(window.Left, window.Top, width, height);
            IntRect keep = at.Intersect(frame);
            return (at, keep.IsEmpty ? at : keep);
        }
        IntRect placed = new(frame.Left, frame.Top, width, height);
        return (placed, placed);
    }

    /// <summary>
    /// A window's frame with nothing in it, sampled every <paramref name="step"/>-th pixel both ways: every sample
    /// transparent black, colour and alpha. That is what a window that cannot be captured (protected content, some
    /// suspended windows) comes back as, and the screen shows such a window as it really is. A black window is not blank:
    /// a console or a paused video is opaque, and a GDI window's client area, whose alpha is zero, still has colour
    /// somewhere unless it is black all over, which the screen shows the same.
    /// </summary>
    public static bool IsBlank(BgraImage img, int step)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(step, 1);
        byte[] d = img.Data;
        for (int y = 0; y < img.Height; y += step)
            for (int x = 0; x < img.Width; x += step)
            {
                int i = (y * img.Width + x) * 4;
                if ((d[i] | d[i + 1] | d[i + 2] | d[i + 3]) != 0) return false;
            }
        return true;
    }

    /// <summary><see cref="IsBlank(BgraImage, int)"/> for an HDR frame.</summary>
    public static bool IsBlank(HalfImage img, int step)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(step, 1);
        for (int y = 0; y < img.Height; y += step)
            for (int x = 0; x < img.Width; x += step)
                if (!IsEmpty(img.Data.AsSpan((y * img.Width + x) * 4, 4))) return false;
        return true;
    }

    /// <summary>One half-float RGBA pixel is transparent black (either zero, positive or negative).</summary>
    public static bool IsEmpty(ReadOnlySpan<ushort> rgba)
        => ((rgba[0] | rgba[1] | rgba[2] | rgba[3]) & 0x7FFF) == 0;
}
