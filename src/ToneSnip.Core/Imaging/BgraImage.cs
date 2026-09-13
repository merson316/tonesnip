using ToneSnip.Core.Geometry;

namespace ToneSnip.Core.Imaging;

/// <summary>
/// Row-major 8-bit BGRA, straight (non-premultiplied) alpha. BGRA matches the clipboard DIB and GDI, so nothing
/// downstream has to swap channels.
/// </summary>
public sealed class BgraImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }

    public BgraImage(int width, int height, byte[] data)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        // In long: width*height*4 can overflow int and falsely match a smaller array.
        if (data.Length != (long)width * height * 4) throw new ArgumentException("data length must be width*height*4", nameof(data));
        Width = width; Height = height; Data = data;
    }

    public static BgraImage Blank(int width, int height) => new(width, height, new byte[checked(width * height * 4)]);

    public BgraImage Crop(IntRect r)
    {
        if (r.IsEmpty || r.Left < 0 || r.Top < 0 || r.Right > Width || r.Bottom > Height) throw new ArgumentOutOfRangeException(nameof(r), $"{r} is outside {Width}x{Height}");
        var dst = new byte[r.Width * r.Height * 4];
        for (int y = 0; y < r.Height; y++) Buffer.BlockCopy(Data, ((r.Top + y) * Width + r.Left) * 4, dst, y * r.Width * 4, r.Width * 4);
        return new BgraImage(r.Width, r.Height, dst);
    }

    /// <summary>Rotates by quarterTurns * 90° clockwise.</summary>
    public BgraImage RotateClockwise(int quarterTurns)
    {
        int turns = ((quarterTurns % 4) + 4) % 4;
        if (turns == 0) return this;
        int nw = turns % 2 == 0 ? Width : Height, nh = turns % 2 == 0 ? Height : Width;
        var into = new BgraImage(nw, nh, new byte[Data.Length]);
        RotateClockwiseInto(turns, into);
        return into;
    }

    /// <summary>The same rotation into a caller-owned buffer, so a pooled frame can be turned without allocating.
    /// <paramref name="dst"/> must already be the rotated size; a zero-turn call is a straight copy.</summary>
    public void RotateClockwiseInto(int quarterTurns, BgraImage dst)
    {
        int turns = ((quarterTurns % 4) + 4) % 4;
        int w = Width, h = Height, nw = turns % 2 == 0 ? w : h, nh = turns % 2 == 0 ? h : w;
        if (dst.Width != nw || dst.Height != nh) throw new ArgumentException($"target {dst.Width}x{dst.Height} is not {nw}x{nh}", nameof(dst));
        if (turns == 0) { Buffer.BlockCopy(Data, 0, dst.Data, 0, Data.Length); return; }
        byte[] into = dst.Data;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int nx, ny;
                switch (turns) { case 1: nx = h - 1 - y; ny = x; break; case 2: nx = w - 1 - x; ny = h - 1 - y; break; default: nx = y; ny = w - 1 - x; break; }
                Buffer.BlockCopy(Data, (y * w + x) * 4, into, (ny * nw + nx) * 4, 4);
            }
    }
}
