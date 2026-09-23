using System.Runtime.InteropServices;
using ToneSnip.Core.Annotate;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;

namespace ToneSnip.Windows.Overlay;

/// <summary>
/// One frozen-frame window per monitor: a plain Win32 popup painting through GDI. No UI framework on purpose: a XAML
/// tree and composition surface would add GPU and memory cost to what is a copy of a buffer the app already holds.
/// Coordinates are physical pixels throughout (PerMonitorV2 manifest).
/// <para>
/// The paint path allocates nothing: the frozen frame and the annotated back buffer stay pinned, the double buffer is a
/// DIB section created once, and the pens, brushes, font and lasso point array are created once per window and
/// released in <see cref="Dispose"/>. Only the invalidated area is repainted, rectangle by rectangle when it is a
/// complex region (the guide lines of <see cref="FrameStyle.Guides"/> are monitor-long strips whose bounding box would
/// be the whole monitor).
/// </para>
/// </summary>
public sealed class OverlayWindow : IDisposable
{
    private const string ClassName = "tonesnip-overlay";

    // One registration per process with a static window procedure that finds the instance by HWND: a per-instance
    // procedure would leave a second window routing its messages to the first instance's (possibly dead) delegate.
    private static readonly Win32.WndProcDelegate StaticProc = Proc;   // static field: the class holds a raw function pointer to it
    private static readonly Dictionary<IntPtr, OverlayWindow> Live = new();
    private static readonly object Gate = new();
    private static bool _registered;

    private static readonly IntPtr CursorCross = Win32.LoadCursorW(IntPtr.Zero, (IntPtr)Win32.IdcCross);
    private static readonly IntPtr CursorArrow = Win32.LoadCursorW(IntPtr.Zero, (IntPtr)Win32.IdcArrow);
    private static readonly IntPtr CursorIBeam = Win32.LoadCursorW(IntPtr.Zero, (IntPtr)Win32.IdcIBeam);
    private static readonly IntPtr NullPen = Gdi.GetStockObject(Gdi.NullPen);

    private readonly IntRect _bounds;
    private readonly BgraImage _frame;
    private readonly IOverlayHost _host;
    private readonly ILog? _log;
    private readonly GCHandle _framePin;
    private readonly IntPtr _memDc, _dib, _bits, _oldBitmap;
    private readonly IntPtr _penLasso, _pillBrush, _accentBrush, _keylineBrush, _updateRgn;
    private readonly IntPtr _font, _chipFont;
    /// <summary>The selection frame the user picked, read once: a snip keeps its look even if Settings changes mid-snip.</summary>
    private readonly FrameStyle _style;
    /// <summary>The pill's and the selection frames' metrics at this monitor's scaling, known once the window exists.</summary>
    private readonly CursorPillLayout _pill = CursorPillLayout.For(1.0);
    private readonly ViewfinderLayout _viewfinder = ViewfinderLayout.For(1.0);
    private readonly GuidesLayout _guides = GuidesLayout.For(1.0);
    private readonly IntRect[] _arms = new IntRect[ViewfinderLayout.MaxArms];   // the brackets, filled per paint
    private readonly IntRect[] _lines = new IntRect[GuidesLayout.MaxLines];    // the guide lines, filled per paint

    /// <summary>The update region, read as rectangles before BeginPaint validates it: its RGNDATA, and the rectangles
    /// merged back into the strips and boxes they were cut from.</summary>
    private const int MaxRegionRects = 512, RegionHeaderBytes = 32;
    private readonly byte[] _regionData = new byte[RegionHeaderBytes + MaxRegionRects * 16];
    private readonly IntRect[] _paintRects = new IntRect[MaxRegionRects];

    // Annotated copy of the frozen frame, allocated when a document first exists and then patched in place. Taken from
    // `backBuffer` when the host has a pool, so a full-monitor buffer is not allocated per snip.
    private readonly Func<int, int, BgraImage>? _backBuffer;
    private BgraImage? _back;
    private GCHandle _backPin;
    private bool _backValid;

    // The monitor-local areas the previous paint's selection frame, lasso, cursor readout and guide lines covered. Kept
    // apart rather than as one union, so a moving guide line invalidates two strips and not the monitor between them.
    private const int MaxRegions = 3 + GuidesLayout.MaxLines;
    private IntRect[] _lastRegions = new IntRect[MaxRegions], _nowRegions = new IntRect[MaxRegions];
    private int _lastRegionCount;
    private bool _lastHadSelection;
    private Gdi.Point[] _points = new Gdi.Point[256];   // the lasso's screen-to-client buffer, grown on demand
    private OverlayCursor _cursor = OverlayCursor.Cross;
    private bool _painted, _disposed;
    /// <summary>True only while this window releases the capture itself, so its own WM_CAPTURECHANGED is not a loss.</summary>
    private bool _releasingCapture;

    public IntPtr Hwnd { get; }

    /// <param name="backBuffer">Width and height in, an annotated back buffer of that size out. Null allocates one per
    /// window instead.</param>
    public OverlayWindow(IntRect bounds, BgraImage frame, IOverlayHost host, ILog? log = null, Func<int, int, BgraImage>? backBuffer = null)
    {
        if (frame.Width != bounds.Width || frame.Height != bounds.Height)
            throw new ArgumentException($"frame {frame.Width}x{frame.Height} does not match the monitor {bounds}", nameof(frame));
        _bounds = bounds; _frame = frame; _host = host; _log = log; _backBuffer = backBuffer;
        _framePin = GCHandle.Alloc(frame.Data, GCHandleType.Pinned);

        IntPtr screen = Win32.GetDC(IntPtr.Zero);
        try
        {
            _memDc = Gdi.CreateCompatibleDC(screen);
            _dib = Gdi.CreateDib(screen, bounds.Width, bounds.Height, out _bits);
        }
        finally { Win32.ReleaseDC(IntPtr.Zero, screen); }
        if (_memDc == IntPtr.Zero || _dib == IntPtr.Zero) { Release(); throw new InvalidOperationException("overlay: could not create the double buffer"); }
        _oldBitmap = Gdi.SelectObject(_memDc, _dib);

        _penLasso = Gdi.CreatePen(Gdi.PsSolid, 2, Gdi.Ref(0xFFFFFFFF));
        _pillBrush = Gdi.CreateSolidBrush(Gdi.Ref(0xFF202020));
        _accentBrush = Gdi.CreateSolidBrush(Gdi.Ref(host.FrameAccent));
        _keylineBrush = Gdi.CreateSolidBrush(Gdi.Ref(0xFF000000));
        _updateRgn = Gdi.CreateRectRgn(0, 0, 0, 0);
        _style = host.FrameStyle;

        IntPtr instance = Win32.GetModuleHandleW(null);
        try
        {
            lock (Gate)
            {
                if (!_registered) Register(instance);
                Hwnd = Win32.CreateWindowExW(Win32.WsExToolWindow | Win32.WsExTopmost, ClassName, string.Empty, Win32.WsPopup,
                                             bounds.Left, bounds.Top, bounds.Width, bounds.Height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                if (Hwnd == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (Live.Count == 0) Unregister(instance);
                    throw new InvalidOperationException("CreateWindowExW failed: " + error);
                }
                Live[Hwnd] = this;
            }
        }
        catch { Release(); throw; }   // the pin, the DIB section, the DC and the pens already exist by now

        // The window covers exactly one monitor, so its DPI is that monitor's. The overlay lives for one snip, so a
        // scaling change while it is up (WM_DPICHANGED) is not followed.
        uint dpi = Win32.GetDpiForWindow(Hwnd);
        double scale = dpi > 0 ? dpi / 96.0 : 1.0;
        _pill = CursorPillLayout.For(scale);
        _viewfinder = ViewfinderLayout.For(scale);
        _guides = GuidesLayout.For(scale);
        _font = Gdi.CreateFontW(-_pill.FontPx, 0, 0, 0, Gdi.FwNormal, 0, 0, 0, Gdi.DefaultCharSet, 0, 0, Gdi.ClearTypeQuality, 0, "Segoe UI");
        // One chip font for both frames that have a chip: their sizes are the same 12 effective pixels.
        _chipFont = Gdi.CreateFontW(-_viewfinder.ChipFontPx, 0, 0, 0, Gdi.FwSemibold, 0, 0, 0, Gdi.DefaultCharSet, 0, 0, Gdi.ClearTypeQuality, 0, "Segoe UI");
    }

    /// <summary>Puts the window on screen without taking the foreground; <see cref="Activate"/> hands it the keyboard.</summary>
    public void Show() => Win32.ShowWindow(Hwnd, Win32.SwShowNoActivate);

    /// <summary>Paints any pending update now (UpdateWindow sends WM_PAINT straight to the window procedure), rather
    /// than when the message queue gets round to it.</summary>
    public void PaintNow()
    {
        if (!_disposed && Hwnd != IntPtr.Zero) Win32.UpdateWindow(Hwnd);
    }

    /// <summary>Foreground plus keyboard focus, even when the snip came from a hotkey while another app was active.</summary>
    public void Activate() => Win32.ForceForeground(Hwnd, _log);

    /// <summary>The cursor for every state the overlay has; applied at once, and reaffirmed on each WM_SETCURSOR.</summary>
    public void SetCursor(OverlayCursor cursor)
    {
        if (_cursor == cursor) return;
        _cursor = cursor;
        Win32.SetCursor(Handle(cursor));
    }

    private static IntPtr Handle(OverlayCursor c) => c switch { OverlayCursor.Arrow => CursorArrow, OverlayCursor.IBeam => CursorIBeam, _ => CursorCross };

    private IntRect Local => new(0, 0, _bounds.Width, _bounds.Height);

    // ----- invalidation -----

    /// <summary>
    /// Repaints only what can have changed since the last paint: the old and the new selection frame (the dim state
    /// flips only inside them), the lasso, the cursor readout and the guide lines.
    /// </summary>
    public void Render()
    {
        if (_host.HasDocument && !_backValid) { RenderDirty(IntRect.Empty); return; }
        IntRect sel = _host.Selection.IsEmpty ? _host.Hover : _host.Selection;
        bool hasSelection = !sel.IsEmpty;
        // While annotating the whole-monitor dim depends on whether anything is selected, so that transition repaints all.
        if (_host.Annotating && hasSelection != _lastHadSelection) { _lastHadSelection = hasSelection; RenderFull(); return; }
        _lastHadSelection = hasSelection;
        int now = DynamicRegions(sel, _nowRegions);
        for (int i = 0; i < _lastRegionCount; i++) Invalidate(_lastRegions[i]);
        for (int i = 0; i < now; i++) Invalidate(_nowRegions[i]);
        (_lastRegions, _nowRegions) = (_nowRegions, _lastRegions);
        _lastRegionCount = now;
    }

    /// <summary>Repaints the whole monitor (mode or annotate-state changes).</summary>
    public void RenderFull()
    {
        IntRect sel = _host.Selection.IsEmpty ? _host.Hover : _host.Selection;
        _lastRegionCount = DynamicRegions(sel, _lastRegions);
        _lastHadSelection = !sel.IsEmpty;
        if (_disposed || Hwnd == IntPtr.Zero) return;
        Win32.InvalidateRect(Hwnd, IntPtr.Zero, false);
    }

    /// <summary>Writes the monitor-local areas the current state paints over the frozen frame, and returns how many.</summary>
    private int DynamicRegions(IntRect sel, IntRect[] into)
    {
        IntRect local = Local;
        int n = 0;
        IntRect r = sel.Offset(-_bounds.Left, -_bounds.Top);
        if (!sel.IsEmpty) Add(FrameReach(r));
        if (_host.Path.Count > 1) Add(Pad(FreeformMask.BoundingBox(_host.Path).Offset(-_bounds.Left, -_bounds.Top), 3));
        (int cx, int cy) = _host.Cursor;
        bool readout = Readout(cx, cy);
        if (readout) Add(_style == FrameStyle.Guides ? _guides.LoupeReach(cx - _bounds.Left, cy - _bounds.Top) : _pill.Reach(cx - _bounds.Left, cy - _bounds.Top));
        if (_style == FrameStyle.Guides)
        {
            int lines = GuideLines(r, cx - _bounds.Left, cy - _bounds.Top, readout);
            for (int i = 0; i < lines; i++) Add(_lines[i]);
        }
        return n;

        void Add(IntRect area) { area = area.Intersect(local); if (!area.IsEmpty) into[n++] = area; }
    }

    /// <summary>Everything the selection frame for <paramref name="r"/> (monitor-local) covers, guide lines aside.</summary>
    private IntRect FrameReach(IntRect r) => _style switch
    {
        FrameStyle.Viewfinder => _viewfinder.Reach(r),
        FrameStyle.Guides => _guides.Reach(r),
        _ => Pad(r, 2),
    };

    /// <summary>
    /// Whether the cursor readout (pill or loupe) is shown on this monitor. A drawing tool repaints only its dirty
    /// rectangle, so a cursor-following readout would smear; it is also useless there.
    /// </summary>
    private bool Readout(int cx, int cy) => !_host.DrawingActive && _bounds.Contains(cx, cy);

    /// <summary>Fills <see cref="_lines"/> with the guide lines for a monitor-local selection, or with nothing selected
    /// a crosshair through the monitor-local cursor when the readout is shown; returns how many.</summary>
    private int GuideLines(IntRect r, int lx, int ly, bool crosshair)
        => _guides.Lines(r, crosshair ? lx : -1, crosshair ? ly : -1, Local, _lines);

    private static IntRect Pad(IntRect r, int by) => IntRect.FromLtrb(r.Left - by, r.Top - by, r.Right + by, r.Bottom + by);

    /// <summary>Re-renders the annotated back buffer for a source-frame rectangle (Empty = whole monitor) and repaints it.</summary>
    public void RenderDirty(IntRect sourceDirty)
    {
        if (_disposed || !_host.HasDocument) return;   // Release() has already freed the pins: EnsureBack would leak a new one
        EnsureBack();
        IntRect local = sourceDirty.IsEmpty || !_backValid
            ? Local
            : sourceDirty.Intersect(_bounds).Offset(-_bounds.Left, -_bounds.Top);
        if (local.IsEmpty) return;
        // RenderShapes may grow the painted area beyond `local` to cover a redaction it touches; the zebra must cover
        // the same grown area or it would leave stale stripes (or miss new ones) at the edge of that growth.
        IntRect painted = _host.RenderShapes(_frame, _bounds, _back!, local);
        _host.Zebra?.Invoke(_back!, _bounds, painted);
        _backValid = true;
        local = local.Union(painted);
        // Handles and the selection outline live outside a shape's own bounds, so repaint a little wider than we rendered.
        const int pad = EditSession.HandleSize + 2;
        Invalidate(IntRect.FromLtrb(local.Left - pad, local.Top - pad, local.Right + pad, local.Bottom + pad).Intersect(Local));
    }

    /// <summary>Marks the annotated buffer stale so the next <see cref="Render"/> rebuilds it (the base frame changed).</summary>
    public void InvalidateBack() => _backValid = false;

    /// <summary>
    /// The annotated buffer, on first use. A pooled one may hold the previous snip's marks, which is harmless: the first
    /// <see cref="RenderDirty"/> has <c>_backValid</c> false and rewrites the whole monitor before anything is painted.
    /// </summary>
    private void EnsureBack()
    {
        if (_back != null) return;
        _back = _backBuffer?.Invoke(_frame.Width, _frame.Height) ?? BgraImage.Blank(_frame.Width, _frame.Height);
        _backPin = GCHandle.Alloc(_back.Data, GCHandleType.Pinned);
    }

    private void Invalidate(IntRect r)
    {
        if (_disposed || Hwnd == IntPtr.Zero || r.IsEmpty) return;
        var rect = new Win32.Rect { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
        Win32.InvalidateRect(Hwnd, ref rect, false);
    }

    // ----- paint -----

    private void OnPaint()
    {
        int rects = UpdateRects();   // before BeginPaint, which validates the region
        IntPtr dc = Win32.BeginPaint(Hwnd, out Win32.PaintStruct ps);
        if (dc == IntPtr.Zero) return;   // no DC, no paint — and EndPaint is only owed after a BeginPaint that worked
        bool drew = false;
        try
        {
            bool region = rects > 0;   // _updateRgn holds exactly the rectangles read into _paintRects
            if (rects == 0) _paintRects[rects++] = IntRect.FromLtrb(ps.Paint.Left, ps.Paint.Top, ps.Paint.Right, ps.Paint.Bottom);
            int clips = 0;
            for (int i = 0; i < rects; i++)
            {
                IntRect clip = _paintRects[i].Intersect(Local);
                if (!clip.IsEmpty) _paintRects[clips++] = clip;
            }
            if (clips > 0)
            {
                // The rectangles are disjoint, so each pixel is painted by exactly one of them. The chrome is drawn once
                // over all of them between the two passes, rather than through a GDI+ graphics per rectangle.
                for (int i = 0; i < clips; i++) PaintUnder(_paintRects[i]);
                if (_host.Annotating && _host.HasDocument)
                {
                    // Clipped to the update region itself when it was read, so the chrome lands only where the second
                    // pass blits; otherwise to the one rectangle there is.
                    if (region) Gdi.SelectClipRgn(_memDc, _updateRgn);
                    else ClipTo(_paintRects[0]);
                    _host.DrawChrome(_memDc, _bounds);
                }
                for (int i = 0; i < clips; i++) PaintOver(dc, _paintRects[i]);
                drew = true;
            }
        }
        finally { Win32.EndPaint(Hwnd, ref ps); }
        // "Overlay shown" means pixels reached the screen, so an empty update rectangle does not count as the first paint.
        if (!drew || _painted) return;
        _painted = true;
        _host.OnFirstPaint();
    }

    /// <summary>
    /// Reads a complex update region into <see cref="_paintRects"/> and returns how many rectangles to paint, or 0 to
    /// paint the bounding rectangle instead (a simple or unreadable region). The region's band slivers are joined back
    /// into the strips and boxes that were invalidated (<see cref="RegionRects.Join"/>).
    /// </summary>
    private unsafe int UpdateRects()
    {
        const int complexRegion = 3;
        if (Win32.GetUpdateRgn(Hwnd, _updateRgn, false) != complexRegion) return 0;
        fixed (byte* data = _regionData)
        {
            if (Gdi.GetRegionData(_updateRgn, (uint)_regionData.Length, data) == 0) return 0;   // more rectangles than fit
            int count = *(int*)(data + 8);   // RGNDATAHEADER.nCount
            if (count <= 0 || count > MaxRegionRects) return 0;
            for (int i = 0; i < count; i++)
            {
                int* rc = (int*)(data + RegionHeaderBytes + i * 16);
                _paintRects[i] = IntRect.FromLtrb(rc[0], rc[1], rc[2], rc[3]);
            }
            return RegionRects.Join(_paintRects.AsSpan(0, count), _paintRects);
        }
    }

    /// <summary>Clips the double buffer's GDI and GDI+ drawing to one rectangle.</summary>
    private void ClipTo(IntRect clip)
    {
        Gdi.SelectClipRgn(_memDc, IntPtr.Zero);
        Gdi.IntersectClipRect(_memDc, clip.Left, clip.Top, clip.Right, clip.Bottom);
    }

    /// <summary>The annotated buffer replaces the frozen frame as soon as a document exists, so turning the tool row off
    /// still shows (and saves) what was drawn; only the live chrome belongs to the annotating state.</summary>
    private unsafe byte* Source => (byte*)(_host.HasDocument && _backValid && _back != null ? _backPin : _framePin).AddrOfPinnedObject();

    /// <summary>What lies under the chrome in one rectangle: the frame, the dim and the selection frame.</summary>
    private unsafe void PaintUnder(IntRect clip)
    {
        int w = _bounds.Width, h = _bounds.Height;

        // GDI batches its drawing, so the previous paint's outline or pill could otherwise land on top of these bytes.
        Gdi.GdiFlush();
        byte* bits = (byte*)_bits;
        bool annotating = _host.Annotating && _host.HasDocument;
        Gdi.CopyRows(Source, bits, w, clip);

        IntRect sel = _host.Selection.IsEmpty ? _host.Hover : _host.Selection;
        IntRect hit = sel.Intersect(_bounds).Offset(-_bounds.Left, -_bounds.Top);
        if (hit.IsEmpty)
        {
            // While annotating with nothing selected the desktop stays undimmed: the user is drawing on it, not aiming at it.
            if (!annotating || !sel.IsEmpty) Gdi.Darken(bits, w, clip);
        }
        else
        {
            Gdi.Darken(bits, w, new IntRect(0, 0, w, hit.Top).Intersect(clip));
            Gdi.Darken(bits, w, new IntRect(0, hit.Bottom, w, h - hit.Bottom).Intersect(clip));
            Gdi.Darken(bits, w, new IntRect(0, hit.Top, hit.Left, hit.Height).Intersect(clip));
            Gdi.Darken(bits, w, new IntRect(hit.Right, hit.Top, w - hit.Right, hit.Height).Intersect(clip));
        }

        // Clip GDI and GDI+ drawing to the update rectangle; nothing outside it is blitted.
        ClipTo(clip);

        IntRect r = sel.Offset(-_bounds.Left, -_bounds.Top);
        (int cx, int cy) = _host.Cursor;
        int lx = cx - _bounds.Left, ly = cy - _bounds.Top;
        bool readout = Readout(cx, cy);
        if (_style == FrameStyle.Guides)
        {
            // Byte writes, like the dim: before any GDI drawing of this paint is batched.
            int lines = GuideLines(r, lx, ly, readout);
            for (int i = 0; i < lines; i++) Gdi.TintDashed(bits, w, _lines[i].Intersect(clip), _guides.Dash, horizontal: _lines[i].Height == _guides.Edge, GuideTint);
        }
        if (!hit.IsEmpty)
        {
            switch (_style)
            {
                case FrameStyle.Viewfinder: ViewfinderFrame(bits, clip, r); break;
                case FrameStyle.Guides: GuidesFrame(bits, clip, r); break;
                default: NormalFrame(bits, clip, r); break;
            }
        }
    }

    /// <summary>What lies over the chrome in one rectangle (the lasso and the cursor readout), then the blit. The
    /// in-progress shape and the handles, drawn between the two passes, go onto the double buffer, never into the
    /// back buffer.</summary>
    private unsafe void PaintOver(IntPtr dc, IntRect clip)
    {
        ClipTo(clip);
        byte* bits = (byte*)_bits;
        (int cx, int cy) = _host.Cursor;
        int lx = cx - _bounds.Left, ly = cy - _bounds.Top;
        bool readout = Readout(cx, cy);

        IReadOnlyList<(int X, int Y)> path = _host.Path;
        if (path.Count > 1)
        {
            if (_points.Length < path.Count) _points = new Gdi.Point[Math.Max(path.Count, _points.Length * 2)];
            for (int i = 0; i < path.Count; i++) { _points[i].X = path[i].X - _bounds.Left; _points[i].Y = path[i].Y - _bounds.Top; }
            IntPtr oldPen = Gdi.SelectObject(_memDc, _penLasso);
            Gdi.Polyline(_memDc, _points, path.Count);
            Gdi.SelectObject(_memDc, oldPen);
        }

        if (readout)
        {
            if (_style == FrameStyle.Guides) Loupe(bits, Source, clip, lx, ly);
            else if (_host.PillText(_bounds) is string text) Pill(text, lx, ly);
        }

        Gdi.BitBlt(dc, clip.Left, clip.Top, clip.Width, clip.Height, _memDc, clip.Left, clip.Top, Gdi.SrcCopy);
    }

    /// <summary>How far the Viewfinder hairline and the guide lines lift the pixels under them towards white, of 255.</summary>
    private const int HairlineTint = 140, GuideTint = 90;

    // The frames below take the selection in monitor-local pixels, write the frame's lines straight into the bytes
    // (so they read over dark and bright content alike, with no GDI object), and are called with the DC already
    // clipped to `clip`.

    /// <summary>Normal, the 1.0.1 outline: a white line on the selection's edge pixels inside a black one just outside
    /// them, 1 px at any scaling. The size is in the cursor pill.</summary>
    private unsafe void NormalFrame(byte* bits, IntRect clip, IntRect r)
    {
        ShadeRing(bits, clip, Pad(r, 1), 1, keep: 0);
        TintRing(bits, clip, r, 1, amount: 255);
    }

    /// <summary>Viewfinder: a hairline on the edge, a keylined accent bracket on each of the selection's corners that lies
    /// on this monitor, and the size chip on the monitor holding the top-left corner.</summary>
    private unsafe void ViewfinderFrame(byte* bits, IntRect clip, IntRect r)
    {
        TintRing(bits, clip, r, _viewfinder.Edge, HairlineTint);

        // Every keyline before any arm, so a corner's two arms cover each other's keyline where they meet.
        int arms = _viewfinder.Brackets(r, Local, _arms), k = _viewfinder.Keyline;
        for (int i = 0; i < arms; i++) Fill(Pad(_arms[i], k), _keylineBrush);
        for (int i = 0; i < arms; i++) Fill(_arms[i], _accentBrush);

        if (!Local.Contains(r.Left, r.Top) || _host.SelectionLabel is not string label) return;
        IntPtr oldFont = Gdi.SelectObject(_memDc, _chipFont);
        Gdi.GetTextExtentPoint32W(_memDc, label, label.Length, out Gdi.Size size);
        Bubble(_viewfinder.Chip(r, size.Cx, size.Cy, Local), _viewfinder.ChipRadius, _viewfinder.ChipPadX, _viewfinder.ChipPadY, label);
        Gdi.SelectObject(_memDc, oldFont);
    }

    /// <summary>Guides and loupe: a white hairline on the edge and the size chip below the bottom-right corner, on the
    /// monitor holding that corner. The guide lines and the loupe are painted separately.</summary>
    private unsafe void GuidesFrame(byte* bits, IntRect clip, IntRect r)
    {
        TintRing(bits, clip, r, _guides.Edge, amount: 255);
        if (!Local.Contains(r.Right - 1, r.Bottom - 1) || _host.SelectionLabel is not string label) return;
        IntPtr oldFont = Gdi.SelectObject(_memDc, _chipFont);
        Gdi.GetTextExtentPoint32W(_memDc, label, label.Length, out Gdi.Size size);
        Bubble(_guides.Chip(r, size.Cx, size.Cy, Local), _guides.ChipRadius, _guides.ChipPadX, _guides.ChipPadY, label);
        Gdi.SelectObject(_memDc, oldFont);
    }

    /// <summary>
    /// The pixel loupe for a monitor-local cursor: <see cref="GuidesLayout.Source"/> frame pixels square magnified to
    /// <see cref="GuidesLayout.Cell"/> each, read from <paramref name="source"/> (the frozen frame, or the annotated
    /// buffer) undimmed, on a faint cell grid with the cursor's pixel outlined, inside a white border and a dark
    /// keyline. The coordinates and nits readout sits underneath.
    /// </summary>
    private unsafe void Loupe(byte* bits, byte* source, IntRect clip, int lx, int ly)
    {
        GuidesLayout g = _guides;
        string label = _host.PillText(_bounds) ?? "";
        IntPtr oldFont = Gdi.SelectObject(_memDc, _font);
        Gdi.GetTextExtentPoint32W(_memDc, label, label.Length, out Gdi.Size size);
        (IntRect loupe, IntRect box) = g.Loupe(lx, ly, size.Cx + 2 * g.LabelPadX, size.Cy + 2 * g.LabelPadY, Local);

        Gdi.GdiFlush();   // the chip, chrome and lasso are batched GDI drawing, and these are byte writes
        int w = _bounds.Width, h = _bounds.Height, cell = g.Cell, half = g.Source / 2, border = g.Ring / 2;
        ShadeRing(bits, clip, Pad(loupe, g.Ring), g.Ring - border, keep: 0);
        TintRing(bits, clip, Pad(loupe, border), border, amount: 255);
        for (int j = 0; j < g.Source; j++)
        {
            int sy = ly - half + j;
            for (int i = 0; i < g.Source; i++)
            {
                IntRect c = new IntRect(loupe.Left + i * cell, loupe.Top + j * cell, cell, cell).Intersect(clip);
                if (c.IsEmpty) continue;
                int sx = lx - half + i;
                uint pixel = sx >= 0 && sy >= 0 && sx < w && sy < h ? *(uint*)(source + ((long)sy * w + sx) * 4) : 0xFF000000;
                Gdi.Fill(bits, w, c, pixel);
            }
        }
        for (int i = 1; i < g.Source; i++)
        {
            Gdi.Shade(bits, w, new IntRect(loupe.Left + i * cell, loupe.Top, 1, loupe.Height).Intersect(clip), keep: 200);
            Gdi.Shade(bits, w, new IntRect(loupe.Left, loupe.Top + i * cell, loupe.Width, 1).Intersect(clip), keep: 200);
        }
        var centre = new IntRect(loupe.Left + half * cell, loupe.Top + half * cell, cell, cell);
        TintRing(bits, clip, centre, g.Edge, amount: 255);
        ShadeRing(bits, clip, Pad(centre, g.Edge), g.Edge, keep: 0);

        if (label.Length > 0) Bubble(box, g.LabelRadius, g.LabelPadX, g.LabelPadY, label);
        Gdi.SelectObject(_memDc, oldFont);
    }

    /// <summary>Lifts a ring <paramref name="thickness"/> wide just inside <paramref name="outer"/> towards white.</summary>
    private unsafe void TintRing(byte* bits, IntRect clip, IntRect outer, int thickness, int amount)
    {
        for (int side = 0; side < 4; side++) Gdi.Tint(bits, _bounds.Width, RingSide(outer, thickness, side).Intersect(clip), amount);
    }

    /// <summary>Darkens a ring <paramref name="thickness"/> wide just inside <paramref name="outer"/>, keeping
    /// <paramref name="keep"/>/255 of each channel (0 is black).</summary>
    private unsafe void ShadeRing(byte* bits, IntRect clip, IntRect outer, int thickness, int keep)
    {
        for (int side = 0; side < 4; side++) Gdi.Shade(bits, _bounds.Width, RingSide(outer, thickness, side).Intersect(clip), keep);
    }

    /// <summary>One side of a ring: top and bottom run the full width, left and right fill between them, and no pixel
    /// is in two sides (a second tint would lift it twice).</summary>
    private static IntRect RingSide(IntRect o, int t, int side) => side switch
    {
        0 => IntRect.FromLtrb(o.Left, o.Top, o.Right, Math.Min(o.Bottom, o.Top + t)),
        1 => IntRect.FromLtrb(o.Left, Math.Max(o.Top + t, o.Bottom - t), o.Right, o.Bottom),
        2 => IntRect.FromLtrb(o.Left, o.Top + t, Math.Min(o.Right, o.Left + t), o.Bottom - t),
        _ => IntRect.FromLtrb(Math.Max(o.Left + t, o.Right - t), o.Top + t, o.Right, o.Bottom - t),
    };

    private void Fill(IntRect r, IntPtr brush)
    {
        var rect = new Win32.Rect { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
        Gdi.FillRect(_memDc, ref rect, brush);
    }

    /// <summary>The cursor pill: the host's readout (Normal: size and nits; Viewfinder: nits).</summary>
    private void Pill(string text, int cx, int cy)
    {
        IntPtr oldFont = Gdi.SelectObject(_memDc, _font);
        Gdi.GetTextExtentPoint32W(_memDc, text, text.Length, out Gdi.Size size);
        IntRect p = _pill.Place(cx, cy, size.Cx, size.Cy, _bounds.Width, _bounds.Height);
        Bubble(p, _pill.Radius, _pill.PadX, _pill.PadY, text);
        Gdi.SelectObject(_memDc, oldFont);
    }

    /// <summary>White text on a dark rounded rectangle, in the font already selected: the cursor pill and the size chip.</summary>
    private void Bubble(IntRect box, int radius, int padX, int padY, string text)
    {
        IntPtr oldBrush = Gdi.SelectObject(_memDc, _pillBrush);
        IntPtr oldPen = Gdi.SelectObject(_memDc, NullPen);
        Gdi.RoundRect(_memDc, box.Left, box.Top, box.Right, box.Bottom, radius * 2, radius * 2);
        Gdi.SelectObject(_memDc, oldPen);
        Gdi.SelectObject(_memDc, oldBrush);
        Gdi.SetBkMode(_memDc, Gdi.TransparentBk);
        Gdi.SetTextColor(_memDc, Gdi.Ref(0xFFFFFFFF));
        var area = new Win32.Rect { Left = box.Left + padX, Top = box.Top + padY, Right = box.Right, Bottom = box.Bottom };
        Gdi.DrawTextW(_memDc, text, text.Length, ref area, Gdi.DtLeft | Gdi.DtTop | Gdi.DtSingleLine | Gdi.DtNoPrefix);
    }

    // ----- window procedure -----

    private static IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        OverlayWindow? self;
        lock (Gate) Live.TryGetValue(hwnd, out self);
        // Messages that arrive before CreateWindowExW returns (WM_NCCREATE, WM_CREATE) have no instance yet.
        return self != null ? self.Dispatch(hwnd, msg, wParam, lParam) : Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// <see cref="Proc"/> is a raw function pointer held by the window class, so an exception must not unwind through
    /// <c>user32!DispatchMessage</c> (a silent process kill with the frozen desktop still on screen). Log it and ask
    /// the host to end the session.
    /// </summary>
    private IntPtr Dispatch(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try { return DispatchCore(hwnd, msg, wParam, lParam); }
        catch (Exception e)
        {
            _log?.Error($"overlay: wndproc msg 0x{msg:X4}: {e}");
            try { _host.Fail(e); } catch (Exception inner) { _log?.Error("overlay: cancelling after that failed too: " + inner); }
            return IntPtr.Zero;
        }
    }

    private IntPtr DispatchCore(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WmPaint: OnPaint(); return IntPtr.Zero;
            case Win32.WmEraseBkgnd: return (IntPtr)1;                       // every pixel comes from the double buffer
            case Win32.WmSetCursor: Win32.SetCursor(Handle(_cursor)); return (IntPtr)1;
            case Win32.WmMouseMove: _host.OnMouseMove(ScreenX(lParam), ScreenY(lParam)); return IntPtr.Zero;
            case Win32.WmLButtonDown:
                Win32.SetCapture(hwnd);                                      // a drag continues onto the next monitor
                _host.OnMouseDown(ScreenX(lParam), ScreenY(lParam));
                return IntPtr.Zero;
            case Win32.WmLButtonUp:
                _releasingCapture = true;
                Win32.ReleaseCapture();                                      // sends WM_CAPTURECHANGED to us, synchronously
                _releasingCapture = false;
                _host.OnMouseUp(ScreenX(lParam), ScreenY(lParam));
                return IntPtr.Zero;
            case Win32.WmCaptureChanged:
                // Something else took the mouse, so the button-up that would end the drag will never arrive.
                if (!_releasingCapture) _host.OnCaptureLost();
                return IntPtr.Zero;
            case Win32.WmRButtonUp: _host.OnRightClick(); return IntPtr.Zero;
            case Win32.WmKeyDown or Win32.WmSysKeyDown:
                _host.OnKey((int)wParam, Win32.KeyDown(Win32.VkControl), Win32.KeyDown(Win32.VkShift), Win32.KeyDown(Win32.VkMenu));
                return IntPtr.Zero;                                          // handled: Alt must not open a system menu
            case Win32.WmActivate:
                if (Win32.LoWord(wParam) != 0) _host.OnActivated(hwnd);      // the host tracks which window has the keyboard
                return IntPtr.Zero;
            case Win32.WmDestroy:
                lock (Gate) Live.Remove(hwnd);
                return IntPtr.Zero;
            default: return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    // Client pixels to virtual-desktop pixels. Signed: with the mouse captured, a drag reports negative coordinates.
    private int ScreenX(IntPtr lParam) => Win32.LoWord(lParam) + _bounds.Left;
    private int ScreenY(IntPtr lParam) => Win32.HiWord(lParam) + _bounds.Top;

    private static void Register(IntPtr instance)
    {
        IntPtr className = Marshal.StringToHGlobalUni(ClassName);
        try
        {
            var wc = new Win32.WndClassEx
            {
                Size = (uint)Marshal.SizeOf<Win32.WndClassEx>(),
                Style = Win32.CsOwnDc,          // one DC per window: the paint path asks for it thousands of times
                WndProc = Marshal.GetFunctionPointerForDelegate(StaticProc),
                Instance = instance,
                Cursor = IntPtr.Zero,           // no class cursor, so WM_SETCURSOR is ours to answer
                ClassName = className,
            };
            if (Win32.RegisterClassExW(ref wc) == 0) throw new InvalidOperationException("RegisterClassExW failed: " + Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(className); }
        _registered = true;
    }

    /// <summary>
    /// Drops the class registration. UnregisterClassW fails while any window of the class exists, so the flag follows
    /// the call's result; otherwise the next snip would fail to register an existing class.
    /// </summary>
    private void Unregister(IntPtr instance)
    {
        _registered = !Win32.UnregisterClassW(ClassName, instance);
        if (_registered) _log?.Warn($"overlay: UnregisterClassW({ClassName}) failed: {Marshal.GetLastWin32Error()}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Hwnd != IntPtr.Zero) Win32.DestroyWindow(Hwnd);   // WM_DESTROY takes this window out of the instance map
        lock (Gate)
        {
            Live.Remove(Hwnd);
            // The last window unregisters the class; the next snip registers it again.
            if (Live.Count == 0 && _registered) Unregister(Win32.GetModuleHandleW(null));
        }
        Release();
    }

    /// <summary>Frees the GDI objects and the pins; safe to call from a half-built constructor and from Dispose.</summary>
    private void Release()
    {
        if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero) Gdi.SelectObject(_memDc, _oldBitmap);
        foreach (IntPtr h in new[] { _dib, _penLasso, _pillBrush, _accentBrush, _keylineBrush, _updateRgn, _font, _chipFont })
            if (h != IntPtr.Zero) Gdi.DeleteObject(h);
        if (_memDc != IntPtr.Zero) Gdi.DeleteDC(_memDc);
        if (_framePin.IsAllocated) _framePin.Free();
        if (_backPin.IsAllocated) _backPin.Free();
        _back = null;
    }
}
