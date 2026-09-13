using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ToneSnip.App.Interop;

/// <summary>The memory figure users see: Task Manager's Memory column, the private working set.</summary>
internal static class ProcessMemory
{
    /// <summary>This process's private working set in bytes (PROCESS_MEMORY_COUNTERS_EX2), falling back to private
    /// bytes, which also counts non-resident committed pages.</summary>
    public static long PrivateWorkingSet()
    {
        using var me = Process.GetCurrentProcess();
        var c = new Counters { Cb = (uint)Marshal.SizeOf<Counters>() };
        return GetProcessMemoryInfo(me.Handle, ref c, c.Cb) ? (long)c.PrivateWorkingSetSize : me.PrivateMemorySize64;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Counters
    {
        public uint Cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage, PrivateWorkingSetSize, SharedCommitUsage;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref Counters counters, uint size);
}
