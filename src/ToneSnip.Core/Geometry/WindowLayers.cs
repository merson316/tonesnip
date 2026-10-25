namespace ToneSnip.Core.Geometry;

/// <summary>One child window as Windows reports it, in screen pixels: its window rectangle, its client area (which
/// clips its own children), and the index of its parent in the same list, or -1 for the top-level window.</summary>
public readonly record struct ChildWindow(IntRect Bounds, IntRect Client, int Parent);

/// <summary>
/// The rectangles window mode can offer under the pointer for one top-level window: the window itself and the child
/// windows inside it, such as a browser's page under its tabs and toolbar. Each child shows only where its parent's
/// client area does, and its parent shows, so its visible part is clipped by every ancestor and the window's frame.
/// </summary>
public sealed class WindowLayers
{
    /// <summary>How close, in pixels on every side, a rectangle may come to one around it and still be the same step.
    /// Windows 11 draws a 1 px border inside a window's frame, so a child that fills the client area (Chrome's and
    /// Discord's compositing layer, Terminal's content) is the frame less 1 px.</summary>
    public const int SameStep = 3;

    private readonly IntRect _frame;
    private readonly IntRect[] _visible;

    /// <param name="frame">The top-level window's visible frame (DWM's extended frame bounds).</param>
    /// <param name="client">Its client area, which clips its direct children.</param>
    /// <param name="children">Its descendants, each parent listed before its children, as EnumChildWindows walks them.
    /// A child whose parent is not earlier in the list is left out.</param>
    public WindowLayers(IntRect frame, IntRect client, IReadOnlyList<ChildWindow> children)
    {
        _frame = frame;
        _visible = new IntRect[children.Count];
        for (int i = 0; i < children.Count; i++)
        {
            ChildWindow c = children[i];
            IntRect area;
            if (c.Parent < 0) area = client.Intersect(frame);
            else if (c.Parent < i && !_visible[c.Parent].IsEmpty) area = children[c.Parent].Client.Intersect(_visible[c.Parent]);
            else area = IntRect.Empty;
            _visible[i] = c.Bounds.Intersect(area);
        }
    }

    /// <summary>The window's frame alone, for a window with no children or with child detection off.</summary>
    public WindowLayers(IntRect frame) : this(frame, IntRect.Empty, Array.Empty<ChildWindow>()) { }

    public IntRect Frame => _frame;

    /// <summary>
    /// The rectangles under (<paramref name="x"/>, <paramref name="y"/>), smallest first and the frame last, each once:
    /// a child that covers all of its parent (a browser's compositing layer over the whole window), or all but
    /// <see cref="SameStep"/> pixels of it, adds no step; the outer one stays, so the wheel never moves by a border. The
    /// smallest is the one the pointer means, as ShareX picks it; the rest are the steps out to the whole window.
    /// Sibling order is ignored on purpose: some apps lay an invisible full-size child over their content, and picking
    /// the front-most child would hide the page behind it. Fills <paramref name="into"/>, which is cleared first;
    /// empty when the point is outside the frame. A loop over an array: this runs on every mouse move.
    /// </summary>
    public void At(int x, int y, List<IntRect> into)
    {
        into.Clear();
        if (!_frame.Contains(x, y)) return;
        foreach (IntRect r in _visible)
            if (r.Contains(x, y) && r != _frame && !into.Contains(r)) into.Add(r);
        into.Sort(static (a, b) => ((long)a.Width * a.Height).CompareTo((long)b.Width * b.Height));
        into.Add(_frame);
        for (int i = into.Count - 2; i >= 0; i--)
            if (Hugs(into[i], into[i + 1])) into.RemoveAt(i);
    }

    /// <summary>True when <paramref name="inner"/> lies inside <paramref name="outer"/> within <see cref="SameStep"/>
    /// pixels of each of its edges.</summary>
    private static bool Hugs(IntRect inner, IntRect outer)
        => inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom
           && inner.Left - outer.Left <= SameStep && inner.Top - outer.Top <= SameStep
           && outer.Right - inner.Right <= SameStep && outer.Bottom - inner.Bottom <= SameStep;
}
