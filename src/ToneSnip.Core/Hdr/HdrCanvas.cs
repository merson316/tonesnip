using ToneSnip.Core.Color;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;

namespace ToneSnip.Core.Hdr;

/// <summary>
/// The HDR file's pixels, assembled in linear light: the SDR composite lifted to the snip's reference white, the captured
/// HDR crops on top (× exposure), the lasso in alpha. Shapes are composited afterwards with <see cref="CompositeLayer"/>.
/// </summary>
public static class HdrCanvas
{
    public static float ReferenceScale(float referenceWhiteNits) => Math.Max(referenceWhiteNits, 1f) / 80f;

    /// <summary>
    /// <paramref name="region"/>, <paramref name="viewport"/>, layer bounds and <paramref name="lasso"/> are in
    /// virtual-desktop coordinates; <paramref name="sdr"/> is region-sized and region-relative, as
    /// <c>Compositor.Compose</c> produces it. The canvas covers <paramref name="viewport"/> ∩ <paramref name="region"/>,
    /// or the whole region when <paramref name="viewport"/> is empty.
    /// </summary>
    public static HalfImage Build(BgraImage sdr, IntRect region, IReadOnlyList<HdrLayer> layers, IntRect viewport, float exposure, IReadOnlyList<(int X, int Y)>? lasso, float referenceWhiteNits)
    {
        if (sdr.Width != region.Width || sdr.Height != region.Height) throw new ArgumentException("sdr composite must be region-sized");
        IntRect vp = viewport.IsEmpty ? region : viewport.Intersect(region);
        if (vp.IsEmpty) throw new ArgumentException("viewport does not intersect the region");
        // As Compositor.Compose does: a layer whose image is not its bounds would be read past its end below.
        foreach (HdrLayer layer in layers)
            if (layer.Image.Width != layer.Bounds.Width || layer.Image.Height != layer.Bounds.Height)
                throw new ArgumentException($"layer image {layer.Image.Width}x{layer.Image.Height} does not match its bounds {layer.Bounds}");
        var canvas = new HalfImage(vp.Width, vp.Height);
        float scale = ReferenceScale(referenceWhiteNits);
        ushort[] d = canvas.Data;
        // Base: every pixel from the SDR composite, lifted.
        Parallel.For(0, vp.Height, y =>
        {
            int srcRow = (vp.Top - region.Top + y) * sdr.Width + (vp.Left - region.Left);
            for (int x = 0; x < vp.Width; x++)
            {
                int s = (srcRow + x) * 4, t = (y * vp.Width + x) * 4;
                ColorMath.LiftSrgb(sdr.Data[s], sdr.Data[s + 1], sdr.Data[s + 2], scale, out float r, out float g, out float b);
                d[t] = Transfer.FloatToHalf(r); d[t + 1] = Transfer.FloatToHalf(g); d[t + 2] = Transfer.FloatToHalf(b);
                d[t + 3] = Transfer.FloatToHalf(sdr.Data[s + 3] / 255f);
            }
        });
        // HDR crops overwrite their area; exposure applies to them only (the SDR file does the same).
        foreach (HdrLayer layer in layers)
        {
            IntRect hit = layer.Bounds.Intersect(vp);
            if (hit.IsEmpty) continue;
            ushort[] src = layer.Image.Data;
            Parallel.For(hit.Top, hit.Bottom, y =>
            {
                for (int x = hit.Left; x < hit.Right; x++)
                {
                    int s = ((y - layer.Bounds.Top) * layer.Image.Width + (x - layer.Bounds.Left)) * 4, t = ((y - vp.Top) * vp.Width + (x - vp.Left)) * 4;
                    float e = exposure * layer.Exposure;
                    if (e == 1f) { d[t] = src[s]; d[t + 1] = src[s + 1]; d[t + 2] = src[s + 2]; }
                    else
                    {
                        d[t] = Transfer.FloatToHalf(Finite(Transfer.HalfToFloat(src[s]) * e));
                        d[t + 1] = Transfer.FloatToHalf(Finite(Transfer.HalfToFloat(src[s + 1]) * e));
                        d[t + 2] = Transfer.FloatToHalf(Finite(Transfer.HalfToFloat(src[s + 2]) * e));
                    }
                    // alpha stays what the SDR composite had (the lasso, if any, is applied below)
                }
            });
        }
        if (lasso != null) ApplyLasso(canvas, vp, lasso);
        return canvas;
    }

    /// <summary>The largest finite half float. Exposure can push highlights past it, and an overflowing half becomes
    /// infinity, which would corrupt the gain map.</summary>
    public const float HalfMax = 65504f;

    /// <summary>A value a half float holds: NaN becomes 0, anything beyond ±<see cref="HalfMax"/> is clamped.</summary>
    public static float Finite(float v) => float.IsNaN(v) ? 0f : Math.Clamp(v, -HalfMax, HalfMax);

    /// <summary>Clears every pixel outside the polygon (virtual-desktop frame, same as <paramref name="bounds"/> and every
    /// <see cref="HdrLayer.Bounds"/>) to transparent black. Same rule as FreeformMask.</summary>
    public static void ApplyLasso(HalfImage img, IntRect bounds, IReadOnlyList<(int X, int Y)> polygon)
    {
        if (polygon.Count < 3) return;
        // FreeformMask's one-byte-per-pixel overload gives the same edge decisions without a full BGRA image.
        var mask = new byte[img.Width * img.Height];
        Array.Fill(mask, (byte)255);
        FreeformMask.Apply(mask, img.Width, img.Height, bounds, polygon);
        ushort[] d = img.Data;
        for (int i = 0, n = img.Width * img.Height; i < n; i++)
            if (mask[i] == 0) { d[i * 4] = 0; d[i * 4 + 1] = 0; d[i * 4 + 2] = 0; d[i * 4 + 3] = 0; }
    }

    /// <summary>A copy of the canvas on an opaque white at <paramref name="referenceWhiteNits"/> — the HDR side of
    /// <see cref="Flatten.OnWhite"/> — for the UltraHDR JPEG, which has no alpha: its SDR base and its gain map then
    /// agree that a transparent area is white, with no gain.</summary>
    public static HalfImage FlattenOnWhite(HalfImage canvas, float referenceWhiteNits)
    {
        var o = new HalfImage(canvas.Width, canvas.Height, (ushort[])canvas.Data.Clone());
        float white = ReferenceScale(referenceWhiteNits);
        ushort[] d = o.Data;
        ushort opaque = Transfer.FloatToHalf(1f);
        for (int i = 0; i < d.Length; i += 4)
        {
            float a = Math.Clamp(Transfer.HalfToFloat(d[i + 3]), 0f, 1f);
            if (a >= 1f) continue;
            for (int c = 0; c < 3; c++) d[i + c] = Transfer.FloatToHalf(Finite(Transfer.HalfToFloat(d[i + c]) * a + white * (1f - a)));
            d[i + 3] = opaque;
        }
        return o;
    }

    /// <summary>Alpha-blends a straight-alpha sRGB layer (what the SDR rasterizer drew) over the straight-alpha canvas in
    /// linear light: "over", with the colour divided by the result's alpha so a mark over a transparent pixel is not
    /// darkened by its own alpha.</summary>
    public static void CompositeLayer(HalfImage canvas, BgraImage layer, float referenceWhiteNits)
    {
        if (layer.Width != canvas.Width || layer.Height != canvas.Height) throw new ArgumentException("layer must match the canvas");
        float scale = ReferenceScale(referenceWhiteNits);
        ushort[] d = canvas.Data; byte[] l = layer.Data;
        Parallel.For(0, canvas.Height, y =>
        {
            for (int x = 0; x < canvas.Width; x++)
            {
                int i = (y * canvas.Width + x) * 4;
                byte la = l[i + 3];
                if (la == 0) continue;
                float a = la / 255f;
                ColorMath.LiftSrgb(l[i], l[i + 1], l[i + 2], scale, out float lr, out float lg, out float lb);
                float cr = Transfer.HalfToFloat(d[i]), cg = Transfer.HalfToFloat(d[i + 1]), cb = Transfer.HalfToFloat(d[i + 2]), ca = Transfer.HalfToFloat(d[i + 3]);
                float below = ca * (1f - a), outA = a + below;
                if (outA <= 0f) { d[i] = d[i + 1] = d[i + 2] = d[i + 3] = 0; continue; }
                d[i] = Transfer.FloatToHalf(Finite((lr * a + cr * below) / outA));
                d[i + 1] = Transfer.FloatToHalf(Finite((lg * a + cg * below) / outA));
                d[i + 2] = Transfer.FloatToHalf(Finite((lb * a + cb * below) / outA));
                d[i + 3] = Transfer.FloatToHalf(outA);
            }
        });
    }
}
