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

    public static HalfImage FromFloat(FloatImage img)
    {
        var h = new HalfImage(img.Width, img.Height);
        for (int i = 0, n = img.Width * img.Height; i < n; i++)
        {
            h.Data[i * 4] = BitConverter.HalfToUInt16Bits((Half)img.Data[i * 3]);
            h.Data[i * 4 + 1] = BitConverter.HalfToUInt16Bits((Half)img.Data[i * 3 + 1]);
            h.Data[i * 4 + 2] = BitConverter.HalfToUInt16Bits((Half)img.Data[i * 3 + 2]);
            h.Data[i * 4 + 3] = 0x3C00;
        }
        return h;
    }

    public FloatImage ToFloat()
    {
        var f = new FloatImage(Width, Height);
        for (int i = 0, n = Width * Height; i < n; i++)
        {
            f.Data[i * 3] = Transfer.HalfToFloat(Data[i * 4]);
            f.Data[i * 3 + 1] = Transfer.HalfToFloat(Data[i * 4 + 1]);
            f.Data[i * 3 + 2] = Transfer.HalfToFloat(Data[i * 4 + 2]);
        }
        return f;
    }

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

    /// <summary>Rotates by quarterTurns * 90° clockwise. Source (x,y) lands at (H-1-y, x) for one turn.</summary>
    public HalfImage RotateClockwise(int quarterTurns)
    {
        int turns = ((quarterTurns % 4) + 4) % 4;
        if (turns == 0) return this;
        int nw = turns % 2 == 0 ? Width : Height, nh = turns % 2 == 0 ? Height : Width;
        var into = new HalfImage(nw, nh);
        RotateClockwiseInto(turns, into);
        return into;
    }

    /// <summary>The same rotation into a caller-owned buffer, so a pooled frame can be turned without allocating.
    /// <paramref name="dst"/> must already be the rotated size; a zero-turn call is a straight copy.</summary>
    public void RotateClockwiseInto(int quarterTurns, HalfImage dst)
    {
        int turns = ((quarterTurns % 4) + 4) % 4;
        int nw = turns % 2 == 0 ? Width : Height, nh = turns % 2 == 0 ? Height : Width;
        if (dst.Width != nw || dst.Height != nh) throw new ArgumentException($"target {dst.Width}x{dst.Height} is not {nw}x{nh}", nameof(dst));
        if (turns == 0) { Array.Copy(Data, dst.Data, Data.Length); return; }
        RotateCore(turns, dst);
    }

    private void RotateCore(int turns, HalfImage dst)
    {
        int w = Width, h = Height, nw = dst.Width;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int nx, ny;
                switch (turns) { case 1: nx = h - 1 - y; ny = x; break; case 2: nx = w - 1 - x; ny = h - 1 - y; break; default: nx = y; ny = w - 1 - x; break; }
                Array.Copy(Data, (y * w + x) * 4, dst.Data, (ny * nw + nx) * 4, 4);
            }
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
