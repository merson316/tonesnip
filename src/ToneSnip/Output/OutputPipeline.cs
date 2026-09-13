using System.Diagnostics;
using ToneSnip.App.Capture;
using ToneSnip.Core.Config;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Imaging;
using ToneSnip.Windows.Imaging;

namespace ToneSnip.App.Output;

/// <summary>
/// Clipboard, then autosave, then whoever listens (toast/viewer). Each stage is independent; failures are logged.
/// <para>Encoding and writing run on the thread pool so a large snip does not block the UI. <see cref="Completed"/>
/// and the compaction after it run on the calling (UI) thread.</para>
/// </summary>
public sealed class OutputPipeline(Func<SnipSettings> settings, ILog log)
{
    public event Action<CaptureResult>? Completed;
    /// <summary>When true the result keeps its image, half crops and document after output (an editor is about to open); the editor compacts on close.</summary>
    public Func<CaptureResult, bool> KeepAlive { get; set; } = _ => false;
    public Func<uint> Accent { get; set; } = () => Annotate.ShapeRenderer.DefaultAccent;

    public async Task RunAsync(CaptureResult result)
    {
        SnipSettings s = settings();
        uint accent = Accent();
        (BgraImage img, byte[]? png) = await Task.Run(() => Write(result, s, accent));
        result.Output = img;   // the rendered image; Compact below keeps it as the PNG
        Completed?.Invoke(result);
#if TONESNIP_HARNESS
        MemoryProbe.Mark("after toast/viewer");
#endif
        if (!KeepAlive(result))
        {
            // Encode on the pool, but compact on this thread, where Completed's listeners read the result.
            if (png == null)
                png = await Task.Run(() => { try { return Bitmaps.EncodePng(img); } catch (Exception e) { log.Error("png: " + e.Message); return null; } });
            result.Compact(png);
        }
    }

    /// <summary>Everything that encodes or touches the disk or the clipboard. Thread pool.</summary>
    private (BgraImage Image, byte[]? Png) Write(CaptureResult result, SnipSettings s, uint accent)
    {
        BgraImage img = result.Rendered(accent);
        byte[]? png = null;
        if (s.CopyToClipboard || s.Format == "png") { try { png = Bitmaps.EncodePng(img); } catch (Exception e) { log.Error("png: " + e.Message); } }
        result.CopyAttempted = s.CopyToClipboard;
        result.SaveAttempted = s.AutoSave;
        if (s.CopyToClipboard && png != null)
        {
            try { ClipboardWriter.Set(img, png, log); result.Copied = true; log.Debug($"clipboard: {img.Width}x{img.Height}"); } catch (Exception e) { log.Error("clipboard: " + e.Message); }
#if TONESNIP_HARNESS
            MemoryProbe.Mark("after clipboard");
#endif
        }
        if (s.AutoSave)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                // Refused rather than resolved against the working directory.
                string folder = s.SafeSaveFolder(AppPaths.Pictures) ?? throw new IOException("the save folder is not an absolute path: " + Core.Config.PathGuard.Describe(s.ResolvedSaveFolder(AppPaths.Pictures)));
                result.SavedPath = ImageSaver.Save(img, folder, s.Format, s.JpegQuality, result.TakenLocal, png);
                log.Info("saved " + result.SavedPath);
                App.Current.Timing("saved", sw);
            }
            catch (Exception e) { log.Error("save: " + e.Message); }
#if TONESNIP_HARNESS
            MemoryProbe.Mark("after save");
#endif
        }
        if (s.AutoSave && result.SavedPath != null && s.Hdr.File != "none" && result.Crops.Count > 0)
        {
            // The HDR sidecar must never take the SDR save, Completed or Compact down with it.
            try { HdrOutput.Write(result, img, result.SavedPath, s.Hdr.File, s, accent, log); }
            catch (Exception e) { log.Error("hdr: " + e.Message); }
#if TONESNIP_HARNESS
            MemoryProbe.Mark("after hdr");
#endif
        }
        return (img, png);
    }
}
