using ToneSnip.Core.Annotate;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Config;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Hdr;
using ToneSnip.Core.Imaging;
using ToneSnip.Core.Tonemap;

namespace ToneSnip.App.Capture;

/// <summary>One monitor's part of an HDR snip. <paramref name="BaseExposure"/> is the exposure the snip was tonemapped
/// with before any user multiplier, auto exposure included, fixed when the snip was built: the editor and the HDR file
/// reuse it rather than measure the crop again, and a settings change afterwards cannot make them disagree.</summary>
public sealed record HalfCrop(IntRect Bounds, HalfImage Image, OutputInfo Output, float BaseExposure);

/// <summary>The finished snip: BGRA8 image plus the half-float crops the viewer/editor may re-tonemap later.</summary>
public sealed class CaptureResult
{
    private BgraImage? _image;
    private byte[]? _png;
    /// <summary>The image whose pixels <see cref="_png"/> holds, so <see cref="Compact"/> can skip an encode that would
    /// produce the same bytes: an image decoded from it, or one whose PNG a save already made
    /// (<see cref="CachePng"/>). Reset by <see cref="ImageChanged"/>, since the editor re-exposes the image in
    /// place.</summary>
    private BgraImage? _pngOf;

    /// <summary>The snip as BGRA8. After <see cref="Compact"/> it is decoded from PNG on demand.</summary>
    public required BgraImage Image
    {
        get
        {
            if (_image == null) { _image = Decode!(_png!); _pngOf = _image; }
            return _image;
        }
        init => _image = value;
    }
    /// <summary>Set by the app: PNG decoder used to restore a compacted image.</summary>
    public static Func<byte[], BgraImage>? Decode { get; set; }

    /// <summary>The image without caching a decode, so reading a compacted result does not pin a full-size
    /// image.</summary>
    public BgraImage Peek() => Output ?? _image ?? Decode!(_png!);

    /// <summary>The PNG of a compacted result (the bytes <see cref="Peek"/> decodes), so a copy need not
    /// re-encode.</summary>
    public byte[]? CompactedPng => Output == null && _image == null ? _png : null;
    public static Func<BgraImage, byte[]>? Encode { get; set; }

    /// <summary>Records that <paramref name="png"/> is <paramref name="of"/> encoded (an editor save wrote it), so a
    /// later <see cref="Compact"/> keeps these bytes instead of encoding the image again.</summary>
    public void CachePng(BgraImage of, byte[] png) { _png = png; _pngOf = of; }

    /// <summary>The editor is rewriting <see cref="Image"/>'s pixels in place, so a PNG decoded from or made for it is no
    /// longer its encoding.</summary>
    public void ImageChanged() { if (ReferenceEquals(_pngOf, _image)) _pngOf = null; }

    /// <summary>Keeps only a PNG of the rendered image and drops the float crops and document, so shapes are no
    /// longer separately editable. The `edit` flow does not compact until the editor closes.</summary>
    public void Compact(byte[]? png = null)
    {
        if (Decode == null) return;
        // A newer render always replaces the cached PNG (for example after an editor save on a compacted result),
        // unless the cached PNG is already that render's.
        BgraImage? target = Output ?? _image;
        byte[]? fresh = target == null ? null : png ?? (ReferenceEquals(target, _pngOf) ? null : Encode?.Invoke(target));
        if (fresh != null) { _png = fresh; _pngOf = target; }
        if (_png == null) return;
        // A cropped render is no longer in the region's frame, and its lasso is already baked into its pixels.
        if (Output != null && (Output.Width != Region.Width || Output.Height != Region.Height)) Freeform = null;
        _image = null;
        _pngOf = null;
        Crops.Clear();
        Doc = null;
        Output = null;
    }

    /// <summary>
    /// <see cref="Compact"/> for a caller on the UI thread (the editor closing): the encode, when one is needed, runs on
    /// the thread pool, and the compaction itself back on the calling thread. None is needed when the PNG held is
    /// already the image's (nothing was edited, or a save made it).
    /// </summary>
    public async Task CompactAsync()
    {
        BgraImage? target = Output ?? _image;
        if (Decode == null || Encode is not { } encode || target == null || ReferenceEquals(target, _pngOf)) { Compact(); return; }
        byte[] png = await Task.Run(() => encode(target));
        // Something else may have compacted it, or a save replaced the render, while the encode ran.
        if (ReferenceEquals(Output ?? _image, target)) Compact(png);
        else Compact();
    }

    public required IntRect Region { get; init; }
    public required bool AnyHdr { get; init; }
    public required List<HalfCrop> Crops { get; init; }
    public DateTime TakenLocal { get; init; } = DateTime.Now;
    public string? SavedPath { get; set; }
    /// <summary>What the output pipeline attempted and whether it succeeded, so the notification reports it
    /// accurately.</summary>
    public bool SaveAttempted { get; set; }
    public bool CopyAttempted { get; set; }
    public bool Copied { get; set; }
    /// <summary>Path of the HDR sidecar file written next to <see cref="SavedPath"/>, if any; set by <see cref="ToneSnip.App.Output.HdrOutput.Write"/>.</summary>
    public string? HdrPath { get; set; }

    /// <summary>Shapes drawn in the overlay or the editor, in the same frame as <see cref="Region"/> (virtual-desktop pixels). Dropped by <see cref="Compact"/>.</summary>
    public AnnotationDoc? Doc { get; set; }
    /// <summary>Exposure multiplier the HDR crops were tonemapped with, over the settings value.</summary>
    public float Exposure { get; set; } = 1f;

    /// <summary>The rendered image (annotations and crop applied) that the toast/history thumbnail and a later plain viewer show; set by the output pipeline, cleared by <see cref="Compact"/>.</summary>
    public BgraImage? Output { get; set; }

    /// <summary>The image with annotations and crop applied. The plain image when there is no document.</summary>
    public BgraImage Rendered(uint accent = Annotate.ShapeRenderer.DefaultAccent)
    {
        if (Doc == null || (Doc.Shapes.Count == 0 && Doc.Crop.IsEmpty)) return Image;
        IntRect viewport = Doc.Crop.IsEmpty ? Region : Doc.Crop.Intersect(Region);
        BgraImage baseImg = viewport == Region ? Image : Image.Crop(viewport.Offset(-Region.Left, -Region.Top));
        var target = BgraImage.Blank(baseImg.Width, baseImg.Height);
        Annotate.ShapeRenderer.Render(Doc, baseImg, viewport, target, null, accent);
        if (Freeform != null && ClipToLassoSetting()) FreeformMask.Apply(target, viewport, Freeform);   // annotations are clipped to the lasso like the pixels are
        return target;
    }

    /// <summary>
    /// An active-window snip's transparent corners (<see cref="WindowGrab.Corners"/>), already on
    /// <see cref="Image"/> (<see cref="ApplyCorners"/>). Kept because <see cref="Retonemap"/> writes the crops over the
    /// image, alpha included, and has to put them back; the HDR file takes its alpha from the image.
    /// </summary>
    public WindowCorners? Corners { get; private set; }

    /// <summary>
    /// Puts an active-window snip's corners on the freshly built snip: on <see cref="Image"/>, whose tonemap wrote opaque
    /// pixels, and divided out of the HDR crops of the window, whose colour is premultiplied as captured. The HDR file
    /// pairs the crops' colour with the image's straight alpha, and <see cref="Retonemap"/> tonemaps it again.
    /// </summary>
    public void ApplyCorners(WindowCorners? corners)
    {
        Corners = corners;
        if (corners == null) return;
        corners.Apply(Image);
        foreach (HalfCrop c in Crops) if (c.Bounds == Region) corners.Unpremultiply(c.Image);
    }

    /// <summary>
    /// The editor's exposure pass: re-tonemaps every HDR crop at <paramref name="multiplier"/> over the base exposure it
    /// was built with (not measured again: a slider tick used to re-run the percentile) into <paramref name="target"/>,
    /// the region-sized image, then puts a window snip's corners back, since the copy brings the tonemap's opaque alpha.
    /// The crops' colour is already straight (<see cref="ApplyCorners"/>), so only the alpha is set. A freeform lasso is
    /// the caller's to cut again. <paramref name="tmp"/> is a buffer reused across passes.
    /// </summary>
    public void Retonemap(BgraImage target, float multiplier, FrameGrabber grabber, ref BgraImage? tmp)
    {
        foreach (HalfCrop c in Crops)
        {
            if (tmp == null || tmp.Width != c.Image.Width || tmp.Height != c.Image.Height) tmp = BgraImage.Blank(c.Image.Width, c.Image.Height);
            grabber.TonemapInto(c.Image, c.Output, c.BaseExposure * multiplier, tmp);
            IntRect dst = c.Bounds.Offset(-Region.Left, -Region.Top);
            for (int y = 0; y < dst.Height; y++) Buffer.BlockCopy(tmp.Data, y * tmp.Width * 4, target.Data, ((dst.Top + y) * target.Width + dst.Left) * 4, dst.Width * 4);
        }
        if (Corners is { } corners && target.Width == Region.Width && target.Height == Region.Height) corners.Apply(target, straight: true);
    }

    /// <summary>The freeform lasso, in the same frame as <see cref="Region"/>, when the snip was one; shapes are clipped to it.</summary>
    public IReadOnlyList<(int X, int Y)>? Freeform { get; private set; }
    /// <summary>Set by the app: reads the live `annotate.clipToLasso` setting.</summary>
    public static Func<bool> ClipToLassoSetting { get; set; } = () => true;

    /// <summary>
    /// Composes the snip from the grab. <paramref name="keepCrops"/> false leaves <see cref="Crops"/> empty: each HDR
    /// crop is a full-resolution half-float copy (8 bytes a pixel), and when neither an editor nor an HDR file will read
    /// it, <see cref="Compact"/> would only drop it again after the output. The HDR area is tonemapped from the frame
    /// either way; a frame on the graphics card reads back only the crop.
    /// <para>An HDR output whose frame was lost with its graphics device, or could not be read at all (before or during
    /// the build), goes in as SDR: its frozen image as the overlay showed it, and no crop, so no nits in the editor and
    /// nothing in the HDR file.</para>
    /// </summary>
    public static CaptureResult Build(List<CapturedOutput> outputs, IntRect region, IReadOnlyList<(int X, int Y)>? freeform, FrameGrabber grabber, SnipSettings settings, AnnotationDoc? doc = null, float exposure = 1f, bool retonemap = false, bool keepCrops = true)
    {
        var layers = new List<OutputLayer>();
        var crops = new List<HalfCrop>();
        var retonemapped = new List<(CapturedOutput Output, IHdrFrame Frame, IntRect Local, IntRect Hit, float Exposure, HalfCrop? Crop)>();
        int hdr = 0;
        foreach (CapturedOutput o in outputs)
        {
            IntRect hit = o.Info.Bounds.Intersect(region);
            if (hit.IsEmpty) continue;
            // Every output goes in as its frozen image first, so an HDR one whose frame is lost below still has pixels.
            layers.Add(new OutputLayer(o.Info.Bounds, o.Sdr));
            if (o.ReadableHdr is not { } frame) continue;
            IntRect local = hit.Offset(-o.Info.Left, -o.Info.Top);
            try
            {
                HalfImage? crop = keepCrops ? frame.Crop(local) : null;
                float baseExposure = BaseExposure(frame, local, crop, o.Info, settings);
                HalfCrop? kept = crop == null ? null : new HalfCrop(hit, crop, o.Info, baseExposure);
                if (kept != null) crops.Add(kept);
                hdr++;
                if (settings.AutoExposure || exposure != 1f || retonemap) retonemapped.Add((o, frame, local, hit, baseExposure * exposure, kept));
            }
            catch (Exception e) { FrameFailed(o, e); }   // the output stays SDR
        }
        BgraImage image = Compositor.Compose(layers, region);
        // Tonemapped from the frame straight into the composite, over the frozen image: no crop-sized image to allocate
        // and then copy.
        foreach ((CapturedOutput o, IHdrFrame frame, IntRect local, IntRect hit, float e, HalfCrop? crop) in retonemapped)
        {
            try { frame.Tonemap(grabber.CurveFor(o.Info, e), local, image, hit.Left - region.Left, hit.Top - region.Top); }
            catch (Exception ex)
            {
                FrameFailed(o, ex);
                // The frozen image is already in place; its crop goes too, so the snip does not claim HDR it lost.
                if (crop != null) crops.Remove(crop);
                hdr--;
            }
        }
        if (freeform != null) FreeformMask.Apply(image, region, freeform);
        return new CaptureResult { Image = image, Region = region, AnyHdr = hdr > 0, Crops = crops, Doc = doc, Exposure = exposure, Freeform = freeform };
    }

    /// <summary>Set by the app: where a frame that could not be read while building a snip is reported.</summary>
    public static ILog? Log { get; set; }

    /// <summary>
    /// An output's frame could not be read for the snip. Any failure counts as a loss for that output rather than of the
    /// snip, which keeps the frozen SDR image the overlay showed: a lost device is already logged where it was found,
    /// anything else (the device busy past its budget, out of memory) is logged here.
    /// </summary>
    private static void FrameFailed(CapturedOutput o, Exception e)
    {
        if (e is not HdrFrameLostException) Log?.Warn($"{o.Info.DeviceName}: the HDR frame could not be read, this snip keeps its SDR image: {e.GetType().Name}: {e.Message}");
    }

    /// <summary>
    /// The exposure an HDR snip of <paramref name="rect"/> (frame-relative) is tonemapped with before the user's
    /// multiplier: measured once here, then carried by <see cref="HalfCrop.BaseExposure"/>. Auto exposure reads the
    /// same samples whichever image it is given (<see cref="AutoExposure.Compute(HalfImage, IntRect, float, float)"/>):
    /// the snip's crop when it keeps one, and otherwise the frame itself, of which a frame on the graphics card reads
    /// back only the samples (<see cref="AutoExposure.Compute(IHdrFrame, IntRect, float, float)"/>).
    /// </summary>
    public static float BaseExposure(IHdrFrame frame, IntRect rect, HalfImage? crop, OutputInfo output, SnipSettings settings)
    {
        if (!settings.AutoExposure) return settings.Exposure;
        float white = settings.SdrWhiteNits ?? output.SdrWhiteNits;
        if (crop != null) return AutoExposure.Compute(crop, white, settings.Exposure);
        return AutoExposure.Compute(frame, rect, white, settings.Exposure);
    }
}
