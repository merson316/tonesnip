using ToneSnip.Core.Color;
using ToneSnip.Core.Imaging;
using ToneSnip.Core.Tonemap;
using Xunit;

namespace ToneSnip.Core.Tests;

public class LutTests
{
    [Fact]
    public void Srgb_table_is_within_one_code_value_of_the_formula()
    {
        for (int i = 0; i <= 100_000; i++)
        {
            float v = i / 100_000f;
            int exact = (int)MathF.Round(Transfer.SrgbEncode(v) * 255f);
            Assert.InRange(SrgbTable.Encode(v), exact - 1, exact + 1);
        }
        Assert.Equal(0, SrgbTable.Encode(-5f));
        Assert.Equal(255, SrgbTable.Encode(7f));
        Assert.Equal(0, SrgbTable.Encode(float.NaN));
    }

    [Fact]
    public void Aces_lut_matches_the_float_tonemapper_on_every_half()
    {
        var p = new TonemapParams { SdrWhiteNits = 212f, PeakNits = 456f, Exposure = 1.3f };
        var lut = new AcesLut(p);
        var tm = new AcesTonemapper(p);
        int worst = 0;
        for (int bits = 0; bits < 65536; bits++)
        {
            float v = (float)BitConverter.UInt16BitsToHalf((ushort)bits);
            if (float.IsNaN(v) || float.IsInfinity(v)) continue;
            float r = v, g = v, b = v;
            tm.Map(ref r, ref g, ref b);
            int exact = (int)MathF.Round(Transfer.SrgbEncode(r) * 255f);
            worst = Math.Max(worst, Math.Abs(exact - lut.Map(v)));
        }
        Assert.True(worst <= 1, $"worst error {worst}");
    }

    [Fact]
    public void Rgba8_conversions_agree()
    {
        var img = new FloatImage(4, 1);
        float[] d = img.Data;
        d[0] = 0.5f; d[1] = 2f; d[2] = 0.01f;  d[3] = 1f; d[4] = 1f; d[5] = 1f;  d[6] = -1f; d[7] = 0f; d[8] = 8f;  d[9] = 0.2f; d[10] = 0.2f; d[11] = 0.2f;
        var p = new TonemapParams { SdrWhiteNits = 200f, PeakNits = 400f };
        byte[] a = PixelConvert.ToRgba8(img, new AcesTonemapper(p));
        byte[] b = PixelConvert.ToRgba8(img, new AcesLut(p));
        for (int i = 0; i < a.Length; i++) Assert.InRange(b[i], a[i] - 1, a[i] + 1);
        byte[] pass = PixelConvert.ToRgba8Passthrough(img);
        Assert.Equal(255, pass[3]); Assert.Equal(255, pass[4]); Assert.Equal(0, pass[8]); Assert.Equal(255, pass[10]);
        // The BGRA path from half floats agrees with the float path, channels swapped.
        BgraImage bgra = PixelConvert.ToBgra8(HalfImage.FromFloat(img), new AcesTonemapper(p));
        for (int px = 0; px < 4; px++)
        {
            Assert.InRange(bgra.Data[px * 4 + 2], a[px * 4] - 1, a[px * 4] + 1);
            Assert.InRange(bgra.Data[px * 4], a[px * 4 + 2] - 1, a[px * 4 + 2] + 1);
        }
        BgraImage viaLut = PixelConvert.ToBgra8(HalfImage.FromFloat(img), new AcesLut(p));
        for (int i = 0; i < viaLut.Data.Length; i++) Assert.InRange(viaLut.Data[i], bgra.Data[i] - 1, bgra.Data[i] + 1);
        BgraImage passHalf = PixelConvert.ToBgra8Passthrough(HalfImage.FromFloat(img));
        Assert.Equal(255, passHalf.Data[8]); Assert.Equal(0, passHalf.Data[10]);   // pixel 2: B=8 -> 255, R=-1 -> 0
    }
}
