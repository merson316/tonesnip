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

/// <summary>`--hold`: one real window left up for an outside UIA driver, one method per window family.</summary>
internal static partial class Screenshots
{
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
            _primary = new IntRect(0, 0, User32.GetSystemMetrics(SmCxScreen), User32.GetSystemMetrics(SmCyScreen));
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
            else if (what.Equals("pin", StringComparison.OrdinalIgnoreCase))
            {
                await HoldPin(seconds);
            }
            else if (!what.StartsWith("flyout", StringComparison.OrdinalIgnoreCase))
            {
                _failures++;
                app.Log.Warn($"hold: no window called {what}; known: flyout-row, flyout-grid, settings-<page>, editor, " +
                             "editor-hdr, editor-front, toolbar, toolbar-annotate, countdown, textentry, toast-saved, toast-copied, toast-dwell, " +
                             "toast-notice, pin");
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
        int n = User32.GetWindowTextW(WinRT.Interop.WindowNative.GetWindowHandle(window), buffer, buffer.Length);
        return n > 0 ? new string(buffer, 0, n) : "";
    }

    /// <summary>
    /// `--hold editor-front [seconds]`: opens the editor the way a notification click does, through
    /// <see cref="App.OpenViewer"/> from a queued dispatcher turn a moment after start, when this process (launched from
    /// a script) holds no foreground right, and logs whether the editor ended up in the foreground.
    /// </summary>
    private static async Task HoldEditorFront(CaptureResult result, int seconds)
    {
        App app = App.Current;
        await Task.Delay(1500);
        var opened = new TaskCompletionSource();
        app.Ui.TryEnqueue(() => { app.OpenViewer(result); opened.SetResult(); });
        await opened.Task;
        await Task.Delay(1000);
        User32.GetWindowThreadProcessId(User32.GetForegroundWindow(), out uint pid);
        bool front = pid == (uint)Environment.ProcessId;
        app.Log.Info($"hold: editor-front foreground {(front ? "is the editor" : "is pid " + pid)}");
        Console.WriteLine($"hold: editor-front foreground {(front ? "is the editor" : "is pid " + pid)}");
        if (!front) _failures++;
        for (int i = 0; i < seconds; i++) await Task.Delay(1000);
        Console.WriteLine("hold: editor closing");
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
        if (what.Equals("editor-front", StringComparison.OrdinalIgnoreCase)) { await HoldEditorFront(result, seconds); return; }
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
        var info = new OutputInfo(0, @"\\.\DISPLAY1", _primary.Left, _primary.Top, _primary.Width, _primary.Height, true, 80f, 400f, "Primary");
        var outputs = new List<CapturedOutput> { new(info, new Core.Hdr.HalfFrame(SyntheticHalf(8, 8)), BgraImage.Blank(8, 8)) };
        var session = new Overlay.OverlaySession(outputs, null!, _primary, SnipMode.Rectangle, app.Settings, app.Log);
        var toolbar = new Overlay.ToolbarWindow(session, _primary);
        try
        {
            session.ToolbarChanged = toolbar.Refresh;
            session.Announce = toolbar.Announce;   // as Show wires it, so a UIA pass can read the live region
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
        var card = new Output.ToastWindow(app.Log, r => app.OpenViewer(r), r => app.OpenViewer(r, annotate: true), r => _ = app.PinAsync(r));
        bool closed = false;
        try
        {
            card.WhenClosed(() => closed = true);
            // toast-notice: the text-only card that Copy text shows.
            if (what.EndsWith("notice", StringComparison.OrdinalIgnoreCase)) card.BindNotice("Text copied", "3 lines · “ToneSnip reads the text in a snip”", (string)app.Resources["GlyphCopyText"]);
            else card.Bind(result, thumb);
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
    /// `--hold pin [seconds]`: a snip pinned to the screen, 80 px in from the primary work area's top-left, zoomed and
    /// faded once as the wheel would, then back to 100 %. It activates, and Escape or its close button unpins it.
    /// </summary>
    private static async Task HoldPin(int seconds)
    {
        App app = App.Current;
        BgraImage img = SyntheticResult(hdr: false).Image;
        var pin = new Output.PinWindow(Windows.Imaging.Bitmaps.EncodePng(img), img.Width, img.Height, DateTime.Now, (_work.Left + 80, _work.Top + 80));
        bool closed = false;
        try
        {
            pin.WhenClosed(() => closed = true);
            await Settle();
            pin.ExerciseForHarness();
            await Settle();
            app.Log.Info($"hold: a pin, {img.Width}x{img.Height}");
            for (int i = 0; i < seconds && !closed; i++)
            {
                await Task.Delay(1000);
                Console.WriteLine($"hold: {i + 1}s {(closed ? "pin CLOSED" : "pin up")}");
            }
            Console.WriteLine(closed ? "hold: the pin was closed" : "hold: pin closing");
        }
        finally { if (!closed) { Forget(pin); pin.Close(); } await Settle(); }
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
}
