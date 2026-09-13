using System.Runtime.InteropServices;
using ToneSnip.Core.Annotate;
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
/// released in <see cref="Dispose"/>. Only the invalidated rectangle is repainted.
/// </para>
/// </summary>
public sealed class OverlayWindow : IDisposable
{
    private const string ClassName = "tonesnip-overlay";
    private const int PillFontPx = 12, PillRadius = 14;

    // One registration per process with a static window procedure that finds the instance by HWND: a per-instance
    // procedure would leave a second window routing its messages to the first instance's (possibly dead) delegate.
    private static readonly Win32.WndProcDelegate StaticProc = Proc;   // static field: the class holds a raw function pointer to it
    private static readonly Dictionary<IntPtr, OverlayWindow> Live = new();
    private static readonly object Gate = new();
    private static bool _registered;

    private static readonly IntPtr CursorCross = Win32.LoadCursorW(IntPtr.Zero, (IntPtr)Win32.IdcCross);
    private static readonly IntPtr CursorArrow = Win32.LoadCursorW(IntPtr.Zero, (IntPtr)Win32.IdcArrow);
    private static readonly IntPtr CursorIBeam = Win32.LoadCursorW(IntPtr.Zero, (IntPtr)Win32.IdcIBeam);
    private static readonly IntPtr NullBrush = Gdi.GetStockObject(Gdi.NullBrush);
    private static readonly IntPtr NullPen = Gdi.GetStockObject(Gdi.NullPen);

    private readonly IntRect _bounds;
    private readonly BgraImage _frame;
    private readonly IOverlayHost _host;
    private readonly ILog? _log;
    private readonly GCHandle _framePin;
    private readonly IntPtr _memDc, _dib, _bits, _oldBitmap;
    private readonly IntPtr _penBlack, _penWhite, _penLasso, _pillBrush, _font;

    // Annotated copy of the frozen frame, allocated when a document first exists and then patched in place. Taken from
    // `backBuffer` when the host has a pool, so a full-monitor buffer is not allocated per snip.
    private readonly Func<int, int, BgraImage>? _backBuffer;
    private BgraImage? _back;
    private GCHandle _backPin;
    private bool _backValid;

    private IntRect _lastDynamic = IntRect.Empty;   // monitor-local area the previous paint's selection, lasso and pill covered
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

        _penBlack = Gdi.CreatePen(Gdi.PsSolid, 1, Gdi.Ref(0xFF000000));
        _penWhite = Gdi.CreatePen(Gdi.PsSolid, 1, Gdi.Ref(0xFFFFFFFF));
        _penLasso = Gdi.CreatePen(Gdi.PsSolid, 2, Gdi.Ref(0xFFFFFFFF));
        _pillBrush = Gdi.CreateSolidBrush(Gdi.Ref(0xFF202020));
        _font = Gdi.CreateFontW(-PillFontPx, 0, 0, 0, 400, 0, 0, 0, Gdi.DefaultCharSet, 0, 0, Gdi.ClearTypeQuality, 0, "Segoe UI");

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
    }

    /// <summary>Puts the window on screen without taking the foreground; <see cref="Activate"/> hands it the keyboard.</summary>
    public void Show() => Win32.ShowWindow(Hwnd, Win32.SwShowNoActivate);

    /// <summary>Foreground plus keyboard focus, even when the snip came from a hotkey while another app was active.</summary>
    public void Activate() => Win32.ForceForeground(Hwnd);

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
    /// Repaints only what can have changed since the last paint: the old and the new selection (the dim state flips
    /// only inside their union), the lasso, and the cursor pill.
    /// </summary>
    public void Render()
    {
        if (_host.HasDocument && !_backValid) { RenderDirty(IntRect.Empty); return; }
        IntRect sel = _host.Selection.IsEmpty ? _host.Hover : _host.Selection;
        bool hasSelection = !sel.IsEmpty;
        // While annotating the whole-monitor dim depends on whether anything is selected, so that transition repaints all.
        if (_host.Annotating && hasSelection != _lastHadSelection) { _lastHadSelection = hasSelection; RenderFull(); return; }
        _lastHadSelection = hasSelection;
        IntRect now = DynamicRegion(sel);
        IntRect dirty = now.Union(_lastDynamic);
        _lastDynamic = now;
        if (dirty.IsEmpty) return;
        Invalidate(dirty);
    }

    /// <summary>Repaints the whole monitor (mode or annotate-state changes).</summary>
    public void RenderFull()
    {
        IntRect sel = _host.Selection.IsEmpty ? _host.Hover : _host.Selection;
        _lastDynamic = DynamicRegion(sel);
        _lastHadSelection = !sel.IsEmpty;
        if (_disposed || Hwnd == IntPtr.Zero) return;
        Win32.InvalidateRect(Hwnd, IntPtr.Zero, false);
    }

    private IntRect DynamicRegion(IntRect sel)
    {
        IntRect local = Local;
        IntRect r = IntRect.Empty;
        if (!sel.IsEmpty) r = Pad(sel.Offset(-_bounds.Left, -_bounds.Top), 2).Intersect(local);
        if (_host.Path.Count > 1) r = r.Union(Pad(FreeformMask.BoundingBox(_host.Path).Offset(-_bounds.Left, -_bounds.Top), 3).Intersect(local));
        if (!_host.DrawingActive)
        {
            (int cx, int cy) = _host.Cursor;
            if (_bounds.Contains(cx, cy)) r = r.Union(new IntRect(cx - _bounds.Left - 460, cy - _bounds.Top - 80, 920, 160).Intersect(local));
        }
        return r;
    }

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
        IntPtr dc = Win32.BeginPaint(Hwnd, out Win32.PaintStruct ps);
        if (dc == IntPtr.Zero) return;   // no DC, no paint — and EndPaint is only owed after a BeginPaint that worked
        bool drew = false;
        try
        {
            IntRect clip = IntRect.FromLtrb(ps.Paint.Left, ps.Paint.Top, ps.Paint.Right, ps.Paint.Bottom).Intersect(Local);
            if (!clip.IsEmpty) { Paint(dc, clip); drew = true; }
        }
        finally { Win32.EndPaint(Hwnd, ref ps); }
        // "Overlay shown" means pixels reached the screen, so an empty update rectangle does not count as the first paint.
        if (!drew || _painted) return;
        _painted = true;
        _host.OnFirstPaint();
    }

    private unsafe void Paint(IntPtr dc, IntRect clip)
    {
        // The annotated buffer replaces the frozen frame as soon as a document exists, so turning the tool row off
        // still shows (and saves) what was drawn; only the live chrome belongs to the annotating state.
        bool annotating = _host.Annotating && _host.HasDocument;
        bool useBack = _host.HasDocument && _backValid && _back != null;
        int w = _bounds.Width, h = _bounds.Height;

        // GDI batches its drawing, so the previous paint's outline or pill could otherwise land on top of these bytes.
        Gdi.GdiFlush();
        byte* bits = (byte*)_bits;
        Gdi.CopyRows((byte*)(useBack ? _backPin : _framePin).AddrOfPinnedObject(), bits, w, clip);

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
        Gdi.SelectClipRgn(_memDc, IntPtr.Zero);
        Gdi.IntersectClipRect(_memDc, clip.Left, clip.Top, clip.Right, clip.Bottom);

        if (!hit.IsEmpty)
        {
            IntRect r = sel.Offset(-_bounds.Left, -_bounds.Top);
            IntPtr oldBrush = Gdi.SelectObject(_memDc, NullBrush);
            IntPtr oldPen = Gdi.SelectObject(_memDc, _penBlack);
            Gdi.Rectangle(_memDc, r.Left - 1, r.Top - 1, r.Right + 1, r.Bottom + 1);
            Gdi.SelectObject(_memDc, _penWhite);
            Gdi.Rectangle(_memDc, r.Left, r.Top, r.Right, r.Bottom);
            Gdi.SelectObject(_memDc, oldPen);
            Gdi.SelectObject(_memDc, oldBrush);
        }

        // The in-progress shape and the handles are painted onto the double buffer, never into the back buffer.
        if (annotating) _host.DrawChrome(_memDc, _bounds);

        IReadOnlyList<(int X, int Y)> path = _host.Path;
        if (path.Count > 1)
        {
            if (_points.Length < path.Count) _points = new Gdi.Point[Math.Max(path.Count, _points.Length * 2)];
            for (int i = 0; i < path.Count; i++) { _points[i].X = path[i].X - _bounds.Left; _points[i].Y = path[i].Y - _bounds.Top; }
            IntPtr oldPen = Gdi.SelectObject(_memDc, _penLasso);
            Gdi.Polyline(_memDc, _points, path.Count);
            Gdi.SelectObject(_memDc, oldPen);
        }

        // A drawing tool repaints only its dirty rectangle, so a cursor-following pill would smear; it is also useless there.
        if (!_host.DrawingActive)
        {
            (int cx, int cy) = _host.Cursor;
            if (_bounds.Contains(cx, cy) && _host.PillText(_bounds) is string text) Pill(text, cx - _bounds.Left, cy - _bounds.Top);
        }

        Gdi.BitBlt(dc, clip.Left, clip.Top, clip.Width, clip.Height, _memDc, clip.Left, clip.Top, Gdi.SrcCopy);
    }

    /// <summary>The cursor pill: size and, on an HDR monitor, the luminance readout.</summary>
    private void Pill(string text, int cx, int cy)
    {
        IntPtr oldFont = Gdi.SelectObject(_memDc, _font);
        Gdi.GetTextExtentPoint32W(_memDc, text, text.Length, out Gdi.Size size);
        int pw = size.Cx + 20, ph = size.Cy + 8;
        int x = cx + 16, y = cy + 24;
        if (x + pw > _bounds.Width) x = cx - pw - 8;
        if (y + ph > _bounds.Height) y = cy - ph - 8;
        x = Math.Max(0, x); y = Math.Max(0, y);
        IntPtr oldBrush = Gdi.SelectObject(_memDc, _pillBrush);
        IntPtr oldPen = Gdi.SelectObject(_memDc, NullPen);
        Gdi.RoundRect(_memDc, x, y, x + pw, y + ph, PillRadius * 2, PillRadius * 2);
        Gdi.SelectObject(_memDc, oldPen);
        Gdi.SelectObject(_memDc, oldBrush);
        Gdi.SetBkMode(_memDc, Gdi.TransparentBk);
        Gdi.SetTextColor(_memDc, Gdi.Ref(0xFFFFFFFF));
        var box = new Win32.Rect { Left = x + 10, Top = y + 4, Right = x + pw, Bottom = y + ph };
        Gdi.DrawTextW(_memDc, text, text.Length, ref box, Gdi.DtLeft | Gdi.DtTop | Gdi.DtSingleLine | Gdi.DtNoPrefix);
        Gdi.SelectObject(_memDc, oldFont);
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
        foreach (IntPtr h in new[] { _dib, _penBlack, _penWhite, _penLasso, _pillBrush, _font })
            if (h != IntPtr.Zero) Gdi.DeleteObject(h);
        if (_memDc != IntPtr.Zero) Gdi.DeleteDC(_memDc);
        if (_framePin.IsAllocated) _framePin.Free();
        if (_backPin.IsAllocated) _backPin.Free();
        _back = null;
    }
}
