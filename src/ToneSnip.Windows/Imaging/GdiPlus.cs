using System.Runtime.InteropServices;

namespace ToneSnip.Windows.Imaging;

/// <summary>
/// The slice of the GDI+ flat API (gdiplus.dll) the annotation rasterizer needs, as thin disposable handle wrappers.
/// <para>
/// The wrappers mirror their <c>System.Drawing</c> counterparts, defaults included (pens in world units, alternate fill
/// rule, colours as 0xAARRGGBB), without taking a dependency on <c>System.Drawing.Common</c>.
/// </para>
/// <para>
/// GDI+ is started at first use and never shut down; <c>GdiplusShutdown</c> at process exit only risks teardown-order
/// problems. Handles are released by <see cref="IDisposable"/>.
/// </para>
/// </summary>
public static partial class GdiPlus
{
    // ----- enums (native values; only the members the rasterizer uses) -----

    public enum SmoothingMode { AntiAlias = 4 }
    public enum PixelOffsetMode { Half = 4 }
    /// <summary>Grayscale antialiasing with hinting (3), and ClearType with hinting (5).</summary>
    public enum TextRenderingHint { AntiAliasGridFit = 3, ClearTypeGridFit = 5 }
    public enum LineCap { Square = 1, Round = 2 }
    public enum LineJoin { Round = 2 }
    public enum DashStyle { Solid = 0, Dash = 1 }
    /// <summary>Where the stroke sits relative to the path: centred (the default) or wholly inside it.</summary>
    public enum PenAlignment { Center = 0, Inset = 1 }
    public enum FontStyle { Regular = 0, Bold = 1 }
    public enum StringAlignment { Near = 0, Center = 1, Far = 2 }
    public enum HatchStyle { DiagonalCross = 5 }
    /// <summary>Blend the source over the destination (the default), or overwrite it including alpha, which is how a
    /// hole is punched back to transparency.</summary>
    public enum CompositingMode { SourceOver = 0, SourceCopy = 1 }

    [StructLayout(LayoutKind.Sequential)]
    public struct PointF
    {
        public float X, Y;
        public PointF(float x, float y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RectF
    {
        public float X, Y, W, H;
        public RectF(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; }
    }

    // ----- startup -----

    private static readonly object Gate = new();
    private static bool _started;

    private static void Init()
    {
        lock (Gate)
        {
            if (_started) return;
            var input = new StartupInput { GdiplusVersion = 1 };
            Check(GdiplusStartup(out IntPtr _, ref input, out StartupOutput _), nameof(GdiplusStartup));
            _started = true;
        }
    }

    private static void Check(int status, string call)
    {
        if (status != 0) throw new InvalidOperationException($"{call} failed: GDI+ status {status}");
    }

    // ----- handles -----

    /// <summary>A GDI+ bitmap over pixels the caller owns and keeps pinned: 32-bpp BGRA, top-down, stride bytes a row.</summary>
    public sealed class Bitmap : IDisposable
    {
        internal IntPtr Handle;

        /// <param name="premultiplied">32bpp PARGB instead of straight ARGB. Only for an HICON's colour DIB, which the
        /// shell composites as premultiplied; never for pixels that end up in a <see cref="Core.Imaging.BgraImage"/> or
        /// a PNG, which are straight alpha.</param>
        public Bitmap(int width, int height, int stride, IntPtr scan0, bool premultiplied = false)
        {
            Init();
            int format = premultiplied ? PixelFormat32bppPArgb : PixelFormat32bppArgb;
            Check(GdipCreateBitmapFromScan0(width, height, stride, format, scan0, out Handle), nameof(GdipCreateBitmapFromScan0));
        }

        public Graphics CreateGraphics()
        {
            Check(GdipGetImageGraphicsContext(Handle, out IntPtr g), nameof(GdipGetImageGraphicsContext));
            return new Graphics(g);
        }

        public void Dispose() { if (Handle != IntPtr.Zero) { GdipDisposeImage(Handle); Handle = IntPtr.Zero; } }
    }

    public sealed class Graphics : IDisposable
    {
        internal IntPtr Handle;
        internal Graphics(IntPtr handle) { Handle = handle; }

        /// <summary>Draws straight onto a device context, as the overlay windows do for their chrome.</summary>
        public static Graphics FromHdc(IntPtr hdc)
        {
            Init();
            Check(GdipCreateFromHDC(hdc, out IntPtr g), nameof(GdipCreateFromHDC));
            return new Graphics(g);
        }

        public SmoothingMode Smoothing { set => Check(GdipSetSmoothingMode(Handle, (int)value), nameof(GdipSetSmoothingMode)); }
        public PixelOffsetMode PixelOffset { set => Check(GdipSetPixelOffsetMode(Handle, (int)value), nameof(GdipSetPixelOffsetMode)); }
        public TextRenderingHint TextRendering { set => Check(GdipSetTextRenderingHint(Handle, (int)value), nameof(GdipSetTextRenderingHint)); }

        /// <summary>The gamma correction ClearType and grayscale antialiasing are applied with, 0-12 in thousandths of
        /// an em; GDI+ starts at 4. Lower is heavier and softer, higher thins the stems and sharpens them.</summary>
        public uint TextContrast { set => Check(GdipSetTextContrast(Handle, value), nameof(GdipSetTextContrast)); }
        public CompositingMode Compositing { set => Check(GdipSetCompositingMode(Handle, (int)value), nameof(GdipSetCompositingMode)); }

        /// <summary>Font metrics for a string, as <c>Graphics.MeasureString</c> reports them.</summary>
        public (float Width, float Height) MeasureString(string text, Font font, StringFormat format)
        {
            var layout = new RectF(0, 0, 1 << 14, 1 << 14);
            Check(GdipMeasureString(Handle, text, text.Length, font.Handle, ref layout, format.Handle, out RectF box, out int _, out int _), nameof(GdipMeasureString));
            return (box.W, box.H);
        }

        /// <summary>Replaces the clip with one rectangle.</summary>
        public void SetClip(int x, int y, int width, int height)
            => Check(GdipSetClipRectI(Handle, x, y, width, height, CombineModeReplace), nameof(GdipSetClipRectI));

        public void DrawLine(Pen pen, float x1, float y1, float x2, float y2)
            => Check(GdipDrawLine(Handle, pen.Handle, x1, y1, x2, y2), nameof(GdipDrawLine));

        public void DrawLines(Pen pen, PointF[] points) => DrawLines(pen, points, points.Length);

        /// <summary>The first <paramref name="count"/> points, so a caller can pass a pooled array longer than the line.</summary>
        public void DrawLines(Pen pen, PointF[] points, int count)
            => Check(GdipDrawLines(Handle, pen.Handle, points, Math.Min(count, points.Length)), nameof(GdipDrawLines));

        public void DrawRectangle(Pen pen, float x, float y, float width, float height)
            => Check(GdipDrawRectangle(Handle, pen.Handle, x, y, width, height), nameof(GdipDrawRectangle));

        public void FillRectangle(Brush brush, float x, float y, float width, float height)
            => Check(GdipFillRectangle(Handle, brush.Handle, x, y, width, height), nameof(GdipFillRectangle));

        public void DrawEllipse(Pen pen, float x, float y, float width, float height)
            => Check(GdipDrawEllipse(Handle, pen.Handle, x, y, width, height), nameof(GdipDrawEllipse));

        public void FillEllipse(Brush brush, float x, float y, float width, float height)
            => Check(GdipFillEllipse(Handle, brush.Handle, x, y, width, height), nameof(GdipFillEllipse));

        public void FillPolygon(Brush brush, PointF[] points)
            => Check(GdipFillPolygon(Handle, brush.Handle, points, points.Length, FillModeAlternate), nameof(GdipFillPolygon));

        public void DrawPath(Pen pen, Path path) => Check(GdipDrawPath(Handle, pen.Handle, path.Handle), nameof(GdipDrawPath));
        public void FillPath(Brush brush, Path path) => Check(GdipFillPath(Handle, brush.Handle, path.Handle), nameof(GdipFillPath));

        public void DrawString(string text, Font font, Brush brush, RectF layout, StringFormat format)
            => Check(GdipDrawString(Handle, text, text.Length, font.Handle, ref layout, format.Handle, brush.Handle), nameof(GdipDrawString));

        public void Dispose() { if (Handle != IntPtr.Zero) { GdipDeleteGraphics(Handle); Handle = IntPtr.Zero; } }
    }

    public sealed class Pen : IDisposable
    {
        internal IntPtr Handle;

        /// <param name="argb">0xAARRGGBB.</param>
        public Pen(uint argb, float width)
        {
            Init();
            Check(GdipCreatePen1(argb, width, UnitWorld, out Handle), nameof(GdipCreatePen1));
        }

        public LineCap StartCap { set => Check(GdipSetPenStartCap(Handle, (int)value), nameof(GdipSetPenStartCap)); }
        public LineCap EndCap { set => Check(GdipSetPenEndCap(Handle, (int)value), nameof(GdipSetPenEndCap)); }
        public LineJoin LineJoin { set => Check(GdipSetPenLineJoin(Handle, (int)value), nameof(GdipSetPenLineJoin)); }
        public DashStyle DashStyle { set => Check(GdipSetPenDashStyle(Handle, (int)value), nameof(GdipSetPenDashStyle)); }
        public PenAlignment Alignment { set => Check(GdipSetPenMode(Handle, (int)value), nameof(GdipSetPenMode)); }

        public void Dispose() { if (Handle != IntPtr.Zero) { GdipDeletePen(Handle); Handle = IntPtr.Zero; } }
    }

    public sealed class Brush : IDisposable
    {
        internal IntPtr Handle;
        private Brush(IntPtr handle) { Handle = handle; }

        public static Brush Solid(uint argb)
        {
            Init();
            Check(GdipCreateSolidFill(argb, out IntPtr b), nameof(GdipCreateSolidFill));
            return new Brush(b);
        }

        public static Brush Hatch(HatchStyle style, uint foreArgb, uint backArgb)
        {
            Init();
            Check(GdipCreateHatchBrush((int)style, foreArgb, backArgb, out IntPtr b), nameof(GdipCreateHatchBrush));
            return new Brush(b);
        }

        public void Dispose() { if (Handle != IntPtr.Zero) { GdipDeleteBrush(Handle); Handle = IntPtr.Zero; } }
    }

    public sealed class FontFamily : IDisposable
    {
        internal IntPtr Handle;
        private FontFamily(IntPtr handle) { Handle = handle; }

        /// <summary>Null when the family is not installed or not usable; any other failure throws rather than silently
        /// falling back.</summary>
        public static FontFamily? TryCreate(string name)
        {
            Init();
            int status = GdipCreateFontFamilyFromName(name, IntPtr.Zero, out IntPtr f);
            if (status is FontFamilyNotFound or NotTrueTypeFont) return null;
            Check(status, nameof(GdipCreateFontFamilyFromName));
            return new FontFamily(f);
        }

        /// <summary>
        /// The family GDI+ always has. Returned as a clone, because GDI+ hands back a process-wide object and
        /// <see cref="Dispose"/> deletes whatever handle it holds.
        /// </summary>
        public static FontFamily GenericSansSerif()
        {
            Init();
            Check(GdipGetGenericFontFamilySansSerif(out IntPtr f), nameof(GdipGetGenericFontFamilySansSerif));
            Check(GdipCloneFontFamily(f, out IntPtr clone), nameof(GdipCloneFontFamily));
            return new FontFamily(clone);
        }

        public void Dispose() { if (Handle != IntPtr.Zero) { GdipDeleteFontFamily(Handle); Handle = IntPtr.Zero; } }
    }

    public sealed class Font : IDisposable
    {
        internal IntPtr Handle;

        /// <param name="emSize">In pixels: the rasterizer's sizes are device pixels, never points.</param>
        public Font(FontFamily family, float emSize, FontStyle style)
        {
            Init();
            Check(GdipCreateFont(family.Handle, emSize, (int)style, UnitPixel, out Handle), nameof(GdipCreateFont));
        }

        public void Dispose() { if (Handle != IntPtr.Zero) { GdipDeleteFont(Handle); Handle = IntPtr.Zero; } }
    }

    public sealed class Path : IDisposable
    {
        internal IntPtr Handle;

        public Path()
        {
            Init();
            Check(GdipCreatePath(FillModeAlternate, out Handle), nameof(GdipCreatePath));
        }

        /// <summary>Text as outlines, laid out from a point (a zero-size layout rectangle).</summary>
        public void AddString(string text, FontFamily family, FontStyle style, float emSize, float x, float y, StringFormat format)
        {
            var layout = new RectF(x, y, 0, 0);
            Check(GdipAddPathString(Handle, text, text.Length, family.Handle, (int)style, emSize, ref layout, format.Handle), nameof(GdipAddPathString));
        }

        public void Dispose() { if (Handle != IntPtr.Zero) { GdipDeletePath(Handle); Handle = IntPtr.Zero; } }
    }

    public sealed class StringFormat : IDisposable
    {
        internal IntPtr Handle;
        private StringFormat(IntPtr handle) { Handle = handle; }

        public StringFormat()
        {
            Init();
            Check(GdipCreateStringFormat(0, 0, out Handle), nameof(GdipCreateStringFormat));
        }

        /// <summary>
        /// The typographic preset: no extra leading, no fitting of the glyph run into the layout box. Returned as a
        /// clone, because GDI+ hands back a process-wide object that must never be deleted.
        /// </summary>
        public static StringFormat GenericTypographic()
        {
            Init();
            Check(GdipStringFormatGetGenericTypographic(out IntPtr f), nameof(GdipStringFormatGetGenericTypographic));
            Check(GdipCloneStringFormat(f, out IntPtr clone), nameof(GdipCloneStringFormat));
            return new StringFormat(clone);
        }

        public StringAlignment Alignment { set => Check(GdipSetStringFormatAlign(Handle, (int)value), nameof(GdipSetStringFormatAlign)); }
        public StringAlignment LineAlignment { set => Check(GdipSetStringFormatLineAlign(Handle, (int)value), nameof(GdipSetStringFormatLineAlign)); }

        public void Dispose() { if (Handle != IntPtr.Zero) { GdipDeleteStringFormat(Handle); Handle = IntPtr.Zero; } }
    }

    // ----- native -----

    private const int PixelFormat32bppArgb = 2498570;   // 0x0026200A
    private const int PixelFormat32bppPArgb = 925707;   // 0x000E200B
    private const int UnitWorld = 0, UnitPixel = 2;
    private const int CombineModeReplace = 0;
    private const int FillModeAlternate = 0;
    /// <summary>GDI+ Status values for "that font is not usable": the only two TryCreate answers null for.</summary>
    private const int FontFamilyNotFound = 14, NotTrueTypeFont = 16;

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInput
    {
        public uint GdiplusVersion;
        public IntPtr DebugEventCallback;
        public int SuppressBackgroundThread, SuppressExternalCodecs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupOutput { public IntPtr NotificationHook, NotificationUnhook; }

    [LibraryImport("gdiplus.dll")] private static partial int GdiplusStartup(out IntPtr token, ref StartupInput input, out StartupOutput output);

    [LibraryImport("gdiplus.dll")] private static partial int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, IntPtr scan0, out IntPtr bitmap);
    [LibraryImport("gdiplus.dll")] private static partial int GdipDisposeImage(IntPtr image);
    [LibraryImport("gdiplus.dll")] private static partial int GdipGetImageGraphicsContext(IntPtr image, out IntPtr graphics);
    [LibraryImport("gdiplus.dll")] private static partial int GdipCreateFromHDC(IntPtr hdc, out IntPtr graphics);
    [LibraryImport("gdiplus.dll")] private static partial int GdipDeleteGraphics(IntPtr graphics);
    [LibraryImport("gdiplus.dll")] private static partial int GdipSetSmoothingMode(IntPtr graphics, int mode);
    [LibraryImport("gdiplus.dll")] private static partial int GdipSetPixelOffsetMode(IntPtr graphics, int mode);
    [LibraryImport("gdiplus.dll")] private static partial int GdipSetTextRenderingHint(IntPtr graphics, int hint);
    [LibraryImport("gdiplus.dll")] private static partial int GdipSetTextContrast(IntPtr graphics, uint contrast);
    [LibraryImport("gdiplus.dll")] private static partial int GdipSetClipRectI(IntPtr graphics, int x, int y, int width, int height, int combineMode);
    [LibraryImport("gdiplus.dll")] private static partial int GdipSetCompositingMode(IntPtr graphics, int mode);
    [LibraryImport("gdiplus.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int GdipMeasureString(IntPtr graphics, string text, int length, IntPtr font, ref RectF layoutRect, IntPtr format, out RectF bounds, out int codepointsFitted, out int linesFilled);

    [LibraryImport("gdiplus.dll")] private static partial int GdipCreatePen1(uint argb, float width, int unit, out IntPtr pen);
    [LibraryImport("gdiplus.dll")] private static partial int GdipDeletePen(IntPtr pen);
    [LibraryImport("gdiplus.dll")] private static partial int GdipSetPenStartCap(IntPtr pen, int cap);
    [LibraryImport("gdiplus.dll")] private static partial int GdipSetPenEndCap(IntPtr pen, int cap);
    [LibraryImport("gdiplus.dll")] private static partial int GdipSetPenLineJoin(IntPtr pen, int join);
    [LibraryImport("gdiplus.dll")] private static partial int GdipSetPenDashStyle(IntPtr pen, int style);
    [LibraryImport("gdiplus.dll")] private static partial int GdipSetPenMode(IntPtr pen, int alignment);

    [LibraryImport("gdiplus.dll")] private static partial int GdipCreateSolidFill(uint argb, out IntPtr brush);
    [LibraryImport("gdiplus.dll")] private static partial int GdipCreateHatchBrush(int style, uint foreArgb, uint backArgb, out IntPtr brush);
    [LibraryImport("gdiplus.dll")] private static partial int GdipDeleteBrush(IntPtr brush);

    [LibraryImport("gdiplus.dll")] private static partial int GdipDrawLine(IntPtr graphics, IntPtr pen, float x1, float y1, float x2, float y2);
    [LibraryImport("gdiplus.dll")] private static partial int GdipDrawLines(IntPtr graphics, IntPtr pen, [In] PointF[] points, int count);
    [LibraryImport("gdiplus.dll")] private static partial int GdipDrawRectangle(IntPtr graphics, IntPtr pen, float x, float y, float width, float height);
    [LibraryImport("gdiplus.dll")] private static partial int GdipFillRectangle(IntPtr graphics, IntPtr brush, float x, float y, float width, float height);
    [LibraryImport("gdiplus.dll")] private static partial int GdipDrawEllipse(IntPtr graphics, IntPtr pen, float x, float y, float width, float height);
    [LibraryImport("gdiplus.dll")] private static partial int GdipFillEllipse(IntPtr graphics, IntPtr brush, float x, float y, float width, float height);
    [LibraryImport("gdiplus.dll")] private static partial int GdipFillPolygon(IntPtr graphics, IntPtr brush, [In] PointF[] points, int count, int fillMode);
    [LibraryImport("gdiplus.dll")] private static partial int GdipDrawPath(IntPtr graphics, IntPtr pen, IntPtr path);
    [LibraryImport("gdiplus.dll")] private static partial int GdipFillPath(IntPtr graphics, IntPtr brush, IntPtr path);
    [LibraryImport("gdiplus.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int GdipDrawString(IntPtr graphics, string text, int length, IntPtr font, ref RectF layoutRect, IntPtr format, IntPtr brush);

    [LibraryImport("gdiplus.dll")] private static partial int GdipCreatePath(int fillMode, out IntPtr path);
    [LibraryImport("gdiplus.dll")] private static partial int GdipDeletePath(IntPtr path);
    [LibraryImport("gdiplus.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int GdipAddPathString(IntPtr path, string text, int length, IntPtr family, int style, float emSize, ref RectF layoutRect, IntPtr format);

    [LibraryImport("gdiplus.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int GdipCreateFontFamilyFromName(string name, IntPtr fontCollection, out IntPtr family);
    [LibraryImport("gdiplus.dll")] private static partial int GdipGetGenericFontFamilySansSerif(out IntPtr family);
    [LibraryImport("gdiplus.dll")] private static partial int GdipCloneFontFamily(IntPtr family, out IntPtr clone);
    [LibraryImport("gdiplus.dll")] private static partial int GdipDeleteFontFamily(IntPtr family);
    [LibraryImport("gdiplus.dll")] private static partial int GdipCreateFont(IntPtr family, float emSize, int style, int unit, out IntPtr font);
    [LibraryImport("gdiplus.dll")] private static partial int GdipDeleteFont(IntPtr font);

    [LibraryImport("gdiplus.dll")] private static partial int GdipCreateStringFormat(int formatAttributes, int language, out IntPtr format);
    [LibraryImport("gdiplus.dll")] private static partial int GdipStringFormatGetGenericTypographic(out IntPtr format);
    [LibraryImport("gdiplus.dll")] private static partial int GdipCloneStringFormat(IntPtr format, out IntPtr clone);
    [LibraryImport("gdiplus.dll")] private static partial int GdipSetStringFormatAlign(IntPtr format, int align);
    [LibraryImport("gdiplus.dll")] private static partial int GdipSetStringFormatLineAlign(IntPtr format, int align);
    [LibraryImport("gdiplus.dll")] private static partial int GdipDeleteStringFormat(IntPtr format);
}
