namespace ToneSnip.Core.Tonemap;

/// <summary>Uncharted 2 filmic curve per channel; the white point is the monitor peak in SDR-white units.</summary>
public sealed class HableTonemapper : ITonemapper
{
    private readonly float _scale;
    private readonly float _invWhite;

    public HableTonemapper(TonemapParams p)
    {
        float sdr = MathF.Max(p.SdrWhiteNits, 1f);
        _scale = 80f / sdr * MathF.Max(p.Exposure, 0f);
        float white = MathF.Max(p.PeakNits / sdr, 1.01f);
        _invWhite = 1f / Curve(white);
    }

    private static readonly float RawAtZero = Raw(0f);

    private static float Raw(float x)
    {
        const float A = 0.15f, B = 0.50f, C = 0.10f, D = 0.20f, E = 0.02f, F = 0.30f;
        return (x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F);
    }

    /// <summary>Raw(x) - Raw(0), so black maps to exactly zero (the analytic offset E/F would leave float noise).</summary>
    private static float Curve(float x) => Raw(x) - RawAtZero;

    public void Map(ref float r, ref float g, ref float b)
    {
        r = Math.Clamp(Curve(MathF.Max(r * _scale, 0f)) * _invWhite, 0f, 1f);
        g = Math.Clamp(Curve(MathF.Max(g * _scale, 0f)) * _invWhite, 0f, 1f);
        b = Math.Clamp(Curve(MathF.Max(b * _scale, 0f)) * _invWhite, 0f, 1f);
    }
}
