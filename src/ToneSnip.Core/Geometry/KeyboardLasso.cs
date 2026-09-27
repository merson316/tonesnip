namespace ToneSnip.Core.Geometry;

/// <summary>
/// A freeform selection drawn from the keyboard, as a polygon: Space or Enter starts it at the pointer, the arrow keys
/// move the pointer with a straight edge following it from the last corner, Space fixes a corner there, Backspace
/// takes the last one back, and Enter closes the outline back to where it started. The arrow keys can only move
/// straight across or down, so corners are what give a keyboard lasso its diagonals.
/// <para>Physical desktop pixels, like the mouse's lasso, so the snip masks the result the same way.</para>
/// </summary>
public sealed class KeyboardLasso
{
    /// <summary>The fixed corners, then the live end that follows the pointer. Empty while no lasso is being drawn.</summary>
    private readonly List<(int X, int Y)> _points = new();

    /// <summary>True from <see cref="Start"/> until <see cref="Finish"/> or <see cref="Clear"/>.</summary>
    public bool Active => _points.Count > 0;

    /// <summary>The outline as drawn so far: the corners, then the live end at the pointer. Empty when not active.</summary>
    public IReadOnlyList<(int X, int Y)> Path => _points;

    /// <summary>How many corners are fixed, the starting point included.</summary>
    public int Corners => Math.Max(0, _points.Count - 1);

    /// <summary>Starts a new lasso at the pointer, dropping any lasso in progress.</summary>
    public void Start(int x, int y)
    {
        _points.Clear();
        _points.Add((x, y));
        _points.Add((x, y));
    }

    /// <summary>The pointer moved: the live end follows it.</summary>
    public void Move(int x, int y)
    {
        if (Active) _points[^1] = (x, y);
    }

    /// <summary>Fixes a corner at the pointer. False, and nothing changes, when the pointer is still on the last corner.</summary>
    public bool AddCorner()
    {
        if (!Active || _points[^1] == _points[^2]) return false;
        _points.Add(_points[^1]);
        return true;
    }

    /// <summary>Takes back the last fixed corner; the live end stays at the pointer. False when only the starting point
    /// is left, which Escape clears instead.</summary>
    public bool RemoveCorner()
    {
        if (_points.Count <= 2) return false;
        _points.RemoveAt(_points.Count - 2);
        return true;
    }

    /// <summary>
    /// Closes the lasso: its corners and the pointer, with repeated points dropped, as a closed outline (the last point
    /// joins the first when masked). Null, with the lasso kept for more corners, when that is not a shape with an
    /// area: fewer than three distinct points, or all of them on one line.
    /// </summary>
    public List<(int X, int Y)>? Finish()
    {
        var outline = new List<(int X, int Y)>(_points.Count);
        foreach ((int X, int Y) p in _points)
            if (outline.Count == 0 || outline[^1] != p) outline.Add(p);
        if (outline.Count > 1 && outline[^1] == outline[0]) outline.RemoveAt(outline.Count - 1);
        if (outline.Count < 3 || TwiceArea(outline) == 0) return null;
        _points.Clear();
        return outline;
    }

    public void Clear() => _points.Clear();

    /// <summary>Twice the polygon's signed area (the shoelace sum); zero when every point is on one line.</summary>
    private static long TwiceArea(List<(int X, int Y)> p)
    {
        long sum = 0;
        for (int i = 0; i < p.Count; i++)
        {
            (int x1, int y1) = p[i];
            (int x2, int y2) = p[(i + 1) % p.Count];
            sum += (long)x1 * y2 - (long)x2 * y1;
        }
        return sum;
    }
}
