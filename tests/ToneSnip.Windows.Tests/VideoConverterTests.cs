using ToneSnip.Core.Geometry;
using ToneSnip.Core.Hdr;
using ToneSnip.Core.Imaging;
using ToneSnip.Core.Record;
using ToneSnip.Core.Tonemap;
using ToneSnip.Windows.Record;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit;

namespace ToneSnip.Windows.Tests;

/// <summary>
/// The recorder's conversion on the graphics card (CSVideo through <see cref="VideoConverter"/>) against
/// <see cref="Nv12"/>, its definition: a video frame must hold what a snip of the same moment holds, so an HDR frame is
/// tonemapped as the CPU tonemaps a snip, and an SDR frame keeps its own values.
/// </summary>
public sealed unsafe class VideoConverterTests(Warp warp) : IClassFixture<Warp>
{
    private ID3D11Texture2D Texture(Format format, int width, int height, byte[] pixels, int bytesPerPixel)
    {
        var desc = new Texture2DDescription(format, (uint)width, (uint)height, 1, 1, BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None);
        fixed (byte* p = pixels) return warp.Device.CreateTexture2D(desc, new SubresourceData((IntPtr)p, (uint)(width * bytesPerPixel)));
    }

    private byte[] Convert(ID3D11Texture2D frame, Format format, IntRect area, TonemapCurve? curve)
    {
        var nv12 = new byte[Nv12.Size(area.Width, area.Height)];
        lock (warp.Gate)
        {
            using var converter = new VideoConverter(warp.Gpu, format, area.Width, area.Height);
            fixed (byte* p = nv12) converter.Convert(frame, area.Left, area.Top, curve, p);
        }
        return nv12;
    }

    private static (int Max, int Differing) Compare(byte[] expected, byte[] actual)
    {
        int max = 0, differing = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            int d = Math.Abs(expected[i] - actual[i]);
            if (d == 0) continue;
            differing++;
            max = Math.Max(max, d);
        }
        return (max, differing);
    }

    /// <summary>An SDR monitor's frame: every byte value in every channel, cut from inside a larger frame at an
    /// offset, at a width that is not a whole number of the shader's 4-pixel words.</summary>
    [Fact]
    public void An_sdr_frame_converts_as_the_reference_does()
    {
        const int W = 134, H = 90;
        var full = BgraImage.Blank(W, H);
        var rng = new Random(7);
        rng.NextBytes(full.Data);
        for (int i = 0; i < 256; i++) { full.Data[i * 4] = full.Data[i * 4 + 1] = full.Data[i * 4 + 2] = (byte)i; }
        var area = new IntRect(6, 4, 98, 82);
        using ID3D11Texture2D frame = Texture(Format.B8G8R8A8_UNorm, W, H, full.Data, 4);
        byte[] gpu = Convert(frame, Format.B8G8R8A8_UNorm, area, null);

        var expected = new byte[gpu.Length];
        Nv12.FromBgra(full.Crop(area), expected);
        (int max, int differing) = Compare(expected, gpu);
        Assert.True(max <= 1, $"{max} steps off at worst, {differing} of {gpu.Length} bytes differ");
        Assert.True(differing * 200 <= gpu.Length, $"{differing} of {gpu.Length} bytes differ (over 0.5 %)");
    }

    public static TheoryData<string, float, float> Curves => new()
    {
        { "desktop", 1f, 1f }, { "desktop", 2f, 0.6f }, { "hable", 1f, 1f }, { "aces", 1.5f, 1f },
    };

    /// <summary>An HDR monitor's frame, tonemapped on the card, against the CPU's tonemap of the same pixels turned into
    /// NV12 by the reference: the snip's tonemap is within one step of the CPU's, and so is the video's.</summary>
    [Theory]
    [MemberData(nameof(Curves))]
    public void An_hdr_frame_converts_as_its_snip_would(string name, float exposure, float knee)
    {
        HalfImage scene = Images.Scene(260, 180);
        var curve = new TonemapCurve(name, new TonemapParams { SdrWhiteNits = 240, PeakNits = 1000, Exposure = exposure, Knee = knee });
        var area = new IntRect(10, 8, 226, 160);
        byte[] halves = new byte[scene.Data.Length * 2];
        Buffer.BlockCopy(scene.Data, 0, halves, 0, halves.Length);
        using ID3D11Texture2D frame = Texture(Format.R16G16B16A16_Float, scene.Width, scene.Height, halves, 8);
        byte[] gpu = Convert(frame, Format.R16G16B16A16_Float, area, curve);

        BgraImage snip = BgraImage.Blank(area.Width, area.Height);
        HalfFrame.Tonemap(scene, curve, area, snip, 0, 0);
        var expected = new byte[gpu.Length];
        Nv12.FromBgra(snip, expected);
        (int max, int differing) = Compare(expected, gpu);
        Assert.True(max <= 1, $"{name}: {max} steps off at worst, {differing} of {gpu.Length} bytes differ");
        Assert.True(differing * 100 <= gpu.Length, $"{name}: {differing} of {gpu.Length} bytes differ (over 1 %)");
    }

    [Fact]
    public void Odd_sides_are_refused()
    {
        lock (warp.Gate) Assert.Throws<ArgumentException>(() => new VideoConverter(warp.Gpu, Format.B8G8R8A8_UNorm, 99, 64));
    }
}
