using System.Diagnostics;
using System.Runtime.InteropServices;
using ToneSnip.Core.Config;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Windows.Interop;

namespace ToneSnip.Windows;

/// <summary>
/// The only way this app hands a stored path to the Windows shell: Explorer for the reveal calls, and
/// <see cref="Recycle"/> for a delete the user can undo.
/// <para>
/// Arguments are quoted by <see cref="ExplorerArguments.Folder"/> and <see cref="ExplorerArguments.Select"/>, which
/// refuse a path a quote could escape, and <c>UseShellExecute</c> stays false so a URL or executable in
/// <c>settings.json</c> cannot be launched in place of a folder.
/// </para>
/// Every path is validated by <see cref="PathGuard"/> and must exist; anything else is a logged no-op, because the
/// callers are click handlers where a stale row is ordinary.
/// </summary>
public static partial class Shell
{
    /// <summary>Opens Explorer on the file's folder with the file selected. No-op unless the path is a safe absolute
    /// path naming a file that exists.</summary>
    public static void Reveal(string? path, ILog? log = null)
    {
        if (!PathGuard.IsSafeAbsolute(path)) { log?.Warn($"reveal refused (not a safe absolute path): {PathGuard.Describe(path)}"); return; }
        if (!File.Exists(path)) { log?.Debug("reveal skipped (file is gone): " + path); return; }
        // Not ArgumentList: it quotes "/select,path" whole when the path has a space, and Explorer then ignores the
        // switch and opens its default folder (ExplorerArguments.Select).
        if (ExplorerArguments.Select(path!) is not { } arguments) { log?.Warn("reveal refused (the path cannot be quoted): " + PathGuard.Describe(path)); return; }
        Start(psi => psi.Arguments = arguments, "reveal", log);
    }

    /// <summary>Opens Explorer on a folder. No-op unless the folder is a safe absolute path that exists.</summary>
    public static void OpenFolder(string? folder, ILog? log = null)
    {
        if (!PathGuard.IsSafeAbsolute(folder)) { log?.Warn($"open folder refused (not a safe absolute path): {PathGuard.Describe(folder)}"); return; }
        if (!Directory.Exists(folder)) { log?.Debug("open folder skipped (folder is gone): " + folder); return; }
        // Not ArgumentList: it leaves a path without spaces unquoted, and Explorer splits its command line at commas
        // (ExplorerArguments.Folder).
        if (ExplorerArguments.Folder(folder!) is not { } arguments) { log?.Warn("open folder refused (the path cannot be quoted): " + PathGuard.Describe(folder)); return; }
        Start(psi => psi.Arguments = arguments, "open folder", log);
    }

    /// <summary>Whether a delete to the Recycle Bin can land there (<see cref="RecycleBin.Covers"/>): a fixed local
    /// drive. Never throws.</summary>
    public static bool RecycleBinCovers(string? path)
    {
        try { return RecycleBin.Covers(path, root => (int)Kernel32.GetDriveTypeW(root)); }
        catch { return false; }
    }


    /// <summary>
    /// Moves one file to the Recycle Bin. True when the file is gone from <paramref name="path"/> afterwards (including
    /// when it was already gone), verified by checking the path; false when the shell refused, with the reason logged.
    /// Directories are refused.
    /// <para>
    /// <b>The caller must not fall back to a permanent delete on false:</b> the user asked for a recoverable delete
    /// (<c>SnipSettings.DeleteToRecycleBin</c>).
    /// </para>
    /// <para>
    /// <c>FOF_NOCONFIRMATION</c> also suppresses the "too big for the Recycle Bin" prompt, so a file the bin cannot hold
    /// is deleted permanently and this still returns true.
    /// </para>
    /// </summary>
    public static bool Recycle(string? path, ILog? log = null)
    {
        if (!PathGuard.IsSafeAbsolute(path)) { log?.Warn($"recycle refused (not a safe absolute path): {PathGuard.Describe(path)}"); return false; }
        // File.Exists is false for a folder, which would otherwise be reported as "already gone".
        if (Directory.Exists(path)) { log?.Warn("recycle refused (that is a folder, not a file): " + PathGuard.Describe(path)); return false; }
        if (!File.Exists(path)) return true;
        // pFrom is a double-null-terminated list: StringToHGlobalUni writes one terminator, the appended "\0" is the other.
        IntPtr from = Marshal.StringToHGlobalUni(path + "\0");
        try
        {
            var op = new SHFILEOPSTRUCTW
            {
                wFunc = FO_DELETE,
                pFrom = from,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };
            // The return is a DE_* shell code, not a Win32 error, so it is logged as a raw number.
            int result = SHFileOperationW(ref op);
            if (result == 0 && op.fAnyOperationsAborted == 0)
            {
                if (!File.Exists(path)) return true;
                log?.Warn($"recycle reported success for '{path}' but the file is still there");
                return false;
            }
            log?.Warn($"recycle failed for '{path}': SHFileOperation returned 0x{result:X} aborted={op.fAnyOperationsAborted != 0}");
            return false;
        }
        // A shell that is not running, or a policy that blocks the operation, must not take a click handler down.
        catch (Exception e) { log?.Warn($"recycle failed for '{path}': {e.Message}"); return false; }
        finally { Marshal.FreeHGlobal(from); }
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    /// <summary>SHFILEOPSTRUCTW. Default packing, not <c>Pack = 1</c>: it matches the native layout on win-x64, the only
    /// target. The string fields are IntPtr because <c>pFrom</c> needs two terminators (see <see cref="Recycle"/>).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public IntPtr pFrom;
        public IntPtr pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public IntPtr lpszProgressTitle;
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHFileOperationW(ref SHFILEOPSTRUCTW fileOp);

    private static void Start(Action<ProcessStartInfo> arguments, string what, ILog? log)
    {
        try
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            arguments(psi);
            Process.Start(psi);
        }
        // Explorer failing to launch (a locked-down machine, no shell running) must not crash the calling UI handler.
        catch (Exception e) { log?.Warn($"{what}: {e.Message}"); }
    }
}
