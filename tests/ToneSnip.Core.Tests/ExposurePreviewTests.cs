using ToneSnip.Core.Tonemap;
using Xunit;

namespace ToneSnip.Core.Tests;

public class ExposurePreviewTests
{
    [Fact]
    public async Task Values_set_during_a_pass_collapse_to_the_newest()
    {
        var seen = new List<float>();
        var firstPass = new TaskCompletionSource();
        var preview = new ExposurePreview(async m =>
        {
            seen.Add(m);
            if (seen.Count == 1) await firstPass.Task;
            return true;
        }, e => throw e);
        preview.Set(1f);
        preview.Set(2f);
        preview.Set(3f);
        firstPass.SetResult();
        await preview.Idle;
        Assert.Equal(new[] { 1f, 3f }, seen);
    }

    [Fact]
    public async Task Cancel_drops_the_value_not_yet_taken()
    {
        var seen = new List<float>();
        var firstPass = new TaskCompletionSource();
        var preview = new ExposurePreview(async m => { seen.Add(m); await firstPass.Task; return true; }, e => throw e);
        preview.Set(1f);
        preview.Set(2f);
        preview.Cancel();
        firstPass.SetResult();
        await preview.Idle;
        Assert.Equal(new[] { 1f }, seen);
    }

    [Fact]
    public async Task A_pass_that_returns_false_stops_the_loop_and_a_later_value_starts_it_again()
    {
        var seen = new List<float>();
        var firstPass = new TaskCompletionSource();
        var preview = new ExposurePreview(async m => { seen.Add(m); await firstPass.Task; return false; }, e => throw e);
        preview.Set(1f);
        preview.Set(2f);
        firstPass.SetResult();
        await preview.Idle;
        Assert.Equal(new[] { 1f }, seen);
        preview.Set(4f);
        await preview.Idle;
        Assert.Equal(new[] { 1f, 4f }, seen);
    }

    [Fact]
    public async Task The_value_is_taken_only_after_the_gate_opens()
    {
        var seen = new List<float>();
        var gate = new TaskCompletionSource();
        var preview = new ExposurePreview(m => { seen.Add(m); return Task.FromResult(true); }, e => throw e, () => gate.Task);
        preview.Set(1f);
        preview.Set(2f);   // replaces 1 while the loop waits at the gate
        Assert.Empty(seen);
        gate.SetResult();
        await preview.Idle;
        Assert.Equal(new[] { 2f }, seen);
    }

    [Fact]
    public async Task A_pass_that_throws_is_reported_and_ends_the_loop()
    {
        Exception? reported = null;
        var preview = new ExposurePreview(_ => throw new InvalidOperationException("boom"), e => reported = e);
        preview.Set(1f);
        await preview.Idle;
        Assert.IsType<InvalidOperationException>(reported);
    }
}
