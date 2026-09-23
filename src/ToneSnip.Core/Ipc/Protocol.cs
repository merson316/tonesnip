namespace ToneSnip.Core.Ipc;

/// <summary>The names two ToneSnip processes use to find each other: the second-launch pipe a later launch forwards its
/// command line through.</summary>
public static class Protocol
{
    /// <summary>
    /// Suffix that keeps tonesnip-debug.exe's pipe, mutex and data folder separate from the installed tonesnip.exe.
    /// Empty in the release build; a compile-time constant so the wrong name can never be chosen at runtime.
    /// </summary>
#if TONESNIP_HARNESS
    public const string InstanceSuffix = "-debug";
#else
    public const string InstanceSuffix = "";
#endif

    /// <summary>
    /// The second-launch pipe for one Windows session. Pipe names are machine-wide while the single-instance mutex is
    /// per session, so the session id keeps each user's launches talking to their own instance.
    /// </summary>
    public static string HostPipeFor(int sessionId) => $"tonesnip{InstanceSuffix}-{sessionId}";
}
