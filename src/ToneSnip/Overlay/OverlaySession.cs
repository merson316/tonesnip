using ToneSnip.App.Capture;
using ToneSnip.App.Interop;
using ToneSnip.Core.Annotate;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Config;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using ToneSnip.Core.Tonemap;
using ToneSnip.Windows.Interop;
using ToneSnip.Windows.Overlay;

namespace ToneSnip.App.Overlay;

/// <summary>
/// Selection state shared by every overlay window; all coordinates are physical virtual-desktop pixels. The windows
/// themselves are plain Win32 (<see cref="OverlayWindow"/>) and know nothing about annotation documents, settings or
/// luminance: this session is their <see cref="IOverlayHost"/>.
/// </summary>
public sealed class OverlaySession(List<CapturedOutput> outputs, FrameGrabber grabber, IntRect desktop, SnipMode mode, SnipSettings settings, ILog log) : IOverlayHost
{
    /// <summary>The frozen frames, until <see cref="Finish"/>. A field rather than the constructor parameter so a
    /// finished session stops referring to them: the frames are pooled, and a late message or queued dispatcher turn
    /// must not read buffers the next snip is already writing into.</summary>
    private List<CapturedOutput> _outputs = outputs;
    private static readonly List<CapturedOutput> NoOutputs = new();
    private readonly TaskCompletionSource<OverlayOutcome> _done = new();
    private readonly List<OverlayWindow> _windows = new();
    private ToolbarWindow? _toolbar;
    private Annotate.TextEntryWindow? _textWindow;
    private readonly HashSet<IntPtr> _ownHwnds = new();
    /// <summary>Popups (toolbar, countdown, text box) made owned windows of the focused overlay frame, so they stay above
    /// it when it activates. Windows destroys owned windows with their owner, so these are disowned before the frames
    /// go.</summary>
    private readonly List<IntPtr> _owned = new();
    /// <summary>Window-mode candidates, enumerated once per session: the desktop is frozen while the overlay is up, so
    /// hovering is a linear scan with no allocation or DWM call per move.</summary>
    private List<WindowInfo>? _windowList;
    /// <summary>Lookups for the paint and input paths.</summary>
    private readonly Dictionary<IntRect, CapturedOutput> _byMonitor = new();
    private readonly Dictionary<IntPtr, OverlayWindow> _byHwnd = new();
    /// <summary>The single crop the zebra pass takes, reused because it runs on the paint path.</summary>
    private readonly (IntRect Bounds, HalfImage Half)[] _zebraCrop = new (IntRect, HalfImage)[1];
    private OverlayWindow? _focus;
    private (int X, int Y)? _dragStart;
    private readonly List<(int X, int Y)> _path = new();
    private EditSession? _edit;
    private bool _annotating;
    private bool _drawTool = true;              // false after a capture mode was picked while annotating
    private bool _finishing;
    private bool _firstPaint;
    private Style? _styleToSave;                // written once, on Finish, instead of on every palette click
    /// <summary>"annotate" flow: the selection made before annotating, kept while the user draws on the frozen desktop.</summary>
    private IntRect _pendingSelection = IntRect.Empty;
    /// <summary>The freeform outline that produced <see cref="_pendingSelection"/>, so a lasso keeps its mask through the annotate step.</summary>
    private IReadOnlyList<(int X, int Y)>? _pendingPath;

    public SnipSettings Settings => settings;
    public EditSession? Edit => _edit;
    /// <summary>
    /// The desktop accent when the session started, deliberately not live. It is a colour in the annotation document:
    /// tracking a theme or accent change would repaint strokes the user already drew. Chrome follows the theme; content
    /// does not.
    /// </summary>
    public uint Accent { get; } = Theme.ThemeManager.AccentArgb;
    /// <summary>The selection brackets' colour. Chrome, not content, but read once like <see cref="Accent"/>: the
    /// windows build their brush when they are created.</summary>
    public uint FrameAccent { get; } = Theme.ThemeManager.OverlayAccentArgb;
    /// <summary>The selection frame from Settings, as the snip started.</summary>
    public FrameStyle FrameStyle { get; } = FrameStyles.Parse(settings.SelectionFrame);
    public bool AnyHdr => _outputs.Any(o => o.Half != null);
    /// <summary>True in the "annotate" flow once a selection exists: the bar shows Done and Enter saves.</summary>
    public bool ShowDone => _annotating && !_pendingSelection.IsEmpty;
    /// <summary>True while a drawing tool owns the mouse; selection modes take over when the user picks one.</summary>
    public bool DrawingActive => _annotating && _edit != null && _drawTool;
    public bool HasDocument => _edit != null;
    /// <summary>Raised on the first overlay window's first paint, for timing.</summary>
    public Action? FirstPaint { get; set; }

#if TONESNIP_HARNESS
    /// <summary>The frozen-frame windows, so the memory test can check they are collected after <see cref="Finish"/>.
    /// Empty once the session has finished.</summary>
    internal IReadOnlyList<OverlayWindow> WindowsForHarness => _windows;
#endif

    public bool Annotating
    {
        get => _annotating;
        set
        {
            if (_annotating == value) return;
            _annotating = value;
            if (!value) CancelEditDrag();       // the mouse goes back to selection: no stroke may resume later
            if (value)
            {
                _drawTool = true;
                _dragStart = null; _path.Clear();
                if (_edit == null)
                {
                    var doc = new AnnotationDoc { Current = settings.Annotate.ToStyle(Accent), Private = settings.Annotate.PrivacyMode };
                    _edit = new EditSession(doc) { Tool = Tool.Pen };
                    _edit.Changed += dirty => { foreach (OverlayWindow w in _windows) w.RenderDirty(dirty); };
                    _edit.TextRequested += () => _ = AskText();
                }
            }
            ToolbarChanged?.Invoke();
            RenderAllFull();   // the dim policy changes with the annotate state
        }
    }

    public SnipMode Mode
    {
        get => mode;
        set
        {
            bool wasDrawing = DrawingActive;
            _drawTool = false;                  // picking a capture mode hands the mouse back to selection
            if (wasDrawing) CancelEditDrag();
            if (mode == value)
            {
                if (wasDrawing) { ToolbarChanged?.Invoke(); RenderAll(); }   // same mode, but the mouse changed hands
                return;
            }
            mode = value; _dragStart = null; _path.Clear(); Selection = IntRect.Empty; Hover = IntRect.Empty;
            ToolbarChanged?.Invoke();
            RenderAll();
        }
    }
    /// <summary>Current rectangle (drag in progress, pending nudge, hovered window/monitor).</summary>
    public IntRect Selection { get; private set; } = IntRect.Empty;
    public IntRect Hover { get; private set; } = IntRect.Empty;
    public IReadOnlyList<(int X, int Y)> Path => _path;
    public (int X, int Y) Cursor { get; private set; }
    public Action? ToolbarChanged { get; set; }

    public Task<OverlayOutcome> Show()
    {
        try
        {
            (int cx, int cy) = Native.CursorPos();
            Cursor = (cx, cy);
            OverlayWindow? focus = null;
            FrameGrabber? pool = grabber;   // the leak and screenshot harnesses build a session with none
            foreach (CapturedOutput o in _outputs)
            {
                // The annotated back buffer is a full-monitor image, so it comes from the grabber's pool too.
                var w = new OverlayWindow(o.Info.Bounds, o.Sdr, this, log,
                                          pool == null ? null : (bw, bh) => pool.BackBuffer(o.Info, bw, bh));
                _windows.Add(w);
                _byMonitor[o.Info.Bounds] = o;
                _byHwnd[w.Hwnd] = w;
                w.Show();
                _ownHwnds.Add(w.Hwnd);
                if (o.Info.Bounds.Contains(cx, cy)) focus = w;
            }
            _focus = focus ?? _windows[0];
            // The annotate state decides the dim, so it is set before the first paint rather than repainting after it.
            // The toolbar does not exist yet; it reads the state when it is first placed.
            if (settings.AfterSelect == "annotateFirst") Annotating = true;
            // ShowWindow only queues WM_PAINT, which would wait behind the toolbar's XAML build and the window
            // enumeration below: paint the frozen frames now, so they are on screen before any of that starts (and the
            // log's "overlay shown" is the time to pixels, not to the end of Show).
            foreach (OverlayWindow w in _windows) w.PaintNow();
            if (_finishing) return _done.Task;   // a paint that failed has already ended the session
            _focus.Activate();
            IntRect monitor = _outputs.FirstOrDefault(o => o.Info.Bounds.Contains(cx, cy))?.Info.Bounds ?? _outputs[0].Info.Bounds;
            var toolbar = new ToolbarWindow(this, monitor);
            _toolbar = toolbar;
            _ownHwnds.Add(toolbar.Hwnd);
            toolbar.Owner = _focus.Hwnd;
            _owned.Add(toolbar.Hwnd);
            if (CountdownWindow.Live is { } countdown)
            {
                _ownHwnds.Add(countdown);   // never offer to snip our own countdown
                Own(countdown);             // and keep it above the frames, like the toolbar
            }
            ToolbarChanged = toolbar.Refresh;
            // Enumerated once, after every own window is registered, so window mode does not enumerate per mouse move.
            if (mode == SnipMode.Window) _windowList = WindowFinder.TopLevel(_ownHwnds);
            log.Debug($"overlay: {_windows.Count} windows, mode {mode}, annotating {_annotating}");
            return _done.Task;
        }
        catch
        {
            // Take down any windows that did build, so a partial failure does not leave topmost frozen frames on screen.
            Finish(OverlayOutcome.Cancelled);
            throw;
        }
    }

    /// <summary>Paints every frozen-frame window's pending update at once; the paint watchdog's last check before it
    /// gives up on an overlay that has not painted.</summary>
    public void PaintNow() { foreach (OverlayWindow w in _windows) w.PaintNow(); }

    public void RenderAll() { foreach (OverlayWindow w in _windows) { w.SetCursor(WantedCursor); w.Render(); } }
    public void RenderAllFull() { foreach (OverlayWindow w in _windows) { w.SetCursor(WantedCursor); w.RenderFull(); } }

    /// <summary>Dragging a shape around with the select tool is a pointing job, not an aiming one.</summary>
    private OverlayCursor WantedCursor => DrawingActive && _edit?.Tool == Tool.Select ? OverlayCursor.Arrow : OverlayCursor.Cross;

    /// <summary>A tool button or tool key was used: drawing takes the mouse back from the capture modes.</summary>
    public void UseDrawTool()
    {
        _drawTool = true;
        _dragStart = null; _path.Clear();       // a selection drag in flight does not survive the switch
        Selection = _pendingSelection;          // Empty outside the "annotate" flow, which keeps its outline while drawing
        Hover = IntRect.Empty;
        RenderAll();
    }

    /// <summary>Routing left the drawing tools mid-stroke: drop it, or the next move and release would commit a phantom shape.</summary>
    private void CancelEditDrag() { if (_edit is { } e && (e.Busy || e.InProgress != null)) e.CancelDrag(); }

    private bool _exposurePreviewed;
    private ExposurePreview? _exposure;

    /// <summary>Exposure preview while annotating: re-tonemaps every HDR output's frozen frame in place, throttled to one pass per ~16 ms while the slider moves.</summary>
    public void SetExposure(float multiplier)
        => (_exposure ??= new ExposurePreview(ExposurePass, e => log.Error("exposure: " + e))).Set(multiplier);

    private async Task<bool> ExposurePass(float e)
    {
        _exposurePreviewed = true;
        List<CapturedOutput> frames = _outputs;   // snapshot: Finish may drop the field while this pass runs
        await Task.Run(() => { foreach (CapturedOutput o in frames) if (o.Half != null) grabber.TonemapInto(o.Half, o.Info, settings.Exposure * e, o.Sdr); });
        if (_finishing) return false;
        if (_edit != null) Annotate.ShapeRenderer.InvalidateRedactions(_edit.Doc);   // the base frames changed under the cached tiles
        foreach (OverlayWindow w in _windows) { w.InvalidateBack(); w.RenderDirty(IntRect.Empty); }
        return true;
    }

    /// <summary>Rebuilds every window's annotated back buffer from the current Sdr frames (e.g. after Zebra toggles), without re-tonemapping.</summary>
    public void RefreshBackBuffers() { foreach (OverlayWindow w in _windows) { w.InvalidateBack(); w.RenderDirty(IntRect.Empty); } }

    /// <summary>Gives keyboard focus back to an overlay window after a toolbar click. The popups are owned by that frame,
    /// so they stay above it without being raised again.</summary>
    public void RefocusOverlay() => _focus?.Activate();

    /// <summary>Makes a popup an owned window of the focused frame, and remembers it so the owner can follow the focus
    /// to another monitor and be cleared before the frames are destroyed.</summary>
    private void Own(IntPtr popup)
    {
        if (popup == IntPtr.Zero || _focus == null) return;
        Native.SetOwner(popup, _focus.Hwnd);
        if (!_owned.Contains(popup)) _owned.Add(popup);
    }

    /// <summary>Moves the popups' owner to the newly focused frame (e.g. after a click on another monitor), so they stay
    /// above it.</summary>
    private void Reown()
    {
        if (_focus is not { } f) return;
        foreach (IntPtr h in _owned) Native.SetOwner(h, f.Hwnd);
    }

    /// <summary>Cuts every popup loose. Destroying a window destroys what it owns, so this runs before the frames do.</summary>
    private void Disown()
    {
        foreach (IntPtr h in _owned) Native.SetOwner(h, IntPtr.Zero);
        _owned.Clear();
    }

    // ----- IOverlayHost -----

    /// <summary>The frozen frame plus the annotation document, for one monitor's back buffer.</summary>
    private Func<BgraImage, IntRect, BgraImage, IntRect?, IntRect>? _renderShapes;
    public Func<BgraImage, IntRect, BgraImage, IntRect?, IntRect> RenderShapes => _renderShapes ??= RenderShapesCore;

    private IntRect RenderShapesCore(BgraImage baseImg, IntRect monitor, BgraImage target, IntRect? dirty)
        => _edit is { } edit
            ? Annotate.ShapeRenderer.Render(edit.Doc, baseImg, monitor, target, dirty, Accent, Annotate.ShapeRenderer.DragGhostId(edit))
            : IntRect.Empty;

    private Action<IntPtr, IntRect>? _drawChrome;
    public Action<IntPtr, IntRect> DrawChrome => _drawChrome ??= DrawChromeCore;

    /// <summary>
    /// Draws the in-progress shape, selection handles and crop marquee onto the overlay's device context. The renderer
    /// wraps the HDC in GDI+ graphics, so chrome and baked shapes share one code path while the windows stay plain GDI.
    /// </summary>
    private void DrawChromeCore(IntPtr hdc, IntRect monitor)
    {
        if (_edit is not { } edit) return;
        Annotate.ShapeRenderer.Chrome(edit, monitor, hdc, monitor.Width, monitor.Height, Accent);
    }

    private Action<BgraImage, IntRect, IntRect>? _zebra;
    public Action<BgraImage, IntRect, IntRect> Zebra => _zebra ??= ZebraCore;

    private void ZebraCore(BgraImage back, IntRect monitor, IntRect painted)
    {
        if (_edit is not { } edit || !edit.Doc.Zebra) return;
        if (!_byMonitor.TryGetValue(monitor, out CapturedOutput? o) || o.Half == null) return;
        _zebraCrop[0] = (monitor, o.Half);
        Annotate.ShapeRenderer.Zebra(back, monitor, _zebraCrop,
            (settings.SdrWhiteNits ?? o.Info.SdrWhiteNits) / 80f, settings.Exposure * edit.Doc.Exposure, painted);
    }

    /// <summary>The cursor readout: the selection's size and the nits (Normal), the nits alone (Viewfinder, whose size is
    /// on its chip), or the cursor's coordinates and the nits under the loupe (Guides).</summary>
    /// <param name="monitor">The window asking; the pill belongs to whichever monitor the cursor is on.</param>
    public string? PillText(IntRect monitor)
    {
        if (!monitor.Contains(Cursor.X, Cursor.Y)) return null;
        // Asked by every window on every mouse move, so cached until something it reads changes.
        var key = (Cursor.X, Cursor.Y, Selection, Hover, settings.ShowNitsReadout);
        if (_pillKey == key) return _pillText;
        _pillKey = key;
        return _pillText = BuildPillText();
    }

    private string? BuildPillText()
    {
        string? nits = NitsText();
        switch (FrameStyle)
        {
            case FrameStyle.Viewfinder: return nits;
            case FrameStyle.Guides: return nits == null ? $"{Cursor.X}, {Cursor.Y}" : $"{Cursor.X}, {Cursor.Y}  ·  {nits}";
            default:
                IntRect sel = Selection.IsEmpty ? Hover : Selection;
                string? size = sel.IsEmpty ? null : $"{sel.Width} × {sel.Height}";
                return nits == null ? size : size == null ? nits : size + "   " + nits;
        }
    }

    private (int, int, IntRect, IntRect, bool)? _pillKey;
    private string? _pillText;

    /// <summary>The frame's size chip. Asked on every paint, so the string is kept until the rectangle changes.</summary>
    public string? SelectionLabel
    {
        get
        {
            IntRect sel = Selection.IsEmpty ? Hover : Selection;
            if (sel.IsEmpty || FrameStyle == FrameStyle.Normal) return null;
            if (sel != _labelFor) { _labelFor = sel; _label = $"{sel.Width} × {sel.Height}"; }
            return _label;
        }
    }

    private IntRect _labelFor = IntRect.Empty;
    private string? _label;

    /// <summary>
    /// An overlay window took the foreground. It becomes the window <see cref="RefocusOverlay"/> returns focus to, and
    /// the popups are re-owned by it so they stay above the frame the user is on.
    /// </summary>
    public void OnActivated(IntPtr hwnd)
    {
        if (_byHwnd.TryGetValue(hwnd, out OverlayWindow? w)) _focus = w;
        Reown();
    }

    /// <summary>
    /// The mouse capture was taken away mid-drag, so no button-up is coming: drop the drag, or the next move would keep
    /// extending the selection with no button held.
    /// </summary>
    public void OnCaptureLost()
    {
        if (_finishing) return;   // DestroyWindow releases the capture too, and by then there is nothing left to render
        if (DrawingActive) { CancelEditDrag(); RenderAll(); return; }
        if (_dragStart == null) return;
        _dragStart = null;
        if (mode == SnipMode.Rectangle) Selection = IntRect.Empty;
        else if (mode == SnipMode.Freeform) _path.Clear();
        RenderAll();
    }

    public void OnFirstPaint()
    {
        if (_firstPaint) return;
        _firstPaint = true;
        FirstPaint?.Invoke();
    }

    public void OnRightClick() => Finish(OverlayOutcome.Cancelled);

    /// <summary>A window procedure caught an exception. The frozen desktop is taken down rather than left on screen.</summary>
    public void Fail(Exception error)
    {
        log.Error("overlay: " + error);
        Finish(OverlayOutcome.Cancelled);
    }

    public void OnMouseMove(int x, int y)
    {
        Cursor = (x, y);
        if (DrawingActive && _edit != null) { _edit.Move(x, y, Mods()); return; }
        switch (mode)
        {
            case SnipMode.Rectangle when _dragStart != null: Selection = IntRect.FromDrag(_dragStart.Value.X, _dragStart.Value.Y, x, y).Clamp(desktop); break;
            case SnipMode.Window: Hover = WindowAt(x, y)?.Bounds.Clamp(desktop) ?? IntRect.Empty; break;
            case SnipMode.FullScreen: Hover = MonitorAt(x, y); break;
            case SnipMode.Freeform when _dragStart != null:
                if (_path.Count == 0 || Math.Abs(_path[^1].X - x) + Math.Abs(_path[^1].Y - y) >= 2) _path.Add((x, y));
                break;
        }
        RenderAll();
    }

    /// <summary>The front-most window under a point, from the list cached for this session. A loop, not LINQ: this runs
    /// on every mouse move.</summary>
    private WindowInfo? WindowAt(int x, int y)
    {
        _windowList ??= WindowFinder.TopLevel(_ownHwnds);
        foreach (WindowInfo w in _windowList) if (w.Bounds.Contains(x, y)) return w;
        return null;
    }

    /// <summary>The bounds of the monitor holding a point, or Empty. A loop, not LINQ: this runs on every mouse move.</summary>
    private IntRect MonitorAt(int x, int y)
    {
        foreach (CapturedOutput o in _outputs) if (o.Info.Bounds.Contains(x, y)) return o.Info.Bounds;
        return IntRect.Empty;
    }

    public void OnMouseDown(int x, int y)
    {
        Cursor = (x, y);
        _toolbar?.ClosePopups();   // the toolbar window never activates, so a click on the desktop must close its pickers explicitly
        if (DrawingActive && _edit != null) { _edit.Begin(x, y, Mods()); return; }
        switch (mode)
        {
            case SnipMode.Rectangle: _dragStart = (x, y); Selection = IntRect.FromDrag(x, y, x, y); break;
            case SnipMode.Freeform: _dragStart = (x, y); _path.Clear(); _path.Add((x, y)); break;
            case SnipMode.Window: if (!Hover.IsEmpty) Complete(Hover, null); break;
            case SnipMode.FullScreen: if (!Hover.IsEmpty) Complete(Hover, null); break;
        }
        RenderAll();
    }

    public void OnMouseUp(int x, int y)
    {
        if (DrawingActive && _edit != null) { _edit.End(x, y, Mods()); return; }
        if (_dragStart == null) return;
        OnMouseMove(x, y);
        bool click = Math.Abs(_dragStart.Value.X - x) < 3 && Math.Abs(_dragStart.Value.Y - y) < 3;
        _dragStart = null;
        if (mode == SnipMode.Rectangle)
        {
            if (click) { Selection = IntRect.Empty; RenderAll(); return; }   // a click without a drag: keep the overlay, nothing selected
            Complete(Selection, null);
        }
        else if (mode == SnipMode.Freeform)
        {
            IntRect box = FreeformMask.BoundingBox(_path).Clamp(desktop);
            if (_path.Count < 3 || box.IsEmpty) { _path.Clear(); RenderAll(); return; }
            Complete(box, _path.ToList());
        }
    }

    /// <summary>
    /// A selection was made. In the "annotate" flow the first selection keeps the overlay up so the user can draw and
    /// then press Done or Enter; otherwise the snip finishes. A freeform lasso shows its bounding box while annotating,
    /// and its outline is kept so the saved snip is still masked to the drawn shape.
    /// </summary>
    private void Complete(IntRect region, IReadOnlyList<(int X, int Y)>? path)
    {
        if (settings.AfterSelect == "annotate" && _pendingSelection.IsEmpty)
        {
            _pendingSelection = region; _pendingPath = path; Selection = region;
            _path.Clear();          // the lasso is kept in _pendingPath; the overlay now outlines its bounding box instead
            Annotating = true; return;
        }
        Finish(new OverlayOutcome(region, path, -1, _edit?.Doc, _edit?.Doc.Exposure ?? 1f));
    }

    /// <summary>Done in the tool row (and Enter): saves the selection the user drew on.</summary>
    public void Done() { if (!_pendingSelection.IsEmpty) Finish(new OverlayOutcome(_pendingSelection, _pendingPath, -1, _edit?.Doc, _edit?.Doc.Exposure ?? 1f)); }

    /// <summary>Shift and Control as the shapes see them, read from the keyboard rather than from a framework.</summary>
    private static InputMods Mods()
        => (Win32.KeyDown(Win32.VkShift) ? InputMods.Shift : 0) | (Win32.KeyDown(Win32.VkControl) ? InputMods.Ctrl : 0);

    public void OnKey(int vk, bool ctrl, bool shift, bool alt)
    {
        if (vk == Win32.VkEscape)
        {
            // Escape peels off annotation state first (pending text, marquee, drag, selection) and cancels the snip last.
            if (_annotating && _edit != null && _edit.Escape()) { RenderAll(); return; }
            Finish(OverlayOutcome.Cancelled); return;
        }
        if (vk == Win32.VkA && !ctrl && !shift && !alt) { Annotating = !Annotating; return; }
        if (_annotating && _toolbar != null)
        {
            // Only a tool letter hands the mouse back to drawing; undo, redo, delete and crop-apply must leave a
            // capture mode (and the selection drag in flight) exactly where it is.
            Annotate.AnnotateBar.KeyHandled handled = _toolbar.HandleAnnotateKey(vk, ctrl);
            if (handled == Annotate.AnnotateBar.KeyHandled.Tool) { UseDrawTool(); return; }
            if (handled == Annotate.AnnotateBar.KeyHandled.Command) { RenderAll(); return; }
        }
        if (vk == Win32.VkReturn && ShowDone) { Done(); return; }
        switch (vk)
        {
            case Win32.VkReturn: if (!Selection.IsEmpty) Complete(Selection, null); else if (!Hover.IsEmpty) Complete(Hover, null); return;
            case Win32.VkR: Mode = SnipMode.Rectangle; break;
            case Win32.VkW: Mode = SnipMode.Window; break;
            case Win32.VkF: Mode = SnipMode.FullScreen; break;
            case Win32.VkL: Mode = SnipMode.Freeform; break;
            case Win32.VkLeft or Win32.VkRight or Win32.VkUp or Win32.VkDown when !Selection.IsEmpty:
                int step = shift ? 10 : 1;
                Selection = Selection.Nudge(vk == Win32.VkLeft ? -step : vk == Win32.VkRight ? step : 0, vk == Win32.VkUp ? -step : vk == Win32.VkDown ? step : 0, desktop);
                break;
        }
        RenderAll();
    }

    /// <summary>Toolbar delay pick: close and let the session restart after the delay.</summary>
    public void RestartWithDelay(int seconds) => Finish(new OverlayOutcome(IntRect.Empty, null, seconds));

    public void Finish(OverlayOutcome outcome)
    {
        if (_finishing || _done.Task.IsCompleted) return;
        _finishing = true;
        _statsCatchUp?.Stop();
        // Before the frames go: an owned window is destroyed with its owner, and the toolbar and text box are WinUI
        // windows that must be closed through Close().
        Disown();
        foreach (OverlayWindow w in _windows) { try { w.Dispose(); } catch { } }
        _windows.Clear();
        _byHwnd.Clear(); _byMonitor.Clear();
        _focus = null;
        try { _toolbar?.Close(); } catch { }
        _toolbar = null;
        try { _textWindow?.Close(); } catch { }
        _textWindow = null;
        SaveStyle();
        // Release everything now rather than when the session is collected: the frames are pooled and the next snip
        // writes straight into them.
        _windowList = null;
        _zebraCrop[0] = default;
        _path.Clear(); _pendingPath = null;
        _ownHwnds.Clear();
        _outputs = NoOutputs;
        if (_edit is { } edit)
        {
            // The document outlives the session (it travels on in the result). Its cached redaction tiles hold the
            // pooled frames they were rendered from, and its Changed event reaches this session through EditSession,
            // so both links are cut here.
            Annotate.ShapeRenderer.InvalidateRedactions(edit.Doc);
            edit.Detach();
        }
        _edit = null;
        // An exposure preview pass may still be tonemapping into o.Sdr on a worker thread, and CaptureResult.Build reads
        // those frames as soon as _done completes, so wait for the pass to finish first.
        _exposure?.Cancel();
        outcome = outcome with { ExposurePreviewed = _exposurePreviewed };
        if (_exposure is { Idle.IsCompleted: false }) _ = CompleteAfterExposure(outcome);
        else _done.TrySetResult(outcome);
    }

    private async Task CompleteAfterExposure(OverlayOutcome outcome)
    {
        try { await _exposure!.Idle; }
        catch (Exception e) { log.Warn("exposure: " + e.Message); }
        _done.TrySetResult(outcome);
    }

    /// <summary>The text tool asked for a string: a borderless box at the click point, then back to the overlay.</summary>
    private async Task AskText()
    {
        if (_edit?.PendingText is not (int x, int y)) return;
        try
        {
            var w = Annotate.TextEntryWindow.For(_edit.Style, Accent);
            _textWindow = w;
            _ownHwnds.Add(w.Hwnd);      // window mode must not offer to snip our own box
            // Owned by the frame under the click, so the box stays above it while it has the keyboard.
            Task<string?> typed = w.ShowAt(x, y, _focus?.Hwnd ?? IntPtr.Zero);
            if (w.Owner != IntPtr.Zero) _owned.Add(w.Hwnd);
            string? text = await typed;
            // Disowned before it leaves _owned, since Disown() only reaches windows still in the list.
            Native.SetOwner(w.Hwnd, IntPtr.Zero);
            _owned.Remove(w.Hwnd);
            if (_textWindow == w) _textWindow = null;
            if (_finishing) return;                      // the snip ended; the document is already handed over
            if (text == null) _edit?.CancelText(); else _edit?.CommitText(text);
            RefocusOverlay();
        }
        catch (Exception e)
        {
            log.Warn("text entry: " + e.Message);
            _edit?.CancelText();
        }
    }

    /// <summary>Colour, width and text size stick for the next snip; the file is written once, when the overlay closes.</summary>
    public void PersistStyle(Style s) => _styleToSave = s;

    private void SaveStyle() => App.Current.RememberAnnotateStyle(_styleToSave, settings.Annotate, Accent);

    /// <summary>The rectangle and frame the cached peak/mean were sampled from, and the text.</summary>
    private (IntRect, HalfImage)? _statsFor;
    private string _stats = "";
    /// <summary>When the peak and mean were last sampled, for the throttle while a drag resizes the selection.</summary>
    private long _statsAt;
    /// <summary>A drag changes the rectangle on every mouse move; the peak and mean are sampled at most this often
    /// meanwhile, while the nits under the cursor stay live.</summary>
    private static readonly TimeSpan StatsInterval = TimeSpan.FromMilliseconds(100);
    /// <summary>Fires once after a throttled sample was skipped, so figures held back mid-drag catch up with the
    /// rectangle even when the mouse then stops (no move, no repaint). A field: an unreferenced DispatcherQueueTimer can
    /// be collected before it ticks.</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _statsCatchUp;

    /// <summary>Luminance readout for HDR outputs: nits under the cursor, and peak/mean inside the selection.</summary>
    public string? NitsText()
    {
        if (!settings.ShowNitsReadout) return null;
        CapturedOutput? o = null;   // a loop, not LINQ: this is on the paint path
        foreach (CapturedOutput x in _outputs) if (x.Half != null && x.Info.Bounds.Contains(Cursor.X, Cursor.Y)) { o = x; break; }
        if (o?.Half == null) return null;
        HalfImage f = o.Half;
        float Nits(int x, int y) { (float r, float g, float b) = f.Sample(x - o.Info.Left, y - o.Info.Top); return Core.Color.Transfer.Luminance709(r, g, b) * 80f; }
        string text = $"{Nits(Cursor.X, Cursor.Y):F0} nits";
        IntRect sel = Selection.IsEmpty ? Hover : Selection;
        IntRect hit = sel.Intersect(o.Info.Bounds);
        if (!hit.IsEmpty)
        {
            // Up to 20,000 samples, so only recomputed when the rectangle or frame changes, and during a drag at most
            // every StatsInterval: the figures lag the rectangle by that much until the button is let go.
            bool throttled = _dragStart != null && _statsFor != null && System.Diagnostics.Stopwatch.GetElapsedTime(_statsAt) < StatsInterval;
            if (_statsFor != (hit, f) && throttled) CatchUpStats();
            else if (_statsFor != (hit, f))
            {
                _statsAt = System.Diagnostics.Stopwatch.GetTimestamp();
                float peak = 0, sum = 0; int n = 0, step = Math.Max(1, (int)Math.Sqrt(hit.Width * (long)hit.Height / 20000.0));
                for (int y = hit.Top; y < hit.Bottom; y += step) for (int x = hit.Left; x < hit.Right; x += step) { float v = Nits(x, y); peak = Math.Max(peak, v); sum += v; n++; }
                _statsFor = (hit, f);
                _stats = n > 0 ? $"  peak {peak:F0}  mean {sum / n:F0}" : "";
            }
            text += _stats;
        }
        return text;
    }

    /// <summary>
    /// Re-asks for the readout once the throttle interval has passed since the last sample. The pill's text is cached
    /// per cursor and rectangle, which have not changed, so that cache is dropped first; the repaint then samples the
    /// rectangle as it is now.
    /// </summary>
    private void CatchUpStats()
    {
        if (_statsCatchUp is { IsRunning: true }) return;
        if (_statsCatchUp == null)
        {
            // The harnesses can build a session on a thread with no dispatcher; they do not drag.
            if (Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() is not { } ui) return;
            _statsCatchUp = ui.CreateTimer();
            _statsCatchUp.IsRepeating = false;
            _statsCatchUp.Tick += (_, _) =>
            {
                if (_finishing) return;
                _pillKey = null;
                RenderAll();
            };
        }
        TimeSpan left = StatsInterval - System.Diagnostics.Stopwatch.GetElapsedTime(_statsAt);
        _statsCatchUp.Interval = left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1);
        _statsCatchUp.Start();
    }
}
