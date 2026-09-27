using ToneSnip.Core.Color;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Hdr;
using ToneSnip.Core.Imaging;
using ToneSnip.Core.Tonemap;
using ToneSnip.Windows.Capture;
using Xunit;

namespace ToneSnip.Windows.Tests;

/// <summary>
/// The GPU path (<see cref="GpuHdrFrame"/>, <see cref="GpuTonemapper"/>, Shaders/Tonemap.hlsl) against the CPU path
/// (<see cref="HalfFrame"/>), which is the reference: the shaders reimplement the CPU curves, and any drift between the
/// two shows up as a snip that looks different depending on the hdr.gpuTonemap switch.
/// </summary>
public sealed class GpuTonemapTests(Warp warp) : IClassFixture<Warp>
{
    /// <summary>Every curve, at the exposures and knees where their branches differ.</summary>
    public static TheoryData<string, float, float> Cases => new()
    {
        { "desktop", 1f, 1f }, { "desktop", 1f, 0.5f }, { "desktop", 3f, 1f }, { "desktop", 3f, 0.6f }, { "desktop", 0.25f, 0.8f },
        { "hable", 1f, 1f }, { "hable", 3f, 1f }, { "aces", 1f, 1f }, { "aces", 3f, 1f },
    };

    private static TonemapCurve Curve(string name, float exposure, float knee)
        => new(name, new TonemapParams { SdrWhiteNits = 212, PeakNits = 1000, Exposure = exposure, Knee = knee });

    private static BgraImage Cpu(HalfImage img, TonemapCurve curve, IntRect source)
    {
        BgraImage dst = BgraImage.Blank(source.Width, source.Height);
        HalfFrame.Tonemap(img, curve, source, dst, 0, 0);
        return dst;
    }

    /// <summary>The acceptance bar of the GPU tonemap: no channel of any pixel more than one step away from the CPU's,
    /// and only a sliver of pixels off at all (float rounding in pow and exp2, not a different curve).</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_half_value_tonemaps_within_one_step_of_the_cpu(string name, float exposure, float knee)
    {
        HalfImage stress = Images.Stress();
        TonemapCurve curve = Curve(name, exposure, knee);
        var all = new IntRect(0, 0, stress.Width, stress.Height);
        using GpuHdrFrame frame = warp.Upload(stress);
        BgraImage gpu = BgraImage.Blank(stress.Width, stress.Height);
        frame.Tonemap(curve, all, gpu, 0, 0);
        (int max, long differing) = Images.Compare(Cpu(stress, curve, all), gpu);
        long pixels = (long)stress.Width * stress.Height;
        Assert.True(max <= 1, $"{name} exposure {exposure} knee {knee}: {max} LSB off at worst, {differing} of {pixels} pixels differ");
        Assert.True(differing * 1000 <= pixels, $"{name} exposure {exposure} knee {knee}: {differing} of {pixels} pixels differ (over 0.1 %)");
    }

    /// <summary>A selection away from the frame's origin, placed into a larger target at an offset, as a snip across
    /// monitors is composed: the source origin, the destination offset and the row pitch all have to line up, and the
    /// rest of the target is left alone.</summary>
    [Fact]
    public void A_sub_rectangle_lands_at_its_offset_and_nothing_else_is_touched()
    {
        HalfImage scene = Images.Scene(300, 200);
        TonemapCurve curve = Curve("desktop", 1.5f, 0.7f);
        var source = new IntRect(37, 21, 151, 93);
        const int X = 9, Y = 14;
        using GpuHdrFrame frame = warp.Upload(scene);
        var target = new BgraImage(200, 130, Enumerable.Repeat((byte)0x5A, 200 * 130 * 4).ToArray());
        frame.Tonemap(curve, source, target, X, Y);

        BgraImage expected = Cpu(scene, curve, source);
        for (int y = 0; y < target.Height; y++)
            for (int x = 0; x < target.Width; x++)
            {
                int i = (y * target.Width + x) * 4;
                bool inside = x >= X && x < X + source.Width && y >= Y && y < Y + source.Height;
                if (!inside)
                {
                    Assert.True(target.Data.AsSpan(i, 4).SequenceEqual(new byte[] { 0x5A, 0x5A, 0x5A, 0x5A }), $"({x}, {y}) is outside the placed area but was written");
                    continue;
                }
                int e = ((y - Y) * source.Width + (x - X)) * 4;
                for (int c = 0; c < 3; c++)
                    Assert.True(Math.Abs(target.Data[i + c] - expected.Data[e + c]) <= 1, $"({x}, {y}) channel {c}: GPU {target.Data[i + c]}, CPU {expected.Data[e + c]}");
            }
    }

    /// <summary>A full-frame tonemap of a frame taller than one staging band, read back through both bands in turn.</summary>
    [Fact]
    public void A_frame_larger_than_a_staging_band_reads_back_whole()
    {
        HalfImage scene = Images.Scene(1100, 1100);   // 4.84 MB of BGRA: two 4 MB bands
        TonemapCurve curve = Curve("hable", 1f, 1f);
        var all = new IntRect(0, 0, scene.Width, scene.Height);
        using GpuHdrFrame frame = warp.Upload(scene);
        BgraImage gpu = BgraImage.Blank(scene.Width, scene.Height);
        frame.Tonemap(curve, all, gpu, 0, 0);
        Assert.True(Images.Compare(Cpu(scene, curve, all), gpu).MaxLsb <= 1);
    }

    /// <summary>The readouts come back bit-exact: they copy half floats, and only the CPU does any arithmetic.</summary>
    [Fact]
    public void Crop_downsample_and_samples_are_the_uploaded_pixels()
    {
        HalfImage scene = Images.Scene(1100, 1030);
        scene.Data[0] = 0x7E00;   // a NaN and an infinity must survive the trip too
        scene.Data[1] = 0x7C00;
        using GpuHdrFrame frame = warp.Upload(scene);
        foreach (IntRect rect in new[] { new IntRect(0, 0, 1100, 1030), new IntRect(3, 999, 1, 31), new IntRect(250, 17, 777, 1001) })
            Assert.Equal(scene.Crop(rect).Data, frame.Crop(rect).Data);
        foreach (int step in new[] { 1, 3, 8 })
            Assert.Equal(scene.Downsample(step).Data, frame.Downsample(step).Data);
        var rng = new Random(7);
        for (int k = 0; k < 50; k++)
        {
            int x = rng.Next(scene.Width), y = rng.Next(scene.Height);
            Assert.True(frame.TrySample(x, y, out float r, out float g, out float b));
            Assert.Equal(scene.Sample(x, y), (r, g, b));
        }
        Assert.True(frame.TrySample(0, 0, out float nan, out float inf, out _));
        Assert.True(float.IsNaN(nan));
        Assert.True(float.IsPositiveInfinity(inf));
    }

    [Fact]
    public void Samples_outside_the_frame_are_refused()
    {
        using GpuHdrFrame frame = warp.Upload(Images.Scene(40, 30));
        Assert.False(frame.TrySample(-1, 0, out _, out _, out _));
        Assert.False(frame.TrySample(40, 0, out _, out _, out _));
        Assert.False(frame.TrySample(0, 30, out _, out _, out _));
    }

    /// <summary>Auto exposure's samples, in the crop's row-major order, whatever the step does at a row's end.</summary>
    [Fact]
    public void Luminances_match_the_cpu_frame_at_every_step()
    {
        HalfImage scene = Images.Scene(700, 400);
        using GpuHdrFrame frame = warp.Upload(scene);
        using var cpu = new HalfFrame(scene);
        var rect = new IntRect(13, 7, 611, 389);
        foreach (int step in new[] { 1, 2, 7, 611, 5000 })
            Assert.Equal(cpu.Luminances(rect, step), frame.Luminances(rect, step));
    }

    /// <summary>The GPU's stats count every pixel, where the CPU frame samples; they agree with an exact CPU sum.</summary>
    [Fact]
    public void Stats_are_the_exact_peak_and_mean()
    {
        HalfImage scene = Images.Scene(500, 300);
        using GpuHdrFrame frame = warp.Upload(scene);
        var rect = new IntRect(40, 30, 333, 201);
        Assert.True(frame.TryStats(rect, out float peak, out float mean));
        float cpuPeak = 0; double sum = 0;
        for (int y = rect.Top; y < rect.Bottom; y++)
            for (int x = rect.Left; x < rect.Right; x++)
            {
                (float r, float g, float b) = scene.Sample(x, y);
                float v = Transfer.Luminance709(r, g, b) * 80f;
                cpuPeak = MathF.Max(cpuPeak, v); sum += v;
            }
        Assert.Equal(cpuPeak, peak, 0.01f);
        Assert.Equal((float)(sum / ((double)rect.Width * rect.Height)), mean, 0.01f);

        // Clipped to the frame; a rectangle wholly outside it is empty, with nothing to measure.
        Assert.True(frame.TryStats(new IntRect(600, 400, 10, 10), out float none, out float noMean));
        Assert.Equal((0f, 0f), (none, noMean));
    }

    [Fact]
    public void Zebra_marks_the_same_pixels_as_the_cpu()
    {
        HalfImage stress = Images.Stress();
        using GpuHdrFrame frame = warp.Upload(stress);
        Assert.Equal(ZebraMask.Of(stress, 212f / 80f, 1.3f).Bits, frame.Zebra(212f / 80f, 1.3f).Bits);
        HalfImage scene = Images.Scene(333, 77);   // a width that is not a multiple of the mask's 32-pixel words
        using GpuHdrFrame odd = warp.Upload(scene);
        Assert.Equal(ZebraMask.Of(scene, 2.65f, 1f).Bits, odd.Zebra(2.65f, 1f).Bits);
    }

    [Fact]
    public void Rectangles_outside_the_frame_or_the_target_throw()
    {
        using GpuHdrFrame frame = warp.Upload(Images.Scene(64, 48));
        TonemapCurve curve = Curve("aces", 1f, 1f);
        Assert.Throws<ArgumentOutOfRangeException>(() => frame.Tonemap(curve, new IntRect(10, 10, 60, 10), BgraImage.Blank(64, 48), 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => frame.Tonemap(curve, new IntRect(0, 0, 64, 48), BgraImage.Blank(64, 48), 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => frame.Tonemap(curve, IntRect.Empty, BgraImage.Blank(64, 48), 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => frame.Crop(new IntRect(-1, 0, 5, 5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => frame.Luminances(new IntRect(0, 0, 65, 1), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => frame.Luminances(new IntRect(0, 0, 5, 5), 0));
    }

    /// <summary>After the snip is built its frame is disposed; a late reader gets a refusal or an exception, never
    /// another snip's pixels, and a second dispose is harmless.</summary>
    [Fact]
    public void A_disposed_frame_refuses_every_read()
    {
        GpuHdrFrame frame = warp.Upload(Images.Scene(32, 32));
        frame.Dispose();
        frame.Dispose();
        Assert.False(frame.Readable);
        Assert.False(frame.TrySample(1, 1, out _, out _, out _));
        Assert.False(frame.TryStats(new IntRect(0, 0, 8, 8), out _, out _));
        Assert.Throws<ObjectDisposedException>(() => frame.Crop(new IntRect(0, 0, 8, 8)));
        Assert.Throws<ObjectDisposedException>(() => frame.Tonemap(Curve("desktop", 1f, 1f), new IntRect(0, 0, 8, 8), BgraImage.Blank(8, 8), 0, 0));
    }

    [Fact]
    public void Nothing_is_logged_on_the_happy_path()
    {
        using (GpuHdrFrame frame = warp.Upload(Images.Scene(64, 64)))
            frame.Tonemap(Curve("desktop", 1f, 1f), new IntRect(0, 0, 64, 64), BgraImage.Blank(64, 64), 0, 0);
        Assert.Empty(warp.Log.Warnings);
    }
}
