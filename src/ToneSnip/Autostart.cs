using System.Runtime.InteropServices;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Startup;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace ToneSnip.App;

/// <summary>
/// "Start with Windows": the per-user Run key for the portable exe, the package's <c>ToneSnipStartup</c>
/// StartupTask for the MSIX build. If the user has disabled the task in Windows Settings, the request is logged and
/// left alone.
/// <para>An installed package owns startup (<see cref="StartupOwnership"/>): the two builds share one settings.json, so
/// one "Start with Windows" used to arm the Run key and the StartupTask both, and the boot launch that lost the
/// single-instance mutex forwarded an empty command line, which opens Settings.</para>
/// <para>Compiled out of the debug build: the Run value and the task are shared by every ToneSnip on the machine,
/// so a debug build must not be able to change them.</para>
/// </summary>
public static partial class Autostart
{
#if !TONESNIP_HARNESS
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "ToneSnip";
    private const string TaskId = "ToneSnipStartup";

    /// <summary>
    /// The MSIX's package family name: <c>Name</c> plus the hash of <c>Publisher</c> from
    /// packaging/Package.appxmanifest. Needed as a literal because an unpackaged process has no identity to ask.
    /// <para>If the publisher ever changes this goes stale, and the only cost is the old behaviour: a portable copy
    /// writes the Run key again and the next packaged launch clears it.</para>
    /// </summary>
    private const string PackageFamily = "merson316.ToneSnip_hkka9fdy79wv2";

    /// <summary>
    /// Where the app model records this user's startup task states. Not a documented layout, so a missing value and a
    /// failed read are both accounted for; it is the only way to see the package's task from outside the package, since
    /// <c>StartupTask.GetAsync</c> needs an identity the portable exe does not have.
    /// </summary>
    private const string TaskStateKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\"
        + PackageFamily + @"\" + TaskId;

    private const string TaskStateValue = "State";
#endif

    /// <summary>The switch on a launch Windows makes on the app's behalf, such as the Run key's: start in the tray and
    /// ask the running instance for nothing. See <see cref="CommandLine"/>.</summary>
    public const string BackgroundSwitch = "--background";

    /// <summary>Set once at startup so the fire-and-forget packaged path has somewhere to report to.</summary>
    public static ILog? Log { get; set; }

    /// <summary>True when the process runs from the MSIX package rather than as the portable exe.</summary>
    public static bool IsPackaged { get; } = ProbePackaged();

#if !TONESNIP_HARNESS
    /// <summary>
    /// Which mechanism may be touched from this process. Asked afresh every time rather than cached: installing the
    /// MSIX beside a portable copy that is already in the tray is the ordinary upgrade, and a stale answer would have
    /// that copy reconcile against the Run key the installer just cleared and write "off" into the shared
    /// settings.json, disabling the installed app's startup task. The probe is one kernel32 call.
    /// </summary>
    private static StartupOwner Owner()
    {
        // A packaged process needs neither probe: the rule gives its own task the first say.
        bool installedElsewhere = !IsPackaged && ProbePackageInstalled();
        return StartupOwnership.For(IsPackaged, installedElsewhere, installedElsewhere && ProbePackageClaimsStartup());
    }
#endif

    /// <param name="stored">The setting as settings.json has it, returned when nothing on the machine can be read for
    /// an answer (<see cref="StartupOwnership.StoredSettingIsAuthoritative"/>).</param>
    public static bool IsEnabled(bool stored)
    {
#if TONESNIP_HARNESS
        // Whatever is registered belongs to the production app, not this build.
        return false;
#else
        StartupOwner owner = Owner();
        if (StartupOwnership.StoredSettingIsAuthoritative(owner)) return stored;
        if (StartupOwnership.RunKeyFollowsSetting(owner))
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
        // Only the copy the Run key belongs to writes it; every other copy just keeps it clear, so the two mechanisms
        // can never both be armed.
        StartupOwner owner = Owner();
        if (!StartupOwnership.RunKeyFollowsSetting(owner)) ClearRunKey(owner);

        if (owner == StartupOwner.InstalledPackage)
        {
            // Nothing else to do from out here: an unpackaged process cannot reach another package's StartupTask. The
            // shared settings.json carries the setting, and the installed app applies it on its next launch.
            Log?.Debug("autostart: the installed ToneSnip package owns startup; this copy left it alone");
            return;
        }
        if (owner == StartupOwner.ThisPackage) { _ = ApplyStartupTaskAsync(enabled); return; }   // fire and forget; ApplyStartupTaskAsync logs

        // Policy or security software can lock the Run key; an exception must not escape into OnLaunched or the
        // SettingsChanged listeners.
        try
        {
            using RegistryKey k = Registry.CurrentUser.CreateSubKey(Key)!;
            // With --background, a boot launch that loses the race to a launch by the user exits quietly rather than
            // opening Settings. Written on every Apply, so an entry from an older build without it is brought up to date.
            if (enabled) k.SetValue(Name, $"\"{exePath}\" {BackgroundSwitch}"); else k.DeleteValue(Name, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log?.Warn($"autostart: the Run key could not be {(enabled ? "written" : "cleared")}: {ex.Message}");
        }
#endif
    }

#if !TONESNIP_HARNESS
    /// <summary>
    /// Removes a Run entry this copy does not own, and says so at Info: it is rare, it changes what happens at the next
    /// boot, and the production log keeps nothing below Info.
    /// <para>The re-read catches a delete that did not take, such as a locked key. It reads the real hive from the
    /// package too: the manifest claims <c>unvirtualizedResources</c>, which is also why both builds share one
    /// settings.json.</para>
    /// </summary>
    private static void ClearRunKey(StartupOwner owner)
    {
        try
        {
            // Read-only first. Asking for write access on a machine whose Run key is locked by policy throws, and the
            // ordinary case has nothing to remove, so opening it writable up front would warn on every launch about an
            // entry that was never there.
            using (RegistryKey? read = Registry.CurrentUser.OpenSubKey(Key))
            {
                if (read?.GetValue(Name) == null) return;   // the ordinary case: nothing registered, nothing to report
            }
            using RegistryKey? k = Registry.CurrentUser.OpenSubKey(Key, writable: true);
            if (k == null) return;
            k.DeleteValue(Name, throwOnMissingValue: false);
            string why = owner == StartupOwner.ThisPackage
                ? "this package's startup task owns startup"
                : "the installed ToneSnip package owns startup";
            if (k.GetValue(Name) == null) Log?.Info($"autostart: removed a portable copy's Run entry - {why}");
            else Log?.Warn("autostart: a portable copy's Run entry could not be removed; ToneSnip will start twice at boot - remove \"ToneSnip\" from Settings > Apps > Startup");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log?.Warn("autostart: a portable copy's Run entry could not be removed: " + ex.Message);
        }
    }

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
    private const int ErrorInsufficientBuffer = 122;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetCurrentPackageFullName(ref int packageFullNameLength, [Out] char[]? packageFullName);

    private static bool ProbePackaged()
    {
        // Cheaper and quieter than catching the exception Package.Current throws when unpackaged: with a zero-length
        // buffer this returns ERROR_INSUFFICIENT_BUFFER inside a package and APPMODEL_ERROR_NO_PACKAGE outside one.
        int length = 0;
        return GetCurrentPackageFullName(ref length, null) != AppmodelErrorNoPackage;
    }

#if !TONESNIP_HARNESS
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetPackagesByPackageFamily(string packageFamilyName, ref int count,
                                                          [Out] IntPtr[]? packageFullNames, ref int bufferLength, [Out] char[]? buffer);

    /// <summary>
    /// True when the MSIX is registered for this user, asked the same zero-length-buffer way as
    /// <see cref="ProbePackaged"/>: the count comes back whether or not there was room for the names. Works without
    /// package identity, so the portable exe can tell that the installed app is there.
    /// </summary>
    private static bool ProbePackageInstalled()
    {
        try
        {
            int count = 0, characters = 0;
            int rc = GetPackagesByPackageFamily(PackageFamily, ref count, null, ref characters, null);
            return (rc == 0 || rc == ErrorInsufficientBuffer) && count > 0;
        }
        // Missing on a Windows without the app model API set; treat that as no package rather than failing startup.
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log?.Warn("autostart: the installed package could not be looked up: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// True when the installed package is arranging startup, or has been told not to: either way its StartupTask is
    /// its own business. False when nothing has ever touched the task, which is how the MSIX ships it - then a portable
    /// copy owns the Run key, because deferring would leave the switch on and nothing starting at boot.
    /// </summary>
    private static bool ProbePackageClaimsStartup()
    {
        try
        {
            using RegistryKey? k = Registry.CurrentUser.OpenSubKey(TaskStateKey);
            if (k?.GetValue(TaskStateValue) is not int state) return false;   // no record: the task is as shipped, disabled
            // A veto in Windows Settings (DisabledByUser) counts: a portable copy must not answer it by arranging
            // startup again behind the user's back.
            return (StartupTaskState)state != StartupTaskState.Disabled;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Unreadable: leave startup to the package rather than risk arming a second entry beside it.
            Log?.Warn("autostart: the installed package's startup task could not be read, so it keeps startup: " + ex.Message);
            return true;
        }
    }
#endif
}
