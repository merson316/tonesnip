using ToneSnip.Core.Color;

namespace ToneSnip.Core.Tonemap;

/// <summary>ACES is per channel, so the whole curve plus sRGB encode collapses into one byte per FP16 bit pattern.</summary>
public sealed class AcesLut
{
    public byte[] Table { get; } = new byte[65536];

    public AcesLut(TonemapParams p)
    {
        var tm = new AcesTonemapper(p);
        for (int bits = 0; bits < 65536; bits++)
        {
            float v = (float)BitConverter.UInt16BitsToHalf((ushort)bits);
            if (float.IsNaN(v)) v = 0f; else if (float.IsInfinity(v)) v = v > 0 ? 65504f : 0f;
            float r = v, g = v, b = v;
            tm.Map(ref r, ref g, ref b);
            Table[bits] = (byte)Math.Clamp((int)MathF.Round(Transfer.SrgbEncode(r) * 255f), 0, 255);
        }
    }

    public byte Map(float linear) => Table[BitConverter.HalfToUInt16Bits((Half)linear)];
}
