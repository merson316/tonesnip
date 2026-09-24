using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using ToneSnip.Core.Tonemap;

namespace ToneSnip.Core.Hdr;

/// <summary>A tonemap curve by name (<see cref="TonemapperFactory.Names"/>) with the parameters to run it at.</summary>
public readonly record struct TonemapCurve(string Name, TonemapParams Params);

/// <summary>
/// One monitor's captured HDR frame (linear scRGB, 1.0 = 80 nits) as the overlay and the snip read it. The pixels may
/// live in memory (<see cref="HalfFrame"/>) or stay on the graphics card, in which case every call reads back only what
/// it returns: one pixel, two numbers, a crop, or a tonemapped image.
/// <para>A frame is owned by the grab that made it and disposed once the snip is built. Nothing may read it
/// afterwards.</para>
/// </summary>
public interface IHdrFrame : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>False once the pixels can no longer be read: the graphics device holding them was lost, or the frame
    /// was disposed. The snip then keeps the SDR image it already has and treats the monitor as SDR.</summary>
    bool Readable { get; }

    /// <summary>Linear scRGB of one pixel. False when it cannot be read right now without making the caller wait (the
    /// device is busy with a capture), or at all (<see cref="Readable"/>). Cheap enough for every mouse move.</summary>
    bool TrySample(int x, int y, out float r, out float g, out float b);

    /// <summary>Peak and mean luminance in nits over <paramref name="rect"/> (frame pixels). False as for
    /// <see cref="TrySample"/>.</summary>
    bool TryStats(IntRect rect, out float peak, out float mean);

    /// <summary>Tonemaps the part of the frame inside <paramref name="source"/> into <paramref name="target"/> with its
    /// top-left at (<paramref name="x"/>, <paramref name="y"/>), leaving the rest of the target as it was.</summary>
    /// <exception cref="HdrFrameLostException">The pixels are gone.</exception>
    void Tonemap(TonemapCurve curve, IntRect source, BgraImage target, int x, int y);

    /// <summary>A new half-float image of the part of the frame inside <paramref name="rect"/>, owned by the
    /// caller.</summary>
    /// <exception cref="HdrFrameLostException">The pixels are gone.</exception>
    HalfImage Crop(IntRect rect);

    /// <summary>Every <paramref name="step"/>-th pixel in both directions, as <see cref="HalfImage.Downsample"/>.</summary>
    /// <exception cref="HdrFrameLostException">The pixels are gone.</exception>
    HalfImage Downsample(int step);

    /// <summary>The luminance in nits (<see cref="AutoExposure.Nits"/>) of every <paramref name="step"/>-th pixel of the
    /// part of the frame inside <paramref name="rect"/>, counted in a crop's row-major order: what auto exposure measures,
    /// without a copy of the crop.</summary>
    /// <exception cref="HdrFrameLostException">The pixels are gone.</exception>
    float[] Luminances(IntRect rect, int step);

    /// <summary>The pixels brighter than SDR white after exposure, as the zebra pass marks them: luminance ×
    /// <paramref name="exposure"/> above <paramref name="sdrWhiteScRgb"/>.</summary>
    /// <exception cref="HdrFrameLostException">The pixels are gone.</exception>
    ZebraMask Zebra(float sdrWhiteScRgb, float exposure);
}

/// <summary>An <see cref="IHdrFrame"/> can no longer be read, because the graphics device that held it was lost.</summary>
public sealed class HdrFrameLostException(string message, Exception? inner = null) : Exception(message, inner);
