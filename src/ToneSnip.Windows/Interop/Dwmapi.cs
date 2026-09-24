using System.Runtime.InteropServices;

namespace ToneSnip.Windows.Interop;

/// <summary>The dwmapi imports, declared once and source-generated; <see cref="Dwm"/> holds the helpers built on
/// them.</summary>
public static partial class Dwmapi
{
    private const string Dll = "dwmapi.dll";

    [StructLayout(LayoutKind.Sequential)] public struct Margins { public int Left, Right, Top, Bottom; }

    [LibraryImport(Dll)] public static partial int DwmFlush();
    [LibraryImport(Dll)] public static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [LibraryImport(Dll)] public static partial int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [LibraryImport(Dll)] public static partial int DwmGetWindowAttribute(IntPtr hwnd, int attr, out User32.Rect value, int size);
    [LibraryImport(Dll)] public static partial int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
}
