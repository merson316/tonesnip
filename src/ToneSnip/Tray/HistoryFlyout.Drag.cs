using ToneSnip.Core.Output;
using ToneSnip.Windows.Overlay;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;

namespace ToneSnip.App.Tray;

/// <summary>A saved snip dragged out of the card as its file, as from Explorer: into a browser or chat to upload it, a
/// terminal to paste its path, or a folder to copy it.</summary>
/// <remarks>The drag is WinUI's own: CanDrag on each item template's root, bound to <see cref="HistoryRow.CanDrag"/>, so
/// a click, a double click and the context menu are untouched until the pointer passes the system drag threshold. The
/// file goes as a storage item, which a Win32 target reads as an ordinary file drop with the real path.
/// <c>StorageFile.GetFileFromPathAsync</c> reaches files outside the package in both builds, as the thumbnails show
/// (<see cref="Controls.ThumbnailLoader"/>), so no OLE drag of its own is needed. Copy is the only operation offered and
/// the item is read-only, so a drop on Explorer copies whatever keys are held and no target can move the file.</remarks>
public sealed partial class HistoryFlyout
{
    /// <summary>How long a drag may outlast the mouse button before the watch takes it as over. DropCompleted ends it
    /// well before; this only stops a lost DropCompleted from keeping the card open for good. A touch or pen drag holds
    /// no button, so it is let go after this too, which costs nothing: nothing takes the foreground until the drop.</summary>
    private const double DragGraceMs = 2000;

    /// <summary>A drag from a row is under way, so the card stays open when it loses the foreground.</summary>
    private bool _dragging;
    /// <summary>When the watch first saw no mouse button held during the drag; null while one is.</summary>
    private DateTime? _dragReleased;

    /// <summary>Fills the drag with the row's saved file once the threshold is passed. The file is looked for here, off
    /// the UI thread: a row whose file has gone starts no drag and is probed, which drops it.</summary>
    private async void OnRowDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (RowOf(sender) is not { } r || HistoryList.DragFile(r.Item.Entry) is not { } path) { args.Cancel = true; return; }
        args.AllowedOperations = DataPackageOperation.Copy;
        args.Data.RequestedOperation = DataPackageOperation.Copy;
        _dragging = true;
        _dragReleased = null;
        DragOperationDeferral deferral = args.GetDeferral();
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            args.Data.SetStorageItems(new[] { file }, readOnly: true);
            // The bitmap the row already shows, held under the pointer as Explorer holds an icon. Without one (still
            // decoding, or the placeholder) the framework draws the row itself.
            if (r.ThumbSource is BitmapImage { PixelWidth: > 0 } thumb)
                args.DragUI.SetContentFromBitmapImage(thumb, new Point(thumb.PixelWidth / 2.0, thumb.PixelHeight / 2.0));
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            EndDrag();
            if (ex is not FileNotFoundException) App.Current.Log.Warn("history drag: " + ex.Message);
            Unreadable(r.Item);
        }
        finally { deferral.Complete(); }
    }

    private void OnRowDropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        App.Current.Log.Debug($"flyout: drag ended ({args.DropResult})");
        EndDrag();
    }

    /// <summary>The drag is over. Clears the hover, since the pointer left the row with no exit raised; the watch then
    /// closes the card on its next tick if the drop gave another window the foreground.</summary>
    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        _dragReleased = null;
        LeaveHover();
    }

    /// <summary>For the watch: whether a drag is still holding the card open. A drag whose buttons have been up for
    /// <see cref="DragGraceMs"/> is taken as over.</summary>
    private bool DragHolds()
    {
        if (!_dragging) return false;
        if (Win32.MouseButtonHeld()) { _dragReleased = null; return true; }
        _dragReleased ??= DateTime.UtcNow;
        if ((DateTime.UtcNow - _dragReleased.Value).TotalMilliseconds < DragGraceMs) return true;
        App.Current.Log.Debug("flyout: a drag ended without DropCompleted");
        EndDrag();
        return false;
    }
}
