using ToneSnip.Core.Color;
using ToneSnip.Core.Geometry;

namespace ToneSnip.Core.Imaging;

/// <summary>
/// Row-major RGBA half floats (linear scRGB, 1.0 = 80 nits), exactly as R16G16B16A16_FLOAT frames arrive, so a captured
/// frame is a row copy rather than a 12-byte-per-pixel float expansion. Alpha is carried but ignored.
/// </summary>
public sealed class HalfImage
{
    public int Width { get; }
    public int Height { get; }
    public ushort[] Data { get; }

    public HalfImage(int width, int height, ushort[] data)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (data.Length != (long)width * height * 4) throw new ArgumentException("data length must be width*height*4", nameof(data));
        Width = width; Height = height; Data = data;
    }

    public HalfImage(int width, int height) : this(width, height, new ushort[checked(width * height * 4)]) { }

    /// <summary>Linear RGB of one pixel.</summary>
    public (float R, float G, float B) Sample(int x, int y)
    {
        int i = (y * Width + x) * 4;
        return (Transfer.HalfToFloat(Data[i]), Transfer.HalfToFloat(Data[i + 1]), Transfer.HalfToFloat(Data[i + 2]));
    }

    public HalfImage Crop(IntRect r)
    {
        if (r.IsEmpty || r.Left < 0 || r.Top < 0 || r.Right > Width || r.Bottom > Height) throw new ArgumentOutOfRangeException(nameof(r), $"{r} is outside {Width}x{Height}");
        var dst = new HalfImage(r.Width, r.Height);
        for (int y = 0; y < r.Height; y++) Array.Copy(Data, ((r.Top + y) * Width + r.Left) * 4, dst.Data, y * r.Width * 4, r.Width * 4);
        return dst;
    }

    /// <summary>Every step-th pixel in both directions; used for previews and statistics.</summary>
    public HalfImage Downsample(int step)
    {
        int w = Math.Max(1, Width / step), h = Math.Max(1, Height / step);
        var dst = new HalfImage(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) Array.Copy(Data, ((y * step) * Width + x * step) * 4, dst.Data, (y * w + x) * 4, 4);
        return dst;
    }
}
