using ToneSnip.Core.Color;
using ToneSnip.Core.Imaging;

namespace ToneSnip.Windows.Tests;

/// <summary>The test pictures, the same as tools/gpu-tonemap-bench's.</summary>
internal static class Images
{
    /// <summary>
    /// 256 x 512: every half-float bit pattern in each channel (NaN, infinities, negatives and subnormals included), then
    /// a 30-stop luminance ramp across the hues. Whatever a capture can hold, the GPU must tonemap as the CPU does.
    /// </summary>
    public static HalfImage Stress()
    {
        var img = new HalfImage(256, 512);
        ushort[] d = img.Data;
        for (int i = 0; i < 65536; i++)
        {
            d[i * 4] = (ushort)i; d[i * 4 + 1] = (ushort)((i * 7919) & 0xFFFF); d[i * 4 + 2] = (ushort)((i * 104729) & 0xFFFF); d[i * 4 + 3] = 0x3C00;
        }
        for (int i = 65536; i < 131072; i++)
        {
            int k = i - 65536;
            float l = MathF.Pow(2f, -14f + 30f * (k % 256) / 255f);
            float h = k / 256 / 256f * 6.2831853f;
            float r = l * (0.6f + 0.4f * MathF.Cos(h)), g = l * (0.6f + 0.4f * MathF.Cos(h - 2.094f)), b = l * (0.6f + 0.4f * MathF.Cos(h + 2.094f));
            d[i * 4] = Transfer.FloatToHalf(r); d[i * 4 + 1] = Transfer.FloatToHalf(g); d[i * 4 + 2] = Transfer.FloatToHalf(b); d[i * 4 + 3] = 0x3C00;
        }
        return img;
    }

    /// <summary>
    /// A desktop-like frame, <paramref name="width"/> x <paramref name="height"/>: diagonal luminance from black past
    /// 1000 nits with a different tint per quarter. Large enough, at the sizes the tests use, that a readback crosses
    /// several of the tonemapper's 4 MB staging bands.
    /// </summary>
    public static HalfImage Scene(int width, int height)
    {
        var img = new HalfImage(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                float v = 14f * (x + y) / (width + height);   // up to 14 x 80 = 1120 nits
                (float tr, float tg, float tb) = ((x * 2 / width) + (y * 2 / height) * 2) switch
                {
                    0 => (1f, 1f, 1f), 1 => (1f, 0.7f, 0.4f), 2 => (0.4f, 0.8f, 1f), _ => (0.9f, 0.3f, 0.9f),
                };
                img.Data[i] = Transfer.FloatToHalf(v * tr); img.Data[i + 1] = Transfer.FloatToHalf(v * tg);
                img.Data[i + 2] = Transfer.FloatToHalf(v * tb); img.Data[i + 3] = 0x3C00;
            }
        return img;
    }

    /// <summary>The largest per-channel difference over B, G and R, and how many pixels differ at all.</summary>
    public static (int MaxLsb, long Differing) Compare(BgraImage a, BgraImage b)
    {
        byte[] x = a.Data, y = b.Data;
        int max = 0; long differing = 0;
        for (int i = 0; i < x.Length; i += 4)
        {
            int m = 0;
            for (int c = 0; c < 3; c++) m = Math.Max(m, Math.Abs(x[i + c] - y[i + c]));
            if (m > 0) differing++;
            max = Math.Max(max, m);
        }
        return (max, differing);
    }
}
