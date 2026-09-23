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
    public void Bgra8_conversions_agree()
    {
        var img = new HalfImage(4, 1);
        float[] rgb = { 0.5f, 2f, 0.01f,  1f, 1f, 1f,  -1f, 0f, 8f,  0.2f, 0.2f, 0.2f };
        for (int px = 0; px < 4; px++)
            for (int c = 0; c < 3; c++) img.Data[px * 4 + c] = Transfer.FloatToHalf(rgb[px * 3 + c]);
        var p = new TonemapParams { SdrWhiteNits = 200f, PeakNits = 400f };
        var viaTonemapper = new byte[16];
        PixelConvert.ToBgra8Into(img, new AcesTonemapper(p), null, viaTonemapper);
        var viaLut = new byte[16];
        PixelConvert.ToBgra8Into(img, null, new AcesLut(p), viaLut);
        for (int i = 0; i < viaLut.Length; i++) Assert.InRange(viaLut[i], viaTonemapper[i] - 1, viaTonemapper[i] + 1);
        var pass = new byte[16];
        PixelConvert.ToBgra8Into(img, null, null, pass);
        Assert.Equal(255, pass[8]); Assert.Equal(0, pass[10]);   // pixel 2: B=8 -> 255, R=-1 -> 0
        Assert.Equal(255, pass[3]);
    }
}
