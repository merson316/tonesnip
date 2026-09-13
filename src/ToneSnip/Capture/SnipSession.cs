using System.Diagnostics;
using ToneSnip.App.Output;
using ToneSnip.Core.Annotate;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Config;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Geometry;
using ToneSnip.Windows.Interop;

namespace ToneSnip.App.Capture;

/// <summary>Runs one snip from trigger to output on the UI thread.</summary>
public sealed class SnipSession(FrameGrabber grabber, OutputPipeline output, Func<SnipSettings> settings, ILog log, Func<IntRect> primaryMonitor)
{
    /// <summary>When the last snip started, as a monotonic Stopwatch timestamp (the wall clock can step back after a
    /// resume). Zero means no recent start.</summary>
    private long _last;
    /// <summary>A second snip this soon after the last one started is taken for a double press.</summary>
    private static readonly TimeSpan Rearm = TimeSpan.FromMilliseconds(700);
    private CancellationTokenSource? _countdown;
    /// <summary>
    /// A snip is in flight. Written only on the UI thread by <see cref="Run"/>; read from background threads (App's
    /// frame release and reclaim, the memory harness) to avoid a blocking GC during a snip, hence <c>volatile</c>.
    /// </summary>
    private volatile bool _busy;
    /// <summary>When the snip in flight began, for the refusal line in <see cref="Run"/>. UI thread only.</summary>
    private long _started;
    public bool Busy => _busy;
    public bool CountingDown => _countdown != null;
    public void CancelCountdown() => _countdown?.Cancel();
    public event Action<string>? Failed;
    /// <summary>The snip is over, however it ended, and the output pipeline has run. Raised once per
    /// <see cref="Run"/>, including across a delay restart.</summary>
    public event Action? Idle;
    /// <summary>Hides the app's own transient windows (toast card, Recent flyout) before capture so they are not in the
    /// snip; returns whether any were hidden.</summary>
    public Func<bool>? HideOwnWindows { get; set; }

    /// <summary>Holds <see cref="Busy"/> for the whole run, including a delay restart that re-enters
    /// <see cref="RunCore"/>, so a hotkey during the countdown cannot start a concurrent snip.</summary>
    public async Task Run(SnipMode mode, int delaySeconds)
    {
        // Logged so a session that never ended is distinguishable from a dead hotkey.
        if (_busy) { log.Info($"{mode}: refused, the snip started {Stopwatch.GetElapsedTime(_started).TotalSeconds:F0} s ago has not finished"); return; }
        if (_last != 0 && Stopwatch.GetElapsedTime(_last) is var since && since < Rearm)
        {
            log.Info($"{mode}: refused, {since.TotalMilliseconds:F0} ms after the last snip started");
            return;
        }
        _busy = true;
        _started = Stopwatch.GetTimestamp();
        try { await RunCore(mode, delaySeconds); }
        finally { _busy = false; Idle?.Invoke(); }
    }

    private async Task RunCore(SnipMode mode, int delaySeconds)
    {
        _last = Stopwatch.GetTimestamp();
        var sw = Stopwatch.StartNew();
        try
        {
            // Hidden before the countdown too, not just before the grab.
            if (HideOwnWindows?.Invoke() == true && delaySeconds <= 0) await Task.Run(() => { Dwm.Flush(); Dwm.Flush(); });
            if (delaySeconds > 0) await Delay(delaySeconds);
#if TONESNIP_HARNESS
            MemoryProbe.Mark("before grab");
#endif
            List<CapturedOutput> outputs = await Task.Run(grabber.GrabAll);
            long tGrab = sw.ElapsedMilliseconds;
#if TONESNIP_HARNESS
            MemoryProbe.Mark("after grab");
#endif
            IntRect desktop = outputs.Aggregate(IntRect.Empty, (r, o) => r.Union(o.Info.Bounds));
            IntRect region;
            IReadOnlyList<(int X, int Y)>? freeform = null;
            AnnotationDoc? doc = null; float exposure = 1f; bool retonemap = false;
            switch (mode)
            {
                case SnipMode.FullScreenAll: region = desktop; break;
                case SnipMode.ActiveWindow:
                    region = (WindowFinder.ActiveForSnip()?.Bounds ?? desktop).Clamp(desktop);
                    if (region.IsEmpty) region = desktop;
                    break;
                default:
                    (region, freeform, doc, exposure, bool restarted, retonemap) = await SelectInteractively(mode, outputs, desktop, sw);
                    if (region.IsEmpty)
                    {
                        log.Info(restarted ? $"{mode}: restarted with a delay" : $"{mode}: cancelled after {sw.ElapsedMilliseconds} ms");
                        return;
                    }
                    break;
            }
#if TONESNIP_HARNESS
            MemoryProbe.Mark("after overlay");
#endif
            CaptureResult result = await Task.Run(() => CaptureResult.Build(outputs, region, freeform, grabber, settings(), doc, exposure, retonemap));
#if TONESNIP_HARNESS
            MemoryProbe.Mark("after build");
#endif
            log.Info($"{mode}: grab {tGrab} ms, region {region}, hdr={result.AnyHdr}, total {sw.ElapsedMilliseconds} ms, shapes={doc?.Shapes.Count ?? 0}, exposure={exposure:F2}");
            await output.RunAsync(result);
        }
        catch (OperationCanceledException) { log.Info($"{mode}: countdown cancelled"); }
        catch (Exception e)
        {
            log.Error($"{mode}: {e}");
            Failed?.Invoke(e.Message);
        }
    }

    /// <summary>Puts the frozen-frame overlay up and waits for the selection; <paramref name="sw"/> times the first paint.</summary>
    private async Task<(IntRect, IReadOnlyList<(int X, int Y)>?, AnnotationDoc?, float, bool, bool)> SelectInteractively(SnipMode mode, List<CapturedOutput> outputs, IntRect desktop, Stopwatch sw)
    {
        var overlay = new Overlay.OverlaySession(outputs, grabber, desktop, mode, settings(), log);
        overlay.FirstPaint = () => App.Current.Timing($"{mode}: overlay shown", sw);
        Overlay.OverlayOutcome outcome = await overlay.Show();
        if (outcome.RestartWithDelay >= 0)
        {
            // Awaited within the same Run, so Busy stays true across the countdown and the restarted snip.
            _last = 0;
            await RunCore(overlay.Mode, outcome.RestartWithDelay);
            return (IntRect.Empty, null, null, 1f, true, false);
        }
        return (outcome.Region, outcome.Freeform, outcome.Doc, outcome.Exposure, false, outcome.ExposurePreviewed);
    }

    /// <summary>
    /// Counts down on the primary monitor; Escape (through the hook) cancels. Returns once the countdown is off the screen.
    /// <para>The window is hidden and two DWM compositions are waited out before the grab; closing it immediately can
    /// leave it, or a black square, in the captured frame.</para>
    /// </summary>
    private async Task Delay(int seconds)
    {
        var cts = new CancellationTokenSource();
        _countdown = cts;
        var w = new Overlay.CountdownWindow(primaryMonitor());
        try
        {
            for (int s = seconds; s > 0; s--) { w.Set(s); await Task.Delay(1000, cts.Token); }
            w.AppWindow.Hide();
            await Task.Run(() => { Dwm.Flush(); Dwm.Flush(); });
        }
        finally { w.Close(); _countdown = null; cts.Dispose(); }
    }
}
