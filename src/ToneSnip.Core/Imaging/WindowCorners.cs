using ToneSnip.Core.Color;
using ToneSnip.Core.Geometry;

namespace ToneSnip.Core.Imaging;

/// <summary>
/// The transparency a window capture carries at the window's rounded corners, kept so an active-window snip has
/// transparent corners instead of the desktop behind them (or black).
/// <para>Only the four corner squares are read, and only a corner whose alpha is believable is taken as it is: the
/// capture's alpha channel is not trustworthy over the rest of a window (a GDI-drawn window can leave it at zero where
/// DWM shows it opaque), so everywhere else the snip stays opaque. A corner is believed when its innermost pixel, which
/// lies inside the rounded outline whatever the radius, is fully opaque. A square window (maximised, snapped, or one that
/// opted out of rounding) has opaque corners and nothing changes.</para>
/// <para>A corner that is not believed but whose outermost pixel is empty (transparent black: the capture has nothing
/// there, as outside the rounded outline over a GDI client area) is rounded like a believed rounded corner of the same
/// window, mirrored; or, when no corner is believed at all, with the outline DWM draws at the window's radius. Left
/// opaque, those pixels would be black specks.</para>
/// <para>The capture's pixels are premultiplied; the snip's are straight alpha, so a partly transparent edge pixel is
/// divided back out when it is applied.</para>
/// </summary>
public sealed class WindowCorners
{
    /// <summary>DWM's corner radius at 96 DPI (DWMWCP_ROUND); the small one (DWMWCP_ROUNDSMALL) is 4.</summary>
    public const int Radius96 = 8;

    /// <summary>Corner alpha, top-left, top-right, bottom-left, bottom-right, <see cref="Size"/> squared each; null for
    /// a corner left opaque.</summary>
    private readonly byte[]?[] _alpha;

    public int Size { get; }

    private WindowCorners(int size, byte[]?[] alpha) { Size = size; _alpha = alpha; }

    /// <summary>The side of the square read at each corner for a window at <paramref name="dpi"/>: the scaled radius
    /// plus one pixel of antialiasing.</summary>
    public static int SizeFor(int dpi) => (int)Math.Ceiling(Radius96 * Math.Max(dpi, 96) / 96.0) + 1;

    /// <summary>The four corner squares of a <paramref name="width"/> x <paramref name="height"/> image, in the order
    /// <see cref="Read(int, int, int, Func{IntRect, Patch}, double)"/> asks for them. The side shrinks to half the image
    /// for a tiny window.</summary>
    public static IntRect[] Squares(int width, int height, int size)
    {
        int s = Math.Min(size, Math.Min(width, height) / 2);
        return new[]
        {
            new IntRect(0, 0, s, s), new IntRect(width - s, 0, s, s),
            new IntRect(0, height - s, s, s), new IntRect(width - s, height - s, s, s),
        };
    }

    /// <summary>One corner square as the capture holds it, row-major: each pixel's alpha, and whether it is empty (colour
    /// and alpha all zero); null <paramref name="Empty"/> when that is not known.</summary>
    public readonly record struct Patch(byte[] Alpha, bool[]? Empty);

    /// <summary><see cref="Read(int, int, int, Func{IntRect, Patch}, double)"/> from alpha alone: a corner that is not
    /// believed stays opaque.</summary>
    public static WindowCorners? Read(int width, int height, int size, Func<IntRect, byte[]> alphaOf)
        => Read(width, height, size, r => new Patch(alphaOf(r), null), 0);

    /// <summary>
    /// Reads the corners of a <paramref name="width"/> x <paramref name="height"/> capture. <paramref name="patchOf"/>
    /// returns one of <see cref="Squares"/>. <paramref name="radius"/> is the radius in pixels DWM rounds this window's
    /// corners with (zero when it does not), used only when no corner is believable. Null when there is nothing to
    /// apply.
    /// </summary>
    public static WindowCorners? Read(int width, int height, int size, Func<IntRect, Patch> patchOf, double radius)
    {
        IntRect[] squares = Squares(width, height, size);
        int s = squares[0].Width;
        if (s <= 0) return null;
        var alpha = new byte[]?[4];
        var believed = new bool[4];
        var empty = new bool[4];
        for (int c = 0; c < 4; c++)
        {
            (byte[] a, bool[]? e) = patchOf(squares[c]);
            if (a.Length != s * s || (e != null && e.Length != s * s)) throw new ArgumentException($"corner {c}: {a.Length} alpha values for a {s}x{s} square");
            // The pixel in the window's own corner.
            empty[c] = e != null && e[(Top(c) ? 0 : s - 1) * s + (Left(c) ? 0 : s - 1)];
            // The pixel diagonally farthest from the window's own corner.
            int ix = Left(c) ? s - 1 : 0, iy = Top(c) ? s - 1 : 0;
            if (a[iy * s + ix] != 255) continue;
            believed[c] = true;
            if (Array.TrueForAll(a, v => v == 255)) continue;
            alpha[c] = a;
        }
        // A believed rounded corner is the best model for the others: DWM rounds all four alike.
        int model = Array.FindIndex(alpha, a => a != null);
        bool anyBelieved = Array.IndexOf(believed, true) >= 0;
        for (int c = 0; c < 4; c++)
        {
            if (believed[c] || !empty[c]) continue;
            if (model >= 0) alpha[c] = Mirror(alpha[model]!, s, model, c);
            // Square believed corners say the window is not rounded now (snapped, say), whatever its preference.
            else if (!anyBelieved && radius > 0) alpha[c] = Rounded(s, radius, c);
        }
        return Array.Exists(alpha, a => a != null) ? new WindowCorners(s, alpha) : null;
    }

    private static bool Left(int corner) => corner is 0 or 2;
    private static bool Top(int corner) => corner is 0 or 1;

    /// <summary>Corner <paramref name="from"/>'s square flipped to sit in corner <paramref name="to"/>.</summary>
    private static byte[] Mirror(byte[] a, int s, int from, int to)
    {
        var m = new byte[s * s];
        bool flipX = Left(from) != Left(to), flipY = Top(from) != Top(to);
        for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
                m[y * s + x] = a[(flipY ? s - 1 - y : y) * s + (flipX ? s - 1 - x : x)];
        return m;
    }

    /// <summary>A quarter circle of <paramref name="radius"/> pixels in corner <paramref name="corner"/> of an
    /// <paramref name="s"/>-pixel square, as DWM draws it: transparent outside, opaque inside, and a pixel the outline
    /// crosses covered by how far inside it its centre is.</summary>
    public static byte[] Rounded(int s, double radius, int corner)
    {
        var a = new byte[s * s];
        for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                // Pixel centres, measured from the window's own corner.
                double px = (Left(corner) ? x : s - 1 - x) + 0.5, py = (Top(corner) ? y : s - 1 - y) + 0.5;
                if (px >= radius || py >= radius) { a[y * s + x] = 255; continue; }
                double d = Math.Sqrt((radius - px) * (radius - px) + (radius - py) * (radius - py));
                a[y * s + x] = (byte)Math.Round(Math.Clamp(radius - d + 0.5, 0, 1) * 255);
            }
        return a;
    }

    /// <summary>Puts the corners' transparency on <paramref name="image"/>, which is the captured window at the size the
    /// corners were read at: fully transparent pixels become transparent black, as outside a freeform lasso. Partly
    /// transparent ones are un-premultiplied when the image was tonemapped from the capture; with
    /// <paramref name="straight"/> it was tonemapped from crops <see cref="Unpremultiply"/> already divided, and only
    /// the alpha is set.</summary>
    public void Apply(BgraImage image, bool straight = false)
    {
        IntRect[] squares = Squares(image.Width, image.Height, Size);
        byte[] d = image.Data;
        for (int c = 0; c < 4; c++)
        {
            if (_alpha[c] is not { } a) continue;
            IntRect sq = squares[c];
            if (sq.Width != Size) throw new ArgumentException($"the image is {image.Width}x{image.Height}, too small for the corners read");
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    byte alpha = a[y * Size + x];
                    if (alpha == 255) continue;
                    int i = ((sq.Top + y) * image.Width + sq.Left + x) * 4;
                    if (alpha == 0) { d[i] = d[i + 1] = d[i + 2] = d[i + 3] = 0; continue; }
                    if (!straight)
                        for (int k = 0; k < 3; k++) d[i + k] = (byte)Math.Min(255, (d[i + k] * 255 + alpha / 2) / alpha);
                    d[i + 3] = alpha;
                }
        }
    }

    /// <summary>
    /// Divides the corners' alpha out of an HDR crop of the window (the same size as the snip), whose colour is
    /// premultiplied as captured. The HDR file takes its colour from the crop and its alpha from the snip, which is
    /// straight, so a premultiplied edge pixel would be darkened twice; and the editor's exposure pass tonemaps this
    /// colour. A fully transparent pixel becomes black. The crop's own alpha is left alone: nothing reads it.
    /// </summary>
    public void Unpremultiply(HalfImage image)
    {
        IntRect[] squares = Squares(image.Width, image.Height, Size);
        ushort[] d = image.Data;
        for (int c = 0; c < 4; c++)
        {
            if (_alpha[c] is not { } a) continue;
            IntRect sq = squares[c];
            if (sq.Width != Size) throw new ArgumentException($"the image is {image.Width}x{image.Height}, too small for the corners read");
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    byte alpha = a[y * Size + x];
                    if (alpha == 255) continue;
                    int i = ((sq.Top + y) * image.Width + sq.Left + x) * 4;
                    float scale = alpha == 0 ? 0f : 255f / alpha;
                    for (int k = 0; k < 3; k++) d[i + k] = Transfer.FloatToHalf(Transfer.HalfToFloat(d[i + k]) * scale);
                }
        }
    }
}
