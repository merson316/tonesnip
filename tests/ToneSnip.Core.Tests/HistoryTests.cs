using ToneSnip.Core.Output;
using Xunit;

namespace ToneSnip.Core.Tests;

public class HistoryTests
{
    private static HistoryEntry E(int n) => new($"id{n}", $"p{n}.png", new DateTime(2026, 9, 5, 0, 0, n, DateTimeKind.Utc), 10, 10, false, $"t{n}.png");

    [Fact]
    public void Newest_first_and_capped()
    {
        var h = new HistoryList(max: 3);
        Assert.Empty(h.Add(E(1))); Assert.Empty(h.Add(E(2))); Assert.Empty(h.Add(E(3)));
        List<HistoryEntry> dropped = h.Add(E(4));
        Assert.Equal(new[] { "id4", "id3", "id2" }, h.Entries.Select(e => e.Id));
        Assert.Equal("id1", Assert.Single(dropped).Id);
    }

    [Fact]
    public void Loads_unsorted_input_newest_first_and_removes()
    {
        var h = new HistoryList(new[] { E(1), E(3), E(2) });
        Assert.Equal("id3", h.Entries[0].Id);
        Assert.True(h.Remove("id3"));
        Assert.False(h.Remove("id3"));
        Assert.Equal(2, h.Entries.Count);
    }

    [Fact]
    public void Update_replaces_in_place_without_reordering()
    {
        var h = new HistoryList(new[] { E(1), E(2), E(3) });
        Assert.True(h.Update(E(1) with { Width = 99 }));
        Assert.Equal(new[] { "id3", "id2", "id1" }, h.Entries.Select(e => e.Id));   // id1 stays last
        Assert.Equal(99, h.Entries[2].Width);
        Assert.False(h.Update(E(4)));
        Assert.Equal(3, h.Entries.Count);
    }

    [Theory]
    [InlineData(0.5, "just now")]
    [InlineData(3, "3 min ago")]
    [InlineData(125, "2 h ago")]
    [InlineData(30 * 60, "yesterday")]
    [InlineData(5 * 24 * 60, "5 days ago")]
    public void Time_ago(double minutes, string expected)
    {
        var now = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, HistoryList.TimeAgo(now.AddMinutes(-minutes), now));
    }

    [Fact]
    public void Entries_carry_an_optional_hdr_path_that_survives_json()
    {
        var e = new HistoryEntry("id", @"C:\x\Snip.png", DateTime.UtcNow, 1, 1, true, "t.png", @"C:\x\Snip.jxr");
        string json = System.Text.Json.JsonSerializer.Serialize(new List<HistoryEntry> { e, e with { HdrPath = null } });
        List<HistoryEntry>? back = System.Text.Json.JsonSerializer.Deserialize<List<HistoryEntry>>(json);
        Assert.Equal(@"C:\x\Snip.jxr", back![0].HdrPath); Assert.Null(back[1].HdrPath);
    }
}

public class HistoryListHardeningTests
{
    [Fact]
    public void Null_and_incomplete_entries_from_a_hand_edited_file_are_dropped_not_thrown_on()
    {
        // A history.json of "[null]" parses cleanly; the list must not throw at startup.
        var entries = new ToneSnip.Core.Output.HistoryEntry?[]
        {
            null,
            new("a", @"C:\Shots\a.png", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), 10, 10, false, @"C:\t\a.png"),
            new(null!, @"C:\Shots\b.png", DateTime.UtcNow, 10, 10, false, @"C:\t\b.png"),
            new("c", null, DateTime.UtcNow, 10, 10, false, null!),
        };
        var list = new ToneSnip.Core.Output.HistoryList(entries!);
        Assert.Equal(new[] { "a" }, list.Entries.Select(e => e.Id));
    }
}

public class OrphanThumbTests
{
    [Fact]
    public void Thumbnails_no_row_names_are_orphans_and_named_ones_are_kept()
    {
        // A crash between writing a thumbnail and saving history.json, or a hand edit, can leave unreferenced thumbnails.
        var entries = new[] { new ToneSnip.Core.Output.HistoryEntry("a", null, DateTime.UtcNow, 1, 1, false, @"C:\App\thumbs\a.png") };
        string[] files = { @"C:\App\thumbs\a.png", @"C:\App\thumbs\B.PNG", @"C:\App\thumbs\c.png" };
        Assert.Equal(new[] { @"C:\App\thumbs\B.PNG", @"C:\App\thumbs\c.png" }, ToneSnip.Core.Output.HistoryList.OrphanThumbs(files, entries));
    }

    [Fact]
    public void A_row_is_matched_case_insensitively()
    {
        var entries = new[] { new ToneSnip.Core.Output.HistoryEntry("a", null, DateTime.UtcNow, 1, 1, false, @"C:\App\THUMBS\A.png") };
        Assert.Empty(ToneSnip.Core.Output.HistoryList.OrphanThumbs(new[] { @"C:\App\thumbs\a.png" }, entries));
    }
}
