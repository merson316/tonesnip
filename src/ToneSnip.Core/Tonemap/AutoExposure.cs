using ToneSnip.Core.Color;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Hdr;
using ToneSnip.Core.Imaging;

namespace ToneSnip.Core.Tonemap;

public static class AutoExposure
{
    private const int MaxSamples = 1_000_000;
    private const float MinFactor = 0.5f, MaxFactor = 4f;

    /// <summary>Exposure that puts the region's 99th-percentile luminance at SDR white, clamped to [0.5, 4] times <paramref name="baseExposure"/>.</summary>
    public static float Compute(HalfImage img, float sdrWhiteNits, float baseExposure)
        => Compute(img, new IntRect(0, 0, img.Width, img.Height), sdrWhiteNits, baseExposure);

    /// <summary>As <see cref="Compute(HalfImage, float, float)"/> for the part of <paramref name="img"/> inside
    /// <paramref name="rect"/>, with the same samples a crop of that rectangle would give, so the snip's exposure can be
    /// measured on the frame without copying the crop out.</summary>
    public static float Compute(HalfImage img, IntRect rect, float sdrWhiteNits, float baseExposure)
    {
        float p99 = Percentile99(img, rect);
        if (!(p99 > 0f)) return baseExposure;
        return baseExposure * Math.Clamp(sdrWhiteNits / p99, MinFactor, MaxFactor);
    }

    /// <summary>As <see cref="Compute(HalfImage, IntRect, float, float)"/> for the part of <paramref name="frame"/>
    /// inside <paramref name="rect"/>, wherever its pixels are: only the samples are read back
    /// (<see cref="IHdrFrame.Luminances"/>), not a crop, so the answer is the same without a crop-sized copy.</summary>
    public static float Compute(IHdrFrame frame, IntRect rect, float sdrWhiteNits, float baseExposure)
    {
        if (frame is HalfFrame inMemory) return Compute(inMemory.Image, rect, sdrWhiteNits, baseExposure);
        float p99 = Percentile99(frame.Luminances(rect, SampleStep(rect)));
        if (!(p99 > 0f)) return baseExposure;
        return baseExposure * Math.Clamp(sdrWhiteNits / p99, MinFactor, MaxFactor);
    }

    /// <summary>Every how many pixels of <paramref name="rect"/>, counted in a crop's row-major order, a sample is
    /// taken: at most about <see cref="MaxSamples"/> of them.</summary>
    public static int SampleStep(IntRect rect) => Math.Max(1, rect.Width * rect.Height / MaxSamples);

    /// <summary>
    /// The 99th-percentile luminance in nits of every step-th pixel of <paramref name="rect"/>, counted in the crop's
    /// row-major order: the element a sorted list of those samples holds at index <c>count * 0.99</c>. Zero when that
    /// sample is not positive (a black frame, or NaN), which the caller treats alike.
    /// <para>Found by radix selection on the float's bits rather than by sorting a list of up to a million samples
    /// (4 MB on the Large Object Heap, and a sort, for every call). A positive float's bits order like its value, so
    /// three histogram passes of 11, 10 and 10 bits pin the exact sample down with an 8 KB table; the samples are
    /// recomputed each pass instead of stored.</para>
    /// </summary>
    public static float Percentile99(HalfImage img, IntRect rect)
    {
        if (rect.IsEmpty || rect.Left < 0 || rect.Top < 0 || rect.Right > img.Width || rect.Bottom > img.Height)
            throw new ArgumentOutOfRangeException(nameof(rect), $"{rect} is outside {img.Width}x{img.Height}");
        return Select(new RectSamples(img, rect, SampleStep(rect)));
    }

    /// <summary>The same percentile over samples already taken (<see cref="IHdrFrame.Luminances"/>): the same
    /// selection, so the same answer as over the image they were read from.</summary>
    public static float Percentile99(float[] samples) => samples.Length == 0 ? 0f : Select(new ArraySamples(samples));

    /// <summary>The samples of one percentile, by index. A struct per source, so the selection loop is compiled for
    /// each without a call per sample.</summary>
    private interface ISamples
    {
        int Count { get; }
        float this[int i] { get; }
    }

    private readonly struct RectSamples(HalfImage img, IntRect rect, int step) : ISamples
    {
        public int Count => (rect.Width * rect.Height + step - 1) / step;
        public float this[int i] => Sample(img, rect, i * step);
    }

    private readonly struct ArraySamples(float[] samples) : ISamples
    {
        public int Count => samples.Length;
        public float this[int i] => samples[i];
    }

    private static float Select<T>(T samples) where T : struct, ISamples
    {
        int count = samples.Count;
        int k = Math.Min(count - 1, (int)(count * 0.99f));
        // Not-positive samples (zero, negative, NaN) all sort below every positive one, so they only shift the index.
        int low = 0;
        for (int i = 0; i < count; i++) if (!(samples[i] > 0f)) low++;
        if (k < low) return 0f;
        k -= low;
        var counts = new int[1 << 11];
        uint prefix = 0, prefixMask = 0;
        foreach ((int shift, int width) in Passes)
        {
            uint mask = (1u << width) - 1;
            Array.Clear(counts);
            for (int i = 0; i < count; i++)
            {
                float v = samples[i];
                if (!(v > 0f)) continue;
                uint bits = BitConverter.SingleToUInt32Bits(v);
                if ((bits & prefixMask) == prefix) counts[(bits >> shift) & mask]++;
            }
            int bin = 0;
            while (k >= counts[bin]) { k -= counts[bin]; bin++; }
            prefix |= (uint)bin << shift;
            prefixMask |= mask << shift;
        }
        return BitConverter.UInt32BitsToSingle(prefix);
    }

    /// <summary>The bit fields of a positive float, high to low, one selection pass each.</summary>
    private static readonly (int Shift, int Width)[] Passes = { (20, 11), (10, 10), (0, 10) };

    /// <summary>Luminance in nits of sample <paramref name="s"/>: pixel number s of a crop of the rectangle.</summary>
    private static float Sample(HalfImage img, IntRect rect, int s)
    {
        int i = ((rect.Top + s / rect.Width) * img.Width + rect.Left + s % rect.Width) * 4;
        return Nits(img.Data.AsSpan(i, 3));
    }

    /// <summary>Luminance in nits of one half-float RGB pixel, as every auto exposure sample is measured.</summary>
    public static float Nits(ReadOnlySpan<ushort> rgb)
        => Transfer.Luminance709(Transfer.HalfToFloat(rgb[0]), Transfer.HalfToFloat(rgb[1]), Transfer.HalfToFloat(rgb[2])) * 80f;
}
