using ToneSnip.App.Interop;
using ToneSnip.Core.Diagnostics;

namespace ToneSnip.App;

/// <summary>Logs the private working set (Task Manager's Memory column) and its change since the previous mark at named
/// points of a snip. Enabled by the TONESNIP_MEMPROBE environment variable.</summary>
public static class MemoryProbe
{
    public static ILog? Log { get; set; }
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("TONESNIP_MEMPROBE") != null;
    private static long _last;

    public static void Mark(string stage)
    {
        if (Log == null || !Enabled) return;
        long now = ProcessMemory.PrivateWorkingSet();
        long delta = _last == 0 ? 0 : now - _last;
        _last = now;
        Log.Info($"mem[{stage}]: task manager {now / 1_048_576} MB ({(delta >= 0 ? "+" : "")}{delta / 1_048_576} MB), managed {GC.GetTotalMemory(false) / 1_048_576} MB");
    }
}
