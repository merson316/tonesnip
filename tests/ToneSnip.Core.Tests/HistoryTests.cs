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
    public void Lowering_the_cap_drops_the_oldest_and_raising_it_makes_room()
    {
        var h = new HistoryList(new[] { E(1), E(2), E(3), E(4), E(5) }, max: 5);
        Assert.Equal(new[] { "id2", "id1" }, h.Resize(3).Select(e => e.Id));
        Assert.Equal(new[] { "id5", "id4", "id3" }, h.Entries.Select(e => e.Id));
        Assert.Empty(h.Resize(4));
        Assert.Empty(h.Add(E(6)));   // the fourth slot is free now
        Assert.Equal("id3", Assert.Single(h.Add(E(7))).Id);
        Assert.Equal(4, h.Entries.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => h.Resize(0));
    }

    [Fact]
    public void Loading_more_rows_than_the_cap_keeps_the_newest()
    {
        var h = new HistoryList(new[] { E(1), E(4), E(2), E(3) }, max: 2);
        Assert.Equal(new[] { "id4", "id3" }, h.Entries.Select(e => e.Id));
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

    [Fact]
    public void Kind_and_text_survive_json_by_name_and_old_files_read_as_plain_snips()
    {
        var e = E(1) with { Kind = HistoryKind.Window | HistoryKind.Pin, Text = "hello" };
        string json = System.Text.Json.JsonSerializer.Serialize(new List<HistoryEntry> { e }, ToneSnip.Core.Config.JsonFile.Options);
        Assert.Contains("\"Window, Pin\"", json);
        HistoryEntry back = System.Text.Json.JsonSerializer.Deserialize<List<HistoryEntry>>(json, ToneSnip.Core.Config.JsonFile.Options)![0];
        Assert.Equal(HistoryKind.Window | HistoryKind.Pin, back.Kind);
        Assert.Equal("hello", back.Text);
        // A history.json from 1.0.4 has neither field.
        string old = """[{"id":"a","path":null,"takenUtc":"2026-09-05T00:00:00Z","width":1,"height":1,"hdr":false,"thumb":"t.png"}]""";
        HistoryEntry plain = System.Text.Json.JsonSerializer.Deserialize<List<HistoryEntry>>(old, ToneSnip.Core.Config.JsonFile.Options)![0];
        Assert.Equal(HistoryKind.None, plain.Kind);
        Assert.Null(plain.Text);
    }

    [Fact]
    public void Kept_text_is_trimmed_capped_and_never_splits_a_surrogate_pair()
    {
        Assert.Null(HistoryList.ClipText(null));
        Assert.Null(HistoryList.ClipText("  \r\n "));
        Assert.Equal("abc", HistoryList.ClipText("  abc\r\n"));
        Assert.Equal(HistoryList.MaxText, HistoryList.ClipText(new string('x', HistoryList.MaxText + 50))!.Length);
        string emoji = new string('x', HistoryList.MaxText - 1) + "\U0001F600";
        string clipped = HistoryList.ClipText(emoji)!;
        Assert.Equal(HistoryList.MaxText - 1, clipped.Length);
        Assert.False(char.IsHighSurrogate(clipped[^1]));
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

    /// <summary>A disk holding only <paramref name="present"/> (files and folders alike), recording what was asked.</summary>
    private static (Func<string, bool> File, Func<string, bool> Folder, List<string> Asked) Disk(params string[] present)
    {
        var asked = new List<string>();
        bool Has(string p) { asked.Add(p); return present.Contains(p, StringComparer.OrdinalIgnoreCase); }
        return (Has, Has, asked);
    }

    [Fact]
    public void Probe_keeps_a_file_that_is_there()
    {
        var d = Disk(@"C:\Shots\a.png", @"C:\Shots");
        Assert.Equal(HistoryProbe.Keep, HistoryList.Probe(@"C:\Shots\a.png", d.File, d.Folder));
    }

    [Theory]
    [InlineData(@"C:\Shots\a.png", @"C:\")]                           // the folder is still there
    [InlineData(@"C:\a.png", @"C:\")]
    [InlineData(@"C:\Old\Shots\a.png", @"C:\")]                       // the folder was deleted too
    [InlineData(@"C:\Users\me\Pictures\2025\Trip\Day 3\a.png", @"C:\")]   // several levels deleted
    [InlineData(@"\\nas\share\Shots\a.png", @"\\nas\share")]
    [InlineData(@"\\nas\share\a.png", @"\\nas\share")]
    public void Probe_removes_a_missing_file_whose_drive_or_share_is_there(string path, string root)
    {
        var d = Disk(@"C:\", @"C:\Shots", @"\\nas\share", @"\\nas\share\Shots");
        Assert.Equal(HistoryProbe.Remove, HistoryList.Probe(path, d.File, d.Folder));
        Assert.Equal(new[] { path, root }, d.Asked);
    }

    [Theory]
    [InlineData(@"E:\Shots\a.png", @"E:\")]                           // an unplugged drive
    [InlineData(@"E:\a.png", @"E:\")]
    [InlineData(@"\\nas\share\Shots\a.png", @"\\nas\share")]          // an offline share
    [InlineData(@"\\other\pics\a.png", @"\\other\pics")]              // a server that is not on the network
    public void Probe_hides_but_keeps_a_row_whose_drive_or_share_cannot_be_reached(string path, string root)
    {
        var d = Disk(@"C:\", @"C:\Shots");
        Assert.Equal(HistoryProbe.Hide, HistoryList.Probe(path, d.File, d.Folder));
        Assert.Equal(new[] { path, root }, d.Asked);
    }

    [Fact]
    public void Probe_keeps_a_snip_that_was_never_saved_without_touching_the_disk()
    {
        var d = Disk();
        Assert.Equal(HistoryProbe.Keep, HistoryList.Probe(null, d.File, d.Folder));
        Assert.Empty(d.Asked);
    }

    [Theory]
    [InlineData(@"Shots\a.png")]
    [InlineData(@"\\?\C:\Shots\a.png")]
    [InlineData(@"C:\Shots\CON.png")]
    public void Probe_removes_an_unsafe_path_without_touching_the_disk(string path)
    {
        var d = Disk(path, @"C:\Shots");
        Assert.Equal(HistoryProbe.Remove, HistoryList.Probe(path, d.File, d.Folder));
        Assert.Empty(d.Asked);
    }
}
