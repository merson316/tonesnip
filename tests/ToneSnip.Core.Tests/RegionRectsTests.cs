using ToneSnip.Core.Geometry;
using Xunit;

namespace ToneSnip.Core.Tests;

public class RegionRectsTests
{
    private static IntRect[] Join(params IntRect[] bands)
    {
        var into = new IntRect[bands.Length];
        return into[..RegionRects.Join(bands, into)];
    }

    private static long Area(IEnumerable<IntRect> rects) => rects.Sum(r => (long)r.Width * r.Height);

    /// <summary>No two rectangles share a pixel, so painting each once covers the region exactly once.</summary>
    private static void AssertDisjoint(IntRect[] rects)
    {
        for (int i = 0; i < rects.Length; i++)
            for (int j = i + 1; j < rects.Length; j++)
                Assert.True(rects[i].Intersect(rects[j]).IsEmpty, $"{rects[i]} overlaps {rects[j]}");
    }

    [Fact]
    public void A_vertical_strip_cut_into_bands_by_a_box_beside_it_comes_back_whole()
    {
        // What Windows hands over for a 1 px guide column at x = 100 plus a 200 x 50 box at (300, 400): the column is
        // split at the box's top and bottom.
        IntRect[] joined = Join(
            IntRect.FromLtrb(100, 0, 101, 400),
            IntRect.FromLtrb(100, 400, 101, 450), IntRect.FromLtrb(300, 400, 500, 450),
            IntRect.FromLtrb(100, 450, 101, 1600));
        Assert.Equal(2, joined.Length);
        Assert.Contains(IntRect.FromLtrb(100, 0, 101, 1600), joined);
        Assert.Contains(IntRect.FromLtrb(300, 400, 500, 450), joined);
    }

    [Fact]
    public void A_moving_crosshair_old_and_new_joins_back_to_its_four_strips()
    {
        // Rows at y = 500 and 503, columns at x = 900 and 903, on a 2560 x 1600 monitor, banded the way GDI stores them.
        var bands = new List<IntRect>();
        void Band(int top, int bottom) { bands.Add(IntRect.FromLtrb(900, top, 901, bottom)); bands.Add(IntRect.FromLtrb(903, top, 904, bottom)); }
        Band(0, 500);
        bands.Add(IntRect.FromLtrb(0, 500, 2560, 501));
        Band(501, 503);
        bands.Add(IntRect.FromLtrb(0, 503, 2560, 504));
        Band(504, 1600);
        IntRect[] joined = Join(bands.ToArray());

        Assert.Equal(Area(bands), Area(joined));
        AssertDisjoint(joined);
        Assert.Equal(8, joined.Length);   // two full rows, and each column in the three pieces the rows leave
        Assert.Contains(IntRect.FromLtrb(900, 0, 901, 500), joined);
        Assert.Contains(IntRect.FromLtrb(903, 504, 904, 1600), joined);
    }

    [Fact]
    public void Rectangles_with_the_same_columns_but_a_gap_between_them_stay_apart()
    {
        IntRect[] joined = Join(IntRect.FromLtrb(10, 0, 20, 10), IntRect.FromLtrb(10, 15, 20, 30));
        Assert.Equal(2, joined.Length);
    }

    [Fact]
    public void Touching_rectangles_with_different_columns_stay_apart()
    {
        IntRect[] joined = Join(IntRect.FromLtrb(10, 0, 20, 10), IntRect.FromLtrb(10, 10, 25, 20));
        Assert.Equal(2, joined.Length);
    }

    [Fact]
    public void Two_offset_boxes_keep_their_area_and_never_overlap()
    {
        // The old and new pill reaches after a small move: two 920 x 160 boxes 12 px apart diagonally, banded.
        IntRect[] bands =
        {
            IntRect.FromLtrb(40, 100, 960, 112),
            IntRect.FromLtrb(40, 112, 972, 260),
            IntRect.FromLtrb(52, 260, 972, 272),
        };
        IntRect[] joined = Join(bands);
        Assert.Equal(Area(bands), Area(joined));
        AssertDisjoint(joined);
    }

    [Fact]
    public void It_can_join_in_place()
    {
        IntRect[] rects = { IntRect.FromLtrb(5, 0, 6, 10), IntRect.FromLtrb(5, 10, 6, 20), IntRect.FromLtrb(5, 20, 6, 30) };
        int n = RegionRects.Join(rects, rects);
        Assert.Equal(1, n);
        Assert.Equal(IntRect.FromLtrb(5, 0, 6, 30), rects[0]);
    }

    [Fact]
    public void Nothing_in_nothing_out() => Assert.Equal(0, RegionRects.Join(ReadOnlySpan<IntRect>.Empty, Span<IntRect>.Empty));
}
