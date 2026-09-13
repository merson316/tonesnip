using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using Xunit;

namespace ToneSnip.Core.Tests;

public class CompositorTests
{
    private static BgraImage Solid(int w, int h, byte r, byte g, byte b)
    {
        var img = new BgraImage(w, h, new byte[w * h * 4]);
        for (int i = 0; i < w * h; i++) { img.Data[i * 4] = r; img.Data[i * 4 + 1] = g; img.Data[i * 4 + 2] = b; img.Data[i * 4 + 3] = 255; }
        return img;
    }

    private static (byte r, byte g, byte b, byte a) Px(BgraImage img, int x, int y)
    {
        int i = (y * img.Width + x) * 4;
        return (img.Data[i], img.Data[i + 1], img.Data[i + 2], img.Data[i + 3]);
    }

    [Fact]
    public void Composes_across_two_monitors_with_a_gap()
    {
        var layers = new[]
        {
            new OutputLayer(new IntRect(0, 0, 100, 100), Solid(100, 100, 255, 0, 0)),
            new OutputLayer(new IntRect(100, 20, 50, 200), Solid(50, 200, 0, 255, 0)),
        };
        BgraImage img = Compositor.Compose(layers, new IntRect(90, 0, 30, 40));
        Assert.Equal((30, 40), (img.Width, img.Height));
        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), Px(img, 0, 0));
        Assert.Equal(((byte)0, (byte)255, (byte)0, (byte)255), Px(img, 15, 30));
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)0), Px(img, 15, 5));   // above DISPLAY2's top: uncovered
    }

    [Fact]
    public void Crop_copies_the_subrectangle()
    {
        BgraImage img = Solid(10, 10, 1, 2, 3);
        img.Data[(3 * 10 + 4) * 4] = 9;
        BgraImage c = img.Crop(new IntRect(4, 3, 2, 2));
        Assert.Equal(9, c.Data[0]);
        Assert.Equal(1, c.Data[4]);
    }
}
