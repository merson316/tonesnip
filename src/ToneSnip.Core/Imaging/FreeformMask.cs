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
    /// Both overloads go through <see cref="FreeformSpans"/>, so their edge rules, and the editor's, which cuts one dirty
    /// rectangle at a time with the same spans, cannot drift apart. <paramref name="bpp"/> is 4 for BGRA, 1 for a mask.
    /// </summary>
    private static void Apply(byte[] data, int width, int height, int bpp, IntRect imageBounds, IReadOnlyList<(int X, int Y)> polygon)
        => FreeformSpans.Build(width, height, imageBounds, polygon).ClearOutside(data, bpp, new IntRect(0, 0, width, height));
}

/// <summary>
/// A lasso rasterised once for an image: per row, the column spans inside the polygon (even-odd crossings at pixel
/// centres). Clearing the outside of any rectangle then needs no polygon walk and no sort, so the editor can cut just
/// the area a paint changed. A few integers a row rather than a byte per pixel.
/// </summary>
public sealed class FreeformSpans
{
    /// <summary>Where each row's spans start in <see cref="_spans"/>; row r's run to <c>_rowStart[r + 1]</c>. Null
    /// keeps everything: a "polygon" of fewer than three points cuts nothing.</summary>
    private readonly int[]? _rowStart;
    /// <summary>Left-inclusive, right-exclusive column pairs, sorted and disjoint within each row.</summary>
    private readonly int[] _spans;

    public int Width { get; }
    public int Height { get; }

    private FreeformSpans(int width, int height, int[]? rowStart, int[] spans)
    {
        Width = width; Height = height; _rowStart = rowStart; _spans = spans;
    }

    /// <summary>Rasterises <paramref name="polygon"/> for an image of <paramref name="width"/> x
    /// <paramref name="height"/> whose top-left pixel is at <paramref name="imageBounds"/>' top-left (the polygon is in
    /// that frame).</summary>
    public static FreeformSpans Build(int width, int height, IntRect imageBounds, IReadOnlyList<(int X, int Y)> polygon)
    {
        if (polygon.Count < 3) return new FreeformSpans(width, height, null, Array.Empty<int>());
        int n = polygon.Count;
        var rowStart = new int[height + 1];
        var spans = new List<int>(height * 2);
        var xs = new List<float>(n);
        for (int row = 0; row < height; row++)
        {
            rowStart[row] = spans.Count;
            float py = imageBounds.Top + row + 0.5f;
            xs.Clear();
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                (int xi, int yi) = polygon[i]; (int xj, int yj) = polygon[j];
                if ((yi + 0.5f > py) != (yj + 0.5f > py))
                    xs.Add(xi + (py - (yi + 0.5f)) * (xj - xi) / (float)(yj - yi));
            }
            xs.Sort();
            int first = spans.Count;
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int inL = Math.Clamp((int)MathF.Ceiling(xs[k] - 0.5f) - imageBounds.Left, 0, width);
                int inR = Math.Clamp((int)MathF.Floor(xs[k + 1] - 0.5f) + 1 - imageBounds.Left, 0, width);
                if (inR <= inL) continue;
                // The lefts rise with the sorted crossings, so a span either extends the last one or starts after it.
                if (spans.Count > first && inL <= spans[^1]) spans[^1] = Math.Max(spans[^1], inR);
                else { spans.Add(inL); spans.Add(inR); }
            }
        }
        rowStart[height] = spans.Count;
        return new FreeformSpans(width, height, rowStart, spans.ToArray());
    }

    /// <summary>Clears to zero every pixel of <paramref name="area"/> (image pixels, clipped to the image) that lies
    /// outside the polygon, in an image of this size with <paramref name="bpp"/> bytes a pixel. Pixels outside
    /// <paramref name="area"/> are not touched.</summary>
    public void ClearOutside(byte[] data, int bpp, IntRect area)
    {
        if (_rowStart == null) return;
        area = area.Intersect(new IntRect(0, 0, Width, Height));
        if (area.IsEmpty) return;
        for (int row = area.Top; row < area.Bottom; row++)
        {
            int rowOffset = row * Width;
            int col = area.Left;
            for (int k = _rowStart[row]; k < _rowStart[row + 1] && col < area.Right; k += 2)
            {
                int inL = Math.Min(_spans[k], area.Right);
                if (inL > col) data.AsSpan((rowOffset + col) * bpp, (inL - col) * bpp).Clear();
                col = Math.Max(col, _spans[k + 1]);
            }
            if (col < area.Right) data.AsSpan((rowOffset + col) * bpp, (area.Right - col) * bpp).Clear();
        }
    }

    /// <summary><see cref="ClearOutside(byte[], int, IntRect)"/> on a BGRA image of this size.</summary>
    public void ClearOutside(BgraImage image, IntRect area)
    {
        if (image.Width != Width || image.Height != Height) throw new ArgumentException($"image {image.Width}x{image.Height} is not the lasso's {Width}x{Height}", nameof(image));
        ClearOutside(image.Data, 4, area);
    }
}
