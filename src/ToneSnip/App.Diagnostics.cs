using System.Runtime.InteropServices;
using ToneSnip.App.Interop;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Windows.Capture;
using Microsoft.Win32;

namespace ToneSnip.App;

/// <summary>The tray's "Re-arm hotkeys" and the About page's "Copy diagnostics": what to reach for when a hotkey stops
/// working, and what to paste into the report about it.</summary>
public partial class App
{
    /// <summary>
    /// Re-installs the keyboard hook now, for a hotkey that has stopped working. The hook re-arms itself on resume, on
    /// unlock and every few minutes; this is the same re-arm on demand, logged at Info with where it came from so the
    /// log shows whether the user had to reach for it.
    /// </summary>
    public void RearmHotkeys(string from)
    {
        if (Hook is not { } hook) { Log.Warn($"re-arm hotkeys ({from}): there is no keyboard hook in this process"); return; }
        Log.Info($"re-arm hotkeys asked for from {from}; hook {(hook.Armed ? "armed" : "NOT armed")} before it, {hook.BindingCount} bindings");
        hook.Rearm("from " + from);
    }

    /// <summary>
    /// Builds the diagnostics text (<see cref="DiagnosticsReport"/>) and puts it on the clipboard. The log is read off
    /// the UI thread; everything else is read here first, since the monitors and settings are the UI thread's. Returns
    /// whether the text reached the clipboard.
    /// </summary>
    public async Task<bool> CopyDiagnosticsAsync()
    {
        try
        {
            DiagnosticsInfo facts = DiagnosticsFacts();
            string text = await Task.Run(() =>
            {
                (string, string)[] folders = ProfileFolders();
                List<string> lines = DiagnosticsReport.RelevantLines(ReadLog()).Select(l => DiagnosticsReport.Scrub(l, folders)).ToList();
                return DiagnosticsReport.Format(facts with { LogLines = lines });
            });
            await ToneSnip.App.Output.ClipboardWriter.SetTextQueued(text, Log);
            Log.Info($"diagnostics copied, {text.Length} characters");
            return true;
        }
        catch (Exception e)
        {
            Log.Warn("copy diagnostics: " + e.Message);
            return false;
        }
    }

    /// <summary>The report's facts, read on the UI thread; its log lines are filled in afterwards.</summary>
    private DiagnosticsInfo DiagnosticsFacts()
    {
        DiagnosticsHotkeys? hotkeys = Hook is { } hook
            ? new DiagnosticsHotkeys(hook.Armed, hook.ArmedAt, hook.Arms, Paused, Settings.Bindings().Select(b => $"{b.Chord}={b.Action}").ToList())
            : null;
        return new DiagnosticsInfo
        {
            Version = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
#if TONESNIP_HARNESS
            Build = "debug build",
#else
            Build = Autostart.IsPackaged ? "installed package" : "portable exe",
#endif
            Os = OsVersion(),
            Runtime = RuntimeInformation.FrameworkDescription,
            Monitors = KnownOutputs.Select(o => new DiagnosticsMonitor(o, Native.ScaleAt(o.Bounds))).ToList(),
            Hotkeys = hotkeys,
            // The grabber's own answer, which honours TONESNIP_GPU_TONEMAP; the harnesses' holds have no grabber.
            GpuTonemap = Grabber is { } g ? g.UseGpu() : Settings.Hdr.GpuTonemap,
            CaptureSupported = ScreenCapture.Supported,
            CapturePointer = Settings.CaptureCursor,
            GdiFallbacks = Grabber is { } grabber ? grabber.LastFallbacks : Array.Empty<string>(),
            Now = DateTime.Now,
            LogLines = Array.Empty<string>(),
        };
    }

    /// <summary>"10.0.26100.4652 (24H2)": the kernel's version with the update revision and release name the registry
    /// adds, since the build alone does not say which cumulative update is installed.</summary>
    private static string OsVersion()
    {
        Version v = Environment.OSVersion.Version;
        try
        {
            using RegistryKey? k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string ubr = k?.GetValue("UBR") is int u ? "." + u : "";
            string release = k?.GetValue("DisplayVersion") is string d && d.Length > 0 ? $" ({d})" : "";
            return $"{v.Major}.{v.Minor}.{v.Build}{ubr}{release}";
        }
        catch { return v.ToString(); }
    }

    /// <summary>The folders under the user's profile that log lines can name, and what the report writes instead.</summary>
    private static (string, string)[] ProfileFolders() =>
    [
        (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
        (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%"),
        (Path.GetTempPath(), "%TEMP%"),
        (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%"),
    ];

    /// <summary>The previous log file then the current one, oldest line first. Opened for shared reading, since this
    /// process may be writing the current one.</summary>
    private IEnumerable<string> ReadLog()
    {
        if (Log is not FileLog file) yield break;
        foreach (string path in new[] { file.PreviousPath, file.Path })
        {
            List<string> lines = new();
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line) lines.Add(line);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            foreach (string line in lines) yield return line;
        }
    }
}
