using ToneSnip.Core.Color;
using ToneSnip.Core.Imaging;

namespace ToneSnip.Core.Tonemap;

public static class AutoExposure
{
    private const int MaxSamples = 1_000_000;
    private const float MinFactor = 0.5f, MaxFactor = 4f;

    /// <summary>Exposure that puts the region's 99th-percentile luminance at SDR white, clamped to [0.5, 4] times <paramref name="baseExposure"/>.</summary>
    public static float Compute(FloatImage img, float sdrWhiteNits, float baseExposure)
    {
        int pixels = img.Width * img.Height;
        int step = Math.Max(1, pixels / MaxSamples);
        var lum = new List<float>(Math.Min(pixels, MaxSamples) + 1);
        float[] d = img.Data;
        for (int i = 0; i < pixels; i += step)
            lum.Add(Transfer.Luminance709(d[i * 3], d[i * 3 + 1], d[i * 3 + 2]) * 80f);
        return FromSortedLuminance(lum, sdrWhiteNits, baseExposure);
    }

    public static float Compute(HalfImage img, float sdrWhiteNits, float baseExposure)
    {
        int pixels = img.Width * img.Height;
        int step = Math.Max(1, pixels / MaxSamples);
        var lum = new List<float>(Math.Min(pixels, MaxSamples) + 1);
        ushort[] d = img.Data;
        for (int i = 0; i < pixels; i += step)
            lum.Add(Transfer.Luminance709(Transfer.HalfToFloat(d[i * 4]), Transfer.HalfToFloat(d[i * 4 + 1]), Transfer.HalfToFloat(d[i * 4 + 2])) * 80f);
        return FromSortedLuminance(lum, sdrWhiteNits, baseExposure);
    }

    private static float FromSortedLuminance(List<float> lum, float sdrWhiteNits, float baseExposure)
    {
        lum.Sort();
        float p99 = lum[Math.Min(lum.Count - 1, (int)(lum.Count * 0.99f))];
        if (!(p99 > 0f)) return baseExposure;
        return baseExposure * Math.Clamp(sdrWhiteNits / p99, MinFactor, MaxFactor);
    }
}
