using ToneSnip.Core.Geometry;
using ToneSnip.Windows.Interop;
using Xunit;

namespace ToneSnip.Windows.Tests;

public class UiaTreeTests
{
    [Fact]
    public void A_window_that_is_gone_gives_no_parts_and_does_not_throw()
    {
        List<IntRect> parts = UiaTree.Parts(new IntPtr(0x7FFFFFF0), new IntRect(0, 0, 800, 600), out string note);
        Assert.Empty(parts);
        Assert.Contains("0 parts", note);
    }

    [Fact]
    public void A_window_that_is_gone_is_given_up_on_within_the_budget()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        UiaTree.Parts(new IntPtr(0x7FFFFFF0), new IntRect(0, 0, 800, 600), out _);
        // One call can run to its own timeout past the budget; a second of slack for a loaded CI runner.
        Assert.True(watch.ElapsedMilliseconds < UiaTree.BudgetMs + UiaTree.CallTimeoutMs + 1000, $"took {watch.ElapsedMilliseconds} ms");
    }
}
