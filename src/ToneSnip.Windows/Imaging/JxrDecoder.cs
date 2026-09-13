using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ToneSnip.Core.Imaging;

namespace ToneSnip.Windows.Imaging;

public static class JxrDecoder
{
    public static HalfImage DecodeHalf(byte[] jxr)
    {
        IWICImagingFactory factory = Wic.CreateFactory();
        try
        {
            IStream? stream = null;
            IWICBitmapDecoder? decoder = null;
            IWICBitmapFrameDecode? frame = null;
            try
            {
                stream = Wic.MemoryStream(jxr);
                Wic.Check(factory.CreateDecoderFromStream(stream, IntPtr.Zero, Wic.DecodeCacheOnDemand, out decoder), "CreateDecoderFromStream");
                Wic.Check(decoder.GetFrame(0, out frame), "GetFrame");
                Wic.Check(frame.GetSize(out uint w, out uint h), "GetSize");
                Wic.Check(frame.GetPixelFormat(out Guid pf), "GetPixelFormat");
                if (pf != Wic.Pf64bppRgbaHalf) throw new InvalidOperationException($"decoded pixel format is {pf}, not 64bppRGBAHalf");
                var img = new HalfImage((int)w, (int)h);
                GCHandle hnd = GCHandle.Alloc(img.Data, GCHandleType.Pinned);
                try { Wic.Check(frame.CopyPixels(IntPtr.Zero, w * 8, w * h * 8, hnd.AddrOfPinnedObject()), "CopyPixels"); }
                finally { hnd.Free(); }
                return img;
            }
            finally
            {
                if (frame != null) Marshal.ReleaseComObject(frame);
                if (decoder != null) Marshal.ReleaseComObject(decoder);
                if (stream != null) Marshal.ReleaseComObject(stream);
            }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }
}
