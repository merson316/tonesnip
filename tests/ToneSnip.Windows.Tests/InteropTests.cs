using System.Runtime.InteropServices;
using ToneSnip.Core.Color;
using ToneSnip.Core.Imaging;
using ToneSnip.Windows.Capture;
using ToneSnip.Windows.Display;
using ToneSnip.Windows.Imaging;
using ToneSnip.Windows.Interop;
using ToneSnip.Windows.Tray;
using Xunit;

namespace ToneSnip.Windows.Tests;

/// <summary>The Win32, WIC and GDI helpers that need no window, no monitor and no desktop session.</summary>
public class InteropTests
{
    // ----- FrameConverter: a mapped capture frame into the pooled images -----

    /// <summary>A mapped texture's rows are RowPitch apart, usually wider than the row, and the padding must not leak
    /// into the image.</summary>
    [Fact]
    public void Frames_are_copied_row_by_row_skipping_the_pitch_padding()
    {
        const int W = 5, H = 3, HalfPitch = 64, BgraPitch = 32;
        byte[] half = new byte[HalfPitch * H], bgra = new byte[BgraPitch * H];
        new Random(3).NextBytes(half);
        new Random(4).NextBytes(bgra);
        HalfImage h = new(W, H);
        BgraImage b = BgraImage.Blank(W, H);
        GCHandle hh = GCHandle.Alloc(half, GCHandleType.Pinned), bh = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            FrameConverter.ToHalfInto(hh.AddrOfPinnedObject(), HalfPitch, W, H, h);
            FrameConverter.ToBgra8Into(bh.AddrOfPinnedObject(), BgraPitch, W, H, b);
        }
        finally { hh.Free(); bh.Free(); }
        for (int y = 0; y < H; y++)
        {
            Assert.Equal(MemoryMarshal.Cast<byte, ushort>(half.AsSpan(y * HalfPitch, W * 8)).ToArray(), h.Data.AsSpan(y * W * 4, W * 4).ToArray());
            for (int x = 0; x < W; x++)
            {
                Assert.Equal(bgra.AsSpan(y * BgraPitch + x * 4, 3).ToArray(), b.Data.AsSpan((y * W + x) * 4, 3).ToArray());
                Assert.Equal(255, b.Data[(y * W + x) * 4 + 3]);   // the desktop is opaque whatever the frame's alpha says
            }
        }
    }

    [Fact]
    public void A_frame_is_not_copied_into_an_image_of_another_size()
    {
        Assert.Throws<ArgumentException>(() => FrameConverter.ToHalfInto(IntPtr.Zero, 64, 4, 4, new HalfImage(4, 5)));
        Assert.Throws<ArgumentException>(() => FrameConverter.ToBgra8Into(IntPtr.Zero, 64, 4, 4, BgraImage.Blank(5, 4)));
    }

    // ----- Bitmaps and the JPEG XR codec, through WIC -----

    private static BgraImage Pattern(int w, int h, bool translucent)
    {
        var img = BgraImage.Blank(w, h);
        for (int i = 0; i < w * h; i++)
        {
            img.Data[i * 4] = (byte)(i * 7); img.Data[i * 4 + 1] = (byte)(i * 13); img.Data[i * 4 + 2] = (byte)(i * 29);
            img.Data[i * 4 + 3] = translucent ? (byte)(i % 3 == 0 ? 0 : 255) : (byte)255;
        }
        return img;
    }

    [Fact]
    public void A_png_decodes_back_to_the_same_pixels_alpha_included()
    {
        BgraImage img = Pattern(37, 23, translucent: true);
        for (int i = 0; i < img.Data.Length; i += 4) if (img.Data[i + 3] == 0) img.Data[i] = img.Data[i + 1] = img.Data[i + 2] = 0;
        BgraImage back = Bitmaps.Decode(Bitmaps.EncodePng(img));
        Assert.Equal((37, 23), (back.Width, back.Height));
        Assert.Equal(img.Data, back.Data);
    }

    /// <summary>JPEG has no alpha: a transparent pixel (a window snip's rounded corner) comes out white, not black.</summary>
    [Fact]
    public void A_jpeg_flattens_transparency_on_white()
    {
        var img = BgraImage.Blank(16, 16);   // all zero: transparent black
        BgraImage back = Bitmaps.Decode(Bitmaps.EncodeJpeg(img, 95));
        Assert.All(Enumerable.Range(0, 16 * 16), i => Assert.True(back.Data[i * 4] >= 250 && back.Data[i * 4 + 1] >= 250 && back.Data[i * 4 + 2] >= 250));
    }

    /// <summary>The editor's Save over its own JPEG: the new pixels replace the file, and no .tmp is left beside it.</summary>
    [Fact]
    public void A_replaced_jpeg_holds_the_new_image_and_leaves_no_temporary_file()
    {
        string dir = Directory.CreateTempSubdirectory("tonesnip-test-").FullName;
        try
        {
            string path = Path.Combine(dir, "snip.jpg");
            File.WriteAllBytes(path, [1, 2, 3]);
            Bitmaps.ReplaceJpeg(Pattern(40, 30, false), 90, path);
            BgraImage back = Bitmaps.Decode(File.ReadAllBytes(path));
            Assert.Equal((40, 30), (back.Width, back.Height));
            Assert.Equal(["snip.jpg"], Directory.GetFiles(dir).Select(Path.GetFileName));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Garbage_is_refused_rather_than_decoded()
    {
        Assert.ThrowsAny<Exception>(() => Bitmaps.Decode([1, 2, 3, 4, 5, 6, 7, 8]));
    }

    [Fact]
    public void A_thumbnail_fits_the_longest_edge_and_keeps_the_aspect()
    {
        BgraImage t = Bitmaps.Thumbnail(Pattern(640, 200, false), 160);
        Assert.Equal((160, 50), (t.Width, t.Height));
        BgraImage small = Pattern(100, 40, false);
        Assert.Same(small, Bitmaps.Thumbnail(small, 160));
    }

    /// <summary>The HDR sidecar and the Settings preview's packed frame: lossless JPEG XR gives back the same half
    /// floats.</summary>
    [Fact]
    public void Lossless_jpeg_xr_round_trips_half_floats_exactly()
    {
        HalfImage img = Images.Scene(96, 64);
        HalfImage back = JxrDecoder.DecodeHalf(JxrEncoder.Encode(img, lossless: true));
        Assert.Equal((96, 64), (back.Width, back.Height));
        Assert.Equal(img.Data, back.Data);
    }

    [Fact]
    public void Lossy_jpeg_xr_stays_close_and_is_smaller()
    {
        HalfImage img = Images.Scene(256, 128);
        byte[] lossless = JxrEncoder.Encode(img, lossless: true), lossy = JxrEncoder.Encode(img, lossless: false, quality: 0.9f);
        Assert.True(lossy.Length < lossless.Length, $"lossy {lossy.Length} bytes, lossless {lossless.Length}");
        HalfImage back = JxrDecoder.DecodeHalf(lossy);
        for (int i = 0; i < img.Data.Length; i += 4)
        {
            float a = Transfer.HalfToFloat(img.Data[i + 1]), b = Transfer.HalfToFloat(back.Data[i + 1]);
            Assert.True(MathF.Abs(a - b) <= 0.05f * MathF.Max(1f, a), $"green {a} came back {b}");
        }
    }

    // ----- small Win32 helpers -----

    [Fact]
    public void A_window_that_does_not_exist_is_snipped_from_the_screen()
    {
        Assert.Equal("the window is gone", WindowFinder.CaptureRefusal(IntPtr.Zero));
        Assert.Equal((Core.Geometry.IntRect.Empty, Core.Geometry.IntRect.Empty), WindowFinder.Rects(IntPtr.Zero));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(40)]
    public void The_tray_glyph_becomes_an_icon_at_every_size(int size)
    {
        IntPtr icon = TrayGlyph.CreateIcon(size, 0xFFFFFFFF);
        Assert.NotEqual(IntPtr.Zero, icon);
        Assert.True(User32.DestroyIcon(icon));
    }

    [Fact]
    public void The_tray_glyph_colour_follows_the_setting_unless_a_contrast_theme_is_on()
    {
        Assert.Equal(0xFF0078D4u, TrayGlyph.GlyphArgb("accent", "dark", 0xFF0078D4, highContrast: false));
        Assert.Equal(0xFFFFFFFFu, TrayGlyph.GlyphArgb("mono", "dark", 0xFF0078D4, highContrast: false));
        Assert.Equal(0xFF000000u, TrayGlyph.GlyphArgb("mono", "light", 0xFF0078D4, highContrast: false));
        Assert.Equal(SystemTheme.SysColorArgb(SystemTheme.ColorWindowText), TrayGlyph.GlyphArgb("accent", "dark", 0xFF0078D4, highContrast: true));
    }

    [Theory]
    [InlineData(0xFF000000u, true)]
    [InlineData(0xFFFFFFFFu, false)]
    [InlineData(0xFF0078D4u, true)]
    [InlineData(0xFFFFFF00u, false)]
    public void Dark_colours_are_told_apart_by_luma(uint argb, bool dark) => Assert.Equal(dark, SystemTheme.IsDarkColour(argb));

    /// <summary>A readback band: a 4 MB staging texture, never taller than the read or than D3D11 allows.</summary>
    [Fact]
    public void Half_float_readback_bands_fit_their_budget()
    {
        Assert.Equal(1, GpuTonemapper.HalfBandRows(1_000_000, 50));
        Assert.Equal(50, GpuTonemapper.HalfBandRows(10, 50));
        Assert.Equal((4 << 20) / (3840 * 8), GpuTonemapper.HalfBandRows(3840, 2160));
        Assert.Equal(16384, GpuTonemapper.HalfBandRows(1, 100_000));
    }
}
