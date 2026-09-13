using ToneSnip.Core.Color;

namespace ToneSnip.Core.Tonemap;

/// <summary>
/// "Match the desktop" tonemapper. Normalises scRGB so the monitor's SDR white level maps to 1.0.
/// With Knee = 1 everything at or below SDR white is untouched and brighter pixels are clipped on
/// luminance with hue preserved. With Knee &lt; 1 a BT.2390 Hermite roll-off (in the PQ domain) starts
/// at Knee * SDR white and reaches 1.0 at the monitor peak. The knee is capped at the largest value
/// for which the BT.2390 spline stays monotonic (KS = 1.5 * maxLum - 0.5).
/// </summary>
public sealed class DesktopTonemapper : ITonemapper
{
    private readonly float _scale;     // scRGB -> SDR-white units, including exposure
    private readonly float _sdrWhite;  // nits
    private readonly bool _clip;
    private readonly float _pqPeak;    // PQ(peak nits)
    private readonly float _ks;        // knee start, normalised PQ
    private readonly float _maxLum;    // PQ(sdr white) / PQ(peak)

    public DesktopTonemapper(TonemapParams p)
    {
        _sdrWhite = MathF.Max(p.SdrWhiteNits, 1f);
        _scale = 80f / _sdrWhite * MathF.Max(p.Exposure, 0f);
        float peak = MathF.Max(p.PeakNits, _sdrWhite * 1.01f);
        _pqPeak = Transfer.PqEncode(peak);
        _maxLum = Transfer.PqEncode(_sdrWhite) / _pqPeak;
        float ksLimit = 1.5f * _maxLum - 0.5f;
        float knee = Math.Clamp(p.Knee, 0.01f, 1f);
        float ksRequested = Transfer.PqEncode(knee * _sdrWhite) / _pqPeak;
        _clip = knee >= 1f || ksLimit <= 0f;
        _ks = MathF.Min(ksRequested, ksLimit);
    }

    public void Map(ref float r, ref float g, ref float b)
    {
        // A non-finite channel would make the luminance scale produce NaN, which encodes as black. NaN becomes 0 and
        // infinity the largest finite half.
        r = Finite(r); g = Finite(g); b = Finite(b);
        r = MathF.Max(r * _scale, 0f);
        g = MathF.Max(g * _scale, 0f);
        b = MathF.Max(b * _scale, 0f);
        float y = Transfer.Luminance709(r, g, b);
        if (y > 0f)
        {
            float yOut = _clip ? MathF.Min(y, 1f) : Compress(y);
            if (yOut < y)
            {
                float s = yOut / y;
                r *= s; g *= s; b *= s;
            }
        }
        r = MathF.Min(r, 1f);
        g = MathF.Min(g, 1f);
        b = MathF.Min(b, 1f);
    }

    private static float Finite(float v) => float.IsNaN(v) ? 0f : Math.Clamp(v, -65504f, 65504f);

    /// <summary>BT.2390 EETF on luminance. Input and output are in SDR-white units.</summary>
    private float Compress(float y)
    {
        float e1 = Transfer.PqEncode(y * _sdrWhite) / _pqPeak;
        if (e1 <= _ks) return y;
        float t = MathF.Min((e1 - _ks) / (1f - _ks), 1f);
        float t2 = t * t, t3 = t2 * t;
        float e2 = (2f * t3 - 3f * t2 + 1f) * _ks
                 + (t3 - 2f * t2 + t) * (1f - _ks)
                 + (-2f * t3 + 3f * t2) * _maxLum;
        return MathF.Min(Transfer.PqDecode(e2 * _pqPeak) / _sdrWhite, 1f);
    }
}
