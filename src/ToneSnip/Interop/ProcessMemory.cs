using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ToneSnip.App.Interop;

/// <summary>The memory figure users see: Task Manager's Memory column, the private working set.</summary>
internal static partial class ProcessMemory
{
    /// <summary>This process's private working set in bytes (PROCESS_MEMORY_COUNTERS_EX2), falling back to private
    /// bytes, which also counts non-resident committed pages.</summary>
    public static long PrivateWorkingSet()
    {
        var c = new Counters { Cb = (uint)Marshal.SizeOf<Counters>() };
        if (GetProcessMemoryInfo(Windows.Interop.Kernel32.GetCurrentProcess(), ref c, c.Cb)) return (long)c.PrivateWorkingSetSize;
        using var me = Process.GetCurrentProcess();
        return me.PrivateMemorySize64;
    }

    /// <summary>
    /// Takes every page Windows can take back out of this process's working set (SetProcessWorkingSetSize with -1, -1),
    /// as Windows itself does to an idle process when memory is wanted elsewhere. Nothing is freed: a page used again
    /// comes back from the standby list, usually without a disk read. For an idle tray app this returns the memory the
    /// last snip touched (driver heaps, the WinUI windows' pages) instead of holding it until Windows asks.
    /// </summary>
    public static bool TrimWorkingSet() => SetProcessWorkingSetSize(Windows.Interop.Kernel32.GetCurrentProcess(), -1, -1);

    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);

    [StructLayout(LayoutKind.Sequential)]
    private struct Counters
    {
        public uint Cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage, PrivateWorkingSetSize, SharedCommitUsage;
    }

    [LibraryImport("psapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(IntPtr process, ref Counters counters, uint size);
}
