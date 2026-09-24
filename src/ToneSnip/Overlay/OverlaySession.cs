using ToneSnip.App.Capture;
using ToneSnip.App.Interop;
using ToneSnip.Core.Annotate;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Config;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Hdr;
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
    /// <summary>Each HDR monitor's zebra mask and the white and exposure it was computed for: the paint path reuses it
    /// until the exposure changes, since the frame may be on the graphics card.</summary>
    private readonly Dictionary<IntRect, (ZebraMask Mask, float White, float Exposure)> _zebraMasks = new();
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
    /// <summary>What Narrator reads when the frozen desktop takes the keyboard: what the screen is for and its keys.
    /// Changes after that are spoken through the toolbar's live region (<see cref="Announce"/>).</summary>
    public string WindowTitle => "ToneSnip: select an area. Arrow keys move the pointer, Space starts and finishes a selection, "
                               + "Tab picks a window. R W F L switch modes, T copies text, P pins, C picks a colour, A annotates, Escape cancels";

    /// <summary>The mode as the toolbar's live region says it.</summary>
    private static string ModeName(SnipMode m) => m switch
    {
        SnipMode.Window => "Window mode",
        SnipMode.FullScreen => "Full screen mode",
        SnipMode.Freeform => "Freeform mode",
        _ => "Rectangle mode",
    };
    public bool AnyHdr => _outputs.Any(o => o.ReadableHdr != null);
    /// <summary>True in the "annotate" flow once a selection exists: the bar shows Done and Enter saves.</summary>
    public bool ShowDone => _annotating && !_pendingSelection.IsEmpty;
    /// <summary>True while a drawing tool owns the mouse; selection modes take over when the user picks one.</summary>
    public bool DrawingActive => _annotating && _edit != null && _drawTool;
    public bool HasDocument => _edit != null;
    /// <summary>The colour picker is armed (C, or the toolbar's picker): the loupe follows the cursor and the next click,
    /// C, Space or Enter copies the colour under it.</summary>
    public bool Picking => _picking;
    private bool _picking;
    /// <summary>The button went down on a pick, so its button-up belongs to nothing.</summary>
    private bool _pickClick;
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
            Announce?.Invoke(value ? "Annotating: the tool row is open" : "Annotating off");
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
            _keyAnchor = null; _tabIndex = -1;
            ToolbarChanged?.Invoke();
            Announce?.Invoke(ModeName(value));
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
            Announce = toolbar.Announce;
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
    private OverlayCursor WantedCursor => !_picking && DrawingActive && _edit?.Tool == Tool.Select ? OverlayCursor.Arrow : OverlayCursor.Cross;

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
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        await Task.Run(() =>
        {
            foreach (CapturedOutput o in frames)
            {
                if (o.ReadableHdr is not { } frame) continue;
                // A lost frame keeps the image it last had; the loss is logged where it was found.
                try { frame.Tonemap(grabber.CurveFor(o.Info, settings.Exposure * e), new IntRect(0, 0, o.Info.Width, o.Info.Height), o.Sdr, 0, 0); }
                catch (HdrFrameLostException) { }
            }
        });
        log.Debug($"exposure: pass at {e:F2} in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms");
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

    /// <summary>
    /// Paints the zebra from the monitor's cached mask. A mask for another white or exposure is rebuilt off the UI
    /// thread (<see cref="RebuildZebra"/>) while the old one stays up: building it waits for the device, behind an
    /// exposure pass, and allocates a monitor-sized buffer, so doing it here stalled every exposure step.
    /// </summary>
    private void ZebraCore(BgraImage back, IntRect monitor, IntRect painted)
    {
        if (_edit is not { } edit || !edit.Doc.Zebra || _zebraFailed) return;
        if (!_byMonitor.TryGetValue(monitor, out CapturedOutput? o) || o.ReadableHdr is not { } frame) return;
        float white = (settings.SdrWhiteNits ?? o.Info.SdrWhiteNits) / 80f, exposure = settings.Exposure * edit.Doc.Exposure;
        bool have = _zebraMasks.TryGetValue(monitor, out var cached);
        if (!have || cached.White != white || cached.Exposure != exposure)
        {
            if (Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() != null) _ = RebuildZebra(monitor, frame, white, exposure);
            // The harnesses can paint on a thread with no dispatcher to come back to: built in place there.
            else if (BuildZebra(frame, white, exposure) is { } mask) { _zebraMasks[monitor] = cached = (mask, white, exposure); have = true; }
        }
        if (have) Annotate.ShapeRenderer.Zebra(back, monitor, monitor, cached.Mask, painted);
    }

    /// <summary>Monitors whose zebra mask is being rebuilt. UI thread only.</summary>
    private readonly HashSet<IntRect> _zebraBuilding = new();
    /// <summary>A mask failed to build for a reason other than a lost device: the zebra is off for the rest of this
    /// overlay rather than retried on every paint.</summary>
    private volatile bool _zebraFailed;

    /// <summary>
    /// Builds one monitor's zebra mask on the thread pool, then repaints with it. One build per monitor at a time: a
    /// slider moving meanwhile is caught up by the repaint, which finds the mask stale and builds again, so the builds
    /// follow the slider without queueing up behind it.
    /// </summary>
    private async Task RebuildZebra(IntRect monitor, IHdrFrame frame, float white, float exposure)
    {
        if (!_zebraBuilding.Add(monitor)) return;
        ZebraMask? mask;
        try { mask = await Task.Run(() => BuildZebra(frame, white, exposure)); }
        finally { _zebraBuilding.Remove(monitor); }
        if (mask == null || _finishing) return;
        _zebraMasks[monitor] = (mask, white, exposure);
        RefreshBackBuffers();
    }

    /// <summary>The mask, or null when it cannot be read: a lost device (logged where it was found), the overlay
    /// finishing under it, or anything else, which is logged once and turns the zebra off.</summary>
    private ZebraMask? BuildZebra(IHdrFrame frame, float white, float exposure)
    {
        try { return frame.Zebra(white, exposure); }
        catch (HdrFrameLostException) { return null; }
        catch (Exception e)
        {
            if (!_finishing && !_zebraFailed)
            {
                _zebraFailed = true;
                log.Warn("zebra: the mask was not built, the zebra is off for this snip: " + e.GetType().Name + ": " + e.Message);
            }
            return null;
        }
    }

    /// <summary>The cursor readout: the selection's size and the nits (Normal), the nits alone (Viewfinder, whose size is
    /// on its chip), or the cursor's coordinates and the nits under the loupe (Guides).</summary>
    /// <param name="monitor">The window asking; the pill belongs to whichever monitor the cursor is on.</param>
    public string? PillText(IntRect monitor)
    {
        if (!monitor.Contains(Cursor.X, Cursor.Y)) return null;
        // Asked by every window on every mouse move, so cached until something it reads changes.
        var key = (Cursor.X, Cursor.Y, Selection, Hover, settings.ShowNitsReadout, _pillNote, _picking);
        if (_pillKey == key) return _pillText;
        _pillKey = key;
        return _pillText = BuildPillText();
    }

    private string? BuildPillText()
    {
        if (_pillNote != null) return _pillNote;   // a confirmation stands in for the readout while it lasts
        // The picker's loupe reads the colour it would copy, whatever the frame.
        if (_picking) return PixelUnderCursor() is { } p ? Core.Extract.ColorText.Loupe(p.Argb, settings.ColorFormat, p.Nits) : null;
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

    private (int, int, IntRect, IntRect, bool, string?, bool)? _pillKey;
    private string? _pillText;

    /// <summary>A short confirmation shown in the pill (or under the loupe) instead of the readout, and the timer that
    /// takes it down. A field: an unreferenced DispatcherQueueTimer can be collected before it ticks.</summary>
    private string? _pillNote;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _pillNoteTimer;
    private static readonly TimeSpan PillNoteFor = TimeSpan.FromMilliseconds(1500);

    private void ShowPillNote(string note)
    {
        if (_finishing) return;   // as in CatchUpStats: no timer after Finish has detached it
        _pillNote = note;
        if (Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() is { } ui)
        {
            if (_pillNoteTimer == null)
            {
                _pillNoteTimer = ui.CreateTimer();
                _pillNoteTimer.IsRepeating = false;
                _pillNoteTimer.Tick += OnPillNoteElapsed;
            }
            _pillNoteTimer.Interval = PillNoteFor;
            _pillNoteTimer.Stop();
            _pillNoteTimer.Start();
        }
        RenderAll();
    }

    private void OnPillNoteElapsed(Microsoft.UI.Dispatching.DispatcherQueueTimer timer, object args)
    {
        if (_finishing) return;
        _pillNote = null;
        RenderAll();
    }

    /// <summary>
    /// Arms the colour picker, or puts it away. Armed, the pixel loupe follows the cursor in every frame style, and the
    /// next click, C, Space or Enter copies the colour under it (<see cref="CopyColour"/>) and puts the picker away. It
    /// is one of the things the pointer can be for, so it and an armed Copy text or Pin exclude each other.
    /// </summary>
    public void SetPicking(bool on, bool quiet = false)
    {
        if (_finishing || _picking == on) return;
        _picking = on;
        if (on) _then = SnipAction.Snip;
        _pillKey = null;
        ToolbarChanged?.Invoke();
        if (!quiet) Announce?.Invoke(on ? "Pick colour: click, or press C, to copy the colour under the pointer; Shift adds the nits, Escape cancels" : "Pick colour off");
        RenderAll();
    }

    /// <summary>The frozen pixel under the cursor (0xAARRGGBB, opaque) and the nits there on an HDR monitor, or null off
    /// every monitor. The frozen frame's, before annotations, as the snip itself is.</summary>
    private (uint Argb, float? Nits)? PixelUnderCursor()
    {
        CapturedOutput? o = null;
        foreach (CapturedOutput x in _outputs) if (x.Info.Bounds.Contains(Cursor.X, Cursor.Y)) { o = x; break; }
        if (o == null) return null;
        int lx = Cursor.X - o.Info.Left, ly = Cursor.Y - o.Info.Top;
        byte[] px = o.Sdr.Data;
        int i = (ly * o.Sdr.Width + lx) * 4;
        uint argb = 0xFF000000u | (uint)px[i + 2] << 16 | (uint)px[i + 1] << 8 | px[i];
        float? nits = null;
        try
        {
            if (o.ReadableHdr is { } f && f.TrySample(lx, ly, out float r, out float g, out float b))
                nits = Core.Color.Transfer.Luminance709(r, g, b) * 80f;
        }
        catch (HdrFrameLostException) { }   // logged where it was found; the colour alone is still right
        return (argb, nits);
    }

    /// <summary>
    /// The armed picker's click (or C, Space, Enter): copies the colour of the frozen pixel under the cursor, as the
    /// Colour format setting writes it, puts the picker away and says what was copied in the pill (or under the loupe).
    /// On an HDR monitor the confirmation adds the nits under it, and Shift copies them too; without Shift only the
    /// colour is copied, so it pastes into a colour field.
    /// </summary>
    private void CopyColour(bool withNits)
    {
        if (PixelUnderCursor() is not { } pixel) return;
        (uint argb, float? nits) = pixel;
        string colour = Core.Extract.ColorText.Format(argb, settings.ColorFormat);
        string copied = withNits ? Core.Extract.ColorText.WithNits(colour, nits) : colour;
        string said = Core.Extract.ColorText.Confirmation(colour, nits, withNits);
        SetPicking(false, quiet: true);
        ShowPillNote(said);
        Announce?.Invoke(said);
        // Off the UI thread: the clipboard can be held by another app, and the write waits for it. Queued, so two
        // quick copies land in the order they were made.
        _ = Output.ClipboardWriter.SetTextQueued(copied, log).ContinueWith(
            t => log.Warn("copy colour: " + t.Exception!.GetBaseException().Message),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

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
        _pickClick = false;
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
        if (_picking || _pickClick) { RenderAll(); return; }   // the loupe follows; nothing is being selected or drawn
        if (DrawingActive && _edit != null) { _edit.Move(x, y, Mods()); return; }
        switch (mode)
        {
            case SnipMode.Rectangle when _dragStart != null: Selection = IntRect.FromDrag(_dragStart.Value.X, _dragStart.Value.Y, x, y).Clamp(desktop); break;
            case SnipMode.Rectangle when _keyAnchor is { } a: Selection = IntRect.FromDrag(a.X, a.Y, x, y).Clamp(desktop); break;
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
        if (_picking) { _pickClick = true; CopyColour(withNits: Win32.KeyDown(Win32.VkShift)); return; }
        if (DrawingActive && _edit != null) { _edit.Begin(x, y, Mods()); return; }
        _keyAnchor = null;   // the mouse takes over from a selection begun on the keyboard
        switch (mode)
        {
            case SnipMode.Rectangle: _dragStart = (x, y); Selection = IntRect.FromDrag(x, y, x, y); break;
            case SnipMode.Freeform: _dragStart = (x, y); _path.Clear(); _path.Add((x, y)); break;
            // The window is cut from the frozen frame, not captured again on its own as an active-window snip is: the
            // snip must be what was shown and chosen, and anything drawn or exposed here is in that frame's pixels.
            case SnipMode.Window: if (!Hover.IsEmpty) Complete(Hover, null); break;
            case SnipMode.FullScreen: if (!Hover.IsEmpty) Complete(Hover, null); break;
        }
        RenderAll();
    }

    public void OnMouseUp(int x, int y)
    {
        if (_pickClick) { _pickClick = false; return; }
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
        // Text and pins have no annotate step: the selection is what they were armed for.
        if (settings.AfterSelect == "annotate" && _pendingSelection.IsEmpty && _then == SnipAction.Snip)
        {
            _pendingSelection = region; _pendingPath = path; Selection = region;
            _path.Clear();          // the lasso is kept in _pendingPath; the overlay now outlines its bounding box instead
            Annotating = true; return;
        }
        Finish(Outcome(region, path));
    }

    /// <summary>Done in the tool row (and Enter): saves the selection the user drew on.</summary>
    public void Done() { if (!_pendingSelection.IsEmpty) Finish(Outcome(_pendingSelection, _pendingPath)); }

    /// <summary>The outcome for a finished selection, carrying the document and what the selection is for.</summary>
    private OverlayOutcome Outcome(IntRect region, IReadOnlyList<(int X, int Y)>? path)
        => new(region, path, -1, _edit?.Doc, _edit?.Doc.Exposure ?? 1f) { Action = _then };

    private SnipAction _then;

    /// <summary>What the next selection is for: an ordinary snip, or armed from the toolbar (or T, P) to copy its text
    /// or pin it.</summary>
    public SnipAction Then => _then;

    /// <summary>Raised with a sentence for the toolbar's live region, so a screen reader hears what changed.</summary>
    public Action<string>? Announce { get; set; }

    /// <summary>
    /// Arms <paramref name="action"/> for the selection, or disarms it when it is already armed. With a selection
    /// already made (the "annotate" flow's, or one being made from the keyboard) it applies at once; otherwise the next
    /// selection finishes straight into it.
    /// </summary>
    public void Arm(SnipAction action)
    {
        if (_finishing) return;
        _then = _then == action ? SnipAction.Snip : action;
        if (_then != SnipAction.Snip)
        {
            SetPicking(false, quiet: true);   // the pointer is for one thing at a time
            if (!_pendingSelection.IsEmpty) { Finish(Outcome(_pendingSelection, _pendingPath)); return; }
            // A selection made from the keyboard, finished or not, is the one to use.
            if (!Selection.IsEmpty && _dragStart == null && Selection.Width > 1 && Selection.Height > 1) { _keyAnchor = null; Finish(Outcome(Selection, null)); return; }
        }
        ToolbarChanged?.Invoke();
        Announce?.Invoke(_then switch
        {
            SnipAction.CopyText => "Copy text: select an area to copy the text in it",
            SnipAction.Pin => "Pin to screen: select an area to pin it",
            _ => action == SnipAction.CopyText ? "Copy text off" : "Pin to screen off",
        });
    }

    /// <summary>Shift and Control as the shapes see them, read from the keyboard rather than from a framework.</summary>
    private static InputMods Mods()
        => (Win32.KeyDown(Win32.VkShift) ? InputMods.Shift : 0) | (Win32.KeyDown(Win32.VkControl) ? InputMods.Ctrl : 0);

    public void OnKey(int vk, bool ctrl, bool shift, bool alt, bool repeat)
    {
        // A held key repeats. The arrows should, but a held T, P, C or A would arm and disarm, copy again and again, or
        // flicker the tool row, and a held Space or Enter would go on from a pick to start or finish a selection.
        if (repeat && vk is Win32.VkT or Win32.VkP or Win32.VkC or Win32.VkA or Win32.VkSpace or Win32.VkReturn) return;
        if (vk == Win32.VkEscape)
        {
            // Escape peels off annotation state first (pending text, marquee, drag, selection) and cancels the snip last.
            if (_picking) { SetPicking(false); return; }
            if (_annotating && _edit != null && _edit.Escape()) { RenderAll(); return; }
            if (_keyAnchor != null) { DropAnchor(); Announce?.Invoke("Selection cleared"); RenderAll(); return; }
            Finish(OverlayOutcome.Cancelled); return;
        }
        if (vk == Win32.VkA && !ctrl && !shift && !alt) { Annotating = !Annotating; return; }
        // The armed picker takes C, Space and Enter as its click, ahead of the tool letters and the selection keys.
        if (_picking && !ctrl && vk is Win32.VkC or Win32.VkSpace or Win32.VkReturn) { CopyColour(withNits: shift); return; }
        if (_annotating && _toolbar != null)
        {
            // Only a tool letter hands the mouse back to drawing; undo, redo, delete and crop-apply must leave a
            // capture mode (and the selection drag in flight) exactly where it is.
            Annotate.AnnotateBar.KeyHandled handled = _toolbar.HandleAnnotateKey(vk, ctrl);
            if (handled == Annotate.AnnotateBar.KeyHandled.Tool) { UseDrawTool(); return; }
            if (handled == Annotate.AnnotateBar.KeyHandled.Command) { RenderAll(); return; }
        }
        if (!_pendingSelection.IsEmpty && vk is Win32.VkReturn or Win32.VkSpace)
        {
            // The annotate step's selection is already made, its lasso kept in _pendingPath; SelectKey would finish it
            // again as a plain rectangle. Enter saves it as Done does, and so does Space, except under a drawing tool,
            // where a stray Space must not end the snip.
            if (vk == Win32.VkReturn || !DrawingActive) Done();
            return;
        }
        // Under a drawing tool or mid-drag the keyboard does not start selections or move the cursor; it can still
        // take or move a selection that already exists.
        bool pointerBusy = DrawingActive || _dragStart != null;
        switch (vk)
        {
            case Win32.VkReturn or Win32.VkSpace when _dragStart == null: SelectKey(pointerBusy); return;
            case Win32.VkTab when !pointerBusy: CycleHover(shift ? -1 : 1); return;
            case Win32.VkR: Mode = SnipMode.Rectangle; break;
            case Win32.VkW: Mode = SnipMode.Window; break;
            case Win32.VkF: Mode = SnipMode.FullScreen; break;
            case Win32.VkL: Mode = SnipMode.Freeform; break;
            // Tool letters while annotating (handled above); what the selection is for otherwise.
            case Win32.VkT when !ctrl && !alt: Arm(SnipAction.CopyText); return;
            case Win32.VkP when !ctrl && !alt: Arm(SnipAction.Pin); return;
            // The overlay's tool row has no crop, so C is free while annotating too. It arms the picker; a second C (or a
            // click) copies, above.
            case Win32.VkC when !ctrl: SetPicking(true); return;
            case Win32.VkLeft or Win32.VkRight or Win32.VkUp or Win32.VkDown:
                int step = shift ? 10 : 1;
                int dx = vk == Win32.VkLeft ? -step : vk == Win32.VkRight ? step : 0, dy = vk == Win32.VkUp ? -step : vk == Win32.VkDown ? step : 0;
                // A selection being made from the keyboard follows the cursor; one already made moves (or with Alt,
                // grows and shrinks from its bottom-right corner); otherwise the cursor itself moves, so the keyboard
                // can aim wherever the mouse can.
                if (_keyAnchor == null && !Selection.IsEmpty)
                {
                    Selection = alt ? Resize(Selection, dx, dy) : Selection.Nudge(dx, dy, desktop);
                    break;
                }
                if (!pointerBusy) MoveCursor(Cursor.X + dx, Cursor.Y + dy);
                return;
        }
        RenderAll();
    }

    // ----- keyboard-only selection -----

    /// <summary>The corner a keyboard selection started from (Space or Enter in rectangle mode); the other corner
    /// follows the cursor until the second Space or Enter.</summary>
    private (int X, int Y)? _keyAnchor;
    /// <summary>Tab's place in the window list (window mode) or the monitors (full-screen mode).</summary>
    private int _tabIndex = -1;

    private void DropAnchor()
    {
        _keyAnchor = null;
        Selection = IntRect.Empty;
    }

    /// <summary>
    /// Space and Enter: in rectangle mode the first press anchors a selection at the cursor and the second finishes it;
    /// in window and full-screen modes either takes what is highlighted, as a click would. A selection already made
    /// (the arrow keys can move one) is finished as it is.
    /// </summary>
    private void SelectKey(bool pointerBusy)
    {
        if (_keyAnchor != null)
        {
            if (Selection.Width < 2 || Selection.Height < 2) return;   // a point is not a snip; keep going
            _keyAnchor = null;
            Complete(Selection, null);
            return;
        }
        if (!Selection.IsEmpty) { Complete(Selection, null); return; }
        if (mode is SnipMode.Window or SnipMode.FullScreen) { if (!Hover.IsEmpty) Complete(Hover, null); return; }
        if (mode != SnipMode.Rectangle || pointerBusy) return;   // a lasso needs a pointer; a drawing tool has it
        _keyAnchor = Cursor;
        Selection = IntRect.FromDrag(Cursor.X, Cursor.Y, Cursor.X, Cursor.Y).Clamp(desktop);
        Announce?.Invoke($"Selection started at {Cursor.X}, {Cursor.Y}. Arrow keys move the other corner, Shift for 10 pixels; Space or Enter finishes, Escape clears");
        RenderAll();
    }

    /// <summary>Moves the real cursor (kept on a monitor, never in a gap between them), so the readout, the loupe and a
    /// keyboard selection all follow it; the move is applied here too rather than waiting for the WM_MOUSEMOVE it
    /// generates.</summary>
    private void MoveCursor(int x, int y)
    {
        List<IntRect> monitors = _outputs.Select(o => o.Info.Bounds).ToList();
        if (monitors.Count == 0) monitors.Add(desktop);
        (x, y) = KeyboardSelection.ClampToMonitors(x, y, monitors);
        User32.SetCursorPos(x, y);
        OnMouseMove(x, y);
    }

    /// <summary>Alt+arrows: the selection's right and bottom edges move, never past the desktop or below 1 × 1.</summary>
    private IntRect Resize(IntRect r, int dx, int dy)
        => IntRect.FromLtrb(r.Left, r.Top, Math.Clamp(r.Right + dx, r.Left + 1, desktop.Right), Math.Clamp(r.Bottom + dy, r.Top + 1, desktop.Bottom));

    /// <summary>Tab and Shift+Tab: the next or previous window (window mode) or monitor (full-screen mode) is
    /// highlighted, front to back, and its name is spoken; Space or Enter then takes it.</summary>
    private void CycleHover(int step)
    {
        if (mode == SnipMode.Window)
        {
            _windowList ??= WindowFinder.TopLevel(_ownHwnds);
            List<WindowInfo> list = _windowList;
            for (int tries = 0; tries < list.Count; tries++)
            {
                _tabIndex = KeyboardSelection.Cycle(_tabIndex, step, list.Count);
                IntRect r = list[_tabIndex].Bounds.Clamp(desktop);
                if (r.IsEmpty) continue;
                Hover = r;
                string title = list[_tabIndex].Title;
                Announce?.Invoke($"{(title.Length > 0 ? title : "Untitled window")}, {r.Width} × {r.Height}");
                break;
            }
        }
        else if (mode == SnipMode.FullScreen && _outputs.Count > 0)
        {
            _tabIndex = KeyboardSelection.Cycle(_tabIndex, step, _outputs.Count);
            Hover = _outputs[_tabIndex].Info.Bounds;
            Announce?.Invoke($"Monitor {_tabIndex + 1} of {_outputs.Count}, {Hover.Width} × {Hover.Height}");
        }
        RenderAll();
    }

    /// <summary>Toolbar delay pick: close and let the session restart after the delay.</summary>
    public void RestartWithDelay(int seconds) => Finish(new OverlayOutcome(IntRect.Empty, null, seconds));

    public void Finish(OverlayOutcome outcome)
    {
        if (_finishing || _done.Task.IsCompleted) return;
        _finishing = true;
        // Stopped and detached: a stopped timer still holds its Tick, and the Tick holds this session, which the
        // dispatcher then keeps alive with whatever it still references, the frames among them.
        if (_statsCatchUp is { } statsTimer) { statsTimer.Stop(); statsTimer.Tick -= OnStatsCatchUp; _statsCatchUp = null; }
        if (_pillNoteTimer is { } noteTimer) { noteTimer.Stop(); noteTimer.Tick -= OnPillNoteElapsed; _pillNoteTimer = null; }
        // Before the frames go: an owned window is destroyed with its owner, and the toolbar and text box are WinUI
        // windows that must be closed through Close().
        Disown();
        foreach (OverlayWindow w in _windows) { try { w.Dispose(); } catch { } }
        _windows.Clear();
        _byHwnd.Clear(); _byMonitor.Clear();
        _focus = null;
        Announce = null;
        try { _toolbar?.Close(); } catch { }
        _toolbar = null;
        try { _textWindow?.Close(); } catch { }
        _textWindow = null;
        SaveStyle();
        // Release everything now rather than when the session is collected: the frames are pooled and the next snip
        // writes straight into them.
        _windowList = null;
        _zebraMasks.Clear();
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

    /// <summary>The rectangle and frame the cached peak/mean were read from, and the text.</summary>
    private (IntRect, IHdrFrame)? _statsFor;
    /// <summary>The last nits under the cursor that could be read: shown again when the device is busy for a move, so
    /// the pill does not flicker, while the catch-up timer asks again.</summary>
    private string? _lastNits;
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

    /// <summary>
    /// Luminance readout for HDR outputs: nits under the cursor, and peak/mean inside the selection. On the GPU path
    /// each is a small readback that never waits for the device: when a capture holds it, the last figures stay up and
    /// the catch-up timer asks again.
    /// </summary>
    public string? NitsText()
    {
        if (!settings.ShowNitsReadout) return null;
        CapturedOutput? o = null;   // a loop, not LINQ: this is on the paint path
        foreach (CapturedOutput x in _outputs) if (x.ReadableHdr != null && x.Info.Bounds.Contains(Cursor.X, Cursor.Y)) { o = x; break; }
        if (o?.ReadableHdr is not { } f) return null;
        string text;
        if (f.TrySample(Cursor.X - o.Info.Left, Cursor.Y - o.Info.Top, out float r, out float g, out float b))
            text = _lastNits = $"{Core.Color.Transfer.Luminance709(r, g, b) * 80f:F0} nits";
        else if (_lastNits != null && f.Readable) { text = _lastNits; CatchUpStats(); }
        else return null;
        IntRect sel = Selection.IsEmpty ? Hover : Selection;
        IntRect hit = sel.Intersect(o.Info.Bounds);
        if (!hit.IsEmpty)
        {
            // Only recomputed when the rectangle or frame changes, and during a drag at most every StatsInterval: the
            // figures lag the rectangle by that much until the button is let go.
            bool throttled = _dragStart != null && _statsFor != null && System.Diagnostics.Stopwatch.GetElapsedTime(_statsAt) < StatsInterval;
            if (_statsFor != (hit, f) && throttled) CatchUpStats();
            else if (_statsFor != (hit, f))
            {
                if (f.TryStats(hit.Offset(-o.Info.Left, -o.Info.Top), out float peak, out float mean))
                {
                    _statsAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    _statsFor = (hit, f);
                    _stats = $"  peak {peak:F0}  mean {mean:F0}";
                }
                else CatchUpStats();   // the device was busy: the old figures stay up until the timer asks again
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
        // A paint after Finish must not arm a timer that Finish has already detached.
        if (_finishing || _statsCatchUp is { IsRunning: true }) return;
        if (_statsCatchUp == null)
        {
            // The harnesses can build a session on a thread with no dispatcher; they do not drag.
            if (Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() is not { } ui) return;
            _statsCatchUp = ui.CreateTimer();
            _statsCatchUp.IsRepeating = false;
            _statsCatchUp.Tick += OnStatsCatchUp;
        }
        TimeSpan left = StatsInterval - System.Diagnostics.Stopwatch.GetElapsedTime(_statsAt);
        _statsCatchUp.Interval = left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1);
        _statsCatchUp.Start();
    }

    private void OnStatsCatchUp(Microsoft.UI.Dispatching.DispatcherQueueTimer timer, object args)
    {
        if (_finishing) return;
        _pillKey = null;
        RenderAll();
    }
}
