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
internal static class Screenshots
{
    /// <summary>One saved PNG, as index.json records it.</summary>
    private sealed record Shot(string File, string Window, string State, string Theme, string Method, int Width, int Height, double DpiScale);

    private const int SmCxScreen = 0, SmCyScreen = 1;
    private const uint PwRenderFullContent = 2;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, char[] text, int max);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    /// <summary>Every window `--hold` accepts, as <see cref="Hold"/> dispatches it, so <c>CommandLine.Parse</c> can
    /// refuse an unknown one up front. Bare family names are defaults: `settings` is General, `flyout` the Row layout,
    /// `toolbar` the resting bar, `editor` the SDR editor.</summary>
    internal static readonly string[] HoldWindows =
    {
        "flyout", "flyout-row", "flyout-grid",
        "settings", "settings-general", "settings-hotkeys", "settings-tonemap", "settings-output", "settings-about",
        "editor", "editor-hdr", "toolbar", "toolbar-annotate",
        "countdown", "textentry", "toast-saved", "toast-copied", "toast-dwell",
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

    /// <summary>
    /// `tonesnip-debug.exe --hold &lt;window&gt; [seconds]`: opens one real window and leaves it up for `winapp ui`
    /// to drive from another process. Nothing is faked (the flyout keeps its own dismissal), so a test can tell
    /// whether an interaction closes the window. A console line each second reports progress.
    /// </summary>
    internal static async Task Hold(string what, int seconds, string? theme = null, bool flipTheme = false)
    {
        App app = App.Current;
        Task? flip = null;
        try
        {
            _primary = new IntRect(0, 0, GetSystemMetrics(SmCxScreen), GetSystemMetrics(SmCyScreen));
            app.PrimaryMonitor = _primary;
            _work = Native.PrimaryWorkArea();
            if (_work.IsEmpty) _work = _primary;
            _scale = Native.ScaleAt(_primary);
            // Seed first: SeedSettings applies the seeded theme ("auto"), which would undo an earlier --theme.
            SeedSettings();
            SeedHistory();
            // `--theme light|dark` picks the theme explicitly. App.ScreenshotMode turns off ApplySettings' live
            // theme apply, so changing the theme through the held window's own controls has no effect here.
            if (theme is "dark" or "light") ThemeManager.Apply(theme);
            if (flipTheme) flip = ArmThemeFlip(theme, seconds);
            app.Log.Info($"hold: {what} for {seconds}s, {app.History.Items.Count} history rows");
            Console.WriteLine($"hold: {what} for {seconds}s");
            if (what.StartsWith("settings", StringComparison.OrdinalIgnoreCase))
            {
                await HoldSettings(what, seconds);
            }
            else if (what.StartsWith("editor", StringComparison.OrdinalIgnoreCase))
            {
                await HoldEditor(what, seconds);
            }
            else if (what.StartsWith("toolbar", StringComparison.OrdinalIgnoreCase))
            {
                await HoldToolbar(what, seconds);
            }
            else if (what.Equals("countdown", StringComparison.OrdinalIgnoreCase))
            {
                await HoldCountdown(seconds);
            }
            else if (what.Equals("textentry", StringComparison.OrdinalIgnoreCase))
            {
                await HoldTextEntry(seconds);
            }
            else if (what.StartsWith("toast", StringComparison.OrdinalIgnoreCase))
            {
                await HoldToast(what, seconds);
            }
            else if (!what.StartsWith("flyout", StringComparison.OrdinalIgnoreCase))
            {
                _failures++;
                app.Log.Warn($"hold: no window called {what}; known: flyout-row, flyout-grid, settings-<page>, editor, " +
                             "editor-hdr, toolbar, toolbar-annotate, countdown, textentry, toast-saved, toast-copied, toast-dwell");
            }
            else
            {
                // The layout is read when the flyout is built; ApplySettings is in-memory in harness mode.
                string layout = what.EndsWith("grid", StringComparison.OrdinalIgnoreCase) ? "grid" : "row";
                app.ApplySettings(app.Settings with { RecentFlyoutLayout = layout });
                var flyout = new Tray.HistoryFlyout();
                bool closed = false;
                flyout.WhenClosed(() => closed = true);
                flyout.Open();
                await Settle();
                await Settle();   // the rows, then the placement, land on separate layout passes
                if (!closed) await FastPointerSweep(flyout);
                for (int i = 0; i < seconds && !closed; i++)
                {
                    await Task.Delay(1000);
                    if (closed) { Console.WriteLine($"hold: {i + 1}s flyout CLOSED"); break; }
                    // UIA cannot read Opacity, so the revealed-row count is printed for an outside driver to check
                    // that at most one row shows its actions.
                    (int revealed, int realized) = flyout.RevealedRows();
                    Console.WriteLine($"hold: {i + 1}s flyout up, revealed {revealed}/{realized}");
                }
                app.Log.Info(closed ? $"hold: the flyout closed itself before the {seconds}s were up" : "hold: the flyout was still up at the end");
                Console.WriteLine(closed ? "hold: the flyout closed itself" : "hold: the flyout survived");
                if (!closed) flyout.Dismiss();
                await Task.Delay(200);
            }
        }
        catch (Exception ex)
        {
            _failures++;
            app.Log.Error("hold: " + ex);
        }
        // Await the flip before exiting: a window that dismissed itself early, or a very short hold, can end before
        // it runs, and `Start` reads `_failures` as soon as `app.Exit()` returns.
        if (flip != null)
        {
            if (await Task.WhenAny(flip, Task.Delay(3000)) != flip)
            {
                _failures++;
                app.Log.Error("hold: the theme flip had not run when the hold ended");
                Console.WriteLine("hold: theme flip DID NOT RUN (the hold ended first)");
            }
        }
        app.Exit();
    }

    /// <summary>Sweeps a simulated pointer across the rows faster than one action fade, then checks the settled state:
    /// at most one row shows its actions, and only the one under the pointer.
    /// <para>Moves go through <see cref="Tray.HistoryFlyout.HoverRowForHarness"/> (the flyout's own
    /// <c>EnterRow</c> / <c>LeaveHover</c>). It runs in-process because 12 ms between moves, against an 83 ms fade, is
    /// faster than a UIA client can drive. Only settled counts are asserted; mid-fade rows are legitimately half
    /// revealed.</para></summary>
    private static async Task FastPointerSweep(Tray.HistoryFlyout flyout)
    {
        (int _, int realized) = flyout.RevealedRows();
        if (realized < 2) { Console.WriteLine($"hold: fast sweep skipped, only {realized} row(s) realized"); return; }
        int worst = 0;
        for (int pass = 0; pass < 6; pass++)
        {
            for (int i = 0; i < realized; i++)
            {
                flyout.HoverRowForHarness(pass % 2 == 0 ? i : realized - 1 - i);
                await Task.Delay(12);                       // well inside the fade, so every move overlaps the last
                worst = Math.Max(worst, flyout.RevealedRows().Revealed);
            }
            flyout.HoverRowForHarness(-1);                  // ...and out of the window entirely, with no settle
            await Task.Delay(12);
            worst = Math.Max(worst, flyout.RevealedRows().Revealed);
        }
        await Task.Delay(500);                              // longer than one fade: the settled state is the invariant
        (int afterSweep, int stillRealized) = flyout.RevealedRows();

        // "None revealed" would also pass if the reveal were broken, so rest on one row and require exactly one,
        // then sweep again and stop on a row.
        flyout.HoverRowForHarness(1);
        await Task.Delay(400);
        int onOneRow = flyout.RevealedRows().Revealed;
        for (int i = 0; i < realized; i++) { flyout.HoverRowForHarness(i); await Task.Delay(9); }
        await Task.Delay(400);
        int afterLanding = flyout.RevealedRows().Revealed;
        flyout.HoverRowForHarness(-1);
        await Task.Delay(400);
        int afterLeaving = flyout.RevealedRows().Revealed;

        var faults = new List<string>();
        if (afterSweep > 1) faults.Add($"{afterSweep} revealed after a sweep that ended outside the window");
        if (onOneRow != 1) faults.Add($"{onOneRow} revealed while the pointer sat on one row");
        if (afterLanding != 1) faults.Add($"{afterLanding} revealed after a sweep that ended on a row");
        if (afterLeaving != 0) faults.Add($"{afterLeaving} revealed after the pointer left");
        string counts = $"out {afterSweep}, resting {onOneRow}, landed {afterLanding}, left {afterLeaving}, of {stillRealized} realized, peak {worst} mid-fade";
        if (faults.Count > 0)
        {
            _failures++;
            App.Current.Log.Error($"hold: fast sweep: {string.Join("; ", faults)} ({counts})");
            Console.WriteLine($"hold: fast sweep FAIL - {string.Join("; ", faults)}");
        }
        else App.Current.Log.Info($"hold: fast sweep PASS ({counts})");
        Console.WriteLine($"hold: fast sweep {(faults.Count == 0 ? "PASS" : "FAIL")}, {counts}");
    }

    /// <summary>
    /// `--flip-theme`: half way through a hold, apply the other theme to the window already up.
    /// <para>Screenshots only show theme resources resolved at load time; element-scoped <c>ThemeDictionaries</c>
    /// (Theme/PickerToggle.xaml, Theme/DeleteAction.xaml, Controls/ModeGroup.xaml) must also follow a live theme
    /// change. The flip calls <see cref="ThemeManager.Apply(string)"/>, as WM_THEMECHANGED does, and its console line
    /// is the synchronisation point for an outside driver.</para>
    /// </summary>
    private static async Task ArmThemeFlip(string? from, int seconds)
    {
        if (from is not ("dark" or "light"))
        {
            _failures++;
            App.Current.Log.Warn("hold: --flip-theme needs --theme dark|light to have something to flip away from");
            Console.WriteLine("hold: --flip-theme ignored (no --theme dark|light)");
            return;
        }
        string to = from == "dark" ? "light" : "dark";
        // Half way, and never sooner than two seconds, so the driver can shoot the settled window first.
        int at = Math.Max(2, seconds / 2);
        Console.WriteLine($"hold: theme flip armed, {from} -> {to} at {at}s");
        // All inside a try: the hold may already have ended, and a throw here would otherwise go unobserved.
        var flipped = new TaskCompletionSource();
        try
        {
            await Task.Delay(at * 1000);
            App.Current.RunOnUi(() =>
            {
                try
                {
                    ThemeManager.Apply(to);
                    App.Current.Log.Info($"hold: theme flipped {from} -> {to} at {at}s");
                    Console.WriteLine($"hold: theme flipped {from} -> {to}");
                }
                // The window may already have gone (a flyout dismisses itself): report it rather than fail silently.
                catch (Exception ex)
                {
                    _failures++;
                    App.Current.Log.Error("hold: theme flip failed: " + ex);
                    Console.WriteLine($"hold: theme flip FAILED: {ex.Message}");
                }
                finally { flipped.TrySetResult(); }
            });
            await flipped.Task;
        }
        catch (Exception ex)
        {
            _failures++;
            try { App.Current.Log.Error("hold: theme flip could not be delivered: " + ex); } catch { }
            Console.WriteLine($"hold: theme flip FAILED to reach the UI thread: {ex.Message}");
        }
    }

    /// <summary>
    /// `--hold settings-general|settings-hotkeys|settings-tonemap|settings-output|settings-about [seconds]`: the real
    /// settings window on the named page, closed when the time is up.
    /// </summary>
    private static async Task HoldSettings(string what, int seconds)
    {
        App app = App.Current;
        string page = what.Contains('-') ? what[(what.IndexOf('-') + 1)..] : "general";
        int idx = Array.FindIndex(SettingsPages, p => p.Equals(page, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            _failures++;
            app.Log.Warn($"hold: no settings page called {page}; known: {string.Join(", ", SettingsPages)}");
            return;
        }
        var win = new Settings.SettingsWindow();
        try
        {
            Move(win.AppWindow);
            win.Activate();
            await Settle();
            win.ShowPage(idx);
            await Settle();
            app.Log.Info($"hold: settings on the {page} page");
            for (int i = 0; i < seconds; i++)
            {
                await Task.Delay(1000);
                Console.WriteLine($"hold: {i + 1}s settings up ({page})");
            }
            Console.WriteLine("hold: settings closing");
        }
        finally { Forget(win); win.Close(); await Settle(); }
    }

    /// <summary>
    /// The Win32 window text (what the taskbar and Alt+Tab show), which can differ from <c>Window.Title</c>.
    /// </summary>
    private static string WindowText(Window window)
    {
        var buffer = new char[512];
        int n = GetWindowTextW(WinRT.Interop.WindowNative.GetWindowHandle(window), buffer, buffer.Length);
        return n > 0 ? new string(buffer, 0, n) : "";
    }

    /// <summary>The page names `--hold settings-&lt;page&gt;` accepts, in nav item Tag order.</summary>
    private static readonly string[] SettingsPages = { "general", "hotkeys", "tonemap", "output", "about" };

    /// <summary>
    /// `--hold editor|editor-hdr [seconds]`: the real editor window. `editor` opens the newest history entry (or a
    /// synthetic snip); `editor-hdr` opens a synthetic HDR snip, so the exposure slider and zebra toggle appear
    /// without a live HDR capture.
    /// <para>A Save in this window writes for real, so a UIA script must not send Ctrl+S. The close goes through
    /// <c>CloseForHarness</c>, so no "save changes?" dialog is left open.</para>
    /// <para>`editor-hdr` also shows each status note for five seconds, so a UIA pass can observe the live region,
    /// then re-titles the window over a stand-in path as Save as would.</para>
    /// </summary>
    private static async Task HoldEditor(string what, int seconds)
    {
        App app = App.Current;
        bool hdr = what.EndsWith("hdr", StringComparison.OrdinalIgnoreCase);
        CaptureResult result = hdr
            ? SyntheticResult(hdr: true)
            : (app.History.Items.FirstOrDefault() is { } newest ? app.History.ToResult(newest) : null) ?? SyntheticResult(hdr: false);
        var win = new Viewer.ViewerWindow(result);
        try
        {
            Move(win.AppWindow);
            win.Activate();
            await Settle();
            app.Log.Info($"hold: the editor on {(hdr ? "a synthetic HDR snip" : result.SavedPath ?? "a synthetic snip")}");
            app.Log.Info($"hold: window text \"{WindowText(win)}\"");
            Console.WriteLine($"hold: window text \"{WindowText(win)}\"");
            if (hdr) await CycleEditorNotes(win);
            for (int i = 0; i < seconds; i++)
            {
                await Task.Delay(1000);
                Console.WriteLine($"hold: {i + 1}s editor up");
            }
            Console.WriteLine("hold: editor closing");
        }
        finally { Forget(win); win.CloseForHarness(); await Settle(); }
    }

    /// <summary>Shows each status note in turn, then applies the Save-as re-title. Nothing is written.</summary>
    private static async Task CycleEditorNotes(Viewer.ViewerWindow win)
    {
        foreach (string note in new[] { "note", "busy", "done", "error" })
        {
            win.ShowHarnessNote(note, true);
            App.Current.Log.Info($"hold: note {note} shown (live region)");
            Console.WriteLine($"hold: note {note} shown");
            // Long enough for a `winapp ui` call (about a second each) to read the live region.
            await Task.Delay(5000);
            win.ShowHarnessNote(note, false);
        }
        // Save as's re-title, over a name that was never written.
        win.RetitleForHarness(Path.Combine(AppPaths.Pictures, "Retitled by the harness.png"));
        await Settle();
        App.Current.Log.Info($"hold: window text after retitle \"{WindowText(win)}\"");
        Console.WriteLine($"hold: window text after retitle \"{WindowText(win)}\"");
    }

    /// <summary>
    /// `--hold toolbar|toolbar-annotate [seconds]`: the real overlay toolbar (with `-annotate`, the tool row too) over
    /// a synthetic session, so no overlay is created and the desktop is never frozen.
    /// <para>With no real session, the Close button does not close the bar and a delay pick does not restart a
    /// snip; the hold's timer closes it.</para>
    /// </summary>
    private static async Task HoldToolbar(string what, int seconds)
    {
        App app = App.Current;
        var info = new OutputInfo(0, @"\\.\DISPLAY1", _primary.Left, _primary.Top, _primary.Width, _primary.Height, 0, true, 80f, 400f, "Primary");
        var outputs = new List<CapturedOutput> { new(info, SyntheticHalf(8, 8), BgraImage.Blank(8, 8)) };
        var session = new Overlay.OverlaySession(outputs, null!, _primary, SnipMode.Rectangle, app.Settings, app.Log);
        var toolbar = new Overlay.ToolbarWindow(session, _primary);
        try
        {
            session.ToolbarChanged = toolbar.Refresh;
            await Settle();
            if (what.EndsWith("annotate", StringComparison.OrdinalIgnoreCase))
            {
                session.Annotating = true;   // raises ToolbarChanged, which attaches the shared tool row
                await Settle();
            }
            toolbar.LogSize("hold");
            app.Log.Info($"hold: the overlay toolbar ({what})");
            for (int i = 0; i < seconds; i++)
            {
                await Task.Delay(1000);
                Console.WriteLine($"hold: {i + 1}s toolbar up");
            }
            Console.WriteLine("hold: toolbar closing");
        }
        finally { Forget(toolbar); toolbar.Close(); await Settle(); }
    }

    /// <summary>
    /// `--hold countdown [seconds]`: the real countdown pill, cycling 3-2-1 so a UIA pass can observe the live region.
    /// </summary>
    private static async Task HoldCountdown(int seconds)
    {
        var win = new Overlay.CountdownWindow(_primary);
        try
        {
            win.Set(3);
            await Settle();
            App.Current.Log.Info("hold: the countdown pill, cycling 3-2-1");
            for (int i = 0; i < seconds; i++)
            {
                await Task.Delay(1000);
                int digit = 3 - i % 3;
                win.Set(digit);
                Console.WriteLine($"hold: {i + 1}s countdown showing {digit}");
            }
            Console.WriteLine("hold: countdown closing");
        }
        finally { Forget(win); win.Close(); await Settle(); }
    }

    /// <summary>
    /// `--hold textentry [seconds]`: the real annotation text box. It activates, and losing the foreground commits and
    /// closes it, so a UIA pass that opens anything else will find it gone.
    /// <para>Built through <see cref="Annotate.TextEntryWindow.For"/> from the persisted style, as the overlay builds
    /// it. Logs the settled size and the size re-measured from the parking position.</para>
    /// </summary>
    private static async Task HoldTextEntry(int seconds)
    {
        Annotate.TextEntryWindow win = OpenTextEntry();
        bool closed = false;
        try
        {
            win.WhenClosed(() => closed = true);
            await Settle();
            LogTextEntrySize(win, "settled");
            string parked = await win.MeasureParkedForHarnessAsync();
            App.Current.Log.Info($"hold: text box parked re-measure: {parked}");
            Console.WriteLine($"hold: text box parked re-measure: {parked}");
            await Settle();
            LogTextEntrySize(win, "after the parked re-measure");
            App.Current.Log.Info("hold: the annotation text box");
            for (int i = 0; i < seconds && !closed; i++)
            {
                await Task.Delay(1000);
                Console.WriteLine($"hold: {i + 1}s {(closed ? "text box CLOSED" : "text box up")}");
            }
            Console.WriteLine(closed ? "hold: the text box committed and closed itself" : "hold: text box closing");
        }
        finally { if (!closed) { Forget(win); win.Close(); } await Settle(); }
    }

    /// <summary>
    /// `--hold toast-saved|toast-copied|toast-dwell [seconds]`: the real notification card. Only the five-second dwell
    /// varies: <c>toast-saved</c> and <c>toast-copied</c> stop it so the card holds still; <c>toast-dwell</c> leaves
    /// it running so the timeout, hover pause and resume can be observed.
    /// <para>The subject is the newest history entry; `toast-copied` drops its path, as a clipboard-only snip
    /// would.</para></summary>
    private static async Task HoldToast(string what, int seconds)
    {
        App app = App.Current;
        bool dwell = what.EndsWith("dwell", StringComparison.OrdinalIgnoreCase);
        bool saved = !what.EndsWith("copied", StringComparison.OrdinalIgnoreCase);
        (CaptureResult result, string thumb) = ToastSubject(saved);
        var card = new Output.ToastWindow(app.Log, r => app.OpenViewer(r), r => app.OpenViewer(r, annotate: true));
        bool closed = false;
        try
        {
            card.WhenClosed(() => closed = true);
            card.Bind(result, thumb);
            if (!dwell) card.StopDwellForHarness();
            await Settle();
            if (!dwell) card.StopDwellForHarness();   // Bind restarts it; stop it again once the window has settled
            app.Log.Info($"hold: the notification card ({(saved ? result.SavedPath ?? "a synthetic snip" : "copied only")})"
                         + (dwell ? ", dwell running" : ", dwell stopped"));
            for (int i = 0; i < seconds && !closed; i++)
            {
                await Task.Delay(1000);
                Console.WriteLine($"hold: {i + 1}s {(closed ? "toast CLOSED" : "toast up")}");
            }
            Console.WriteLine(closed ? "hold: the card was dismissed" : "hold: toast closing");
        }
        finally { if (!closed) { Forget(card); card.Close(); } await Settle(); }
    }

    /// <summary>
    /// A snip for the card: the newest history entry that exists on disk, or a synthetic stand-in. With
    /// <paramref name="saved"/> false the path is removed, giving the clipboard-only card.
    /// <para><c>ToResult</c> decodes a fresh object each time, so nothing here touches a result the app holds.</para>
    /// </summary>
    private static (CaptureResult Result, string Thumb) ToastSubject(bool saved)
    {
        Output.HistoryItem? newest = App.Current.History.Items.FirstOrDefault(i => !i.FileMissing);
        CaptureResult result = (newest != null ? App.Current.History.ToResult(newest) : null) ?? SyntheticResult(hdr: false);
        string thumb = newest?.Entry.Thumb ?? "";
        if (saved) result.SavedPath ??= Path.Combine(AppPaths.Pictures, Core.Config.SnipSettings.DefaultSaveFolderName, "Snip 2026-09-06 142233.png");
        else result.SavedPath = null;
        return (result, thumb);
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
            _primary = new IntRect(0, 0, GetSystemMetrics(SmCxScreen), GetSystemMetrics(SmCyScreen));
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

    // ----- the windows ---------------------------------------------------------------------------------------------

    private static async Task CaptureSettings(string dir, string theme)
    {
        // The shots below change settings in memory; restore them afterwards so the next theme's pass starts from
        // the same values.
        var before = App.Current.Settings;
        var win = new Settings.SettingsWindow();
        try
        {
            Move(win.AppWindow);
            win.Activate();
            await Settle();
            foreach ((string name, int page, bool advanced, bool hdr, int location, bool jpeg) in new[]
            {
                ("settings-general", 0, false, false, 0, false),
                ("settings-hotkeys", 1, false, false, 0, false),
                ("settings-tonemap", 2, false, false, 0, false),
                ("settings-tonemap-advanced", 2, true, false, 0, false),
                ("settings-output", 3, false, false, 0, false),         // PNG: Format is the plain card, no chevron
                ("settings-output-location", 3, false, false, 1, false),      // the Save-automatically expander open
                ("settings-output-location-off", 3, false, false, 2, false),  // ...with the switch off: the Location row disabled
                ("settings-output-hdr", 3, false, true, 0, false),
                ("settings-output-jpeg", 3, false, false, 0, true),     // JPEG: Format is the expander, open on its quality row
                ("settings-about", 4, false, false, 0, false),
            })
            {
                // hdr picks JPEG XR and jpeg picks JPEG, both in memory (nothing here saves), and each opens the
                // expander that only its own format has.
                win.ShowPage(page, advanced, hdr, location, jpeg);
                // preferRender: across ten states in one window, PrintWindow's DWM surface lags a state behind.
                // RenderTargetBitmap reads the live XAML tree but loses Mica, corners and shadow; index.json
                // records the method.
                await Save(dir, name, theme, win, preferRender: true);
            }
        }
        finally { Forget(win); win.Close(); App.Current.ApplySettings(before); await Settle(); }
    }

    private static async Task CaptureFlyout(string dir, string theme)
    {
        // The flyout layout is a setting shown in `settings-general`; restore it so the next theme's settings shot
        // shows the seeded value.
        var beforeLayout = App.Current.Settings;
        try
        {
            await CaptureFlyoutStates(dir, theme);
        }
        finally { App.Current.ApplySettings(beforeLayout); }
    }

    private static async Task CaptureFlyoutStates(string dir, string theme)
    {
        int rows = App.Current.History.Items.Count;
        foreach (string layout in new[] { "row", "grid" })
        {
            // The layout is read when the flyout is built, so each layout gets its own window.
            App.Current.ApplySettings(App.Current.Settings with { RecentFlyoutLayout = layout });
            await Show(dir, $"flyout-{layout}", theme, -1, armed: false);
            await Show(dir, $"flyout-{layout}-hover", theme, 1, armed: false);
            if (layout == "row" && rows >= 4) await Show(dir, "flyout-row-delete-armed", theme, 3, armed: true);
            // One row revealed, then the list scrolls under a stationary pointer: recycled containers receive no
            // PointerExited, so this checks their reveal state is reset.
            if (rows >= 6) await Show(dir, $"flyout-{layout}-scrolled", theme, 1, armed: false, scroll: 240);
        }

        // The empty state (a fresh install). One shot covers both layouts: neither item panel is realised behind it.
        App.Current.ApplySettings(App.Current.Settings with { RecentFlyoutLayout = "row" });
        App.Current.History.SeedForHarness(Array.Empty<Core.Output.HistoryEntry>());
        try { await Show(dir, "flyout-empty", theme, -1, armed: false); }
        finally { App.Current.History.SeedForHarness(_seededRows); }

        // A fresh flyout per shot: hover and an armed delete are window state, and the first shot has to show neither.
        static async Task Show(string dir, string name, string theme, int row, bool armed, double scroll = 0)
        {
            var flyout = new Tray.HistoryFlyout { StaysOpen = true };
            try
            {
                flyout.Open();
                await Settle();
                await Settle();   // the rows, then the placement, land on separate layout passes
                if (row >= 0) flyout.ShowRowState(row, hover: true, armed);
                if (scroll > 0)
                {
                    (int wasRevealed, int wasRealized) = flyout.RevealedRows();
                    flyout.ScrollBy(scroll);
                    await Settle();
                    await Settle();
                    (int revealed, int realized) = flyout.RevealedRows();
                    App.Current.Log.Info($"screenshots: {name}: revealed rows {wasRevealed}/{wasRealized} before the scroll, {revealed}/{realized} after");
                }
                await Save(dir, name, theme, flyout);
            }
            finally
            {
                Forget(flyout);
                flyout.StaysOpen = false;
                flyout.Dismiss();
                await Settle();
            }
        }
    }

    private static async Task CaptureEditor(string dir, string theme)
    {
        var newest = App.Current.History.Items.FirstOrDefault();
        CaptureResult result = (newest != null ? App.Current.History.ToResult(newest) : null) ?? SyntheticResult(hdr: false);
        var win = new Viewer.ViewerWindow(result);
        try
        {
            Move(win.AppWindow);
            win.Activate();
            await Settle();
            await Save(dir, "editor", theme, win);
            win.ShowState(annotate: true);
            await Save(dir, "editor-annotate", theme, win);
            foreach ((string name, string panel) in new[] { ("editor-colour", "colour"), ("editor-width", "width"), ("editor-size", "size") })
            {
                win.ShowState(annotate: true, panel);
                await Save(dir, name, theme, win);
            }
        }
        finally { Forget(win); win.Close(); await Settle(); }

        // The HDR controls and zebra stripes need float crops; a synthetic scRGB canvas stands in for a live HDR snip.
        var hdrWin = new Viewer.ViewerWindow(SyntheticResult(hdr: true));
        try
        {
            Move(hdrWin.AppWindow);
            hdrWin.Activate();
            await Settle();
            hdrWin.ShowState(annotate: false, panel: null, zebra: true);
            await Save(dir, "editor-hdr", theme, hdrWin);
        }
        finally { Forget(hdrWin); hdrWin.Close(); await Settle(); }
    }

    private static async Task CaptureToolbar(string dir, string theme)
    {
        // No capture: the overlay windows are never created. An 8x8 half-float frame makes the session report HDR so
        // the exposure and zebra controls appear; the null grabber is only reached by moving the exposure slider.
        var info = new OutputInfo(0, @"\\.\DISPLAY1", _primary.Left, _primary.Top, _primary.Width, _primary.Height, 0, true, 80f, 400f, "Primary");
        var outputs = new List<CapturedOutput> { new(info, SyntheticHalf(8, 8), BgraImage.Blank(8, 8)) };
        var session = new Overlay.OverlaySession(outputs, null!, _primary, SnipMode.Rectangle, App.Current.Settings, App.Current.Log);
        var toolbar = new Overlay.ToolbarWindow(session, _primary);
        try
        {
            session.ToolbarChanged = toolbar.Refresh;
            await Settle();
            await Save(dir, "toolbar", theme, toolbar);
            // Opening a band must never widen the bar. Each state logs its size; the logged numbers are the check,
            // since a PrintWindow shot can be a frame stale.
            toolbar.LogSize("resting");
            toolbar.OpenBand("delay");
            await Save(dir, "toolbar-delay", theme, toolbar);
            toolbar.LogSize("delay");
            toolbar.ClosePopups();
            session.Annotating = true;   // raises ToolbarChanged, which attaches the shared tool row
            await Save(dir, "toolbar-annotate", theme, toolbar);
            toolbar.LogSize("annotate");
            toolbar.OpenBand("colour");
            await Save(dir, "toolbar-colour", theme, toolbar);
            toolbar.LogSize("colour");
            // The far-right band: this shot must be the same width as toolbar-annotate.
            toolbar.ClosePopups();
            toolbar.OpenBand("exposure");
            await Save(dir, "toolbar-exposure", theme, toolbar);
            toolbar.LogSize("exposure");
        }
        finally { Forget(toolbar); toolbar.Close(); await Settle(); }
    }

    private static async Task CaptureCountdown(string dir, string theme)
    {
        var win = new Overlay.CountdownWindow(_primary);
        try
        {
            win.Set(3);
            await Settle();
            await Save(dir, "countdown", theme, win);
        }
        finally { Forget(win); win.Close(); await Settle(); }
    }

    /// <summary>The text box as the overlay opens it (<see cref="Annotate.TextEntryWindow.For"/> over the persisted
    /// style), a third of the way into the work area.</summary>
    private static Annotate.TextEntryWindow OpenTextEntry()
    {
        uint accent = ThemeManager.AccentArgb;
        Annotate.TextEntryWindow win = Annotate.TextEntryWindow.For(App.Current.Settings.Annotate.ToStyle(accent), accent);
        _ = win.ShowAt(_work.Left + _work.Width / 3, _work.Top + _work.Height / 3);
        return win;
    }

    private static void LogTextEntrySize(Annotate.TextEntryWindow win, string when)
    {
        global::Windows.Graphics.SizeInt32 size = win.AppWindow.Size;
        App.Current.Log.Info($"hold: text box {size.Width}x{size.Height} {when} (style size {App.Current.Settings.Annotate.TextSize})");
        Console.WriteLine($"hold: text box {size.Width}x{size.Height} {when}");
    }

    private static async Task CaptureTextEntry(string dir, string theme)
    {
        Annotate.TextEntryWindow win = OpenTextEntry();
        try
        {
            await Settle();
            LogTextEntrySize(win, "settled");
            await Save(dir, "textentry-empty", theme, win);
            win.SetText("Clipped highlight");
            await Save(dir, "textentry-text", theme, win);
        }
        finally { Forget(win); win.Close(); await Settle(); }
    }

    /// <summary>
    /// The notification card as `toast-saved` and `toast-copied` (no file, so no "Show in folder"), one window each,
    /// with the dwell timer stopped so the card cannot fade out mid-shot.
    /// </summary>
    private static async Task CaptureToast(string dir, string theme)
    {
        foreach ((string name, bool saved) in new[] { ("toast-saved", true), ("toast-copied", false) })
        {
            (CaptureResult result, string thumb) = ToastSubject(saved);
            var card = new Output.ToastWindow(App.Current.Log, _ => { }, _ => { });
            try
            {
                card.Bind(result, thumb);
                card.StopDwellForHarness();
                await Settle();
                card.StopDwellForHarness();
                await Save(dir, name, theme, card);
            }
            finally { Forget(card); card.Close(); await Settle(); }
        }
    }

    /// <summary>The tray glyph at 16, 20 and 24 px (100 %, 125 %, 150 %) in each colour setting, read back from the
    /// HICON itself (<see cref="TrayGlyph.RenderIcon"/>) so the PNG is what the shell composites.
    /// <para>The high-contrast variant is drawn on the COLOR_WINDOW ground it is paired with, using
    /// <see cref="TrayGlyph.GlyphArgb"/> with high contrast forced on.</para></summary>
    private static void CaptureTrayGlyphs(string dir)
    {
        uint accent = ThemeManager.SystemAccentArgb;
        foreach ((string name, uint argb, uint ground) in new[]
        {
            ("tray-mono-dark", 0xFFFFFFFFu, 0u),
            ("tray-mono-light", 0xFF000000u, 0u),
            ("tray-accent", accent, 0u),
            ("tray-highcontrast", TrayGlyph.GlyphArgb("accent", "auto", accent, highContrast: true), SystemTheme.SysColorArgb(SystemTheme.ColorWindow)),
        })
            foreach (int size in new[] { 16, 20, 24 })
            {
                BgraImage? icon = TrayGlyph.RenderIcon(size, argb);
                if (icon == null) { App.Current.Log.Warn($"screenshots: {name}-{size}: the HICON could not be read back"); _failures++; continue; }
                BgraImage img = OnGround(icon, ground);
                string file = $"{name}-{size}.png";
                File.WriteAllBytes(Path.Combine(dir, file), Bitmaps.EncodePng(img));
                Index.Add(new Shot(file, "tray", $"{name["tray-".Length..]}-{size}", "n/a", "TrayGlyph.RenderIcon", img.Width, img.Height, 1.0));
                App.Current.Log.Info($"screenshots: {file} {img.Width}x{img.Height} read back from the HICON, glyph {argb:X8} on {ground:X8}");
            }
    }

    /// <summary>A transparent glyph composited over one opaque colour; the image itself when there is no ground (0).</summary>
    private static BgraImage OnGround(BgraImage img, uint groundArgb)
    {
        if (groundArgb == 0) return img;
        byte gb = (byte)groundArgb, gg = (byte)(groundArgb >> 8), gr = (byte)(groundArgb >> 16);
        for (int i = 0; i < img.Data.Length; i += 4)
        {
            int a = img.Data[i + 3];
            img.Data[i] = (byte)((img.Data[i] * a + gb * (255 - a)) / 255);
            img.Data[i + 1] = (byte)((img.Data[i + 1] * a + gg * (255 - a)) / 255);
            img.Data[i + 2] = (byte)((img.Data[i + 2] * a + gr * (255 - a)) / 255);
            img.Data[i + 3] = 255;
        }
        return img;
    }

    /// <summary>
    /// The tray context menu. It exists only inside <c>TrackPopupMenuEx</c>'s modal loop, so it cannot be captured;
    /// instead <see cref="TrayMenu.DrawSheet"/> renders the rows with the real menu's palette, measurements and text
    /// path (without the system border, shadow and corners).
    /// <para>Dark, light and high-contrast sheets. The entries cover every row role (header, plain, checked,
    /// accelerator, separator) and row 3 is drawn hovered.</para></summary>
    private static void CaptureTrayMenu(string dir)
    {
        using var window = new MessageWindow(App.Current.Log);
        var menu = new TrayMenu(window, App.Current.Log);
        menu.AddHeader("New snip");
        menu.Add("Rectangle", () => { }, accelerator: () => "Ctrl+Shift+S");
        menu.Add("Window", () => { });
        menu.AddSeparator();
        menu.AddHeader("Delay");
        menu.Add("None", () => { }, () => true);
        menu.Add("3 seconds", () => { });
        menu.AddSeparator();
        menu.Add("Settings…", () => { });
        menu.Add("Quit ToneSnip", () => { });

        foreach ((string name, bool dark, bool highContrast) in new[]
        {
            ("tray-menu-dark", true, false),
            ("tray-menu-light", false, false),
            ("tray-menu-highcontrast", true, true),
        })
        {
            menu.IsDark = () => dark;
            // Larger than the menu can measure to; cropped to what DrawSheet reports it filled.
            BgraImage? sheet = DrawMenuSheet(menu, 480, 640, highContrast);
            if (sheet == null) { _failures++; App.Current.Log.Error($"screenshots: {name} could not be drawn"); continue; }
            string file = $"{name}.png";
            File.WriteAllBytes(Path.Combine(dir, file), Bitmaps.EncodePng(sheet));
            Index.Add(new Shot(file, "tray", name["tray-".Length..], highContrast ? "highcontrast" : dark ? "dark" : "light", "TrayMenu.DrawSheet", sheet.Width, sheet.Height, 1.0));
            App.Current.Log.Info($"screenshots: {file} {sheet.Width}x{sheet.Height} via TrayMenu.DrawSheet");
        }
    }

    /// <summary>A top-down 32-bpp DIB for <see cref="TrayMenu.DrawSheet"/> to fill, cropped to what it filled.</summary>
    private static BgraImage? DrawMenuSheet(TrayMenu menu, int width, int height, bool highContrast)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr dc = CreateCompatibleDC(screen);
        IntPtr dib = IntPtr.Zero, old = IntPtr.Zero;
        BgraImage? img = null;
        // GDI handles are released in the finally so no path can leak them.
        try
        {
            var header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width, Height = -height,   // negative: top-down, so the rows line up with BgraImage's
                Planes = 1, BitCount = 32,
            };
            dib = CreateDIBSection(dc, ref header, 0 /*DIB_RGB_COLORS*/, out IntPtr bits, IntPtr.Zero, 0);
            if (dib != IntPtr.Zero)
            {
                old = SelectObject(dc, dib);
                (int w, int h) = menu.DrawSheet(dc, 96, highContrast, hoveredId: 3);
                GdiFlush();
                var full = BgraImage.Blank(width, height);
                Marshal.Copy(bits, full.Data, 0, full.Data.Length);
                // GDI leaves alpha at zero; the sheet is opaque, so set it.
                for (int i = 3; i < full.Data.Length; i += 4) full.Data[i] = 255;
                img = full.Crop(new IntRect(0, 0, Math.Min(w, width), Math.Min(h, height)));
            }
        }
        catch (Exception ex) { App.Current.Log.Error("screenshots: tray menu sheet: " + ex); }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(dc, old);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
        return img;
    }

    // ----- synthetic subjects --------------------------------------------------------------------------------------

    /// <summary>
    /// Replaces the history with <see cref="HarnessData"/>'s rows and freezes the clock. Called before any window
    /// exists.
    /// <para>The rows are held in memory only (<c>SnipHistory.SeedForHarness</c>). Each row's PNG and thumbnail are
    /// rewritten every run under a temp folder, never under <see cref="AppPaths.Dir"/>.</para>
    /// </summary>
    private static void SeedHistory()
    {
        App app = App.Current;
        string dir = Path.Combine(Path.GetTempPath(), "tonesnip-harness-history");
        var entries = new List<Core.Output.HistoryEntry>();
        try
        {
            Directory.CreateDirectory(dir);
            for (int i = 0; i < HarnessData.Rows.Length; i++)
            {
                HarnessData.Row row = HarnessData.Rows[i];
                BgraImage img = SyntheticImage(row.Width, row.Height);
                string name = HarnessData.FileName(row);
                string png = Path.Combine(dir, name);
                string thumb = Path.Combine(dir, $"thumb-{i + 1}.png");
                // The "file moved or deleted" row has no PNG; every row, missing or not, keeps its thumbnail.
                if (row.Missing) { try { File.Delete(png); } catch { /* already absent */ } }
                else File.WriteAllBytes(png, Bitmaps.EncodePng(img));
                File.WriteAllBytes(thumb, Bitmaps.EncodePng(Bitmaps.Thumbnail(img, Output.SnipHistory.ThumbMaxEdge)));
                // The HDR copy is named but not written: its badge and format tag come from the path alone.
                string? hdrPath = row.HdrFile == null ? null : Output.HdrOutput.PathFor(png, row.HdrFile);
                entries.Add(new Core.Output.HistoryEntry($"harness{i + 1}", row.Copied ? null : png,
                                                         HarnessData.TakenUtc(row), row.Width, row.Height, row.Hdr, thumb,
                                                         row.Copied ? null : hdrPath));
            }
        }
        catch (Exception ex) { _failures++; app.Log.Error("screenshots: history seed: " + ex); return; }
        HarnessData.Freeze();
        _seededRows = entries;
        app.History.SeedForHarness(entries);
        app.Log.Info($"screenshots: seeded {entries.Count} synthetic history rows from {dir}, ages measured from {HarnessData.ReferenceUtc:yyyy-MM-dd HH:mm:ss}Z");
    }

    /// <summary>
    /// Applies <see cref="HarnessData.Settings"/> in memory, ignoring any settings.json the debug build may have.
    /// The theme goes through <c>ThemeManager</c> directly, because <c>ApplySettings</c> skips the live theme apply in
    /// screenshot mode.</summary>
    private static void SeedSettings()
    {
        App app = App.Current;
        app.ApplySettings(HarnessData.Settings);
        ThemeManager.Apply(app.Settings.Theme);
        app.Log.Info($"screenshots: seeded settings (save folder {app.Settings.ResolvedSaveFolder(AppPaths.Pictures)}, theme {app.Settings.Theme})");
    }

    /// <summary>A stand-in snip: a diagonal gradient with a few flat rectangles, so scaling and cropping are
    /// visible.</summary>
    private static BgraImage SyntheticImage(int width, int height)
    {
        BgraImage img = BgraImage.Blank(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                img.Data[i] = (byte)(40 + 180 * y / height);
                img.Data[i + 1] = (byte)(60 + 150 * x / width);
                img.Data[i + 2] = (byte)(90 + 120 * (x + y) / (width + height));
                img.Data[i + 3] = 255;
            }
        foreach ((IntRect r, byte b, byte g, byte rd) in new[]
        {
            (new IntRect(width / 12, height / 8, width / 4, height / 5), (byte)0x20, (byte)0x20, (byte)0xE0),
            (new IntRect(width / 2, height / 3, width / 5, height / 4), (byte)0xE0, (byte)0xC0, (byte)0x20),
            (new IntRect(width / 6, height * 3 / 5, width / 3, height / 6), (byte)0xF0, (byte)0xF0, (byte)0xF0),
        })
            for (int y = r.Top; y < r.Bottom; y++)
                for (int x = r.Left; x < r.Right; x++)
                {
                    int i = (y * width + x) * 4;
                    img.Data[i] = b; img.Data[i + 1] = g; img.Data[i + 2] = rd; img.Data[i + 3] = 255;
                }
        return img;
    }

    /// <summary>The scRGB half-float twin of <see cref="SyntheticImage"/> (1.0 = 80 nits), with a corner well above SDR
    /// white so the zebra stripes have something to mark.</summary>
    private static HalfImage SyntheticHalf(int width, int height)
    {
        var canvas = new HalfImage(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                float v = (x + y) / (float)(width + height);
                float boost = x > width * 3 / 4 && y < height / 4 ? 6f : 1f;
                canvas.Data[i] = BitConverter.HalfToUInt16Bits((Half)(v * boost));
                canvas.Data[i + 1] = BitConverter.HalfToUInt16Bits((Half)(v * 0.6f * boost));
                canvas.Data[i + 2] = BitConverter.HalfToUInt16Bits((Half)(v * 0.3f * boost));
                canvas.Data[i + 3] = 0x3C00;   // 1.0
            }
        return canvas;
    }

    /// <summary>A stand-in snip for the editor and toast here and in <see cref="LeakTest"/>.</summary>
    internal static CaptureResult SyntheticResult(bool hdr)
    {
        var region = new IntRect(0, 0, 1280, 720);
        BgraImage image = SyntheticImage(region.Width, region.Height);
        var crops = new List<HalfCrop>();
        if (hdr)
        {
            var info = new OutputInfo(0, @"\\.\DISPLAY1", 0, 0, region.Width, region.Height, 0, true, 203f, 1000f, "Synthetic HDR");
            crops.Add(new HalfCrop(region, SyntheticHalf(region.Width, region.Height), info));
        }
        return new CaptureResult { Image = image, Region = region, AnyHdr = hdr, Crops = crops };
    }

    // ----- capture -------------------------------------------------------------------------------------------------

    /// <summary>Top-left of the primary monitor's work area, with the inset the design's own cards keep.</summary>
    private static void Move(Microsoft.UI.Windowing.AppWindow window)
        => window.Move(new PointInt32(_work.Left + (int)(24 * _scale), _work.Top + (int)(24 * _scale)));

    /// <summary>Low-priority dispatcher turns around a short delay, so queued layout work has run.
    /// <para>Kept at 150 ms: DWM stops refreshing the surface PrintWindow reads once a window sits still, so a longer
    /// wait produces stale captures. Stock NavigationView and Expander animations may still be moving in some
    /// shots.</para></summary>
    private static async Task Settle()
    {
        await Turn();
        await Turn();
        await Task.Delay(150);
        await Turn();
    }

    private static Task Turn()
    {
        var done = new TaskCompletionSource();
        if (!App.Current.Ui.TryEnqueue(DispatcherQueuePriority.Low, () => done.TrySetResult())) done.TrySetResult();
        return done.Task;
    }

    /// <summary>The two capture methods, as recorded in index.json.</summary>
    private const string PrintMethod = "PrintWindow", RenderMethod = "RenderTargetBitmap";

    /// <summary>The digest and method of each window's previous shot: two identical consecutive captures by the same
    /// method mean a stale DWM surface. Keyed by window object, since HWND values are reused.</summary>
    private static readonly Dictionary<Window, (string Digest, string Method)> LastShot = new();

    /// <summary>Drops a window's entry as it closes, so the dictionary does not keep closed windows alive. Every
    /// capture site calls this beside its Close/Dismiss.</summary>
    private static void Forget(Window window) => LastShot.Remove(window);

    /// <summary>Settles, photographs and writes the PNG. <c>PrintWindow(PW_RENDERFULLCONTENT)</c> keeps Mica, corners
    /// and shadow; a blank result falls back to rendering the XAML content root.
    /// <para>DWM stops refreshing a still window's surface, so a capture can show the previous state. A shot identical
    /// to the window's previous one is re-rendered from the live XAML tree, which cannot be stale. Waiting longer only
    /// makes this worse.</para>
    /// <para>The comparison only works between shots taken by the same method, so once a window falls back to the
    /// live tree, all its later shots do too.</para></summary>
    private static async Task Save(string dir, string name, string theme, Window window, bool preferRender = false)
    {
        // A window whose last shot came from the live tree keeps using it, so consecutive shots stay comparable.
        bool pinned = LastShot.TryGetValue(window, out (string Digest, string Method) last) && last.Method == RenderMethod;
        preferRender = preferRender || pinned;
        if (pinned) App.Current.Log.Info($"screenshots: {name}-{theme}.png takes the live tree; this window's previous shot did");
        // Activating makes DWM compose a fresh frame; only needed for PrintWindow, so the desktop is not disturbed
        // otherwise.
        if (!preferRender) window.Activate();
        await Settle();
        QuiesceScrollBars(window.Content);
        (BgraImage? img, string method) = await Photograph(window, preferRender);
        string file = $"{name}-{theme}.png";
        if (img == null)
        {
            _failures++;
            App.Current.Log.Warn($"screenshots: {file} produced no pixels");
            return;
        }
        string digest = Digest(img);
        // Same window, same method, same bytes: the DWM surface is stale.
        if (LastShot.TryGetValue(window, out last) && last.Method == method && last.Digest == digest)
        {
            App.Current.Log.Warn($"screenshots: {file} is byte-identical to the previous shot of this window; DWM's surface is stale, rendering the live tree instead");
            (BgraImage? live, string liveMethod) = await Photograph(window, preferRender: true);
            if (live != null) { img = live; method = liveMethod; digest = Digest(img); }
            if (last.Digest == digest) App.Current.Log.Warn($"screenshots: {file} is still identical after the re-render; the two states really do look the same");
        }
        LastShot[window] = (digest, method);
        File.WriteAllBytes(Path.Combine(dir, file), Bitmaps.EncodePng(img));
        int dash = name.IndexOf('-');
        Index.Add(new Shot(file, dash < 0 ? name : name[..dash], dash < 0 ? "default" : name[(dash + 1)..], theme, method, img.Width, img.Height, _scale));
        App.Current.Log.Info($"screenshots: {file} {img.Width}x{img.Height} via {method}");
    }

    /// <summary>
    /// Puts every <c>ScrollBar</c> under this window into its resting, invisible state before the shot. WinUI shows
    /// the panning indicator whenever a <c>ScrollViewer</c>'s extent changes, which otherwise makes shots vary run
    /// to run.
    /// <para>Uses the template's own <c>NoIndicator</c> and <c>Collapsed</c> states without transitions: the state the
    /// fade would reach by itself.</para>
    /// </summary>
    private static void QuiesceScrollBars(DependencyObject? root)
    {
        if (root == null) return;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is Microsoft.UI.Xaml.Controls.Primitives.ScrollBar bar)
            {
                VisualStateManager.GoToState(bar, "NoIndicator", useTransitions: false);
                VisualStateManager.GoToState(bar, "Collapsed", useTransitions: false);
            }
            QuiesceScrollBars(child);
        }
    }

    /// <summary>A cheap content hash of a shot, for the repeat check alone.</summary>
    private static string Digest(BgraImage image)
        => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(image.Data));

    /// <summary>
    /// The window's pixels through PrintWindow, or the rendered XAML content root (no Mica, corners or shadow) when
    /// that comes back blank or <paramref name="preferRender"/> is set.
    /// </summary>
    private static async Task<(BgraImage? Img, string Method)> Photograph(Window window, bool preferRender = false)
    {
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        BgraImage? shot = preferRender ? null : PrintWindowShot(hwnd);
        if (shot != null && !IsBlank(shot)) return (shot, PrintMethod);
        BgraImage? rendered = await RenderShot(window);
        // RenderTargetBitmap uses the content root's last layout size, which for a popup parked off-screen can be
        // larger than the window. Trim a larger render to the window; a smaller one (Mica frame) is kept whole.
        if (rendered != null && GetWindowRect(hwnd, out Rect r) && Trim(rendered, r.Right - r.Left, r.Bottom - r.Top) is { } trimmed)
        {
            App.Current.Log.Info($"screenshots: fallback render was {rendered.Width}x{rendered.Height} for a {r.Right - r.Left}x{r.Bottom - r.Top} window; trimmed to the window");
            return (trimmed, RenderMethod);
        }
        return (rendered, RenderMethod);
    }

    /// <summary>The top-left <paramref name="width"/> × <paramref name="height"/> of an image that is bigger than that;
    /// null when it is not, which is the ordinary case.</summary>
    private static BgraImage? Trim(BgraImage img, int width, int height)
    {
        if (width <= 0 || height <= 0 || (img.Width <= width && img.Height <= height)) return null;
        int w = Math.Min(img.Width, width), h = Math.Min(img.Height, height);
        BgraImage cut = BgraImage.Blank(w, h);
        for (int y = 0; y < h; y++) Array.Copy(img.Data, y * img.Width * 4, cut.Data, y * w * 4, w * 4);
        return cut;
    }

    /// <summary>The window's own pixels into a top-down 32-bpp DIB the size of its window rect.</summary>
    private static BgraImage? PrintWindowShot(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out Rect r)) return null;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return null;
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr dc = CreateCompatibleDC(screen);
        var header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = w, Height = -h,   // negative: top-down, so the rows line up with BgraImage's
            Planes = 1, BitCount = 32,
        };
        IntPtr dib = CreateDIBSection(dc, ref header, 0 /*DIB_RGB_COLORS*/, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = dib == IntPtr.Zero ? IntPtr.Zero : SelectObject(dc, dib);
        BgraImage? img = null;
        if (dib != IntPtr.Zero && PrintWindow(hwnd, dc, PwRenderFullContent))
        {
            GdiFlush();
            img = BgraImage.Blank(w, h);
            Marshal.Copy(bits, img.Data, 0, img.Data.Length);
            // DWM can leave alpha at zero for an opaque window. Keep per-pixel alpha when present (rounded corners),
            // otherwise make the image opaque.
            int opaque = 0;
            for (int i = 3; i < img.Data.Length; i += 4) if (img.Data[i] != 0) opaque++;
            if (opaque * 100 < w * h) for (int i = 3; i < img.Data.Length; i += 4) img.Data[i] = 255;
        }
        if (old != IntPtr.Zero) SelectObject(dc, old);
        if (dib != IntPtr.Zero) DeleteObject(dib);
        DeleteDC(dc);
        ReleaseDC(IntPtr.Zero, screen);
        return img;
    }

    /// <summary>The XAML content root only: no Mica, no window frame, no shadow. The fallback for a window PrintWindow
    /// hands back black.</summary>
    private static async Task<BgraImage?> RenderShot(Window window)
    {
        if (window.Content is not UIElement root) return null;
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(root);
        if (rtb.PixelWidth <= 0 || rtb.PixelHeight <= 0) return null;
        var img = BgraImage.Blank(rtb.PixelWidth, rtb.PixelHeight);
        using (Stream stream = (await rtb.GetPixelsAsync()).AsStream()) stream.ReadExactly(img.Data);
        // RenderTargetBitmap leaves unpainted pixels (most of a Mica page) transparent, so composite over the theme's
        // backdrop colour. The pixels are premultiplied: src + backdrop x (1 - a).
        Color backdrop = ThemeManager.BackdropColor();
        for (int i = 0; i < img.Data.Length; i += 4)
        {
            int inverse = 255 - img.Data[i + 3];
            if (inverse != 0)
            {
                img.Data[i] = (byte)Math.Min(255, img.Data[i] + backdrop.B * inverse / 255);
                img.Data[i + 1] = (byte)Math.Min(255, img.Data[i + 1] + backdrop.G * inverse / 255);
                img.Data[i + 2] = (byte)Math.Min(255, img.Data[i + 2] + backdrop.R * inverse / 255);
            }
            img.Data[i + 3] = 255;
        }
        return img;
    }

    /// <summary>
    /// True when the print came back as an empty frame: the interior inside the caption strip and border is one flat
    /// colour, which no real window of this app is.
    /// </summary>
    private static bool IsBlank(BgraImage img)
    {
        int left = Math.Min(8, img.Width / 4), right = img.Width - left;
        int top = Math.Min(48, img.Height / 4), bottom = img.Height - Math.Min(8, img.Height / 4);
        if (right - left < 8 || bottom - top < 8) return false;
        int i0 = (top * img.Width + left) * 4;
        byte b = img.Data[i0], g = img.Data[i0 + 1], r = img.Data[i0 + 2];
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                int i = (y * img.Width + x) * 4;
                if (img.Data[i] != b || img.Data[i + 1] != g || img.Data[i + 2] != r) return false;
            }
        return true;
    }
}
