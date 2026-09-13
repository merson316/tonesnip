using ToneSnip.Core.Geometry;

namespace ToneSnip.Core.Imaging;

public static class FreeformMask
{
    /// <summary>The smallest rectangle covering every point (right and bottom exclusive).</summary>
    /// <remarks>One pass, no delegates: the overlay asks for this on every mouse move while a lasso is being drawn.</remarks>
    public static IntRect BoundingBox(IReadOnlyList<(int X, int Y)> points)
    {
        if (points.Count == 0) return IntRect.Empty;
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        for (int i = 0; i < points.Count; i++)
        {
            (int x, int y) = points[i];
            if (x < l) l = x;
            if (y < t) t = y;
            if (x > r) r = x;
            if (y > b) b = y;
        }
        return IntRect.FromLtrb(l, t, r + 1, b + 1);
    }

    /// <summary>Clears every pixel outside the closed polygon (desktop coordinates) to transparent black. Even-odd rule, pixel centers.</summary>
    public static void Apply(BgraImage image, IntRect imageBounds, IReadOnlyList<(int X, int Y)> polygon)
        => Apply(image.Data, image.Width, image.Height, 4, imageBounds, polygon);

    /// <summary>Same edge rule as the BGRA overload, on a one-byte-per-pixel coverage mask: zeroes every pixel outside
    /// the polygon and leaves the rest untouched (the caller pre-fills "inside", e.g. with 255).</summary>
    public static void Apply(byte[] mask, int width, int height, IntRect imageBounds, IReadOnlyList<(int X, int Y)> polygon)
        => Apply(mask, width, height, 1, imageBounds, polygon);

    /// <summary>
    /// The scanline fill shared by both overloads (so their edge rules cannot drift apart): even-odd crossings at pixel
    /// centres, clearing every pixel outside the spans. <paramref name="bpp"/> is 4 for BGRA, 1 for a mask.
    /// </summary>
    private static void Apply(byte[] data, int width, int height, int bpp, IntRect imageBounds, IReadOnlyList<(int X, int Y)> polygon)
    {
        if (polygon.Count < 3) return;
        int n = polygon.Count;
        var xs = new List<float>(n);
        for (int row = 0; row < height; row++)
        {
            float py = imageBounds.Top + row + 0.5f;
            xs.Clear();
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                (int xi, int yi) = polygon[i]; (int xj, int yj) = polygon[j];
                if ((yi + 0.5f > py) != (yj + 0.5f > py))
                    xs.Add(xi + (py - (yi + 0.5f)) * (xj - xi) / (float)(yj - yi));
            }
            xs.Sort();
            int rowStart = row * width * bpp;
            int col = 0;
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int inL = Math.Clamp((int)MathF.Ceiling(xs[k] - 0.5f) - imageBounds.Left, 0, width);
                int inR = Math.Clamp((int)MathF.Floor(xs[k + 1] - 0.5f) + 1 - imageBounds.Left, 0, width);
                for (; col < inL; col++) Clear(data, rowStart + col * bpp, bpp);
                col = Math.Max(col, inR);
            }
            for (; col < width; col++) Clear(data, rowStart + col * bpp, bpp);
        }
    }

    private static void Clear(byte[] d, int i, int bpp) { for (int k = 0; k < bpp; k++) d[i + k] = 0; }
}
