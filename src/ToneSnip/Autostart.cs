using System.Runtime.InteropServices;
using ToneSnip.Core.Diagnostics;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace ToneSnip.App;

/// <summary>
/// "Start with Windows": the per-user Run key for the portable exe, the package's <c>ToneSnipStartup</c>
/// StartupTask for the MSIX build. If the user has disabled the task in Windows Settings, the request is logged and
/// left alone.
/// <para>Compiled out of the debug build: the Run value and the task are shared by every ToneSnip on the machine,
/// so a debug build must not be able to change them.</para>
/// </summary>
public static class Autostart
{
#if !TONESNIP_HARNESS
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "ToneSnip";
    private const string TaskId = "ToneSnipStartup";
#endif

    /// <summary>Set once at startup so the fire-and-forget packaged path has somewhere to report to.</summary>
    public static ILog? Log { get; set; }

    /// <summary>True when the process runs from the MSIX package rather than as the portable exe.</summary>
    public static bool IsPackaged { get; } = ProbePackaged();

    public static bool IsEnabled()
    {
#if TONESNIP_HARNESS
        // Whatever is registered belongs to the production app, not this build.
        return false;
#else
        if (!IsPackaged)
        {
            using RegistryKey? k = Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue(Name) is string;
        }
        try
        {
            // Off the caller's thread: StartupTask is not UI-affine, and the caller may be the UI thread.
            return Task.Run(async () => (await StartupTask.GetAsync(TaskId)).State).GetAwaiter().GetResult() == StartupTaskState.Enabled;
        }
        catch (Exception ex) { Log?.Warn("autostart: reading the startup task failed: " + ex.Message); return false; }
#endif
    }

    /// <summary>
    /// Writes the startup state. A no-op in the debug build, whose fresh settings would otherwise delete or repoint
    /// the installed app's entry; guarding here covers every caller.
    /// </summary>
    public static void Apply(bool enabled, string exePath)
    {
#if TONESNIP_HARNESS
        Log?.Debug($"autostart: not applied ({enabled}) - the debug build never touches the Run key or the StartupTask");
#else
        if (IsPackaged) { _ = ApplyStartupTaskAsync(enabled); return; }   // fire and forget; ApplyStartupTaskAsync logs
        // Policy or security software can lock the Run key; an exception must not escape into OnLaunched or the
        // SettingsChanged listeners.
        try
        {
            using RegistryKey k = Registry.CurrentUser.CreateSubKey(Key)!;
            if (enabled) k.SetValue(Name, $"\"{exePath}\""); else k.DeleteValue(Name, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log?.Warn($"autostart: the Run key could not be {(enabled ? "written" : "cleared")}: {ex.Message}");
        }
#endif
    }

#if !TONESNIP_HARNESS
    private static async Task ApplyStartupTaskAsync(bool enabled)
    {
        try
        {
            StartupTask task = await StartupTask.GetAsync(TaskId);
            if (!enabled)
            {
                if (task.State == StartupTaskState.Enabled) { task.Disable(); Log?.Debug("autostart: startup task disabled"); }
                return;
            }
            if (task.State == StartupTaskState.Enabled) return;
            StartupTaskState state = await task.RequestEnableAsync();
            if (state == StartupTaskState.Enabled) Log?.Debug("autostart: startup task enabled");
            else Log?.Warn($"autostart: Windows refused the startup task ({state}); change it in Settings > Apps > Startup");
        }
        catch (Exception ex) { Log?.Warn("autostart: " + ex.Message); }
    }
#endif

    private const int AppmodelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);

    private static bool ProbePackaged()
    {
        // Cheaper and quieter than catching the exception Package.Current throws when unpackaged: with a zero-length
        // buffer this returns ERROR_INSUFFICIENT_BUFFER inside a package and APPMODEL_ERROR_NO_PACKAGE outside one.
        int length = 0;
        return GetCurrentPackageFullName(ref length, null) != AppmodelErrorNoPackage;
    }
}
