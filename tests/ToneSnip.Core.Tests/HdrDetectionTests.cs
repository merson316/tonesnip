using ToneSnip.Core.Capture;
using Xunit;

namespace ToneSnip.Core.Tests;

public class HdrDetectionTests
{
    private const uint Supported = 0x1, Enabled = 0x2, WideColorEnforced = 0x4;

    [Fact]
    public void A_pq_output_is_hdr_whatever_display_config_says()
        => Assert.True(HdrDetection.IsHdr(pqColorSpace: true, linearColorSpace: false, advancedColorBits: Supported | Enabled | WideColorEnforced));

    [Fact]
    public void Advanced_colour_enabled_on_its_own_is_hdr()
        => Assert.True(HdrDetection.IsHdr(false, false, Supported | Enabled));

    [Fact]
    public void An_sdr_display_with_auto_colour_management_is_not_hdr()
    {
        // Windows 11 also sets advancedColorEnabled for an SDR panel with ACM on; wideColorEnforced distinguishes it.
        Assert.False(HdrDetection.IsHdr(false, false, Supported | Enabled | WideColorEnforced));
        Assert.False(HdrDetection.IsHdr(false, true, Supported | Enabled | WideColorEnforced));
    }

    [Fact]
    public void Without_display_config_the_colour_space_decides()
    {
        Assert.True(HdrDetection.IsHdr(false, true, null));
        Assert.False(HdrDetection.IsHdr(false, false, null));
    }
}
