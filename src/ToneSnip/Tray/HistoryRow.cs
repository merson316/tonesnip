using System.ComponentModel;
using ToneSnip.App.Controls;
using ToneSnip.App.Output;
using ToneSnip.Core.Output;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ToneSnip.App.Tray;

/// <summary>What every row of one open flyout shares: brushes from the window's current theme and the thumbnail decode
/// width. Top-level because <c>x:Bind</c> cannot name a nested type.</summary>
public sealed record HistoryRowStyle(Brush Glyph, Brush ArmedFill, Brush ArmedGlyph, Brush HoverSolid, Brush HoverFade, int ThumbWidth);

/// <summary>Row view model for the Recent flyout's two item templates.</summary>
/// <remarks>Bindings must be <c>Mode=OneWay</c> (<c>x:Bind</c> defaults to <c>OneTime</c>): <see cref="Update"/>,
/// <see cref="DeleteArmed"/> and <see cref="Restyle"/> all drive the row through <see cref="PropertyChanged"/>.</remarks>
public sealed class HistoryRow : INotifyPropertyChanged
{
    /// <summary>
    /// Thumbnail files that failed to open or decode, kept for the life of the process: rows are rebuilt each time the
    /// flyout opens, so a per-row flag let every open re-open every bad file. Each file is also logged only once. A
    /// thumbnail's name is new for every write, so a listed path never becomes good later. UI thread only.
    /// </summary>
    private static readonly HashSet<string> FailedThumbs = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Bounds <see cref="FailedThumbs"/>: far above any history's size, so it only ever clears after a very
    /// long session of broken thumbnails, which then cost one more attempt each.</summary>
    private const int FailedThumbsKept = 1024;

    /// <summary>The entry the bindings last reflected, so a refresh can skip unchanged rows. The item itself is
    /// mutated in place on re-save, so the entry record is compared instead.</summary>
    private HistoryEntry _seen;
    /// <summary>Whether the file's drive had a Recycle Bin at the last update, which decides the delete wording.</summary>
    private bool _covers;
    /// <summary>Whether the thumbnail file was still being written at the last update.</summary>
    private bool _thumbPending;
    /// <summary>Whether a list container is showing this row, so its thumbnail is wanted (<see cref="LoadThumb"/>).</summary>
    private bool _thumbWanted;
    private HistoryRowStyle _style;
    private bool _deleteArmed;

    /// <summary>Brushes are passed in by the flyout rather than looked up, because a lookup in code would use the
    /// application theme rather than the window's.</summary>
    public HistoryRow(HistoryItem item, HistoryRowStyle style)
    {
        Item = item;
        _style = style;
        _seen = item.Entry;
        _covers = item.RecycleBinCovers;
        _thumbPending = item.ThumbPending;
        // No thumbnail yet: the flyout asks for it once a container shows the row (LoadThumb), so opening the flyout
        // does not start a decode for every snip in the history.
    }

    public HistoryItem Item { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Takes brushes rebuilt after a theme change and notifies the bindings that read them.</summary>
    public void Restyle(HistoryRowStyle style)
    {
        _style = style;
        foreach (string p in new[] { nameof(HoverSolid), nameof(HoverFade), nameof(DeleteGlyph), nameof(ConfirmFill), nameof(ConfirmForeground) }) Raise(p);
    }

    /// <summary>Re-reads the same snip after a refresh, notifying bindings only when something shown has changed.</summary>
    public void Update(HistoryItem item)
    {
        Item = item;
        HistoryEntry now = item.Entry;
        bool covers = item.RecycleBinCovers;
        bool pending = item.ThumbPending;
        if (_seen == now && covers == _covers && pending == _thumbPending) return;
        // The editor writes a new thumbnail file per save, so a new path means a new bitmap; a new snip's file exists
        // only once its pending write has landed.
        bool rewritten = !pending && (_thumbPending || !string.Equals(_seen.Thumb, now.Thumb, StringComparison.OrdinalIgnoreCase));
        _seen = now;
        _covers = covers;
        _thumbPending = pending;
        if (rewritten)
        {
            if (_thumbWanted) SetThumb(now.Thumb, _style.ThumbWidth);
            else ThumbSource = null;   // the old bitmap is stale; the new one loads when a container asks
        }
        foreach (string p in new[]
                 {
                     nameof(Title), nameof(Subtitle), nameof(AccessibleName), nameof(HdrVisibility),
                     nameof(SdrVisibility), nameof(HdrFileVisibility), nameof(HdrFileText), nameof(KindText), nameof(KindVisibility), nameof(FolderVisibility),
                     nameof(DeleteTip), nameof(ShownTitle), nameof(ShownSubtitle),
                 })
            Raise(p);
        if (rewritten) { Raise(nameof(ThumbSource)); Raise(nameof(PlaceholderVisibility)); }
    }

    /// <summary>The file's name; for a snip never saved, the first line of its text (a Copy text snip), "Pinned" (a pin
    /// from the snip screen) or "Copied only".</summary>
    public string Title => Item.Entry.Path != null ? Path.GetFileName(Item.Entry.Path)
        : Item.Entry.Text is { } text ? FirstLine(text)
        : Item.Entry.Kind.HasFlag(HistoryKind.Pin) ? "Pinned"
        : "Copied only";
    /// <summary>The row's screen-reader name, including what the Raw badges show, e.g. "Snip 120326.png, HDR, JXR,
    /// Window, 6 min ago · 1920 × 1080".</summary>
    public string AccessibleName => string.Join(", ", new[] { Title, Item.Entry.HdrPath != null ? "HDR" : "SDR", HdrFileText, KindText, Subtitle }.Where(p => p.Length > 0));

    private static string FirstLine(string text)
    {
        int end = text.AsSpan().IndexOfAny('\r', '\n');
        return end < 0 ? text : text[..end];
    }

    /// <summary>The kind tag: one word, the most specific when a snip is more than one (a pin made in window
    /// mode reads Pin).</summary>
    public string KindText => Item.Entry.Kind switch
    {
        var k when k.HasFlag(HistoryKind.Pin) => "Pin",
        var k when k.HasFlag(HistoryKind.Text) => "Text",
        var k when k.HasFlag(HistoryKind.Window) => "Window",
        _ => "",
    };
    public Visibility KindVisibility => KindText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Subtitle => $"{HistoryList.TimeAgo(Item.Entry.TakenUtc, NowUtc)} · {Item.Entry.Width} × {Item.Entry.Height}";

#if TONESNIP_HARNESS
    /// <summary>The wall clock, or a fixed time in screenshot harness modes so relative times are reproducible.</summary>
    internal static DateTime NowUtc => HarnessData.HistoryRowNowUtc ?? DateTime.UtcNow;
#else
    internal static DateTime NowUtc => DateTime.UtcNow;
#endif

    // Badges describe the files on disk: the main file is always SDR, so only a row with an HDR copy is badged HDR.
    public Visibility HdrVisibility => Item.Entry.HdrPath != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SdrVisibility => Item.Entry.HdrPath == null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HdrFileVisibility => Item.Entry.HdrPath != null ? Visibility.Visible : Visibility.Collapsed;
    public string HdrFileText => Item.Entry.HdrPath == null ? "" : HdrOutput.FormatOf(Item.Entry.HdrPath) switch { "jxr" => "JXR", "png" => "HDR PNG", "jpeg" => "HDR JPG", _ => "HDR file" };
    public Visibility FolderVisibility => Item.Entry.Path != null ? Visibility.Visible : Visibility.Collapsed;
    public Brush HoverSolid => _style.HoverSolid;
    public Brush HoverFade => _style.HoverFade;
    public Brush DeleteGlyph => _style.Glyph;
    /// <summary>The prompt's Delete button wears the destructive fill even at rest.</summary>
    public Brush ConfirmFill => _style.ArmedFill;
    public Brush ConfirmForeground => _style.ArmedGlyph;
    /// <summary>The delete button's tooltip and accessible name, saying where the file goes. A settings change while
    /// the flyout is open shows at the next refresh.</summary>
    public string DeleteTip => WillRecycle ? "Delete to the Recycle Bin" : "Delete";

    /// <summary>The setting is on and the file's drive has a Recycle Bin; removable and network drives delete outright.</summary>
    private bool WillRecycle => App.Current.Settings.DeleteToRecycleBin && Item.RecycleBinCovers;

    /// <summary>
    /// The row is showing its delete prompt: the title and subtitle become the question and its consequence, and
    /// Delete and Cancel buttons replace the badges and actions.
    /// </summary>
    public bool DeleteArmed
    {
        get => _deleteArmed;
        set
        {
            if (_deleteArmed == value) return;
            _deleteArmed = value;
            foreach (string p in new[] { nameof(ShownTitle), nameof(ShownSubtitle), nameof(RestingVisibility), nameof(ConfirmVisibility) }) Raise(p);
        }
    }

    public string ShownTitle => !_deleteArmed ? Title : "Delete this snip?";
    /// <summary>Kept short: in the row layout the prompt buttons share its line.</summary>
    public string ShownSubtitle => !_deleteArmed ? Subtitle
        : WillRecycle ? "Goes to the Recycle Bin" : "Deleted permanently";
    /// <summary>The badges and the action strip, shown except while the row is asking.</summary>
    public Visibility RestingVisibility => _deleteArmed ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ConfirmVisibility => _deleteArmed ? Visibility.Visible : Visibility.Collapsed;

    public ImageSource? ThumbSource { get; private set; }

    /// <summary>A placeholder glyph when there is no bitmap (missing or undecodable), so the slot does not look like
    /// it is still loading.</summary>
    public Visibility PlaceholderVisibility => ThumbSource == null ? Visibility.Visible : Visibility.Collapsed;

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    /// <summary>A container is showing this row: loads its thumbnail unless it is loaded, its file is still being
    /// written (<see cref="Update"/> loads it once the write lands), or it has failed before, in this open or an earlier
    /// one (<see cref="FailedThumbs"/>). A save writes a new thumbnail path, which is tried.</summary>
    public void LoadThumb()
    {
        _thumbWanted = true;
        if (ThumbSource != null || _thumbPending || FailedThumbs.Contains(Item.Entry.Thumb)) return;
        SetThumb(Item.Entry.Thumb, _style.ThumbWidth);
        Raise(nameof(ThumbSource));
        Raise(nameof(PlaceholderVisibility));
    }

    /// <summary>The row's container was recycled: lets go of the decoded bitmap, which the next container to show the
    /// row loads again.</summary>
    public void DropThumb()
    {
        _thumbWanted = false;
        if (ThumbSource == null) return;
        ThumbSource = null;
        Raise(nameof(ThumbSource));
        Raise(nameof(PlaceholderVisibility));
    }

    /// <summary>Loads the thumbnail decoded at <paramref name="width"/> device pixels (the slot at 200 %); a file that
    /// is missing or fails to decode shows the placeholder.</summary>
    private void SetThumb(string path, int width) => ThumbnailLoader.Load(path, width, bmp => ThumbSource = bmp, (bmp, ex) =>
    {
        if (FailedThumbs.Count >= FailedThumbsKept) FailedThumbs.Clear();
        if (FailedThumbs.Add(path)) App.Current.Log.Warn("history thumbnail: " + (ex?.Message ?? "the file is missing"));
        // Show the placeholder, unless a newer thumbnail has replaced this bitmap mid-decode.
        if (ReferenceEquals(ThumbSource, bmp))
        {
            ThumbSource = null;
            Raise(nameof(ThumbSource));
            Raise(nameof(PlaceholderVisibility));
        }
    });
}
