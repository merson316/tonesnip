using ToneSnip.Core.Capture;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using Xunit;

namespace ToneSnip.Core.Tests;

public class WindowSnipTests
{
    // An SDR primary at the origin, an HDR monitor to its right, and a second HDR monitor below that one.
    private static readonly OutputInfo Primary = new(0, @"\\.\DISPLAY1", 0, 0, 1920, 1080, false, 80f, 80f);
    private static readonly OutputInfo Right = new(1, @"\\.\DISPLAY2", 1920, 0, 2560, 1440, true, 240f, 1000f);
    private static readonly OutputInfo Below = new(2, @"\\.\DISPLAY3", 1920, 1440, 2560, 1440, true, 200f, 600f);
    private static readonly OutputInfo[] Layout = { Primary, Right, Below };

    [Fact]
    public void A_window_on_one_monitor_takes_that_monitors_values()
    {
        Assert.Same(Right, WindowSnip.OutputFor(new IntRect(2000, 100, 800, 600), Layout));
        Assert.Same(Primary, WindowSnip.OutputFor(new IntRect(100, 100, 800, 600), Layout));
    }

    [Fact]
    public void A_window_across_two_HDR_monitors_takes_the_one_it_overlaps_most()
    {
        // 1000 rows on Right, 200 on Below.
        Assert.Same(Right, WindowSnip.OutputFor(new IntRect(2000, 440, 800, 1200), Layout));
        // 100 rows on Right, 700 on Below.
        Assert.Same(Below, WindowSnip.OutputFor(new IntRect(2000, 1340, 800, 800), Layout));
    }

    [Fact]
    public void A_window_across_HDR_and_SDR_takes_the_primary_even_when_mostly_on_the_HDR_one()
    {
        // 100 columns on the SDR primary, 700 on the HDR monitor.
        Assert.Same(Primary, WindowSnip.OutputFor(new IntRect(1820, 100, 800, 600), Layout));
    }

    [Fact]
    public void A_mixed_span_that_misses_the_primary_takes_the_largest_overlap()
    {
        // An SDR monitor under the primary; the window is on it and on the HDR one to its right, not on the primary.
        OutputInfo underPrimary = new(3, @"\\.\DISPLAY4", 0, 1080, 1920, 1080, false, 80f, 80f);
        OutputInfo[] layout = { Primary, Right, Below, underPrimary };
        // 120 columns on the SDR monitor, 280 on Below.
        Assert.Same(Below, WindowSnip.OutputFor(new IntRect(1800, 1500, 400, 300), layout));
        // 300 columns on the SDR monitor, 100 on Below.
        Assert.Same(underPrimary, WindowSnip.OutputFor(new IntRect(1620, 1500, 400, 300), layout));
    }

    [Fact]
    public void Mixed_spans_fall_back_to_the_largest_overlap_when_no_monitor_is_at_the_origin()
    {
        OutputInfo sdr = Primary with { Left = -1920 }, hdr = Right with { Left = 0 };
        Assert.Same(hdr, WindowSnip.OutputFor(new IntRect(-100, 100, 800, 600), new[] { sdr, hdr }));
    }

    [Fact]
    public void A_window_off_every_monitor_has_none()
    {
        Assert.Null(WindowSnip.OutputFor(new IntRect(-5000, -5000, 100, 100), Layout));
        Assert.Null(WindowSnip.OutputFor(IntRect.Empty, Layout));
    }

    private static BgraImage Filled(int w, int h, byte b, byte g, byte r, byte a)
    {
        var img = BgraImage.Blank(w, h);
        for (int i = 0; i < img.Data.Length; i += 4) { img.Data[i] = b; img.Data[i + 1] = g; img.Data[i + 2] = r; img.Data[i + 3] = a; }
        return img;
    }

    [Fact]
    public void Only_a_frame_with_nothing_in_it_is_blank()
    {
        Assert.True(WindowSnip.IsBlank(BgraImage.Blank(64, 48), 4));
        // An opaque black console or paused video is a real picture.
        Assert.False(WindowSnip.IsBlank(Filled(64, 48, 0, 0, 0, 255), 4));
        // A GDI client area: alpha left at zero, colour drawn.
        Assert.False(WindowSnip.IsBlank(Filled(64, 48, 30, 60, 200, 0), 4));
        // One sampled pixel with something in it is enough.
        BgraImage one = BgraImage.Blank(64, 48);
        one.Data[(8 * 64 + 8) * 4 + 3] = 255;
        Assert.False(WindowSnip.IsBlank(one, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowSnip.IsBlank(one, 0));
    }

    [Fact]
    public void An_HDR_frame_is_blank_only_when_every_channel_is_zero()
    {
        var half = new HalfImage(32, 32);
        Assert.True(WindowSnip.IsBlank(half, 2));
        half.Data[3] = 0x8000;   // negative zero alpha is still zero
        Assert.True(WindowSnip.IsBlank(half, 2));
        half.Data[3] = Core.Color.Transfer.FloatToHalf(1f);   // opaque black
        Assert.False(WindowSnip.IsBlank(half, 2));
    }

    [Fact]
    public void A_frame_the_size_of_the_visible_frame_lands_on_it()
    {
        IntRect frame = new(100, 50, 800, 600), window = IntRect.FromLtrb(93, 50, 907, 657);
        (IntRect at, IntRect keep) = WindowSnip.Place(frame, window, 800, 600);
        Assert.Equal(frame, at);
        Assert.Equal(frame, keep);
    }

    [Fact]
    public void A_frame_with_the_resize_borders_is_cut_to_the_visible_frame()
    {
        IntRect frame = new(100, 50, 800, 600), window = IntRect.FromLtrb(93, 50, 907, 657);
        (IntRect at, IntRect keep) = WindowSnip.Place(frame, window, window.Width, window.Height);
        Assert.Equal(window, at);
        Assert.Equal(frame, keep);
    }

    [Fact]
    public void A_frame_of_a_window_still_resizing_keeps_its_own_size_at_the_frames_corner()
    {
        IntRect frame = new(100, 50, 800, 600), window = IntRect.FromLtrb(93, 50, 907, 657);
        (IntRect at, IntRect keep) = WindowSnip.Place(frame, window, 780, 610);
        Assert.Equal(new IntRect(100, 50, 780, 610), at);
        Assert.Equal(at, keep);
    }

    [Fact]
    public void A_maximised_window_whose_rectangles_agree_is_placed_as_is()
    {
        IntRect r = new(0, 0, 1920, 1040);
        Assert.Equal((r, r), WindowSnip.Place(r, r, 1920, 1040));
        Assert.Equal((IntRect.Empty, IntRect.Empty), WindowSnip.Place(r, r, 0, 1040));
    }
}

public class WindowCornersTests
{
    [Theory]
    [InlineData(96, 9)]
    [InlineData(120, 11)]
    [InlineData(144, 13)]
    [InlineData(192, 17)]
    [InlineData(0, 9)]   // an unknown DPI reads as 96
    public void The_square_covers_the_scaled_radius_and_its_antialiasing(int dpi, int size)
        => Assert.Equal(size, WindowCorners.SizeFor(dpi));

    [Fact]
    public void Squares_sit_in_the_four_corners_and_shrink_for_a_tiny_window()
    {
        Assert.Equal(new[] { new IntRect(0, 0, 9, 9), new IntRect(91, 0, 9, 9), new IntRect(0, 41, 9, 9), new IntRect(91, 41, 9, 9) },
                     WindowCorners.Squares(100, 50, 9));
        Assert.All(WindowCorners.Squares(10, 6, 9), r => Assert.Equal((3, 3), (r.Width, r.Height)));
    }

    /// <summary>A round corner as DWM leaves it: transparent outside a quarter circle of radius <paramref name="r"/>
    /// centred <paramref name="r"/> in from the corner, one partly transparent pixel on the edge, opaque inside.</summary>
    private static byte[] RoundCorner(int s, int r, int corner)
    {
        var a = new byte[s * s];
        for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                // Distances measured from the window's own corner.
                int dx = corner is 0 or 2 ? x : s - 1 - x, dy = corner is 0 or 1 ? y : s - 1 - y;
                double d = Math.Sqrt(Math.Pow(Math.Max(0, r - dx - 0.5), 2) + Math.Pow(Math.Max(0, r - dy - 0.5), 2));
                a[y * s + x] = d <= r - 1 ? (byte)255 : d >= r ? (byte)0 : (byte)128;
            }
        return a;
    }

    private static BgraImage Grey(int w, int h, byte v, byte alpha = 255)
    {
        var img = BgraImage.Blank(w, h);
        for (int i = 0; i < img.Data.Length; i += 4) { img.Data[i] = img.Data[i + 1] = img.Data[i + 2] = v; img.Data[i + 3] = alpha; }
        return img;
    }

    private static byte[] Px(BgraImage img, int x, int y) => img.Data.AsSpan((y * img.Width + x) * 4, 4).ToArray();

    [Fact]
    public void Rounded_corners_become_transparent_and_the_edge_is_un_premultiplied()
    {
        const int s = 9, w = 40, h = 30;
        IntRect[] squares = WindowCorners.Squares(w, h, s);
        WindowCorners? corners = WindowCorners.Read(w, h, s, r => RoundCorner(s, 8, Array.IndexOf(squares, r)));
        Assert.NotNull(corners);
        // Premultiplied grey 100 as the capture holds it; the snip wants straight alpha.
        BgraImage img = Grey(w, h, 100);
        corners!.Apply(img);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Px(img, 0, 0));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Px(img, w - 1, h - 1));
        Assert.Equal(new byte[] { 100, 100, 100, 255 }, Px(img, s - 1, s - 1));
        Assert.Equal(new byte[] { 100, 100, 100, 255 }, Px(img, w / 2, h / 2));   // outside the squares: untouched
        // Somewhere on each edge a pixel is half covered: its colour is divided back out of the premultiplied value.
        int half = Enumerable.Range(0, s * s).First(i => RoundCorner(s, 8, 0)[i] == 128);
        Assert.Equal(new byte[] { 199, 199, 199, 128 }, Px(img, half % s, half / s));
    }

    [Fact]
    public void Opaque_corners_need_nothing()
        => Assert.Null(WindowCorners.Read(40, 30, 9, _ => Enumerable.Repeat((byte)255, 81).ToArray()));

    [Fact]
    public void A_corner_whose_inside_is_not_opaque_is_not_believed()
    {
        // A GDI window can leave the whole alpha channel at zero: that is not a transparent window.
        Assert.Null(WindowCorners.Read(40, 30, 9, _ => new byte[81]));
        // One corner believable, the others not: only it is applied.
        const int s = 9;
        IntRect[] squares = WindowCorners.Squares(40, 30, s);
        WindowCorners? corners = WindowCorners.Read(40, 30, s, r => Array.IndexOf(squares, r) == 1 ? RoundCorner(s, 8, 1) : new byte[81]);
        BgraImage img = Grey(40, 30, 50);
        corners!.Apply(img);
        Assert.Equal(new byte[] { 50, 50, 50, 255 }, Px(img, 0, 0));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Px(img, 39, 0));
    }

    /// <summary>A GDI client area as the capture holds it: alpha zero everywhere, and nothing at all (transparent
    /// black) outside the rounded outline.</summary>
    private static WindowCorners.Patch Gdi(int s, int r, int corner)
    {
        byte[] outline = RoundCorner(s, r, corner);
        return new WindowCorners.Patch(new byte[s * s], outline.Select(v => v == 0).ToArray());
    }

    [Fact]
    public void Corners_over_a_GDI_client_area_are_rounded_like_the_believed_ones()
    {
        const int s = 9, w = 40, h = 30;
        IntRect[] squares = WindowCorners.Squares(w, h, s);
        // Top corners from the DWM caption, bottom ones over the client area.
        WindowCorners? corners = WindowCorners.Read(w, h, s, r =>
        {
            int c = Array.IndexOf(squares, r);
            return c < 2 ? new WindowCorners.Patch(RoundCorner(s, 8, c), null) : Gdi(s, 8, c);
        }, radius: 0);
        BgraImage img = Grey(w, h, 100);
        corners!.Apply(img);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Px(img, 0, h - 1));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Px(img, w - 1, h - 1));
        Assert.Equal(new byte[] { 100, 100, 100, 255 }, Px(img, s - 1, h - s));
        // The mirror of the top-left square, flipped upside down.
        int half = Enumerable.Range(0, s * s).First(i => RoundCorner(s, 8, 0)[i] == 128);
        Assert.Equal(128, Px(img, half % s, h - 1 - half / s)[3]);
    }

    [Fact]
    public void With_no_corner_believed_the_window_radius_draws_the_outline()
    {
        const int s = 9, w = 40, h = 30;
        IntRect[] squares = WindowCorners.Squares(w, h, s);
        WindowCorners? corners = WindowCorners.Read(w, h, s, r => Gdi(s, 8, Array.IndexOf(squares, r)), radius: 8);
        BgraImage img = Grey(w, h, 100);
        corners!.Apply(img);
        foreach ((int x, int y) in new[] { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1) }) Assert.Equal(0, Px(img, x, y)[3]);
        Assert.Equal(255, Px(img, s - 1, s - 1)[3]);
        Assert.Equal(255, Px(img, 8, 0)[3]);   // past the radius along the edge
        // Without a radius (maximised, or Windows 10) nothing is rounded.
        Assert.Null(WindowCorners.Read(w, h, s, r => Gdi(s, 8, Array.IndexOf(squares, r)), radius: 0));
    }

    [Fact]
    public void Square_believed_corners_or_drawn_ones_are_not_rounded_by_guesswork()
    {
        const int s = 9, w = 40, h = 30;
        IntRect[] squares = WindowCorners.Squares(w, h, s);
        // Opaque, believed top corners say the window is square now (snapped), whatever its radius.
        Assert.Null(WindowCorners.Read(w, h, s, r =>
        {
            int c = Array.IndexOf(squares, r);
            return c < 2 ? new WindowCorners.Patch(Enumerable.Repeat((byte)255, s * s).ToArray(), null) : Gdi(s, 8, c);
        }, radius: 8));
        // Something drawn in the very corner: the window is not rounded there.
        var drawn = new WindowCorners.Patch(new byte[s * s], new bool[s * s]);
        Assert.Null(WindowCorners.Read(w, h, s, _ => drawn, radius: 8));
    }

    [Fact]
    public void The_rounded_outline_is_clear_outside_opaque_inside_and_partial_on_the_edge()
    {
        byte[] a = WindowCorners.Rounded(9, 8, 0);
        Assert.Equal(0, a[0]);
        Assert.Equal(255, a[8 * 9 + 8]);
        Assert.Contains(a, v => v is > 0 and < 255);
        // Each corner is the top-left one mirrored.
        byte[] br = WindowCorners.Rounded(9, 8, 3);
        for (int y = 0; y < 9; y++)
            for (int x = 0; x < 9; x++) Assert.Equal(a[y * 9 + x], br[(8 - y) * 9 + 8 - x]);
    }

    [Fact]
    public void Straight_images_get_the_alpha_only_and_HDR_crops_are_un_premultiplied()
    {
        const int s = 9, w = 40, h = 30;
        IntRect[] squares = WindowCorners.Squares(w, h, s);
        WindowCorners corners = WindowCorners.Read(w, h, s, r => RoundCorner(s, 8, Array.IndexOf(squares, r)))!;
        int half = Enumerable.Range(0, s * s).First(i => RoundCorner(s, 8, 0)[i] == 128);
        (int hx, int hy) = (half % s, half / s);

        BgraImage straight = Grey(w, h, 100);
        corners.Apply(straight, straight: true);
        Assert.Equal(new byte[] { 100, 100, 100, 128 }, Px(straight, hx, hy));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Px(straight, 0, 0));

        var crop = new HalfImage(w, h);
        ushort v = Core.Color.Transfer.FloatToHalf(0.5f);
        for (int i = 0; i < crop.Data.Length; i++) crop.Data[i] = v;
        corners.Unpremultiply(crop);
        (float r, _, _) = crop.Sample(hx, hy);
        Assert.Equal(0.5f * 255f / 128f, r, 2);
        Assert.Equal(0f, crop.Sample(0, 0).R);
        Assert.Equal(0.5f, crop.Sample(w / 2, h / 2).R);   // outside the corners: untouched
    }

    [Fact]
    public void A_wrong_sized_alpha_patch_is_refused()
        => Assert.Throws<ArgumentException>(() => WindowCorners.Read(40, 30, 9, _ => new byte[10]));
}
