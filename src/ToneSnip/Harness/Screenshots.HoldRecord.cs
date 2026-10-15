using System.Globalization;
using ToneSnip.App.Interop;
using ToneSnip.App.Record;
using ToneSnip.App.Theme;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Geometry;
using ToneSnip.Windows.Record;

namespace ToneSnip.App;

internal static partial class Screenshots
{
    /// <summary>
    /// `--hold record [seconds]`: a real recording, with its outline and bar, of a 640 x 360 area near the bottom-right
    /// corner of the monitor under the pointer, or of TONESNIP_HOLD_AREA ("x,y,w,h" in virtual-desktop pixels). No
    /// snip screen is opened, so nothing is frozen and the keyboard stays where it was; the bar can be driven through
    /// UIA meanwhile, and Stop or Discard there ends the hold early. The file goes to %TEMP% and is deleted afterwards.
    /// With TONESNIP_HOLD_SHOTS naming a folder, the bar is photographed there during the countdown and while recording.
    /// The console reports the countdown, each second recorded, and the file: its length, frames, codec and size.
    /// </summary>
    private static async Task HoldRecord(int seconds)
    {
        App app = App.Current;
        List<OutputInfo> outputs = app.Grabber.Outputs();
        (int cx, int cy) = Native.CursorPos();
        IntRect? asked = Area(Environment.GetEnvironmentVariable("TONESNIP_HOLD_AREA"));
        OutputInfo? output = asked is { } a
            ? outputs.FirstOrDefault(o => o.Bounds.IntersectsWith(a))
            : outputs.FirstOrDefault(o => o.Bounds.Contains(cx, cy)) ?? outputs.FirstOrDefault();
        if (output == null) { _failures++; Console.WriteLine("hold: no monitor to record"); return; }
        IntRect m = output.Bounds;
        IntRect area = asked ?? new IntRect(m.Right - 640 - 120, m.Bottom - 360 - 200, 640, 360);
        if (Core.Record.RecordingArea.For(area, [m]) is not var (monitor, fixedArea)) { _failures++; Console.WriteLine($"hold: {area} is not on a monitor"); return; }
        string path = Path.Combine(Path.GetTempPath(), $"tonesnip-hold-record-{Environment.ProcessId}.mp4");
        var request = new RecordingRequest(monitor, fixedArea, output.Hdr, app.Grabber.CurveFor(output), 30, true, path);
        var session = new RecordingSession(request, app.Log, ThemeManager.OverlayAccentArgb);
        string? shots = Environment.GetEnvironmentVariable("TONESNIP_HOLD_SHOTS");
        string theme = ThemeManager.IsDark ? "dark" : "light";
        Console.WriteLine($"hold: recording {fixedArea} on {output.DeviceName}, hdr={output.Hdr}");
        Task<(RecordingResult? Result, string? Failure)> run = session.RunAsync();
        try
        {
            await Settle();
            if (shots != null && session.BarForHarness is { } countdown) await Save(shots, "recordbar-countdown", theme, countdown, preferRender: true);
            for (int i = 0; i < seconds + RecordingSession.CountdownSeconds && !run.IsCompleted; i++)
            {
                await Task.WhenAny(run, Task.Delay(1000));
                if (run.IsCompleted) break;
                Console.WriteLine(i < RecordingSession.CountdownSeconds ? $"hold: {i + 1}s countdown" : $"hold: {i + 1 - RecordingSession.CountdownSeconds}s recorded");
                if (i == RecordingSession.CountdownSeconds + 1 && shots != null && session.BarForHarness is { } bar) await Save(shots, "recordbar-recording", theme, bar, preferRender: true);
            }
            if (!run.IsCompleted) session.Stop(keep: true);
            (RecordingResult? result, string? failure) = await run;
            if (failure != null) { _failures++; Console.WriteLine("hold: the recording failed: " + failure); }
            Console.WriteLine(result != null
                ? $"hold: recording saved, {result.Duration.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} s, {result.Frames} frames, {result.Codec} {result.Width}x{result.Height}, {new FileInfo(result.Path).Length / 1024} KB"
                : "hold: no recording was kept");
        }
        finally
        {
            if (!run.IsCompleted) { session.Stop(keep: false); await run; }
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    /// <summary>"x,y,w,h" as a rectangle, or null.</summary>
    private static IntRect? Area(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string[] p = text.Split(',');
        if (p.Length != 4) return null;
        var n = new int[4];
        for (int i = 0; i < 4; i++)
            if (!int.TryParse(p[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n[i])) return null;
        return new IntRect(n[0], n[1], n[2], n[3]);
    }
}
