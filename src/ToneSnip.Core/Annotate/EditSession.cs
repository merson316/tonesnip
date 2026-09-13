using ToneSnip.Core.Geometry;

namespace ToneSnip.Core.Annotate;

/// <summary>Turns pointer and key input into document edits. Coordinates are source-frame pixels; hosts convert.</summary>
public sealed class EditSession
{
    public const int HandleSize = 10;
    private static readonly Random Rng = new();
    private Tool _tool = Tool.Select;
    private (int X, int Y)? _start;
    private Handle _handle = Handle.None;
    private Shape? _dragOriginal;
    private List<(int X, int Y)>? _points;

    private readonly Action<IntRect> _forward;
    public EditSession(AnnotationDoc doc) { Doc = doc; _forward = r => Changed?.Invoke(r); doc.Changed += _forward; }

    /// <summary>Stops forwarding the document's changes. The document outlives the session (it travels with the capture
    /// result), and while it holds this subscription it also holds every host the session's events reach.</summary>
    public void Detach() => Doc.Changed -= _forward;

    public AnnotationDoc Doc { get; }
    public Shape? InProgress { get; private set; }
    public IntRect CropMarquee { get; private set; } = IntRect.Empty;
    public (int X, int Y)? PendingText { get; private set; }
    public bool Busy => _start != null;
    public event Action<IntRect>? Changed;
    public event Action? ToolChanged;
    public event Action? TextRequested;

    public Tool Tool
    {
        get => _tool;
        set { if (_tool == value) return; CancelInProgress(); _tool = value; if (value != Tool.Select) Doc.Select(null); ToolChanged?.Invoke(); }
    }

    public Style Style
    {
        get => Doc.Current;
        set
        {
            Doc.Current = value;
            // Re-picking the shape's current style is not an edit: no undo step, no unsaved-changes prompt.
            if (Doc.Selected is Shape s && Restyle(s, value) is var restyled && !restyled.Equals(s)) Doc.Replace(restyled);
        }
    }

    public Handle HandleAt(int x, int y) => Doc.Selected is Shape s ? Handles.Hit(s, x, y, HandleSize) : Handle.None;

    /// <summary>Returns true when the press starts a drag or edit the host should track (capture the mouse).</summary>
    public bool Begin(int x, int y, InputMods mods)
    {
        if (PendingText != null) return false;
        switch (_tool)
        {
            case Tool.Select:
                _handle = HandleAt(x, y);
                if (_handle == Handle.None) { Shape? hit = Doc.HitTop(x, y); Doc.Select(hit?.Id); if (hit == null) return false; }
                _dragOriginal = Doc.Selected; _start = (x, y); return true;
            case Tool.Text:
                _start = (x, y); return true;
            case Tool.Counter:
                _start = (x, y); return true;
            case Tool.Pen: case Tool.Highlighter:
                _points = new List<(int, int)> { (x, y) };
                InProgress = new PenShape(0, _points, Doc.Current.Width, Doc.Current.Color, _tool == Tool.Highlighter);
                _start = (x, y); Changed?.Invoke(InProgress.DirtyBounds); return true;
            default:
                _start = (x, y); UpdateDragShape(x, y, mods); return true;
        }
    }

    public void Move(int x, int y, InputMods mods)
    {
        if (_start == null) return;
        switch (_tool)
        {
            case Tool.Select:
                if (_dragOriginal == null || _start is not (int sx, int sy)) return;
                int dx = x - sx, dy = y - sy;
                // Zero offset uses the original itself: a pen stroke's Moved(0, 0) is a new points array that does not
                // Equal the original and would commit a no-op edit.
                Shape moved = dx == 0 && dy == 0 ? _dragOriginal
                    : _handle == Handle.None ? _dragOriginal.Moved(dx, dy) : _dragOriginal.Resized(_handle, dx, dy) ?? _dragOriginal;
                IntRect dirty = (InProgress ?? _dragOriginal).DirtyBounds.Union(moved.DirtyBounds);
                InProgress = moved; Changed?.Invoke(Pad(dirty)); return;
            case Tool.Pen: case Tool.Highlighter:
                if (_points != null && (Math.Abs(_points[^1].X - x) + Math.Abs(_points[^1].Y - y) >= 2))
                {
                    _points.Add((x, y));
                    Changed?.Invoke(BoundsOfLast(_points, Doc.Current.Width));
                }
                return;
            case Tool.Text: case Tool.Counter: return;
            default: UpdateDragShape(x, y, mods); return;
        }
    }

    public void End(int x, int y, InputMods mods)
    {
        if (_start == null) return;
        (int sx, int sy) = _start.Value; _start = null;
        switch (_tool)
        {
            case Tool.Select:
            {
                // Clear InProgress first: hosts leave the dragged shape out of the render while a moved copy is being
                // drawn as chrome, so the repaint that puts it back must see the drag already finished.
                Shape? moved = InProgress, original = _dragOriginal;
                InProgress = null; _dragOriginal = null; _handle = Handle.None;
                if (moved == null || original == null) return;
                if (!moved.Equals(original)) Doc.Replace(moved);
                else Changed?.Invoke(Pad(moved.DirtyBounds.Union(original.DirtyBounds)));   // dragged back to where it started
                return;
            }
            case Tool.Text:
                PendingText = (sx, sy); TextRequested?.Invoke(); return;
            case Tool.Counter:
                Doc.Add(new CounterShape(Doc.NewId(), sx, sy, Doc.NextCounter, Doc.Current.TextSize, Doc.Current.Color)); return;
            case Tool.Pen: case Tool.Highlighter:
                if (_points != null) Doc.Add(new PenShape(Doc.NewId(), _points.ToArray(), Doc.Current.Width, Doc.Current.Color, _tool == Tool.Highlighter));
                _points = null; InProgress = null; return;
            case Tool.Crop:
                UpdateDragShape(x, y, mods);
                IntRect m = CropMarquee; InProgress = null;
                if (m.Width < Handles.MinSize || m.Height < Handles.MinSize) CropMarquee = IntRect.Empty;
                Changed?.Invoke(IntRect.Empty); return;
            default:
                UpdateDragShape(x, y, mods);
                Shape? made = InProgress; InProgress = null;
                if (made == null) return;
                bool tiny = made switch
                {
                    LineShape l => Math.Abs(l.X2 - l.X1) + Math.Abs(l.Y2 - l.Y1) < 4,
                    BoxShape b => b.Rect.Width < 4 || b.Rect.Height < 4,
                    RedactShape r => r.Rect.Width < 4 || r.Rect.Height < 4,
                    _ => made.Bounds.Width < 4 || made.Bounds.Height < 4,
                };
                if (tiny) { Changed?.Invoke(Pad(made.DirtyBounds)); return; }
                Doc.Add(made with { Id = Doc.NewId() }); return;
        }
    }

    public void CommitText(string text)
    {
        if (PendingText is not (int x, int y)) return;
        PendingText = null;
        if (!string.IsNullOrWhiteSpace(text)) Doc.Add(new TextShape(Doc.NewId(), x, y, text.TrimEnd(), Doc.Current.TextSize, Doc.Current.Color));
    }

    public void CancelText() { PendingText = null; }

    /// <summary>Applies the marquee as the crop, intersected with any existing crop: the captured pointer can drag past
    /// the visible edge, and must not bring back pixels an earlier crop removed.</summary>
    public void ApplyCrop()
    {
        if (CropMarquee.IsEmpty) return;
        IntRect crop = Doc.Crop.IsEmpty ? CropMarquee : CropMarquee.Intersect(Doc.Crop);
        if (crop.IsEmpty) { CancelCrop(); return; }
        Doc.SetCrop(crop); CropMarquee = IntRect.Empty; Tool = Tool.Select;
    }

    public void CancelCrop() { if (CropMarquee.IsEmpty) return; CropMarquee = IntRect.Empty; Changed?.Invoke(IntRect.Empty); }

    /// <summary>Escape: pending text, then crop marquee, then an in-progress drag, then the selection. False when nothing was pending.</summary>
    public bool Escape()
    {
        if (PendingText != null) { CancelText(); return true; }
        if (!CropMarquee.IsEmpty) { CancelCrop(); return true; }
        if (_start != null || InProgress != null) { CancelInProgress(); return true; }
        if (Doc.SelectedId != null) { Doc.Select(null); return true; }
        return false;
    }

    /// <summary>Drops a drag in flight, for hosts that take the mouse away mid-stroke (the overlay switching to a capture mode).</summary>
    public void CancelDrag() => CancelInProgress();

    public bool DeleteSelected()
    {
        if (Doc.SelectedId is not int id) return false;
        Doc.Remove(id); return true;
    }

    private void CancelInProgress()
    {
        // Both ends of an abandoned move matter: the copy that was being dragged, and the home the real shape was
        // hidden from while it was (hosts suppress the original under an in-progress Select drag).
        IntRect dirty = (InProgress?.DirtyBounds ?? IntRect.Empty).Union(_dragOriginal?.DirtyBounds ?? IntRect.Empty);
        _start = null; _points = null; InProgress = null; _dragOriginal = null; _handle = Handle.None;
        if (!CropMarquee.IsEmpty) { CropMarquee = IntRect.Empty; dirty = IntRect.Empty; }
        Changed?.Invoke(dirty.IsEmpty ? IntRect.Empty : Pad(dirty));
    }

    private void UpdateDragShape(int x, int y, InputMods mods)
    {
        if (_start is not (int sx, int sy)) return;
        IntRect old = InProgress?.DirtyBounds ?? IntRect.Empty;
        bool shift = mods.HasFlag(InputMods.Shift);
        Style st = Doc.Current;
        switch (_tool)
        {
            case Tool.Line: case Tool.Arrow:
                (int ex, int ey) = shift ? Snap45(sx, sy, x, y) : (x, y);
                InProgress = new LineShape(0, sx, sy, ex, ey, st.Width, st.Color, _tool == Tool.Arrow); break;
            case Tool.Rect: case Tool.Ellipse:
                InProgress = new BoxShape(0, DragRect(sx, sy, x, y, shift), st.Width, st.Color, _tool == Tool.Ellipse, Filled: false); break;
            case Tool.Blur: case Tool.Pixelate:
                uint seed = InProgress is RedactShape r ? r.Seed : (uint)Rng.Next(1, int.MaxValue);
                InProgress = new RedactShape(0, DragRect(sx, sy, x, y, shift), st.Width, _tool == Tool.Blur, Doc.Private, seed); break;
            case Tool.Crop:
                CropMarquee = DragRect(sx, sy, x, y, shift); Changed?.Invoke(IntRect.Empty); return;
        }
        if (InProgress != null) Changed?.Invoke(Pad(old.Union(InProgress.DirtyBounds)));
    }

    private static IntRect DragRect(int sx, int sy, int x, int y, bool square)
    {
        if (!square) return IntRect.FromDrag(sx, sy, x, y);
        int side = Math.Max(Math.Abs(x - sx), Math.Abs(y - sy));
        return IntRect.FromDrag(sx, sy, sx + Math.Sign(x - sx == 0 ? 1 : x - sx) * side, sy + Math.Sign(y - sy == 0 ? 1 : y - sy) * side);
    }

    private static (int, int) Snap45(int sx, int sy, int x, int y)
    {
        int dx = x - sx, dy = y - sy;
        double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
        double len = Math.Sqrt(dx * dx + dy * dy);
        return (sx + (int)Math.Round(Math.Cos(angle) * len), sy + (int)Math.Round(Math.Sin(angle) * len));
    }

    private static Shape Restyle(Shape s, Style st) => s switch
    {
        PenShape p => p with { Width = st.Width, Color = st.Color },
        LineShape l => l with { Width = st.Width, Color = st.Color },
        BoxShape b => b with { Width = st.Width, Color = st.Color },
        TextShape t => t with { Size = st.TextSize, Color = st.Color },
        CounterShape c => c with { Size = st.TextSize, Color = st.Color },
        RedactShape r => r with { Strength = st.Width },
        _ => s,
    };

    private static IntRect BoundsOfLast(List<(int X, int Y)> pts, int width)
    {
        int n = pts.Count; (int x1, int y1) = pts[Math.Max(0, n - 2)]; (int x2, int y2) = pts[n - 1]; int pad = width + 2;
        return IntRect.FromLtrb(Math.Min(x1, x2) - pad, Math.Min(y1, y2) - pad, Math.Max(x1, x2) + pad + 1, Math.Max(y1, y2) + pad + 1);
    }

    private static IntRect Pad(IntRect r) => r.IsEmpty ? r : IntRect.FromLtrb(r.Left - HandleSize, r.Top - HandleSize, r.Right + HandleSize, r.Bottom + HandleSize);
}
