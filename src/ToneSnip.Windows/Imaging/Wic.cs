using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ToneSnip.Windows.Imaging;

/// <summary>
/// Just enough of the Windows Imaging Component for the app's encoders and decoders. Hand-written COM interop; vtable
/// order follows wincodec.h exactly, and unused slots are declared so the ones after them line up.
/// </summary>
internal static class Wic
{
    public static readonly Guid ClsidImagingFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    public static readonly Guid ContainerWmp = new("57a37caa-367a-4540-916b-f183c5093a4b");
    public static readonly Guid ContainerJpeg = new("19e4a5aa-5662-4fc5-a0c0-1758028e1057");
    public static readonly Guid ContainerPng = new("1b7cfaf4-713f-473c-bbcd-6137425faeaf");
    public static readonly Guid Pf64bppRgbaHalf = new("6fddc324-4e03-4bfe-b185-3d77768dc93a");
    public static readonly Guid Pf32bppBgra = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");
    public static readonly Guid Pf24bppBgr = new("6fddc324-4e03-4bfe-b185-3d77768dc90c");
    public static readonly Guid Pf8bppGray = new("6fddc324-4e03-4bfe-b185-3d77768dc908");
    public const uint CacheOptionNo = 2;      // WICBitmapEncoderNoCache
    public const uint DecodeCacheOnDemand = 0;

    [DllImport("shlwapi.dll")] private static extern IStream SHCreateMemStream(IntPtr pInit, uint cbInit);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, out IntPtr ppv);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint coInit);
    [ThreadStatic] private static bool _comReady;
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    /// <summary>
    /// Ensures this thread is COM-initialized before any CoCreateInstance call. Multithreaded, because callers are mostly
    /// pool threads and an STA would stick to them; a thread already in an apartment (the UI thread) keeps it.
    /// Never calls CoUninitialize, which could tear COM down under other code on an arbitrary caller's thread.
    /// </summary>
    private static void EnsureCom()
    {
        if (_comReady) return;
        int hr = CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */);
        if (hr != 0 /* S_OK */ && hr != 1 /* S_FALSE */ && hr != RpcEChangedMode) Check(hr, "CoInitializeEx");
        _comReady = true;
    }

    public static IWICImagingFactory CreateFactory()
    {
        EnsureCom();
        Guid clsid = ClsidImagingFactory, iid = typeof(IWICImagingFactory).GUID;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, ref iid, out IntPtr p);
        Check(hr, "CoCreateInstance(WICImagingFactory)");
        object o = Marshal.GetObjectForIUnknown(p);
        Marshal.Release(p);
        return (IWICImagingFactory)o;
    }

    public static IStream MemoryStream(byte[]? initial = null)
    {
        IStream? s;
        if (initial == null) { s = SHCreateMemStream(IntPtr.Zero, 0); }
        else
        {
            GCHandle h = GCHandle.Alloc(initial, GCHandleType.Pinned);
            try { s = SHCreateMemStream(h.AddrOfPinnedObject(), (uint)initial.Length); } finally { h.Free(); }
        }
        return s ?? throw new InvalidOperationException("SHCreateMemStream failed");
    }

    public static byte[] ReadAll(IStream s)
    {
        s.Seek(0, 2 /* STREAM_SEEK_END */, IntPtr.Zero);
        var stat = new System.Runtime.InteropServices.ComTypes.STATSTG(); s.Stat(out stat, 1 /* STATFLAG_NONAME */);
        var buf = new byte[stat.cbSize];
        s.Seek(0, 0 /* STREAM_SEEK_SET */, IntPtr.Zero);
        IntPtr readPtr = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            // A byte array is pinned for the call rather than copied, so the whole stream is read straight into the
            // result: a memory stream returns it all at once. Only a short read needs a bounce buffer, since Read has no
            // offset, and that one stays under the Large Object Heap's threshold.
            int total = 0;
            if (buf.Length > 0)
            {
                s.Read(buf, buf.Length, readPtr);
                total = Math.Max(0, Marshal.ReadInt32(readPtr));
            }
            byte[]? chunk = null;
            while (total < buf.Length)
            {
                chunk ??= new byte[Math.Min(64 * 1024, buf.Length - total)];
                s.Read(chunk, Math.Min(chunk.Length, buf.Length - total), readPtr);
                int n = Marshal.ReadInt32(readPtr);
                if (n <= 0) break;
                Buffer.BlockCopy(chunk, 0, buf, total, n); total += n;
            }
            if (total != buf.Length) throw new InvalidOperationException($"short read from memory stream: {total} of {buf.Length}");
        }
        finally { Marshal.FreeHGlobal(readPtr); }
        return buf;
    }

    public static void Check(int hr, string what) { if (hr < 0) throw new COMException($"{what} failed", hr); }

    /// <summary>Sets one named option on an encoder frame's property bag (VT_BOOL for bool, VT_R4 for float).</summary>
    /// <remarks>The VARIANT and the name are marshalled by hand: runtime marshalling of an <c>object[]</c> as a VARIANT
    /// array tries to export a type library and fails with TYPE_E_LIBNOTREGISTERED.</remarks>
    public static void SetOption(IPropertyBag2 bag, string name, object value)
    {
        const int VariantSize = 24;   // x64 VARIANT: vt (2) + reserved (6) + value union (8) + BRECORD tail (8)
        IntPtr pname = Marshal.StringToCoTaskMemUni(name);
        IntPtr pvar = Marshal.AllocCoTaskMem(VariantSize);
        IntPtr pprop = Marshal.AllocCoTaskMem(Marshal.SizeOf<PROPBAG2>());
        try
        {
            for (int i = 0; i < VariantSize; i++) Marshal.WriteByte(pvar, i, 0);
            var prop = new PROPBAG2 { pstrName = pname };
            if (value is bool b) { prop.vt = 11 /* VT_BOOL */; Marshal.WriteInt16(pvar, 8, (short)(b ? -1 : 0)); }
            else { prop.vt = 4 /* VT_R4 */; Marshal.WriteInt32(pvar, 8, BitConverter.SingleToInt32Bits(Convert.ToSingle(value))); }
            Marshal.WriteInt16(pvar, 0, (short)prop.vt);
            Marshal.StructureToPtr(prop, pprop, false);   // a managed PROPBAG2[] would be marshalled as a SAFEARRAY (same typelib failure)
            Check(bag.Write(1, pprop, pvar), $"IPropertyBag2.Write({name})");
        }
        finally { Marshal.FreeCoTaskMem(pprop); Marshal.FreeCoTaskMem(pvar); Marshal.FreeCoTaskMem(pname); }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPBAG2
{
    public uint dwType; public ushort vt; public ushort cfType; public uint dwHint;
    public IntPtr pstrName;   // LPOLESTR, allocated by the caller so the struct stays blittable
    public Guid clsid;
}


[ComImport, Guid("22F55882-280B-11d0-A8A9-00A0C90C2004"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyBag2
{
    // Arrays are passed as raw pointers: the built-in interop marshals a managed array parameter on a COM interface as a
    // SAFEARRAY, which for a user-defined struct needs a registered type library and fails with TYPE_E_LIBNOTREGISTERED.
    [PreserveSig] int Read(uint cProperties, IntPtr pPropBag, IntPtr pErrLog, IntPtr pvarValue, IntPtr phrError);
    [PreserveSig] int Write(uint cProperties, IntPtr pPropBag, IntPtr pvarValue);   // pPropBag: PROPBAG2[cProperties]; pvarValue: VARIANT[cProperties]
    [PreserveSig] int CountProperties(out uint pcProperties);
    [PreserveSig] int GetPropertyInfo(uint iProperty, uint cProperties, IntPtr pPropBag, out uint pcProperties);
    [PreserveSig] int LoadObject([MarshalAs(UnmanagedType.LPWStr)] string pstrName, uint dwHint, IntPtr pUnkObject, IntPtr pErrLog);
}

[ComImport, Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapSource
{
    [PreserveSig] int GetSize(out uint width, out uint height);
    [PreserveSig] int GetPixelFormat(out Guid format);
    [PreserveSig] int GetResolution(out double dpiX, out double dpiY);
    [PreserveSig] int CopyPalette(IntPtr palette);
    [PreserveSig] int CopyPixels(IntPtr prc, uint stride, uint bufferSize, IntPtr buffer);
}

[ComImport, Guid("3B16811B-6A43-4ec9-A813-3D930C13B940"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapFrameDecode
{
    // IWICBitmapSource
    [PreserveSig] int GetSize(out uint width, out uint height);
    [PreserveSig] int GetPixelFormat(out Guid format);
    [PreserveSig] int GetResolution(out double dpiX, out double dpiY);
    [PreserveSig] int CopyPalette(IntPtr palette);
    [PreserveSig] int CopyPixels(IntPtr prc, uint stride, uint bufferSize, IntPtr buffer);
    // IWICBitmapFrameDecode
    [PreserveSig] int GetMetadataQueryReader(out IntPtr reader);
    [PreserveSig] int GetColorContexts(uint count, IntPtr contexts, out uint actual);
    [PreserveSig] int GetThumbnail(out IntPtr thumbnail);
}

[ComImport, Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICFormatConverter
{
    // IWICBitmapSource
    [PreserveSig] int GetSize(out uint width, out uint height);
    [PreserveSig] int GetPixelFormat(out Guid format);
    [PreserveSig] int GetResolution(out double dpiX, out double dpiY);
    [PreserveSig] int CopyPalette(IntPtr palette);
    [PreserveSig] int CopyPixels(IntPtr prc, uint stride, uint bufferSize, IntPtr buffer);
    // IWICFormatConverter
    // The source is typed, not IntPtr, so the marshaller QIs for IWICBitmapSource: the frame's IUnknown pointer is a
    // different vtable, and Initialize fail-fasts inside WindowsCodecs when called through it.
    [PreserveSig] int Initialize(IWICBitmapSource source, ref Guid dstFormat, uint dither, IntPtr palette, double alphaThreshold, uint paletteType);
    [PreserveSig] int CanConvert(ref Guid src, ref Guid dst, out int canConvert);
}

[ComImport, Guid("9EDDE9E7-8DEE-47ea-99DF-E6FAF2ED44BF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapDecoder
{
    [PreserveSig] int QueryCapability(IStream stream, out uint capability);
    [PreserveSig] int Initialize(IStream stream, uint cacheOptions);
    [PreserveSig] int GetContainerFormat(out Guid format);
    [PreserveSig] int GetDecoderInfo(out IntPtr info);
    [PreserveSig] int CopyPalette(IntPtr palette);
    [PreserveSig] int GetMetadataQueryReader(out IntPtr reader);
    [PreserveSig] int GetPreview(out IntPtr preview);
    [PreserveSig] int GetColorContexts(uint count, IntPtr contexts, out uint actual);
    [PreserveSig] int GetThumbnail(out IntPtr thumbnail);
    [PreserveSig] int GetFrameCount(out uint count);
    [PreserveSig] int GetFrame(uint index, out IWICBitmapFrameDecode frame);
}

[ComImport, Guid("00000105-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapFrameEncode
{
    [PreserveSig] int Initialize(IPropertyBag2? options);
    [PreserveSig] int SetSize(uint width, uint height);
    [PreserveSig] int SetResolution(double dpiX, double dpiY);
    [PreserveSig] int SetPixelFormat(ref Guid format);
    [PreserveSig] int SetColorContexts(uint count, IntPtr contexts);
    [PreserveSig] int SetPalette(IntPtr palette);
    [PreserveSig] int SetThumbnail(IntPtr thumbnail);
    [PreserveSig] int WritePixels(uint lineCount, uint stride, uint bufferSize, IntPtr pixels);
    [PreserveSig] int WriteSource(IntPtr source, IntPtr rect);
    [PreserveSig] int Commit();
    [PreserveSig] int GetMetadataQueryWriter(out IntPtr writer);
}

[ComImport, Guid("00000103-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapEncoder
{
    [PreserveSig] int Initialize(IStream stream, uint cacheOption);
    [PreserveSig] int GetContainerFormat(out Guid format);
    [PreserveSig] int GetEncoderInfo(out IntPtr info);
    [PreserveSig] int SetColorContexts(uint count, IntPtr contexts);
    [PreserveSig] int SetPalette(IntPtr palette);
    [PreserveSig] int SetThumbnail(IntPtr thumbnail);
    [PreserveSig] int SetPreview(IntPtr preview);
    [PreserveSig] int CreateNewFrame(out IWICBitmapFrameEncode frame, ref IPropertyBag2? options);
    [PreserveSig] int Commit();
    [PreserveSig] int GetMetadataQueryWriter(out IntPtr writer);
}

[ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICImagingFactory
{
    [PreserveSig] int CreateDecoderFromFilename([MarshalAs(UnmanagedType.LPWStr)] string filename, IntPtr vendor, uint access, uint options, out IWICBitmapDecoder decoder);
    [PreserveSig] int CreateDecoderFromStream(IStream stream, IntPtr vendor, uint options, out IWICBitmapDecoder decoder);
    [PreserveSig] int CreateDecoderFromFileHandle(IntPtr handle, IntPtr vendor, uint options, out IWICBitmapDecoder decoder);
    [PreserveSig] int CreateComponentInfo(ref Guid clsid, out IntPtr info);
    [PreserveSig] int CreateDecoder(ref Guid container, IntPtr vendor, out IWICBitmapDecoder decoder);
    [PreserveSig] int CreateEncoder(ref Guid container, IntPtr vendor, out IWICBitmapEncoder encoder);
    [PreserveSig] int CreatePalette(out IntPtr palette);
    [PreserveSig] int CreateFormatConverter(out IWICFormatConverter converter);
    [PreserveSig] int CreateBitmapScaler(out IntPtr scaler);
    [PreserveSig] int CreateBitmapClipper(out IntPtr clipper);
    [PreserveSig] int CreateBitmapFlipRotator(out IntPtr rotator);
    [PreserveSig] int CreateStream(out IntPtr stream);
    [PreserveSig] int CreateColorContext(out IntPtr context);
    [PreserveSig] int CreateColorTransformer(out IntPtr transformer);
    [PreserveSig] int CreateBitmap(uint width, uint height, ref Guid format, uint option, out IntPtr bitmap);
    [PreserveSig] int CreateBitmapFromSource(IntPtr source, uint option, out IntPtr bitmap);
    [PreserveSig] int CreateBitmapFromSourceRect(IntPtr source, uint x, uint y, uint w, uint h, out IntPtr bitmap);
    [PreserveSig] int CreateBitmapFromMemory(uint width, uint height, ref Guid format, uint stride, uint size, IntPtr buffer, out IntPtr bitmap);
    [PreserveSig] int CreateBitmapFromHBITMAP(IntPtr hbitmap, IntPtr hpalette, uint options, out IntPtr bitmap);
    [PreserveSig] int CreateBitmapFromHICON(IntPtr hicon, out IntPtr bitmap);
    [PreserveSig] int CreateComponentEnumerator(uint types, uint options, out IntPtr enumerator);
    [PreserveSig] int CreateFastMetadataEncoderFromDecoder(IntPtr decoder, out IntPtr encoder);
    [PreserveSig] int CreateFastMetadataEncoderFromFrameDecode(IntPtr frame, out IntPtr encoder);
    [PreserveSig] int CreateQueryWriter(ref Guid format, IntPtr vendor, out IntPtr writer);
    [PreserveSig] int CreateQueryWriterFromReader(IntPtr reader, IntPtr vendor, out IntPtr writer);
}

/// <summary>Shared encode loop: container + pixel format + options → bytes.</summary>
internal static class WicEncode
{
    /// <summary><paramref name="pixels"/> is pinned as-is (byte[], ushort[], …) and handed to WIC by address, so a caller
    /// whose native layout already matches <paramref name="pixelFormat"/> (e.g. a <c>HalfImage</c>'s <c>ushort[]</c>) need
    /// not copy into a <c>byte[]</c> first.</summary>
    public static byte[] Run(Guid container, Guid pixelFormat, int width, int height, int stride, Array pixels, Action<IPropertyBag2>? options)
    {
        IWICImagingFactory factory = Wic.CreateFactory();
        try
        {
            IStream? stream = null;
            IWICBitmapEncoder? encoder = null;
            IWICBitmapFrameEncode? frame = null;
            IPropertyBag2? bag = null;
            try
            {
                stream = Wic.MemoryStream();
                Guid c = container;
                Wic.Check(factory.CreateEncoder(ref c, IntPtr.Zero, out encoder), "CreateEncoder");
                Wic.Check(encoder.Initialize(stream, Wic.CacheOptionNo), "IWICBitmapEncoder.Initialize");
                Wic.Check(encoder.CreateNewFrame(out frame, ref bag), "CreateNewFrame");
                if (bag != null) options?.Invoke(bag);
                else if (options != null) throw new InvalidOperationException("encoder returned no property bag; options cannot be applied");
                Wic.Check(frame.Initialize(bag), "IWICBitmapFrameEncode.Initialize");
                Wic.Check(frame.SetSize((uint)width, (uint)height), "SetSize");
                Guid pf = pixelFormat;
                Wic.Check(frame.SetPixelFormat(ref pf), "SetPixelFormat");
                if (pf != pixelFormat) throw new InvalidOperationException($"WIC cannot encode this container in the requested pixel format ({pixelFormat})");
                GCHandle h = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try { Wic.Check(frame.WritePixels((uint)height, (uint)stride, checked((uint)((long)stride * height)), h.AddrOfPinnedObject()), "WritePixels"); }
                finally { h.Free(); }
                Wic.Check(frame.Commit(), "frame Commit");
                Wic.Check(encoder.Commit(), "encoder Commit");
                return Wic.ReadAll(stream);
            }
            finally
            {
                if (bag != null) Marshal.ReleaseComObject(bag);
                if (frame != null) Marshal.ReleaseComObject(frame);
                if (encoder != null) Marshal.ReleaseComObject(encoder);
                if (stream != null) Marshal.ReleaseComObject(stream);
            }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }
}
