using System.Runtime.Intrinsics;

namespace ToneSnip.Core.Imaging;

/// <summary>
/// The overlay's dim: BGRA colour channels scaled to <c>keep</c>/255, rounded, with alpha untouched. It runs over the
/// whole monitor on the overlay's first paint and on every full repaint, so it is vectorised; the vector path gives the
/// same bytes as <see cref="Scale"/>, the per-byte formula.
/// </summary>
public static class PixelShade
{
    /// <summary>One channel: <c>(v * keep + 127) / 255</c>, the rounded scale the vector path must match.</summary>
    public static byte Scale(byte v, int keep) => (byte)((v * keep + 127) / 255);

    /// <summary>
    /// Scales a row of BGRA pixels in place (<paramref name="row"/>'s length is a multiple of 4).
    /// <para>Each byte is widened to 16 bits and multiplied by <paramref name="keep"/>, alpha by 255, which with the
    /// rounding below leaves it exactly as it was. For <c>x = v * keep + 127</c>, which is at most 65,152,
    /// <c>(x + 1 + (x &gt;&gt; 8)) &gt;&gt; 8</c> is <c>x / 255</c> exactly, and never leaves 16 bits.</para>
    /// </summary>
    public static void Shade(Span<byte> row, int keep)
    {
        if ((uint)keep > 255) throw new ArgumentOutOfRangeException(nameof(keep));
        int i = 0;
        if (Vector256.IsHardwareAccelerated && row.Length >= Vector256<byte>.Count)
        {
            Vector256<ushort> factor = Vector256.Create(Factor(keep)).AsUInt16();
            for (; i <= row.Length - Vector256<byte>.Count; i += Vector256<byte>.Count)
            {
                (Vector256<ushort> lo, Vector256<ushort> hi) = Vector256.Widen(Vector256.Create<byte>(row.Slice(i, Vector256<byte>.Count)));
                Vector256.Narrow(Divide255(lo * factor), Divide255(hi * factor)).CopyTo(row.Slice(i));
            }
        }
        else if (Vector128.IsHardwareAccelerated && row.Length >= Vector128<byte>.Count)
        {
            Vector128<ushort> factor = Vector128.Create(Factor(keep)).AsUInt16();
            for (; i <= row.Length - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                (Vector128<ushort> lo, Vector128<ushort> hi) = Vector128.Widen(Vector128.Create<byte>(row.Slice(i, Vector128<byte>.Count)));
                Vector128.Narrow(Divide255(lo * factor), Divide255(hi * factor)).CopyTo(row.Slice(i));
            }
        }
        // The tail, and machines with no vector unit. Whole pixels only: i is a multiple of 16.
        for (; i + 3 < row.Length; i += 4)
        {
            row[i] = Scale(row[i], keep);
            row[i + 1] = Scale(row[i + 1], keep);
            row[i + 2] = Scale(row[i + 2], keep);
        }
    }

    /// <summary>Four 16-bit lanes, B, G and R at <paramref name="keep"/> and A at 255, as one 64-bit pattern.</summary>
    private static ulong Factor(int keep)
    {
        ulong k = (uint)keep;
        return k | k << 16 | k << 32 | 255UL << 48;
    }

    private static Vector256<ushort> Divide255(Vector256<ushort> product)
    {
        Vector256<ushort> x = product + Vector256.Create((ushort)127);
        return Vector256.ShiftRightLogical(x + Vector256<ushort>.One + Vector256.ShiftRightLogical(x, 8), 8);
    }

    private static Vector128<ushort> Divide255(Vector128<ushort> product)
    {
        Vector128<ushort> x = product + Vector128.Create((ushort)127);
        return Vector128.ShiftRightLogical(x + Vector128<ushort>.One + Vector128.ShiftRightLogical(x, 8), 8);
    }
}
