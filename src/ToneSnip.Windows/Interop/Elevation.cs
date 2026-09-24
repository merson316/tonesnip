using System.Runtime.InteropServices;

namespace ToneSnip.Windows.Interop;

/// <summary>Whether a process runs elevated, for the window snips, which capture an elevated window from the screen
/// rather than from the window itself.</summary>
public static partial class Elevation
{
    private const uint ProcessQueryLimitedInformation = 0x1000, TokenQuery = 0x0008;
    private const int TokenElevationClass = 20;

    [LibraryImport("kernel32.dll")] private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseHandle(IntPtr handle);
    [LibraryImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [LibraryImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

    /// <summary>This process runs elevated.</summary>
    public static bool Self { get; } = Of(Kernel32.GetCurrentProcess(), own: false) == true;

    /// <summary>Whether process <paramref name="pid"/> runs elevated; null when that cannot be read, which for another
    /// user's or a protected process is expected.</summary>
    public static bool? OfProcess(uint pid)
    {
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) return null;
        return Of(process, own: true);
    }

    private static bool? Of(IntPtr process, bool own)
    {
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out IntPtr token)) return null;
            try { return GetTokenInformation(token, TokenElevationClass, out int elevated, sizeof(int), out _) ? elevated != 0 : null; }
            finally { CloseHandle(token); }
        }
        finally { if (own) CloseHandle(process); }
    }
}
