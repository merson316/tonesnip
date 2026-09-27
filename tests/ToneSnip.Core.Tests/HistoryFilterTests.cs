using ToneSnip.Core.Output;
using Xunit;

namespace ToneSnip.Core.Tests;

public class HistoryFilterTests
{
    private static readonly DateTime Now = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    private static HistoryEntry E(string id, string? path, TimeSpan age, bool hdr = false, string? hdrPath = null,
                                  HistoryKind kind = HistoryKind.None, string? text = null)
        => new(id, path, Now - age, 10, 10, hdr, id + ".png", hdrPath, kind, text);

    private static readonly HistoryEntry[] Rows =
    {
        E("plain", @"C:\Users\Sample\Pictures\Screenshots\Snip 2026-09-27 115900.png", TimeSpan.FromMinutes(1)),
        E("hdrfile", @"C:\Shots\Snip 2026-09-27 080000.png", TimeSpan.FromHours(4), hdr: true, hdrPath: @"C:\Shots\Snip 2026-09-27 080000.jxr"),
        E("hdronly", @"C:\Shots\Snip 2026-09-26 230000.png", TimeSpan.FromHours(13), hdr: true),
        E("window", @"C:\Shots\Snip 2026-09-24 120000.png", TimeSpan.FromDays(3), kind: HistoryKind.Window),
        E("pin", null, TimeSpan.FromDays(10), kind: HistoryKind.Pin | HistoryKind.Window),
        E("text", null, TimeSpan.FromDays(40), kind: HistoryKind.Text, text: "Quarterly figures\r\nRevenue up"),
    };

    private static string[] Ids(HistoryFilter f) => f.Apply(Rows, Now, TimeZoneInfo.Utc).Select(e => e.Id).ToArray();

    [Fact]
    public void No_filter_passes_everything_in_order()
    {
        Assert.False(HistoryFilter.None.IsActive);
        Assert.False(new HistoryFilter("   ").IsActive);
        Assert.Equal(Rows.Select(r => r.Id), Ids(HistoryFilter.None));
    }

    [Theory]
    [InlineData(HistoryShow.Hdr, "hdrfile")]
    [InlineData(HistoryShow.Sdr, "plain,hdronly,window,pin,text")]
    [InlineData(HistoryShow.Window, "window,pin")]
    [InlineData(HistoryShow.Pin, "pin")]
    [InlineData(HistoryShow.Text, "text")]
    public void Kinds(HistoryShow show, string expected)
    {
        Assert.True(new HistoryFilter(Show: show).IsActive);
        Assert.Equal(expected.Split(','), Ids(new HistoryFilter(Show: show)));
    }

    [Theory]
    [InlineData(HistoryAge.Today, "plain,hdrfile")]
    [InlineData(HistoryAge.Week, "plain,hdrfile,hdronly,window")]
    [InlineData(HistoryAge.Month, "plain,hdrfile,hdronly,window,pin")]
    public void Ages(HistoryAge age, string expected) => Assert.Equal(expected.Split(','), Ids(new HistoryFilter(Age: age)));

    [Fact]
    public void Today_is_the_local_calendar_day_not_the_last_24_hours()
    {
        // 23:00 UTC yesterday is 01:00 today in UTC+2, but still yesterday in UTC.
        TimeZoneInfo plusTwo = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");
        var f = new HistoryFilter(Age: HistoryAge.Today);
        Assert.True(f.Matches(Rows[2], Now, plusTwo));
        Assert.False(f.Matches(Rows[2], Now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Search_words_all_match_file_names_and_text_ignoring_case()
    {
        Assert.Equal(new[] { "plain", "hdrfile" }, Ids(new HistoryFilter("2026-09-27")));
        Assert.Equal(new[] { "hdrfile" }, Ids(new HistoryFilter(".JXR")));
        Assert.Equal(new[] { "text" }, Ids(new HistoryFilter("revenue QUARTERLY")));
        Assert.Empty(Ids(new HistoryFilter("revenue missing")));
    }

    [Fact]
    public void Search_ignores_the_folder()
    {
        Assert.Empty(Ids(new HistoryFilter("Users")));
        Assert.Empty(Ids(new HistoryFilter("Shots")));
    }

    [Fact]
    public void Hdr_means_an_hdr_copy_not_an_hdr_display()
    {
        // "hdronly" came off an HDR display but kept only its SDR file, so its row is tagged SDR and so is the filter.
        var hdr = new HistoryFilter(Show: HistoryShow.Hdr);
        var sdr = new HistoryFilter(Show: HistoryShow.Sdr);
        Assert.False(hdr.Matches(Rows[2], Now, TimeZoneInfo.Utc));
        Assert.True(sdr.Matches(Rows[2], Now, TimeZoneInfo.Utc));
        // Every row is in exactly one of the two.
        Assert.All(Rows, r => Assert.NotEqual(hdr.Matches(r, Now, TimeZoneInfo.Utc), sdr.Matches(r, Now, TimeZoneInfo.Utc)));
    }

    [Fact]
    public void Search_kind_and_age_combine()
    {
        Assert.Equal(new[] { "hdronly" }, Ids(new HistoryFilter("Snip", HistoryShow.Sdr, HistoryAge.Week) with { Query = "09-26" }));
        Assert.Equal(new[] { "hdrfile" }, Ids(new HistoryFilter("snip", HistoryShow.Hdr, HistoryAge.Today)));
    }

    [Fact]
    public void An_unzoned_time_from_a_hand_edited_file_is_read_as_utc()
    {
        HistoryEntry e = Rows[0] with { TakenUtc = DateTime.SpecifyKind(Rows[0].TakenUtc, DateTimeKind.Unspecified) };
        Assert.True(new HistoryFilter(Age: HistoryAge.Today).Matches(e, Now, TimeZoneInfo.Utc));
    }
}
