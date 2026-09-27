using ToneSnip.Core.Output;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;

namespace ToneSnip.App.Tray;

/// <summary>The Recent flyout's search box and filter band. The filter lives only as long as the card: each open starts
/// with every snip showing and the search box put away.</summary>
public sealed partial class HistoryFlyout
{
    private HistoryFilter _filter = HistoryFilter.None;
    /// <summary>The search box is out, opened by the search button or Ctrl+F.</summary>
    private bool _searchOpen;
    /// <summary>The body's height with none of the find rows out, taken as the first of them comes out; NaN while none
    /// is.</summary>
    private double _restBody = double.NaN;
    /// <summary>The body's height floor while the find buttons show: room for every find row and one row of snips, so
    /// a short list has room for the rows without growing the card. NaN until measured.</summary>
    private double _findRoom = double.NaN;

    /// <summary>The rows the find buttons bring out between the header and the list.</summary>
    private FrameworkElement[] FindRows => [SearchRow, FilterBand, FilterSummary];

    /// <summary>Whether a snip passes the current search and filter, measured at one instant for the whole list.</summary>
    private Func<HistoryEntry, bool> FilterPredicate() => _filter.Predicate(HistoryRow.NowUtc);

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => SetFilter(_filter with { Query = sender.Text });

    private void OnShowChip(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out HistoryShow show)) SetFilter(_filter with { Show = show });
    }

    private void OnAgeChip(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out HistoryAge age)) SetFilter(_filter with { Age = age });
    }

    private void OnClearFilters(object sender, RoutedEventArgs e)
    {
        Search.Text = "";   // raises OnSearchChanged, which filters with the query cleared
        SetFilter(HistoryFilter.None);
        CheckChips();
        // The link collapses with the summary, so focus goes to the search box, or to the filter button while the box
        // is put away, rather than being lost.
        if (_searchOpen) Search.Focus(FocusState.Programmatic);
        else FilterBtn.Focus(FocusState.Programmatic);
    }

    /// <summary>The search button opens the search box, and a second click puts it away.</summary>
    private void OnSearchButton(object sender, RoutedEventArgs e)
    {
        if (_searchOpen) CloseSearch();
        else OpenSearch();
    }

    /// <summary>Brings out the search box with the keyboard in it. The card keeps its size; the list gives up the room.</summary>
    private void OpenSearch()
    {
        _searchOpen = true;
        ShowFilterState();
        Place();
        // Once the row is laid out: a collapsed box cannot take focus.
        DispatcherQueue.TryEnqueue(() => { if (_searchOpen && !IsClosed) Search.Focus(FocusState.Keyboard); });
    }

    /// <summary>Puts the search box away, clearing its search, and hands the keyboard back to the search button.</summary>
    private void CloseSearch()
    {
        _searchOpen = false;
        Search.Text = "";   // raises OnSearchChanged, which filters with the query cleared
        ShowFilterState();
        Place();
        SearchBtn.Focus(FocusState.Keyboard);
    }

    /// <summary>Opens or closes the filter band. The card keeps its size; the list gives up the room.</summary>
    private void OnFilterButton(object sender, RoutedEventArgs e)
    {
        bool open = FilterBand.Visibility != Visibility.Visible;
        FilterBand.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ShowFilterState();
        Place();
    }

    /// <summary>Ctrl+F: opens the search box, or goes back to it when it is already out.</summary>
    private void OnFindAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FindButtons.Visibility != Visibility.Visible) return;
        args.Handled = true;
        if (!_searchOpen) OpenSearch();
        else Search.Focus(FocusState.Keyboard);
    }

    /// <summary>Down or Enter in the search box moves to the first row, so a search can be followed by the row keys.</summary>
    private void OnSearchKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Down or VirtualKey.Enter) || _rows.Count == 0) return;
        e.Handled = true;
        FocusRow(_rows[0], FocusState.Keyboard);
    }

    /// <summary>Escape in the search box, taken before the box's own text field handles it (which would leave the
    /// card's Escape accelerator unraised).</summary>
    private void OnSearchPreviewKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && EscapeSearch()) e.Handled = true;
    }

    /// <summary>Escape in the search box clears its text, and in an empty box puts the box away, rather than closing
    /// the card.</summary>
    private bool EscapeSearch()
    {
        if (!_searchOpen || !IsInside(FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject, Search)) return false;
        if (Search.Text.Length > 0) Search.Text = "";
        else CloseSearch();
        return true;
    }

    private void SetFilter(HistoryFilter next)
    {
        if (next == _filter) return;
        _filter = next;
        Refresh(filterOnly: true);
    }

    /// <summary>Checks the chips that match the filter (after Clear filters).</summary>
    private void CheckChips()
    {
        foreach (UIElement c in ShowChips.Children) if (c is RadioButton r) r.IsChecked = r.Tag as string == _filter.Show.ToString();
        foreach (UIElement c in AgeChips.Children) if (c is RadioButton r) r.IsChecked = r.Tag as string == _filter.Age.ToString();
    }

    /// <summary>
    /// Shows the search and filter buttons once there is a snip to find, the search box while it is out, the count and
    /// Clear link while a filter is on, and each button filled while its row is open or it is narrowing the list.
    /// <paramref name="total"/> is every snip in the history.
    /// </summary>
    private void ShowFilterState(int? total = null)
    {
        int all = total ?? App.Current.History.Items.Count;
        // Kept while a filter is on, even if the last snip goes, so the way back to all of them stays.
        bool findable = all > 0 || _filter.IsActive;
        FindButtons.Visibility = findable ? Visibility.Visible : Visibility.Collapsed;
        SearchRow.Visibility = findable && _searchOpen ? Visibility.Visible : Visibility.Collapsed;
        FilterSummary.Visibility = _filter.IsActive ? Visibility.Visible : Visibility.Collapsed;
        FilterCount.Text = $"{_rows.Count} of {(all == 1 ? "1 snip" : $"{all} snips")}";
        bool banded = _filter.Show != HistoryShow.All || _filter.Age != HistoryAge.Any;
        ShowOn(FilterBtn, FilterBand.Visibility == Visibility.Visible, banded, "Filter");
        ShowOn(SearchBtn, _searchOpen, !string.IsNullOrWhiteSpace(_filter.Query), "Search");
        KeepCardSize();
    }

    /// <summary>
    /// Keeps the card's size while the find rows come and go: while any is out, the body is fixed at the height it had
    /// with none out, less theirs, so the list gives up the room and scrolls. The card is anchored at the bottom, so a
    /// card that grew or shrank would move the header and the search box away from the pointer, and it would jump with
    /// every letter typed as rows drop out. With none out the body sizes to the list again, down to
    /// <see cref="_findRoom"/>.
    /// </summary>
    private void KeepCardSize()
    {
        double taken = 0;
        foreach (FrameworkElement row in FindRows)
            if (row.Visibility == Visibility.Visible) taken += Measured(row);
        if (taken == 0)
        {
            _restBody = double.NaN;
            Body.ClearValue(FrameworkElement.HeightProperty);
            // No floor once there is nothing to find: the empty text alone sizes the card, as before any snip.
            Body.MinHeight = !double.IsNaN(_findRoom) && FindButtons.Visibility == Visibility.Visible ? _findRoom : 0;
            return;
        }
        // ActualHeight is still the last layout's, from before this change, so it is the body with nothing out.
        if (double.IsNaN(_restBody)) _restBody = Body.ActualHeight;
        // A MinHeight would win over the smaller Height.
        Body.MinHeight = 0;
        Body.Height = Math.Max(0, _restBody - taken);
    }

    /// <summary>
    /// Measures, once, the room a short list needs for every find row and one row of snips, and makes it the body's
    /// floor, so opening them later does not grow the card. Needs a realized first row, so it runs after a layout.
    /// Never above the list's own cap: on a small screen the list shrinks further instead.
    /// </summary>
    private void ReserveFindRoom()
    {
        if (!double.IsNaN(_findRoom) || FindButtons.Visibility != Visibility.Visible) return;
        if (_items.ContainerFromIndex(0) is not FrameworkElement first || first.ActualHeight <= 0) return;
        double rows = 0;
        foreach (FrameworkElement row in FindRows)
        {
            // Collapsed measures as nothing, so each is measured visible and put back in the same turn, before a frame.
            Visibility was = row.Visibility;
            row.Visibility = Visibility.Visible;
            rows += Measured(row);
            row.Visibility = was;
        }
        Thickness pad = _items.Padding;
        double one = first.ActualHeight + first.Margin.Top + first.Margin.Bottom + pad.Top + pad.Bottom;
        _findRoom = Math.Min(rows + one, _items.MaxHeight);
        App.Current.Log.Debug($"flyout: {rows:0} px of find rows and a {one:0} px row reserve {_findRoom:0} px for the list");
        KeepCardSize();
    }

    /// <summary>The height <paramref name="row"/> takes in the card, margin included, at the body's width.</summary>
    private double Measured(FrameworkElement row)
    {
        row.Measure(new global::Windows.Foundation.Size(Body.ActualWidth, double.PositiveInfinity));
        return row.DesiredSize.Height;
    }

    /// <summary>A button filled while its row is <paramref name="open"/> or it is <paramref name="on"/>, in the primary
    /// text colour while on, and named so for screen readers, which cannot see either.</summary>
    private void ShowOn(Button button, bool open, bool on, string name)
    {
        if (open || on) button.Background = TokLayer.Background;
        else button.ClearValue(Control.BackgroundProperty);
        button.Foreground = (on ? TokPrimary : TokSecondary).Background;
        AutomationProperties.SetName(button, on ? name + ", on" : name);
    }
}
