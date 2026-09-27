using System.Runtime.CompilerServices;

namespace ToneSnip.Core.Imaging;

/// <summary>Composites a straight-alpha image onto an opaque background, for formats with no alpha.</summary>
public static class Flatten
{
    /// <summary>Whether every pixel is fully opaque, so flattening would change nothing.</summary>
    public static bool IsOpaque(BgraImage img)
    {
        byte[] d = img.Data;
        for (int i = 3; i < d.Length; i += 4) if (d[i] != 255) return false;
        return true;
    }

    /// <summary>A copy on white, matching the SDR JPEG encoder (<see cref="ToBgrOnWhite"/>), so the SDR JPEG and the
    /// UltraHDR base of the same snip agree outside a freeform lasso.</summary>
    public static BgraImage OnWhite(BgraImage img)
    {
        var o = new BgraImage(img.Width, img.Height, (byte[])img.Data.Clone());
        byte[] d = o.Data;
        for (int i = 0; i < d.Length; i += 4)
        {
            if (d[i + 3] == 255) continue;
            (d[i], d[i + 1], d[i + 2]) = OverWhite(d[i], d[i + 1], d[i + 2], d[i + 3]);
            d[i + 3] = 255;
        }
        return o;
    }

    /// <summary>
    /// Rows [<paramref name="top"/>, <paramref name="top"/> + <paramref name="count"/>) of <paramref name="img"/>
    /// packed as 24-bit BGR on white, <c>Width * 3</c> bytes a row, into <paramref name="bgr"/>: what the JPEG encoder is
    /// handed a band at a time, so no flattened or packed copy of the whole image is made. Each pixel is the one
    /// <see cref="OnWhite"/> makes.
    /// </summary>
    public static void ToBgrOnWhite(BgraImage img, int top, int count, Span<byte> bgr)
    {
        if (top < 0 || count < 0 || top + count > img.Height) throw new ArgumentOutOfRangeException(nameof(count), $"rows {top}..{top + count} are outside {img.Height}");
        if (bgr.Length < count * img.Width * 3) throw new ArgumentException("the band is too small", nameof(bgr));
        ReadOnlySpan<byte> src = img.Data.AsSpan(top * img.Width * 4, count * img.Width * 4);
        for (int i = 0, o = 0; i < src.Length; i += 4, o += 3)
            (bgr[o], bgr[o + 1], bgr[o + 2]) = OverWhite(src[i], src[i + 1], src[i + 2], src[i + 3]);
    }

    /// <summary>One straight-alpha BGR pixel composited over white: the single blend <see cref="OnWhite"/>,
    /// <see cref="ToBgrOnWhite"/> and the gain map use, which must agree to the last bit (see <see cref="OnWhite"/>).
    /// An opaque pixel comes back unchanged.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (byte B, byte G, byte R) OverWhite(byte b, byte g, byte r, int alpha)
        => alpha == 255 ? (b, g, r) : (OverWhite(b, alpha), OverWhite(g, alpha), OverWhite(r, alpha));

    /// <summary>One straight-alpha colour channel composited over white.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte OverWhite(byte channel, int alpha) => (byte)((channel * alpha + 255 * (255 - alpha)) / 255);
}
