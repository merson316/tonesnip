using ToneSnip.Core.Geometry;

namespace ToneSnip.Core.Annotate;

/// <summary>Shapes in painter's order plus crop, with snapshot undo/redo. Exposure and zebra are view state, not edits.</summary>
public sealed class AnnotationDoc
{
    public const int UndoDepth = 50;
    /// <summary><see cref="Seq"/> stamps the state with the edit that produced it, so "edited since save" still works
    /// once old snapshots fall off the undo cap.</summary>
    private readonly record struct State(Shape[] Shapes, IntRect Crop, long Seq);
    private State _state = new(Array.Empty<Shape>(), IntRect.Empty, 0);
    private readonly LinkedList<State> _undo = new();
    private readonly Stack<State> _redo = new();
    private int _nextId;

    public IReadOnlyList<Shape> Shapes => _state.Shapes;
    public IntRect Crop => _state.Crop;
    public int? SelectedId { get; private set; }
    /// <summary>Read on every pointer move and every repaint, so it walks the array rather than allocating a query.</summary>
    public Shape? Selected => SelectedId is int id && IndexOf(id) is var i and >= 0 ? Shapes[i] : null;
    public Style Current { get; set; } = Style.Default;
    public bool Private { get; set; } = true;
    public float Exposure { get; set; } = 1f;
    public bool Zebra { get; set; }
    private long _seq;        // highest stamp handed out; every commit takes a fresh one
    private long _savedSeq;
    /// <summary>True when the current state's edit stamp differs from the last save point. Undo and redo restore the
    /// stamp of the state they move to, so undoing back to the saved state clears it again.</summary>
    public bool HasEdits => _state.Seq != _savedSeq;
    public void MarkSaved() => _savedSeq = _state.Seq;
    /// <summary>The edit the current state came from: take it when the pixels are rendered for a save, and hand it to
    /// <see cref="MarkSaved(long)"/> once the write has landed.</summary>
    public long Revision => _state.Seq;
    /// <summary>Marks <paramref name="revision"/> as the saved state. A save that awaits its encode must use this rather
    /// than <see cref="MarkSaved()"/>: an edit made while the write was running is not in the file.</summary>
    public void MarkSaved(long revision) => _savedSeq = revision;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int NextCounter => Shapes.OfType<CounterShape>().Select(c => c.Number).DefaultIfEmpty(0).Max() + 1;
    /// <summary>Dirty rectangle in source-frame pixels; Empty means repaint everything.</summary>
    public event Action<IntRect>? Changed;

    public int NewId() => ++_nextId;

    /// <summary>The source-frame area where the rendered document can differ from the bare frame: Empty when nothing
    /// is drawn, null when anywhere (the zebra, or a shape such as a spotlight that changes the whole image).</summary>
    public IntRect? Covers()
    {
        if (Zebra) return null;
        IntRect area = IntRect.Empty;
        foreach (Shape s in Shapes)
        {
            if (s.AffectsWholeImage) return null;
            area = area.IsEmpty ? s.DirtyBounds : area.Union(s.DirtyBounds);
        }
        return area;
    }

    /// <summary>A copy with every shape and the crop moved by (<paramref name="dx"/>, <paramref name="dy"/>), for
    /// converting between the editor's image-local frame and the desktop frame. Ids, order and view state carry over;
    /// the copy has no undo history and starts saved.</summary>
    public AnnotationDoc Translated(int dx, int dy)
    {
        Shape[] shapes = Shapes.Select(s => s.Moved(dx, dy)).ToArray();
        IntRect crop = Crop.IsEmpty ? Crop : Crop.Offset(dx, dy);
        var d = new AnnotationDoc { Current = Current, Private = Private, Exposure = Exposure };
        d._state = new State(shapes, crop, 0);
        d._nextId = _nextId;
        d.MarkSaved();
        return d;
    }

    public void Add(Shape s)
    {
        bool whole = FirstOrLastSpotlight(s);
        Commit(_state with { Shapes = Shapes.Append(s).ToArray() });
        Raise(whole, null, s);
    }

    public void Replace(Shape s)
    {
        int i = IndexOf(s.Id); if (i < 0) return;
        Shape before = Shapes[i];
        Shape[] next = Shapes.ToArray(); next[i] = s;
        Commit(_state with { Shapes = next });
        // A moved or resized spotlight changes only its own old and new areas: outside both the picture was dimmed
        // and still is.
        Raise(false, before, s);
    }

    public void Remove(int id)
    {
        int i = IndexOf(id); if (i < 0) return;
        Shape gone = Shapes[i];
        bool whole = FirstOrLastSpotlight(gone);
        Commit(_state with { Shapes = Shapes.Where(s => s.Id != id).ToArray() });
        if (SelectedId == id) SelectedId = null;
        Raise(whole, gone, null);
    }

    public void SetCrop(IntRect crop) { Commit(_state with { Crop = crop }); Changed?.Invoke(IntRect.Empty); }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push(_state); _state = _undo.Last!.Value; _undo.RemoveLast();
        if (SelectedId is int id && IndexOf(id) < 0) SelectedId = null;
        Changed?.Invoke(IntRect.Empty); return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.AddLast(_state); _state = _redo.Pop();
        if (SelectedId is int id && IndexOf(id) < 0) SelectedId = null;
        Changed?.Invoke(IntRect.Empty); return true;
    }

    public void Select(int? id)
    {
        if (id == SelectedId) return;
        IntRect dirty = Selected?.DirtyBounds ?? IntRect.Empty;
        SelectedId = id != null && IndexOf(id.Value) >= 0 ? id : null;
        if (Selected != null) dirty = dirty.Union(Selected.DirtyBounds);
        Changed?.Invoke(dirty.IsEmpty ? IntRect.Empty : dirty);
    }

    public Shape? HitTop(int x, int y) { for (int i = Shapes.Count - 1; i >= 0; i--) if (Shapes[i].HitTest(x, y)) return Shapes[i]; return null; }

    /// <summary>Adding the first spotlight dims the whole picture and removing the last undims it; any other spotlight
    /// only lights or darkens its own rectangle. Asked before the edit, when <paramref name="s"/> is the only one.</summary>
    private bool FirstOrLastSpotlight(Shape s)
    {
        if (s is not SpotlightShape) return false;
        foreach (Shape other in Shapes) if (other is SpotlightShape && other.Id != s.Id) return false;
        return true;
    }

    /// <summary>
    /// Raises <see cref="Changed"/> for an edit, part by part (<see cref="DirtyRegion"/>), or once with Empty for the
    /// whole picture. A redaction also repaints the lens of every magnifier whose source it overlaps, since a lens
    /// shows its source's pixels redacted.
    /// </summary>
    private void Raise(bool whole, Shape? before, Shape? after)
    {
        if (whole) { Changed?.Invoke(IntRect.Empty); return; }
        List<IntRect> dirty = DirtyRegion.Of(before, after);
        foreach (Shape? r in new[] { before, after })
            if (r is { IsRedaction: true })
                foreach (Shape s in Shapes)
                    if (s is MagnifierShape m && m.Source.IntersectsWith(r.Bounds)) dirty.Add(m.Lens);
        foreach (IntRect r in DirtyRegion.Merge(dirty)) Changed?.Invoke(r);
    }

    private int IndexOf(int id) { for (int i = 0; i < Shapes.Count; i++) if (Shapes[i].Id == id) return i; return -1; }

    private void Commit(State next)
    {
        _undo.AddLast(_state);
        if (_undo.Count > UndoDepth) _undo.RemoveFirst();
        _redo.Clear();
        _state = next with { Seq = ++_seq };
    }
}
