using ToneSnip.Core.Capture;
using ToneSnip.Core.Diagnostics;
using Xunit;

namespace ToneSnip.Core.Tests;

public class DiagnosticsReportTests
{
    private static string Line(string level, string message) => $"2026-09-27 10:00:00.000 {level}: {message}";

    [Fact]
    public void Only_warnings_errors_and_the_hotkey_trail_are_kept()
    {
        string[] log =
        {
            Line("info", "ToneSnip 1.0.5.0 started, pid 1234"),
            Line("info", "Rectangle: grab 40 ms, region (0, 0, 10, 10), hdr=False, total 900 ms"),
            Line("info", "hotkey region, picked up in 3 ms"),
            Line("warn", "settings preview: the HDR frame was not downsampled: boom"),
            Line("info", "system resumed from sleep"),
            Line("debug", "exposure: pass at 1.00 in 2.0 ms"),
            Line("info", "keyboard hook re-armed (from the tray menu), 5 bindings"),
        };
        List<string> kept = DiagnosticsReport.RelevantLines(log);
        Assert.Equal(new[] { log[0], log[2], log[3], log[4], log[6] }, kept);
    }

    [Fact]
    public void Stack_frames_and_blank_lines_are_left_out()
    {
        string[] log = { Line("error", "overlay: System.Exception: boom"), "   at ToneSnip.App.Overlay.OverlaySession.Show()", "" };
        Assert.Equal(new[] { log[0] }, DiagnosticsReport.RelevantLines(log));
    }

    [Fact]
    public void Only_the_newest_lines_are_kept()
    {
        IEnumerable<string> log = Enumerable.Range(0, 100).Select(i => Line("warn", $"line {i}"));
        List<string> kept = DiagnosticsReport.RelevantLines(log, max: 3);
        Assert.Equal(new[] { Line("warn", "line 97"), Line("warn", "line 98"), Line("warn", "line 99") }, kept);
    }

    [Fact]
    public void Profile_folders_become_variables_the_longest_first()
    {
        var folders = new[] { (@"C:\Users\sean", "%USERPROFILE%"), (@"C:\Users\sean\AppData\Local\", "%LOCALAPPDATA%") };
        string text = @"open folder 'c:\users\SEAN\Pictures\Screenshots': denied; log at C:\Users\sean\AppData\Local\tonesnip\tonesnip.log";
        Assert.Equal(@"open folder '%USERPROFILE%\Pictures\Screenshots': denied; log at %LOCALAPPDATA%\tonesnip\tonesnip.log",
                     DiagnosticsReport.Scrub(text, folders));
    }

    [Fact]
    public void An_empty_folder_scrubs_nothing()
        => Assert.Equal(@"C:\x", DiagnosticsReport.Scrub(@"C:\x", new[] { ("", "%EMPTY%"), ("C:", "%DRIVE%") }));

    [Fact]
    public void The_report_names_each_monitor_and_the_hook_state()
    {
        var info = new DiagnosticsInfo
        {
            Version = "1.0.5",
            Build = "installed package",
            Os = "10.0.26100.4652 (24H2)",
            Runtime = ".NET 10.0.0",
            Monitors = new[]
            {
                new DiagnosticsMonitor(new OutputInfo(0, @"\\.\DISPLAY1", 0, 0, 3840, 2160, true, 240f, 1000f, "PG32UCDM"), 1.5),
                new DiagnosticsMonitor(new OutputInfo(1, @"\\.\DISPLAY2", 3840, 0, 1920, 1080, false, 80f, 300f), 1.0),
            },
            Hotkeys = new DiagnosticsHotkeys(true, new DateTime(2026, 9, 27, 9, 30, 0), 7, false, new[] { "PrintScreen=region" }),
            GpuTonemap = true,
            CaptureSupported = true,
            CapturePointer = false,
            GdiFallbacks = Array.Empty<string>(),
            Now = new DateTime(2026, 9, 27, 10, 0, 0),
            LogLines = new[] { Line("info", "hotkey region, picked up in 3 ms") },
        };
        string text = DiagnosticsReport.Format(info);
        Assert.Contains("ToneSnip 1.0.5 (installed package)", text);
        Assert.Contains(@"\\.\DISPLAY1 ""PG32UCDM"": 3840x2160 at (0, 0), 150 % scale, HDR on, SDR white 240 nits, peak 1000 nits", text);
        Assert.Contains(@"\\.\DISPLAY2: 1920x1080 at (3840, 0), 100 % scale, HDR off", text);
        Assert.Contains("hook armed, last armed 2026-09-27 09:30:00, 7 arm(s)", text);
        Assert.Contains("bindings: PrintScreen=region", text);
        Assert.Contains("HDR tonemapping on the GPU", text);
        Assert.Contains("  " + Line("info", "hotkey region, picked up in 3 ms"), text);
    }

    [Fact]
    public void A_process_without_a_hook_says_so()
    {
        var info = new DiagnosticsInfo
        {
            Version = "1.0.5", Build = "debug build", Os = "10.0", Runtime = ".NET",
            Monitors = Array.Empty<DiagnosticsMonitor>(), Hotkeys = null, GpuTonemap = false, CaptureSupported = false,
            CapturePointer = true, GdiFallbacks = new[] { @"\\.\DISPLAY3" }, Now = DateTime.MinValue, LogLines = Array.Empty<string>(),
        };
        string text = DiagnosticsReport.Format(info);
        Assert.Contains("no keyboard hook", text);
        Assert.Contains("none known yet", text);
        Assert.Contains(@"copied these through GDI: \\.\DISPLAY3", text);
        Assert.Contains("NOT supported", text);
    }
}
