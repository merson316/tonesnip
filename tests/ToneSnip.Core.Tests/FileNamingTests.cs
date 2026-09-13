using System.Globalization;
using ToneSnip.Core.Output;
using Xunit;

namespace ToneSnip.Core.Tests;

public class FileNamingTests
{
    [Fact]
    public void Builds_snipping_tool_style_names()
        => Assert.Equal("Snip 2026-09-04 231455.png", FileNaming.Build(new DateTime(2026, 9, 4, 23, 14, 55), "png"));

    /// <summary>A non-Gregorian default calendar (Thai Buddhist, Hijri) must not change the year in the name.</summary>
    [Theory]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    [InlineData("de-DE")]
    public void Names_are_invariant_under_a_non_gregorian_default_calendar(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Assert.Equal("Snip 2026-09-04 231455.png", FileNaming.Build(new DateTime(2026, 9, 4, 23, 14, 55), "png"));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Resolves_collisions_with_a_counter()
    {
        var taken = new HashSet<string> { Path.Combine("F", "a.png"), Path.Combine("F", "a (2).png") };
        Assert.Equal(Path.Combine("F", "a (3).png"), FileNaming.Resolve("F", "a.png", taken.Contains));
        Assert.Equal(Path.Combine("F", "b.png"), FileNaming.Resolve("F", "b.png", taken.Contains));
    }
}
