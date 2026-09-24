using ToneSnip.Core.Startup;
using Xunit;

namespace ToneSnip.Core.Tests;

public class ExtractionSweepTests
{
    private static readonly string Temp = Path.Combine(Path.GetTempPath(), "sweep-tests");
    private static readonly string Root = Path.Combine(Temp, ".net", "tonesnip");
    private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_bundle_root_is_the_exe_named_folder_under_dot_net()
    {
        string mine = Path.Combine(Root, "ifIegvN4I2ZAnplIJxnrT41zgM0BinE=");
        Assert.Equal(Root, ExtractionSweep.BundleRoot(mine, "tonesnip"));
        // AppContext.BaseDirectory carries a trailing separator.
        Assert.Equal(Root, ExtractionSweep.BundleRoot(mine + Path.DirectorySeparatorChar, "TONESNIP"));
    }

    [Fact]
    public void A_folder_that_is_not_a_default_extraction_folder_has_no_bundle_root()
    {
        // A folder publish, the MSIX's install folder, or DOTNET_BUNDLE_EXTRACT_BASE_DIR elsewhere: never sweep there.
        Assert.Null(ExtractionSweep.BundleRoot(Path.Combine(Temp, "Tools", "tonesnip"), "tonesnip"));
        Assert.Null(ExtractionSweep.BundleRoot(Path.Combine(Temp, "custom", "tonesnip", "hash"), "tonesnip"));
        // A renamed exe extracts under its own name, so another name's folder is not this exe's to sweep.
        Assert.Null(ExtractionSweep.BundleRoot(Path.Combine(Root, "hash"), "tonesnip (1)"));
        Assert.Null(ExtractionSweep.BundleRoot("", "tonesnip"));
    }

    [Fact]
    public void Old_siblings_are_stale_but_this_build_and_recent_ones_are_not()
    {
        string mine = Path.Combine(Root, "current");
        string old = Path.Combine(Root, "previous-build"), fresh = Path.Combine(Root, "starting-now"), leftover = Path.Combine(Root, "mp1UvMQXtVln");
        IReadOnlyList<string> stale = ExtractionSweep.Stale(mine + Path.DirectorySeparatorChar, new[]
        {
            (mine, Now.AddDays(-30)),
            (old, Now.AddDays(-7)),
            (leftover, Now - ExtractionSweep.MinimumAge),
            (fresh, Now.AddMinutes(-5)),
        }, Now);
        Assert.Equal(new[] { old, leftover }, stale);
    }
}
