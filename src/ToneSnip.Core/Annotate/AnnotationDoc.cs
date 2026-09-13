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

    public void Add(Shape s) { Commit(_state with { Shapes = Shapes.Append(s).ToArray() }); Changed?.Invoke(s.DirtyBounds); }

    public void Replace(Shape s)
    {
        int i = IndexOf(s.Id); if (i < 0) return;
        IntRect dirty = Shapes[i].DirtyBounds.Union(s.DirtyBounds);
        Shape[] next = Shapes.ToArray(); next[i] = s;
        Commit(_state with { Shapes = next });
        Changed?.Invoke(dirty);
    }

    public void Remove(int id)
    {
        int i = IndexOf(id); if (i < 0) return;
        IntRect dirty = Shapes[i].DirtyBounds;
        Commit(_state with { Shapes = Shapes.Where(s => s.Id != id).ToArray() });
        if (SelectedId == id) SelectedId = null;
        Changed?.Invoke(dirty);
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

    private int IndexOf(int id) { for (int i = 0; i < Shapes.Count; i++) if (Shapes[i].Id == id) return i; return -1; }

    private void Commit(State next)
    {
        _undo.AddLast(_state);
        if (_undo.Count > UndoDepth) _undo.RemoveFirst();
        _redo.Clear();
        _state = next with { Seq = ++_seq };
    }
}
