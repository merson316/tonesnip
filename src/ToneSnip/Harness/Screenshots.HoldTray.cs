using System.Runtime.InteropServices;
using ToneSnip.Windows.Hotkeys;
using ToneSnip.Windows.Interop;
using ToneSnip.Windows.Tray;
using Microsoft.UI.Dispatching;

namespace ToneSnip.App;

/// <summary>`--hold tray-menu`: the tray's own context menu, open for a UIA driver to pick from.</summary>
internal static partial class Screenshots
{
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EndMenu();

    /// <summary>
    /// `--hold tray-menu [seconds]`: the real tray menu (<see cref="App.BuildTrayMenu"/>) over a keyboard hook with no
    /// bindings, which passes every key on. The driver invokes "Re-arm hotkeys"; the hold then checks the hook really
    /// was installed again and ends. The exit code says whether it was: a menu closed any other way, or still open
    /// when the time is up, is a failure.
    /// </summary>
    private static async Task HoldTrayMenu(int seconds)
    {
        App app = App.Current;
        using var window = new MessageWindow(app.Log);
        using var hook = new KeyboardHook(app.Log);
        hook.Install();
        app.UseHookForHarness(hook);
        int armsBefore = hook.Arms;
        TrayMenu menu = app.BuildTrayMenu(window);
        // The menu runs its own modal loop; a timer on this thread still ticks inside it and closes it at the end.
        DispatcherQueueTimer timer = app.Ui.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(seconds);
        timer.IsRepeating = false;
        bool timedOut = false;
        timer.Tick += (t, _) => { t.Stop(); timedOut = true; EndMenu(); };
        timer.Start();
        await Settle();
        Console.WriteLine("hold: tray menu up");
        menu.Show(_work.Right - 8, _work.Bottom - 8);
        timer.Stop();
        for (int i = 0; i < 30 && hook.Arms == armsBefore; i++) await Task.Delay(100);
        bool rearmed = hook.Arms > armsBefore;
        if (!rearmed) _failures++;
        string said = rearmed ? "hold: Re-arm hotkeys re-installed the hook"
                    : timedOut ? "hold: the tray menu timed out with nothing picked"
                    : "hold: the tray menu closed without re-arming the hook";
        app.Log.Info(said);
        Console.WriteLine(said);
    }
}
