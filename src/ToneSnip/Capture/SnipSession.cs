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
        // A new grab would only queue behind the abandoned one on the grabber's lock, busy again with nothing on screen.
        if (_abandonedGrab is { IsCompleted: false })
        {
            log.Warn($"{mode}: refused, the grab abandoned {Stopwatch.GetElapsedTime(_abandonedAt).TotalSeconds:F0} s ago has still not returned");
            // Said on screen too, or the hotkey looks dead; throttled so holding or mashing it does not stack balloons.
            if (_stuckNoticeAt == 0 || Stopwatch.GetElapsedTime(_stuckNoticeAt) >= StuckNoticeInterval)
            {
                _stuckNoticeAt = Stopwatch.GetTimestamp();
                Failed?.Invoke("The last snip is still stuck; try again in a moment");
            }
            return;
        }
        _abandonedGrab = null;
        _stuckNoticeAt = 0;
        if (_last != 0 && Stopwatch.GetElapsedTime(_last) is var since && since < Rearm)
        {
            log.Info($"{mode}: refused, {since.TotalMilliseconds:F0} ms after the last snip started");
            return;
        }
        _busy = true;
        _started = Stopwatch.GetTimestamp();
        // One Info line per stage (started, grabbed, overlay shown, outcome), so the production log shows how far a
        // snip got when a hotkey seems to do nothing.
        log.Info(delaySeconds > 0 ? $"{mode}: started, {delaySeconds} s delay" : $"{mode}: started");
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
            if (HideOwnWindows?.Invoke() == true && delaySeconds <= 0) await WaitForComposition(mode, "after hiding the card and flyout");
            if (delaySeconds > 0) await Delay(mode, delaySeconds);
#if TONESNIP_HARNESS
            MemoryProbe.Mark("before grab");
#endif
            long grabStarted = Stopwatch.GetTimestamp();
            List<CapturedOutput>? outputs = await Grab(mode);
            if (outputs == null) return;
            long tGrab = sw.ElapsedMilliseconds;
            log.Info($"{mode}: grabbed {outputs.Count} monitor(s) in {Stopwatch.GetElapsedTime(grabStarted).TotalMilliseconds:F0} ms");
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
                    (region, freeform, doc, exposure, bool restarted, retonemap) = await SelectInteractively(mode, outputs, desktop);
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
            SnipSettings s = settings();
            CaptureResult result = await Task.Run(() => CaptureResult.Build(outputs, region, freeform, grabber, s, doc, exposure, retonemap, keepCrops: output.UsesCrops(s)));
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

    /// <summary>A grab still running after this long is logged, so a stall shows up in the production log while it lasts.</summary>
    private static readonly TimeSpan GrabWarning = TimeSpan.FromSeconds(5);
    /// <summary>
    /// A grab still running after this long is abandoned so that <see cref="Busy"/> clears. Every wait inside a grab is
    /// bounded, but the packaged build's first snip may wait up to a minute for the borderless-capture prompt to be
    /// answered, so this sits well beyond that rather than at <see cref="GrabWarning"/>.
    /// </summary>
    private static readonly TimeSpan GrabGiveUp = TimeSpan.FromSeconds(90);
    /// <summary>How long the overlay may take from being shown to its first paint before the snip is ended. It
    /// normally paints within a few hundred milliseconds.</summary>
    private static readonly TimeSpan PaintBudget = TimeSpan.FromSeconds(5);
    /// <summary>What an overlay that has not painted yet is doing, for the watchdog's line. Written on the UI thread,
    /// read on the watchdog's pool thread, which is the one that can still log if the UI thread is stuck.</summary>
    private volatile string? _stage;
    /// <summary>A grab given up on after <see cref="GrabGiveUp"/> that has not returned yet. It still holds the grabber,
    /// so <see cref="Run"/> refuses until it ends. UI thread only.</summary>
    private Task? _abandonedGrab;
    /// <summary>When <see cref="_abandonedGrab"/> was given up on, for the refusal line. UI thread only.</summary>
    private long _abandonedAt;
    /// <summary>When a refusal behind <see cref="_abandonedGrab"/> last showed its balloon (zero: not since the grab was
    /// abandoned). UI thread only.</summary>
    private long _stuckNoticeAt;
    /// <summary>At most one "still stuck" balloon this often, however often the hotkey is pressed.</summary>
    private static readonly TimeSpan StuckNoticeInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Grabs every monitor off the UI thread, logging a grab that runs past <see cref="GrabWarning"/>. Returns null
    /// when it runs past <see cref="GrabGiveUp"/>: the snip is then abandoned, the late grab logs how it ended, and
    /// <see cref="Run"/> refuses new snips until it has.
    /// </summary>
    private async Task<List<CapturedOutput>?> Grab(SnipMode mode)
    {
        long started = Stopwatch.GetTimestamp();
        Task<List<CapturedOutput>> grabbing = Task.Run(grabber.GrabAll);
        // The timeouts are caught unfiltered and the task checked inside: a grab finishing between the timer and an
        // exception filter would otherwise let the TimeoutException escape and fail a snip that worked. A grab that
        // ends in a TimeoutException of its own is rethrown by the await.
        try { return await grabbing.WaitAsync(GrabWarning); }
        catch (TimeoutException)
        {
            if (grabbing.IsCompleted) return await grabbing;
            log.Warn($"{mode}: stuck in the grab, which has not returned {GrabWarning.TotalSeconds:F0} s after it started");
        }
        try { return await grabbing.WaitAsync(GrabGiveUp - GrabWarning); }
        catch (TimeoutException)
        {
            if (grabbing.IsCompleted) return await grabbing;
            log.Warn($"{mode}: the grab has not returned after {GrabGiveUp.TotalSeconds:F0} s; the snip is abandoned, and snips are refused until the grab returns");
            _abandonedGrab = grabbing;
            _abandonedAt = Stopwatch.GetTimestamp();
            _ = grabbing.ContinueWith(t => log.Warn($"{mode}: the abandoned grab {(t.IsFaulted ? "failed: " + t.Exception?.InnerException?.Message : "returned")} after {Stopwatch.GetElapsedTime(started).TotalSeconds:F0} s"),
                                      TaskScheduler.Default);
            // The snip ends with nothing on screen, so say why rather than fail silently.
            Failed?.Invoke("Windows did not hand over the screen image; nothing was captured");
            return null;
        }
    }

    /// <summary>
    /// Puts the frozen-frame overlay up and waits for the selection. A watchdog ends the snip if the overlay has not
    /// painted within <see cref="PaintBudget"/>: frozen windows that never draw would otherwise hold <see cref="Busy"/>
    /// with nothing on screen to dismiss. Its line is logged from a pool thread before it asks the UI thread to end the
    /// snip, so it is written even if the UI thread is the thing that is stuck.
    /// <para>That watchdog is armed only once <see cref="Overlay.OverlaySession.Show"/> returns, so a Show that never
    /// returns is covered by a second, log-only timer started before it: it cannot end the snip (that needs the UI
    /// thread Show is holding), but the log then says where the snip stopped.</para>
    /// </summary>
    private async Task<(IntRect, IReadOnlyList<(int X, int Y)>?, AnnotationDoc?, float, bool, bool)> SelectInteractively(SnipMode mode, List<CapturedOutput> outputs, IntRect desktop)
    {
        long built = Stopwatch.GetTimestamp();
        bool painted = false;   // UI thread only
        var watchdog = new CancellationTokenSource();
        var overlay = new Overlay.OverlaySession(outputs, grabber, desktop, mode, settings(), log);
        overlay.FirstPaint = () =>
        {
            painted = true;
            watchdog.Cancel();
            _stage = null;
            log.Info($"{mode}: overlay shown in {Stopwatch.GetElapsedTime(built).TotalMilliseconds:F0} ms");
        };
        Overlay.OverlayOutcome outcome;
        try
        {
            _stage = "building the overlay windows";
            var showing = new CancellationTokenSource();
            _ = Task.Delay(PaintBudget, showing.Token).ContinueWith(t =>
            {
                // Show paints the frames before it builds the toolbar, and the first paint clears the stage.
                if (!t.IsCanceled && !showing.IsCancellationRequested) log.Warn($"{mode}: stuck {_stage ?? "building the overlay's toolbar"} {PaintBudget.TotalSeconds:F0} s after the overlay was started");
            }, TaskScheduler.Default);
            Task<Overlay.OverlayOutcome> selecting;
            try { selecting = overlay.Show(); }
            finally { showing.Cancel(); }
            // Timed from here, not from before Show: Show runs synchronously on this thread, so a slow one would let the
            // watchdog's queued check run ahead of the first WM_PAINT and end an overlay that was about to draw.
            if (!painted)
            {
                _stage = "waiting for the overlay's first paint";
                _ = Task.Delay(PaintBudget, watchdog.Token).ContinueWith(t =>
                {
                    if (t.IsCanceled) return;
                    log.Warn($"{mode}: stuck {_stage ?? "in the overlay"} {PaintBudget.TotalSeconds:F0} s after it was shown; ending the snip unless it paints now");
                    App.Current.RunOnUi(() =>
                    {
                        if (painted) return;
                        // A paint still queued behind other messages is not a stuck overlay: paint it now and look again.
                        overlay.PaintNow();
                        if (painted) return;
                        overlay.Finish(Overlay.OverlayOutcome.Cancelled);
                        // Ended as a cancel, so the snip would otherwise just vanish; say it failed.
                        Failed?.Invoke("The snip screen did not appear; nothing was captured");
                    });
                }, TaskScheduler.Default);
            }
            outcome = await selecting;
        }
        finally { watchdog.Cancel(); _stage = null; }
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
    /// Waits out two DWM compositions so a window just hidden is off the screen before the grab, but no longer than
    /// <see cref="Dwm.CompositionBudget"/>: unbounded, a composition that never came left the snip busy and every later
    /// hotkey refused.
    /// </summary>
    private async Task WaitForComposition(SnipMode mode, string when)
    {
        Task flush = Dwm.FlushTwice();
        try { await flush.WaitAsync(Dwm.CompositionBudget); }
        catch (TimeoutException)
        {
            // Unfiltered for the same reason as in Grab; a flush that finished just in time needs no line.
            if (!flush.IsCompleted) log.Warn($"{mode}: DWM did not compose within {Dwm.CompositionBudget.TotalMilliseconds:F0} ms {when}; grabbing anyway");
        }
    }

    /// <summary>
    /// Counts down on the primary monitor; Escape (through the hook) cancels. Returns once the countdown is off the screen.
    /// <para>The window is hidden and two DWM compositions are waited out (<see cref="WaitForComposition"/>) before the
    /// grab; closing it immediately can leave it, or a black square, in the captured frame.</para>
    /// </summary>
    private async Task Delay(SnipMode mode, int seconds)
    {
        var cts = new CancellationTokenSource();
        _countdown = cts;
        var w = new Overlay.CountdownWindow(primaryMonitor());
        try
        {
            for (int s = seconds; s > 0; s--) { w.Set(s); await Task.Delay(1000, cts.Token); }
            w.AppWindow.Hide();
            await WaitForComposition(mode, "after the countdown");
        }
        finally { w.Close(); _countdown = null; cts.Dispose(); }
    }
}
