using ToneSnip.Core.Imaging;

namespace ToneSnip.Windows.Imaging;

public static class JpegEncoder
{
    /// <summary>BGRA in, JPEG out; alpha is ignored (flatten beforehand if it matters).</summary>
    public static byte[] EncodeBgra(BgraImage img, int quality)
    {
        var bgr = new byte[img.Width * img.Height * 3];
        for (int i = 0, o = 0; i < img.Data.Length; i += 4, o += 3) { bgr[o] = img.Data[i]; bgr[o + 1] = img.Data[i + 1]; bgr[o + 2] = img.Data[i + 2]; }
        return WicEncode.Run(Wic.ContainerJpeg, Wic.Pf24bppBgr, img.Width, img.Height, img.Width * 3, bgr, bag => Wic.SetOption(bag, "ImageQuality", Math.Clamp(quality, 1, 100) / 100f));
    }

    public static byte[] EncodeGray(byte[] gray, int width, int height, int quality)
    {
        if (gray.Length != width * height) throw new ArgumentException("gray must be width*height bytes");
        return WicEncode.Run(Wic.ContainerJpeg, Wic.Pf8bppGray, width, height, width, gray, bag => Wic.SetOption(bag, "ImageQuality", Math.Clamp(quality, 1, 100) / 100f));
    }
}
