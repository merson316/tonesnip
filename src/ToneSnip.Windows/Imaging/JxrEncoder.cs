using ToneSnip.Core.Imaging;

namespace ToneSnip.Windows.Imaging;

/// <summary>JPEG XR from RGBA half (scRGB): what Windows Photos and the Game Bar read as HDR.</summary>
public static class JxrEncoder
{
    public static byte[] Encode(HalfImage img, bool lossless = true, float quality = 0.9f) =>
        WicEncode.Run(Wic.ContainerWmp, Wic.Pf64bppRgbaHalf, img.Width, img.Height, img.Width * 8, img.Data, bag =>
        {
            Wic.SetOption(bag, "Lossless", lossless);
            if (!lossless) Wic.SetOption(bag, "ImageQuality", Math.Clamp(quality, 0.01f, 1f));
        });
}
