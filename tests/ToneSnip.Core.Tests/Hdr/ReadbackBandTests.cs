using ToneSnip.Core.Hdr;
using Xunit;

namespace ToneSnip.Core.Tests.Hdr;

/// <summary>The GPU readback's staging band height (<see cref="ReadbackBand"/>).</summary>
public class ReadbackBandTests
{
    private const int Band = 4 << 20;

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(63)]
    public void A_narrow_read_never_asks_for_a_texture_taller_than_D3D11_allows(int width)
    {
        // 4 MB of 4-byte pixels at these widths would be 16 644 to over a million rows.
        Assert.Equal(ReadbackBand.MaxTextureDimension, ReadbackBand.Rows(width, 100_000, 4, Band));
        Assert.Equal(2160, ReadbackBand.Rows(width, 2160, 4, Band));
    }

    [Fact]
    public void A_band_is_no_taller_than_the_read()
    {
        Assert.Equal(1, ReadbackBand.Rows(3840, 1, 4, Band));
        Assert.Equal(40, ReadbackBand.Rows(200, 40, 8, Band));
    }

    [Fact]
    public void A_wide_read_fits_the_band_bytes_with_at_least_one_row()
    {
        Assert.Equal(273, ReadbackBand.Rows(3840, 2160, 4, Band));   // 4 MB / 15 360 bytes a row
        Assert.Equal(136, ReadbackBand.Rows(3840, 2160, 8, Band));
        Assert.Equal(1, ReadbackBand.Rows(16384, 2160, 8, 1024));
    }

    [Fact]
    public void An_empty_read_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReadbackBand.Rows(0, 10, 4, Band));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReadbackBand.Rows(10, 0, 4, Band));
    }
}
