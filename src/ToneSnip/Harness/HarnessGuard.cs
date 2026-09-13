using System.Runtime.InteropServices;

namespace ToneSnip.App;

/// <summary>
/// Run before any harness that opens windows on the desktop: if a running ToneSnip has a snip in flight, wait for it
/// to finish rather than open windows over a frozen desktop mid-selection.
/// <para>The running app is another process, so the check goes through the window manager: the overlay's window
/// class and the countdown pill's window title are both findable top-level windows.</para>
/// </summary>
internal static class HarnessGuard
{
    /// <summary>The countdown pill's window text. Declared in the production build, because the window to find
    /// belongs to the installed app.</summary>
    private const string CountdownTitle = Overlay.CountdownWindow.ProbeTitle;

    /// <summary>The class <c>ToneSnip.Windows.Overlay.OverlayWindow</c> registers; not suffixed for the debug build,
    /// for the same reason.</summary>
    private const string OverlayClass = "tonesnip-overlay";

    /// <summary>Seconds waited before giving up, polling once a second.</summary>
    private const int WaitSeconds = 300;

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? className, string? title);

    /// <summary>
    /// Blocks while a snip is in flight, for up to five minutes. True when the desktop is clear; false when it never
    /// cleared, in which case the caller must open nothing and exit 2.
    /// </summary>
    /// <param name="harness">The prefix for log lines ("screenshots", "leaktest").</param>
    internal static bool WaitForIdleDesktop(string harness)
    {
        if (!SnipInFlight()) return true;
        AttachConsole(-1);   // WinExe: reattach to the launching console, as the harnesses themselves do
        Log($"{harness}: a snip is in flight, waiting up to {WaitSeconds} s for the overlay to close");
        for (int second = 0; second < WaitSeconds; second++)
        {
            // Progress goes to the console only, to keep the log readable.
            try { Console.WriteLine($"{harness}: waiting for the overlay to close"); } catch { }
            Thread.Sleep(1000);
            if (!SnipInFlight()) { Log($"{harness}: the overlay closed after {second + 1} s"); return true; }
        }
        Log($"{harness}: the overlay is still open after {WaitSeconds} s, so nothing was opened");
        return false;
    }

    /// <summary>True while the running app has an overlay or a countdown pill on screen.</summary>
    private static bool SnipInFlight()
        => FindWindowExW(IntPtr.Zero, IntPtr.Zero, OverlayClass, null) != IntPtr.Zero
        || FindWindowExW(IntPtr.Zero, IntPtr.Zero, null, CountdownTitle) != IntPtr.Zero;

    /// <summary>To the console and the log. Neither may throw: the guard runs before the app exists, and there may be
    /// no console.</summary>
    private static void Log(string line)
    {
        try { Console.WriteLine(line); } catch { }
        try { new Core.Diagnostics.FileLog(AppPaths.LogPath).Info(line); } catch { }
    }
}
