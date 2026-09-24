using System.Runtime.InteropServices.WindowsRuntime;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Extract;
using ToneSnip.Core.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ToneSnip.App.Output;

/// <summary>
/// "Copy text": recognises the text in a snip with the in-box Windows OCR (Windows.Media.Ocr) in the user's profile
/// languages, and puts it on the clipboard as plain text. The engine is created per call and dropped straight after,
/// so its language model is not held while the app idles in the tray.
/// </summary>
public static class TextExtractor
{
    /// <summary>What came of one recognition: the text (empty when none was found), or why there is none.</summary>
    public sealed record Outcome(string Text, string? Problem)
    {
        public bool Found => Problem == null && Text.Length > 0;
    }

    /// <summary>The message when Windows has no OCR language for any of the user's languages.</summary>
    public const string NoLanguage = "Windows has no text recognition for your languages. Add a language with its optical character recognition feature in Settings > Time & language > Language & region.";

    /// <summary>Recognises the text in <paramref name="image"/>, which must not change meanwhile. Thread pool: the
    /// resize and the recognition both run off the calling thread.</summary>
    public static Task<Outcome> RecognizeAsync(BgraImage image, ILog log) => Task.Run(async () =>
    {
        OcrEngine? engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null) { log.Warn("ocr: no OCR language is installed for the user's profile languages"); return new Outcome("", NoLanguage); }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Screen text is small; the engine reads it better enlarged, and refuses an image past its limit.
        double scale = OcrText.ScaleFor(image.Width, image.Height, (int)OcrEngine.MaxImageDimension);
        // The bitmap takes its own copy, so the scaled image is unreferenced before the recognition allocates.
        using SoftwareBitmap bitmap = ToBitmap(Resample.Scale(image, scale));
        OcrResult result = await engine.RecognizeAsync(bitmap);
        var lines = new List<Core.Extract.OcrLine>(result.Lines.Count);
        foreach (global::Windows.Media.Ocr.OcrLine line in result.Lines)
        {
            var words = new List<Core.Extract.OcrWord>(line.Words.Count);
            foreach (global::Windows.Media.Ocr.OcrWord w in line.Words)
                words.Add(new Core.Extract.OcrWord(w.Text, w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height));
            lines.Add(new Core.Extract.OcrLine(words));
        }
        string text = OcrText.Join(lines);
        // The length only: the text itself may be private and never goes to the log.
        log.Info($"ocr: {image.Width}x{image.Height} at {scale:0.##}x, {engine.RecognizerLanguage.LanguageTag}, {lines.Count} lines, {text.Length} characters in {sw.ElapsedMilliseconds} ms");
        return new Outcome(text, null);
    });

    private static SoftwareBitmap ToBitmap(BgraImage img)
        => SoftwareBitmap.CreateCopyFromBuffer(img.Data.AsBuffer(), BitmapPixelFormat.Bgra8, img.Width, img.Height, BitmapAlphaMode.Ignore);
}
