using ToneSnip.Core.Color;

namespace ToneSnip.Core.Tonemap;

/// <summary>sRGB encode of a linear [0,1] value to 8 bits through a 4096-entry table (no pow per pixel).</summary>
public static class SrgbTable
{
    private const int Size = 4096;
    private static readonly byte[] Table = Build();

    private static byte[] Build()
    {
        var t = new byte[Size + 1];
        for (int i = 0; i <= Size; i++) t[i] = (byte)Math.Clamp((int)MathF.Round(Transfer.SrgbEncode(i / (float)Size) * 255f), 0, 255);
        return t;
    }

    public static byte Encode(float linear)
    {
        if (!(linear > 0f)) return 0;          // also NaN
        if (linear >= 1f) return 255;
        return Table[(int)(linear * Size + 0.5f)];
    }
}
