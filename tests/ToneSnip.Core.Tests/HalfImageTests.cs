using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using ToneSnip.Core.Tonemap;
using Xunit;

namespace ToneSnip.Core.Tests;

public class HalfImageTests
{
    private static FloatImage Ramp(int w, int h)
    {
        var f = new FloatImage(w, h);
        for (int i = 0; i < w * h; i++) { f.Data[i * 3] = i * 0.25f; f.Data[i * 3 + 1] = 1f; f.Data[i * 3 + 2] = -0.5f; }
        return f;
    }

    [Fact]
    public void Round_trips_through_half()
    {
        FloatImage f = Ramp(6, 3);
        FloatImage back = HalfImage.FromFloat(f).ToFloat();
        for (int i = 0; i < f.Data.Length; i++) Assert.Equal(f.Data[i], back.Data[i], 3);
    }

    [Fact]
    public void Crop_and_rotate_match_float_image()
    {
        FloatImage f = Ramp(5, 4);
        HalfImage h = HalfImage.FromFloat(f);
        Assert.Equal(f.Crop(1, 1, 3, 2).Data, h.Crop(new IntRect(1, 1, 3, 2)).ToFloat().Data);
        Assert.Equal(f.RotateClockwise(1).Data, h.RotateClockwise(1).ToFloat().Data);
        Assert.Equal(f.RotateClockwise(3).Data, h.RotateClockwise(3).ToFloat().Data);
        Assert.Equal((5, 4), (h.RotateClockwise(2).Width, h.RotateClockwise(2).Height));
    }

    [Fact]
    public void Sample_and_downsample()
    {
        HalfImage h = HalfImage.FromFloat(Ramp(8, 8));
        Assert.Equal(0.25f * 9, h.Sample(1, 1).R, 3);
        HalfImage small = h.Downsample(4);
        Assert.Equal((2, 2), (small.Width, small.Height));
        Assert.Equal(h.Sample(4, 4).R, small.Sample(1, 1).R);
    }

    [Fact]
    public void Auto_exposure_agrees_between_half_and_float()
    {
        var f = new FloatImage(64, 64);
        Array.Fill(f.Data, 4f);
        Assert.Equal(AutoExposure.Compute(f, 200f, 1f), AutoExposure.Compute(HalfImage.FromFloat(f), 200f, 1f), 3);
    }

    [Fact]
    public void Bgra_rotate_matches_half_rotate()
    {
        HalfImage h = HalfImage.FromFloat(Ramp(5, 3));
        BgraImage b = PixelConvert.ToBgra8Passthrough(h);
        Assert.Equal(PixelConvert.ToBgra8Passthrough(h.RotateClockwise(1)).Data, b.RotateClockwise(1).Data);
    }

    // Rotating into a caller-owned buffer must match the allocating form byte for byte.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Half_rotate_into_matches_allocating_rotate(int turns)
    {
        HalfImage h = HalfImage.FromFloat(Ramp(5, 3));
        HalfImage want = h.RotateClockwise(turns);
        var into = new HalfImage(want.Width, want.Height);
        Array.Fill(into.Data, (ushort)0xDEAD);   // the pooled buffer arrives dirty; every byte must be overwritten
        h.RotateClockwiseInto(turns, into);
        Assert.Equal(want.Data, into.Data);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Bgra_rotate_into_matches_allocating_rotate(int turns)
    {
        BgraImage b = PixelConvert.ToBgra8Passthrough(HalfImage.FromFloat(Ramp(5, 3)));
        BgraImage want = b.RotateClockwise(turns);
        BgraImage into = BgraImage.Blank(want.Width, want.Height);
        Array.Fill(into.Data, (byte)0xAB);
        b.RotateClockwiseInto(turns, into);
        Assert.Equal(want.Data, into.Data);
    }

    [Fact]
    public void Rotate_into_a_buffer_of_the_wrong_size_is_rejected()
    {
        HalfImage h = HalfImage.FromFloat(Ramp(5, 3));
        Assert.Throws<ArgumentException>(() => h.RotateClockwiseInto(1, new HalfImage(5, 3)));
        BgraImage b = PixelConvert.ToBgra8Passthrough(h);
        Assert.Throws<ArgumentException>(() => b.RotateClockwiseInto(1, BgraImage.Blank(5, 3)));
    }
}
