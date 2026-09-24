using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using ToneSnip.App.Capture;
using ToneSnip.App.Interop;
using ToneSnip.App.Theme;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using ToneSnip.Windows;
using ToneSnip.Windows.Imaging;
using ToneSnip.Windows.Interop;
using ToneSnip.Windows.Tray;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.UI;

namespace ToneSnip.App;

/// <summary>
/// `tonesnip-debug.exe --screenshots &lt;dir&gt; [--theme dark|light|both]`: opens every window in turn, drives it
/// into each designed state, photographs the real window pixels and exits.
/// <para>Safe beside a running ToneSnip: no mutex, tray icon, keyboard hook, capture, host pipe or toast registration,
/// and no write under <see cref="AppPaths.Dir"/> except the log. <see cref="App.ScreenshotMode"/> makes
/// <see cref="App.ApplySettings"/> an in-memory update.</para>
/// <para><c>TONESNIP_NO_MICA=1</c> forces the no-Mica fallback (as in a Remote Desktop session), here and in
/// `--hold`.</para>
/// </summary>
internal static partial class Screenshots
{
    /// <summary>One saved PNG, as index.json records it.</summary>
    private sealed record Shot(string File, string Window, string State, string Theme, string Method, int Width, int Height, double DpiScale);

    private const int SmCxScreen = 0, SmCyScreen = 1;
    private const uint PwRenderFullContent = 2;

    /// <summary>Every window `--hold` accepts, as <see cref="Hold"/> dispatches it, so <c>CommandLine.Parse</c> can
    /// refuse an unknown one up front. Bare family names are defaults: `settings` is General, `flyout` the Row layout,
    /// `toolbar` the resting bar, `editor` the SDR editor.</summary>
    internal static readonly string[] HoldWindows =
    {
        "flyout", "flyout-row", "flyout-grid",
        "settings", "settings-general", "settings-hotkeys", "settings-tonemap", "settings-output", "settings-about",
        "editor", "editor-hdr", "editor-front", "toolbar", "toolbar-annotate",
        "countdown", "textentry", "toast-saved", "toast-copied", "toast-dwell", "toast-notice", "pin",
    };

    /// <summary>The rows <see cref="SeedHistory"/> seeded, so the empty-state shot can restore them.</summary>
    private static List<Core.Output.HistoryEntry> _seededRows = new();

    private static readonly List<Shot> Index = new();
    private static int _failures;
    private static IntRect _primary, _work;
    private static double _scale = 1.0;

    /// <summary>Starts the XAML application for this mode: <see cref="Program"/>'s start-up without the mutex, so the
    /// running app keeps its single instance.</summary>
    internal static int Start(StartupCommand command)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>   // not `_`: the discard below would bind to the parameter instead
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App { Command = command };
        });
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>Runs every capture, writes index.json and ends the message loop. A window that fails is logged,
    /// counted and skipped.</summary>
    internal static async Task Run(string dir, string? theme)
    {
        App app = App.Current;
        try
        {
            Directory.CreateDirectory(dir);
            // Shots show settled states, so the app's own transitions are switched off.
            ThemeManager.AnimationsPinned = true;
            ThemeManager.AnimationsEnabled = false;
            _primary = new IntRect(0, 0, User32.GetSystemMetrics(SmCxScreen), User32.GetSystemMetrics(SmCyScreen));
            app.PrimaryMonitor = _primary;
            _work = Native.PrimaryWorkArea();
            if (_work.IsEmpty) _work = _primary;
            _scale = Native.ScaleAt(_primary);
            SeedSettings();
            SeedHistory();
            app.Log.Info($"screenshots: primary {_primary.Width}x{_primary.Height} at {_scale:0.##}x, work area {_work.Width}x{_work.Height}, {app.History.Items.Count} history rows -> {dir}");

            foreach (string t in Themes(theme))
            {
                ThemeManager.Apply(t);
                await Settle();
                await Section(t, "settings", () => CaptureSettings(dir, t));
                await Section(t, "flyout", () => CaptureFlyout(dir, t));
                await Section(t, "editor", () => CaptureEditor(dir, t));
                await Section(t, "toolbar", () => CaptureToolbar(dir, t));
                await Section(t, "countdown", () => CaptureCountdown(dir, t));
                await Section(t, "textentry", () => CaptureTextEntry(dir, t));
                await Section(t, "toast", () => CaptureToast(dir, t));
            }

            // Not per theme: the tray glyph is rendered in every colour, and the native menu is drawn to a bitmap in
            // each palette.
            await Section("both", "tray", () => { CaptureTrayGlyphs(dir); CaptureTrayMenu(dir); return Task.CompletedTask; });

            File.WriteAllText(Path.Combine(dir, "index.json"), JsonSerializer.Serialize(Index, new JsonSerializerOptions { WriteIndented = true }));
            app.Log.Info($"screenshots: {Index.Count} files written, {_failures} failed");
        }
        catch (Exception ex)
        {
            _failures++;
            app.Log.Error("screenshots: " + ex);
        }
        app.Exit();
    }

    private static string[] Themes(string? theme) => theme switch
    {
        "dark" => new[] { "dark" },
        "light" => new[] { "light" },
        _ => new[] { "dark", "light" },
    };

    /// <summary>One window's worth of captures. A failure here costs that window, not the run.</summary>
    private static async Task Section(string theme, string name, Func<Task> body)
    {
        try { await body(); }
        catch (Exception ex) { _failures++; App.Current.Log.Warn($"screenshots: {name} ({theme}) failed: {ex}"); }
    }
}
