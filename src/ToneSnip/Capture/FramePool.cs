using ToneSnip.Core.Imaging;

namespace ToneSnip.App.Capture;

/// <summary>
/// One set of full-monitor frame buffers per output, reused by every snip. Frame-sized arrays land on the Large Object
/// Heap, which is rarely collected, so reusing them avoids a large garbage build-up.
/// <para><b>Ownership.</b> Every buffer belongs to the grabber and is overwritten in place by the next grab, so nothing
/// that outlives the overlay may hold one. <see cref="CaptureResult.Build"/> copies (composite, and the crops when
/// something will read them), the settings preview keeps a downsample, and the overlay releases its pins before the result is built. A
/// new consumer that wants to keep a <see cref="CapturedOutput"/> must copy it first.</para>
/// <para><b>Size.</b> <see cref="EndGrab"/> drops buffers the grab did not use, leaving about 4 bytes a pixel per SDR
/// output and 12 per HDR output (half frame plus tonemapped copy), plus 4 for an annotated overlay back buffer.
/// <see cref="FrameGrabber.ReleaseBuffers"/> frees it all when idle.</para>
/// <para><b>Threading.</b> All access is under one lock: <see cref="Bytes"/> is read from a background thread while
/// the next grab may be starting.</para>
/// </summary>
internal sealed class FramePool
{
    /// <summary>The grab's destination, in desktop orientation.</summary>
    internal const string Frame = "frame";
    /// <summary>The overlay window's annotated copy of the frame. Requested after the grab, so <see cref="EndGrab"/>
    /// keeps it whenever its output still exists.</summary>
    internal const string Back = "back";

    private readonly object _gate = new();
    private readonly Dictionary<(int Output, string Role), HalfImage> _half = new();
    private readonly Dictionary<(int Output, string Role), BgraImage> _bgra = new();
    /// <summary>Keys handed out since <see cref="BeginGrab"/>: what <see cref="EndGrab"/> keeps.</summary>
    private readonly HashSet<(int Output, string Role)> _touched = new();

    public HalfImage Half(int output, string role, int width, int height)
    {
        lock (_gate)
        {
            _touched.Add((output, role));
            if (_half.TryGetValue((output, role), out HalfImage? img) && img.Width == width && img.Height == height) return img;
            img = new HalfImage(width, height);
            _half[(output, role)] = img;   // a resolution change replaces the buffer rather than adding one
            return img;
        }
    }

    public BgraImage Bgra(int output, string role, int width, int height)
    {
        lock (_gate)
        {
            _touched.Add((output, role));
            if (_bgra.TryGetValue((output, role), out BgraImage? img) && img.Width == width && img.Height == height) return img;
            img = BgraImage.Blank(width, height);
            _bgra[(output, role)] = img;
            return img;
        }
    }

    /// <summary>
    /// Lets go of one half buffer this grab requested but will not use: an output that fell back to GDI after its
    /// WGC copy had begun. Without this, <see cref="EndGrab"/> would count it as used and keep 8 bytes a pixel for
    /// an output whose snip is SDR. A copy still writing to it keeps it alive only until the copy ends.
    /// </summary>
    public void DropHalf(int output, string role)
    {
        lock (_gate) { _half.Remove((output, role)); _touched.Remove((output, role)); }
    }

    /// <summary>
    /// Lets go of one BGRA buffer a copy that is still running was given, so the next grab allocates its own rather than
    /// sharing it with that copy. The copy keeps the old one alive only until it ends.
    /// </summary>
    public void DropBgra(int output, string role)
    {
        lock (_gate) { _bgra.Remove((output, role)); _touched.Remove((output, role)); }
    }

    /// <summary>Starts the record of what a grab asked for.</summary>
    public void BeginGrab() { lock (_gate) _touched.Clear(); }

    /// <summary>
    /// Drops buffers for outputs not in this grab (indices are renumbered on enumeration), and grab-time buffers this
    /// grab did not touch (such as the half frame of an output that fell back to GDI). <see cref="Back"/> is exempt
    /// from the second rule because the overlay requests it later.
    /// </summary>
    public void EndGrab(IReadOnlyCollection<int> outputs)
    {
        lock (_gate) { Drop(_half, outputs); Drop(_bgra, outputs); }
    }

    private void Drop<T>(Dictionary<(int Output, string Role), T> map, IReadOnlyCollection<int> outputs)
    {
        List<(int Output, string Role)>? stale = null;
        foreach ((int Output, string Role) key in map.Keys)
            if (!outputs.Contains(key.Output) || (key.Role != Back && !_touched.Contains(key)))
                (stale ??= new()).Add(key);
        if (stale == null) return;
        foreach ((int Output, string Role) key in stale) map.Remove(key);
    }

    /// <summary>Releases every buffer; the next grab allocates a fresh set.</summary>
    public void Clear()
    {
        lock (_gate) { _half.Clear(); _bgra.Clear(); _touched.Clear(); }
    }

    /// <summary>Bytes currently held, for logging.</summary>
    public long Bytes
    {
        get { lock (_gate) return _half.Values.Sum(i => (long)i.Data.Length * 2) + _bgra.Values.Sum(i => (long)i.Data.Length); }
    }
}
