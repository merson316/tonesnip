using ToneSnip.App.Capture;
using ToneSnip.App.Interop;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Geometry;
using Shell = ToneSnip.Windows.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ToneSnip.App.Output;

/// <summary>
/// ToneSnip's own notification card, shown when <c>Settings.Notification</c> is "tonesnip": a no-activate
/// <see cref="PopupWindow"/> in the bottom-right of the primary work area. It fades in, dismisses itself after
/// <see cref="Dwell"/> (paused while hovered), and re-binds rather than stacking.
/// </summary>
/// <remarks>
/// <para>It must never take the foreground: the Recent flyout closes when it loses the foreground, so an activating
/// card would dismiss it. Hence no Activate/Focus calls and no keyboard handling.</para>
/// <para>It is not owned by the overlay, since it outlives the snip; <see cref="ToastService"/> instead waits for the
/// session to finish before showing it.</para>
/// </remarks>
public sealed partial class ToastWindow : PopupWindow
{
    /// <summary>Fade duration; skipped when <c>ThemeManager.AnimationsEnabled</c> is off.</summary>
    private const double FadeMs = 120;

    /// <summary>How long the card stays up with the pointer off it.</summary>
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(5);

    /// <summary>The 96 px thumbnail slot in physical pixels at 200 %, so the larger file on disk is not decoded at
    /// full size.</summary>
    private const int ThumbDecodeWidth = 192;

    private readonly ILog _log;
    private readonly Action<CaptureResult> _open;
    private readonly Action<CaptureResult> _edit;
    private readonly DispatcherQueueTimer _dwell;
    private Storyboard? _fade;
    private CaptureResult? _result;
    private bool _dismissing;
    /// <summary>Set once the first placement has happened, so a re-bind re-places instead of fading in again.</summary>
    private bool _placed;

    internal ToastWindow(ILog log, Action<CaptureResult> open, Action<CaptureResult> edit) : base(activate: false)
    {
        _log = log;
        _open = open;
        _edit = edit;
        InitializeComponent();
        Theme.ThemeManager.Attach(this);

        _dwell = DispatcherQueue.CreateTimer();
        _dwell.IsRepeating = false;
        _dwell.Interval = Dwell;
        _dwell.Tick += OnDwellElapsed;

        // Hovering pauses the dwell timer.
        Root.PointerEntered += OnPointerEntered;
        Root.PointerExited += OnPointerExited;

        this.WhenClosed(() =>
        {
            _dwell.Stop();
            _dwell.Tick -= OnDwellElapsed;
            Root.PointerEntered -= OnPointerEntered;
            Root.PointerExited -= OnPointerExited;
            _fade?.Stop();
            _fade = null;
            _result = null;
        });

        ShowPopup(OnPlaced);
    }

    /// <summary>True while the card is fading out or closed, so a new snip builds a fresh card instead.</summary>
    internal bool IsDismissing => _dismissing || IsClosed;

    /// <summary>
    /// Puts a result on the card and restarts the dwell timer; a later snip re-binds and re-places the same window.
    /// </summary>
    internal void Bind(CaptureResult r, string thumbPath)
    {
        _result = r;
        bool saved = r.SavedPath != null;
        Core.Output.SnipNotice notice = ToastService.NoticeFor(r);
        string title = notice.Title;
        string detail = saved ? $"{Path.GetFileName(r.SavedPath)}  ·  {r.Region.Width} × {r.Region.Height}" : notice.Detail;

        TitleText.Text = title;
        // A live region announces on a name change, so the name carries the full sentence (the title alone often
        // repeats unchanged).
        AutomationProperties.SetName(TitleText, title + ", " + detail);
        Controls.LiveRegion.Announce(TitleText);
        DetailText.Text = detail;
        AutomationProperties.SetName(Root, title + ", " + detail);

        // Open and Edit work on the in-memory result; "Show in folder" needs a file.
        FolderLink.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
        Thumb.Source = LoadThumb(thumbPath);

        _dismissing = false;
        if (_placed) Place();
        RestartDwell();
    }

#if TONESNIP_HARNESS
    /// <summary>Screenshot harness: keeps the card up long enough to be photographed.</summary>
    internal void StopDwellForHarness() => _dwell.Stop();
#endif

    /// <summary>Fades the card out and closes it. Safe to call twice, and safe on an already-closed window.</summary>
    internal void Dismiss()
    {
        if (_dismissing || IsClosed) return;
        _dismissing = true;
        _dwell.Stop();
        if (!Theme.ThemeManager.AnimationsEnabled) { CloseNow(); return; }
        Fade(1, 0, CloseNow);
    }

    /// <summary>Hides immediately and then closes, before a snip captures the desktop.</summary>
    internal void HideNow()
    {
        if (IsClosed) return;
        _dismissing = true;
        _dwell.Stop();
        _fade?.Stop();
        AppWindow.Hide();
        CloseNow();
    }

    private void CloseNow()
    {
        if (IsClosed) return;
        // Deferred: closing a WinUI window from inside its own input handling or a storyboard callback breaks its
        // content island.
        DispatcherQueue.TryEnqueue(Close);
    }

    /// <summary>Bottom-right of the primary work area with a 12 px inset, the same corner as the Recent
    /// flyout.</summary>
    private void Place()
    {
        if (IsClosed) return;
        IntRect work = Native.PrimaryWorkArea();
        if (work.IsEmpty) work = App.Current.PrimaryMonitor;
        TargetArea = work;                      // before ContentSize: it measures at this monitor's scale
        (int w, int h) = ContentSize();
        int inset = (int)(12 * Scale);
        PlacePhysical(new IntRect(work.Right - w - inset, work.Bottom - h - inset, w, h));
    }

    /// <summary>The first layout pass places the card and fades it in; later passes only re-place it.</summary>
    private void OnPlaced()
    {
        if (IsClosed) return;
        Place();
        if (_placed) return;
        _placed = true;
        // An opacity fade only; animating the window position is not practical.
        if (!Theme.ThemeManager.AnimationsEnabled) { Root.Opacity = 1; return; }
        Fade(0, 1, null);
    }

    private void Fade(double from, double to, Action? done)
    {
        _fade?.Stop();
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(FadeMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, Root);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var story = new Storyboard();
        story.Children.Add(animation);
        if (done != null) story.Completed += (_, _) => done();
        _fade = story;
        story.Begin();
    }

    private void RestartDwell()
    {
        _dwell.Stop();
        _dwell.Start();
    }

    private void OnDwellElapsed(DispatcherQueueTimer timer, object args) => Dismiss();

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => _dwell.Stop();

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) { if (!_dismissing) RestartDwell(); }

    // ----- the three links and the close ----------------------------------------------------------------------------
    // Every action dismisses the card.

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (_result is { } r) _open(r);
        Dismiss();
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (_result is { } r) _edit(r);
        Dismiss();
    }

    private void OnFolder(object sender, RoutedEventArgs e)
    {
        Shell.Reveal(_result?.SavedPath, _log);
        Dismiss();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Dismiss();

    /// <summary>The history thumbnail at <see cref="ThumbDecodeWidth"/>. Returned empty and filled asynchronously from
    /// a stream; a missing or unreadable file leaves it empty.</summary>
    private BitmapImage? LoadThumb(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var bmp = new BitmapImage { DecodePixelType = DecodePixelType.Physical, DecodePixelWidth = ThumbDecodeWidth };
            _ = FillAsync(bmp, path);
            return bmp;
        }
        catch { return null; }
    }

    private async Task FillAsync(BitmapImage bmp, string path)
    {
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            using IRandomAccessStreamWithContentType stream = await file.OpenReadAsync();
            await bmp.SetSourceAsync(stream);
        }
        catch (Exception ex) { _log.Warn("toast thumbnail: " + ex.Message); }
    }
}
