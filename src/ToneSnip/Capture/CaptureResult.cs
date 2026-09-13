using ToneSnip.Core.Annotate;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Config;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using ToneSnip.Core.Tonemap;

namespace ToneSnip.App.Capture;

public sealed record HalfCrop(IntRect Bounds, HalfImage Image, OutputInfo Output);

/// <summary>The finished snip: BGRA8 image plus the half-float crops the viewer/editor may re-tonemap later.</summary>
public sealed class CaptureResult
{
    private BgraImage? _image;
    private byte[]? _png;

    /// <summary>The snip as BGRA8. After <see cref="Compact"/> it is decoded from PNG on demand.</summary>
    public required BgraImage Image
    {
        get { _image ??= Decode!(_png!); return _image; }
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

    /// <summary>Keeps only a PNG of the rendered image and drops the float crops and document, so shapes are no
    /// longer separately editable. The `edit` flow does not compact until the editor closes.</summary>
    public void Compact(byte[]? png = null)
    {
        if (Decode == null) return;
        // A newer render always replaces the cached PNG (for example after an editor save on a compacted result).
        byte[]? fresh = Output != null ? png ?? Encode?.Invoke(Output) : _image != null ? png ?? Encode?.Invoke(_image) : null;
        if (fresh != null) _png = fresh;
        if (_png == null) return;
        // A cropped render is no longer in the region's frame, and its lasso is already baked into its pixels.
        if (Output != null && (Output.Width != Region.Width || Output.Height != Region.Height)) Freeform = null;
        _image = null;
        Crops.Clear();
        Doc = null;
        Output = null;
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

    public static CaptureResult Build(List<CapturedOutput> outputs, IntRect region, IReadOnlyList<(int X, int Y)>? freeform, FrameGrabber grabber, SnipSettings settings, AnnotationDoc? doc = null, float exposure = 1f, bool retonemap = false)
    {
        var layers = new List<OutputLayer>();
        var crops = new List<HalfCrop>();
        bool anyHdr = false;
        foreach (CapturedOutput o in outputs)
        {
            IntRect hit = o.Info.Bounds.Intersect(region);
            if (hit.IsEmpty) continue;
            if (o.Half != null)
            {
                anyHdr = true;
                HalfImage crop = o.Half.Crop(hit.Offset(-o.Info.Left, -o.Info.Top));
                crops.Add(new HalfCrop(hit, crop, o.Info));
                float baseExposure = settings.AutoExposure ? AutoExposure.Compute(crop, settings.SdrWhiteNits ?? o.Info.SdrWhiteNits, settings.Exposure) : settings.Exposure;
                if (settings.AutoExposure || exposure != 1f || retonemap) { layers.Add(new OutputLayer(hit, grabber.Tonemap(crop, o.Info, baseExposure * exposure))); continue; }
            }
            layers.Add(new OutputLayer(o.Info.Bounds, o.Sdr));
        }
        BgraImage image = Compositor.Compose(layers, region);
        if (freeform != null) FreeformMask.Apply(image, region, freeform);
        return new CaptureResult { Image = image, Region = region, AnyHdr = anyHdr, Crops = crops, Doc = doc, Exposure = exposure, Freeform = freeform };
    }
}
