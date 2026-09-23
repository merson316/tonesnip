using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ToneSnip.App.Controls;

/// <summary>Loads a history thumbnail PNG for the Recent flyout and the toast card.</summary>
internal static class ThumbnailLoader
{
    /// <summary>
    /// Hands <paramref name="show"/> the thumbnail decoded at <paramref name="decodeWidth"/> device pixels, so the
    /// full-size file is never held, or null when there is no path. The bitmap is shown empty and filled
    /// asynchronously from a stream; it is shown before the fill starts, so <paramref name="failed"/> can tell it from a
    /// newer one. <paramref name="failed"/> runs on the UI thread, where the WinRT awaits resume, with a null exception
    /// when the file does not exist: that is found by the asynchronous open, not by a File.Exists on the UI thread,
    /// which can stall on a network share or a waking disk.
    /// </summary>
    public static void Load(string? path, int decodeWidth, Action<BitmapImage?> show, Action<BitmapImage, Exception?> failed)
    {
        BitmapImage bmp;
        try
        {
            if (string.IsNullOrEmpty(path)) { show(null); return; }
            bmp = new BitmapImage { DecodePixelType = DecodePixelType.Physical, DecodePixelWidth = decodeWidth };
        }
        catch { show(null); return; }
        show(bmp);
        _ = FillAsync(bmp, path, failed);
    }

    private static async Task FillAsync(BitmapImage bmp, string path, Action<BitmapImage, Exception?> failed)
    {
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            using IRandomAccessStreamWithContentType stream = await file.OpenReadAsync();
            await bmp.SetSourceAsync(stream);
        }
        catch (FileNotFoundException) { failed(bmp, null); }
        catch (Exception ex) { failed(bmp, ex); }
    }
}
