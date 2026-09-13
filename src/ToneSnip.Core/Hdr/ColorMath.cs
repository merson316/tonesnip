using ToneSnip.Core.Color;

namespace ToneSnip.Core.Hdr;

public static class ColorMath
{
    /// <summary>Linear BT.709 RGB to linear BT.2020 RGB (same D65 white); the inverse of <see cref="Transfer.Bt2020To709"/>.</summary>
    public static void Bt709To2020(ref float r, ref float g, ref float b)
    {
        float r2 = 0.6274f * r + 0.3293f * g + 0.0433f * b;
        float g2 = 0.0691f * r + 0.9195f * g + 0.0114f * b;
        float b2 = 0.0164f * r + 0.0880f * g + 0.8956f * b;
        r = r2; g = g2; b = b2;
    }

    /// <summary>sRGB-encoded 8-bit BGR to linear scRGB scaled so that 8-bit white lands at <paramref name="scale"/> (reference white / 80).</summary>
    public static void LiftSrgb(byte b8, byte g8, byte r8, float scale, out float r, out float g, out float b)
    {
        r = LiftTable[r8] * scale; g = LiftTable[g8] * scale; b = LiftTable[b8] * scale;
    }

    private static readonly float[] LiftTable = BuildLift();
    private static float[] BuildLift() { var t = new float[256]; for (int i = 0; i < 256; i++) t[i] = Transfer.SrgbDecode(i / 255f); return t; }
}
