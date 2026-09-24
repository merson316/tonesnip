using System.Collections.ObjectModel;
using ToneSnip.App.Capture;
using ToneSnip.App.Interop;
using ToneSnip.App.Output;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Config;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using ToneSnip.Core.Output;
using ToneSnip.Windows.Imaging;
using ToneSnip.Windows.Interop;
using ToneSnip.Windows.Overlay;
using Shell = ToneSnip.Windows.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI;
// Windows.System also carries a DispatcherQueueTimer; only the key codes are wanted from it.
using VirtualKey = Windows.System.VirtualKey;

namespace ToneSnip.App.Tray;

/// <summary>The tray left-click flyout: a header that starts snips (modes, delay, settings), the recent snips with their
/// actions, and folder totals in the footer. The Row or Grid layout is read from settings each time it opens.</summary>
public sealed partial class HistoryFlyout : PopupWindow
{
    /// <summary>Fade-in for the window and the delay band. Heights are never animated.</summary>
    private const double FadeMs = 120;

    private readonly DispatcherQueueTimer _watch;
    private readonly DateTime _shown = DateTime.UtcNow;
    /// <summary>The list for the chosen layout; the other stays collapsed and unbound.</summary>
    private readonly ListViewBase _items;
    /// <summary>The bound rows, diffed against the history in <see cref="Refresh"/> so containers, hover, focus and an
    /// armed delete survive a snip landing or a delete.</summary>
    private readonly ObservableCollection<HistoryRow> _rows = new();
    private readonly bool _grid;
    /// <summary>The rows' shared brushes, built on the first refresh and rebuilt on theme change.</summary>
    private HistoryRowStyle? _style;
    private Storyboard? _bandFade;
    private HistoryRow? _focused;
    private bool _held;
    private bool _closing;
    private bool _placed;
    /// <summary>One background copy or open at a time, so repeated clicks do not race.</summary>
    private bool _working;
    /// <summary>A <see cref="Refresh"/> is queued, so a burst of history changes refreshes once.</summary>
    private bool _refreshQueued;
    /// <summary>The footer's folder scan: whether one has run for this open, is running, or is wanted again once the
    /// running one ends.</summary>
    private bool _scanned, _scanning, _rescan;
    /// <summary>The row each realized container is showing, so the row lets go of its thumbnail when its container is
    /// recycled or re-filled.</summary>
    private readonly Dictionary<SelectorItem, HistoryRow> _shownIn = new();

    public HistoryFlyout() : base(activate: true)
    {
        InitializeComponent();
        FitLists();
        Theme.ThemeManager.Attach(this);
        // Brushes painted from code must be re-resolved on theme change; see OnThemeChanged.
        Root.ActualThemeChanged += OnThemeChanged;
        this.WhenClosed(() => Root.ActualThemeChanged -= OnThemeChanged);
        // Read once per open; a layout change in Settings applies to the next flyout.
        _grid = string.Equals(App.Current.Settings.RecentFlyoutLayout, "grid", StringComparison.OrdinalIgnoreCase);
        if (_grid)
        {
            List.Visibility = Visibility.Collapsed;
            Cells.Visibility = Visibility.Visible;
        }
        _items = _grid ? Cells : List;
        _items.ItemsSource = _rows;
        // Containers are recycled, and the reveal is an Opacity inside the item template, so it must be reset per row.
        _items.ContainerContentChanging += OnContainerContentChanging;
        // Scrolling raises no PointerExited for a stationary pointer, so scrolls clear the hover too. The ScrollViewer
        // is a template part, available only once the list has loaded.
        _items.Loaded += (_, _) => HookScrollViewer();
        this.WhenClosed(() =>
        {
            _items.ContainerContentChanging -= OnContainerContentChanging;
            if (_scroller != null) { _scroller.ViewChanged -= OnItemsScrolled; _scroller = null; }
        });
        // Add/delete transitions only: the collection is diffed, so an entrance transition would replay on existing
        // rows. An empty collection (not the default) when Windows animations are off.
        _items.ItemContainerTransitions = Theme.ThemeManager.AnimationsEnabled
            ? new TransitionCollection { new AddDeleteThemeTransition() }
            : new TransitionCollection();
        Modes.Mode = App.Current.LastMode;
        Modes.ModeClicked += OnModeClicked;
        DelayRow.Seconds = Delay;
        DelayRow.Picked += OnDelayPicked;
        ShowDelay(open: false);
        this.WhenActivated(OnActivated);
        // Forcing the foreground from a hotkey (AttachThreadInput) can leave activation state stale, so Deactivated may
        // never fire. Poll the foreground window as well and close once it leaves us.
        _watch = DispatcherQueue.CreateTimer();
        _watch.Interval = TimeSpan.FromMilliseconds(150);
        _watch.IsRepeating = true;
        _watch.Tick += OnWatch;
        _watch.Start();
        App.Current.History.Changed += OnHistoryChanged;
        // Checks file existence and Recycle Bin coverage off the UI thread; changes arrive through History.Changed.
        _ = App.Current.History.ProbeAsync();
        this.WhenClosed(() =>
        {
            _watch.Stop();
            _watch.Tick -= OnWatch;
            _bandFade?.Stop();
            foreach (Storyboard fade in _actionFades.Values) fade.Stop();
            _actionFades.Clear();
            App.Current.History.Changed -= OnHistoryChanged;
        });
    }

    /// <summary>Shows the flyout; the rows and the placement follow the first layout pass.</summary>
    public void Open() => ShowPopup(OnPlaced);

    /// <summary>Closes on the next dispatcher turn: closing a WinUI window from inside its own input or activation
    /// handler tears down the island mid-event.</summary>
    public void Dismiss()
    {
#if TONESNIP_HARNESS
        if (StaysOpen || _closing) return;
#else
        if (_closing) return;
#endif
        _closing = true;
        _watch.Stop();
        DispatcherQueue.TryEnqueue(Close);
    }

    private void OnActivated(WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated) Dismiss();
    }

    private void OnWatch(DispatcherQueueTimer timer, object args)
    {
        if (User32.GetForegroundWindow() == Hwnd) { _held = true; return; }
        if (_held || (DateTime.UtcNow - _shown).TotalMilliseconds > 1500) { timer.Stop(); Dismiss(); }
    }

    private void OnHistoryChanged()
    {
        if (_refreshQueued) return;
        _refreshQueued = DispatcherQueue.TryEnqueue(() => { _refreshQueued = false; Refresh(); });
    }

    private void OnPlaced()
    {
        if (IsClosed) return;
        if (!_placed)
        {
            _placed = true;
            Refresh();
            Surface.UpdateLayout();
            Place();
            FocusFirstRow();
            // Fade only: the card is the window, so there is no slide.
            if (!Theme.ThemeManager.AnimationsEnabled) { Root.Opacity = 1; return; }
            var fade = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(FadeMs)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(fade, Root);
            Storyboard.SetTargetProperty(fade, "Opacity");
            var story = new Storyboard();
            story.Children.Add(fade);
            story.Begin();
            return;
        }
        Place();
    }

    /// <summary>
    /// Focuses the newest snip so the keyboard works immediately. Pointer focus draws no focus rectangle and reveals
    /// no actions; the first arrow key turns it into keyboard focus.
    /// </summary>
    private void FocusFirstRow()
    {
        if (_rows.Count > 0) FocusRow(_rows[0], FocusState.Pointer);
    }

    /// <summary>
    /// Lowers the lists' XAML <c>MaxHeight</c> caps to fit the primary work area, so the card never overruns small
    /// screens. Never raises them.
    /// </summary>
    private void FitLists()
    {
        IntRect work = Native.PrimaryWorkArea();
        if (work.IsEmpty) return;
        double scale = Native.ScaleAt(work);
        if (scale <= 0) scale = 1.0;
        // DIPs: header, hairline, footer, and the 12 px placement inset at each end.
        const double Chrome = 48 + 1 + 40 + 24;
        // A floor of about four rows (or one row of cells); a negative MaxHeight would not lay out.
        double room = Math.Max(160, work.Height / scale - Chrome);
        List.MaxHeight = Math.Min(List.MaxHeight, room);
        Cells.MaxHeight = Math.Min(Cells.MaxHeight, room);
        App.Current.Log.Debug($"flyout: work area {work.Width}x{work.Height} at {scale:0.##}x leaves {room:0} px for the list (caps 440 row / 520 grid)");
    }

    /// <summary>Places the card at the bottom right of the primary work area, 12 px in to leave room for its shadow.</summary>
    private void Place()
    {
        if (IsClosed) return;
        IntRect work = Native.PrimaryWorkArea();
        if (work.IsEmpty) work = App.Current.PrimaryMonitor;
        TargetArea = work;                      // before ContentSize, which measures at this area's Scale
        (int w, int h) = ContentSize();
        int inset = (int)(12 * Scale);
        PlacePhysical(new IntRect(work.Right - w - inset, work.Bottom - h - inset, w, h));
    }

    /// <summary>Diffs the rows against the history by id: removes, inserts and moves rows and updates survivors in place,
    /// so containers (with their hover, focus and armed delete) are not rebuilt on every snip.</summary>
    private void Refresh()
    {
        if (IsClosed) return;
        _style ??= NewRowStyle();
        IReadOnlyList<HistoryItem> items = App.Current.History.Items;
        bool membership = false;
        for (int i = _rows.Count - 1; i >= 0; i--)
        {
            if (items.Any(x => x.Entry.Id == _rows[i].Item.Entry.Id)) continue;
            Forget(_rows[i]);
            _rows.RemoveAt(i);
            membership = true;
        }
        for (int i = 0; i < items.Count; i++)
        {
            HistoryItem item = items[i];
            int at = IndexOf(item.Entry.Id);
            if (at < 0) { _rows.Insert(Math.Min(i, _rows.Count), new HistoryRow(item, _style)); membership = true; continue; }
            if (at != i) _rows.Move(at, i);
            _rows[i].Update(item);
        }
        Empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Text = $"Your snips will show up here. Press {App.Current.Settings.Hotkeys.Region} for a region snip.";
        // The folder is scanned once per open, and again only when a snip lands or is deleted: the probe's and the
        // thumbnail writes' changes do not change what the folder holds.
        if (!_scanned || membership) RefreshTotals();
        // Once containers settle: re-sync focus (a delete moves it), renumber the row ids and load the thumbnails of
        // the rows on screen.
        DispatcherQueue.TryEnqueue(() => { StampRowIds(); SyncFocus(); LoadRealizedThumbs(); });
    }

    /// <summary>
    /// Asks every row with a realized container for its thumbnail. Scrolling loads rows as their containers fill
    /// (<see cref="OnContainerContentChanging"/>); this covers the rows realized by the first layout and by a refresh.
    /// <see cref="HistoryRow.LoadThumb"/> does nothing for a row already loaded.
    /// </summary>
    private void LoadRealizedThumbs()
    {
        if (IsClosed) return;
        for (int i = 0; i < _rows.Count; i++) if (_items.ContainerFromIndex(i) != null) _rows[i].LoadThumb();
    }

    /// <summary>The extensions the footer counts.</summary>
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".jxr" };

    /// <summary>
    /// Updates the footer's image count and size for the top level of the save folder. Shows the cached figure at once
    /// and replaces it when the background scan finishes.
    /// </summary>
    private void RefreshTotals()
    {
        ShowTotals(App.Current.FolderTotals);
        _scanned = true;
        // One scan at a time: a change during a scan asks for one more once it ends, not for a second enumeration now.
        if (_scanning) { _rescan = true; return; }
        _scanning = true;
        string folder = App.Current.Settings.ResolvedSaveFolder(AppPaths.Pictures);
        // Captured on the UI thread: Window is thread-affine.
        DispatcherQueue ui = DispatcherQueue;
        _ = Task.Run(() =>
        {
            (int Count, long Bytes) totals = ScanFolder(folder);
            // App.FolderTotals is only read and written on the UI thread.
            ui.TryEnqueue(() =>
            {
                App.Current.FolderTotals = totals;
                _scanning = false;
                if (IsClosed) return;
                ShowTotals(totals);
                if (_rescan) { _rescan = false; RefreshTotals(); }
            });
        });
    }

    private void ShowTotals((int Count, long Bytes)? totals)
    {
        if (totals is not var (count, bytes)) return;   // nothing counted yet
        Totals.Text = $"{(count == 1 ? "1 image" : $"{count} images")} · {FormatSize(bytes)}";
    }

    private static (int Count, long Bytes) ScanFolder(string folder)
    {
        int count = 0;
        long bytes = 0;
        try
        {
            var dir = new DirectoryInfo(folder);
            if (!dir.Exists) return (0, 0);
            foreach (FileInfo f in dir.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                if (!ImageExtensions.Contains(f.Extension.ToLowerInvariant())) continue;
                count++;
                bytes += f.Length;
            }
        }
        catch (Exception ex) { App.Current.Log.Warn("folder totals: " + ex.Message); }
        return (count, bytes);
    }

    private int IndexOf(string id)
    {
        for (int i = 0; i < _rows.Count; i++) if (_rows[i].Item.Entry.Id == id) return i;
        return -1;
    }

    /// <summary>Clears every reference to a row leaving the list and hides its actions before the container is
    /// recycled.</summary>
    private void Forget(HistoryRow row)
    {
        if (ReferenceEquals(_armed, row)) Disarm();
        if (ReferenceEquals(_focused, row)) _focused = null;
        if (_hoverRoot is { } h && ReferenceEquals(h.Tag, row)) { _hoverRoot = null; Reveal(h); }
        if (_focusRoot is { } f && ReferenceEquals(f.Tag, row)) { _focusRoot = null; _keyboardRoot = null; Reveal(f); }
    }

    /// <summary>"512 B", "12 KB", "4.8 MB", "1.2 GB": one decimal below 10, none above.</summary>
    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 || v >= 10 ? $"{Math.Round(v)} {units[u]}" : $"{v:0.0} {units[u]}";
    }

    /// <summary>
    /// Rebuilds the brushes this window sets from code (the row style and the delay pill) once the framework has
    /// re-resolved the tree's ThemeResources. <c>ThemeManager.Changed</c> would be too early.
    /// </summary>
    private void OnThemeChanged(FrameworkElement sender, object args)
    {
        if (IsClosed) return;
        _style = NewRowStyle();
        foreach (HistoryRow row in _rows) row.Restyle(_style);
        ShowDelay(DelayRow.Visibility == Visibility.Visible);
    }

    /// <summary>The brushes and thumbnail size shared by all rows, read from the Tok swatches so they come from this
    /// window's theme rather than the application's.</summary>
    private HistoryRowStyle NewRowStyle()
    {
        // The row hover fill (SurfaceLayer over SurfaceBase) as an opaque colour, since the action group covers the
        // row's translucent hover fill.
        Color hover = Over(TokLayer.Background, TokBase.Background);
        var fade = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        // Zero-alpha of the same colour: a ramp from Transparent (black) would darken the middle.
        fade.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(0, hover.R, hover.G, hover.B) });
        fade.GradientStops.Add(new GradientStop { Offset = 1, Color = hover });
        return new HistoryRowStyle(TokPrimary.Background, TokError.Background, TokAccentText.Background,
                                   new SolidColorBrush(hover), fade, _grid ? 328 : 128);
    }

    /// <summary>Source-over of one solid brush on another, opaque.</summary>
    private static Color Over(Brush over, Brush under)
    {
        Color a = (over as SolidColorBrush)?.Color ?? Color.FromArgb(0, 0, 0, 0);
        Color b = (under as SolidColorBrush)?.Color ?? Color.FromArgb(0, 0, 0, 0);
        double f = a.A / 255.0;
        byte Mix(byte x, byte y) => (byte)Math.Round(x * f + y * (1 - f));
        return Color.FromArgb(255, Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B));
    }

    /// <summary>Escape cancels an open delete prompt, otherwise closes the card.</summary>
    private void OnEscape(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_armed is { } r)
        {
            bool inside = _focusRoot is { } f && ReferenceEquals(f.Tag, r);
            Disarm();
            if (inside) FocusRow(r, FocusState.Keyboard);
            return;
        }
        Dismiss();
    }

    /// <summary>A mode toggle in the header starts a snip in that mode.</summary>
    private void OnModeClicked(SnipMode mode)
    {
        Dismiss();
        App.Current.Snip(mode);
    }

    /// <summary>The default delay from settings, or 0 if the stored value is not one of the choices.</summary>
    private int Delay => SnipSettings.Delays.Contains(App.Current.Settings.DefaultDelay) ? App.Current.Settings.DefaultDelay : 0;

    private void OnDelayPicker(object sender, RoutedEventArgs e)
    {
        bool open = DelayRow.Visibility != Visibility.Visible;
        DelayRow.Seconds = Delay;
        ShowBand(open);
        Place();
    }

    /// <summary>A delay chosen here becomes the default delay setting.</summary>
    private void OnDelayPicked(int seconds)
    {
        App.Current.ApplySettings(App.Current.Settings with { DefaultDelay = seconds });
        DelayRow.Seconds = Delay;
        // Collapsing the band would drop keyboard focus, so return it to the pill that opened the band.
        bool keyboardInBand = FocusManager.GetFocusedElement(Root.XamlRoot) is UIElement { FocusState: FocusState.Keyboard } focused
                              && IsInside(focused, DelayRow);
        ShowBand(false);
        Place();
        if (keyboardInBand) DelayBtn.Focus(FocusState.Keyboard);
    }

    /// <summary>Opens or closes the delay band, fading its content in. The window's height changes in one step.</summary>
    private void ShowBand(bool open)
    {
        bool was = DelayRow.Visibility == Visibility.Visible;
        DelayRow.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        HeaderLine.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        ShowDelay(open);
        if (!open || was) return;
        _bandFade?.Stop();
        _bandFade = null;
        DelayRow.Opacity = 1;
        if (!Theme.ThemeManager.AnimationsEnabled) return;
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(FadeMs)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(fade, DelayRow);
        Storyboard.SetTargetProperty(fade, "Opacity");
        _bandFade = new Storyboard();
        _bandFade.Children.Add(fade);
        _bandFade.Begin();
    }

    /// <summary>Shows the current delay on the pill: secondary text when none, primary when set, filled while the band
    /// is open, matching the overlay toolbar.</summary>
    private void ShowDelay(bool open)
    {
        int d = Delay;
        DelayBtn.Content = d == 0 ? "No delay" : $"{d} s";
        // Spelled out for screen readers rather than the visible "3 s".
        AutomationProperties.SetName(DelayBtn, d == 0 ? "Delay, no delay" : $"Delay, {d} seconds");
        DelayBtn.Foreground = (d == 0 ? TokSecondary : TokPrimary).Background;
        // Cleared rather than set to null: the style's Transparent keeps the pill's padding hit-testable.
        if (open) DelayBtn.Background = TokLayer.Background;
        else DelayBtn.ClearValue(Control.BackgroundProperty);
    }

    private void OnSettings(object sender, RoutedEventArgs e) { Dismiss(); App.Current.ShowSettings(); }

    private void OnOpenFolder(object sender, RoutedEventArgs e) => App.Current.OpenSaveFolder();

    private static bool IsInside(DependencyObject? node, DependencyObject ancestor)
    {
        for (; node != null; node = VisualTreeHelper.GetParent(node)) if (ReferenceEquals(node, ancestor)) return true;
        return false;
    }

    private static HistoryRow? RowOf(object sender) => (sender as FrameworkElement)?.Tag as HistoryRow;

    private async void OnCopy(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) await CopyAsync(r); }

    /// <summary>
    /// Loads, decodes and copies the snip on the thread pool so the card stays responsive (the Win32 clipboard has no
    /// apartment requirement). Copying deliberately leaves the flyout open.
    /// </summary>
    private async Task CopyAsync(HistoryRow r)
    {
        if (_working) return;
        _working = true;
        try
        {
            HistoryItem item = r.Item;
            await ClipboardWriter.Enqueue(() =>
            {
                if (App.Current.History.LoadForCopy(item) is { } loaded) ClipboardWriter.Set(loaded.Image, loaded.Png, App.Current.Log);
            });
        }
        catch (Exception ex) { App.Current.Log.Warn("history copy: " + ex.Message); }
        finally { _working = false; }
    }

    private async void OnOpen(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) await OpenInViewerAsync(r); }

    private async void OnEdit(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) await OpenInViewerAsync(r, annotate: true); }

    private async void OnRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) { if (RowOf(sender) is { } r) await OpenInViewerAsync(r); }

    /// <summary><see cref="SnipHistory.ToResult"/> may decode the saved file, so it runs on the thread pool; the card
    /// closes only once there is something to show.</summary>
    private async Task OpenInViewerAsync(HistoryRow r, bool annotate = false)
    {
        if (_working) return;
        _working = true;
        try
        {
            HistoryItem item = r.Item;
            CaptureResult? result = await Task.Run(() => App.Current.History.ToResult(item));
            if (result == null) return;
            Dismiss();
            App.Current.OpenViewer(result, annotate);
        }
        catch (Exception ex) { App.Current.Log.Warn("history open: " + ex.Message); }
        finally { _working = false; }
    }

    private async void OnPin(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) await PinAsync(r); }

    private async void OnCopyText(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) await CopyTextAsync(r); }

    /// <summary>Pins the row's snip and closes the card, which would otherwise sit over the new pin's corner.</summary>
    private async Task PinAsync(HistoryRow r)
    {
        if (_working) return;
        _working = true;
        try
        {
            HistoryItem item = r.Item;
            CaptureResult? result = await Task.Run(() => App.Current.History.ToResult(item));
            if (result == null) return;
            Dismiss();
            await App.Current.PinAsync(result);
        }
        catch (Exception ex) { App.Current.Log.Warn("history pin: " + ex.Message); }
        finally { _working = false; }
    }

    /// <summary>Recognises the row's snip; the card stays open, as it does for Copy.</summary>
    private async Task CopyTextAsync(HistoryRow r)
    {
        if (_working) return;
        _working = true;
        try
        {
            HistoryItem item = r.Item;
            if (await Task.Run(() => App.Current.History.Load(item)) is { } img) await App.Current.CopyTextAsync(img);
        }
        catch (Exception ex) { App.Current.Log.Warn("history copy text: " + ex.Message); }
        finally { _working = false; }
    }

    private void OnShowInFolder(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { Item.Entry.Path: { } path }) Shell.Reveal(path, App.Current.Log);
    }

    private void OnListKey(object sender, KeyRoutedEventArgs e)
    {
        if (_focused is not { } r) return;
        if (e.Key == VirtualKey.Enter) { _ = OpenInViewerAsync(r); e.Handled = true; }
        else if (e.Key == VirtualKey.C && Win32.KeyDown(Win32.VkControl)) { _ = CopyAsync(r); e.Handled = true; }
        else if (e.Key == VirtualKey.P && !Win32.KeyDown(Win32.VkControl)) { _ = PinAsync(r); e.Handled = true; }
        else if (e.Key == VirtualKey.Delete)
        {
            e.Handled = true;
            // Ignore auto-repeat so holding Delete cannot confirm its own prompt.
            if (e.KeyStatus.WasKeyDown) return;
            if (!r.DeleteArmed) { Delete(r, fromKeyboard: true); return; }
            // Once armed, only Delete pressed on the prompt's Delete button confirms.
            if (ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), ButtonOf(r, "ConfirmDeleteBtn"))) Delete(r, fromKeyboard: true);
        }
    }

    /// <summary>
    /// A container is being filled with a row, new or recycled. Stamps its automation id and name, and resets its
    /// revealed actions immediately, since the reveal would otherwise carry over to the new row.
    /// </summary>
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        // The list raises this for phase 0 only; x:Phase bindings are run by the list itself and raise nothing. The
        // thumbnail loads in phase 1, once the row's text has shown, through a callback asked for here. A container
        // leaving for the recycle queue lets go of its bitmap, so only the rows on screen hold one.
        if (args.Phase > 0) return;
        if (args.ItemContainer is { } container)
        {
            // A container re-filled with another row without passing through the recycle queue still releases the
            // row it showed before, unless another container has already taken that row over.
            if (_shownIn.TryGetValue(container, out HistoryRow? before) && (args.InRecycleQueue || !ReferenceEquals(before, args.Item)))
            {
                _shownIn.Remove(container);
                if (!_shownIn.ContainsValue(before)) before.DropThumb();
            }
            if (!args.InRecycleQueue && args.Item is HistoryRow row)
            {
                _shownIn[container] = row;
                args.RegisterUpdateCallback(1, OnThumbPhase);
            }
        }
        // A container entering the recycle queue has no position to name.
        if (!args.InRecycleQueue && args.ItemContainer != null)
        {
            AutomationProperties.SetAutomationId(args.ItemContainer, RowId(args.ItemIndex));
            NameContainer(args.ItemContainer, args.Item as HistoryRow);
        }
        if (args.ItemContainer?.ContentTemplateRoot is not FrameworkElement root) return;
        if (ReferenceEquals(_hoverRoot, root)) _hoverRoot = null;
        if (ReferenceEquals(_focusRoot, root)) _focusRoot = null;
        if (ReferenceEquals(_keyboardRoot, root)) _keyboardRoot = null;
        if (ActionsOf(root) is not { } actions) return;
        StopFade(actions);
        actions.Opacity = 0;
    }

    /// <summary>Phase 1 of a container being filled: loads its row's thumbnail, unless the container has been recycled or
    /// given another row since phase 0.</summary>
    private void OnThumbPhase(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (IsClosed || args.InRecycleQueue || args.ItemContainer is not { } container) return;
        if (args.Item is HistoryRow row && _shownIn.TryGetValue(container, out HistoryRow? shown) && ReferenceEquals(shown, row)) row.LoadThumb();
    }

    /// <summary>
    /// The AutomationId of the row container at <paramref name="index"/>: "Flyout_Row1" for the top row.
    /// </summary>
    /// <remarks>
    /// 1-based, matching what a screen reader announces (scheme in Theme/Controls.xaml). By position rather than snip
    /// id, so a UIA client can address "the third row" and then an action inside it by its own id.
    /// </remarks>
    private static string RowId(int index) => $"Flyout_Row{index + 1}";

    /// <summary>
    /// Renumbers every realized container's row id and name. ContainerContentChanging covers realization and recycling,
    /// but containers whose item did not change are not re-prepared when Refresh inserts or moves rows around them.
    /// </summary>
    private void StampRowIds()
    {
        if (IsClosed) return;
        for (int i = 0; i < _rows.Count; i++)
            if (_items.ContainerFromIndex(i) is { } container) { AutomationProperties.SetAutomationId(container, RowId(i)); NameContainer(container, _rows[i]); }
    }

    /// <summary>Gives the container, which is what screen readers land on, the row's accessible name instead of the
    /// item's type name.</summary>
    private static void NameContainer(DependencyObject container, HistoryRow? row)
    {
        if (row != null) AutomationProperties.SetName(container, row.AccessibleName);
    }
}
