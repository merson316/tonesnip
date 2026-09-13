using ToneSnip.Core.Capture;

namespace ToneSnip.App;

public static class AppPaths
{
    /// <summary>
    /// %LOCALAPPDATA%\tonesnip, or %LOCALAPPDATA%\tonesnip-debug for the debug build
    /// (<see cref="Protocol.InstanceSuffix"/>), so the two never share settings, history or logs.
    /// </summary>
    public static string Dir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tonesnip" + Protocol.InstanceSuffix);
    public static string SettingsPath => Path.Combine(Dir, "settings.json");
    public static string LogPath => Path.Combine(Dir, "tonesnip.log");
    /// <summary>This process's Windows session, which the second-launch pipe's name carries (<see cref="Protocol.HostPipeFor"/>).</summary>
    public static int SessionId { get; } = System.Diagnostics.Process.GetCurrentProcess().SessionId;
    /// <summary>The Pictures known folder, without checking it exists (a GetFolderPath that verifies answers "" for a
    /// deleted or unreachable redirected folder, and "" + "Screenshots" is a relative path), falling back to
    /// %USERPROFILE%\Pictures when the known folder has no path at all.</summary>
    public static string Pictures
    {
        get
        {
            string p = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures, Environment.SpecialFolderOption.DoNotVerify);
            if (!string.IsNullOrWhiteSpace(p)) return p;
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
            return string.IsNullOrWhiteSpace(profile) ? "" : Path.Combine(profile, "Pictures");
        }
    }
    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "tonesnip" + Protocol.InstanceSuffix + ".exe");

    /// <summary>The single-instance mutex, suffixed so a debug build never collides with, or forwards to, the
    /// installed app.</summary>
    public const string InstanceMutex = MutexRoot + Protocol.InstanceSuffix;

    private const string MutexRoot = @"Local\tonesnip";
}
