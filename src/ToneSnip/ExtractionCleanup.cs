using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Startup;

namespace ToneSnip.App;

/// <summary>
/// Deletes the extraction folders that earlier builds of tonesnip.exe left under %TEMP%\.net\tonesnip\
/// (<see cref="ExtractionSweep"/>), a minute after startup so it never competes with the cold start.
/// <para>Called only by the instance that holds the single-instance mutex, so no other build of tonesnip.exe is
/// running from a folder it deletes. A folder still in use anyway (a second launch forwarding its command) holds
/// loaded DLLs, fails to delete, and is logged and left; the host re-extracts any file missing from its own folder
/// on the next start.</para>
/// <para>Does nothing in the debug build: harness modes run before the mutex, so several tonesnip-debug.exe builds can
/// run at once from sibling folders.</para>
/// </summary>
internal static class ExtractionCleanup
{
    private static readonly TimeSpan Delay = TimeSpan.FromMinutes(1);

    public static void Start(FileLog log)
    {
#if !TONESNIP_HARNESS
        string? root = ExtractionSweep.BundleRoot(AppContext.BaseDirectory, Path.GetFileNameWithoutExtension(AppPaths.ExePath));
        if (root == null) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(Delay).ConfigureAwait(false);
            try
            {
                var siblings = new DirectoryInfo(root).EnumerateDirectories().Select(d => (d.FullName, d.LastWriteTimeUtc));
                foreach (string dir in ExtractionSweep.Stale(AppContext.BaseDirectory, siblings, DateTime.UtcNow))
                {
                    try { Directory.Delete(dir, recursive: true); log.Info($"extraction cleanup: removed {dir}"); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Warn($"extraction cleanup: kept {dir}: {e.Message}"); }
                }
            }
            catch (Exception e) { log.Warn("extraction cleanup: " + e.Message); }
        });
#endif
    }
}
