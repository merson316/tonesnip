namespace ToneSnip.Core.Imaging;

/// <summary>Row-major, interleaved RGB float image (no alpha).</summary>
public sealed class FloatImage
{
    public int Width { get; }
    public int Height { get; }
    public float[] Data { get; }

    public FloatImage(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        Width = width;
        Height = height;
        Data = new float[checked(width * height * 3)];
    }

    public FloatImage Crop(int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > Width || y + height > Height)
            throw new ArgumentOutOfRangeException(nameof(x), $"Crop ({x},{y},{width}x{height}) is outside the {Width}x{Height} image.");
        var result = new FloatImage(width, height);
        for (int row = 0; row < height; row++)
            Array.Copy(Data, ((y + row) * Width + x) * 3, result.Data, row * width * 3, width * 3);
        return result;
    }

    /// <summary>Rotates by quarterTurns * 90° clockwise. Source (x,y) lands at (H-1-y, x) for one turn.</summary>
    public FloatImage RotateClockwise(int quarterTurns)
    {
        int turns = ((quarterTurns % 4) + 4) % 4;
        if (turns == 0) return this;
        int w = Width, h = Height;
        int nw = turns % 2 == 0 ? w : h;
        int nh = turns % 2 == 0 ? h : w;
        var result = new FloatImage(nw, nh);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int nx, ny;
                switch (turns)
                {
                    case 1: nx = h - 1 - y; ny = x; break;
                    case 2: nx = w - 1 - x; ny = h - 1 - y; break;
                    default: nx = y; ny = w - 1 - x; break;
                }
                int s = (y * w + x) * 3, d = (ny * nw + nx) * 3;
                result.Data[d] = Data[s]; result.Data[d + 1] = Data[s + 1]; result.Data[d + 2] = Data[s + 2];
            }
        return result;
    }
}
