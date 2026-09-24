using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ToneSnip.Core.Imaging;

namespace ToneSnip.Windows.Imaging;

/// <summary>PNG/JPEG encoding, decoding and thumbnails through WIC.</summary>
public static class Bitmaps
{
    public static byte[] EncodePng(BgraImage img) => WicEncode.Run(Wic.ContainerPng, Wic.Pf32bppBgra, img.Width, img.Height, img.Width * 4, img.Data, null);

    /// <summary>JPEG has no alpha: transparent pixels are flattened on white, as <see cref="Flatten.OnWhite"/> does, while
    /// packing to 24-bit BGR, so no flattened copy of the image is made. An opaque image is encoded as is.</summary>
    public static byte[] EncodeJpeg(BgraImage img, int quality)
    {
        var bgr = new byte[img.Width * img.Height * 3];
        for (int i = 0, o = 0; i < img.Data.Length; i += 4, o += 3)
        {
            int a = img.Data[i + 3];
            bgr[o] = Flatten.OverWhite(img.Data[i], a);
            bgr[o + 1] = Flatten.OverWhite(img.Data[i + 1], a);
            bgr[o + 2] = Flatten.OverWhite(img.Data[i + 2], a);
        }
        return WicEncode.Run(Wic.ContainerJpeg, Wic.Pf24bppBgr, img.Width, img.Height, img.Width * 3, bgr, bag => Wic.SetOption(bag, "ImageQuality", Math.Clamp(quality, 1, 100) / 100f));
    }

    /// <summary>An 8-bit grayscale JPEG, for the UltraHDR gain map.</summary>
    public static byte[] EncodeGrayJpeg(byte[] gray, int width, int height, int quality)
    {
        if (gray.Length != width * height) throw new ArgumentException("gray must be width*height bytes");
        return WicEncode.Run(Wic.ContainerJpeg, Wic.Pf8bppGray, width, height, width, gray, bag => Wic.SetOption(bag, "ImageQuality", Math.Clamp(quality, 1, 100) / 100f));
    }

    /// <summary>The largest image <see cref="Decode"/> will allocate for: 268 megapixels (1 GB of BGRA), more than three
    /// 8K monitors side by side but far short of what a forged header can ask for.</summary>
    private const ulong MaxDecodePixels = 1UL << 28;

    /// <summary>Any container WIC knows (PNG, JPEG, JPEG XR, BMP, …) to straight-alpha BGRA.</summary>
    public static BgraImage Decode(byte[] bytes)
    {
        IWICImagingFactory factory = Wic.CreateFactory();
        IStream? stream = null;
        try
        {
            stream = Wic.MemoryStream(bytes);   // inside the try: a throw out here would leak the factory RCW
            IWICBitmapDecoder? decoder = null;
            IWICBitmapFrameDecode? frame = null;
            IWICFormatConverter? conv = null;
            try
            {
                Wic.Check(factory.CreateDecoderFromStream(stream, IntPtr.Zero, Wic.DecodeCacheOnDemand, out decoder), "CreateDecoderFromStream");
                Wic.Check(decoder.GetFrame(0, out frame), "GetFrame");
                Wic.Check(factory.CreateFormatConverter(out conv), "CreateFormatConverter");
                Guid bgra = Wic.Pf32bppBgra;
                Wic.Check(conv.Initialize((IWICBitmapSource)frame, ref bgra, 0, IntPtr.Zero, 0.0, 0), "IWICFormatConverter.Initialize");
                Wic.Check(conv.GetSize(out uint w, out uint h), "GetSize");
                // The size comes from the file's own header, and the files decoded here are history entries named by a
                // user-editable JSON file, so a tiny file must not be able to demand gigabytes.
                if (w == 0 || h == 0 || (ulong)w * h > MaxDecodePixels) throw new InvalidDataException($"image header claims {w}x{h}, over the {MaxDecodePixels / 1_000_000} megapixel limit");
                var img = BgraImage.Blank((int)w, (int)h);
                GCHandle hnd = GCHandle.Alloc(img.Data, GCHandleType.Pinned);
                try { Wic.Check(conv.CopyPixels(IntPtr.Zero, w * 4, w * h * 4, hnd.AddrOfPinnedObject()), "CopyPixels"); }
                finally { hnd.Free(); }
                return img;
            }
            finally
            {
                if (conv != null) Marshal.ReleaseComObject(conv);
                if (frame != null) Marshal.ReleaseComObject(frame);
                if (decoder != null) Marshal.ReleaseComObject(decoder);
            }
        }
        finally { if (stream != null) Marshal.ReleaseComObject(stream); Marshal.ReleaseComObject(factory); }
    }

    /// <summary>Nearest-neighbour downscale so the longest edge is at most <paramref name="maxEdge"/> (history thumbnails).</summary>
    public static BgraImage Thumbnail(BgraImage img, int maxEdge)
    {
        int step = (int)Math.Ceiling(Math.Max(img.Width, img.Height) / (double)maxEdge);
        if (step <= 1) return img;
        int w = Math.Max(1, img.Width / step), h = Math.Max(1, img.Height / step);
        var d = new byte[w * h * 4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) Buffer.BlockCopy(img.Data, ((y * step) * img.Width + x * step) * 4, d, (y * w + x) * 4, 4);
        return new BgraImage(w, h, d);
    }
}
