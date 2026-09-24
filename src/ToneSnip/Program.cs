using System.Diagnostics;
using ToneSnip.Windows.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ToneSnip.App;

internal static class Program
{
    /// <summary>Started at the first instruction in <see cref="Main"/>; cold-start time (tray icon shown) is measured
    /// against it.</summary>
    public static readonly Stopwatch Started = Stopwatch.StartNew();

    /// <summary>Exit code for an unusable command line: <c>EX_USAGE</c> from BSD <c>sysexits.h</c>. Other codes are
    /// 0 for success, 1 for a failed harness run and 2 for <c>HarnessGuard</c> refusing a busy desktop.</summary>
    private const int UsageExit = 64;

    /// <summary>
    /// Prints to the parent process's console, if there is one. A <c>WinExe</c> owns no console; without a parent
    /// console (launched from Explorer) the write silently goes nowhere, which is preferable to <c>AllocConsole</c>
    /// opening a window.
    /// <para>The shell does not wait for a WinExe, so the text appears after the next prompt unless launched with
    /// `start /wait` or `Start-Process -Wait`.</para>
    /// </summary>
    private static void Print(string text)
    {
        Kernel32.AttachConsole(Kernel32.AttachParentProcess);
        Console.WriteLine(text);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // Touch the stopwatch first: the class is beforefieldinit, so the static would otherwise start lazily.
        _ = Started.ElapsedMilliseconds;
        // Heads every new log file, so a rolled log still names the build and process that wrote it.
        Core.Diagnostics.FileLog.Header = $"ToneSnip {typeof(Program).Assembly.GetName().Version}, pid {Environment.ProcessId}";

        // Parsed before anything with a side effect, so a usage error touches nothing: no log, no mutex.
        StartupCommand command = CommandLine.Parse(args, out IReadOnlyList<string> errors);
        if (CommandLine.WantsHelp(args)) { Print(CommandLine.HelpText()); return 0; }
        if (errors.Count > 0)
        {
            // Refused rather than ignored, so a bad option is never forwarded as an empty command. Only the first
            // fault is reported, like getopt.
            Print($"{errors[0]}{Environment.NewLine}try --help to see what this build accepts");
            return UsageExit;
        }
#if TONESNIP_HARNESS
        // The debug build logs at Debug; the harness modes read the log afterwards. Production defaults to Info.
        Core.Diagnostics.FileLog.Minimum = Core.Diagnostics.LogLevel.Debug;
#endif
#if TONESNIP_HARNESS
        if (args.Contains("--selftest")) return SelfTest.Run(args.Contains("--no-capture"), args.Contains("--strict-hashes"));

        // The harness modes run before the mutex, so they claim and forward nothing and can run beside the installed
        // app. They open real windows, so HarnessGuard first waits out a snip in progress, or exits 2.
        if (command.ScreenshotDir != null)
            return HarnessGuard.WaitForIdleDesktop("screenshots") ? Screenshots.Start(command) : 2;
        if (command.LeakTest)
            return HarnessGuard.WaitForIdleDesktop("leaktest") ? LeakTest.Start(command) : 2;
        if (command.MemTest)
            return HarnessGuard.WaitForIdleDesktop("memtest") ? MemTest.Start(command) : 2;
        if (command.HoldWindow != null)
            return HarnessGuard.WaitForIdleDesktop("hold") ? Screenshots.Start(command) : 2;
#endif
        using var mutex = new Mutex(true, AppPaths.InstanceMutex, out bool first);
        if (!first)
        {
            // Only a launch with no arguments at all opens settings in the running instance. A line that parsed to
            // nothing (such as a Windows activation argument) exits quietly; for an activation, the running instance
            // owns the COM registration and receives it anyway.
            if (args.Length != 0 && command.IsEmpty) return 0;
            // The packaged build's StartupTask launches with no arguments, which would otherwise read as the user asking
            // for Settings; like the Run key's --background, it asks for nothing.
            if (args.Length == 0 && LaunchedByStartupTask()) return 0;
            // A quitting instance closes its pipe before releasing the mutex; if the mutex comes free, stop forwarding
            // and carry on as the first instance.
            if (HostPipe.Forward(args.Length == 0 ? command with { OpenSettings = true } : command, giveUp: () => Acquired(mutex))) return 0;
            if (!Acquired(mutex)) return 0;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        var log = new Core.Diagnostics.FileLog(AppPaths.LogPath);
        ExtractionCleanup.Start(log);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { log.Info("process exit (ProcessExit event)"); } catch { } };
        // XAML's UnhandledException (App.Launch) sees only exceptions on the UI thread's dispatch. These catch the rest:
        // a pool or hook thread that throws kills the process, and each FileLog write is complete on disk when it
        // returns, so the line survives the death that follows.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { log.Error($"unhandled{(e.IsTerminating ? ", the process is ending" : "")}: {e.ExceptionObject}"); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try { log.Error("unobserved task exception: " + e.Exception); } catch { }
            e.SetObserved();
        };
        Application.Start(p =>   // not `_`: the discard below would bind to the parameter instead
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            // A bad XAML resource throws from App's constructor, before OnLaunched and its own fence; unguarded, it
            // escapes into native code and the process vanishes without a line.
            try { _ = new App { Command = command }; }
            catch (Exception e)
            {
                try { log.Error("startup failed: the app could not be created: " + e); } catch { }
                Environment.Exit(1);
            }
        });
        try { log.Debug("application message loop ended (Application.Start returned)"); } catch { }
        return 0;
    }

    /// <summary>
    /// True when Windows started the MSIX through its StartupTask. Asked only of a packaged second instance, which exits
    /// straight after; on any failure it answers false, which keeps the old behaviour of a bare launch.
    /// </summary>
    private static bool LaunchedByStartupTask()
    {
        if (!Autostart.IsPackaged) return false;
        try { return Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs()?.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.StartupTask; }
        catch { return false; }
    }

    /// <summary>Takes the single-instance mutex if its owner has let go of it (or died holding it).</summary>
    private static bool Acquired(Mutex mutex)
    {
        try { return mutex.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }
    }
}
