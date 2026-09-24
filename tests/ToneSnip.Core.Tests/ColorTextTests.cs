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
        Assert.Equal("#FFFFFF, 480 nits", ColorText.WithNits("#FFFFFF", 480.4f));
        Assert.Equal("#FFFFFF", ColorText.WithNits("#FFFFFF", null));
    }

    [Fact]
    public void The_confirmation_says_what_was_copied_and_keeps_the_nits_apart_otherwise()
    {
        Assert.Equal("Copied #FFFFFF, 480 nits", ColorText.Confirmation("#FFFFFF", 480f, copiedNits: true));
        Assert.Equal("Copied #FFFFFF  ·  480 nits", ColorText.Confirmation("#FFFFFF", 480f, copiedNits: false));
        Assert.Equal("Copied #FFFFFF", ColorText.Confirmation("#FFFFFF", null, copiedNits: true));
    }

    [Fact]
    public void The_format_setting_is_sanitised()
    {
        Assert.Equal("rgb", new SnipSettings { ColorFormat = "RGB" }.Sanitized(out _).ColorFormat);
        SnipSettings bad = new SnipSettings { ColorFormat = "hsl" }.Sanitized(out List<string> fixes);
        Assert.Equal("hex", bad.ColorFormat);
        Assert.Contains(fixes, f => f.StartsWith("colorFormat"));
        Assert.Equal("hex", (new SnipSettings { ColorFormat = null! }).Sanitized(out _).ColorFormat);
    }
}
