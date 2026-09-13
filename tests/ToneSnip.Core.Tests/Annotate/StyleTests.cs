using ToneSnip.Core.Annotate;
using Xunit;

namespace ToneSnip.Core.Tests.Annotate;

public class StyleTests
{
    /// <summary>
    /// Keeps the palette in step with the accessible names in the annotate bar's <c>SwatchNames</c>, which live in the
    /// app. When the palette changes, update <c>src/ToneSnip/Annotate/AnnotateBar.xaml.cs</c> and the count here.
    /// </summary>
    [Fact]
    public void Palette_length_is_pinned()
    {
        Assert.Equal(9, Style.Palette.Length);
        // The bar's name table and divider index assume the accent placeholder is last and the rest are opaque.
        Assert.Equal(Style.AccentPlaceholder, Style.Palette[^1]);
        for (int i = 0; i < Style.Palette.Length - 1; i++)
            Assert.Equal(0xFFu, Style.Palette[i] >> 24);
    }

    /// <summary>The band lengths are what <c>BandCell</c> reports as SizeOfSet.</summary>
    [Fact]
    public void Width_and_size_bands_are_pinned()
    {
        Assert.Equal(new[] { 2, 4, 8, 12 }, Style.Widths);
        Assert.Equal(new[] { 14, 20, 28 }, Style.TextSizes);
    }
}
