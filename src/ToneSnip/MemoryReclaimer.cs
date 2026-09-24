using System.Diagnostics;
using System.Runtime;
using ToneSnip.App.Capture;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Windows.Hotkeys;
using ToneSnip.Windows.Interop;

namespace ToneSnip.App;

/// <summary>
/// Gives memory back after a snip or an editor closes: a compacting collection a second later, and the pooled frame
/// buffers released after <see cref="FrameIdleRelease"/> without a snip, so an idle tray app returns to its startup
/// footprint.
/// <para>The app's services are read through accessors at the moment they are needed, since they are created during
/// startup, after this object.</para>
/// </summary>
internal sealed class MemoryReclaimer
{
    private readonly ILog _log;
    private readonly Func<FrameGrabber?> _grabber;
    private readonly Func<SnipSession?> _session;
    private readonly Func<KeyboardHook?> _hook;
    /// <summary>Runs before the pooled buffers go: the app packs the Settings preview's frame unless Settings is open.</summary>
    private readonly Action _beforeFrameRelease;

    public MemoryReclaimer(ILog log, Func<FrameGrabber?> grabber, Func<SnipSession?> session, Func<KeyboardHook?> hook, Action beforeFrameRelease)
    {
        _log = log;
        _grabber = grabber;
        _session = session;
        _hook = hook;
        _beforeFrameRelease = beforeFrameRelease;
    }

    /// <summary>
    /// A blocking, compacting gen 2 collection (including the LOH), then a re-arm of the keyboard hook: a blocking GC
    /// also stalls the hook callback, and Windows silently removes a hook that overruns LowLevelHooksTimeout.
    /// </summary>
    /// <returns>The GC pause, for logging.</returns>
    private TimeSpan CompactingCollect()
    {
        TimeSpan before = GC.GetTotalPauseDuration();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        TimeSpan pause = GC.GetTotalPauseDuration() - before;
        if (pause > TimeSpan.FromMilliseconds(250)) _log.Warn($"memory: the compacting collection paused the process for {pause.TotalMilliseconds:F0} ms; the keyboard hook is re-armed after it");
        _hook()?.Rearm();
        return pause;
    }

    /// <summary>GDI (0) or USER (1) handle count of this process, for spotting leaked bitmaps or DCs. Asked with the
    /// pseudo-handle, which needs no closing; Process.GetCurrentProcess().Handle opened a real one per call that was only
    /// closed by a finalizer.</summary>
    private static uint GuiResources(uint flags) => User32.GetGuiResources(Kernel32.GetCurrentProcess(), flags);

    /// <summary>Set while a reclaim is in flight, so a second snip finishing on top of the first does not queue another.</summary>
    private int _reclaiming;

    /// <summary>How long without a snip before the pooled frame buffers are released. Long enough to cover
    /// "Escape, then try again"; a grab into fresh buffers is not noticeably slower than into pooled ones.</summary>
    private static readonly TimeSpan FrameIdleRelease = TimeSpan.FromMinutes(1);
    /// <summary>Incremented by every <c>Idle</c>; a pending release acts only if the ticket is still the one it was
    /// armed with.</summary>
    private int _idleTicket;

    /// <summary>
    /// After <see cref="FrameIdleRelease"/> with no snip, drops the pooled frame buffers and collects, so an idle tray
    /// app returns to its startup footprint.
    /// <para>Dropping the buffers is safe even mid-snip (a grab holding one keeps it). The blocking collect is skipped
    /// if a snip has started; that snip's own <c>Idle</c> reclaims later. A snip starting during the collect is delayed
    /// by the rest of it, which is accepted.</para>
    /// </summary>
    public void ReleaseFramesWhenIdle()
    {
        int ticket = Interlocked.Increment(ref _idleTicket);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FrameIdleRelease);
                if (Volatile.Read(ref _idleTicket) != ticket || _session()?.Busy == true) return;   // a newer idle period owns the release
                _beforeFrameRelease();
                FrameGrabber grabber = _grabber()!;
                long held = grabber.PooledBytes;
                if (held == 0) return;
                grabber.ReleaseBuffers();
                if (_session()?.Busy == true) { _log.Info($"frames released after {FrameIdleRelease.TotalMinutes:F0} min idle: {held / 1_048_576} MB of pooled buffers; a snip started, so the collect is left to its own reclaim"); return; }
                TimeSpan pause = CompactingCollect();
                using var me = Process.GetCurrentProcess();
                _log.Info($"frames released after {FrameIdleRelease.TotalMinutes:F0} min idle: {held / 1_048_576} MB of pooled buffers, managed now {GC.GetTotalMemory(false) / 1_048_576} MB, private {me.PrivateMemorySize64 / 1_048_576} MB, working set {me.WorkingSet64 / 1_048_576} MB, task manager {Interop.ProcessMemory.PrivateWorkingSet() / 1_048_576} MB, gc pause {pause.TotalMilliseconds:F0} ms");
                DebugHooks.MemoryMark("after frames released");
            }
            catch (Exception e) { _log.Warn("frame release: " + e.Message); }
        });
    }

    /// <summary>
    /// Reclaims memory on a background thread a second after a snip ends or an editor closes, then logs where memory
    /// landed under <paramref name="label"/>. The snip's intermediates (the composite, the encoded PNG) are garbage by
    /// then, and an idle tray app may not allocate again for hours, so without this they would sit in the private
    /// working set until the frame release a minute later.
    /// <para>One compacting collection, not the separate collect and finalizer wait that used to precede it: each
    /// blocking pause also stalls the keyboard hook's callback. Finalizers queued by this collection still run straight
    /// after it and free their native memory; only their small managed shells wait for a later collection.</para>
    /// <para>The collection is blocking because the LOH is compacted only by a blocking gen 2 once
    /// <c>CompactOnce</c> is set. Busy is checked after the sleep, since a new snip may have started meanwhile; if so
    /// this gives up, and that snip's own <c>Idle</c> re-arms it.</para>
    /// </summary>
    public void Reclaim(string label)
    {
        if (Interlocked.Exchange(ref _reclaiming, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try
            {
                Thread.Sleep(1000);
                if (_session()?.Busy == true) return;   // a snip started meanwhile; its Idle re-arms this
                TimeSpan pause = CompactingCollect();
                using var me = Process.GetCurrentProcess();
                GCMemoryInfo gc = GC.GetGCMemoryInfo();
                _log.Info($"{label}: managed {GC.GetTotalMemory(false) / 1_048_576} MB, gc committed {gc.TotalCommittedBytes / 1_048_576} MB, private {me.PrivateMemorySize64 / 1_048_576} MB, working set {me.WorkingSet64 / 1_048_576} MB, task manager {Interop.ProcessMemory.PrivateWorkingSet() / 1_048_576} MB, pooled frames {_grabber()?.PooledBytes / 1_048_576 ?? 0} MB, gdi {GuiResources(0)}, user {GuiResources(1)}, gc pause {pause.TotalMilliseconds:F0} ms");
                DebugHooks.MemoryMark("after reclaim");   // only when TONESNIP_MEMPROBE is set
            }
            catch (Exception e) { _log.Warn("reclaim: " + e.Message); }
            finally { Volatile.Write(ref _reclaiming, 0); }
        });
    }
}
