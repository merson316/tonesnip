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
    /// The map for <paramref name="hdr"/> over <paramref name="sdr"/>, whole: <see cref="Measure"/> and then every row
    /// of <see cref="GainMapper.Fill"/>. The encoder takes the rows a band at a time instead, so the map is never held
    /// whole.
    /// </summary>
    public static GainMapResult Compute(HalfImage hdr, BgraImage sdr, float referenceWhiteNits)
    {
        GainMapper m = Measure(hdr, sdr, referenceWhiteNits);
        var gray = new byte[hdr.Width * hdr.Height];
        m.Fill(0, hdr.Height, gray);
        return new GainMapResult(gray, hdr.Width, hdr.Height, m.Min, m.Max);
    }

    /// <summary>
    /// The map's range, from a first pass over every pixel; the returned mapper quantises rows into it on demand,
    /// recomputing each gain rather than keeping a float per pixel (4 bytes a pixel, 33 MB at 4K). The gains are the same
    /// either way, so the map is too.
    /// <para><paramref name="sdr"/> is read flattened on white (<see cref="Flatten.OverWhite(byte, byte, byte, int)"/>),
    /// as the JPEG base it is paired with is, so a freeform snip's transparent SDR needs no flattened copy. An opaque
    /// pixel reads as it is.</para>
    /// </summary>
    public static GainMapper Measure(HalfImage hdr, BgraImage sdr, float referenceWhiteNits)
    {
        if (hdr.Width != sdr.Width || hdr.Height != sdr.Height) throw new ArgumentException("hdr and sdr must match");
        var m = new GainMapper(hdr, sdr, referenceWhiteNits);
        float min = 0f, max = 0f;
        for (int i = 0, n = hdr.Width * hdr.Height; i < n; i++)
        {
            float g = m.Gain(i);
            if (g < min) min = g; if (g > max) max = g;
        }
        if (max - min < 1e-3f) max = min + 1e-3f;   // decoders divide by the range
        m.Min = min; m.Max = max;
        return m;
    }
}

/// <summary>A measured gain map (<see cref="GainMap.Measure"/>): its range, and its 8-bit rows on demand.</summary>
public sealed class GainMapper
{
    private readonly HalfImage _hdr;
    private readonly BgraImage _sdr;
    private readonly float _scale, _ceiling;

    internal GainMapper(HalfImage hdr, BgraImage sdr, float referenceWhiteNits)
    {
        _hdr = hdr; _sdr = sdr;
        _scale = HdrCanvas.ReferenceScale(referenceWhiteNits);
        _ceiling = GainMap.MaxNits / Math.Max(referenceWhiteNits, 1f);
    }

    public int Width => _hdr.Width;
    public int Height => _hdr.Height;
    public float Min { get; internal set; }
    public float Max { get; internal set; }

    /// <summary>Rows [<paramref name="top"/>, <paramref name="top"/> + <paramref name="count"/>) of the map, one byte a
    /// pixel, into <paramref name="gray"/>.</summary>
    public void Fill(int top, int count, Span<byte> gray)
    {
        if (top < 0 || count < 0 || top + count > Height) throw new ArgumentOutOfRangeException(nameof(count), $"rows {top}..{top + count} are outside {Height}");
        if (gray.Length < count * Width) throw new ArgumentException("the band is too small", nameof(gray));
        float min = Min, range = Max - Min;
        for (int i = top * Width, end = (top + count) * Width, o = 0; i < end; i++, o++)
            gray[o] = (byte)Math.Round(Math.Clamp((Gain(i) - min) / range, 0f, 1f) * 255f);
    }

    /// <summary>log2 of pixel <paramref name="i"/>'s HDR over SDR luminance, 1.0 being SDR white on both sides.</summary>
    internal float Gain(int i)
    {
        ushort[] hdr = _hdr.Data;
        byte[] sdr = _sdr.Data;
        float hr = Transfer.HalfToFloat(hdr[i * 4]), hg = Transfer.HalfToFloat(hdr[i * 4 + 1]), hb = Transfer.HalfToFloat(hdr[i * 4 + 2]);
        float yh = Transfer.Luminance709(hr, hg, hb) / _scale;                                  // 1.0 = SDR white
        yh = float.IsNaN(yh) ? 0f : Math.Min(yh, _ceiling);
        (byte b, byte g, byte r) = Flatten.OverWhite(sdr[i * 4], sdr[i * 4 + 1], sdr[i * 4 + 2], sdr[i * 4 + 3]);
        ColorMath.LiftSrgb(b, g, r, 1f, out float sr, out float sg, out float sb);
        float ys = Transfer.Luminance709(sr, sg, sb);
        return MathF.Log2((Math.Max(yh, 0f) + GainMap.Offset) / (Math.Max(ys, 0f) + GainMap.Offset));
    }
}
