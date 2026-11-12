using System.Diagnostics;
using System.Runtime.InteropServices;
using ToneSnip.Core.Geometry;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace ToneSnip.Windows.Interop;

/// <summary>
/// The parts of a window that has no child windows (Firefox, a WinUI or WPF app), read from its UI Automation tree:
/// the panes, lists and pages <see cref="UiParts"/> picks, as screen rectangles for <see cref="WindowLayers.WithParts"/>.
/// <para>Unlike <see cref="WindowFinder.Layers"/> this asks the other process, which can be slow or hung, so it is
/// bounded three ways and must run off the UI thread: every call into the app times out after
/// <see cref="CallTimeoutMs"/>, the walk stops after <see cref="BudgetMs"/> in all, and it reads at most
/// <see cref="MaxExpanded"/> elements' children. The walk is breadth-first, so what a budget cuts off is the
/// deepest and smallest parts, never the outer panes.</para>
/// <para>The first query makes the app build its accessibility tree, which a browser keeps up to date from then on
/// at some cost to itself; so this is only asked of a window whose child windows offer no parts.</para>
/// </summary>
public static class UiaTree
{
    public const uint CallTimeoutMs = 250;
    public const int BudgetMs = 400, MaxExpanded = 150, MaxDepth = 12, MaxChildren = 200;

    /// <summary>The parts inside <paramref name="frame"/>, the window's visible frame; empty when the app exposes none,
    /// refuses (an elevated window) or does not answer in time. <paramref name="note"/> says what was read, for the
    /// log. Never throws. Any thread but the UI thread.</summary>
    public static List<IntRect> Parts(IntPtr hwnd, IntRect frame, out string note)
    {
        var parts = new List<IntRect>();
        var watch = Stopwatch.StartNew();
        int expanded = 0, seen = 0;
        string ended = "whole tree";
        IUIAutomation2? uia = null;
        try
        {
            uia = (IUIAutomation2)new CUIAutomation8();
            uia.ConnectionTimeout = CallTimeoutMs;
            uia.TransactionTimeout = CallTimeoutMs;
            IUIAutomationCacheRequest cache = uia.CreateCacheRequest();
            cache.AddProperty(UIA_PROPERTY_ID.UIA_BoundingRectanglePropertyId);
            cache.AddProperty(UIA_PROPERTY_ID.UIA_ControlTypePropertyId);
            cache.AddProperty(UIA_PROPERTY_ID.UIA_IsOffscreenPropertyId);
            IUIAutomationCondition view = uia.ControlViewCondition;

            var queue = new Queue<(IUIAutomationElement Element, int Depth)>();
            queue.Enqueue((uia.ElementFromHandleBuildCache((HWND)hwnd, cache), 0));
            while (queue.Count > 0)
            {
                (IUIAutomationElement element, int depth) = queue.Dequeue();
                if (expanded >= MaxExpanded) { ended = $"stopped at {MaxExpanded} elements read"; Marshal.ReleaseComObject(element); continue; }
                if (watch.ElapsedMilliseconds >= BudgetMs) { ended = $"stopped at {BudgetMs} ms"; Marshal.ReleaseComObject(element); continue; }
                expanded++;
                try
                {
                    IUIAutomationElementArray? children = element.FindAllBuildCache(TreeScope.TreeScope_Children, view, cache);
                    int count = children == null ? 0 : Math.Min(children.Length, MaxChildren);
                    for (int i = 0; i < count; i++)
                    {
                        IUIAutomationElement child = children!.GetElement(i);
                        seen++;
                        RECT r = child.CachedBoundingRectangle;
                        var e = new UiElement(IntRect.FromLtrb(r.left, r.top, r.right, r.bottom), (int)child.CachedControlType, child.CachedIsOffscreen);
                        if (UiParts.IsPart(e, frame)) parts.Add(e.Bounds);
                        if (depth + 1 < MaxDepth && UiParts.HasPartsInside(e, frame)) queue.Enqueue((child, depth + 1));
                        else Marshal.ReleaseComObject(child);
                    }
                    if (children != null) Marshal.ReleaseComObject(children);
                }
                catch (COMException) { ended = "some elements did not answer"; }   // gone, or timed out: its siblings may still answer
                finally { Marshal.ReleaseComObject(element); }
            }
        }
        catch (Exception e) when (e is COMException or InvalidCastException or UnauthorizedAccessException or TimeoutException)
        {
            ended = "failed: " + e.Message;
        }
        finally
        {
            if (uia != null) Marshal.FinalReleaseComObject(uia);
        }
        note = $"{parts.Count} parts from {seen} elements ({expanded} read) in {watch.ElapsedMilliseconds} ms, {ended}";
        return parts;
    }
}
