using ToneSnip.Core.Imaging;

namespace ToneSnip.Core.Record;

/// <summary>
/// The pixels a recording hands its encoder: NV12, BT.709 Y'CbCr at limited (16-235) range, which is what the file is
/// tagged as. They are made from the same 8-bit values a snip of the screen gets, so a frame of the video and a snip
/// of the same moment match; the colour of each 2 x 2 block is the mean of its four pixels.
/// <para>This is the definition. The recorder converts on the graphics card (CSVideo in Shaders/Tonemap.hlsl), and its
/// tests hold the shader to this within one step.</para>
/// </summary>
public static class Nv12
{
    public const float Kr = 0.2126f, Kg = 0.7152f, Kb = 0.0722f;

    /// <summary>Bytes in one frame: a full-size luma plane and a half-height plane of interleaved Cb, Cr pairs.</summary>
    public static int Size(int width, int height) => checked(width * height * 3 / 2);

    /// <summary>Luma of one display-referred pixel, each channel 0 to 1.</summary>
    public static byte Luma(float r, float g, float b) => Quantise(16f + 219f * (Kr * r + Kg * g + Kb * b));

    /// <summary>Cb and Cr of one display-referred colour, each channel 0 to 1.</summary>
    public static (byte Cb, byte Cr) Chroma(float r, float g, float b)
    {
        float y = Kr * r + Kg * g + Kb * b;
        return (Quantise(128f + 224f * (b - y) / 1.8556f), Quantise(128f + 224f * (r - y) / 1.5748f));
    }

    /// <summary>Rounded half to even, as the shader's <c>round</c> is.</summary>
    private static byte Quantise(float v) => (byte)Math.Clamp(MathF.Round(v), 0f, 255f);

    /// <summary>A whole BGRA image (even sides) into <paramref name="nv12"/>, which is <see cref="Size"/> bytes.</summary>
    public static void FromBgra(BgraImage img, Span<byte> nv12)
    {
        int w = img.Width, h = img.Height;
        if ((w & 1) != 0 || (h & 1) != 0) throw new ArgumentException($"{w}x{h}: NV12 needs even sides", nameof(img));
        if (nv12.Length < Size(w, h)) throw new ArgumentException($"{nv12.Length} bytes is less than a {w}x{h} frame", nameof(nv12));
        byte[] px = img.Data;
        const float Unit = 1f / 255f;
        for (int y = 0; y < h; y += 2)
        {
            Span<byte> chroma = nv12.Slice(w * h + y / 2 * w, w);
            for (int x = 0; x < w; x += 2)
            {
                float r = 0, g = 0, b = 0;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int i = ((y + dy) * w + x + dx) * 4;
                        float pb = px[i] * Unit, pg = px[i + 1] * Unit, pr = px[i + 2] * Unit;
                        nv12[(y + dy) * w + x + dx] = Luma(pr, pg, pb);
                        r += pr; g += pg; b += pb;
                    }
                (byte cb, byte cr) = Chroma(r * 0.25f, g * 0.25f, b * 0.25f);
                chroma[x] = cb;
                chroma[x + 1] = cr;
            }
        }
    }
}
