using ToneSnip.Core.Imaging;
using ToneSnip.Core.Output;
using ToneSnip.Windows.Imaging;

namespace ToneSnip.App.Output;

public static class ImageSaver
{
    /// <summary><paramref name="png"/> is reused when already encoded for the clipboard, so a PNG snip is encoded once.</summary>
    public static string Save(BgraImage image, string folder, string format, int jpegQuality, DateTime local, byte[]? png = null)
    {
        Directory.CreateDirectory(folder);
        string ext = format == "jpeg" ? "jpg" : "png";
        string path = FileNaming.Resolve(folder, FileNaming.Build(local, ext), File.Exists);
        File.WriteAllBytes(path, format == "jpeg" ? Bitmaps.EncodeJpeg(image, jpegQuality) : png ?? Bitmaps.EncodePng(image));
        return path;
    }
}
