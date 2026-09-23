namespace ToneSnip.Core.Tonemap;

/// <summary>
/// Runs the live exposure preview while the slider moves: one re-tonemap pass at a time, always for the newest value, at
/// most one pass per ~16 ms, so dragging cannot queue up a pass per step. Used on the UI thread, whose context the
/// passes resume on.
/// </summary>
/// <param name="pass">One preview pass for a multiplier; false stops the loop (the surface is closing, or there is
/// nothing to tonemap).</param>
/// <param name="failed">A pass threw; the loop has stopped.</param>
/// <param name="beforeTake">Optional: a task to wait out before each value is taken, asked again until it returns null
/// or a completed task. The editor waits here for an HDR sidecar write that reads the image off the UI thread.</param>
public sealed class ExposurePreview(Func<float, Task<bool>> pass, Action<Exception> failed, Func<Task?>? beforeTake = null)
{
    private const int PassGapMs = 16;

    private float _pending = -1;   // newest value waiting for the loop; -1 = nothing pending
    private bool _busy;
    private Task? _loop;

    /// <summary>Completes when no pass is in flight, so a save or a close can join the last one.</summary>
    public Task Idle => _loop ?? Task.CompletedTask;

    /// <summary>Asks for a pass at <paramref name="multiplier"/>, replacing any value not yet taken.</summary>
    public void Set(float multiplier)
    {
        _pending = multiplier;
        if (_busy) return;
        _loop = Loop();
    }

    /// <summary>Drops the value not yet taken; a pass already running finishes (await <see cref="Idle"/> to join it).</summary>
    public void Cancel() => _pending = -1;

    private async Task Loop()
    {
        _busy = true;
        try
        {
            while (_pending > 0)
            {
                // Looped, with no await between the last check and the take, because the wait can end with another
                // one already started.
                while (beforeTake?.Invoke() is { IsCompleted: false } wait) await wait;
                if (!(_pending > 0)) break;
                float m = _pending; _pending = -1;
                if (!await pass(m)) break;
                await Task.Delay(PassGapMs);
            }
        }
        catch (Exception e) { failed(e); }
        finally { _busy = false; }
    }
}
