using ToneSnip.App.Capture;
using ToneSnip.App.Interop;
using ToneSnip.App.Output;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Imaging;
using ToneSnip.Core.Output;

namespace ToneSnip.App;

/// <summary>Reference and extract: "Copy text" and pins, from the overlay, the card, Recent, the editor and pins.</summary>
public partial class App
{
    /// <summary>A selection armed for something other than an ordinary snip (<see cref="SnipSession.Divert"/>).</summary>
    private Task DivertSnip(CaptureResult result, SnipAction action) => action switch
    {
        SnipAction.CopyText => CopyTextSnipAsync(result),
        SnipAction.Pin => PinSnipAsync(result),
        _ => Output.RunAsync(result),
    };

    /// <summary>
    /// A pin made in the snip screen: pinned where it was taken, so it lands exactly over what it shows, and listed in
    /// Recent as an editor's pin is. The result keeps only the pin's PNG, which the pin holds anyway.
    /// </summary>
    private async Task PinSnipAsync(CaptureResult result)
    {
        if (await ShowPinAsync(result, (result.Region.Left, result.Region.Top)) is not var (png, rendered)) return;
        result.Output = rendered;   // the thumbnail shows what was pinned, shapes included
        _ = History.Add(result, HistoryKind.Pin);
        result.Compact(png);
    }

    /// <summary>
    /// A Copy text snip made in the snip screen. One that found text is listed in Recent with that text, which the
    /// search can then find; the result keeps a PNG, encoded on the pool, rather than its full image.
    /// </summary>
    private async Task CopyTextSnipAsync(CaptureResult result)
    {
        if (await CopyTextAsync(result.Image) is not { } text) return;
        _ = History.Add(result, HistoryKind.Text, text);
        await result.CompactAsync();
    }

    /// <summary>Pins on screen. A WinUI window with no reference is collected even while on screen.</summary>
    private readonly List<PinWindow> _pins = new();

    /// <summary>
    /// Pins <paramref name="r"/> to the screen as its rendered SDR image: at <paramref name="at"/> (physical pixels), or
    /// centred on the monitor under the cursor. A compacted result's PNG is used as it is; otherwise the render is
    /// encoded on the pool, and only the PNG is kept. UI thread.
    /// </summary>
    public Task PinAsync(CaptureResult r, (int X, int Y)? at = null) => ShowPinAsync(r, at);

    /// <summary><see cref="PinAsync"/>, returning the pin's PNG and the image rendered for it (null when a compacted
    /// result's PNG was used as it is), or null when the pin failed.</summary>
    private async Task<(byte[] Png, BgraImage? Rendered)?> ShowPinAsync(CaptureResult r, (int X, int Y)? at)
    {
        try
        {
            uint accent = Theme.ThemeManager.AccentArgb;
            (byte[] png, int w, int h, BgraImage? img) = await Task.Run(() =>
            {
                if (r.CompactedPng is { } held && Core.Imaging.PngInfo.TrySize(held, out int pw, out int ph)) return (held, pw, ph, (BgraImage?)null);
                BgraImage img = r.Rendered(accent);
                return (Windows.Imaging.Bitmaps.EncodePng(img), img.Width, img.Height, img);
            });
            var pin = new PinWindow(png, w, h, r.TakenLocal, at);
            _pins.Add(pin);
            pin.WhenClosed(() => { _pins.Remove(pin); ReclaimMemory("memory after pin"); });
            Log.Info($"pinned {w}x{h}, {_pins.Count} pin(s) open");
            return (png, img);
        }
        catch (Exception e)
        {
            Log.Warn("pin: " + e.Message);
            _tray?.Balloon("Pin failed", e.Message);
            return null;
        }
    }

    /// <summary>The longest part of the recognised text the notice quotes back.</summary>
    private const int TextPreview = 60;

    /// <summary>
    /// Recognises the text in <paramref name="image"/> off the UI thread, puts it on the clipboard as plain text and
    /// says so on the card; says so too when there was none, or no language to read it with. The image must not change
    /// until this completes. UI thread.
    /// </summary>
    /// <returns>The text copied, or null when there was none or it could not be copied.</returns>
    public async Task<string?> CopyTextAsync(BgraImage image)
    {
        string glyph = (string)Resources["GlyphCopyText"];
        try
        {
            TextExtractor.Outcome found = await TextExtractor.RecognizeAsync(image, Log);
            if (found.Problem != null) { Toasts.ShowNotice("Text not copied", found.Problem, glyph); return null; }
            if (!found.Found) { Toasts.ShowNotice("No text found", "Nothing readable in the selection; the clipboard is unchanged", glyph); return null; }
            await ClipboardWriter.SetTextQueued(found.Text, Log);
            string first = found.Text.Split('\r', '\n')[0];
            string quote = first.Length > TextPreview ? first[..TextPreview] + "…" : first;
            int lines = found.Text.Split("\r\n").Length;
            Toasts.ShowNotice("Text copied", lines > 1 ? $"{lines} lines · “{quote}”" : $"“{quote}”", glyph);
            return found.Text;
        }
        catch (Exception e)
        {
            Log.Warn("copy text: " + e.Message);
            Toasts.ShowNotice("Text not copied", e.Message, glyph);
            return null;
        }
        finally { ReclaimMemory("memory after text"); }
    }
}
