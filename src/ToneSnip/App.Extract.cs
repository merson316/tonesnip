using ToneSnip.App.Capture;
using ToneSnip.App.Interop;
using ToneSnip.App.Output;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Imaging;

namespace ToneSnip.App;

/// <summary>Reference and extract: "Copy text" and pins, from the overlay, the card, Recent, the editor and pins.</summary>
public partial class App
{
    /// <summary>A selection armed for something other than an ordinary snip (<see cref="SnipSession.Divert"/>).</summary>
    private Task DivertSnip(CaptureResult result, SnipAction action) => action switch
    {
        SnipAction.CopyText => CopyTextAsync(result.Image),
        // Pinned where it was taken, so it lands exactly over what it shows.
        SnipAction.Pin => PinAsync(result, (result.Region.Left, result.Region.Top)),
        _ => Output.RunAsync(result),
    };

    /// <summary>Pins on screen. A WinUI window with no reference is collected even while on screen.</summary>
    private readonly List<PinWindow> _pins = new();

    /// <summary>
    /// Pins <paramref name="r"/> to the screen as its rendered SDR image: at <paramref name="at"/> (physical pixels), or
    /// centred on the monitor under the cursor. A compacted result's PNG is used as it is; otherwise the render is
    /// encoded on the pool, and only the PNG is kept. UI thread.
    /// </summary>
    public async Task PinAsync(CaptureResult r, (int X, int Y)? at = null)
    {
        try
        {
            uint accent = Theme.ThemeManager.AccentArgb;
            (byte[] png, int w, int h) = await Task.Run(() =>
            {
                if (r.CompactedPng is { } held && Core.Imaging.PngInfo.TrySize(held, out int pw, out int ph)) return (held, pw, ph);
                BgraImage img = r.Rendered(accent);
                return (Windows.Imaging.Bitmaps.EncodePng(img), img.Width, img.Height);
            });
            var pin = new PinWindow(png, w, h, r.TakenLocal, at);
            _pins.Add(pin);
            pin.WhenClosed(() => { _pins.Remove(pin); ReclaimMemory("memory after pin"); });
            Log.Info($"pinned {w}x{h}, {_pins.Count} pin(s) open");
        }
        catch (Exception e)
        {
            Log.Warn("pin: " + e.Message);
            _tray?.Balloon("Pin failed", e.Message);
        }
    }

    /// <summary>The longest part of the recognised text the notice quotes back.</summary>
    private const int TextPreview = 60;

    /// <summary>
    /// Recognises the text in <paramref name="image"/> off the UI thread, puts it on the clipboard as plain text and
    /// says so on the card; says so too when there was none, or no language to read it with. The image must not change
    /// until this completes. UI thread.
    /// </summary>
    public async Task CopyTextAsync(BgraImage image)
    {
        string glyph = (string)Resources["GlyphCopyText"];
        try
        {
            TextExtractor.Outcome found = await TextExtractor.RecognizeAsync(image, Log);
            if (found.Problem != null) { Toasts.ShowNotice("Text not copied", found.Problem, glyph); return; }
            if (!found.Found) { Toasts.ShowNotice("No text found", "Nothing readable in the selection; the clipboard is unchanged", glyph); return; }
            await ClipboardWriter.SetTextQueued(found.Text, Log);
            string first = found.Text.Split('\r', '\n')[0];
            string quote = first.Length > TextPreview ? first[..TextPreview] + "…" : first;
            int lines = found.Text.Split("\r\n").Length;
            Toasts.ShowNotice("Text copied", lines > 1 ? $"{lines} lines · “{quote}”" : $"“{quote}”", glyph);
        }
        catch (Exception e)
        {
            Log.Warn("copy text: " + e.Message);
            Toasts.ShowNotice("Text not copied", e.Message, glyph);
        }
        finally { ReclaimMemory("memory after text"); }
    }
}
