using ToneSnip.Core.Color;
using ToneSnip.Core.Imaging;

namespace ToneSnip.Core.Hdr;

public sealed record GainMapResult(byte[] Gray, int Width, int Height, float Min, float Max);

/// <summary>UltraHDR gain map: log2 of HDR over SDR luminance, both relative to the snip's reference white, normalised to 8 bits.</summary>
public static class GainMap
{
    public const float Offset = 1f / 64f;

    /// <summary>The brightest HDR luminance the map is built for, in nits (PQ's ceiling). Clamping to it keeps an
    /// out-of-range or infinite pixel from stretching the gain range.</summary>
    public const float MaxNits = 10000f;

    /// <summary>
    /// The map for <paramref name="hdr"/> over <paramref name="sdr"/>. Two passes over the pixels, the first for the
    /// range and the second to quantise, recomputing each gain rather than keeping a float per pixel (4 bytes a pixel,
    /// 33 MB at 4K); the gains are the same either way, so the map is too.
    /// </summary>
    public static GainMapResult Compute(HalfImage hdr, BgraImage sdr, float referenceWhiteNits)
    {
        if (hdr.Width != sdr.Width || hdr.Height != sdr.Height) throw new ArgumentException("hdr and sdr must match");
        int n = hdr.Width * hdr.Height;
        float scale = HdrCanvas.ReferenceScale(referenceWhiteNits);
        float ceiling = MaxNits / Math.Max(referenceWhiteNits, 1f);
        float min = 0f, max = 0f;
        for (int i = 0; i < n; i++)
        {
            float g = Gain(hdr.Data, sdr.Data, i, scale, ceiling);
            if (g < min) min = g; if (g > max) max = g;
        }
        if (max - min < 1e-3f) max = min + 1e-3f;   // decoders divide by the range
        var gray = new byte[n];
        float range = max - min;
        for (int i = 0; i < n; i++) gray[i] = (byte)Math.Round(Math.Clamp((Gain(hdr.Data, sdr.Data, i, scale, ceiling) - min) / range, 0f, 1f) * 255f);
        return new GainMapResult(gray, hdr.Width, hdr.Height, min, max);
    }

    /// <summary>log2 of pixel <paramref name="i"/>'s HDR over SDR luminance, 1.0 being SDR white on both sides.</summary>
    private static float Gain(ushort[] hdr, byte[] sdr, int i, float scale, float ceiling)
    {
        float hr = Transfer.HalfToFloat(hdr[i * 4]), hg = Transfer.HalfToFloat(hdr[i * 4 + 1]), hb = Transfer.HalfToFloat(hdr[i * 4 + 2]);
        float yh = Transfer.Luminance709(hr, hg, hb) / scale;                                   // 1.0 = SDR white
        yh = float.IsNaN(yh) ? 0f : Math.Min(yh, ceiling);
        ColorMath.LiftSrgb(sdr[i * 4], sdr[i * 4 + 1], sdr[i * 4 + 2], 1f, out float sr, out float sg, out float sb);
        float ys = Transfer.Luminance709(sr, sg, sb);
        return MathF.Log2((Math.Max(yh, 0f) + Offset) / (Math.Max(ys, 0f) + Offset));
    }
}
