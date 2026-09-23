namespace ToneSnip.Core.Imaging;

/// <summary>Composites a straight-alpha image onto an opaque background, for formats with no alpha.</summary>
public static class Flatten
{
    /// <summary>Whether every pixel is fully opaque, so flattening would change nothing.</summary>
    public static bool IsOpaque(BgraImage img)
    {
        byte[] d = img.Data;
        for (int i = 3; i < d.Length; i += 4) if (d[i] != 255) return false;
        return true;
    }

    /// <summary>A copy on white, matching the SDR JPEG encoder, so the SDR JPEG and the UltraHDR base of the same snip
    /// agree outside a freeform lasso.</summary>
    public static BgraImage OnWhite(BgraImage img)
    {
        var o = new BgraImage(img.Width, img.Height, (byte[])img.Data.Clone());
        byte[] d = o.Data;
        for (int i = 0; i < d.Length; i += 4)
        {
            int a = d[i + 3];
            if (a == 255) continue;
            d[i] = (byte)((d[i] * a + 255 * (255 - a)) / 255);
            d[i + 1] = (byte)((d[i + 1] * a + 255 * (255 - a)) / 255);
            d[i + 2] = (byte)((d[i + 2] * a + 255 * (255 - a)) / 255);
            d[i + 3] = 255;
        }
        return o;
    }
}
