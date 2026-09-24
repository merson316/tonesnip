using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace ToneSnip.App.Tray;

/// <summary>A row's delete: the first request arms an in-row prompt, which a second confirms.</summary>
public sealed partial class HistoryFlyout
{
    private HistoryRow? _armed;

    /// <summary>Shows the row's delete prompt (<see cref="HistoryRow.DeleteArmed"/>), which stays until Delete, Cancel,
    /// Escape, or a delete on another row.</summary>
    private void OnDelete(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) Delete(r, fromKeyboard: IsKeyboardFocused(sender)); }

    private void OnConfirmDelete(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { DeleteArmed: true } r) Delete(r, fromKeyboard: false);
    }

    private void OnCancelDelete(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { DeleteArmed: true } r) return;
        bool keyboard = IsKeyboardFocused(sender);
        Disarm();
        // The focused button has just been collapsed; keep focus in the list so the keyboard still works.
        FocusRow(r, keyboard ? FocusState.Keyboard : FocusState.Pointer);
    }

    /// <summary>Arms the prompt on the first request; deletes once armed (the prompt's Delete button, or a fresh Delete
    /// key press while it has focus, see <see cref="OnListKey"/>).</summary>
    private void Delete(HistoryRow r, bool fromKeyboard)
    {
        if (!r.DeleteArmed) { Arm(r, fromKeyboard); return; }
        Disarm();
        App.Current.History.Remove(r.Item, deleteFile: true);
    }

    private void Arm(HistoryRow r, bool fromKeyboard)
    {
        if (_armed != null && _armed != r) _armed.DeleteArmed = false;
        // Read before showing the prompt, which collapses the delete button and moves its focus.
        bool focusInRow = _focusRoot is { } f && ReferenceEquals(f.Tag, r);
        _armed = r;
        r.DeleteArmed = true;
        // The prompt's buttons need a layout pass first. Focus moves to its Delete button: keyboard focus if a key
        // armed it, pointer focus (no rectangle) if a click did.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_armed, r) || ButtonOf(r, "ConfirmDeleteBtn") is not Control confirm) return;
            if (fromKeyboard) confirm.Focus(FocusState.Keyboard);
            else if (focusInRow) confirm.Focus(FocusState.Pointer);
            // A button that was collapsed until now may have no peer yet.
            AutomationPeer? peer = FrameworkElementAutomationPeer.FromElement(confirm) ?? FrameworkElementAutomationPeer.CreatePeerForElement(confirm);
            peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.MostRecent,
                                         $"{r.ShownTitle} {r.ShownSubtitle}", "ToneSnipDeletePrompt");
        });
    }

    private void Disarm()
    {
        if (_armed == null) return;
        _armed.DeleteArmed = false;
        _armed = null;
    }

    private static bool IsKeyboardFocused(object element) => element is UIElement { FocusState: FocusState.Keyboard };

    /// <summary>A named button inside one row's realized container, if that row is realized at all.</summary>
    private FrameworkElement? ButtonOf(HistoryRow r, string name)
    {
        int at = _rows.IndexOf(r);
        if (at < 0 || _items.ContainerFromIndex(at) is not SelectorItem c || c.ContentTemplateRoot is not FrameworkElement root) return null;
        return root.FindName(name) as FrameworkElement ?? Descendant(root, name);
    }

    private void FocusRow(HistoryRow r, FocusState how)
    {
        int at = _rows.IndexOf(r);
        if (at >= 0 && _items.ContainerFromIndex(at) is Control container) container.Focus(how);
    }
}
