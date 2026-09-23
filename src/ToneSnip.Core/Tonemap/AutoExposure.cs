using ToneSnip.Core.Color;
using ToneSnip.Core.Geometry;
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
        int pixels = rect.Width * rect.Height;
        int step = Math.Max(1, pixels / MaxSamples);
        int count = (pixels + step - 1) / step;
        int k = Math.Min(count - 1, (int)(count * 0.99f));
        // Not-positive samples (zero, negative, NaN) all sort below every positive one, so they only shift the index.
        int low = 0;
        for (int i = 0; i < pixels; i += step) if (!(Sample(img, rect, i) > 0f)) low++;
        if (k < low) return 0f;
        k -= low;
        var counts = new int[1 << 11];
        uint prefix = 0, prefixMask = 0;
        foreach ((int shift, int width) in Passes)
        {
            uint mask = (1u << width) - 1;
            Array.Clear(counts);
            for (int i = 0; i < pixels; i += step)
            {
                float v = Sample(img, rect, i);
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
        ushort[] d = img.Data;
        int i = ((rect.Top + s / rect.Width) * img.Width + rect.Left + s % rect.Width) * 4;
        return Transfer.Luminance709(Transfer.HalfToFloat(d[i]), Transfer.HalfToFloat(d[i + 1]), Transfer.HalfToFloat(d[i + 2])) * 80f;
    }
}
