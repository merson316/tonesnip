#if TONESNIP_HARNESS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace ToneSnip.App.Tray;

/// <summary>Screenshot harness entry points: drive the flyout into a state without input. Empty in tonesnip.exe.</summary>
public sealed partial class HistoryFlyout
{
    /// <summary>Screenshot harness: keeps the card open when it loses the foreground.</summary>
    internal bool StaysOpen { get; set; }

    /// <summary>Screenshot harness: shows one row hovered (and optionally armed for delete) without input.</summary>
    internal void ShowRowState(int index, bool hover, bool armed)
    {
        if (index < 0 || index >= _rows.Count) return;
        if (armed) Arm(_rows[index], fromKeyboard: false);
        if (!hover || _items.ContainerFromIndex(index) is not SelectorItem container) return;
        // ListViewItemPresenter's PointerOver is native state with no XAML VisualStateGroup, so GoToState fails; paint
        // the same SurfaceLayer fill that ListViewItemBackgroundPointerOver aliases to instead.
        if (!VisualStateManager.GoToState(container, "PointerOver", false)) container.Background = TokLayer.Background;
        if (container.ContentTemplateRoot is FrameworkElement root) { _hoverRoot = root; Reveal(root); }
    }

    /// <summary>Screenshot harness: scrolls the list without animation, as a wheel would.</summary>
    internal void ScrollBy(double pixels)
    {
        HookScrollViewer();
        _scroller?.ChangeView(null, _scroller.VerticalOffset + pixels, null, true);
    }

    /// <summary>Screenshot harness: how many realized rows show their action group; at most one should.</summary>
    internal (int Revealed, int Realized) RevealedRows()
    {
        int revealed = 0, realized = 0;
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_items.ContainerFromIndex(i) is not SelectorItem c || c.ContentTemplateRoot is not FrameworkElement root) continue;
            realized++;
            if (ActionsOf(root) is { } actions && actions.Opacity > 0.5) revealed++;
        }
        return (revealed, realized);
    }

    /// <summary>Screenshot harness: simulates the pointer entering row <paramref name="index"/>, or leaving the flyout
    /// when negative, through the same <see cref="EnterRow"/> and <see cref="LeaveHover"/> paths real input uses.</summary>
    internal void HoverRowForHarness(int index)
    {
        if (index < 0) { LeaveHover(); return; }
        if (_items.ContainerFromIndex(index) is SelectorItem c && c.ContentTemplateRoot is FrameworkElement root) EnterRow(root);
    }
}
#endif
