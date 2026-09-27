using ToneSnip.Core.Diagnostics;
using Xunit;

namespace ToneSnip.Core.Tests;

public class MemoryPlateauTests
{
    private const long Mb = 1024 * 1024;

    private static long[] Cycles(params int[] mb) => mb.Select(m => m * Mb).ToArray();

    /// <summary>The case that fooled the single-cycle verdict: a flat run with one 55 MB spike on the last cycle.</summary>
    [Fact]
    public void A_spike_on_the_last_cycle_is_still_a_plateau()
    {
        MemoryPlateau.Verdict v = MemoryPlateau.Judge(Cycles(180, 120, 121, 119, 122, 120, 118, 121, 120, 175), 15);
        Assert.True(v.Plateau, $"{v.GrowthPercent:F1} %");
        Assert.Equal(120 * Mb, v.LateMedian);
    }

    /// <summary>And its mirror: a spike on cycle 2, the old baseline, must not hide a leak that follows.</summary>
    [Fact]
    public void A_spike_on_the_second_cycle_does_not_hide_a_ramp()
    {
        long[] run = Enumerable.Range(1, 30).Select(c => (100 + 3L * c) * Mb).ToArray();
        run[1] += 60 * Mb;
        MemoryPlateau.Verdict v = MemoryPlateau.Judge(run, 15);
        Assert.False(v.Plateau, $"{v.GrowthPercent:F1} %");
        Assert.Equal((5, 2, 26), (v.Window, v.EarlyFirst, v.LateFirst));
    }

    [Fact]
    public void Cycle_one_is_warm_up_and_never_the_baseline()
    {
        MemoryPlateau.Verdict v = MemoryPlateau.Judge(Cycles(40, 120, 120, 120, 120, 120), 15);
        Assert.True(v.Plateau);
        Assert.Equal(120 * Mb, v.EarlyMedian);
    }

    /// <summary>The default six cycles: windows of three, sharing cycle 4.</summary>
    [Fact]
    public void Six_cycles_compare_two_to_four_with_four_to_six()
    {
        MemoryPlateau.Verdict v = MemoryPlateau.Judge(Cycles(90, 100, 110, 120, 130, 140), 15);
        Assert.Equal((3, 2, 4), (v.Window, v.EarlyFirst, v.LateFirst));
        Assert.Equal((110 * Mb, 130 * Mb), (v.EarlyMedian, v.LateMedian));
        Assert.False(v.Plateau);   // +18 %
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(10, 5)]
    [InlineData(50, 5)]
    public void The_window_grows_with_the_run_up_to_five(int cycles, int window)
        => Assert.Equal(window, MemoryPlateau.Judge(new long[cycles], 15).Window);

    [Fact]
    public void One_cycle_is_not_enough() => Assert.Throws<ArgumentException>(() => MemoryPlateau.Judge(new long[1], 15));

    [Fact]
    public void An_even_window_takes_the_mean_of_the_middle_two()
        => Assert.Equal(15, MemoryPlateau.Median([40, 10, 20, 5], 0, 4));
}
