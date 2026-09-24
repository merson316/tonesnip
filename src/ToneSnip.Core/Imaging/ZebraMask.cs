using ToneSnip.Core.Color;

namespace ToneSnip.Core.Imaging;

/// <summary>
/// One bit per pixel of a frame: set where the zebra pass stripes it. Rows are padded to whole 32-bit words, so a
/// monitor-sized mask is a 32nd of its half-float frame and the GPU can write it one word per thread.
/// </summary>
public sealed class ZebraMask
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>Words per row.</summary>
    public int Stride { get; }
    /// <summary>Row-major words; pixel x of row y is bit <c>x % 32</c> of word <c>y * Stride + x / 32</c>.</summary>
    public uint[] Bits { get; }

    public ZebraMask(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width; Height = height; Stride = StrideFor(width);
        Bits = new uint[checked(Stride * height)];
    }

    public static int StrideFor(int width) => (width + 31) / 32;

    public bool Over(int x, int y) => (Bits[y * Stride + (x >> 5)] & (1u << (x & 31))) != 0;

    /// <summary>
    /// The test the zebra pass makes for one pixel, in this exact form: the GPU mask mirrors it (Shaders/Tonemap.hlsl,
    /// CSZebra), and a reordered product could flip a pixel that sits on the threshold. Written as "not at or below"
    /// so a NaN pixel is striped, as it always was.
    /// </summary>
    public static bool Exceeds(float r, float g, float b, float sdrWhiteScRgb, float exposure)
        => !(Transfer.Luminance709(r, g, b) * exposure <= sdrWhiteScRgb);

    /// <summary>The mask of a half-float image, computed on the CPU.</summary>
    public static ZebraMask Of(HalfImage img, float sdrWhiteScRgb, float exposure)
    {
        var mask = new ZebraMask(img.Width, img.Height);
        uint[] bits = mask.Bits;
        ushort[] d = img.Data;
        int w = img.Width, stride = mask.Stride;
        Parallel.For(0, img.Height, y =>
        {
            int s = y * w * 4;
            for (int x = 0; x < w; x++, s += 4)
                if (Exceeds(Transfer.HalfToFloat(d[s]), Transfer.HalfToFloat(d[s + 1]), Transfer.HalfToFloat(d[s + 2]), sdrWhiteScRgb, exposure))
                    bits[y * stride + (x >> 5)] |= 1u << (x & 31);   // a row's words belong to this iteration alone
        });
        return mask;
    }
}
