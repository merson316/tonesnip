using ToneSnip.App.Output;
using ToneSnip.App.Record;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Output;
using ToneSnip.Core.Record;
using ToneSnip.Windows.Record;

namespace ToneSnip.App;

/// <summary>Screen recording: the snip screen picks the area, and <see cref="RecordingSession"/> records it.</summary>
public partial class App
{
    /// <summary>The recording running, from its countdown to its file. One at a time. UI thread.</summary>
    private RecordingSession? _recording;

    public bool IsRecording => _recording != null;

    /// <summary>
    /// The record hotkey, the tray's Record item, Recent's record button and <c>--record</c>: opens the snip screen
    /// armed to record, whose selection becomes the area, or stops (and saves) the recording running.
    /// <paramref name="fromMenu"/> waits for a menu or the flyout to close first, as <see cref="Snip"/> does.
    /// </summary>
    public void ToggleRecording(bool fromMenu = false)
    {
        if (_recording is { } running) { running.Stop(keep: true); return; }
        if (fromMenu) Snip(SnipMode.Rectangle, 0, SnipAction.Record);
        else StartSnip(SnipMode.Rectangle, 0, SnipAction.Record);
    }

    /// <summary>A selection made to be recorded (<see cref="Capture.SnipSession.Record"/>): the area is fixed to one
    /// monitor and the file named, and the recording starts its countdown. UI thread.</summary>
    private void BeginRecording(IntRect selection, IReadOnlyList<OutputInfo> monitors)
    {
        string glyph = (string)Resources["GlyphRecording"];
        if (_recording != null)
        {
            Log.Info("record: refused, a recording is already running");
            Toasts.ShowNotice("Already recording", "Stop the recording that is running to start another", glyph);
            return;
        }
        if (RecordingArea.For(selection, monitors.Select(m => m.Bounds).ToList()) is not var (monitor, area))
        {
            Toasts.ShowNotice("Nothing to record", "The selection is not on a monitor", glyph);
            return;
        }
        OutputInfo output = monitors.First(m => m.Bounds == monitor);
        string? folder = Settings.Record.SafeFolder(AppPaths.Videos);
        if (folder == null)
        {
            Log.Warn("record refused: " + Core.Config.PathGuard.Describe(Settings.Record.ResolvedFolder(AppPaths.Videos)));
            Toasts.ShowNotice("Recording not started", "The recordings folder is not a usable folder; choose another in Settings", glyph);
            return;
        }
        try { Directory.CreateDirectory(folder); }
        catch (Exception e)
        {
            Log.Warn($"record: the folder '{folder}' could not be made: {e.Message}");
            Toasts.ShowNotice("Recording not started", "The recordings folder could not be created", glyph);
            return;
        }
        string path = FileNaming.Resolve(folder, FileNaming.BuildRecording(DateTime.Now), File.Exists);
        // The configured exposure throughout: auto exposure is chosen per snip, and would make a video flicker.
        var request = new RecordingRequest(monitor, area, output.Hdr, Grabber.CurveFor(output), Settings.Record.FrameRate, Settings.Record.Cursor, path);
        var session = new RecordingSession(request, Log, Theme.ThemeManager.OverlayAccentArgb);
        _recording = session;
        _tray?.SetTooltip("ToneSnip (recording)");
        _ = RunRecording(session, glyph);
    }

    private async Task RunRecording(RecordingSession session, string glyph)
    {
        RecordingResult? result = null;
        string? failure;
        try { (result, failure) = await session.RunAsync(); }
        catch (Exception e)
        {
            Log.Error("record: " + e);
            failure = e.Message;
        }
        finally
        {
            if (_recording == session) _recording = null;
            _tray?.SetTooltip(Paused ? "ToneSnip (hotkeys paused)" : "ToneSnip");
            ReclaimMemory("memory after recording");
        }
        if (result == null)
        {
            if (failure != null) Toasts.ShowNotice("Recording failed", failure, glyph);
            return;
        }
        if (Settings.CopyToClipboard)
        {
            _ = ClipboardWriter.SetFileQueued(result.Path, Log).ContinueWith(
                t => Log.Warn("record: the file was not copied: " + t.Exception!.GetBaseException().Message),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        string detail = $"{Path.GetFileName(result.Path)}  ·  {RecordingClock.Format(result.Duration)}";
        // A recording that stopped by itself is still saved; the card says why it ended.
        if (failure != null) Toasts.ShowNotice("Recording stopped", $"{failure}. What was recorded is saved as {Path.GetFileName(result.Path)}", glyph);
        else if (Settings.ShowToast) Toasts.ShowRecording(result.Path, detail, glyph);
    }
}
