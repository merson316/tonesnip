using System.Runtime.InteropServices;

namespace ToneSnip.Windows.Interop;

/// <summary>The kernel32 imports used in more than one place, declared once and source-generated.</summary>
public static partial class Kernel32
{
    private const string Dll = "kernel32.dll";

    /// <summary>ATTACH_PARENT_PROCESS: the console of the process that started this one.</summary>
    public const int AttachParentProcess = -1;

    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool AttachConsole(int pid);
    [LibraryImport(Dll)] public static partial uint GetCurrentThreadId();
    /// <summary>A pseudo-handle for this process, which needs no closing.</summary>
    [LibraryImport(Dll)] public static partial IntPtr GetCurrentProcess();
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)] public static partial IntPtr GetModuleHandleW(string? name);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)] public static partial uint GetDriveTypeW(string rootPathName);
}
