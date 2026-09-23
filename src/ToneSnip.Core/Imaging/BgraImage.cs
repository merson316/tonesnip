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
}
