using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace ToneSnip.App.Tray;

/// <summary>The row actions revealed by hover and keyboard focus, and their fade: at most one row shows its actions.</summary>
/// <remarks>The actions live in the item template, out of reach of the container's PointerOver state. The template root
/// tracks the pointer; focus is tracked on the list, because it lands on the container, an ancestor of the template
/// root.</remarks>
public sealed partial class HistoryFlyout
{
    /// <summary>Fade for a row's action group on hover and focus.</summary>
    private const double ActionFadeMs = 83;

    /// <summary>
    /// The fade in flight on each row's action group, removed when it lands. Tracked per row because a Storyboard
    /// holds its end value, and one that is no longer referenced can neither be stopped nor overridden by a local
    /// <c>Opacity</c>, which would leave rows stuck revealed.
    /// </summary>
    private readonly Dictionary<FrameworkElement, Storyboard> _actionFades = new();
    /// <summary>The row under the pointer and the row containing focus.</summary>
    private FrameworkElement? _hoverRoot, _focusRoot;
    /// <summary>The row containing keyboard focus, or null. Actions are revealed for this or <see cref="_hoverRoot"/>,
    /// not <see cref="_focusRoot"/>, since a click also focuses a row.</summary>
    private FrameworkElement? _keyboardRoot;
    /// <summary>The chosen list's ScrollViewer, once its template has been applied.</summary>
    private ScrollViewer? _scroller;

    private void OnRowLoaded(object sender, RoutedEventArgs e) => Reveal((FrameworkElement)sender);

    /// <summary>Subscribes to the list's own ScrollViewer once, whichever layout is up.</summary>
    private void HookScrollViewer()
    {
        if (_scroller != null || IsClosed) return;
        _scroller = Descendant(_items, "ScrollViewer") as ScrollViewer;
        if (_scroller != null) _scroller.ViewChanged += OnItemsScrolled;
        else App.Current.Log.Debug("flyout: no ScrollViewer template part; a scroll will not hide a revealed row");
    }

    /// <summary>Scrolling raises no PointerExited, so the hovered row is cleared; the row now under the pointer
    /// reveals again on its next PointerEntered or PointerMoved.</summary>
    private void OnItemsScrolled(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_hoverRoot is not { } root) return;
        _hoverRoot = null;
        SyncReveal();
        Reveal(root);                                 // it may have scrolled out of realization
    }

    /// <summary>
    /// Handles PointerEntered and PointerMoved, which also bubble from the row's children.
    /// <para>The "at most one revealed row" invariant is kept on enter rather than exit, because exits can be missed:
    /// a ToolTip opening over the row, or the pointer leaving through a row clipped by the viewport, raises an exit
    /// that the bounds check in <see cref="OnRowPointerExited"/> ignores.</para>
    /// </summary>
    private void OnRowPointerEntered(object sender, PointerRoutedEventArgs e) => EnterRow((FrameworkElement)sender);

    /// <summary>Makes <paramref name="root"/> the hovered row. Separate so the harness can call it without a
    /// <see cref="PointerRoutedEventArgs"/>.</summary>
    private void EnterRow(FrameworkElement root)
    {
        if (ReferenceEquals(_hoverRoot, root)) return;
        _hoverRoot = root;
        SyncReveal();
    }

    /// <summary>The pointer is on no row. Counterpart of <see cref="EnterRow"/>.</summary>
    private void LeaveHover()
    {
        if (_hoverRoot == null) return;
        _hoverRoot = null;
        SyncReveal();
    }

    /// <summary>Moving onto a child of the row also raises an exit, so the row is only left when the pointer is
    /// outside its bounds.</summary>
    private void OnRowPointerExited(object sender, PointerRoutedEventArgs e)
    {
        var root = (FrameworkElement)sender;
        if (Inside(e, root)) return;
        if (ReferenceEquals(_hoverRoot, root)) LeaveHover();
    }

    /// <summary>
    /// The pointer left the hover region (either list or the card) without entering a row. WinUI also raises this
    /// when the hit-test target changes among descendants, so the pointer position is checked against
    /// <paramref name="sender"/>'s bounds.
    /// </summary>
    private void OnHoverRegionPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_hoverRoot == null) return;
        if (Inside(e, (FrameworkElement)sender)) return;
        LeaveHover();
    }

    /// <summary>Whether the pointer is inside <paramref name="box"/>'s bounds.</summary>
    private static bool Inside(PointerRoutedEventArgs e, FrameworkElement box)
    {
        Point p = e.GetCurrentPoint(box).Position;
        return p.X >= 0 && p.Y >= 0 && p.X < box.ActualWidth && p.Y < box.ActualHeight;
    }

    /// <summary>Deferred, because moving focus within a row raises LostFocus before GotFocus.</summary>
    private void OnListFocusChanged(object sender, RoutedEventArgs e) => DispatcherQueue.TryEnqueue(SyncFocus);

    private void SyncFocus()
    {
        if (IsClosed || Root.XamlRoot == null) return;
        object? element = FocusManager.GetFocusedElement(Root.XamlRoot);
        ContentControl? container = ContainerOf(element as DependencyObject);
        if (container?.Content is HistoryRow r) _focused = r;
        var root = container?.ContentTemplateRoot as FrameworkElement;
        // Only keyboard focus reveals actions; after a click the pointer governs the reveal.
        FrameworkElement? keyboard = element is UIElement { FocusState: FocusState.Keyboard } ? root : null;
        if (ReferenceEquals(root, _focusRoot) && ReferenceEquals(keyboard, _keyboardRoot)) return;
        _focusRoot = root;
        _keyboardRoot = keyboard;
        SyncReveal();
    }

    /// <summary>The item container (ListViewItem or GridViewItem) holding <paramref name="node"/>, if any.</summary>
    private static ContentControl? ContainerOf(DependencyObject? node)
    {
        while (node != null && node is not SelectorItem) node = VisualTreeHelper.GetParent(node);
        return node as ContentControl;
    }

    /// <summary>
    /// Brings every realized row to the reveal state implied by <see cref="_hoverRoot"/> and <see cref="_keyboardRoot"/>,
    /// so missed or out-of-order pointer events cannot strand a row revealed. Cheap: few rows are realized and
    /// <see cref="Reveal"/> skips rows already at their target.
    /// </summary>
    private void SyncReveal()
    {
        if (IsClosed) return;
        for (int i = 0; i < _rows.Count; i++)
            if (_items.ContainerFromIndex(i) is SelectorItem c && c.ContentTemplateRoot is FrameworkElement root) Reveal(root);
    }

    private void Reveal(FrameworkElement root)
    {
        if (ActionsOf(root) is not { } actions) return;
        double to = ReferenceEquals(root, _hoverRoot) || ReferenceEquals(root, _keyboardRoot) ? 1 : 0;
        // Stop first: while a fade holds its end value, the local Opacity cannot be compared meaningfully.
        StopFade(actions);
        if (Math.Abs(actions.Opacity - to) < 0.001) return;
        if (!Theme.ThemeManager.AnimationsEnabled) { actions.Opacity = to; return; }
        var fade = new DoubleAnimation { To = to, Duration = new Duration(TimeSpan.FromMilliseconds(ActionFadeMs)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(fade, actions);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var story = new Storyboard();
        story.Children.Add(fade);
        // On landing, replace the held animation with a plain local value. The identity check matters because Stop()
        // also raises Completed, and a replaced fade must not undo its replacement.
        story.Completed += (_, _) =>
        {
            if (!_actionFades.TryGetValue(actions, out Storyboard? tracked) || !ReferenceEquals(tracked, story)) return;
            _actionFades.Remove(actions);
            story.Stop();
            actions.Opacity = to;
        };
        _actionFades[actions] = story;
        story.Begin();
    }

    /// <summary>Stops any fade on these actions, keeping the current Opacity so the next fade continues from there.</summary>
    private void StopFade(FrameworkElement actions)
    {
        if (!_actionFades.Remove(actions, out Storyboard? running)) return;
        double at = actions.Opacity;
        running.Stop();
        actions.Opacity = at;
    }

    /// <summary>The action group of one item template, by FindName with a visual-tree walk as fallback.</summary>
    private static FrameworkElement? ActionsOf(FrameworkElement root) =>
        root.FindName("Actions") as FrameworkElement ?? Descendant(root, "Actions");

    private static FrameworkElement? Descendant(DependencyObject node, string name)
    {
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if (child is FrameworkElement fe && fe.Name == name) return fe;
            if (Descendant(child, name) is { } hit) return hit;
        }
        return null;
    }
}
