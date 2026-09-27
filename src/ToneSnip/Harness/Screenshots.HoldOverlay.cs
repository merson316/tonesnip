using ToneSnip.App.Capture;
using ToneSnip.App.Interop;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Imaging;
using ToneSnip.Windows.Interop;

namespace ToneSnip.App;

/// <summary>`--hold overlay-lasso`: a real snip screen, over a synthetic frame, for a UIA driver to use the keyboard on.</summary>
internal static partial class Screenshots
{
    /// <summary>
    /// `--hold overlay-lasso [seconds]`: the real frozen-desktop window and bar on the primary monitor, in freeform
    /// mode, over a plain grey frame rather than a capture, so nothing on the desktop is frozen or snipped. The pointer
    /// starts in the middle of the monitor. The driver draws a lasso from the keyboard; the hold ends when the snip
    /// screen does, and its exit code says whether it ended with a lasso (at least three points around an area) rather
    /// than a cancel or the time running out. Nothing is saved: the outcome is only logged.
    /// </summary>
    private static async Task HoldOverlayLasso(int seconds)
    {
        App app = App.Current;
        var frame = BgraImage.Blank(_primary.Width, _primary.Height);
        for (int i = 0; i < frame.Data.Length; i += 4) { frame.Data[i] = frame.Data[i + 1] = frame.Data[i + 2] = 96; frame.Data[i + 3] = 255; }
        var info = new OutputInfo(0, @"\\.\DISPLAY1", _primary.Left, _primary.Top, _primary.Width, _primary.Height, false, 80f, 80f, "Primary");
        var outputs = new List<CapturedOutput> { new(info, null, frame) };
        // The plain flow: the "annotate" flows would stop at the tool row instead of ending the snip screen.
        var session = new Overlay.OverlaySession(outputs, null!, _primary, SnipMode.Freeform, app.Settings with { AfterSelect = "save" }, app.Log);
        Task<Overlay.OverlayOutcome> done = session.Show();
        // Over the snip screen, so the move reaches only this process's window.
        User32.SetCursorPos(_primary.Left + _primary.Width / 2, _primary.Top + _primary.Height / 2);
        Console.WriteLine("hold: snip screen up in freeform mode");
        if (await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(seconds))) != done) session.Finish(Overlay.OverlayOutcome.Cancelled);
        Overlay.OverlayOutcome outcome = await done;
        bool lasso = !outcome.Region.IsEmpty && outcome.Freeform is { Count: >= 3 };
        if (!lasso) _failures++;
        string said = lasso ? $"hold: the snip screen ended with a lasso of {outcome.Freeform!.Count} points around {outcome.Region}"
                            : "hold: the snip screen ended without a lasso";
        app.Log.Info(said);
        Console.WriteLine(said);
    }
}
