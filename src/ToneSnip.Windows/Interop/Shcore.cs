using System.Runtime.InteropServices;

namespace ToneSnip.Windows.Interop;

/// <summary>The shcore imports, declared once and source-generated.</summary>
public static partial class Shcore
{
    [LibraryImport("shcore.dll")] public static partial int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
