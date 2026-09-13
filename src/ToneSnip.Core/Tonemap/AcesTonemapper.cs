namespace ToneSnip.Core.Tonemap;

/// <summary>Narkowicz ACES fit per channel.</summary>
public sealed class AcesTonemapper : ITonemapper
{
    private readonly float _scale;

    public AcesTonemapper(TonemapParams p)
    {
        _scale = 80f / MathF.Max(p.SdrWhiteNits, 1f) * MathF.Max(p.Exposure, 0f);
    }

    private static float Curve(float x)
        => Math.Clamp(x * (2.51f * x + 0.03f) / (x * (2.43f * x + 0.59f) + 0.14f), 0f, 1f);

    public void Map(ref float r, ref float g, ref float b)
    {
        r = Curve(MathF.Max(r * _scale, 0f));
        g = Curve(MathF.Max(g * _scale, 0f));
        b = Curve(MathF.Max(b * _scale, 0f));
    }
}
