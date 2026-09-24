using ToneSnip.Core.Color;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using ToneSnip.Core.Tonemap;

namespace ToneSnip.Core.Hdr;

/// <summary>
/// An <see cref="IHdrFrame"/> whose pixels are in memory: the CPU path, used when the GPU one is off or unavailable,
/// and by the tests and harnesses. It does not own <see cref="Image"/>, which is usually a pooled buffer, so
/// <see cref="Dispose"/> only stops further reads.
/// </summary>
public sealed class HalfFrame(HalfImage image) : IHdrFrame
{
    /// <summary>At most this many pixels are read for <see cref="TryStats"/>: it runs on the UI thread as the selection
    /// changes, so a large rectangle is sampled on a grid rather than read in full.</summary>
    public const int StatsSamples = 20_000;

    private bool _disposed;

    public HalfImage Image { get; } = image;
    public int Width => Image.Width;
    public int Height => Image.Height;
    public bool Readable => !_disposed;

    public bool TrySample(int x, int y, out float r, out float g, out float b)
    {
        if (_disposed) { r = g = b = 0; return false; }
        (r, g, b) = Image.Sample(x, y);
        return true;
    }

    /// <summary>Sampled, not exact: every step-th pixel of every step-th row, with the step chosen so at most
    /// <see cref="StatsSamples"/> pixels are read.</summary>
    public bool TryStats(IntRect rect, out float peak, out float mean)
    {
        peak = mean = 0;
        if (_disposed) return false;
        rect = rect.Intersect(new IntRect(0, 0, Width, Height));
        if (rect.IsEmpty) return true;
        float sum = 0; int n = 0, step = Math.Max(1, (int)Math.Sqrt(rect.Width * (long)rect.Height / (double)StatsSamples));
        for (int y = rect.Top; y < rect.Bottom; y += step)
            for (int x = rect.Left; x < rect.Right; x += step)
            {
                (float r, float g, float b) = Image.Sample(x, y);
                float v = Transfer.Luminance709(r, g, b) * 80f;
                peak = Math.Max(peak, v); sum += v; n++;
            }
        mean = n > 0 ? sum / n : 0;
        return true;
    }

    public void Tonemap(TonemapCurve curve, IntRect source, BgraImage target, int x, int y)
    {
        Check();
        Tonemap(Image, curve, source, target, x, y);
    }

    /// <summary>The CPU tonemap of any half-float image: the ACES curve through its cached table, the others per
    /// pixel.</summary>
    public static void Tonemap(HalfImage img, TonemapCurve curve, IntRect source, BgraImage target, int x, int y)
    {
        (ITonemapper? tm, AcesLut? lut) = curve.Name == "aces" ? (null, AcesLut.For(curve.Params)) : (TonemapperFactory.Create(curve.Name, curve.Params), (AcesLut?)null);
        PixelConvert.ToBgra8Into(img, source, tm, lut, target, x, y);
    }

    public HalfImage Crop(IntRect rect) { Check(); return Image.Crop(rect); }

    public HalfImage Downsample(int step) { Check(); return Image.Downsample(step); }

    public float[] Luminances(IntRect rect, int step)
    {
        Check();
        if (rect.IsEmpty || rect.Left < 0 || rect.Top < 0 || rect.Right > Width || rect.Bottom > Height)
            throw new ArgumentOutOfRangeException(nameof(rect), $"{rect} is outside {Width}x{Height}");
        ArgumentOutOfRangeException.ThrowIfLessThan(step, 1);
        var nits = new float[(rect.Width * rect.Height + step - 1) / step];
        for (int n = 0; n < nits.Length; n++)
        {
            int s = n * step, i = ((rect.Top + s / rect.Width) * Width + rect.Left + s % rect.Width) * 4;
            nits[n] = AutoExposure.Nits(Image.Data.AsSpan(i, 3));
        }
        return nits;
    }

    public ZebraMask Zebra(float sdrWhiteScRgb, float exposure) { Check(); return ZebraMask.Of(Image, sdrWhiteScRgb, exposure); }

    private void Check()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(HalfFrame));
    }

    public void Dispose() => _disposed = true;
}
