using ToneSnip.Core.Imaging;
using Xunit;

namespace ToneSnip.Core.Tests;

public class PixelShadeTests
{
    /// <summary>Every byte value in every channel, at every keep, against the per-byte formula the overlay used before
    /// it was vectorised. 259 pixels, so the vector loop runs and the three-pixel tail goes through the scalar one.</summary>
    [Fact]
    public void Shade_matches_the_per_byte_formula_for_every_value_and_keep_and_leaves_alpha()
    {
        var source = new byte[259 * 4];
        for (int p = 0; p < 259; p++)
        {
            int v = p & 255;
            source[p * 4] = (byte)v; source[p * 4 + 1] = (byte)(255 - v); source[p * 4 + 2] = (byte)(v ^ 0x5A); source[p * 4 + 3] = (byte)(v * 7);
        }
        var row = new byte[source.Length];
        for (int keep = 0; keep <= 255; keep++)
        {
            source.CopyTo(row, 0);
            PixelShade.Shade(row, keep);
            for (int i = 0; i < row.Length; i++)
            {
                byte expected = i % 4 == 3 ? source[i] : (byte)((source[i] * keep + 127) / 255);
                Assert.True(expected == row[i], $"keep {keep}, byte {i}: {source[i]} -> {row[i]}, expected {expected}");
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(28)]
    [InlineData(64)]
    public void Shade_handles_rows_shorter_than_or_around_a_vector(int bytes)
    {
        var rng = new Random(bytes);
        var source = new byte[bytes];
        rng.NextBytes(source);
        var row = (byte[])source.Clone();
        PixelShade.Shade(row, 153);
        for (int i = 0; i < bytes; i++) Assert.Equal(i % 4 == 3 ? source[i] : PixelShade.Scale(source[i], 153), row[i]);
    }

    [Fact]
    public void Shade_rejects_a_keep_outside_a_byte()
        => Assert.Throws<ArgumentOutOfRangeException>(() => PixelShade.Shade(new byte[4], 256));
}
