namespace ToneSnip.Core.Tonemap;

/// <summary>Display-derived tonemapping parameters.</summary>
public sealed record TonemapParams
{
    /// <summary>Luminance Windows assigns to SDR white on this monitor (DisplayConfig SDR white level).</summary>
    public float SdrWhiteNits { get; init; } = 80f;
    /// <summary>Monitor peak luminance (DXGI MaxLuminance).</summary>
    public float PeakNits { get; init; } = 1000f;
    /// <summary>Linear multiplier applied before tonemapping. 1.0 = as displayed.</summary>
    public float Exposure { get; init; } = 1f;
    /// <summary>Fraction of SDR white where the desktop tonemapper starts its roll-off; 1.0 = clip.</summary>
    public float Knee { get; init; } = 1f;
}
