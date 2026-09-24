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

/// <summary>The per-window captures of a `--screenshots` run: settings, flyout, editor, toolbar, countdown, text entry
/// and toast.</summary>
internal static partial class Screenshots
{
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
        var info = new OutputInfo(0, @"\\.\DISPLAY1", _primary.Left, _primary.Top, _primary.Width, _primary.Height, true, 80f, 400f, "Primary");
        var outputs = new List<CapturedOutput> { new(info, new Core.Hdr.HalfFrame(SyntheticHalf(8, 8)), BgraImage.Blank(8, 8)) };
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
            var card = new Output.ToastWindow(App.Current.Log, _ => { }, _ => { }, _ => { });
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
}
