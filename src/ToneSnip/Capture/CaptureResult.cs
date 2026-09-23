using ToneSnip.Core.Annotate;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Config;
using ToneSnip.Core.Geometry;
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

    /// <summary>The freeform lasso, in the same frame as <see cref="Region"/>, when the snip was one; shapes are clipped to it.</summary>
    public IReadOnlyList<(int X, int Y)>? Freeform { get; private set; }
    /// <summary>Set by the app: reads the live `annotate.clipToLasso` setting.</summary>
    public static Func<bool> ClipToLassoSetting { get; set; } = () => true;

    /// <summary>
    /// Composes the snip from the grab. <paramref name="keepCrops"/> false leaves <see cref="Crops"/> empty: each HDR
    /// crop is a full-resolution half-float copy (8 bytes a pixel), and when neither an editor nor an HDR file will read
    /// it, <see cref="Compact"/> would only drop it again after the output. The HDR area is tonemapped from the frame
    /// either way.
    /// </summary>
    public static CaptureResult Build(List<CapturedOutput> outputs, IntRect region, IReadOnlyList<(int X, int Y)>? freeform, FrameGrabber grabber, SnipSettings settings, AnnotationDoc? doc = null, float exposure = 1f, bool retonemap = false, bool keepCrops = true)
    {
        var layers = new List<OutputLayer>();
        var crops = new List<HalfCrop>();
        var retonemapped = new List<(CapturedOutput Output, IntRect Local, IntRect Hit, float Exposure)>();
        bool anyHdr = false;
        foreach (CapturedOutput o in outputs)
        {
            IntRect hit = o.Info.Bounds.Intersect(region);
            if (hit.IsEmpty) continue;
            if (o.Half != null)
            {
                anyHdr = true;
                IntRect local = hit.Offset(-o.Info.Left, -o.Info.Top);
                float baseExposure = BaseExposure(o.Half, local, o.Info, settings);
                if (keepCrops) crops.Add(new HalfCrop(hit, o.Half.Crop(local), o.Info, baseExposure));
                if (settings.AutoExposure || exposure != 1f || retonemap) { retonemapped.Add((o, local, hit, baseExposure * exposure)); continue; }
            }
            layers.Add(new OutputLayer(o.Info.Bounds, o.Sdr));
        }
        BgraImage image = Compositor.Compose(layers, region);
        // Tonemapped from the frame straight into the composite: no crop-sized image to allocate and then copy.
        foreach ((CapturedOutput o, IntRect local, IntRect hit, float e) in retonemapped)
            grabber.TonemapInto(o.Half!, local, o.Info, e, image, hit.Left - region.Left, hit.Top - region.Top);
        if (freeform != null) FreeformMask.Apply(image, region, freeform);
        return new CaptureResult { Image = image, Region = region, AnyHdr = anyHdr, Crops = crops, Doc = doc, Exposure = exposure, Freeform = freeform };
    }

    /// <summary>The exposure an HDR snip of <paramref name="rect"/> (frame-relative) is tonemapped with before the user's
    /// multiplier: measured once here, then carried by <see cref="HalfCrop.BaseExposure"/>.</summary>
    public static float BaseExposure(HalfImage frame, IntRect rect, OutputInfo output, SnipSettings settings)
        => settings.AutoExposure ? AutoExposure.Compute(frame, rect, settings.SdrWhiteNits ?? output.SdrWhiteNits, settings.Exposure) : settings.Exposure;
}
