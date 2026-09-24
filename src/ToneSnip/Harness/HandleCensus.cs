using System.Runtime.InteropServices;

namespace ToneSnip.App;

/// <summary>This process's open handles counted by kernel object type (Event, Thread, Composition...), so --memtest
/// and --leaktest can show what a growing handle count is made of.</summary>
internal static partial class HandleCensus
{
    [LibraryImport("ntdll.dll")] private static partial int NtQueryObject(IntPtr handle, int infoClass, IntPtr info, int length, out int returned);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetHandleInformation(IntPtr handle, out uint flags);

    public static Dictionary<string, int> Take()
    {
        var counts = new Dictionary<string, int>();
        IntPtr buffer = Marshal.AllocHGlobal(1024);
        try
        {
            for (long v = 4; v < 40000; v += 4)
            {
                var h = (IntPtr)v;
                if (!GetHandleInformation(h, out _)) continue;
                if (NtQueryObject(h, 2 /*ObjectTypeInformation*/, buffer, 1024, out _) != 0) continue;
                // PUBLIC_OBJECT_TYPE_INFORMATION starts with a UNICODE_STRING: Length, MaximumLength, then the buffer.
                int length = Marshal.ReadInt16(buffer);
                IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                string type = Marshal.PtrToStringUni(text, length / 2) ?? "?";
                counts[type] = counts.GetValueOrDefault(type) + 1;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return counts;
    }

    /// <summary>"Event +3, Composition +1": the types whose count moved between two censuses.</summary>
    public static string Diff(Dictionary<string, int> before, Dictionary<string, int> after)
    {
        var moved = after.Keys.Union(before.Keys)
            .Select(k => (Type: k, Delta: after.GetValueOrDefault(k) - before.GetValueOrDefault(k)))
            .Where(d => d.Delta != 0).OrderByDescending(d => d.Delta);
        string text = string.Join(", ", moved.Select(d => $"{d.Type} {(d.Delta > 0 ? "+" : "")}{d.Delta}"));
        return text.Length == 0 ? "none" : text;
    }
}
