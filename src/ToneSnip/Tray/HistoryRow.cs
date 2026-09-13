using System.ComponentModel;
using ToneSnip.App.Output;
using ToneSnip.Core.Output;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ToneSnip.App.Tray;

/// <summary>What every row of one open flyout shares: brushes from the window's current theme and the thumbnail decode
/// width. Top-level because <c>x:Bind</c> cannot name a nested type.</summary>
public sealed record HistoryRowStyle(Brush Glyph, Brush ArmedFill, Brush ArmedGlyph, Brush HoverSolid, Brush HoverFade, int ThumbWidth);

/// <summary>Row view model for the Recent flyout's two item templates.</summary>
/// <remarks>Bindings must be <c>Mode=OneWay</c> (<c>x:Bind</c> defaults to <c>OneTime</c>): <see cref="Update"/>,
/// <see cref="DeleteArmed"/> and <see cref="Restyle"/> all drive the row through <see cref="PropertyChanged"/>.</remarks>
public sealed class HistoryRow : INotifyPropertyChanged
{
    /// <summary>Thumbnails that failed to decode, so each broken file is logged only once.</summary>
    private static readonly HashSet<string> WarnedThumbs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The entry the bindings last reflected, so a refresh can skip unchanged rows. The item itself is
    /// mutated in place on re-save, so the entry record is compared instead.</summary>
    private HistoryEntry _seen;
    /// <summary>Whether the file was missing at the last update.</summary>
    private bool _missing;
    /// <summary>Whether the file's drive had a Recycle Bin at the last update, which decides the delete wording.</summary>
    private bool _covers;
    private HistoryRowStyle _style;
    private bool _deleteArmed;

    /// <summary>Brushes are passed in by the flyout rather than looked up, because a lookup in code would use the
    /// application theme rather than the window's.</summary>
    public HistoryRow(HistoryItem item, HistoryRowStyle style)
    {
        Item = item;
        _style = style;
        _seen = item.Entry;
        _missing = item.FileMissing;
        _covers = item.RecycleBinCovers;
        SetThumb(item.Entry.Thumb, style.ThumbWidth);
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
        bool missing = item.FileMissing;
        bool covers = item.RecycleBinCovers;
        if (_seen == now && missing == _missing && covers == _covers) return;
        bool rewritten = !string.Equals(_seen.Thumb, now.Thumb, StringComparison.OrdinalIgnoreCase);
        _seen = now;
        _missing = missing;
        _covers = covers;
        // The editor writes a new thumbnail file per save, so a new path means a new bitmap.
        if (rewritten) SetThumb(now.Thumb, _style.ThumbWidth);
        foreach (string p in new[]
                 {
                     nameof(Title), nameof(TitleOpacity), nameof(Subtitle), nameof(AccessibleName), nameof(HdrVisibility),
                     nameof(SdrVisibility), nameof(HdrFileVisibility), nameof(HdrFileText), nameof(FolderVisibility),
                     nameof(DeleteTip), nameof(ShownTitle), nameof(ShownTitleOpacity), nameof(ShownSubtitle), nameof(ConfirmLabel),
                 })
            Raise(p);
        if (rewritten) { Raise(nameof(ThumbSource)); Raise(nameof(PlaceholderVisibility)); }
    }

    public string Title => Item.FileMissing ? "File moved or deleted" : Item.Entry.Path != null ? Path.GetFileName(Item.Entry.Path) : "Copied only";
    public double TitleOpacity => Item.FileMissing ? 0.55 : 1.0;
    /// <summary>The row's screen-reader name, including what the Raw badges show, e.g. "Snip 120326.png, HDR, JXR,
    /// 6 min ago · 1920 × 1080".</summary>
    public string AccessibleName => string.Join(", ", new[] { Title, Item.Entry.HdrPath != null ? "HDR" : "SDR", HdrFileText, Subtitle }.Where(p => p.Length > 0));
    public string Subtitle => $"{HistoryList.TimeAgo(Item.Entry.TakenUtc, NowUtc)} · {Item.Entry.Width} × {Item.Entry.Height}";

#if TONESNIP_HARNESS
    /// <summary>The wall clock, or a fixed time in screenshot harness modes so relative times are reproducible.</summary>
    private static DateTime NowUtc => HarnessData.HistoryRowNowUtc ?? DateTime.UtcNow;
#else
    private static DateTime NowUtc => DateTime.UtcNow;
#endif

    // Badges describe the files on disk: the main file is always SDR, so only a row with an HDR copy is badged HDR.
    public Visibility HdrVisibility => Item.Entry.HdrPath != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SdrVisibility => Item.Entry.HdrPath == null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HdrFileVisibility => Item.Entry.HdrPath != null ? Visibility.Visible : Visibility.Collapsed;
    public string HdrFileText => Item.Entry.HdrPath == null ? "" : HdrOutput.FormatOf(Item.Entry.HdrPath) switch { "jxr" => "JXR", "png" => "HDR PNG", "jpeg" => "HDR JPG", _ => "HDR file" };
    public Visibility FolderVisibility => Item.Entry.Path != null && !Item.FileMissing ? Visibility.Visible : Visibility.Collapsed;
    public Brush HoverSolid => _style.HoverSolid;
    public Brush HoverFade => _style.HoverFade;
    public Brush DeleteGlyph => _style.Glyph;
    /// <summary>The prompt's Delete button wears the destructive fill even at rest.</summary>
    public Brush ConfirmFill => _style.ArmedFill;
    public Brush ConfirmForeground => _style.ArmedGlyph;
    /// <summary>The delete button's tooltip and accessible name, saying where the file goes. A settings change while
    /// the flyout is open shows at the next refresh.</summary>
    public string DeleteTip => Item.FileMissing ? "Remove from list"
        : WillRecycle ? "Delete to the Recycle Bin" : "Delete";

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
            foreach (string p in new[] { nameof(ShownTitle), nameof(ShownTitleOpacity), nameof(ShownSubtitle), nameof(RestingVisibility), nameof(ConfirmVisibility) }) Raise(p);
        }
    }

    public string ShownTitle => !_deleteArmed ? Title : Item.FileMissing ? "Remove from list?" : "Delete this snip?";
    public double ShownTitleOpacity => _deleteArmed ? 1.0 : TitleOpacity;
    /// <summary>Kept short: in the row layout the prompt buttons share its line.</summary>
    public string ShownSubtitle => !_deleteArmed ? Subtitle
        : Item.FileMissing ? "The file is already gone"
        : WillRecycle ? "Goes to the Recycle Bin" : "Deleted permanently";
    public string ConfirmLabel => Item.FileMissing ? "Remove" : "Delete";
    /// <summary>The badges and the action strip, shown except while the row is asking.</summary>
    public Visibility RestingVisibility => _deleteArmed ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ConfirmVisibility => _deleteArmed ? Visibility.Visible : Visibility.Collapsed;

    public ImageSource? ThumbSource { get; private set; }

    /// <summary>A placeholder glyph when there is no bitmap (missing or undecodable), so the slot does not look like
    /// it is still loading.</summary>
    public Visibility PlaceholderVisibility => ThumbSource == null ? Visibility.Visible : Visibility.Collapsed;

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    /// <summary>Loads the thumbnail PNG decoded at <paramref name="width"/> device pixels (the slot at 200 %), so the
    /// full-size thumbnail is never held. The bitmap fills asynchronously from a stream.</summary>
    private void SetThumb(string path, int width)
    {
        ThumbSource = null;
        try
        {
            if (!File.Exists(path)) return;
            var bmp = new BitmapImage { DecodePixelType = DecodePixelType.Physical, DecodePixelWidth = width };
            ThumbSource = bmp;   // before the fill, so its failure path can tell this bitmap from a newer one
            _ = FillAsync(bmp, path);
        }
        catch { ThumbSource = null; }
    }

    private async Task FillAsync(BitmapImage bmp, string path)
    {
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            using IRandomAccessStreamWithContentType stream = await file.OpenReadAsync();
            await bmp.SetSourceAsync(stream);
        }
        catch (Exception ex)
        {
            if (WarnedThumbs.Add(path)) App.Current.Log.Warn("history thumbnail: " + ex.Message);
            // Show the placeholder, unless a newer thumbnail has replaced this bitmap mid-decode. The WinRT awaits
            // resume on the UI thread, so raising here is safe.
            if (ReferenceEquals(ThumbSource, bmp))
            {
                ThumbSource = null;
                Raise(nameof(ThumbSource));
                Raise(nameof(PlaceholderVisibility));
            }
        }
    }
}
