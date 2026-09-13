using ToneSnip.Core.Geometry;

namespace ToneSnip.Core.Imaging;

/// <summary>One output's SDR image placed at its desktop bounds.</summary>
public sealed record OutputLayer(IntRect Bounds, BgraImage Image);

public static class Compositor
{
    /// <summary>Copies the parts of every layer that fall inside <paramref name="region"/> into one image. Uncovered pixels stay transparent black.</summary>
    public static BgraImage Compose(IReadOnlyList<OutputLayer> layers, IntRect region)
    {
        if (region.IsEmpty) throw new ArgumentException("empty region", nameof(region));
        BgraImage result = BgraImage.Blank(region.Width, region.Height);
        foreach (OutputLayer layer in layers)
        {
            IntRect hit = layer.Bounds.Intersect(region);
            if (hit.IsEmpty) continue;
            if (layer.Image.Width != layer.Bounds.Width || layer.Image.Height != layer.Bounds.Height)
                throw new ArgumentException($"layer image {layer.Image.Width}x{layer.Image.Height} does not match bounds {layer.Bounds}");
            int srcX = hit.Left - layer.Bounds.Left, srcY = hit.Top - layer.Bounds.Top;
            int dstX = hit.Left - region.Left, dstY = hit.Top - region.Top;
            for (int y = 0; y < hit.Height; y++)
                Buffer.BlockCopy(layer.Image.Data, ((srcY + y) * layer.Image.Width + srcX) * 4, result.Data, ((dstY + y) * result.Width + dstX) * 4, hit.Width * 4);
        }
        return result;
    }
}
