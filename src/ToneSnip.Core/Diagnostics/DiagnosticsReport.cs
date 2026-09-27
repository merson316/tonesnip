using System.Globalization;
using System.Text;
using ToneSnip.Core.Capture;

namespace ToneSnip.Core.Diagnostics;

/// <summary>One monitor as the diagnostics report describes it: the output and its scaling.</summary>
public sealed record DiagnosticsMonitor(OutputInfo Output, double Scale);

/// <summary>The keyboard hook's state for the diagnostics report.</summary>
/// <param name="Armed">Whether the hook is installed right now.</param>
/// <param name="ArmedAt">When it was last installed, by the first arm or any re-arm.</param>
/// <param name="Arms">How many times it has been installed.</param>
/// <param name="Paused">The tray's Pause hotkeys.</param>
/// <param name="Bindings">Each bound chord and its action, as "Chord=action".</param>
public sealed record DiagnosticsHotkeys(bool Armed, DateTime? ArmedAt, int Arms, bool Paused, IReadOnlyList<string> Bindings);

/// <summary>Everything the "Copy diagnostics" text is made from, gathered by the app.</summary>
public sealed record DiagnosticsInfo
{
    public required string Version { get; init; }
    /// <summary>Which exe: the installed package, the portable exe or the debug build.</summary>
    public required string Build { get; init; }
    /// <summary>The Windows version and build, as "10.0.26100.4652 (24H2)".</summary>
    public required string Os { get; init; }
    public required string Runtime { get; init; }
    public required IReadOnlyList<DiagnosticsMonitor> Monitors { get; init; }
    public required DiagnosticsHotkeys? Hotkeys { get; init; }
    public required bool GpuTonemap { get; init; }
    public required bool CaptureSupported { get; init; }
    public required bool CapturePointer { get; init; }
    /// <summary>The monitors the last grab had to copy through GDI.</summary>
    public required IReadOnlyList<string> GdiFallbacks { get; init; }
    public required DateTime Now { get; init; }
    /// <summary>The recent log lines worth reading (<see cref="DiagnosticsReport.RelevantLines"/>), already scrubbed.</summary>
    public required IReadOnlyList<string> LogLines { get; init; }
}

/// <summary>
/// The text the About page's "Copy diagnostics" puts on the clipboard, for pasting into a bug report: the build, the
/// monitors and their HDR state, the keyboard hook, the capture path and the log lines about those. Nothing else from
/// the log goes in, and folders under the user's profile are written as environment variables, so the text can be
/// pasted in public without naming the user.
/// </summary>
public static class DiagnosticsReport
{
    /// <summary>How many log lines the report carries at most, newest last.</summary>
    public const int MaxLogLines = 40;

    /// <summary>
    /// What a log line is about, lower-case, for <see cref="RelevantLines"/>: the hotkey trail (the hook, a pick-up,
    /// a refusal), what can take the hook away (sleep, the lock screen, displays off, a long GC pause), the display
    /// and capture state, and the start line that says which build wrote the log.
    /// </summary>
    private static readonly string[] Topics =
    {
        "hotkey", "keyboard hook", "refused", "resumed", "going to sleep", "session locked", "session unlocked",
        "displays on", "displays off", "display change", "the compacting collection", "gc pause", "graphics device",
        "using gdi", "capture:", "started, pid", "stuck",
    };

    /// <summary>The level tags <see cref="FileLog"/> writes that are always relevant.</summary>
    private static readonly string[] Levels = { " warn: ", " error: " };

    /// <summary>
    /// The last <paramref name="max"/> lines of <paramref name="lines"/> (oldest first, as the log is written) that are
    /// warnings, errors, or about one of the report's topics. Blank lines and the continuation lines of a multi-line
    /// entry (an exception's stack) are left out: they carry the frames, not what happened.
    /// </summary>
    public static List<string> RelevantLines(IEnumerable<string> lines, int max = MaxLogLines)
    {
        var kept = new Queue<string>();
        foreach (string line in lines)
        {
            if (line.Length < 24 || !char.IsDigit(line[0])) continue;   // not "yyyy-MM-dd HH:mm:ss.fff level: ..."
            if (!IsRelevant(line)) continue;
            kept.Enqueue(line);
            if (kept.Count > max) kept.Dequeue();
        }
        return kept.ToList();
    }

    private static bool IsRelevant(string line)
    {
        foreach (string level in Levels) if (line.Contains(level, StringComparison.Ordinal)) return true;
        foreach (string topic in Topics) if (line.Contains(topic, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// <paramref name="text"/> with each known folder written as its placeholder, longest folder first so
    /// %LOCALAPPDATA% wins over %USERPROFILE% for a path under both. Case-insensitive, as Windows paths are. Folders
    /// that are empty or too short to be a real path are skipped.
    /// </summary>
    public static string Scrub(string text, IEnumerable<(string Folder, string Placeholder)> folders)
    {
        foreach ((string folder, string placeholder) in folders.Where(f => f.Folder.Length >= 4).OrderByDescending(f => f.Folder.Length))
        {
            string trimmed = folder.TrimEnd('\\', '/');
            text = text.Replace(trimmed, placeholder, StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }

    /// <summary>The report, as plain text with one fact per line.</summary>
    public static string Format(DiagnosticsInfo d)
    {
        var s = new StringBuilder();
        CultureInfo c = CultureInfo.InvariantCulture;
        s.AppendLine(c, $"ToneSnip {d.Version} ({d.Build})");
        s.AppendLine(c, $"Windows {d.Os}, {d.Runtime}");
        s.AppendLine(c, $"Report written {d.Now.ToString("yyyy-MM-dd HH:mm:ss", c)}");
        s.AppendLine();
        s.AppendLine(c, $"Monitors ({d.Monitors.Count}):");
        if (d.Monitors.Count == 0) s.AppendLine("  none known yet");
        foreach (DiagnosticsMonitor m in d.Monitors)
        {
            OutputInfo o = m.Output;
            string name = string.IsNullOrWhiteSpace(o.FriendlyName) ? o.DeviceName : $"{o.DeviceName} \"{o.FriendlyName}\"";
            string hdr = o.Hdr ? $"HDR on, SDR white {o.SdrWhiteNits:F0} nits, peak {o.PeakNits:F0} nits" : "HDR off";
            s.AppendLine(c, $"  {name}: {o.Width}x{o.Height} at ({o.Left}, {o.Top}), {m.Scale * 100:F0} % scale, {hdr}");
        }
        s.AppendLine();
        if (d.Hotkeys is not { } h) s.AppendLine("Hotkeys: no keyboard hook in this process");
        else
        {
            string at = h.ArmedAt is { } t ? t.ToString("yyyy-MM-dd HH:mm:ss", c) : "never";
            s.AppendLine(c, $"Hotkeys: hook {(h.Armed ? "armed" : "NOT armed")}, last armed {at}, {h.Arms} arm(s){(h.Paused ? ", PAUSED" : "")}");
            s.AppendLine(c, $"  bindings: {(h.Bindings.Count == 0 ? "none" : string.Join(", ", h.Bindings))}");
        }
        s.AppendLine(c, $"Capture: Windows.Graphics.Capture {(d.CaptureSupported ? "supported" : "NOT supported")}, HDR tonemapping on the {(d.GpuTonemap ? "GPU" : "CPU")}, mouse pointer {(d.CapturePointer ? "captured" : "not captured")}");
        if (d.GdiFallbacks.Count > 0) s.AppendLine(c, $"  the last snip copied these through GDI: {string.Join(", ", d.GdiFallbacks)}");
        s.AppendLine();
        s.AppendLine(c, $"Recent log lines about hotkeys, displays and capture ({d.LogLines.Count}):");
        if (d.LogLines.Count == 0) s.AppendLine("  none");
        foreach (string line in d.LogLines) s.AppendLine("  " + line);
        return s.ToString();
    }
}
