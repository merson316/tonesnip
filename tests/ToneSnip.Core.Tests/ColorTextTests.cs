using System.Globalization;
using ToneSnip.Core.Config;
using ToneSnip.Core.Extract;
using Xunit;

namespace ToneSnip.Core.Tests;

public class ColorTextTests
{
    [Fact]
    public void Hex_is_rrggbb_upper_case_without_alpha()
        => Assert.Equal("#1A2B3C", ColorText.Format(0x801A2B3C, "hex"));

    [Fact]
    public void Rgb_is_css_rgb()
        => Assert.Equal("rgb(26, 43, 60)", ColorText.Format(0xFF1A2B3C, "rgb"));

    [Fact]
    public void Nits_follow_the_colour_only_when_there_are_some()
    {
        Assert.Equal("#FFFFFF, 480 nits", ColorText.WithNits("#FFFFFF", 480.4f, precise: false));
        Assert.Equal("#FFFFFF", ColorText.WithNits("#FFFFFF", null, precise: false));
    }

    [Fact]
    public void The_confirmation_says_what_was_copied_and_keeps_the_nits_apart_otherwise()
    {
        Assert.Equal("Copied #FFFFFF, 480 nits", ColorText.Confirmation("#FFFFFF", 480f, copiedNits: true, precise: false));
        Assert.Equal("Copied #FFFFFF  ·  480 nits", ColorText.Confirmation("#FFFFFF", 480f, copiedNits: false, precise: false));
        Assert.Equal("Copied #FFFFFF", ColorText.Confirmation("#FFFFFF", null, copiedNits: true, precise: false));
    }

    [Fact]
    public void Nits_are_whole_by_default_and_three_decimals_when_precise()
    {
        Assert.Equal("480", ColorText.Nits(480.4f, precise: false));
        Assert.Equal("480.125", ColorText.Nits(480.1254f, precise: true));
        Assert.Equal("480.000", ColorText.Nits(480f, precise: true));
    }

    [Fact]
    public void Precise_nits_reach_the_copy_the_confirmation_and_the_loupe()
    {
        Assert.Equal("#FFFFFF, 480.125 nits", ColorText.WithNits("#FFFFFF", 480.125f, precise: true));
        Assert.Equal("Copied #FFFFFF  ·  480.125 nits", ColorText.Confirmation("#FFFFFF", 480.125f, copiedNits: false, precise: true));
        Assert.Equal("#FFFFFF  ·  480.125 nits", ColorText.Loupe(0xFFFFFFFF, "hex", 480.125f, precise: true));
    }

    [Fact]
    public void Nits_use_a_full_stop_whatever_the_locale()
    {
        // "#FFFFFF, 480,125 nits" would read as a list once copied.
        CultureInfo was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("#FFFFFF, 480.125 nits", ColorText.WithNits("#FFFFFF", 480.125f, precise: true));
        }
        finally { CultureInfo.CurrentCulture = was; }
    }

    [Fact]
    public void The_format_setting_is_sanitised()
    {
        Assert.Equal("rgb", new SnipSettings { ColorFormat = "RGB" }.Sanitized(out _).ColorFormat);
        SnipSettings bad = new SnipSettings { ColorFormat = "hsl" }.Sanitized(out List<string> fixes);
        Assert.Equal("hex", bad.ColorFormat);
        Assert.Contains(fixes, f => f.StartsWith("colorFormat", StringComparison.Ordinal));
        Assert.Equal("hex", (new SnipSettings { ColorFormat = null! }).Sanitized(out _).ColorFormat);
    }
}
