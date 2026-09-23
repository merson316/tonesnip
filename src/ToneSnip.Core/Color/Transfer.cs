namespace ToneSnip.Core.Color;

/// <summary>Transfer functions and colour helpers. All inputs/outputs are floats; nits are absolute cd/m².</summary>
public static class Transfer
{
    private const float PqM1 = 0.1593017578125f;
    private const float PqM2 = 78.84375f;
    private const float PqC1 = 0.8359375f;
    private const float PqC2 = 18.8515625f;
    private const float PqC3 = 18.6875f;

    /// <summary>Linear [0,1] to sRGB-encoded [0,1]. Clamps outside that range.</summary>
    public static float SrgbEncode(float linear)
    {
        if (linear <= 0f) return 0f;
        if (linear >= 1f) return 1f;
        return linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
    }

    /// <summary>sRGB-encoded [0,1] to linear [0,1]. Clamps outside that range.</summary>
    public static float SrgbDecode(float encoded)
    {
        if (encoded <= 0f) return 0f;
        if (encoded >= 1f) return 1f;
        return encoded <= 0.04045f ? encoded / 12.92f : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
    }

    /// <summary>Absolute luminance in nits [0,10000] to ST.2084 PQ signal [0,1].</summary>
    public static float PqEncode(float nits)
    {
        float y = Math.Clamp(nits / 10000f, 0f, 1f);
        float ym1 = MathF.Pow(y, PqM1);
        return MathF.Pow((PqC1 + PqC2 * ym1) / (1f + PqC3 * ym1), PqM2);
    }

    /// <summary>ST.2084 PQ signal [0,1] to absolute luminance in nits.</summary>
    public static float PqDecode(float pq)
    {
        float e = Math.Clamp(pq, 0f, 1f);
        float ep = MathF.Pow(e, 1f / PqM2);
        float num = MathF.Max(ep - PqC1, 0f);
        float den = PqC2 - PqC3 * ep;
        if (den <= 0f) return 10000f;
        return MathF.Pow(num / den, 1f / PqM1) * 10000f;
    }

    public static float Luminance709(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;

    public static float HalfToFloat(ushort bits) => (float)BitConverter.UInt16BitsToHalf(bits);

    public static ushort FloatToHalf(float value) => BitConverter.HalfToUInt16Bits((Half)value);
}
