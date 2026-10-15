using ToneSnip.Core.Record;
using ToneSnip.Core.Hdr;
using ToneSnip.Windows.Capture;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ToneSnip.Windows.Record;

/// <summary>
/// Turns a captured frame into the NV12 a recording's encoder takes, on the graphics card. The recorded area is copied
/// out of the capture's surface first, so the frame can go back to the capture system as soon as this returns; CSVideo
/// then tonemaps it (an HDR frame) or takes it as it is (an SDR one) and writes the two planes, and only those, 12 bits
/// a pixel, are read back.
/// <para>One per recording, sized to its area. Every call is made under the device lock its
/// <see cref="GpuTonemapper"/> was made with.</para>
/// </summary>
internal sealed unsafe class VideoConverter : IDisposable
{
    private readonly GpuTonemapper _gpu;
    private readonly int _width, _height;
    private readonly ID3D11Texture2D _source;
    private readonly ID3D11ShaderResourceView _sourceView;
    private readonly ID3D11Texture2D _luma, _chroma, _lumaStaging, _chromaStaging;
    private readonly ID3D11UnorderedAccessView _lumaView, _chromaView;

    /// <param name="format">The capture's pixel format: R16G16B16A16_Float for an HDR monitor, B8G8R8A8_UNorm for an
    /// SDR one.</param>
    public VideoConverter(GpuTonemapper gpu, Format format, int width, int height)
    {
        if ((width & 1) != 0 || (height & 1) != 0 || width <= 0 || height <= 0) throw new ArgumentException($"{width}x{height}: a video frame has even sides", nameof(width));
        _gpu = gpu;
        _width = width;
        _height = height;
        ID3D11Device d = gpu.Device;
        uint words = (uint)GpuTonemapper.VideoWords(width);
        var made = new List<IDisposable>();
        T Keep<T>(T item) where T : IDisposable { made.Add(item); return item; }
        try
        {
            _source = Keep(d.CreateTexture2D(new Texture2DDescription(format, (uint)width, (uint)height, 1, 1, BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None)));
            _sourceView = Keep(d.CreateShaderResourceView(_source));
            _luma = Keep(d.CreateTexture2D(new Texture2DDescription(Format.R32_UInt, words, (uint)height, 1, 1, BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None)));
            _chroma = Keep(d.CreateTexture2D(new Texture2DDescription(Format.R32_UInt, words, (uint)height / 2, 1, 1, BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None)));
            _lumaView = Keep(d.CreateUnorderedAccessView(_luma));
            _chromaView = Keep(d.CreateUnorderedAccessView(_chroma));
            _lumaStaging = Keep(d.CreateTexture2D(new Texture2DDescription(Format.R32_UInt, words, (uint)height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read)));
            _chromaStaging = Keep(d.CreateTexture2D(new Texture2DDescription(Format.R32_UInt, words, (uint)height / 2, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read)));
        }
        catch
        {
            foreach (IDisposable item in made) item.Dispose();
            throw;
        }
    }

    /// <summary>Bytes one converted frame takes.</summary>
    public int FrameBytes => Nv12.Size(_width, _height);

    /// <summary>
    /// Converts the area of <paramref name="frame"/> at <paramref name="left"/>, <paramref name="top"/> (the
    /// converter's size) into <paramref name="nv12"/>, <see cref="FrameBytes"/> long, rows packed tight. An HDR frame is
    /// tonemapped with <paramref name="curve"/>; an SDR frame takes null. Waits for the graphics card.
    /// </summary>
    public void Convert(ID3D11Texture2D frame, int left, int top, TonemapCurve? curve, byte* nv12)
    {
        ID3D11DeviceContext context = _gpu.Context;
        context.CopySubresourceRegion(_source, 0, 0, 0, 0, frame, 0, new Vortice.Mathematics.Box(left, top, 0, left + _width, top + _height, 1));
        _gpu.DispatchVideo(curve, _sourceView, _lumaView, _chromaView, 0, 0, _width, _height);
        context.CopyResource(_lumaStaging, _luma);
        context.CopyResource(_chromaStaging, _chroma);
        Read(_lumaStaging, _height, nv12);
        Read(_chromaStaging, _height / 2, nv12 + (long)_width * _height);
    }

    private void Read(ID3D11Texture2D staging, int rows, byte* dst)
    {
        MappedSubresource m = _gpu.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            for (int y = 0; y < rows; y++)
                Buffer.MemoryCopy((byte*)m.DataPointer + (long)y * m.RowPitch, dst + (long)y * _width, _width, _width);
        }
        finally { _gpu.Context.Unmap(staging, 0); }
    }

    public void Dispose()
    {
        _lumaStaging.Dispose(); _chromaStaging.Dispose();
        _lumaView.Dispose(); _chromaView.Dispose();
        _luma.Dispose(); _chroma.Dispose();
        _sourceView.Dispose(); _source.Dispose();
    }
}
