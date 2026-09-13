namespace ToneSnip.Core.Output;

/// <summary>One remembered snip. <see cref="Path"/> is null when the snip was copied but not saved.</summary>
public sealed record HistoryEntry(string Id, string? Path, DateTime TakenUtc, int Width, int Height, bool Hdr, string Thumb, string? HdrPath = null);

/// <summary>Newest-first list of recent snips, capped; the file-side effects (thumbnail deletion) are left to the caller.</summary>
public sealed class HistoryList
{
    public const int DefaultMax = 20;
    private readonly List<HistoryEntry> _entries;
    public int Max { get; }
    public IReadOnlyList<HistoryEntry> Entries => _entries;

    public HistoryList(IEnumerable<HistoryEntry>? entries = null, int max = DefaultMax)
    {
        Max = max;
        // history.json is hand-editable: drop null rows and rows missing an id or thumbnail rather than throwing at startup.
        _entries = (entries ?? Array.Empty<HistoryEntry>())
            .Where(e => e is { Id: { Length: > 0 }, Thumb: { Length: > 0 } })
            .OrderByDescending(e => e.TakenUtc).Take(max).ToList();
    }

    /// <summary>Inserts at the front and returns the entries that fell off the end.</summary>
    public List<HistoryEntry> Add(HistoryEntry entry)
    {
        _entries.RemoveAll(e => e.Id == entry.Id);
        _entries.Insert(0, entry);
        var dropped = _entries.Skip(Max).ToList();
        if (dropped.Count > 0) _entries.RemoveRange(Max, dropped.Count);
        return dropped;
    }

    /// <summary>Swaps an entry for an updated one with the same Id, keeping its position; false when it is not listed.
    /// <see cref="Add"/> would move the row to the front, which a re-save of an older snip must not do.</summary>
    public bool Update(HistoryEntry entry)
    {
        int i = _entries.FindIndex(e => e.Id == entry.Id);
        if (i < 0) return false;
        _entries[i] = entry;
        return true;
    }

    public bool Remove(string id) => _entries.RemoveAll(e => e.Id == id) > 0;

    /// <summary>The thumbnail files no entry names: left behind by a crash between writing one and saving the list, or
    /// by a hand-edited history.json. Matched case-insensitively, as NTFS does.</summary>
    public static List<string> OrphanThumbs(IEnumerable<string> files, IEnumerable<HistoryEntry> entries)
    {
        var named = new HashSet<string>(entries.Select(e => e.Thumb), StringComparer.OrdinalIgnoreCase);
        return files.Where(f => !named.Contains(f)).ToList();
    }

    /// <summary>"just now", "3 min ago", "2 h ago", "yesterday", "5 days ago", or a short date beyond a month.</summary>
    public static string TimeAgo(DateTime takenUtc, DateTime nowUtc)
    {
        TimeSpan d = nowUtc - takenUtc;
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} min ago";
        if (d < TimeSpan.FromHours(24)) return $"{(int)d.TotalHours} h ago";
        if (d < TimeSpan.FromHours(48)) return "yesterday";
        if (d < TimeSpan.FromDays(31)) return $"{(int)d.TotalDays} days ago";
        return takenUtc.ToLocalTime().ToString("d MMM yyyy");
    }
}
