using System.Runtime.CompilerServices;
using ToneSnip.App.Capture;
using ToneSnip.App.Interop;
using ToneSnip.App.Theme;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using ToneSnip.Windows.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace ToneSnip.App;

/// <summary>
/// `tonesnip-debug.exe --leaktest`: opens and closes every window type five times, collects, and fails (exit code 1)
/// if any window or tracked object is still alive.
/// <para>
/// Safe beside a running ToneSnip: no single-instance mutex, tray icon, keyboard hook, capture, host pipe or toast
/// registration, and no write under <see cref="AppPaths.Dir"/> except the log (<see cref="App.SideMode"/> keeps the
/// editor from persisting its style).
/// </para>
/// </summary>
internal static class LeakTest
{
    /// <summary>Create/close cycles per window type.</summary>
    private const int Rounds = 5;

    /// <summary>Objects that outlived their close, summed over every type; the process exit code.</summary>
    private static int _survivors;
    private static IntRect _monitor;

    /// <summary>Starts the XAML application for this mode, as <see cref="Screenshots.Start"/> does: no mutex.</summary>
    internal static int Start(StartupCommand command)
    {
        Kernel32.AttachConsole(Kernel32.AttachParentProcess);   // WinExe: reattach to the launching console so Console.WriteLine is visible
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>   // not `_`: the discard below would bind to the parameter instead
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App { Command = command };
        });
        return _survivors == 0 ? 0 : 1;
    }

    /// <summary>Runs every type's cycles and ends the message loop. A type that throws is counted as a failure and the
    /// rest still run, so one broken window does not hide the others' numbers.</summary>
    internal static async Task Run()
    {
        App app = App.Current;
        try
        {
            _monitor = Native.PrimaryWorkArea();
            if (_monitor.IsEmpty) _monitor = new IntRect(0, 0, 1920, 1080);
            app.PrimaryMonitor = _monitor;
            Line($"leaktest: {Rounds} create/close cycles per window type on {_monitor.Width}x{_monitor.Height}, {app.History.Items.Count} history rows");
            await Cycle("CountdownWindow", Countdown);
            await Cycle("TextEntryWindow", TextEntry);
            await Cycle("ToolbarWindow", Toolbar);
            await Cycle("ToastWindow", Toast);
            await Cycle("HistoryFlyout", Flyout);
            await Cycle("SettingsWindow", SettingsWin);
            await Cycle("ViewerWindow", Viewer);
            await Cycle("PinWindow", Pin);
            Line(_survivors == 0 ? "leaktest: PASS, nothing survived its close" : $"leaktest: FAIL, {_survivors} objects survived their close");
        }
        catch (Exception ex)
        {
            _survivors++;
            Line("leaktest: " + ex);
        }
        app.Exit();
    }

    /// <summary>
    /// One type: <see cref="Rounds"/> create/close cycles, a full collect, and the count of tracked objects still
    /// alive, with managed memory, native counters and handle types either side.
    /// </summary>
    private static async Task Cycle(string name, Func<List<WeakReference<object>>, Task> round)
    {
        var tracked = new List<WeakReference<object>>();
        long before = Collect();
        (long privBefore, uint gdiBefore, uint userBefore) = NativeCounters();
        Dictionary<string, int> handlesBefore = HandleCensus.Take();
        for (int i = 0; i < Rounds; i++)
        {
            try { await round(tracked); }
            catch (Exception ex) { _survivors++; Line($"leaktest: {name} round {i + 1} failed: {ex}"); break; }
        }
        // WinUI keeps a reference to the most recently closed window until another takes its place, so throwaway
        // flush windows are opened before counting. A genuinely leaked window survives every flush.
        await Settle();
        int alive = 0;
        for (int flush = 1; flush <= 3; flush++)
        {
            await Flush();
            Collect();
            alive = tracked.Count(r => r.TryGetTarget(out _));
            if (alive == 0) { if (flush > 1) Line($"leaktest: {name} let go after {flush} flush windows"); break; }
        }
        long after = Collect();
        (long privAfter, uint gdiAfter, uint userAfter) = NativeCounters();
        string handlesMoved = HandleCensus.Diff(handlesBefore, HandleCensus.Take());
        _survivors += alive;
        Line($"leaktest: {name,-15} {Rounds} cycles, {alive}/{tracked.Count} alive, memory {Mb(before)} -> {Mb(after)} MB ({(after >= before ? "+" : "")}{Mb(after - before)} MB)"
             + $"; native: private WS {privBefore / 1_048_576} -> {privAfter / 1_048_576} MB ({(privAfter - privBefore) / (double)Rounds / 1_048_576:+0.0;-0.0} MB a cycle), gdi {gdiBefore} -> {gdiAfter}, user {userBefore} -> {userAfter}; handles: {handlesMoved}");
    }

    /// <summary>Private working set and GDI/USER handle counts: native leaks the survivor count cannot see.</summary>
    private static (long Private, uint Gdi, uint User) NativeCounters()
    {
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        return (Interop.ProcessMemory.PrivateWorkingSet(), User32.GetGuiResources(me.Handle, 0), User32.GetGuiResources(me.Handle, 1));
    }

    /// <summary>A bare window, opened and closed, to displace WinUI's reference to the last closed window.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task Flush()
    {
        var win = new Window { Content = new Microsoft.UI.Xaml.Controls.Border { Width = 120, Height = 60 } };
        win.AppWindow.Move(new PointInt32(_monitor.Left + 24, _monitor.Top + 24));
        win.Activate();
        await Settle();
        win.Close();
        await Settle();
    }

    // ----- the windows, created and closed the way the app does them -------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task Countdown(List<WeakReference<object>> tracked)
    {
        var win = new Overlay.CountdownWindow(_monitor);
        tracked.Add(new WeakReference<object>(win));
        win.Set(3);
        await Settle();
        win.Close();
        await Settle();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task TextEntry(List<WeakReference<object>> tracked)
    {
        // Through the factory, as the overlay builds it.
        uint accent = ThemeManager.AccentArgb;
        Annotate.TextEntryWindow win = Annotate.TextEntryWindow.For(App.Current.Settings.Annotate.ToStyle(accent), accent);
        tracked.Add(new WeakReference<object>(win));
        _ = win.ShowAt(_monitor.Left + _monitor.Width / 3, _monitor.Top + _monitor.Height / 3);
        await Settle();
        win.SetText("leak test");
        await Settle();
        win.Close();
        await Settle();
    }

    /// <summary>The toolbar and a synthetic session (no capture). Closed as a finished snip closes them: the toolbar's
    /// <c>Close()</c>, which <c>Show()</c> would otherwise own, then the session's <c>Finish</c>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task Toolbar(List<WeakReference<object>> tracked)
    {
        var info = new OutputInfo(0, @"\\.\DISPLAY1", _monitor.Left, _monitor.Top, _monitor.Width, _monitor.Height, false, 80f, 400f, "Primary");
        var outputs = new List<CapturedOutput> { new(info, null, BgraImage.Blank(8, 8)) };
        var session = new Overlay.OverlaySession(outputs, null!, _monitor, SnipMode.Rectangle, App.Current.Settings, App.Current.Log);
        var toolbar = new Overlay.ToolbarWindow(session, _monitor);
        tracked.Add(new WeakReference<object>(toolbar));
        tracked.Add(new WeakReference<object>(session));
        session.ToolbarChanged = toolbar.Refresh;
        await Settle();
        session.Annotating = true;   // the tool row, which is where most of the toolbar's controls live
        await Settle();
        toolbar.Close();
        session.Finish(Overlay.OverlayOutcome.Cancelled);
        await Settle();
    }

    /// <summary>The notification card, bound and then dismissed as its close button does. Dismiss runs the fade-out
    /// storyboard with Close queued behind it, which is the path that must not hold the window (and its snip).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task Toast(List<WeakReference<object>> tracked)
    {
        var card = new Output.ToastWindow(App.Current.Log, _ => { }, _ => { }, _ => { });
        tracked.Add(new WeakReference<object>(card));
        card.Bind(Screenshots.SyntheticResult(hdr: false), "");
        await Settle();
        card.BindNotice("Text copied", "“leak test”", "");   // the text-only notice re-binds the same card
        await Settle();
        card.Dismiss();
        await Settle();
        await Settle();   // the 120 ms fade, then the Close it queues behind itself
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task Flyout(List<WeakReference<object>> tracked)
    {
        var flyout = new Tray.HistoryFlyout();
        tracked.Add(new WeakReference<object>(flyout));
        flyout.Open();
        await Settle();
        await Settle();   // the rows, then the placement, land on separate layout passes
        flyout.Dismiss();
        await Settle();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task SettingsWin(List<WeakReference<object>> tracked)
    {
        var win = new Settings.SettingsWindow();
        tracked.Add(new WeakReference<object>(win));
        win.AppWindow.Move(new PointInt32(_monitor.Left + 24, _monitor.Top + 24));
        win.Activate();
        await Settle();
        win.ShowPage(2, advanced: true, hdr: false);   // every page's controls get built at least once
        await Settle();
        win.Close();
        await Settle();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task Viewer(List<WeakReference<object>> tracked)
    {
        var win = new Viewer.ViewerWindow(Screenshots.SyntheticResult(hdr: false));
        tracked.Add(new WeakReference<object>(win));
        win.AppWindow.Move(new PointInt32(_monitor.Left + 24, _monitor.Top + 24));
        win.Activate();
        await Settle();
        win.ShowState(annotate: true);
        await Settle();
        // Shows the HDR-done note so its dwell timer is running when the window closes.
        win.ShowHdrDoneForHarness();
        await Settle();
        // Closed mid-way through a zoom animation, with the picker's loupe up.
        win.ExerciseViewForHarness();
        win.Close();
        await Settle();
    }

    /// <summary>A pin, zoomed and faded (the layered-window path), then unpinned as Escape does. The PNG and the picture
    /// decoded from it must go with the window.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task Pin(List<WeakReference<object>> tracked)
    {
        BgraImage img = Screenshots.SyntheticResult(hdr: false).Image;
        byte[] png = Windows.Imaging.Bitmaps.EncodePng(img);
        var pin = new Output.PinWindow(png, img.Width, img.Height, DateTime.Now, (_monitor.Left + 48, _monitor.Top + 48));
        tracked.Add(new WeakReference<object>(pin));
        tracked.Add(new WeakReference<object>(png));
        await Settle();
        pin.ExerciseForHarness();
        await Settle();
        pin.Close();
        await Settle();
    }

    // ----- the harness ---------------------------------------------------------------------------------------------

    /// <summary>Low-priority dispatcher turns around a short delay: queued layout work has run and the app's
    /// transitions (167 ms at the longest) have ended.</summary>
    private static async Task Settle()
    {
        await Turn();
        await Turn();
        await Task.Delay(200);
        await Turn();
    }

    private static Task Turn()
    {
        var done = new TaskCompletionSource();
        if (!App.Current.Ui.TryEnqueue(DispatcherQueuePriority.Low, () => done.TrySetResult())) done.TrySetResult();
        return done.Task;
    }

    /// <summary>A blocking gen-2 collect, pending finalizers, and a second collect for what they released.</summary>
    private static long Collect()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(true);
    }

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0");

    private static void Line(string text)
    {
        Console.WriteLine(text);
        App.Current.Log.Info(text);
    }
}
