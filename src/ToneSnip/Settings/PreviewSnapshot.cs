using ToneSnip.Core.Capture;
using ToneSnip.Core.Imaging;
using ToneSnip.Windows.Imaging;

namespace ToneSnip.App.Settings;

/// <summary>
/// The reduced copy of the last frozen HDR frame that the Settings tonemap preview renders, about 7 MB of half floats
/// for a 4K monitor. The app keeps it for its whole life, so while no one is looking at it the pixels are swapped for a
/// lossy JPEG XR of them (<see cref="Pack"/>), and decoded again when Settings next asks (<see cref="Image"/>).
/// <para>Thread-safe: made on the grab's thread, packed on a pool thread, read by the preview's pool thread.</para>
/// </summary>
public sealed class PreviewSnapshot(HalfImage image, OutputInfo output)
{
    /// <summary>JPEG XR quality for the packed copy: the preview is a screen-sized picture of a curve, not an archive.</summary>
    private const float PackQuality = 0.9f;

    private readonly object _gate = new();
    private HalfImage? _image = image;
    /// <summary>Encoded once from the original pixels and kept, so packing again after a decode costs nothing and
    /// does not compound the loss.</summary>
    private byte[]? _packed;

    public OutputInfo Output { get; } = output;
    public int Width { get; } = image.Width;
    public int Height { get; } = image.Height;

    /// <summary>The pixels, decoded again if they were packed. Off the UI thread: the decode takes tens of
    /// milliseconds.</summary>
    public HalfImage Image()
    {
        lock (_gate) return _image ??= JxrDecoder.DecodeHalf(_packed!);
    }

    /// <summary>Drops the pixels, keeping only their JPEG XR, until the next <see cref="Image"/>. Returns the bytes
    /// the packed copy holds.</summary>
    public long Pack()
    {
        lock (_gate)
        {
            if (_image != null)
            {
                _packed ??= JxrEncoder.Encode(_image, lossless: false, quality: PackQuality);
                _image = null;
            }
            return _packed?.Length ?? 0;
        }
    }
}
