using ToneSnip.Core.Color;
using Xunit;

namespace ToneSnip.Core.Tests;

public class TransferTests
{
    [Theory]
    [InlineData(0f)] [InlineData(0.001f)] [InlineData(0.2f)] [InlineData(0.5f)] [InlineData(0.9f)] [InlineData(1f)]
    public void Srgb_round_trips(float linear)
        => Assert.Equal(linear, Transfer.SrgbDecode(Transfer.SrgbEncode(linear)), 0.0005);

    [Fact]
    public void Srgb_encode_known_value() => Assert.Equal(0.7354, Transfer.SrgbEncode(0.5f), 0.001);

    [Fact]
    public void Srgb_encode_clamps()
    {
        Assert.Equal(0f, Transfer.SrgbEncode(-1f));
        Assert.Equal(1f, Transfer.SrgbEncode(5f));
    }

    [Theory]
    [InlineData(1f)] [InlineData(100f)] [InlineData(1000f)] [InlineData(10000f)]
    public void Pq_round_trips(float nits)
        => Assert.Equal(nits, Transfer.PqDecode(Transfer.PqEncode(nits)), nits * 0.01);

    [Fact]
    public void Pq_known_values()
    {
        Assert.Equal(0.508, Transfer.PqEncode(100f), 0.002);
        Assert.Equal(1.0, Transfer.PqEncode(10000f), 0.001);
        Assert.Equal(0.0, Transfer.PqEncode(0f), 0.0001);
    }

    [Fact]
    public void Half_decodes_known_bit_patterns()
    {
        Assert.Equal(1f, Transfer.HalfToFloat(0x3C00));
        Assert.Equal(2f, Transfer.HalfToFloat(0x4000));
        Assert.Equal(0f, Transfer.HalfToFloat(0x0000));
        Assert.Equal(-0.5f, Transfer.HalfToFloat(0xB800));
    }

    [Fact]
    public void Luminance_of_white_is_one() => Assert.Equal(1.0, Transfer.Luminance709(1f, 1f, 1f), 0.0001);

    [Fact]
    public void Bt2020_white_stays_white()
    {
        float r = 1f, g = 1f, b = 1f;
        Transfer.Bt2020To709(ref r, ref g, ref b);
        Assert.Equal(1.0, r, 0.01);
        Assert.Equal(1.0, g, 0.01);
        Assert.Equal(1.0, b, 0.01);
    }
}
