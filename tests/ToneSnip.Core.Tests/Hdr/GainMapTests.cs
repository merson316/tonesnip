using ToneSnip.Core.Color;
using ToneSnip.Core.Hdr;
using ToneSnip.Core.Imaging;
using Xunit;

namespace ToneSnip.Core.Tests.Hdr;

public class GainMapTests
{
    private static HalfImage Half(int w, int h, params float[] perPixelValue)
    {
        var img = new HalfImage(w, h);
        for (int p = 0; p < w * h; p++) for (int c = 0; c < 3; c++) img.Data[p * 4 + c] = Transfer.FloatToHalf(perPixelValue[p]);
        for (int p = 0; p < w * h; p++) img.Data[p * 4 + 3] = 0x3C00;
        return img;
    }

    [Fact]
    public void Sdr_white_has_zero_gain_and_four_times_white_has_log2_four()
    {
        // reference white 160 nits => scRGB 2.0 is SDR white
        HalfImage hdr = Half(3, 1, 2f, 8f, 0.5f);
        var sdr = BgraImage.Blank(3, 1);
        byte[] px = { 255, 255, 255, 255,  255, 255, 255, 255,  0, 0, 0, 255 };   // white, white (clipped), third pixel set to grey below
        Array.Copy(px, sdr.Data, 12);
        byte g = (byte)Math.Round(Transfer.SrgbEncode(0.25f) * 255);   // 0.5 / 2.0 in SDR terms
        sdr.Data[8] = g; sdr.Data[9] = g; sdr.Data[10] = g;
        GainMapResult m = GainMap.Compute(hdr, sdr, 160f);
        Assert.Equal(3, m.Width); Assert.Equal(1, m.Height);
        Assert.InRange(m.Min, -0.2f, 0f); Assert.InRange(m.Max, 1.9f, 2.1f);
        float G(int i) => m.Min + m.Gray[i] / 255f * (m.Max - m.Min);
        Assert.InRange(G(0), -0.05f, 0.05f);   // white: ratio 1 → 0
        Assert.InRange(G(1), 1.9f, 2.05f);      // 4× white → log2 4 = 2
        Assert.InRange(G(2), -0.1f, 0.1f);      // grey matched in both → 0
    }

    [Fact]
    public void An_all_sdr_image_has_a_flat_zero_map_with_a_nonzero_max()
    {
        HalfImage hdr = Half(2, 2, 1f, 1f, 1f, 1f);
        var sdr = BgraImage.Blank(2, 2); Array.Fill(sdr.Data, (byte)255);
        GainMapResult m = GainMap.Compute(hdr, sdr, 80f);
        Assert.True(m.Max > 0f);                   // decoders divide by (max - min)
        Assert.All(m.Gray, v => Assert.InRange(v, 0, 2));
    }
}
