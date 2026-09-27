using System.Runtime.InteropServices;

namespace ToneSnip.App;

/// <summary>
/// This process's large committed allocations, read with VirtualQuery: every reservation holding at least
/// <see cref="MinBytes"/> of committed private or mapped memory. A frame-sized buffer shows up here whoever allocated it
/// (the managed heap, the graphics driver, WIC), so --memtest can say what appeared when private bytes jump.
/// </summary>
internal static partial class RegionCensus
{
    internal const long MinBytes = 8L << 20;

    /// <param name="Base">The reservation's base address.</param>
    /// <param name="Committed">Bytes committed in it.</param>
    /// <param name="Private">Private memory rather than a mapped view.</param>
    internal readonly record struct Region(IntPtr Base, long Committed, bool Private);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public UIntPtr RegionSize;
        public uint State, Protect, Type;
    }

    private const uint MemCommit = 0x1000, MemPrivate = 0x20000, MemMapped = 0x40000;

    [LibraryImport("kernel32.dll")]
    private static partial UIntPtr VirtualQuery(IntPtr address, out MemoryBasicInformation info, UIntPtr length);

    /// <summary>The large allocations, by base address.</summary>
    public static Dictionary<IntPtr, Region> Take()
    {
        var found = new Dictionary<IntPtr, Region>();
        long at = 0;
        var size = (UIntPtr)Marshal.SizeOf<MemoryBasicInformation>();
        while (VirtualQuery(new IntPtr(at), out MemoryBasicInformation m, size) != UIntPtr.Zero)
        {
            long length = (long)m.RegionSize, end = (long)m.BaseAddress + length;
            if (m.State == MemCommit && m.Type is MemPrivate or MemMapped)
            {
                found.TryGetValue(m.AllocationBase, out Region r);
                found[m.AllocationBase] = new Region(m.AllocationBase, r.Committed + length, m.Type == MemPrivate);
            }
            if (end <= at) break;
            at = end;
        }
        return found.Where(k => k.Value.Committed >= MinBytes).ToDictionary(k => k.Key, k => k.Value);
    }
}
