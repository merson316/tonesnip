using ToneSnip.Core.Output;
using Xunit;

namespace ToneSnip.Core.Tests;

public class SnipNoticeTests
{
    [Fact]
    public void Saved_and_copied_says_both()
    {
        SnipNotice n = SnipNotice.For(@"D:\Shots\Snip 1.png", saveAttempted: true, copyAttempted: true, copied: true);
        Assert.Equal("Snip saved", n.Title);
        Assert.Equal("Snip 1.png  ·  copied to clipboard", n.Detail);
    }

    [Fact]
    public void Saved_with_the_clipboard_off_does_not_claim_a_copy()
    {
        SnipNotice n = SnipNotice.For(@"D:\Shots\Snip 1.png", saveAttempted: true, copyAttempted: false, copied: false);
        Assert.Equal(("Snip saved", "Snip 1.png"), (n.Title, n.Detail));
    }

    [Fact]
    public void A_failed_copy_is_not_reported_as_copied()
    {
        SnipNotice n = SnipNotice.For(null, saveAttempted: false, copyAttempted: true, copied: false);
        Assert.Equal("Snip not copied", n.Title);
        Assert.Contains("clipboard", n.Detail);
    }

    [Fact]
    public void A_failed_save_says_so_and_what_did_happen()
    {
        SnipNotice n = SnipNotice.For(null, saveAttempted: true, copyAttempted: true, copied: true);
        Assert.Equal("Snip not saved", n.Title);
        Assert.Equal("Copied to clipboard; the save failed", n.Detail);
        Assert.Equal(("Snip not saved", "Nothing was saved or copied"), (SnipNotice.For(null, true, true, false).Title, SnipNotice.For(null, true, true, false).Detail));
    }

    [Fact]
    public void Copied_only_reads_as_before()
    {
        SnipNotice n = SnipNotice.For(null, saveAttempted: false, copyAttempted: true, copied: true);
        Assert.Equal(("Snip copied", "Copied to clipboard"), (n.Title, n.Detail));
    }
}
