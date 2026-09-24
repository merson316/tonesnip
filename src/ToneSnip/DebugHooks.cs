using System.Diagnostics;
using ToneSnip.Core.Diagnostics;

namespace ToneSnip.App;

/// <summary>
/// The call-outs from production code into the harness. Each method is <c>[Conditional("TONESNIP_HARNESS")]</c>, so
/// in tonesnip.exe the compiler drops every call site together with its arguments, and a call needs no <c>#if</c>
/// around it.
/// <para>This file is compiled into both builds, so it lives outside Harness/ (which production removes, and whose name
/// the production build refuses anywhere else); a body that touches harness code keeps its own <c>#if</c>.</para>
/// </summary>
internal static class DebugHooks
{
    /// <summary>Gives the memory probe the app's log, which it writes its marks to.</summary>
    [Conditional("TONESNIP_HARNESS")]
    public static void AttachMemoryProbe(ILog log)
    {
#if TONESNIP_HARNESS
        MemoryProbe.Log = log;
#endif
    }

    /// <summary>Logs the private working set at a named point of a snip, when TONESNIP_MEMPROBE is set
    /// (<c>MemoryProbe</c>).</summary>
    [Conditional("TONESNIP_HARNESS")]
    public static void MemoryMark(string stage)
    {
#if TONESNIP_HARNESS
        MemoryProbe.Mark(stage);
#endif
    }

    /// <summary>Logs the editor's annotate state and pointer shape for the screenshot harness, which cannot read the
    /// pointer shape through UIA.</summary>
    [Conditional("TONESNIP_HARNESS")]
    public static void EditorAnnotateChanged(bool on, Viewer.EditorSurface surface)
    {
#if TONESNIP_HARNESS
        if (App.Current.ScreenshotMode) App.Current.Log.Debug($"editor: annotate {(on ? "on" : "off")}, tool {surface.Session.Tool}, cursor {surface.CursorName}");
#endif
    }
}
