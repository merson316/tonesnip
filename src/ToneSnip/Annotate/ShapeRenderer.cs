using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ToneSnip.Core.Annotate;
using ToneSnip.Core.Color;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using Gp = ToneSnip.Windows.Imaging.GdiPlus;

namespace ToneSnip.App.Annotate;

/// <summary>
/// Draws an annotation document over a base BGRA image into a target BGRA image of the same size, for one viewport of
/// the source frame. The overlay, the viewer and output all call this, so the pixels are identical everywhere.
/// <para>
/// Drawing goes through the GDI+ flat API (<c>ToneSnip.Windows.Imaging.GdiPlus</c>) rather than System.Drawing.Common,
/// avoiding the WinForms-era package. The self-test's <c>annotate hash</c> lines guard the output.
/// </para>
/// </summary>
public static class ShapeRenderer
{
    public const uint DefaultAccent = 0xFF0078D4;

    /// <summary>
    /// The font family, formats and counter fonts for text shapes. Not shared across threads: the HDR sidecar write
    /// and the output render run off the UI thread, and GDI+ handles are not thread-safe. The UI thread, which renders
    /// on every paint of the overlay and the editor, keeps one for the life of the process (<see cref="Text"/>); any
    /// other thread gets one per pass, disposed with it. Created lazily.
    /// </summary>
    private sealed class TextResources : IDisposable
    {
        /// <summary>More counter sizes than the style offers means something is producing odd sizes: the fonts are let
        /// go rather than piling up GDI+ handles on the UI thread for good.</summary>
        private const int MaxCounterFonts = 8;
        private Gp.FontFamily? _family;
        private Gp.StringFormat? _typographic, _centred;
        private readonly Dictionary<float, Gp.Font> _counterFonts = new();

        /// <summary>"Segoe UI Variable Text", falling back to "Segoe UI" and then the generic sans-serif family.</summary>
        public Gp.FontFamily Family => _family ??= Resolve();
        public Gp.StringFormat Typographic => _typographic ??= Gp.StringFormat.GenericTypographic();

        /// <summary>Centred both ways, for a counter's number.</summary>
        public Gp.StringFormat Centred
        {
            get
            {
                if (_centred != null) return _centred;
                var f = new Gp.StringFormat();
                try { f.Alignment = Gp.StringAlignment.Center; f.LineAlignment = Gp.StringAlignment.Center; }
                catch { f.Dispose(); throw; }
                return _centred = f;
            }
        }

        /// <summary>The bold font of a counter's number at <paramref name="emSize"/> pixels.</summary>
        public Gp.Font CounterFont(float emSize)
        {
            if (_counterFonts.TryGetValue(emSize, out Gp.Font? font)) return font;
            if (_counterFonts.Count >= MaxCounterFonts) DisposeCounterFonts();
            font = new Gp.Font(Family, emSize, Gp.FontStyle.Bold);
            _counterFonts[emSize] = font;
            return font;
        }

        private void DisposeCounterFonts()
        {
            foreach (Gp.Font f in _counterFonts.Values) f.Dispose();
            _counterFonts.Clear();
        }

        private static Gp.FontFamily Resolve()
        {
            foreach (string name in new[] { "Segoe UI Variable Text", "Segoe UI" })
                if (Gp.FontFamily.TryCreate(name) is Gp.FontFamily f) return f;
            return Gp.FontFamily.GenericSansSerif();
        }

        public void Dispose()
        {
            DisposeCounterFonts();
            _family?.Dispose(); _family = null;
            _typographic?.Dispose(); _typographic = null;
            _centred?.Dispose(); _centred = null;
        }
    }

    /// <summary>The UI thread's text resources, kept for the process: GDI+ is never shut down (see GdiPlus), and they
    /// are a family, two formats and a few fonts.</summary>
    [ThreadStatic] private static TextResources? t_uiText;

    /// <summary>The resources for a pass on this thread, and whether the pass owns (and must dispose) them.</summary>
    private static (TextResources Text, bool Owned) Text()
    {
        if (t_uiText != null) return (t_uiText, false);
        // Only a thread with a dispatcher queue is a UI thread; pool threads come and go, so theirs are per pass.
        if (Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() == null) return (new TextResources(), true);
        return (t_uiText = new TextResources(), false);
    }

    public static uint ResolveColor(uint c, uint accent) => c == Style.AccentPlaceholder ? accent : c;

    /// <summary>The shape a host should leave out of the render while it is being dragged: the moved copy is painted as
    /// chrome, so drawing the original underneath it too would leave a ghost trailing the drag.</summary>
    public static int? DragGhostId(EditSession session) => session.InProgress != null && session.Tool == Tool.Select ? session.Doc.SelectedId : null;

    /// <param name="suppressId">Shape left out of the draw pass (not the redaction pass); see <see cref="DragGhostId"/>.</param>
    /// <param name="redactions">False skips the redaction pass entirely (growth loop, tile cache, prune): the caller
    /// applies its own redaction (e.g. <see cref="ToneSnip.Core.Hdr.HdrRedaction"/> on the linear-light canvas) and only
    /// wants the non-redaction shapes drawn.</param>
    /// <returns>The viewport-relative rectangle actually repainted (may be larger than <paramref name="dirty"/> when a
    /// redaction it touches needed to grow the area); <see cref="IntRect.Empty"/> if nothing was painted.</returns>
    public static IntRect Render(AnnotationDoc doc, BgraImage baseImg, IntRect viewport, BgraImage target, IntRect? dirty = null, uint accentArgb = DefaultAccent, int? suppressId = null, bool redactions = true)
    {
        if (baseImg.Width != target.Width || baseImg.Height != target.Height) throw new ArgumentException("base and target must match");
        IntRect local = new IntRect(0, 0, target.Width, target.Height);
        IntRect area = dirty is IntRect d ? d.Intersect(local) : local;
        if (area.IsEmpty) return IntRect.Empty;
        IntRect areaSrc = area.Offset(viewport.Left, viewport.Top);
        if (redactions)
        {
            // A redaction's own pixels depend on its full rect (mean, jitter grid, blur edges), so an incremental repaint
            // that only redacts the dirty slice would differ from a full render. Grow the area to fully cover every
            // redaction it touches, transitively, before copying the base or redacting anything.
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (Shape s in doc.Shapes)
                    if (s is RedactShape r && r.Rect.IntersectsWith(areaSrc) && !Contains(areaSrc, r.Rect.Intersect(viewport)))
                    { areaSrc = areaSrc.Union(r.Rect.Intersect(viewport)); grew = true; }
            }
            area = areaSrc.Offset(-viewport.Left, -viewport.Top).Intersect(local);
        }
        for (int y = area.Top; y < area.Bottom; y++) Buffer.BlockCopy(baseImg.Data, (y * baseImg.Width + area.Left) * 4, target.Data, (y * target.Width + area.Left) * 4, area.Width * 4);
        if (redactions)
        {
            Dictionary<int, CachedTile> cache = TileCache.GetOrCreateValue(doc);
            int count = 0;
            foreach (Shape s in doc.Shapes)
                if (s is RedactShape r) { count++; if (r.Rect.IntersectsWith(areaSrc)) Redact(cache, r, baseImg, target, viewport); }
            if (cache.Count > count) Prune(cache, doc);
        }
        using Surface g = new(target, area);
        (TextResources text, bool owned) = Text();
        try
        {
            foreach (Shape s in doc.Shapes) if (!s.IsRedaction && s.Id != suppressId && s.DirtyBounds.IntersectsWith(areaSrc)) Draw(g.Graphics, s, viewport, accentArgb, text);
        }
        finally { if (owned) text.Dispose(); }
        return area;
    }

    // ----- redaction tile cache -----
    //
    // Blur and pixelate are the expensive part of a repaint, and touching a redaction forces the whole shape to be
    // recomputed, so a pen stroke dragged over a blur would re-blur it on every move. The result depends only on the
    // shape record, base image and viewport, so the last tile per redaction id is cached and blitted back.
    // Keyed weakly by document, so a closed editor's tiles go with it. A redaction spanning two monitors alternates
    // viewports and simply misses the cache.
    // UI thread only, so no locking. The HDR sidecar write, which runs off the UI thread, passes redactions: false.

    private sealed record CachedTile(RedactShape Key, BgraImage Base, IntRect Viewport, BgraImage Tile);
    private static readonly ConditionalWeakTable<AnnotationDoc, Dictionary<int, CachedTile>> TileCache = new();

    /// <summary>Drops a document's cached tiles. Needed when the base image's pixels change in place, as a re-tonemap for
    /// a new exposure does.</summary>
    public static void InvalidateRedactions(AnnotationDoc doc) => TileCache.Remove(doc);

    private static void Redact(Dictionary<int, CachedTile> cache, RedactShape r, BgraImage baseImg, BgraImage target, IntRect viewport)
    {
        IntRect hit = r.Rect.Intersect(viewport);
        if (hit.IsEmpty) return;
        IntRect local = hit.Offset(-viewport.Left, -viewport.Top);
        cache.TryGetValue(r.Id, out CachedTile? c);
        bool fits = c != null && c.Tile.Width == local.Width && c.Tile.Height == local.Height;
        // ReferenceEquals is an identity check, and pooled frames are reused with different pixels. That is safe because
        // each snip has its own AnnotationDoc (the cache key), OverlaySession.Finish drops the entry, and an in-session
        // re-tonemap calls InvalidateRedactions.
        if (!(fits && c!.Key == r && c.Viewport == viewport && ReferenceEquals(c.Base, baseImg)))
        {
            // Redacting into a tile framed on `hit` gives the same pixels as redacting in place: the grid still counts
            // from the shape's corner, and blur and mean only read inside the rect.
            BgraImage tile = fits ? c!.Tile : BgraImage.Blank(local.Width, local.Height);
            for (int y = 0; y < local.Height; y++)
                Buffer.BlockCopy(baseImg.Data, ((local.Top + y) * baseImg.Width + local.Left) * 4, tile.Data, y * local.Width * 4, local.Width * 4);
            Redaction.Apply(r, tile, tile, hit);
            c = new CachedTile(r, baseImg, viewport, tile);
            cache[r.Id] = c;
        }
        for (int y = 0; y < local.Height; y++)
            Buffer.BlockCopy(c!.Tile.Data, y * local.Width * 4, target.Data, ((local.Top + y) * target.Width + local.Left) * 4, local.Width * 4);
    }

    /// <summary>Drops tiles for redactions the document no longer has (deleted, or undone).</summary>
    private static void Prune(Dictionary<int, CachedTile> cache, AnnotationDoc doc)
    {
        foreach (int id in cache.Keys.ToArray())
        {
            bool live = false;
            foreach (Shape s in doc.Shapes) if (s is RedactShape r && r.Id == id) { live = true; break; }
            if (!live) cache.Remove(id);
        }
    }

    public static void Chrome(EditSession session, IntRect viewport, BgraImage target, uint accentArgb)
    {
        using Surface g = new(target, new IntRect(0, 0, target.Width, target.Height));
        Chrome(session, viewport, g.Graphics, target.Width, target.Height, accentArgb);
    }

    /// <summary>
    /// Same chrome straight into a device context, for hosts that paint through GDI (the overlay windows) so the
    /// in-progress shape and the handles never land in their cached back buffer.
    /// </summary>
    public static void Chrome(EditSession session, IntRect viewport, IntPtr hdc, int width, int height, uint accentArgb)
    {
        using Gp.Graphics gr = Gp.Graphics.FromHdc(hdc);
        // The same hints Surface sets, so chrome antialiases in the overlay as it does in the editor.
        gr.Smoothing = Gp.SmoothingMode.AntiAlias;
        gr.TextRendering = Gp.TextRenderingHint.AntiAliasGridFit;
        gr.PixelOffset = Gp.PixelOffsetMode.Half;
        Chrome(session, viewport, gr, width, height, accentArgb);
    }

    private static void Chrome(EditSession session, IntRect viewport, Gp.Graphics gr, int width, int height, uint accentArgb)
    {
        (TextResources text, bool owned) = Text();
        try { ChromeCore(session, viewport, gr, width, height, accentArgb, text); }
        finally { if (owned) text.Dispose(); }
    }

    private static void ChromeCore(EditSession session, IntRect viewport, Gp.Graphics gr, int width, int height, uint accentArgb, TextResources text)
    {
        if (session.InProgress is Shape ip)
        {
            if (ip is RedactShape rp)
            {
                using Gp.Brush hatch = Gp.Brush.Hatch(Gp.HatchStyle.DiagonalCross, 0x78FFFFFF, 0x3C000000);
                Gp.RectF hr = Rect(rp.Rect, viewport);
                gr.FillRectangle(hatch, hr.X, hr.Y, hr.W, hr.H);
            }
            else Draw(gr, ip, viewport, accentArgb, text);
        }
        Shape? sel = session.InProgress ?? session.Doc.Selected;
        if (sel != null && session.Tool == Tool.Select)
        {
            IntRect b = sel.Bounds;
            using Gp.Pen pen = new(0xDCFFFFFF, 1f);
            pen.DashStyle = Gp.DashStyle.Dash;
            using Gp.Pen dark = new(0xA0000000, 3f);
            Gp.RectF rb = Rect(b, viewport);
            gr.DrawRectangle(dark, rb.X, rb.Y, rb.W, rb.H); gr.DrawRectangle(pen, rb.X, rb.Y, rb.W, rb.H);
            using Gp.Brush fill = Gp.Brush.Solid(0xFFFFFFFF); using Gp.Pen edge = new(accentArgb, 1.5f);
            foreach ((Handle _, int hx, int hy) in Handles.Of(sel))
            {
                float x = hx - viewport.Left - EditSession.HandleSize / 2f, y = hy - viewport.Top - EditSession.HandleSize / 2f;
                gr.FillEllipse(fill, x, y, EditSession.HandleSize, EditSession.HandleSize);
                gr.DrawEllipse(edge, x, y, EditSession.HandleSize, EditSession.HandleSize);
            }
        }
        IntRect m = session.CropMarquee.Intersect(viewport).Offset(-viewport.Left, -viewport.Top);
        if (!m.IsEmpty)
        {
            using Gp.Brush dim = Gp.Brush.Solid(0x88000000);
            gr.FillRectangle(dim, 0, 0, width, m.Top); gr.FillRectangle(dim, 0, m.Bottom, width, height - m.Bottom);
            gr.FillRectangle(dim, 0, m.Top, m.Left, m.Height); gr.FillRectangle(dim, m.Right, m.Top, width - m.Right, m.Height);
            using Gp.Pen black = new(0xFF000000, 1f); using Gp.Pen white = new(0xFFFFFFFF, 1f);
            gr.DrawRectangle(black, m.Left - 1, m.Top - 1, m.Width + 1, m.Height + 1); gr.DrawRectangle(white, m.Left, m.Top, m.Width - 1, m.Height - 1);
            using Gp.Brush fill = Gp.Brush.Solid(0xFFFFFFFF);
            foreach ((Handle _, int hx, int hy) in Handles.Of(new BoxShape(0, session.CropMarquee, 1, 0, false, false)))
                gr.FillRectangle(fill, hx - viewport.Left - 4, hy - viewport.Top - 4, 8, 8);
        }
    }

    /// <summary>Diagonal magenta/black stripes over pixels that exceed SDR white after exposure. View only.</summary>
    /// <param name="dirty">Optional viewport-relative rectangle (0,0 at the viewport's top-left) restricting the striped area to a repaint region.</param>
    public static void Zebra(BgraImage target, IntRect viewport, ReadOnlySpan<(IntRect Bounds, HalfImage Half)> crops, float sdrWhiteScRgb, float exposure, IntRect? dirty = null)
    {
        IntRect? dirtyAbs = dirty is IntRect d ? d.Offset(viewport.Left, viewport.Top) : null;
        foreach ((IntRect bounds, HalfImage half) in crops)
        {
            IntRect hit = bounds.Intersect(viewport);
            if (dirtyAbs is IntRect da) hit = hit.Intersect(da);
            if (hit.IsEmpty) continue;
            // A pen stroke's dirty rectangle is a few thousand pixels, less than the cost of fanning out to the pool.
            if ((long)hit.Width * hit.Height < ZebraParallelPixels)
                for (int y = hit.Top; y < hit.Bottom; y++) ZebraRow(target, viewport, bounds, half, sdrWhiteScRgb, exposure, hit, y);
            else
                Parallel.For(hit.Top, hit.Bottom, y => ZebraRow(target, viewport, bounds, half, sdrWhiteScRgb, exposure, hit, y));
        }
    }

    /// <summary>
    /// <see cref="Zebra(BgraImage, IntRect, ReadOnlySpan{ValueTuple{IntRect, HalfImage}}, float, float, IntRect?)"/> from
    /// a precomputed <see cref="ZebraMask"/> of the monitor at <paramref name="bounds"/>: the overlay's form, whose
    /// frame may be on the graphics card, where the mask is computed once per exposure rather than per paint.
    /// </summary>
    public static void Zebra(BgraImage target, IntRect viewport, IntRect bounds, ZebraMask mask, IntRect? dirty = null)
    {
        IntRect hit = bounds.Intersect(viewport);
        if (dirty is IntRect d) hit = hit.Intersect(d.Offset(viewport.Left, viewport.Top));
        if (hit.IsEmpty) return;
        if ((long)hit.Width * hit.Height < ZebraParallelPixels)
            for (int y = hit.Top; y < hit.Bottom; y++) ZebraRow(target, viewport, bounds, mask, hit, y);
        else
            Parallel.For(hit.Top, hit.Bottom, y => ZebraRow(target, viewport, bounds, mask, hit, y));
    }

    private static void ZebraRow(BgraImage target, IntRect viewport, IntRect bounds, ZebraMask mask, IntRect hit, int y)
    {
        for (int x = hit.Left; x < hit.Right; x++)
            if (mask.Over(x - bounds.Left, y - bounds.Top)) Stripe(target, viewport, x, y);
    }

    /// <summary>One zebra pixel: magenta or black by diagonal band.</summary>
    private static void Stripe(BgraImage target, IntRect viewport, int x, int y)
    {
        int i = ((y - viewport.Top) * target.Width + (x - viewport.Left)) * 4;
        bool on = (((x + y) >> 2) & 1) == 0;
        target.Data[i] = on ? (byte)255 : (byte)0; target.Data[i + 1] = 0; target.Data[i + 2] = on ? (byte)255 : (byte)0;
    }

    /// <summary>Below this many pixels the zebra pass runs on the calling thread.</summary>
    private const int ZebraParallelPixels = 64 * 1024;

    private static void ZebraRow(BgraImage target, IntRect viewport, IntRect bounds, HalfImage half, float sdrWhiteScRgb, float exposure, IntRect hit, int y)
    {
        for (int x = hit.Left; x < hit.Right; x++)
        {
            (float r, float g, float b) = half.Sample(x - bounds.Left, y - bounds.Top);
            if (ZebraMask.Exceeds(r, g, b, sdrWhiteScRgb, exposure)) Stripe(target, viewport, x, y);
        }
    }

    private static void Draw(Gp.Graphics g, Shape s, IntRect vp, uint accent, TextResources text)
    {
        switch (s)
        {
            case PenShape p:
            {
                // Pooled: every stroke in the dirty rect is redrawn on every pointer move.
                int n = p.Points.Count;
                Gp.PointF[] pts = System.Buffers.ArrayPool<Gp.PointF>.Shared.Rent(n);
                try
                {
                    for (int i = 0; i < n; i++) pts[i] = new Gp.PointF(p.Points[i].X - vp.Left + 0.5f, p.Points[i].Y - vp.Top + 0.5f);
                    uint c = p.Highlighter ? Alpha(0x73, ResolveColor(p.Color, accent)) : ResolveColor(p.Color, accent);
                    using Gp.Pen pen = new(c, p.Width);
                    pen.StartCap = p.Highlighter ? Gp.LineCap.Square : Gp.LineCap.Round;
                    pen.EndCap = p.Highlighter ? Gp.LineCap.Square : Gp.LineCap.Round;
                    pen.LineJoin = Gp.LineJoin.Round;
                    if (n == 1) { using Gp.Brush br = Gp.Brush.Solid(c); g.FillEllipse(br, pts[0].X - p.Width / 2f, pts[0].Y - p.Width / 2f, p.Width, p.Width); }
                    else g.DrawLines(pen, pts, n);
                }
                finally { System.Buffers.ArrayPool<Gp.PointF>.Shared.Return(pts); }
                break;
            }
            case LineShape l:
            {
                uint c = ResolveColor(l.Color, accent);
                using Gp.Pen pen = new(c, l.Width);
                pen.StartCap = Gp.LineCap.Round; pen.EndCap = Gp.LineCap.Round;
                var a = new Gp.PointF(l.X1 - vp.Left + 0.5f, l.Y1 - vp.Top + 0.5f); var b = new Gp.PointF(l.X2 - vp.Left + 0.5f, l.Y2 - vp.Top + 0.5f);
                if (!l.Arrow) { g.DrawLine(pen, a.X, a.Y, b.X, b.Y); break; }
                double ang = Math.Atan2(b.Y - a.Y, b.X - a.X); float hs = l.HeadSize;
                var tip = b; var baseC = new Gp.PointF(b.X - (float)Math.Cos(ang) * hs, b.Y - (float)Math.Sin(ang) * hs);
                g.DrawLine(pen, a.X, a.Y, baseC.X, baseC.Y);
                var p1 = new Gp.PointF(baseC.X + (float)Math.Sin(ang) * hs / 2, baseC.Y - (float)Math.Cos(ang) * hs / 2);
                var p2 = new Gp.PointF(baseC.X - (float)Math.Sin(ang) * hs / 2, baseC.Y + (float)Math.Cos(ang) * hs / 2);
                using Gp.Brush br = Gp.Brush.Solid(c); g.FillPolygon(br, new[] { tip, p1, p2 });
                break;
            }
            case BoxShape bx:
            {
                Gp.RectF r = Rect(bx.Rect, vp);
                if (bx.Filled) { using Gp.Brush br = Gp.Brush.Solid(ResolveColor(bx.Color, accent)); if (bx.Ellipse) g.FillEllipse(br, r.X, r.Y, r.W, r.H); else g.FillRectangle(br, r.X, r.Y, r.W, r.H); }
                else
                {
                    using Gp.Pen pen = new(ResolveColor(bx.Color, accent), bx.Width);
                    pen.Alignment = Gp.PenAlignment.Inset;
                    if (bx.Ellipse) g.DrawEllipse(pen, r.X, r.Y, r.W, r.H); else g.DrawRectangle(pen, r.X, r.Y, r.W, r.H);
                }
                break;
            }
            case TextShape t:
            {
                using Gp.Path path = new();
                // t.Size is already device pixels (see Style.TextSizes), not points.
                path.AddString(t.Text, text.Family, Gp.FontStyle.Regular, t.Size, t.X - vp.Left, t.Y - vp.Top, text.Typographic);
                using Gp.Pen outline = new(0xC8000000, 2f);
                outline.LineJoin = Gp.LineJoin.Round;
                using Gp.Brush br = Gp.Brush.Solid(ResolveColor(t.Color, accent));
                g.DrawPath(outline, path); g.FillPath(br, path);
                break;
            }
            case CounterShape c:
            {
                var r = new Gp.RectF(c.X - vp.Left - c.Radius, c.Y - vp.Top - c.Radius, 2 * c.Radius, 2 * c.Radius);
                using Gp.Brush br = Gp.Brush.Solid(ResolveColor(c.Color, accent)); g.FillEllipse(br, r.X, r.Y, r.W, r.H);
                using Gp.Pen edge = new(0xA0000000, 1f); g.DrawEllipse(edge, r.X, r.Y, r.W, r.H);
                using Gp.Brush white = Gp.Brush.Solid(0xFFFFFFFF);
                g.DrawString(c.Number.ToString(), text.CounterFont(c.Size * 0.9f), white, r, text.Centred);
                break;
            }
        }
    }

    private static Gp.RectF Rect(IntRect r, IntRect vp) => new(r.Left - vp.Left, r.Top - vp.Top, r.Width, r.Height);
    /// <summary>The colour with a different alpha.</summary>
    private static uint Alpha(uint a, uint argb) => (a << 24) | (argb & 0x00FFFFFF);
    private static bool Contains(IntRect outer, IntRect inner) => inner.IsEmpty || (inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom);

    /// <summary>A GDI+ Graphics over a pinned BGRA buffer, clipped to one rectangle. No copies.</summary>
    private sealed class Surface : IDisposable
    {
        private readonly GCHandle _pin; private readonly Gp.Bitmap _bmp;
        public Gp.Graphics Graphics { get; }
        public Surface(BgraImage img, IntRect clip)
        {
            _pin = GCHandle.Alloc(img.Data, GCHandleType.Pinned);
            Gp.Bitmap? bmp = null;
            Gp.Graphics? gr = null;
            try
            {
                bmp = new Gp.Bitmap(img.Width, img.Height, img.Width * 4, _pin.AddrOfPinnedObject());
                gr = bmp.CreateGraphics();
                // Inside the try: Dispose only runs on a fully built Surface, so a throwing setter would leak the pin.
                gr.Smoothing = Gp.SmoothingMode.AntiAlias; gr.TextRendering = Gp.TextRenderingHint.AntiAliasGridFit; gr.PixelOffset = Gp.PixelOffsetMode.Half;
                gr.SetClip(clip.Left, clip.Top, clip.Width, clip.Height);
            }
            catch
            {
                gr?.Dispose();
                bmp?.Dispose();
                _pin.Free();
                throw;
            }
            _bmp = bmp; Graphics = gr;
        }
        public void Dispose() { Graphics.Dispose(); _bmp.Dispose(); _pin.Free(); }
    }
}
