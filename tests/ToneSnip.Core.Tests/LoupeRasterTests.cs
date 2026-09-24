using ToneSnip.Core.Extract;
using ToneSnip.Core.Imaging;
using Xunit;

namespace ToneSnip.Core.Tests;

public class LoupeRasterTests
{
    private const int Source = 13, Cell = 8, Edge = 1, Size = Source * Cell;

    /// <summary>A 20×20 image whose pixel (x, y) is B = x, G = y, R = 200, half transparent.</summary>
    private static BgraImage Ramp()
    {
        var img = BgraImage.Blank(20, 20);
        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 20; x++)
            {
                int i = (y * 20 + x) * 4;
                img.Data[i] = (byte)x; img.Data[i + 1] = (byte)y; img.Data[i + 2] = 200; img.Data[i + 3] = 128;
            }
        return img;
    }

    private static (byte B, byte G, byte R, byte A) At(byte[] buf, int x, int y)
    {
        int i = (y * Size + x) * 4;
        return (buf[i], buf[i + 1], buf[i + 2], buf[i + 3]);
    }

    [Fact]
    public void Each_cell_shows_its_pixel_opaque_and_the_middle_one_is_the_point()
    {
        var buf = new byte[Size * Size * 4];
        LoupeRaster.Render(Ramp(), 10, 10, Source, Cell, Edge, buf);
        // Inside the middle cell, clear of its outline: pixel (10, 10).
        Assert.Equal(((byte)10, (byte)10, (byte)200, (byte)255), At(buf, 6 * Cell + 4, 6 * Cell + 4));
        // The top-left cell is pixel (4, 4); its first row and column are clear of the grid.
        Assert.Equal(((byte)4, (byte)4, (byte)200, (byte)255), At(buf, 2, 2));
    }

    [Fact]
    public void Pixels_off_the_image_are_black()
    {
        var buf = new byte[Size * Size * 4];
        LoupeRaster.Render(Ramp(), 0, 0, Source, Cell, Edge, buf);
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), At(buf, 2, 2));
    }

    [Fact]
    public void Grid_lines_darken_and_the_middle_pixel_is_ringed_white_in_black()
    {
        var buf = new byte[Size * Size * 4];
        LoupeRaster.Render(Ramp(), 10, 10, Source, Cell, Edge, buf);
        // The grid line at the left edge of the second column, in the first row of cells (pixel (5, 4), B = 5).
        (byte b, _, byte r, _) = At(buf, Cell, 3);
        Assert.Equal(PixelShade.Scale(200, LoupeRaster.GridKeep), r);
        Assert.True(b <= 5);
        int c = 6 * Cell;
        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)255), At(buf, c, c + 3));        // the white line on the cell's edge
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), At(buf, c - 1, c + 3));          // the black one just outside
    }

    [Fact]
    public void A_target_too_small_is_refused()
        => Assert.Throws<ArgumentException>(() => LoupeRaster.Render(Ramp(), 10, 10, Source, Cell, Edge, new byte[10]));

    [Fact]
    public void The_label_is_the_colour_as_copied_with_the_nits_beside_it()
    {
        Assert.Equal("#C80A14", ColorText.Loupe(0xFFC80A14, "hex", null));
        Assert.Equal("rgb(200, 10, 20)  ·  480 nits", ColorText.Loupe(0xFFC80A14, "rgb", 480f));
    }
}
