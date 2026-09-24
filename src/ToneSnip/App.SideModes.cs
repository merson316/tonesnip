#if TONESNIP_HARNESS
using ToneSnip.App.Output;

namespace ToneSnip.App;

/// <summary>The harness side modes (`--screenshots`, `--hold`, `--leaktest`, `--memtest`), which start only what they
/// need: no hook, tray, host pipe or autostart, so they never claim anything the running app owns. Empty in
/// tonesnip.exe.</summary>
public partial class App
{
    /// <summary>True under `--screenshots` and `--hold`: <see cref="ApplySettings"/> updates memory only and skips the
    /// live theme apply, so a held window cannot be observed changing theme.</summary>
    public bool ScreenshotMode { get; private set; }
    /// <summary>True in any harness side mode: nothing writes settings.json.</summary>
    public bool SideMode { get; private set; }

    /// <summary>Starts the side mode the command line asked for, if it needs nothing of the app's own services. True
    /// when one was started, and <see cref="Launch"/> goes no further.</summary>
    private bool StartSideMode()
    {
        if (Command?.ScreenshotDir is { } screenshotDir)
        {
            ScreenshotMode = SideMode = true;
            History = new SnipHistory(Log, () => Settings.ResolvedSaveFolder(AppPaths.Pictures), () => Settings.DeleteToRecycleBin);   // read-only here
            _ = Screenshots.Run(screenshotDir, Command.ScreenshotTheme);
            return true;
        }

        if (Command?.HoldWindow is { } holdWindow)
        {
            ScreenshotMode = SideMode = true;
            History = new SnipHistory(Log, () => Settings.ResolvedSaveFolder(AppPaths.Pictures), () => Settings.DeleteToRecycleBin);
            _ = Screenshots.Hold(holdWindow, Command.HoldSeconds, Command.ScreenshotTheme, Command.HoldFlipTheme);
            return true;
        }

        if (Command?.LeakTest == true)
        {
            SideMode = true;
            History = new SnipHistory(Log, () => Settings.ResolvedSaveFolder(AppPaths.Pictures), () => Settings.DeleteToRecycleBin);
            _ = LeakTest.Run();
            return true;
        }
        return false;
    }

    /// <summary>`--memtest`, which needs the grabber and its Grabbed handler but nothing after them. True when it was
    /// started.</summary>
    private bool StartMemTest()
    {
        if (Command?.MemTest == true)
        {
            SideMode = true;
            History = new SnipHistory(Log, () => Settings.ResolvedSaveFolder(AppPaths.Pictures), () => Settings.DeleteToRecycleBin);   // read-only here
            _ = MemTest.Run();
            return true;
        }
        return false;
    }
}
#endif
