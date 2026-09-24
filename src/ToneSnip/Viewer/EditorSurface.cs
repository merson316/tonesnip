using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using ToneSnip.App.Annotate;
using ToneSnip.App.Capture;
using ToneSnip.Core.Annotate;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using ToneSnip.Core.Tonemap;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics.DirectX;

using Windows.System;

namespace ToneSnip.App.Viewer;

/// <summary>
/// The picture in the viewer: a Win2D <see cref="CanvasControl"/> over one BGRA buffer per crop viewport,
/// re-rendered per dirty rectangle, driving an <see cref="EditSession"/>.
/// </summary>
/// <remarks>
/// Reused buffers: <c>_baseView</c> (the crop's clean pixels), <c>_target</c> (the same with shapes and chrome on top)
/// and a <see cref="CanvasBitmap"/> the size of the view. A paint renders into <c>_target</c> and uploads only the
/// changed rectangle; the <c>Draw</c> handler just blits the bitmap at the current zoom.
/// <para>
/// The element is the full zoomed size so the surrounding ScrollViewer scrolls normally, but the Win2D control inside
/// it covers only the visible part and moves with the scroll (<see cref="SetViewport"/>). A control the full zoomed
/// size would allocate a swap chain that large, which can exceed Direct3D's texture size limit.
/// <c>CanvasVirtualControl</c> is avoided because it leaked GDI objects and shared GPU resources per editor opened.
/// </para>
/// </remarks>
public sealed class EditorSurface : UserControl
{
    /// <summary>A paint over this many milliseconds is worth a log line; under it the log stays quiet.</summary>
    private const int PaintBudgetMs = 4;
    private const int CheckerCell = 8, CheckerSize = CheckerCell * 2;
    /// <summary>Ceiling on the staging buffer. A dirty rect bigger than this is uploaded in row bands.</summary>
    private const int UploadCapBytes = 1 << 20;

    private readonly Canvas _host = new();
    /// <summary>Where the Win2D control sits inside this element, in effective pixels: the top-left of the visible part.</summary>
    private double _originX, _originY;
    private readonly CanvasControl _canvas = new();

    private CaptureResult? _result;
    private BgraImage? _original;        // un-annotated image in the original frame (mutated in place by re-expose)
    private BgraImage? _baseView;        // _original cropped to View (the same object as _original when there is no crop)
    private BgraImage? _target;          // rendered view (shapes over base)
    private BgraImage? _tmp;             // re-expose scratch, kept between slider moves so dragging allocates nothing
    private CanvasBitmap? _bitmap;       // GPU copy of _target, the only thing Draw touches
    private CanvasBitmap? _checker;      // 16 px transparency tile
    private CanvasImageBrush? _checkerBrush;
    /// <summary>Staging for the dirty-rect upload, one native buffer per view size with its Length set per paint.
    /// Win2D requires a SetPixelBytes payload of exactly the rectangle's size, which an <c>IBuffer</c> can give inside a
    /// fixed capacity and an over-sized rented array cannot.</summary>
    private global::Windows.Storage.Streams.IBuffer? _upload;
    private uint _accent = ShapeRenderer.DefaultAccent;
    private bool _zebra;
    private bool _chrome;                // chrome was drawn last paint, so the next dirty rect needs a handle-sized margin
    private bool _marquee;               // a crop marquee dimmed the whole view last paint, so the whole view must go across
    private double _zoom = 1;
    private readonly ExposurePreview _exposure;
    private bool _closing;
    private bool _released;

    private static readonly InputCursor ArrowCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    private static readonly InputCursor HandCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    private static readonly InputCursor CrossCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);
    private static readonly InputCursor BeamCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);
    private static readonly InputCursor SizeAllCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);

    public EditorSurface()
    {
        _exposure = new ExposurePreview(ExposurePass, e => App.Current.Log.Warn("viewer exposure: " + e.Message),
            // Wait out any HDR sidecar write reading _original off the UI thread.
            () => BeforeExposure?.Invoke());
        // A Panel parent, so RemoveFromVisualTree can detach the control from a container and drop the Win2D device
        // when the window closes.
        _host.Children.Add(_canvas);
        Content = _host;
        IsTabStop = true;              // keys reach the window's handler only while something in the window has focus
        // Shows only for keyboard focus. The window returns focus here with FocusState.Pointer after tool-row clicks
        // and text entry, so those do not ring the whole picture.
        UseSystemFocusVisuals = true;
        _canvas.CreateResources += OnCreateResources;
        _canvas.Draw += OnDraw;
        _canvas.Width = _canvas.Height = 1;
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerCaptureLost += OnPointerCaptureLost;
        _canvas.PointerExited += OnPointerExited;
    }

    /// <summary>
    /// The luminance under the pointer in nits, read from the snip's HDR crops (as the overlay's readout reads the
    /// frame, before exposure), or null when the pointer is off the picture, over an SDR part of it, or the snip has no
    /// HDR data left. Raised on every pointer move over the picture.
    /// </summary>
    public event Action<float?>? PointerNits;

    /// <summary>Nits at (<paramref name="x"/>, <paramref name="y"/>) in the image's frame, or null outside every HDR
    /// crop.</summary>
    public float? NitsAt(int x, int y)
    {
        if (_result == null || _result.Crops.Count == 0) return null;
        foreach (HalfCrop c in _result.Crops)
        {
            IntRect b = c.Bounds.Offset(-_result.Region.Left, -_result.Region.Top);
            if (!b.Contains(x, y)) continue;
            (float r, float g, float bl) = c.Image.Sample(x - b.Left, y - b.Top);
            return Core.Color.Transfer.Luminance709(r, g, bl) * 80f;
        }
        return null;
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerOver = false;
        PointerNits?.Invoke(null);
        PickerPointer?.Invoke(null);
    }

    /// <summary>While <see cref="Picking"/>: the source pixel under the pointer on every move, or null once it leaves
    /// the picture. The host draws the loupe from it.</summary>
    public event Action<(int X, int Y)?>? PickerPointer;

    /// <summary>
    /// Renders the picker's loupe around the source pixel (<paramref name="x"/>, <paramref name="y"/>) from the picture
    /// as shown (shapes included, as a pick reads it) into <paramref name="into"/>; see <see cref="LoupeRaster"/>. False
    /// when there is no picture.
    /// </summary>
    public bool RenderLoupe(int x, int y, GuidesLayout g, byte[] into)
    {
        if (_target is not { } t) return false;
        LoupeRaster.Render(t, x - View.Left, y - View.Top, g.Source, g.Cell, g.Edge, into);
        return true;
    }

    /// <summary>The rendered colour at a source pixel (0xAARRGGBB), or null off the view.</summary>
    public uint? ColourAt(int x, int y) => PixelAt(x, y);

    /// <summary>Where the middle of source pixel (<paramref name="x"/>, <paramref name="y"/>) is, in this element's
    /// effective pixels.</summary>
    public Point PixelCentre(int x, int y) => new((x - View.Left + 0.5) * Zoom, (y - View.Top + 0.5) * Zoom);

    public EditSession Session { get; private set; } = new(new AnnotationDoc());
    public IntRect View { get; private set; }
    /// <summary>False while the tool row is collapsed, when the mouse does nothing. The cursor updates immediately rather
    /// than on the next pointer move.</summary>
    public bool Editing
    {
        get => _editing;
        set { if (_editing == value) return; _editing = value; ApplyCursor(); }
    }
    private bool _editing;
    /// <summary>The loaded snip's HDR crops in the view's frame, for the zebra pass; null until zebra first paints.</summary>
    private ((IntRect Bounds, HalfImage Half)[] Crop, float White, float BaseExposure)[]? _zebraCrops;
    /// <summary>The last pointer position over the picture, in source pixels: what <see cref="ApplyCursor"/> hit-tests
    /// when the cursor has to change without a move.</summary>
    private (int X, int Y) _pointer;
    /// <summary>The pointer is over the picture (it moved there and has not left).</summary>
    private bool _pointerOver;

    /// <summary>
    /// The one cursor decision: SizeAll while panning; a crosshair while picking a colour or marking text; arrow when not editing; under Select an arrow, a hand over a
    /// shape or SizeAll over a handle; I-beam for Text; a crosshair for the drawing tools.
    /// </summary>
    private void ApplyCursor()
    {
        (int x, int y) = _pointer;
        ProtectedCursor = Panning ? SizeAllCursor
            : Picking || SelectingText ? CrossCursor
            : !Editing ? ArrowCursor
            : Session.Tool == Tool.Select
            ? Session.HandleAt(x, y) != Handle.None ? SizeAllCursor : Session.Doc.HitTop(x, y) != null ? HandCursor : ArrowCursor
            : Session.Tool == Tool.Text ? BeamCursor : CrossCursor;
    }

#if TONESNIP_HARNESS
    /// <summary>The cursor's shape by name, for the harness log; the property itself is protected.</summary>
    internal string CursorName => ProtectedCursor is InputSystemCursor c ? c.CursorShape.ToString() : "default";
#endif
    /// <summary>
    /// True while the host is panning the view (space held, or a middle-button drag), so no shape may begin. Set only
    /// by <c>ViewerWindow</c>.
    /// </summary>
    public bool Panning
    {
        get => _panning;
        // The cursor updates here rather than on the next pointer move, which may not come for a while.
        set { if (_panning == value) return; _panning = value; ApplyCursor(); }
    }
    private bool _panning;
    /// <summary>Raised after every paint; the host re-applies zoom when <see cref="View"/> changed size.</summary>
    public event Action? Rendered;
    /// <summary>Raised on the UI thread once a re-expose pass has landed, so the host can refresh the "edited" dot.</summary>
    public event Action? ExposureApplied;
    /// <summary>
    /// Awaited by the exposure loop before each pass. The host points it at its pending HDR sidecar write, which reads
    /// off the UI thread the same buffer a tonemap pass rewrites in place.
    /// </summary>
    public Func<Task>? BeforeExposure { get; set; }

    public double Zoom
    {
        get => _zoom;
        // Interpolation is chosen per draw (see OnDraw).
        set { if (Math.Abs(_zoom - value) < 1e-9) return; _zoom = value; _canvas.Invalidate(); }
    }

    public void Load(CaptureResult result, uint accent)
    {
        _result = result; _accent = accent; _original = result.Image; _zebraCrops = null; _exposureLasso = null;
        var doc = new AnnotationDoc { Current = App.Current.Settings.Annotate.ToStyle(accent), Private = App.Current.Settings.Annotate.PrivacyMode, Exposure = result.Exposure };
        // The overlay's shapes are in virtual-desktop pixels, so re-base them into the image's frame. Their ids are
        // re-stamped so new shapes drawn here cannot collide with them.
        if (result.Doc != null) foreach (Shape s in result.Doc.Shapes) doc.Add(s.Moved(-result.Region.Left, -result.Region.Top) with { Id = doc.NewId() });
        doc.MarkSaved();   // shapes carried over from a saved snip are already baked in; the editor should not open showing "edited"
        Session = new EditSession(doc) { Tool = Tool.Select };
        Session.Changed += OnChanged;
        Session.ToolChanged += ApplyCursor;
        View = new IntRect(0, 0, _original.Width, _original.Height);
        Rebuild();
    }

    private IntRect Full => new(0, 0, _original!.Width, _original.Height);
    private IntRect ViewLocal => new(0, 0, View.Width, View.Height);
    private IntRect CurrentCrop() => Session.Doc.Crop.IsEmpty ? Full : Session.Doc.Crop.Intersect(Full);

    /// <summary>Re-sizes the view buffers to the document's crop and repaints everything.</summary>
    private void Rebuild()
    {
        if (_original == null) return;
        IntRect crop = CurrentCrop();
        if (crop.IsEmpty) crop = Full;
        View = crop;
        if (crop.Width == _original.Width && crop.Height == _original.Height)
        {
            _baseView = _original;   // no crop: the view is the image itself, no copy needed
        }
        else
        {
            // Re-exposing or undoing inside one crop size refills the same buffer; only a size change allocates.
            if (_baseView == null || ReferenceEquals(_baseView, _original) || _baseView.Width != crop.Width || _baseView.Height != crop.Height)
                _baseView = BgraImage.Blank(crop.Width, crop.Height);
            for (int y = 0; y < crop.Height; y++)
                Buffer.BlockCopy(_original.Data, ((crop.Top + y) * _original.Width + crop.Left) * 4, _baseView.Data, y * crop.Width * 4, crop.Width * 4);
        }
        if (_target == null || _target.Width != crop.Width || _target.Height != crop.Height)
        {
            _target = BgraImage.Blank(crop.Width, crop.Height);
            // The GPU copy and the staging buffer are per view size too; both are rebuilt below from the new _target.
            _bitmap?.Dispose(); _bitmap = null;
            // Capped rather than view-sized, since most uploads are small; larger ones go in row bands (see Upload).
            // At least one full row, so a band is never narrower than the rectangle.
            _upload = new global::Windows.Storage.Streams.Buffer((uint)Math.Max(crop.Width * 4, Math.Min(UploadCapBytes, crop.Width * crop.Height * 4)));
        }
        Width = Math.Round(crop.Width * Zoom); Height = Math.Round(crop.Height * Zoom);
        Paint(IntRect.Empty);
    }

    private void OnChanged(IntRect dirty)
    {
        if (_original == null) return;
        if (dirty.IsEmpty && View != CurrentCrop()) { Rebuild(); return; }   // crop applied or undone: the view changed size
        Paint(dirty);
    }

    /// <summary>Renders shapes for a source-frame dirty rect (Empty = all) into the target, draws chrome, and uploads the changed pixels.</summary>
    private void Paint(IntRect dirty)
    {
        if (_baseView == null || _target == null) return;
        long started = Stopwatch.GetTimestamp();
        IntRect local = dirty.IsEmpty ? ViewLocal : dirty.Intersect(View).Offset(-View.Left, -View.Top);
        if (local.IsEmpty) return;   // the change was entirely outside the visible crop
        // Chrome is painted over the target, so last frame's handles survive outside the dirty rect unless the base is
        // recopied there too. Handles ring a shape's bounds, so a handle-sized margin covers them.
        bool chrome = Session.InProgress != null || (Session.Tool == Tool.Select && Session.Doc.Selected != null) || !Session.CropMarquee.IsEmpty;
        if (chrome || _chrome) local = PadToView(local);
        _chrome = chrome;
        IntRect painted = ShapeRenderer.Render(Session.Doc, _baseView, View, _target, local, _accent, ShapeRenderer.DragGhostId(Session));
        if (painted.IsEmpty) painted = local;
        // Only what this paint re-rendered: the rest of the target was cut by the paint that rendered it.
        ApplyLasso(_target, painted.Union(local));
        if (_zebra && _result != null && _result.Crops.Count > 0)
        {
            // Built once per loaded snip. Each crop uses its own monitor's SDR white and the exposure it was actually
            // tonemapped with, auto exposure included, so mixed multi-monitor snips mark the right areas.
            // Each crop is kept as the one-element list Zebra takes, so a paint allocates none.
            _zebraCrops ??= _result.Crops.Select(c =>
            {
                float white = App.Current.Settings.SdrWhiteNits ?? c.Output.SdrWhiteNits;
                return (new[] { (c.Bounds.Offset(-_result.Region.Left, -_result.Region.Top), c.Image) }, white / 80f, c.BaseExposure);
            }).ToArray();
            foreach (((IntRect Bounds, HalfImage Half)[] crop, float white, float baseExposure) in _zebraCrops)
                ShapeRenderer.Zebra(_target, View, crop, white, baseExposure * Session.Doc.Exposure, painted);
        }
        ShapeRenderer.Chrome(Session, View, _target, _accent);   // whole view: chrome is cheap and handles move
        // Chrome stays inside the padded dirty rect, except a crop marquee's dimming, which covers the whole view; that
        // case (and the frame that clears it) uploads the whole view.
        bool marquee = !Session.CropMarquee.IsEmpty;
        Upload(marquee || _marquee ? ViewLocal : painted.Intersect(ViewLocal));
        _marquee = marquee;
        _canvas.Invalidate();
        double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        if (ms > PaintBudgetMs) App.Current.Log.Debug($"paint {ms:F0} ms");
        Rendered?.Invoke();
    }

    /// <summary>Copies one rectangle of <c>_target</c> into the GPU bitmap through the reused staging buffer.</summary>
    private void Upload(IntRect rect)
    {
        if (_target == null || _upload == null || rect.IsEmpty) return;
        try
        {
            // No GPU copy yet (first paint, or after a device loss): build it from the whole, already current target.
            if (_bitmap == null) { if (_canvas.ReadyToDraw) CreateBitmap(_canvas); return; }
            int w = rect.Width, h = rect.Height, stride = w * 4;
            if (stride == 0 || h == 0) return;
            // As many whole rows per band as the staging buffer holds.
            int rows = Math.Max(1, (int)(_upload.Capacity / (uint)stride));
            for (int top = 0; top < h; top += rows)
            {
                int band = Math.Min(rows, h - top);
                _upload.Length = (uint)(stride * band);
                for (int y = 0; y < band; y++)
                    _target.Data.CopyTo(((rect.Top + top + y) * _target.Width + rect.Left) * 4, _upload, (uint)(y * stride), stride);
                _bitmap.SetPixelBytes(_upload, rect.Left, rect.Top + top, w, band);
            }
        }
        catch (Exception e) when (DeviceLost(e)) { }
    }

    /// <summary>
    /// Win2D recovers on its own only from a device lost inside its Draw or CreateResources handlers; a loss during an
    /// upload has to be raised by hand, which brings CreateResources round to rebuild from the CPU buffers.
    /// </summary>
    private bool DeviceLost(Exception e)
    {
        try
        {
            if (!_canvas.ReadyToDraw) return false;
            CanvasDevice device = _canvas.Device;
            if (!device.IsDeviceLost(e.HResult)) return false;
            _bitmap?.Dispose(); _bitmap = null;
            device.RaiseDeviceLost();
            App.Current.Log.Warn("viewer device lost; rebuilding the surface");
            return true;
        }
        catch { return false; }
    }

    /// <summary>The GPU copy of the whole target. Runs at load, at every crop change and after a device loss.</summary>
    private void CreateBitmap(ICanvasResourceCreator device)
    {
        if (_target == null) return;
        _bitmap?.Dispose();
        _bitmap = CanvasBitmap.CreateFromBytes(device, _target.Data, _target.Width, _target.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized);
    }

    /// <summary>Also the device-lost path: every GPU resource is rebuilt from the CPU buffers, which never went away.</summary>
    private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        if (_released) return;
        _checkerBrush?.Dispose(); _checkerBrush = null;
        _checker?.Dispose();
        _checker = CanvasBitmap.CreateFromBytes(sender, CheckerTile, CheckerSize, CheckerSize, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        _checkerBrush = new CanvasImageBrush(sender, _checker)
        {
            ExtendX = CanvasEdgeBehavior.Wrap,
            ExtendY = CanvasEdgeBehavior.Wrap,
            Interpolation = CanvasImageInterpolation.NearestNeighbor,
        };
        CreateBitmap(sender);
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_bitmap == null) return;
        // The element's rounded size, not View × Zoom, so no sub-pixel strip of backdrop shows at the edges. The control
        // covers only the visible part, so the picture is offset by where that part starts.
        double w = double.IsNaN(Width) ? View.Width * Zoom : Width, h = double.IsNaN(Height) ? View.Height * Zoom : Height;
        var dest = new Rect(-_originX, -_originY, w, h);
        // Nearest neighbour where every picture pixel covers a whole number of screen pixels (100 %, 200 %…, in
        // physical pixels), and from 300 % up, where crisp squares matter more for pixel inspection than the uneven
        // widths it makes between whole multiples; linear below that.
        double physical = w / Math.Max(1, View.Width) * sender.Dpi / 96.0;
        CanvasImageInterpolation interpolation = ZoomSteps.Crisp(physical)
            ? CanvasImageInterpolation.NearestNeighbor : CanvasImageInterpolation.Linear;
        // The transparency tile stays at screen scale; only a freeform snip's cut-away pixels ever show it.
        // Behind the picture only: while a zoom animates the control can be larger than the picture (HoldCanvasSize).
        if (_checkerBrush != null) args.DrawingSession.FillRectangle(dest, _checkerBrush);
        args.DrawingSession.DrawImage(_bitmap, dest, new Rect(0, 0, View.Width, View.Height), 1f, interpolation);
        if (!_textArea.IsEmpty) DrawTextArea(args.DrawingSession, sender, w / Math.Max(1, View.Width));
    }

    /// <summary>
    /// The area being marked for Copy text, styled as the crop marquee is: the rest of the picture dimmed, a black line
    /// just outside the area and a white one on its edge, one screen line wide at any zoom. Drawn over the blit rather
    /// than into the picture's buffers, so marking re-renders nothing.
    /// </summary>
    private void DrawTextArea(CanvasDrawingSession ds, CanvasControl sender, double scale)
    {
        IntRect a = _textArea.Offset(-View.Left, -View.Top);
        // Snapped to whole screen pixels and drawn aliased: antialiased edges at fractional positions leave a faint seam
        // where the dimmed strips meet.
        double px = 96.0 / sender.Dpi;
        double Snap(double v) => Math.Round(v / px) * px;
        double l = Snap(a.Left * scale - _originX), t = Snap(a.Top * scale - _originY), r = Snap(a.Right * scale - _originX), b = Snap(a.Bottom * scale - _originY);
        double w = sender.Size.Width, h = sender.Size.Height;
        CanvasAntialiasing was = ds.Antialiasing;
        ds.Antialiasing = CanvasAntialiasing.Aliased;
        var dim = global::Windows.UI.Color.FromArgb(0x88, 0, 0, 0);
        ds.FillRectangle(new Rect(0, 0, w, Math.Max(0, t)), dim);
        ds.FillRectangle(new Rect(0, b, w, Math.Max(0, h - b)), dim);
        ds.FillRectangle(new Rect(0, t, Math.Max(0, l), Math.Max(0, b - t)), dim);
        ds.FillRectangle(new Rect(r, t, Math.Max(0, w - r), Math.Max(0, b - t)), dim);
        // Strokes are centred on the path, so each rectangle is inset by half a line to sit on whole pixels.
        ds.DrawRectangle(new Rect(l - px / 2, t - px / 2, r - l + px, b - t + px), Microsoft.UI.Colors.Black, (float)px);
        ds.DrawRectangle(new Rect(l + px / 2, t + px / 2, Math.Max(0, r - l - px), Math.Max(0, b - t - px)), Microsoft.UI.Colors.White, (float)px);
        ds.Antialiasing = was;
    }

    /// <summary>
    /// The part of this element that is on screen, in effective pixels; called when the scroll, viewport or zoom
    /// changes. The Win2D control is resized only when the visible size changes (a resize makes a new swap chain).
    /// </summary>
    public void SetViewport(Rect visible)
    {
        if (_released) return;
        double w = double.IsNaN(Width) ? 0 : Width, h = double.IsNaN(Height) ? 0 : Height;
        double left = Math.Clamp(Math.Floor(visible.X), 0, Math.Max(0, w - 1)), top = Math.Clamp(Math.Floor(visible.Y), 0, Math.Max(0, h - 1));
        double width = Math.Max(1, Math.Min(Math.Ceiling(visible.X + visible.Width) - left, w - left));
        double height = Math.Max(1, Math.Min(Math.Ceiling(visible.Y + visible.Height) - top, h - top));
        // While a zoom animates, the control keeps the size it was given for the animation (HoldCanvasSize) and only
        // grows past it, so the swap chain is not rebuilt every frame. Anything it covers beyond the picture stays clear.
        if (_heldSize is (double hw, double hh))
        {
            width = Math.Max(width, Math.Ceiling(hw));
            height = Math.Max(height, Math.Ceiling(hh));
            _heldSize = (width, height);
        }
        bool moved = left != _originX || top != _originY;
        if (moved) { _originX = left; _originY = top; Canvas.SetLeft(_canvas, left); Canvas.SetTop(_canvas, top); }
        if (_canvas.Width != width || _canvas.Height != height) { _canvas.Width = width; _canvas.Height = height; }
        else if (moved) _canvas.Invalidate();
    }

    /// <summary>The size the Win2D control is held at while the zoom animates, in effective pixels, or null.</summary>
    private (double W, double H)? _heldSize;

    /// <summary>
    /// Keeps the Win2D control at least <paramref name="width"/> × <paramref name="height"/> (effective pixels) and no
    /// smaller than it is now, until <see cref="ReleaseCanvasSize"/>: a zoom animation calls it with the size on screen
    /// at its end, so the control is resized at most once going in and once coming out rather than on every frame.
    /// </summary>
    public void HoldCanvasSize(double width, double height)
    {
        (double w, double h) = _heldSize ?? (_canvas.Width, _canvas.Height);
        _heldSize = (Math.Max(w, width), Math.Max(h, height));
    }

    /// <summary>Ends <see cref="HoldCanvasSize"/>; the next <see cref="SetViewport"/> fits the control to the picture
    /// again.</summary>
    public void ReleaseCanvasSize() => _heldSize = null;

    /// <summary>16×16 BGRA of the two-grey chequerboard tiled behind the picture.</summary>
    private static readonly byte[] CheckerTile = BuildChecker();

    private static byte[] BuildChecker()
    {
        var t = new byte[CheckerSize * CheckerSize * 4];
        for (int y = 0; y < CheckerSize; y++)
            for (int x = 0; x < CheckerSize; x++)
            {
                byte v = (x < CheckerCell) == (y < CheckerCell) ? (byte)0x99 : (byte)0xCC;
                int i = (y * CheckerSize + x) * 4;
                t[i] = t[i + 1] = t[i + 2] = v; t[i + 3] = 0xFF;
            }
        return t;
    }

    private IntRect PadToView(IntRect local) => IntRect.FromLtrb(
        Math.Max(0, local.Left - EditSession.HandleSize), Math.Max(0, local.Top - EditSession.HandleSize),
        Math.Min(View.Width, local.Right + EditSession.HandleSize), Math.Min(View.Height, local.Bottom + EditSession.HandleSize));

    /// <summary>
    /// Exposure preview for the slider: the tonemap runs off the UI thread and only the newest pending value is used,
    /// so dragging cannot queue up a pass per step.
    /// </summary>
    public void SetExposure(float multiplier)
    {
        if (_closing) return;
        _exposure.Set(multiplier);
    }

    /// <summary>Completes when no exposure pass is in flight. A save must not read the image mid-tonemap, and the
    /// window must not compact the result out from under a worker thread.</summary>
    public Task ExposureIdle => _exposure.Idle;

    /// <summary>Stops the exposure loop for good; await <see cref="ExposureIdle"/> afterwards to join the last pass.</summary>
    public void Shutdown() { _closing = true; _exposure.Cancel(); }

    /// <summary>After <see cref="Shutdown"/> and once <see cref="ExposureIdle"/> completed: drops the bitmap and buffers,
    /// unhooks the session from the document, and takes the canvas out of the tree so Win2D's device goes with the
    /// window.</summary>
    public void ReleaseBuffers()
    {
        if (_released) return;
        _released = true;
        Session.Detach();
        ShapeRenderer.InvalidateRedactions(Session.Doc);
        _canvas.CreateResources -= OnCreateResources;
        _canvas.Draw -= OnDraw;
        _canvas.PointerPressed -= OnPointerPressed;
        _canvas.PointerMoved -= OnPointerMoved;
        _canvas.PointerReleased -= OnPointerReleased;
        _canvas.PointerCaptureLost -= OnPointerCaptureLost;
        _canvas.PointerExited -= OnPointerExited;
        _bitmap?.Dispose(); _bitmap = null;
        _checkerBrush?.Dispose(); _checkerBrush = null;
        _checker?.Dispose(); _checker = null;
        _upload = null;
        _target = null; _baseView = null; _original = null; _tmp = null; _lasso = null; _lassoSpans = null; _exposureLasso = null; _result = null; _zebraCrops = null;
        _canvas.RemoveFromVisualTree();
        _host.Children.Clear();
        Content = null;
    }

    /// <summary>One preview pass for <see cref="_exposure"/>: false stops the loop.</summary>
    private async Task<bool> ExposurePass(float m)
    {
        if (_closing || _result == null || _original == null || _result.Crops.Count == 0) return false;
        _result.ImageChanged();   // the pass rewrites the result's image in place, so a PNG held for it goes stale
        await Task.Run(() => Tonemap(m));
        if (_closing) return false;   // the window is going away: no repaint, and the result may already be compacting
        Apply(m);
        return true;
    }

    /// <summary>Re-tonemaps every HDR crop into the original image at the given multiplier. False when there is nothing to do.</summary>
    private bool Tonemap(float multiplier)
    {
        if (_result == null || _original == null || _result.Crops.Count == 0) return false;
        // A window snip's transparent corners go back on in there, or the image and the HDR file saved from it (which
        // takes its alpha from the image) would have black corners.
        _result.Retonemap(_original, multiplier, App.Current.Grabber, ref _tmp);
        // The copy restores pixels outside the lasso, so cut them again as CaptureResult.Build does (ApplyLasso
        // does not when annotate.clipToLasso is off). The spans are built on the first pass and reused by every later
        // one: rasterising the lasso again on each slider step cost as much as a small crop's tonemap.
        if (LassoFits)
        {
            _exposureLasso ??= Core.Imaging.FreeformSpans.Build(_original.Width, _original.Height, _result.Region, _result.Freeform!);
            _exposureLasso.ClearOutside(_original, Full);
        }
        return true;
    }

    /// <summary>The lasso rasterised over the whole image, for <see cref="Tonemap"/>. Only the exposure loop touches it,
    /// one pass at a time; reset when another snip is loaded.</summary>
    private Core.Imaging.FreeformSpans? _exposureLasso;

    /// <summary>UI-thread half of a re-expose: the base pixels changed, so cached redaction tiles are stale.</summary>
    private void Apply(float multiplier)
    {
        if (_result == null) return;
        _result.Exposure = multiplier;   // the image and the multiplier it was tonemapped with stay in step
        ShapeRenderer.InvalidateRedactions(Session.Doc);
        Rebuild();
        ExposureApplied?.Invoke();
    }

    public void SetZebra(bool on) { _zebra = on; Paint(IntRect.Empty); }

    /// <summary>Output pixels: base view plus shapes, no chrome, no zebra.</summary>
    public BgraImage RenderForOutput()
    {
        var outImg = BgraImage.Blank(View.Width, View.Height);
        ShapeRenderer.Render(Session.Doc, _baseView!, View, outImg, null, _accent);
        ApplyLasso(outImg, new IntRect(0, 0, outImg.Width, outImg.Height));
        return outImg;
    }

    /// <summary>Freeform snips keep their lasso: whatever is drawn outside it is cut, as the pixels were. Only
    /// <paramref name="area"/> (view pixels) is cut.</summary>
    private void ApplyLasso(BgraImage target, IntRect area)
    {
        if (!LassoFits || !App.Current.Settings.Annotate.ClipToLasso) return;
        _lasso ??= _result!.Freeform!.Select(p => (p.X - _result.Region.Left, p.Y - _result.Region.Top)).ToArray();
        // Rasterised once per view and reused by every paint, rather than scanned against the whole polygon each time.
        if (_lassoSpans == null || _lassoSpansFor != View)
        {
            _lassoSpans = Core.Imaging.FreeformSpans.Build(View.Width, View.Height, View, _lasso);
            _lassoSpansFor = View;
        }
        _lassoSpans.ClearOutside(target, area);
    }
    private (int X, int Y)[]? _lasso;
    /// <summary>The lasso's inside spans for <see cref="_lassoSpansFor"/>, the view (crop) they were built for.</summary>
    private Core.Imaging.FreeformSpans? _lassoSpans;
    private IntRect _lassoSpansFor;

    /// <summary>The result has a lasso and the image is still in the lasso's frame (a snip saved cropped is not).</summary>
    private bool LassoFits => _result?.Freeform != null && _original != null
        && _original.Width == _result.Region.Width && _original.Height == _result.Region.Height;

    // ----- input -----

    // Floor, not truncate: a drag can leave the control, and truncation would fold -0.5 onto 0 and shift the shape.
    private (int X, int Y) ToSource(Point p) => ((int)Math.Floor(p.X / Zoom) + View.Left, (int)Math.Floor(p.Y / Zoom) + View.Top);

    private static InputMods Mods(VirtualKeyModifiers m)
        => ((m & VirtualKeyModifiers.Shift) != 0 ? InputMods.Shift : 0) | ((m & VirtualKeyModifiers.Control) != 0 ? InputMods.Ctrl : 0);

    /// <summary>The colour picker is armed: the next left click reports the pixel under it (<see cref="ColourPicked"/>)
    /// instead of reaching the document, whatever tool is up.</summary>
    public bool Picking
    {
        get => _picking;
        set
        {
            if (_picking == value) return;
            _picking = value;
            ApplyCursor();
            // Armed with the pointer already over the picture (the K key), the loupe appears without waiting for a move.
            if (value && View.Contains(_pointer.X, _pointer.Y) && _pointerOver) PickerPointer?.Invoke(_pointer);
            else if (!value) PickerPointer?.Invoke(null);
        }
    }
    private bool _picking;

    /// <summary>
    /// Copy text is armed: a left drag marks an area (<see cref="TextAreaSelected"/>) instead of reaching the document,
    /// whatever tool is up. Setting it either way drops a mark in progress.
    /// </summary>
    public bool SelectingText
    {
        get => _selectingText;
        set
        {
            if (_selectingText == value) return;
            _selectingText = value;
            _textFrom = null;
            if (!_textArea.IsEmpty) { _textArea = IntRect.Empty; _canvas.Invalidate(); }
            ApplyCursor();
        }
    }
    private bool _selectingText;
    /// <summary>Where the Copy text drag started and the area it covers so far, in source pixels.</summary>
    private (int X, int Y)? _textFrom;
    private IntRect _textArea;
    /// <summary>Anything smaller than this either way is taken for a click, not an area.</summary>
    private const int MinTextArea = 4;

    /// <summary>A Copy text drag finished on an area at least <see cref="MinTextArea"/> square, in view pixels (0, 0 at
    /// the crop's top-left), which is how the output render is laid out.</summary>
    public event Action<IntRect>? TextAreaSelected;

    /// <summary>A click while <see cref="Picking"/>: the pixel as shown (0xAARRGGBB, shapes included), the nits under it
    /// when the snip has HDR data there, and whether Shift was held.</summary>
    public event Action<uint, float?, bool>? ColourPicked;

    /// <summary>The rendered pixel at a source-frame point, or null off the view.</summary>
    private uint? PixelAt(int x, int y)
    {
        if (_target is not { } t) return null;
        int lx = x - View.Left, ly = y - View.Top;
        if (lx < 0 || ly < 0 || lx >= t.Width || ly >= t.Height) return null;
        int i = (ly * t.Width + lx) * 4;
        return (uint)t.Data[i + 3] << 24 | (uint)t.Data[i + 2] << 16 | (uint)t.Data[i + 1] << 8 | t.Data[i];
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Panning) return;   // the drag belongs to the view, not to the document
        PointerPoint p = e.GetCurrentPoint(this);
        if (!p.Properties.IsLeftButtonPressed) return;
        if (Picking)
        {
            (int px, int py) = ToSource(p.Position);
            if (PixelAt(px, py) is uint argb) ColourPicked?.Invoke(argb, NitsAt(px, py), (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0);
            e.Handled = true;
            return;
        }
        if (SelectingText)
        {
            (int tx, int ty) = ToSource(p.Position);
            _textFrom = (tx, ty);
            _textArea = IntRect.Empty;
            _canvas.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }
        if (!Editing) return;
        (int x, int y) = ToSource(p.Position);
        Focus(FocusState.Programmatic);
        if (Session.Begin(x, y, Mods(e.KeyModifiers))) _canvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // Panning updates the cursor whether or not the tool row is up.
        (int x, int y) = _pointer = ToSource(e.GetCurrentPoint(this).Position);
        _pointerOver = true;
        PointerNits?.Invoke(NitsAt(x, y));
        if (Picking) PickerPointer?.Invoke(View.Contains(x, y) ? (x, y) : null);
        if (_textFrom is { } from)
        {
            _textArea = IntRect.FromDrag(from.X, from.Y, x, y).Intersect(View);
            _canvas.Invalidate();
            return;
        }
        if (!Editing && !Panning) return;
        // Session.Busy cannot be true while panning: OnPointerPressed refuses to begin one.
        if (Session.Busy) Session.Move(x, y, Mods(e.KeyModifiers));
        ApplyCursor();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        // A Copy text mark is taken out first: releasing the capture raises PointerCaptureLost at once, which would
        // otherwise drop it.
        (int X, int Y)? textFrom = _textFrom;
        _textFrom = null;
        // Always release capture first: Busy or Editing can be cleared mid-drag, and a stranded capture would swallow
        // every later click in the window.
        _canvas.ReleasePointerCaptures();
        // Fires for every button, so only a left-button release may commit the shape.
        Microsoft.UI.Input.PointerPoint point = e.GetCurrentPoint(this);
        if (point.Properties.PointerUpdateKind != Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased)
        {
            if (textFrom != null) { _textArea = IntRect.Empty; _canvas.Invalidate(); }
            return;
        }
        if (textFrom is { } f)
        {
            // The release point counts too: a quick drag can deliver no move between the press and the release.
            (int ex, int ey) = ToSource(point.Position);
            _textArea = IntRect.FromDrag(f.X, f.Y, ex, ey).Intersect(View);
            EndTextArea();
            return;
        }
        if (!Session.Busy) return;
        (int x, int y) = ToSource(point.Position);
        Session.End(x, y, Mods(e.KeyModifiers));
    }

    /// <summary>Capture lost to something else (a system gesture, another window): end the drag where it stopped
    /// rather than leaving the session Busy for ever.</summary>
    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_textFrom != null) { _textFrom = null; _textArea = IntRect.Empty; _canvas.Invalidate(); return; }
        if (!Session.Busy) return;
        (int x, int y) = ToSource(e.GetCurrentPoint(this).Position);
        Session.End(x, y, Mods(e.KeyModifiers));
    }

    /// <summary>The Copy text drag let go: an area big enough is reported (the host puts Copy text away, which clears
    /// the mark); a click leaves Copy text armed for another try.</summary>
    private void EndTextArea()
    {
        _textFrom = null;
        IntRect area = _textArea;
        if (area.Width < MinTextArea || area.Height < MinTextArea) { _textArea = IntRect.Empty; _canvas.Invalidate(); return; }
        TextAreaSelected?.Invoke(area.Offset(-View.Left, -View.Top));
    }
}
