using ToneSnip.App.Interop;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Windows.Overlay;
using ToneSnip.Windows.Record;
using Microsoft.UI.Dispatching;

namespace ToneSnip.App.Record;

/// <summary>
/// One recording from its countdown to its file, on the UI thread. The outline around the area and the bar beside it
/// go up at once; the recorder is opened during a three-second countdown, which hides the encoder starting up, and
/// records from the countdown's end until Stop or Discard, or until it stops by itself. Everything on screen goes when
/// it ends.
/// </summary>
public sealed class RecordingSession(RecordingRequest request, ILog log, uint outlineArgb)
{
    public const int CountdownSeconds = 3;
    /// <summary>How often the bar's time is brought up to date.</summary>
    private static readonly TimeSpan TickEvery = TimeSpan.FromMilliseconds(250);

    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ScreenRecorder? _recorder;
    /// <summary>The recorder being opened during the countdown, for an exit that comes before it is taken.</summary>
    private Task<ScreenRecorder>? _opening;
    private RecordBar? _bar;
    /// <summary>A field: an unreferenced DispatcherQueueTimer can be collected before it ticks.</summary>
    private DispatcherQueueTimer? _tick;
    private bool _stopping;

    public RecordingRequest Request => request;

#if TONESNIP_HARNESS
    /// <summary>The bar, while it is up, for the harness to photograph.</summary>
    internal RecordBar? BarForHarness => _bar;
#endif

    /// <summary>
    /// Runs the recording and returns its file, or null when it was discarded, cancelled during the countdown, or
    /// captured nothing; and why it ended, when it ended by itself or could not start.
    /// </summary>
    public async Task<(RecordingResult? Result, string? Failure)> RunAsync()
    {
        Task<ScreenRecorder> opening = _opening = Task.Run(() => ScreenRecorder.Open(request, log));
        RecordingFrame? frame = null;
        bool handedOver = false;
        try
        {
            int ring = Math.Max(2, (int)Math.Round(2 * Native.ScaleAt(request.Monitor)));
            frame = new RecordingFrame(request.Area, request.Monitor, outlineArgb & 0xFFFFFF, ring, log);
            _bar = new RecordBar(this, request.Area, request.Monitor);
            for (int s = CountdownSeconds; s > 0 && !_stopping; s--)
            {
                _bar.ShowCountdown(s);
                await Task.WhenAny(Task.Delay(1000), _stopped.Task);
            }
            ScreenRecorder recorder;
            try { recorder = await opening; }
            catch (Exception e)
            {
                log.Warn("record: not started: " + e);
                // Cancelled meanwhile: the user does not need to hear why it could not have started.
                return (null, _stopping ? null : e.Message);
            }
            handedOver = true;
            if (_stopping)
            {
                await recorder.Stop(keep: false);
                log.Info("record: cancelled during the countdown");
                return (null, null);
            }
            _recorder = recorder;
            recorder.Begin();
            frame.Raise();
            Tick();
            _tick = App.Current.Ui.CreateTimer();
            _tick.Interval = TickEvery;
            _tick.Tick += OnTick;
            _tick.Start();
            RecordingResult? result = await recorder.Completion;
            return (result, recorder.Failure);
        }
        finally
        {
            if (_tick is { } timer) { timer.Stop(); timer.Tick -= OnTick; _tick = null; }
            try { _bar?.Close(); } catch (Exception e) { log.Debug("record: bar close: " + e.Message); }
            _bar = null;
            frame?.Dispose();
            // Something here failed before the recorder was taken: it is still opening, or open, and nothing else
            // will ever stop it.
            if (!handedOver) _ = opening.ContinueWith(t => { if (t.IsCompletedSuccessfully) _ = t.Result.Stop(keep: false); }, TaskScheduler.Default);
        }
    }

    private void OnTick(DispatcherQueueTimer timer, object args) => Tick();

    private void Tick()
    {
        if (_recorder is { } r) _bar?.ShowRecording(r.Elapsed, r.Paused);
    }

    /// <summary>Stop (kept) or Discard, from the bar, the record hotkey or the tray. During the countdown either one
    /// cancels: nothing has been recorded. The first call decides.</summary>
    public void Stop(bool keep)
    {
        if (_stopping) return;
        _stopping = true;
        _stopped.TrySetResult();
        if (_recorder is { } r)
        {
            _bar?.Announce(keep ? "Saving the recording" : "Recording discarded");
            _ = r.Stop(keep);
        }
    }

    /// <summary>Stops and saves the recording and waits, up to <paramref name="budget"/>, for its file to be finished:
    /// for an exit, which would otherwise end the process with the file's index unwritten. Any wait is on the
    /// recorder's own thread, not this one.</summary>
    public void SaveBeforeExit(TimeSpan budget)
    {
        ScreenRecorder? r = _recorder;
        Stop(keep: true);
        if (r != null)
        {
            if (!r.Completion.Wait(budget)) log.Warn($"record: the file was not finished within {budget.TotalSeconds:F0} s of the exit");
            return;
        }
        // Still counting down: nothing was recorded, but the file the recorder opened would be left behind empty.
        try
        {
            if (_opening is { } opening && opening.Wait(budget)) opening.Result.Stop(keep: false).Wait(budget);
        }
        catch (AggregateException e) { log.Debug("record: the countdown's recorder was not opened: " + e.InnerException?.Message); }
    }

    public void TogglePause()
    {
        if (_stopping || _recorder is not { } r) return;
        if (r.Paused) r.Resume(); else r.Pause();
        Tick();
    }
}
