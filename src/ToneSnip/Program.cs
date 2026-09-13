using System.Diagnostics;
using System.Runtime.InteropServices;
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

    private const int AttachParentProcess = -1;
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);

    /// <summary>
    /// Prints to the parent process's console, if there is one. A <c>WinExe</c> owns no console; without a parent
    /// console (launched from Explorer) the write silently goes nowhere, which is preferable to <c>AllocConsole</c>
    /// opening a window.
    /// <para>The shell does not wait for a WinExe, so the text appears after the next prompt unless launched with
    /// `start /wait` or `Start-Process -Wait`.</para>
    /// </summary>
    private static void Print(string text)
    {
        AttachConsole(AttachParentProcess);
        Console.WriteLine(text);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // Touch the stopwatch first: the class is beforefieldinit, so the static would otherwise start lazily.
        _ = Started.ElapsedMilliseconds;

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
            // A quitting instance closes its pipe before releasing the mutex; if the mutex comes free, stop forwarding
            // and carry on as the first instance.
            if (HostPipe.Forward(args.Length == 0 ? command with { OpenSettings = true } : command, giveUp: () => Acquired(mutex))) return 0;
            if (!Acquired(mutex)) return 0;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { new Core.Diagnostics.FileLog(AppPaths.LogPath).Info("process exit (ProcessExit event)"); } catch { } };
        Application.Start(p =>   // not `_`: the discard below would bind to the parameter instead
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App { Command = command };
        });
        try { new Core.Diagnostics.FileLog(AppPaths.LogPath).Debug("application message loop ended (Application.Start returned)"); } catch { }
        return 0;
    }

    /// <summary>Takes the single-instance mutex if its owner has let go of it (or died holding it).</summary>
    private static bool Acquired(Mutex mutex)
    {
        try { return mutex.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }
    }
}
