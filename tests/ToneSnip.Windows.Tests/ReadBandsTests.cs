using System.Runtime.InteropServices;
using ToneSnip.Core.Imaging;
using ToneSnip.Windows.Capture;
using ToneSnip.Windows.Display;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit;

namespace ToneSnip.Windows.Tests;

/// <summary>
/// <see cref="ScreenCapture.ReadBands"/>, which reads a captured frame back through two small staging bands instead of
/// one frame-sized staging texture, on WARP. Its pixels must be exactly those of the whole-frame readback it replaced:
/// every row once, in order, through the mapped band's row pitch, for HDR (fp16) and SDR (BGRA8) frames alike.
/// </summary>
public sealed unsafe class ReadBandsTests(Warp warp) : IClassFixture<Warp>
{
    private static int BytesPerPixel(Format f) => f == Format.R16G16B16A16_Float ? 8 : 4;

    /// <summary>A GPU texture holding <paramref name="pixels"/>, as a capture's frame surface does.</summary>
    private ID3D11Texture2D Surface(byte[] pixels, int width, int height, Format format)
    {
        var desc = new Texture2DDescription(format, (uint)width, (uint)height, 1, 1, BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None);
        fixed (byte* p = pixels) return warp.Device.CreateTexture2D(desc, new SubresourceData((IntPtr)p, (uint)(width * BytesPerPixel(format))));
    }

    private static byte[] Pixels(int width, int height, Format format, int seed)
    {
        var bytes = new byte[width * height * BytesPerPixel(format)];
        if (format == Format.R16G16B16A16_Float)
        {
            // Finite halves: a copy is bitwise either way, but a NaN's payload is not something this test is about.
            var r = new Random(seed);
            Span<ushort> h = MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan());
            for (int i = 0; i < h.Length; i++) h[i] = (ushort)r.Next(0, 0x7C00);
        }
        else new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>What the capture did before: the frame copied whole into one staging texture and read from it.</summary>
    private byte[] WholeFrame(ID3D11Texture2D source, Format format, int width, int height)
    {
        int rowBytes = width * BytesPerPixel(format);
        var result = new byte[rowBytes * height];
        ID3D11DeviceContext context = warp.Device.ImmediateContext;
        lock (warp.Gate)
        {
            using ID3D11Texture2D staging = warp.Device.CreateTexture2D(new Texture2DDescription(format, (uint)width, (uint)height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            context.CopySubresourceRegion(staging, 0, 0, 0, 0, source, 0, new Vortice.Mathematics.Box(0, 0, 0, width, height, 1));
            MappedSubresource m = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                for (int y = 0; y < height; y++) Marshal.Copy(m.DataPointer + y * (int)m.RowPitch, result, y * rowBytes, rowBytes);
            }
            finally { context.Unmap(staging, 0); }
        }
        return result;
    }

    /// <summary>The banded readback into the capture's own consumers, plus the bands it was given.</summary>
    private (byte[] Pixels, List<(int Top, int Rows)> Bands) Banded(ID3D11Texture2D source, Format format, int width, int height, int bandBytes)
    {
        var bands = new List<(int, int)>();
        byte[] pixels;
        lock (warp.Gate)
        {
            if (format == Format.R16G16B16A16_Float)
            {
                var half = new HalfImage(width, height);
                ScreenCapture.ReadBands(warp.Device, warp.Device.ImmediateContext, source, format, width, height,
                    (top, rows, data, pitch) => { bands.Add((top, rows)); FrameConverter.HalfRowsInto(data, pitch, top, rows, half); }, bandBytes);
                pixels = MemoryMarshal.AsBytes(half.Data.AsSpan()).ToArray();
            }
            else
            {
                BgraImage bgra = BgraImage.Blank(width, height);
                ScreenCapture.ReadBands(warp.Device, warp.Device.ImmediateContext, source, format, width, height,
                    (top, rows, data, pitch) => { bands.Add((top, rows)); FrameConverter.Bgra8RowsInto(data, pitch, top, rows, bgra, opaque: false); }, bandBytes);
                pixels = bgra.Data;
            }
        }
        return (pixels, bands);
    }

    public static TheoryData<Format, int, int, int> Cases => new()
    {
        // format, width, height, rows a band: many bands with a partial last one, an exact multiple, one row a band,
        // two bands (the double buffer without a reuse), and a band taller than the frame. Odd widths give the mapped
        // bands a row pitch wider than the row.
        { Format.R16G16B16A16_Float, 97, 61, 7 },
        { Format.R16G16B16A16_Float, 64, 48, 8 },
        { Format.R16G16B16A16_Float, 33, 9, 1 },
        { Format.R16G16B16A16_Float, 50, 30, 20 },
        { Format.R16G16B16A16_Float, 41, 17, 400 },
        { Format.B8G8R8A8_UNorm, 97, 61, 7 },
        { Format.B8G8R8A8_UNorm, 64, 48, 8 },
        { Format.B8G8R8A8_UNorm, 33, 9, 1 },
        { Format.B8G8R8A8_UNorm, 50, 30, 20 },
        { Format.B8G8R8A8_UNorm, 41, 17, 400 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Bands_read_back_exactly_what_the_whole_frame_readback_did(Format format, int width, int height, int bandRows)
    {
        using ID3D11Texture2D surface = Surface(Pixels(width, height, format, width * height), width, height, format);
        (byte[] banded, List<(int Top, int Rows)> bands) = Banded(surface, format, width, height, bandRows * width * BytesPerPixel(format));

        Assert.Equal(WholeFrame(surface, format, width, height), banded);
        int rows = Math.Min(bandRows, height);
        Assert.Equal((height + rows - 1) / rows, bands.Count);
        int next = 0;
        foreach ((int top, int n) in bands)
        {
            Assert.Equal(next, top);   // top to bottom, every row once
            Assert.Equal(Math.Min(rows, height - top), n);
            next += n;
        }
        Assert.Equal(height, next);
    }

    /// <summary>A window's capture surface can be larger than the window: only its top-left is read.</summary>
    [Theory]
    [InlineData(Format.R16G16B16A16_Float)]
    [InlineData(Format.B8G8R8A8_UNorm)]
    public void Only_the_top_left_of_a_larger_surface_is_read(Format format)
    {
        const int SurfaceW = 120, SurfaceH = 90, W = 77, H = 53;
        using ID3D11Texture2D surface = Surface(Pixels(SurfaceW, SurfaceH, format, 11), SurfaceW, SurfaceH, format);
        (byte[] banded, _) = Banded(surface, format, W, H, 6 * W * BytesPerPixel(format));
        Assert.Equal(WholeFrame(surface, format, W, H), banded);
    }

    /// <summary>At the capture's own band size a monitor-sized frame takes several bands, and still comes back
    /// whole.</summary>
    [Theory]
    [InlineData(Format.R16G16B16A16_Float)]
    [InlineData(Format.B8G8R8A8_UNorm)]
    public void A_monitor_sized_frame_reads_back_whole_at_the_default_band_size(Format format)
    {
        const int W = 1920, H = 1080;
        using ID3D11Texture2D surface = Surface(Pixels(W, H, format, 5), W, H, format);
        var bands = new List<(int, int)>();
        byte[] banded;
        lock (warp.Gate)
        {
            BgraImage bgra = BgraImage.Blank(W, H);
            var half = new HalfImage(W, H);
            ScreenCapture.ReadBands(warp.Device, warp.Device.ImmediateContext, surface, format, W, H, (top, rows, data, pitch) =>
            {
                bands.Add((top, rows));
                if (format == Format.R16G16B16A16_Float) FrameConverter.HalfRowsInto(data, pitch, top, rows, half);
                else FrameConverter.Bgra8RowsInto(data, pitch, top, rows, bgra, opaque: false);
            });
            banded = format == Format.R16G16B16A16_Float ? MemoryMarshal.AsBytes(half.Data.AsSpan()).ToArray() : bgra.Data;
        }
        Assert.True(bands.Count > 1, $"{bands.Count} band(s)");
        Assert.All(bands, b => Assert.True((long)b.Item2 * W * BytesPerPixel(format) <= GpuTonemapper.BandBytes));
        Assert.Equal(WholeFrame(surface, format, W, H), banded);
    }
}
