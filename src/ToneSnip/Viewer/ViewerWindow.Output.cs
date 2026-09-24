using System.Diagnostics;
using ToneSnip.App.Capture;
using ToneSnip.App.Output;
using ToneSnip.App.Theme;
using ToneSnip.Core.Annotate;
using ToneSnip.Core.Config;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Imaging;
using ToneSnip.Windows.Imaging;
using Shell = ToneSnip.Windows.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace ToneSnip.App.Viewer;

/// <summary>The editor's output side: Save, Save as and Copy behind one gate, the SDR encode and the HDR sidecar off
/// the UI thread, and the status notes that report them.</summary>
public sealed partial class ViewerWindow
{
    /// <summary>How long "HDR copy written" stays up, and the fade that takes it away.</summary>
    private static readonly TimeSpan HdrDoneFor = TimeSpan.FromSeconds(2);
    private const double HdrDoneFadeMs = 300;

    /// <summary>
    /// Serialises Save, Save as, Copy and the close prompt's save, which each render and write off the UI thread; two
    /// at once could both pass the <see cref="WritesPending"/> check and write the same file together.
    /// </summary>
    private readonly SemaphoreSlim _outputGate = new(1, 1);
    /// <summary>The HDR sidecar write in flight, if any. Save, the exposure loop and the close all wait on it.</summary>
    private Task? _hdrWrite;
    /// <summary>The SDR encode and file write in flight, if any. Part of <see cref="WritesPending"/>, so a second Save
    /// cannot write the same file and the close waits for the bytes to land.</summary>
    private Task? _encode;
    private int _hdrGeneration;
    /// <summary>Outcome of the last finished sidecar write. The close checks it: a Save onto an HDR-only target
    /// (.jxr/.hdr.png/.hdr.jpg) has nothing else on disk to show for itself, so a failed write must not close.</summary>
    private bool _hdrWriteOk = true;
    /// <summary>The "HDR copy written" note's dwell timer and its fade, both cancelled when the window closes.</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _hdrDoneTimer;
    /// <summary>The dwell timer's handler, kept so it can be detached: a stopped DispatcherQueueTimer still holds its
    /// Tick, and the Tick holds this window.</summary>
    private global::Windows.Foundation.TypedEventHandler<Microsoft.UI.Dispatching.DispatcherQueueTimer, object>? _hdrDoneTick;
    private Storyboard? _hdrDoneFade;

    /// <summary>
    /// Writes a snapshot of the editor's document (already translated back to the desktop frame) into
    /// <see cref="_result"/>, which the HDR sidecar is built from. <see cref="EditorSurface.Load"/> only copies out of
    /// the result, so without this the sidecar would miss editor changes, including redactions. Call on the UI thread
    /// before any call into <see cref="HdrOutput"/>.
    /// </summary>
    private void SyncResultFromSession(Rendered rendered)
    {
        _result.Doc = rendered.Doc;
        _result.Exposure = rendered.Exposure;
    }

    /// <summary>What a save's pixels were rendered from, taken together with <see cref="Output"/>. The editor stays live
    /// during the encode, so marking saved and building the HDR copy must use this snapshot, not the document's later
    /// state, or an edit made mid-write would be marked saved without being in the file.</summary>
    private readonly record struct Rendered(long Revision, float Exposure, AnnotationDoc Doc);

    private Rendered Snapshot() => new(Surface.Session.Doc.Revision, Surface.Session.Doc.Exposure,
                                       Surface.Session.Doc.Translated(_result.Region.Left, _result.Region.Top));

    // ----- output -----

    /// <summary>The pixels the viewer would hand out: the current crop with the shapes drawn in, no chrome.</summary>
    private BgraImage Output() => Surface.RenderForOutput();

    private void OnCopy(object sender, RoutedEventArgs e) => Copy();
    private void OnSave(object sender, RoutedEventArgs e) => _ = SaveWhenIdle(saveAs: false);
    private void OnSaveAs(object sender, RoutedEventArgs e) => _ = SaveWhenIdle(saveAs: true);

    /// <summary>
    /// Defers saving until the exposure loop is idle, since a pass rewrites the image in place on a worker thread. The
    /// await resumes on the dispatcher, so the save still runs on the UI thread.
    /// </summary>
    private Task<bool> SaveWhenIdle(bool saveAs) => OneOutputAtATime(async () =>
    {
        if (!await ExposureIdle()) return false;
        return saveAs ? await SaveAs() : await Save();
    });

    private async Task<T> OneOutputAtATime<T>(Func<Task<T>> work)
    {
        await _outputGate.WaitAsync();
        try { return await work(); }
        finally { _outputGate.Release(); }
    }

    /// <summary>Waits out an exposure pass in flight. False when waiting failed (already logged).</summary>
    private async Task<bool> ExposureIdle()
    {
        if (Surface.ExposureIdle.IsCompleted) return true;
        try { await Surface.ExposureIdle; return true; }
        catch (Exception ex) { App.Current.Log.Warn("viewer exposure: " + ex.Message); return false; }
    }

    private void Copy() => _ = CopyAsync();

    /// <summary>
    /// Copies the current pixels. The render stays on the UI thread (it reads the surface's buffers); the PNG encode and
    /// clipboard write run on the pool over the private copy <see cref="Output"/> produced.
    /// </summary>
    private Task CopyAsync() => OneOutputAtATime(async () => { await CopyCore(); return true; });

    private async Task CopyCore()
    {
        try
        {
            // Wait before rendering: _encode tracks one encode at a time, so starting while a Save is still writing
            // would replace its entry and let others proceed past an unfinished write.
            await WritesPending();
            // An exposure pass rewrites the picture in place on a worker thread.
            if (!await ExposureIdle() || _closed) return;
            BgraImage img = Output();
            // Both on the pool: the clipboard needs no apartment, and the DIB copy costs as much as the PNG.
            // The encode on the pool; the write queued behind any other clipboard write, on the pool too.
            byte[] png = await RunEncode(() => Bitmaps.EncodePng(img));
            await ClipboardWriter.SetQueued(img, png, App.Current.Log);
        }
        catch (Exception ex) { App.Current.Log.Warn("viewer copy: " + ex.Message); }
    }

    // ----- Copy text -----

    /// <summary>Ctrl+T arms Copy text, or puts it away.</summary>
    private void OnCopyTextAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SetSelectingText(!Surface.SelectingText);
        args.Handled = true;
    }

    /// <summary>Click, not Checked: <see cref="SetSelectingText"/> sets IsChecked itself and must not loop back.</summary>
    private void OnCopyTextToggle(object sender, RoutedEventArgs e) => SetSelectingText(CopyTextBtn.IsChecked == true);

    private void OnCopyAllText(object sender, RoutedEventArgs e) => _ = CopyTextAsync(null);

    private void OnCopyAllTextAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SetSelectingText(false, quiet: true);
        _ = CopyTextAsync(null);
        args.Handled = true;
    }

    /// <summary>
    /// Arms Copy text: the next drag on the picture marks the area to read, as the overlay's armed Copy text does, and
    /// the status strip says so until the drag or Escape. <paramref name="quiet"/> skips the "off" announcement when
    /// something else takes over.
    /// </summary>
    private void SetSelectingText(bool on, bool quiet = false)
    {
        if (on && Surface.Picking) SetPicking(false);   // one armed pointer mode at a time
        bool was = Surface.SelectingText;
        Surface.SelectingText = on;
        CopyTextBtn.IsChecked = on;
        if (on)
        {
            ShowNote("Copy text: drag over the text to copy it; Esc cancels", dwell: false);
            Surface.Focus(FocusState.Pointer);
        }
        else if (was && !quiet) ShowNote("Copy text off", dwell: true);
        else if (was) HideNote();
    }

    /// <summary>The drag finished: read the text in that area (view pixels) and put Copy text away.</summary>
    private void OnTextAreaSelected(Core.Geometry.IntRect area)
    {
        SetSelectingText(false, quiet: true);
        _ = CopyTextAsync(area);
    }

    /// <summary>
    /// "Copy text": the text in <paramref name="area"/> of what Copy would copy (the crop, with the shapes drawn in),
    /// or in all of it when null, recognised off the UI thread on a private render, so editing can go on meanwhile.
    /// Rendered behind the output gate, as Copy is, so it never reads a picture an exposure pass is rewriting;
    /// recognised after it, so a Save or Copy does not wait out the recognition, which reads only the private render.
    /// </summary>
    private async Task CopyTextAsync(Core.Geometry.IntRect? area)
    {
        BgraImage? img = await OneOutputAtATime(async () =>
        {
            if (!await ExposureIdle() || _closed) return null;
            BgraImage whole = Output();
            // The render is the whole view; the area was marked on the view, so it is in the same pixels. A crop
            // applied meanwhile can leave the area partly outside.
            if (area is not { } a) return whole;
            Core.Geometry.IntRect inside = a.Intersect(new Core.Geometry.IntRect(0, 0, whole.Width, whole.Height));
            return inside.IsEmpty ? null : whole.Crop(inside);
        });
        if (img != null) await App.Current.CopyTextAsync(img);
    }

    private void OnPin(object sender, RoutedEventArgs e) => _ = PinAsync();

    /// <summary>Pins what Copy would copy, as it is now: a private render, so later edits do not reach the pin.</summary>
    private Task PinAsync() => OneOutputAtATime(async () =>
    {
        if (!await ExposureIdle() || _closed) return false;
        BgraImage img = Output();
        var pinned = new CaptureResult { Image = img, Region = new Core.Geometry.IntRect(0, 0, img.Width, img.Height), AnyHdr = false, Crops = new(), TakenLocal = _result.TakenLocal };
        await App.Current.PinAsync(pinned);
        return true;
    });

    // ----- the HDR sidecar, off the UI thread -----

    /// <summary>The tonemapper setting as the status strip names it.</summary>
    private static string TonemapName(string name) => name switch { "hable" => "Hable", "aces" => "ACES", _ => "Desktop" };

    /// <summary>Completes when neither the HDR sidecar write nor the SDR encode is running off the UI thread. The
    /// exposure loop, a second save and the close all wait on it.</summary>
    private Task WritesPending()
    {
        Task? hdr = _hdrWrite, encode = _encode;
        if (hdr == null) return encode ?? Task.CompletedTask;
        return encode == null ? hdr : Task.WhenAll(hdr, encode);
    }

    /// <summary>
    /// Runs one encode and its file write on the thread pool, tracked by <see cref="WritesPending"/>.
    /// <paramref name="work"/> should use only the private image <see cref="Output"/> rendered, not the surface's
    /// buffers.
    /// </summary>
    private async Task<T> RunEncode<T>(Func<T> work)
    {
        Task<T> task = Task.Run(work);
        // WritesPending holds a continuation rather than the task, so waiters only learn that writing stopped and a
        // faulted encode does not re-throw into them. The caller below still sees the real exception.
        Task gate = task.ContinueWith(static _ => { }, TaskScheduler.Default);
        _encode = gate;
        try { return await task; }
        finally { if (ReferenceEquals(_encode, gate)) _encode = null; }
    }

    /// <summary>The same, for a write with no result to hand back (Save as's SDR branch).</summary>
    private Task RunEncode(Action work) => RunEncode<object?>(() => { work(); return null; });

    /// <summary>
    /// Starts the sidecar write on the thread pool from the rendered SDR pixels and the result snapshot
    /// <see cref="SyncResultFromSession"/> took. Everything else that reads the result waits on
    /// <see cref="WritesPending"/> first.
    /// </summary>
    /// <param name="after">Run on the UI thread once the write has finished, with its outcome.</param>
    private void StartHdrWrite(BgraImage img, string path, string format, Action<bool>? after = null)
        => _hdrWrite = RunHdrWrite(img, path, format, after);

    private async Task RunHdrWrite(BgraImage img, string path, string format, Action<bool>? after)
    {
        int generation = ++_hdrGeneration;
        if (!_closed) { ShowNote(HdrError, false); HideHdrDone(); ShowNote(HdrBusy, true); }
        var sw = Stopwatch.StartNew();
        CaptureResult result = _result;
        SnipSettings settings = App.Current.Settings;   // snapshot: a settings change mid-write must not split the file
        uint accent = _accent;
        ILog log = App.Current.Log;
        bool ok = false;
        try { ok = await Task.Run(() => HdrOutput.WriteTo(result, img, path, format, settings, accent, log)); }
        catch (Exception ex) { log.Warn("viewer hdr: " + ex.Message); }
        if (ok) App.Current.Timing("hdr saved", sw);
        else log.Info($"hdr copy failed after {sw.ElapsedMilliseconds} ms ({path})");
        try { after?.Invoke(ok); } catch (Exception ex) { log.Warn("viewer hdr follow-up: " + ex.Message); }
        if (generation != _hdrGeneration) return;   // a newer write already owns the field and the busy note
        _hdrWrite = null;
        _hdrWriteOk = ok;
        if (_closed) return;
        ShowNote(HdrBusy, false);
        ShowNote(HdrError, !ok);
        if (ok) ShowHdrDone();
    }

    /// <summary>"HDR copy written": 2 s in Success, then a 300 ms fade out (skipped when animations are off).</summary>
    private void ShowHdrDone()
    {
        HideHdrDone();
        HdrDone.Opacity = 1;
        ShowNote(HdrDone, true);
        Microsoft.UI.Dispatching.DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = HdrDoneFor;
        timer.IsRepeating = false;
        // Kept so it can be detached when it fires or when HideHdrDone runs early: a stopped timer still holds its
        // Tick, and the Tick closes over this window.
        global::Windows.Foundation.TypedEventHandler<Microsoft.UI.Dispatching.DispatcherQueueTimer, object>? tick = null;
        tick = (t, _) =>
        {
            t.Stop();
            t.Tick -= tick;
            if (!ReferenceEquals(_hdrDoneTimer, t) || _closed) return;
            _hdrDoneTimer = null;
            _hdrDoneTick = null;
            FadeHdrDoneOut();
        };
        timer.Tick += tick;
        _hdrDoneTick = tick;
        _hdrDoneTimer = timer;
        timer.Start();
    }

    private void FadeHdrDoneOut()
    {
        if (!ThemeManager.AnimationsEnabled) { ShowNote(HdrDone, false); return; }
        var anim = new DoubleAnimation { From = 1, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(HdrDoneFadeMs)) };
        Storyboard.SetTarget(anim, HdrDone);
        Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_hdrDoneFade, sb)) return;
            _hdrDoneFade = null;
            ShowNote(HdrDone, false);
            HdrDone.Opacity = 1;
        };
        _hdrDoneFade = sb;
        sb.Begin();
    }

    /// <summary>Takes the note down at once, whatever stage it is at; the next write starts the two-second dwell over.</summary>
    private void HideHdrDone()
    {
        if (_hdrDoneTimer is { } timer)
        {
            timer.Stop();
            if (_hdrDoneTick != null) timer.Tick -= _hdrDoneTick;
        }
        _hdrDoneTimer = null;
        _hdrDoneTick = null;
        _hdrDoneFade?.Stop();
        _hdrDoneFade = null;
        HdrDone.Opacity = 1;
        ShowNote(HdrDone, false);
    }

    /// <summary>Overwrites the file the snip was saved to, in its own format. False when it failed or was cancelled.</summary>
    private async Task<bool> Save()
    {
        if (_result.SavedPath == null) return await SaveAs();
        await WritesPending();   // a second Save while a write is still going out waits for it
        try
        {
            BgraImage img = Output();
            Rendered rendered = Snapshot();
            string path = _result.SavedPath;
            bool jpeg = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
            bool clip = App.Current.Settings.CopyToClipboard;
            int quality = App.Current.Settings.JpegQuality;
            // The encode runs off the UI thread; everything after this await is the UI-thread tail.
            byte[]? png = null;
            await RunEncode(() =>
            {
                png = !jpeg || clip ? Bitmaps.EncodePng(img) : null;   // a JPEG save with no clipboard needs no PNG at all
                File.WriteAllBytes(path, jpeg ? Bitmaps.EncodeJpeg(img, quality) : png!);
                if (clip) ClipboardWriter.Set(img, png!, App.Current.Log);
            });
            // The file is written, so the Save has succeeded even if the window has since closed; only the lines that
            // touch elements are guarded.
            _result.Output = img;   // "Open last snip" and Compact both read this
            // Closing the editor compacts the result to this PNG; kept, so the close does not encode the image again.
            if (png != null) _result.CachePng(img, png);
            bool hdrData = _result.Crops.Count > 0;
            // A sidecar belonging to another file (the original name, after a Save as) is forgotten, not overwritten.
            if (_result.HdrPath != null && !HdrOutput.OwnsSidecar(path, _result.HdrPath)) _result.HdrPath = null;
            string? hdrFormat = _result.HdrPath != null ? HdrOutput.FormatOf(_result.HdrPath) : App.Current.Settings.Hdr.File == "none" ? null : App.Current.Settings.Hdr.File;
            // An exposure pass may have started during the encode, and the sidecar reads what it rewrites; if the wait
            // fails, the SDR file stands without its sidecar.
            if (hdrData && hdrFormat != null && await ExposureIdle())
            {
                SyncResultFromSession(rendered);   // the document the SDR pixels were rendered from
                string hdrPath = _result.HdrPath ?? HdrOutput.PathFor(_result.SavedPath, hdrFormat);
                // The history row is rewritten after the sidecar write, which sets _result.HdrPath; otherwise a sidecar
                // written for the first time here would never reach the row, and a delete would orphan it.
                BgraImage written = img;
                StartHdrWrite(img, hdrPath, hdrFormat, _ => App.Current.History.Replace(_result, written));
            }
            else App.Current.History.Replace(_result, img);   // rewrite thumbnail + size for this entry
            SetHdrNote(!hdrData && _result.HdrPath != null);
            _savedExposure = rendered.Exposure;
            Surface.Session.Doc.MarkSaved(rendered.Revision);
            if (!_closed) UpdateState();
            App.Current.Log.Info("viewer saved " + _result.SavedPath);
            return true;
        }
        catch (Exception ex) { App.Current.Log.Warn("viewer save: " + ex.Message); return false; }
    }

    /// <summary>The "HDR copy not updated" note next to Modified in the status bar.</summary>
    private void SetHdrNote(bool skipped) => ShowNote(HdrNote, skipped);

    /// <summary>
    /// Shows or hides one of the status strip's notes, announcing it when it appears.
    /// <c>AutomationProperties.LiveSetting</c> alone announces nothing: the framework raises no event when Visibility
    /// changes, so the note is announced through <see cref="Controls.LiveRegion"/>. Hiding is not announced.
    /// </summary>
    private void ShowNote(TextBlock note, bool on)
    {
        if (_closed) return;
        Visibility want = on ? Visibility.Visible : Visibility.Collapsed;
        if (note.Visibility == want) return;
        note.Visibility = want;
        if (on) Controls.LiveRegion.Announce(note);
    }

#if TONESNIP_HARNESS
    /// <summary>Shows the real "HDR copy written" note and its dwell timer, so the leak test can exercise the timer
    /// without an HDR sidecar write.</summary>
    internal void ShowHdrDoneForHarness() => ShowHdrDone();

    /// <summary>
    /// Shows or hides one of the status notes through the same call the sidecar write uses, so UIA tests and
    /// screenshots can see it without writing a file.
    /// </summary>
    internal void ShowHarnessNote(string which, bool on) => ShowNote(which switch
    {
        "busy" => HdrBusy,
        "done" => HdrDone,
        "error" => HdrError,
        _ => HdrNote,
    }, on);

    /// <summary>
    /// Re-runs the title update over a stand-in path, as <see cref="SaveAs"/> does after a save, so the window title can
    /// be checked without writing a file.
    /// </summary>
    internal void RetitleForHarness(string? savedPath)
    {
        _result.SavedPath = savedPath;
        UpdateTitle();
    }
#endif

    /// <summary>
    /// The folder and name the dialog opens on. The picker has no arbitrary start folder, only
    /// <see cref="PickerLocationId"/> and <c>SuggestedSaveFile</c>, so the settings' save folder is reached through a
    /// placeholder file, removed by <see cref="DropPlaceholder"/> if still empty. Null falls back to Pictures.
    /// </summary>
    private static async Task<StorageFile?> SuggestedFile(string name)
    {
        try
        {
            string folder = App.Current.Settings.ResolvedSaveFolder(AppPaths.Pictures);
            Directory.CreateDirectory(folder);
            StorageFolder dir = await StorageFolder.GetFolderFromPathAsync(folder);
            return await dir.CreateFileAsync(name, CreationCollisionOption.OpenIfExists);   // never truncates an existing file
        }
        catch (Exception ex) { App.Current.Log.Warn("viewer save as folder: " + ex.Message); return null; }
    }

    /// <summary>Deletes the placeholder when nothing was written to it — the user cancelled, or saved under another
    /// name. A placeholder that opened an existing file is not empty and is left alone.</summary>
    private static async Task DropPlaceholder(StorageFile? placeholder, string? chosenPath)
    {
        if (placeholder == null) return;
        if (chosenPath != null && string.Equals(chosenPath, placeholder.Path, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            if ((await placeholder.GetBasicPropertiesAsync()).Size == 0) await placeholder.DeleteAsync(StorageDeleteOption.PermanentDelete);
        }
        catch (Exception ex) { App.Current.Log.Warn("viewer save as placeholder: " + ex.Message); }
    }

    private FileSavePicker BuildPicker(bool hdr, string name, StorageFile? suggested)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, SuggestedFileName = name };
        picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
        picker.FileTypeChoices.Add("JPEG image", new List<string> { ".jpg" });
        if (hdr)
        {
            picker.FileTypeChoices.Add("JPEG XR (HDR)", new List<string> { ".jxr" });
            picker.FileTypeChoices.Add("PNG (HDR, 16-bit)", new List<string> { ".hdr.png" });
            picker.FileTypeChoices.Add("JPEG with gain map (HDR)", new List<string> { ".hdr.jpg" });
        }
        if (suggested != null) picker.SuggestedSaveFile = suggested;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        return picker;
    }

    /// <summary>Shows the picker. <c>FileTypeChoices.Add</c> validates nothing, so if <c>PickSaveFileAsync</c> rejects
    /// the two-part HDR extensions, retry once with the SDR types only.</summary>
    private async Task<StorageFile?> Pick(bool hdr, string name, StorageFile? suggested)
    {
        try { return await BuildPicker(hdr, name, suggested).PickSaveFileAsync(); }
        catch (Exception ex)
        {
            if (!hdr) { App.Current.Log.Warn("viewer save as: " + ex.Message); return null; }
            App.Current.Log.Warn($"viewer save as: the HDR file types were rejected ({ex.Message}); retrying with PNG and JPEG only");
            try { return await BuildPicker(hdr: false, name, suggested).PickSaveFileAsync(); }
            catch (Exception retry) { App.Current.Log.Warn("viewer save as: " + retry.Message); return null; }
        }
    }

    private async Task<bool> SaveAs()
    {
        await WritesPending();
        bool hdrData = _result.Crops.Count > 0;
        string name = Path.GetFileName(_result.SavedPath ?? Core.Output.FileNaming.Build(_result.TakenLocal, "png"));
        StorageFile? placeholder = await SuggestedFile(name);
        SetHdrNote(false);   // clear any stale note from before this call; the SDR branch below restores it if it still applies
        StorageFile? file = await Pick(hdrData, name, placeholder);
        await DropPlaceholder(placeholder, file?.Path);
        if (file == null || _closed) return false;
        if (!await ExposureIdle()) return false;
        string path = file.Path;
        try
        {
            BgraImage img = Output();
            Rendered rendered = Snapshot();
            // An HDR-format target (.jxr / .hdr.png / .hdr.jpg) is a one-off export: no SavedPath change, no history
            // entry, and _result.HdrPath is restored afterwards. Checked before the extension test below, because
            // Path.GetExtension("x.hdr.png") is ".png".
            string? hdrFmt = HdrOutput.FormatOf(path);
            if (hdrFmt != null)
            {
                string? prevHdrPath = _result.HdrPath;
                SyncResultFromSession(rendered);   // the document the pixels were rendered from
                StartHdrWrite(img, path, hdrFmt, ok => { _result.HdrPath = prevHdrPath; if (ok) App.Current.Log.Info("viewer saved " + path); });
                // True means queued, not written; the close path reads _hdrWriteOk after awaiting WritesPending.
                return true;
            }
            // The picker only returns one of the offered extensions, so the name decides the format.
            string ext = Path.GetExtension(path);
            bool jpeg = ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
            int quality = App.Current.Settings.JpegQuality;
            // Off the UI thread, as in Save; the tail below runs on the UI thread.
            byte[]? png = null;
            await RunEncode(() =>
            {
                png = jpeg ? null : Bitmaps.EncodePng(img);
                File.WriteAllBytes(path, jpeg ? Bitmaps.EncodeJpeg(img, quality) : png!);
            });
            // The file is written, so the Save as has succeeded and its history row must be recorded even if the window
            // has closed; only the lines that touch elements are guarded.
            _result.SavedPath = path;
            if (!HdrOutput.OwnsSidecar(path, _result.HdrPath)) _result.HdrPath = null;   // the old name's sidecar is not this file's
            _result.Output = img;   // as in Save: the retained result must carry the pixels that were written
            if (png != null) _result.CachePng(img, png);   // as in Save: the close keeps this PNG rather than encoding again
            App.Current.History.AddSavedCopy(_result, path, img);
            SetHdrNote(!hdrData && _result.HdrPath != null);
            _savedExposure = rendered.Exposure;
            Surface.Session.Doc.MarkSaved(rendered.Revision);
            if (!_closed)
            {
                OpenFolder.IsEnabled = true;
                UpdateTitle();   // the window is this file's from now on
                UpdateState();
            }
            App.Current.Log.Info("viewer saved " + path);
            return true;
        }
        catch (Exception ex) { App.Current.Log.Warn("viewer save as: " + ex.Message); return false; }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Shell.Reveal(_result.SavedPath, App.Current.Log);
    }
}
