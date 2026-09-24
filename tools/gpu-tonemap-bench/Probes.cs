using System.Diagnostics;
using System.Runtime.InteropServices;
using ToneSnip.Core.Diagnostics;

namespace GpuTonemapBench;

/// <summary>Task Manager's Memory column: the private working set.</summary>
internal static class ProcessMemory
{
    public static double Mb()
    {
        using var me = Process.GetCurrentProcess();
        var c = new Counters { Cb = (uint)Marshal.SizeOf<Counters>() };
        long b = GetProcessMemoryInfo(me.Handle, ref c, c.Cb) ? (long)c.PrivateWorkingSetSize : me.PrivateMemorySize64;
        return b / 1048576.0;
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

/// <summary>
/// `\GPU Adapter Memory(*)\Dedicated Usage` (and Shared Usage) through PDH: the ADAPTER totals, which are truthful, unlike
/// the per-process `GPU Process Memory` counters that climb on every freed-and-recreated resource (KB4490156).
/// </summary>
internal sealed unsafe class AdapterMemory : IDisposable
{
    private IntPtr _query, _dedicated, _shared;
    public bool Ok { get; }

    public AdapterMemory()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) return;
        if (PdhAddEnglishCounterW(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _dedicated) != 0) return;
        if (PdhAddEnglishCounterW(_query, @"\GPU Adapter Memory(*)\Shared Usage", IntPtr.Zero, out _shared) != 0) return;
        Ok = PdhCollectQueryData(_query) == 0;
    }

    /// <summary>Per adapter instance: (dedicated MB, shared MB).</summary>
    public Dictionary<string, (double Dedicated, double Shared)> Read()
    {
        var r = new Dictionary<string, (double, double)>();
        if (!Ok || PdhCollectQueryData(_query) != 0) return r;
        foreach ((string name, double v) in Array(_dedicated)) r[name] = (v, 0);
        foreach ((string name, double v) in Array(_shared)) if (r.TryGetValue(name, out var d)) r[name] = (d.Item1, v);
        return r;
    }

    /// <summary>The adapter with the most dedicated memory in use (the discrete GPU), in MB.</summary>
    public double MainDedicatedMb()
    {
        var r = Read();
        return r.Count == 0 ? double.NaN : r.Values.Max(v => v.Dedicated);
    }

    private static List<(string, double)> Array(IntPtr counter)
    {
        var list = new List<(string, double)>();
        uint size = 0, count = 0;
        PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref size, out count, IntPtr.Zero);
        if (size == 0) return list;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref size, out count, buf) != 0) return list;
            for (int i = 0; i < count; i++)
            {
                byte* item = (byte*)buf + i * 24;   // { wchar* name; { uint status; pad; double value } }
                string name = Marshal.PtrToStringUni(*(IntPtr*)item) ?? "?";
                double v = *(double*)(item + 16);
                list.Add((name, v / 1048576.0));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    public void Dispose() { if (_query != IntPtr.Zero) PdhCloseQuery(_query); _query = IntPtr.Zero; }

    private const uint PdhFmtDouble = 0x00000200;
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}

internal sealed class ConsoleLog(bool verbose) : ILog
{
    public void Debug(string message) { if (verbose) Console.WriteLine("  [debug] " + message); }
    public void Info(string message) { if (verbose) Console.WriteLine("  [info] " + message); }
    public void Warn(string message) => Console.WriteLine("  [warn] " + message);
    public void Error(string message) => Console.WriteLine("  [error] " + message);
}
