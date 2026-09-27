using ToneSnip.Core.Imaging;

namespace ToneSnip.Windows.Imaging;

/// <summary>JPEG XR from RGBA half (scRGB): what Windows Photos and the Game Bar read as HDR.</summary>
public static class JxrEncoder
{
    public static byte[] Encode(HalfImage img, bool lossless = true, float quality = 0.9f) =>
        WicEncode.Run(Wic.ContainerWmp, Wic.Pf64bppRgbaHalf, img.Width, img.Height, img.Width * 8, img.Data, Options(lossless, quality));

    /// <summary><see cref="Encode"/> written straight into a new file at <paramref name="path"/>, replacing one there,
    /// so the encoded bytes are not held in memory.</summary>
    public static void Save(HalfImage img, bool lossless, float quality, string path) =>
        WicEncode.ToFile(path, Wic.ContainerWmp, Wic.Pf64bppRgbaHalf, img.Width, img.Height, img.Width * 8, img.Data, Options(lossless, quality));

    private static Action<IPropertyBag2> Options(bool lossless, float quality) => bag =>
    {
        Wic.SetOption(bag, "Lossless", lossless);
        if (!lossless) Wic.SetOption(bag, "ImageQuality", Math.Clamp(quality, 0.01f, 1f));
    };
}
