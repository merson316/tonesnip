using ToneSnip.App.Annotate;
using ToneSnip.App.Capture;
using ToneSnip.Core.Annotate;
using ToneSnip.Core.Config;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Hdr;
using ToneSnip.Core.Imaging;
using ToneSnip.Windows.Imaging;

namespace ToneSnip.App.Output;

/// <summary>The HDR sidecar: result + document → linear-light canvas → one of the three encoders → file.</summary>
public static class HdrOutput
{
    public static string Extension(string file) => file switch { "jxr" => "jxr", "png" => "hdr.png", "jpeg" => "hdr.jpg", _ => throw new ArgumentException(file) };

    public static string? FormatOf(string path)
    {
        if (path.EndsWith(".jxr", StringComparison.OrdinalIgnoreCase)) return "jxr";
        if (path.EndsWith(".hdr.png", StringComparison.OrdinalIgnoreCase)) return "png";
        if (path.EndsWith(".hdr.jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".hdr.jpeg", StringComparison.OrdinalIgnoreCase)) return "jpeg";
        return null;
    }

    /// <summary>Whether <paramref name="hdrPath"/> is the sidecar <see cref="PathFor"/> derives for <paramref name="sdrPath"/>,
    /// as opposed to one belonging to some other snip's SDR file.</summary>
    public static bool OwnsSidecar(string? sdrPath, string? hdrPath)
    {
        if (sdrPath == null || hdrPath == null) return false;
        string? format = FormatOf(hdrPath);
        return format != null && string.Equals(PathFor(sdrPath, format), hdrPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>`Snip ... .png` → `Snip ... .jxr` / `.hdr.png` / `.hdr.jpg` in the same folder.</summary>
    public static string PathFor(string sdrPath, string file)
    {
        string dir = Path.GetDirectoryName(sdrPath) ?? "", stem = Path.GetFileNameWithoutExtension(sdrPath);
        return Path.Combine(dir, stem + "." + Extension(file));
    }

    // The SDR white override wins, matching SnipSettings.ToTonemapParams, so the sidecar and the SDR composite agree.
    public static float ReferenceWhite(CaptureResult r, SnipSettings s)
        => s.SdrWhiteNits ?? (r.Crops.Count > 0 ? r.Crops.Max(c => c.Output.SdrWhiteNits) : 80f);

    public static (HalfImage Canvas, IntRect Viewport, float ReferenceWhiteNits) BuildCanvas(CaptureResult r, SnipSettings s, uint accent)
    {
        AnnotationDoc? doc = r.Doc;
        IntRect viewport = doc != null && !doc.Crop.IsEmpty ? doc.Crop.Intersect(r.Region) : r.Region;
        float white = ReferenceWhite(r, s);
        // The exposure each crop was tonemapped with when the snip was built, so both files match even if the exposure
        // settings change before an editor save.
        var layers = r.Crops.Select(c => new HdrLayer(c.Bounds, c.Image, c.BaseExposure)).ToList();
        // The lasso mask is always applied first: the HDR crops still hold colour outside the lasso, which would
        // otherwise inflate the gain-map JPEG's range (it has no alpha).
        HalfImage canvas = HdrCanvas.Build(r.Image, r.Region, layers, viewport, r.Exposure, r.Freeform, white);
        if (doc != null)
        {
            foreach (Shape sh in doc.Shapes) if (sh is RedactShape red) HdrRedaction.Apply(red, canvas, viewport, white);
            if (doc.Shapes.Any(x => !x.IsRedaction))
            {
                var layer = BgraImage.Blank(viewport.Width, viewport.Height);   // transparent: base and target are the same buffer
                ShapeRenderer.Render(doc, layer, viewport, layer, null, accent, null, redactions: false);
                HdrCanvas.CompositeLayer(canvas, layer, white);
            }
        }
        // Clips annotations to the lasso too when the setting asks for it.
        if (r.Freeform != null && CaptureResult.ClipToLassoSetting()) HdrCanvas.ApplyLasso(canvas, viewport, r.Freeform);
        return (canvas, viewport, white);
    }

    /// <summary>The sidecar's bytes. <paramref name="canvas"/> belongs to this call: the JPEG branch flattens it in
    /// place.</summary>
    public static byte[] Encode(HalfImage canvas, BgraImage sdrRendered, string file, SnipSettings s, float referenceWhiteNits)
    {
        switch (file)
        {
            case "jxr": return JxrEncoder.Encode(canvas, s.Hdr.JxrLossless, s.Hdr.JxrQuality / 100f);
            case "png": return HdrPngWriter.Encode(canvas);
            case "jpeg":
            {
                if (sdrRendered.Width != canvas.Width || sdrRendered.Height != canvas.Height) throw new ArgumentException("SDR render must match the canvas");
                // JPEG has no alpha: both halves are flattened on white, matching the SDR JPEG. The SDR render is
                // shared with the rest of the output, so it is copied only when it has transparency to flatten (a
                // freeform snip); the canvas is this file's own and is flattened in place.
                BgraImage flat = Flatten.IsOpaque(sdrRendered) ? sdrRendered : Flatten.OnWhite(sdrRendered);
                HdrCanvas.FlattenOnWhite(canvas, referenceWhiteNits);
                GainMapResult gm = GainMap.Compute(canvas, flat, referenceWhiteNits);
                byte[] baseJpeg = Bitmaps.EncodeJpeg(flat, s.JpegQuality);
                byte[] gainJpeg = Bitmaps.EncodeGrayJpeg(gm.Gray, gm.Width, gm.Height, 85);
                return UltraHdrContainer.Assemble(baseJpeg, gainJpeg, new UltraHdrMeta(gm.Min, gm.Max));
            }
            default: throw new ArgumentException(file);
        }
    }

    public static bool Write(CaptureResult r, BgraImage sdrRendered, string sdrPath, string file, SnipSettings s, uint accent, ILog log)
    {
        // PathFor throws for an unknown format; a failed sidecar must never affect the SDR save or what follows it.
        try { return WriteTo(r, sdrRendered, PathFor(sdrPath, file), file, s, accent, log); }
        catch (Exception e) { log.Error($"hdr {file}: {e.Message}"); return false; }
    }

    /// <summary>As <see cref="Write"/>, but <paramref name="path"/> is the sidecar's own final path (no derivation from an SDR path).</summary>
    public static bool WriteTo(CaptureResult r, BgraImage sdrRendered, string path, string file, SnipSettings s, uint accent, ILog log)
    {
        string tmp = path + ".tmp";
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            (HalfImage canvas, _, float white) = BuildCanvas(r, s, accent);
            long length;
            if (file == "png")
            {
                // The PNG writer streams band by band, so it writes straight into the file: in memory the whole file
                // would sit in a growing MemoryStream and then again in its ToArray copy.
                using var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                HdrPngWriter.Write(canvas, stream);
                length = stream.Length;
            }
            else
            {
                byte[] bytes = Encode(canvas, sdrRendered, file, s, white);
                File.WriteAllBytes(tmp, bytes);
                length = bytes.Length;
            }
            File.Move(tmp, path, overwrite: true);   // atomic on one volume, so a failed write leaves no truncated file
            r.HdrPath = path;
            log.Info($"hdr saved {path} ({length / 1024} KB, {file}, {sw.ElapsedMilliseconds} ms)");
            return true;
        }
        catch (Exception e) { log.Error($"hdr {file}: {e.Message}"); try { File.Delete(tmp); } catch { } return false; }
    }
}
