using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using ToneSnip.Windows.Capture;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit;

namespace ToneSnip.Windows.Tests;

/// <summary>
/// <see cref="GpuTonemapper.Keep"/>, which copies a captured frame into the texture a snip keeps. A window's capture
/// surface can be larger than the window (Windows.Graphics.Capture rounds it up and does not shrink it when the window
/// does), so only its top-left width x height is the window.
/// </summary>
public sealed unsafe class KeepTests(Warp warp) : IClassFixture<Warp>
{
    /// <summary>An fp16 texture holding <paramref name="img"/>, as a capture hands one over.</summary>
    private ID3D11Texture2D Surface(HalfImage img, Format format = Format.R16G16B16A16_Float)
    {
        ID3D11Texture2D t = warp.Device.CreateTexture2D(new Texture2DDescription(format, (uint)img.Width, (uint)img.Height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Write));
        lock (warp.Gate)
        {
            MappedSubresource m = warp.Device.ImmediateContext.Map(t, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int rowBytes = format == Format.R16G16B16A16_Float ? img.Width * 8 : img.Width * 4;
                fixed (ushort* src = img.Data)
                    for (int y = 0; y < img.Height; y++)
                        Buffer.MemoryCopy((byte*)src + (long)y * rowBytes, (byte*)m.DataPointer + (long)y * m.RowPitch, rowBytes, rowBytes);
            }
            finally { warp.Device.ImmediateContext.Unmap(t, 0); }
        }
        return t;
    }

    private GpuHdrFrame Keep(ID3D11Texture2D source, int width, int height)
    {
        lock (warp.Gate) return warp.Gpu.Keep(source, (uint)width, (uint)height);
    }

    [Fact]
    public void A_whole_monitor_frame_is_kept_as_it_is()
    {
        HalfImage scene = Images.Scene(320, 180);
        using ID3D11Texture2D surface = Surface(scene);
        using GpuHdrFrame kept = Keep(surface, 320, 180);
        Assert.Equal((320, 180), (kept.Width, kept.Height));
        Assert.Equal(scene.Data, kept.Crop(new IntRect(0, 0, 320, 180)).Data);
    }

    [Fact]
    public void A_window_keeps_only_the_top_left_of_a_larger_surface()
    {
        HalfImage surfaceImage = Images.Scene(400, 300);
        using ID3D11Texture2D surface = Surface(surfaceImage);
        using GpuHdrFrame kept = Keep(surface, 257, 199);
        Assert.Equal((257, 199), (kept.Width, kept.Height));
        Assert.Equal(surfaceImage.Crop(new IntRect(0, 0, 257, 199)).Data, kept.Crop(new IntRect(0, 0, 257, 199)).Data);
    }

    [Fact]
    public void A_size_outside_the_surface_or_an_sdr_surface_is_refused()
    {
        HalfImage scene = Images.Scene(64, 64);
        using ID3D11Texture2D surface = Surface(scene);
        Assert.Throws<ArgumentOutOfRangeException>(() => Keep(surface, 65, 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => Keep(surface, 0, 64));
        using ID3D11Texture2D sdr = Surface(Images.Scene(32, 32), Format.B8G8R8A8_UNorm);
        Assert.Throws<ArgumentException>(() => Keep(sdr, 32, 32));
    }
}
