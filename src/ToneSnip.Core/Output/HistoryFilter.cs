using System.Buffers;

namespace ToneSnip.Core.Output;

/// <summary>Which snips the Recent filter shows: all, or those of one kind. HDR means saved with an HDR copy, as the
/// row's HDR tag says; SDR is everything else, including a snip taken on an HDR display that kept no HDR copy.</summary>
public enum HistoryShow { All, Hdr, Sdr, Window, Pin, Text }

/// <summary>How far back the Recent filter reaches: any time, the local calendar day, or the past 7 or 30 days.</summary>
public enum HistoryAge { Any, Today, Week, Month }

/// <summary>
/// The Recent flyout's search and filter. <see cref="Query"/> is split on white space, and every word must appear,
/// ignoring case, in the snip's file name, its HDR copy's file name or its recognised text; nothing else is indexed.
/// </summary>
public sealed record HistoryFilter(string Query = "", HistoryShow Show = HistoryShow.All, HistoryAge Age = HistoryAge.Any)
{
    public static readonly HistoryFilter None = new();

    /// <summary>Whether anything is filtered out: a search word, a kind or an age.</summary>
    public bool IsActive => Words.Length > 0 || Show != HistoryShow.All || Age != HistoryAge.Any;

    private string[] Words => (Query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Whether <paramref name="e"/> passes. <paramref name="zone"/> decides where "today" starts (the local
    /// zone when null).</summary>
    public bool Matches(HistoryEntry e, DateTime nowUtc, TimeZoneInfo? zone = null) => Matches(e, nowUtc, zone ?? TimeZoneInfo.Local, Words);

    /// <summary>The entries that pass, in their order.</summary>
    public IEnumerable<HistoryEntry> Apply(IEnumerable<HistoryEntry> entries, DateTime nowUtc, TimeZoneInfo? zone = null)
        => IsActive ? entries.Where(Predicate(nowUtc, zone)) : entries;

    /// <summary><see cref="Matches"/> for a whole list at one instant: the query is split once, not per entry. It is not
    /// cached on the record, whose <c>with</c> would copy it along with a stale query.</summary>
    public Func<HistoryEntry, bool> Predicate(DateTime nowUtc, TimeZoneInfo? zone = null)
    {
        if (!IsActive) return static _ => true;
        string[] words = Words;
        TimeZoneInfo z = zone ?? TimeZoneInfo.Local;
        return e => Matches(e, nowUtc, z, words);
    }

    private bool Matches(HistoryEntry e, DateTime nowUtc, TimeZoneInfo zone, string[] words)
    {
        if (!ShowMatches(e) || !AgeMatches(e.TakenUtc, nowUtc, zone)) return false;
        foreach (string word in words)
            if (!Contains(FileName(e.Path), word) && !Contains(FileName(e.HdrPath), word) && !Contains(e.Text, word)) return false;
        return true;
    }

    private bool ShowMatches(HistoryEntry e) => Show switch
    {
        HistoryShow.Hdr => IsHdr(e),
        HistoryShow.Sdr => !IsHdr(e),
        HistoryShow.Window => e.Kind.HasFlag(HistoryKind.Window),
        HistoryShow.Pin => e.Kind.HasFlag(HistoryKind.Pin),
        HistoryShow.Text => e.Kind.HasFlag(HistoryKind.Text),
        _ => true,
    };

    // The HDR and SDR filters split the list exactly as the rows' tags do, which describe the files on disk.
    // HistoryEntry.Hdr is not used: it is set for any snip that touched an HDR display, even one saved as SDR only.
    private static bool IsHdr(HistoryEntry e) => e.HdrPath != null;

    private bool AgeMatches(DateTime takenUtc, DateTime nowUtc, TimeZoneInfo zone)
    {
        switch (Age)
        {
            case HistoryAge.Today:
                // The local calendar day, so a snip from late last night is not "today" this morning.
                return TimeZoneInfo.ConvertTimeFromUtc(Utc(takenUtc), zone).Date == TimeZoneInfo.ConvertTimeFromUtc(Utc(nowUtc), zone).Date;
            case HistoryAge.Week: return nowUtc - takenUtc <= TimeSpan.FromDays(7);
            case HistoryAge.Month: return nowUtc - takenUtc <= TimeSpan.FromDays(30);
            default: return true;
        }
    }

    /// <summary>history.json is hand-editable, so a time without a zone is taken as UTC, as it was written.</summary>
    private static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : DateTime.SpecifyKind(t, DateTimeKind.Utc);

    /// <summary>The name without its folder: a search for "Users" must not match every snip under C:\Users.</summary>
    private static string? FileName(string? path)
    {
        if (path == null) return null;
        int cut = path.AsSpan().LastIndexOfAny(PathSeparators);
        return cut < 0 ? path : path[(cut + 1)..];
    }

    /// <summary>Either separator, whatever the host: history paths are Windows paths, and the tests run on Linux.</summary>
    private static readonly SearchValues<char> PathSeparators = SearchValues.Create("\\/");

    private static bool Contains(string? haystack, string word) => haystack != null && haystack.Contains(word, StringComparison.OrdinalIgnoreCase);
}
