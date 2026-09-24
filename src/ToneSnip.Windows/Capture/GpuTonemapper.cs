using System.Runtime.InteropServices;
using SharpGen.Runtime;
using ToneSnip.Core.Color;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Hdr;
using ToneSnip.Core.Tonemap;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ToneSnip.Windows.Capture;

/// <summary>Writes one row of a half-float frame being uploaded: RGBA half bits, <c>width * 4</c> values.</summary>
public delegate void HalfRowFill(int y, Span<ushort> row);

/// <summary>
/// The compute shaders of Shaders/Tonemap.hlsl on one D3D11 device, and the readback scratch its frames share. Made by
/// <see cref="ScreenCapture"/> the first time a grab keeps an HDR frame on that device, and held until the device is
/// released; each <see cref="GpuHdrFrame"/> holds it too, so a frame that outlives the capture's device (a device lost
/// mid-overlay) can still be disposed safely.
/// <para><b>Threading.</b> Every call into the immediate context is made under <see cref="Gate"/>, the capture's own
/// device lock, so a frame's readback and a capture's copy never interleave.</para>
/// <para><b>Memory.</b> The shaders and a 64-byte constant buffer are all it keeps between snips. The readback
/// staging (a few MB of system memory) is dropped once the last frame is disposed, when the overlay closes.</para>
/// </summary>
public sealed unsafe class GpuTonemapper
{
    /// <summary>Largest system-memory staging band a readback uses. A frame is read back a band at a time, so the
    /// working set never holds a second full frame, and two bands overlap the copy of one with the read of the
    /// other.</summary>
    private const int BandBytes = 4 << 20;

    /// <summary>How long a call made off the UI thread waits for the device before giving up. The same budget as the
    /// capture's own waits: nothing may park a thread for good behind a driver call that never returns.</summary>
    internal const int GateBudgetMs = 2000;

    internal ID3D11Device Device { get; }
    internal ID3D11DeviceContext Context { get; }
    /// <summary>The capture's device lock (<see cref="ScreenCapture"/>).</summary>
    internal object Gate { get; }
    private readonly ILog _log;
    private readonly ID3D11ComputeShader _tonemap, _stats, _zebra;
    private readonly ID3D11Buffer _params;

    /// <summary>The capture's reference plus one per live frame. Touched under <see cref="Gate"/>.</summary>
    private int _refs = 1;
    private int _frames;
    /// <summary>Frames disposed while another thread held <see cref="Gate"/> past its budget. Their share of
    /// <see cref="FrameReleased"/> (dropping the scratch, and perhaps the device) cannot run beside a call that may
    /// still be using those, so the next holder of the gate runs it (<see cref="Settle"/>).</summary>
    private int _pendingReleases;
    private volatile bool _lost;

    private ID3D11Texture2D? _pixelStaging;
    private ID3D11Buffer? _partials, _partialsStaging;
    private ID3D11UnorderedAccessView? _partialsUav;
    private uint _partialCount;
    /// <summary>Two BGRA bands, so the GPU copies one while the CPU reads the other.</summary>
    private readonly ID3D11Texture2D?[] _bgraBands = new ID3D11Texture2D?[2];
    private int _bandWidth, _bandRows;

    private GpuTonemapper(ID3D11Device device, object gate, ILog log)
    {
        // Own references: the capture may release its device while a frame still needs this one.
        Device = device.QueryInterface<ID3D11Device>();
        Context = Device.ImmediateContext;
        Gate = gate;
        _log = log;
        try
        {
            _tonemap = Device.CreateComputeShader(Bytecode("CSTonemap"));
            _stats = Device.CreateComputeShader(Bytecode("CSStats"));
            _zebra = Device.CreateComputeShader(Bytecode("CSZebra"));
            _params = Device.CreateBuffer(new BufferDescription((uint)sizeof(Params), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        }
        catch
        {
            _tonemap?.Dispose(); _stats?.Dispose(); _zebra?.Dispose();
            Context.Dispose(); Device.Dispose();
            throw;
        }
    }

    /// <summary>Makes the shaders on <paramref name="device"/>, whose immediate context is used only under
    /// <paramref name="gate"/>. Called with the gate held. The capture makes one per device; tools/gpu-tonemap-bench
    /// makes one per adapter.</summary>
    public static GpuTonemapper Create(ID3D11Device device, object gate, ILog log) => new(device, gate, log);

    /// <summary>One entry point's precompiled bytecode (Shaders/{entry}.cso, built from Tonemap.hlsl).</summary>
    private static byte[] Bytecode(string entry)
    {
        using Stream s = typeof(GpuTonemapper).Assembly.GetManifestResourceStream($"ToneSnip.Windows.Capture.Shaders.{entry}.cso")
                         ?? throw new InvalidOperationException($"the {entry} shader is not embedded");
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>True once the device was found lost: every frame on it is unreadable.</summary>
    public bool Lost => _lost;

    // ----- frames -----

    /// <summary>Copies the top-left <paramref name="width"/> x <paramref name="height"/> of a captured frame into a new
    /// kept texture (GPU to GPU), so the capture can close its frame at once. A monitor's frame is copied whole; a
    /// window's surface can be larger than the window. Called with <see cref="Gate"/> held.</summary>
    internal GpuHdrFrame Keep(ID3D11Texture2D source, uint width, uint height)
    {
        Settle();
        Texture2DDescription d = source.Description;
        if (d.Format != Format.R16G16B16A16_Float) throw new ArgumentException($"{d.Format} is not an fp16 frame", nameof(source));
        if (width == 0 || height == 0 || width > d.Width || height > d.Height) throw new ArgumentOutOfRangeException(nameof(width), $"{width}x{height} is not inside {d.Width}x{d.Height}");
        ID3D11Texture2D frame = Device.CreateTexture2D(FrameDescription(width, height));
        try
        {
            if (width == d.Width && height == d.Height) Context.CopyResource(frame, source);
            else Context.CopySubresourceRegion(frame, 0, 0, 0, 0, source, 0, new Vortice.Mathematics.Box(0, 0, 0, (int)width, (int)height, 1));
            return Adopt(frame);
        }
        catch { frame.Dispose(); throw; }
    }

    /// <summary>
    /// A frame whose pixels come from the CPU, one row at a time, through a system-memory staging texture: the memory
    /// harness's synthetic frames and the self-test's stress image. Takes <see cref="Gate"/> itself.
    /// </summary>
    public GpuHdrFrame Upload(int width, int height, HalfRowFill fill)
    {
        Enter();
        try
        {
            using ID3D11Texture2D staging = Device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float, (uint)width, (uint)height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Write));
            MappedSubresource m = Context.Map(staging, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
            try
            {
                for (int y = 0; y < height; y++)
                    fill(y, new Span<ushort>((byte*)m.DataPointer + (long)y * m.RowPitch, width * 4));
            }
            finally { Context.Unmap(staging, 0); }
            ID3D11Texture2D frame = Device.CreateTexture2D(FrameDescription((uint)width, (uint)height));
            try
            {
                Context.CopyResource(frame, staging);
                return Adopt(frame);
            }
            catch { frame.Dispose(); throw; }
            finally
            {
                // The runtime defers destroying the frame-sized staging texture (system memory) until the context is
                // flushed; without this the memory harness saw its commit climb by a frame every few uploads.
                staging.Dispose();
                Context.Flush();
            }
        }
        catch (Exception e) when (IsDeviceLoss(e)) { throw Lose(e); }
        finally { Monitor.Exit(Gate); }
    }

    private static Texture2DDescription FrameDescription(uint width, uint height)
        => new(Format.R16G16B16A16_Float, width, height, 1, 1, BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None);

    private GpuHdrFrame Adopt(ID3D11Texture2D frame)
    {
        var f = new GpuHdrFrame(this, frame, Device.CreateShaderResourceView(frame));
        _refs++;
        _frames++;
        return f;
    }

    /// <summary>A frame was disposed. The readback scratch goes with the last one. Called with <see cref="Gate"/>
    /// held.</summary>
    internal void FrameReleased()
    {
        if (--_frames == 0)
        {
            Trim();
            // As after a capture: flushed so the deferred destruction of the frame and the staging actually happens,
            // and trimmed so the driver returns the pools they grew.
            try
            {
                Context.Flush();
                using IDXGIDevice3 dxgi = Device.QueryInterface<IDXGIDevice3>();
                dxgi.Trim();
            }
            catch (Exception e) { _log.Debug("capture: the GPU scratch was not trimmed: " + e.Message); }
        }
        ReleaseOne();
    }

    /// <summary>A frame was disposed without <see cref="Gate"/>, which another thread held past its budget: its
    /// <see cref="FrameReleased"/> waits for the next holder of the gate. Any thread.</summary>
    internal void FrameReleasedLater()
    {
        Interlocked.Increment(ref _pendingReleases);
        _log.Warn($"capture: an HDR frame was freed while the graphics device was busy for over {GateBudgetMs} ms; its readback scratch goes with the next call");
    }

    /// <summary>Runs the <see cref="FrameReleased"/> of frames disposed while the gate was busy. Called with
    /// <see cref="Gate"/> held, by a holder of a reference, so it never frees the device from under its caller.</summary>
    internal void Settle()
    {
        for (int n = Interlocked.Exchange(ref _pendingReleases, 0); n > 0; n--) FrameReleased();
    }

    /// <summary>Drops one reference: the capture's when it releases its device, or a frame's. The last one frees the
    /// shaders and the device references. Called with <see cref="Gate"/> held.</summary>
    public void Release()
    {
        Settle();
        ReleaseOne();
    }

    private void ReleaseOne()
    {
        if (--_refs > 0) return;
        Trim();
        _params.Dispose();
        _tonemap.Dispose(); _stats.Dispose(); _zebra.Dispose();
        Context.Dispose();
        Device.Dispose();
    }

    private void Trim()
    {
        _pixelStaging?.Dispose(); _pixelStaging = null;
        _partialsUav?.Dispose(); _partials?.Dispose(); _partialsStaging?.Dispose();
        _partialsUav = null; _partials = null; _partialsStaging = null; _partialCount = 0;
        for (int i = 0; i < _bgraBands.Length; i++) { _bgraBands[i]?.Dispose(); _bgraBands[i] = null; }
        _bandWidth = _bandRows = 0;
    }

    // ----- the device lock and device loss -----

    /// <summary>Takes <see cref="Gate"/> within <see cref="GateBudgetMs"/>, or throws.</summary>
    internal void Enter()
    {
        if (!Monitor.TryEnter(Gate, GateBudgetMs)) throw new TimeoutException($"the graphics device was still busy after {GateBudgetMs} ms");
        Settle();
    }

    internal bool IsDeviceLoss(Exception e)
    {
        if (e is SharpGenException s && (s.ResultCode == Vortice.DXGI.ResultCode.DeviceRemoved || s.ResultCode == Vortice.DXGI.ResultCode.DeviceReset || s.ResultCode == Vortice.DXGI.ResultCode.DeviceHung))
            return true;
        try { return Device.DeviceRemovedReason.Failure; } catch { return true; }
    }

    private int _readoutFailures;

    /// <summary>A cursor readout failed for a reason other than a lost device. Logged once per device: the overlay asks
    /// on every mouse move, and simply shows no figure.</summary>
    internal void ReadoutFailed(Exception e)
    {
        if (Interlocked.Exchange(ref _readoutFailures, 1) == 0) _log.Warn("capture: an HDR readout failed; the overlay shows no nits for it: " + e.GetType().Name + ": " + e.Message);
    }

    /// <summary>Marks every frame on this device unreadable, logging it once, and returns the exception to throw.</summary>
    internal HdrFrameLostException Lose(Exception cause)
    {
        if (!_lost)
        {
            _lost = true;
            _log.Warn("capture: the graphics device holding the HDR frames was lost; this snip keeps its SDR image, without nits or HDR data: " + cause.Message);
        }
        return new HdrFrameLostException("the graphics device holding the HDR frame was lost", cause);
    }

    // ----- dispatches, all called with Gate held -----

    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public uint SizeX, SizeY, OriginX, OriginY, Mode, Clip;
        public float Scale, SdrWhite, PqPeak, Ks, MaxLum, InvWhite, HableRaw0;
        public uint TilesX;
        public float ZebraWhite, ZebraExposure;
    }

    /// <summary>The same derived constants as the CPU tonemappers' constructors (DesktopTonemapper, HableTonemapper,
    /// AcesTonemapper).</summary>
    private static Params ParamsFor(TonemapCurve curve)
    {
        TonemapParams p = curve.Params;
        var k = new Params();
        float sdr = MathF.Max(p.SdrWhiteNits, 1f);
        k.Scale = 80f / sdr * MathF.Max(p.Exposure, 0f);
        k.SdrWhite = sdr;
        switch (curve.Name.ToLowerInvariant())
        {
            case "desktop":
                k.Mode = 0;
                float peak = MathF.Max(p.PeakNits, sdr * 1.01f);
                k.PqPeak = Transfer.PqEncode(peak);
                k.MaxLum = Transfer.PqEncode(sdr) / k.PqPeak;
                float ksLimit = 1.5f * k.MaxLum - 0.5f;
                float knee = Math.Clamp(p.Knee, 0.01f, 1f);
                k.Clip = knee >= 1f || ksLimit <= 0f ? 1u : 0u;
                k.Ks = MathF.Min(Transfer.PqEncode(knee * sdr) / k.PqPeak, ksLimit);
                break;
            case "hable":
                k.Mode = 1;
                k.HableRaw0 = HableRaw(0f);
                float white = MathF.Max(p.PeakNits / sdr, 1.01f);
                k.InvWhite = 1f / (HableRaw(white) - k.HableRaw0);
                break;
            case "aces": k.Mode = 2; break;
            default: throw new ArgumentException($"Unknown tonemapper '{curve.Name}'. Choose one of: {string.Join(", ", TonemapperFactory.Names)}.");
        }
        return k;
    }

    private static float HableRaw(float x)
    {
        const float A = 0.15f, B = 0.50f, C = 0.10f, D = 0.20f, E = 0.02f, F = 0.30f;
        return (x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F);
    }

    private void SetParams(in Params k)
    {
        MappedSubresource m = Context.Map(_params, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        *(Params*)m.DataPointer = k;
        Context.Unmap(_params, 0);
    }

    private void Unbind()
    {
        Context.CSSetUnorderedAccessView(0, null!);
        Context.CSSetUnorderedAccessView(1, null!);
        Context.CSSetUnorderedAccessView(2, null!);
        Context.CSSetShaderResource(0, null!);
    }

    /// <summary>Tonemaps <paramref name="width"/> x <paramref name="height"/> pixels of <paramref name="src"/> from
    /// (<paramref name="left"/>, <paramref name="top"/>) into the top-left of <paramref name="dst"/>.</summary>
    internal void DispatchTonemap(TonemapCurve curve, ID3D11ShaderResourceView src, ID3D11UnorderedAccessView dst, int left, int top, int width, int height)
    {
        Params k = ParamsFor(curve);
        k.SizeX = (uint)width; k.SizeY = (uint)height; k.OriginX = (uint)left; k.OriginY = (uint)top;
        SetParams(k);
        Context.CSSetShader(_tonemap);
        Context.CSSetConstantBuffer(0, _params);
        Context.CSSetShaderResource(0, src);
        Context.CSSetUnorderedAccessView(0, dst);
        Context.Dispatch((k.SizeX + 7) / 8, (k.SizeY + 7) / 8, 1);
        Unbind();
    }

    /// <summary>
    /// Reads <paramref name="width"/> x <paramref name="height"/> packed BGRA pixels from the top-left of
    /// <paramref name="src"/> into <paramref name="dst"/> at (<paramref name="x"/>, <paramref name="y"/>), a band at a
    /// time through two staging textures: while one band is read, the GPU copies the next.
    /// </summary>
    internal void ReadBgra(ID3D11Texture2D src, int width, int height, byte[] dst, int dstWidth, int x, int y)
    {
        int rows = ReadbackBand.Rows(width, height, 4, BandBytes), bands = (height + rows - 1) / rows;
        EnsureBands(width, rows);
        void Issue(int band)
        {
            int top = band * rows, h = Math.Min(rows, height - top);
            Context.CopySubresourceRegion(_bgraBands[band & 1]!, 0, 0, 0, 0, src, 0, new Vortice.Mathematics.Box(0, top, 0, width, top + h, 1));
        }
        Issue(0);
        if (bands > 1) Issue(1);
        for (int band = 0; band < bands; band++)
        {
            ID3D11Texture2D staging = _bgraBands[band & 1]!;
            int top = band * rows, h = Math.Min(rows, height - top);
            MappedSubresource m = Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);   // waits for the copy
            try
            {
                fixed (byte* d = dst)
                    for (int r = 0; r < h; r++)
                        Buffer.MemoryCopy((byte*)m.DataPointer + (long)r * m.RowPitch, d + ((long)(y + top + r) * dstWidth + x) * 4, (long)width * 4, (long)width * 4);
            }
            finally { Context.Unmap(staging, 0); }
            if (band + 2 < bands) Issue(band + 2);
        }
    }

    /// <summary>Makes the two bands at least <paramref name="width"/> x <paramref name="rows"/>, keeping the ones there
    /// when they are big enough.</summary>
    private void EnsureBands(int width, int rows)
    {
        if (_bgraBands[0] != null && _bandWidth >= width && _bandRows >= rows) return;
        for (int i = 0; i < _bgraBands.Length; i++) { _bgraBands[i]?.Dispose(); _bgraBands[i] = null; }
        _bandWidth = _bandRows = 0;
        for (int i = 0; i < _bgraBands.Length; i++)
            _bgraBands[i] = Device.CreateTexture2D(new Texture2DDescription(Format.R32_UInt, (uint)width, (uint)rows, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        _bandWidth = width;
        _bandRows = rows;
    }

    /// <summary>Rows of a half-float readback band <paramref name="width"/> pixels wide, for a read of
    /// <paramref name="height"/> rows.</summary>
    internal static int HalfBandRows(int width, int height) => ReadbackBand.Rows(width, height, 8, BandBytes);

    /// <summary>One pixel of a frame, as half-float bits.</summary>
    internal (ushort R, ushort G, ushort B) ReadPixel(ID3D11Texture2D frame, int x, int y)
    {
        _pixelStaging ??= Device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float, 1, 1, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        Context.CopySubresourceRegion(_pixelStaging, 0, 0, 0, 0, frame, 0, new Vortice.Mathematics.Box(x, y, 0, x + 1, y + 1, 1));
        MappedSubresource m = Context.Map(_pixelStaging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            ushort* h = (ushort*)m.DataPointer;
            return (h[0], h[1], h[2]);
        }
        finally { Context.Unmap(_pixelStaging, 0); }
    }

    /// <summary>Peak and mean luminance in nits over a rectangle of <paramref name="src"/>, every pixel counted: one
    /// partial per 64x64 tile on the GPU, summed here in double.</summary>
    internal (float Peak, float Mean) Stats(ID3D11ShaderResourceView src, int left, int top, int width, int height)
    {
        uint tx = (uint)(width + 63) / 64, ty = (uint)(height + 63) / 64;
        EnsurePartials(tx * ty);
        var k = new Params { SizeX = (uint)width, SizeY = (uint)height, OriginX = (uint)left, OriginY = (uint)top, TilesX = tx };
        SetParams(k);
        Context.CSSetShader(_stats);
        Context.CSSetConstantBuffer(0, _params);
        Context.CSSetShaderResource(0, src);
        Context.CSSetUnorderedAccessView(1, _partialsUav!);
        Context.Dispatch(tx, ty, 1);
        Unbind();
        Context.CopySubresourceRegion(_partialsStaging!, 0, 0, 0, 0, _partials!, 0, new Vortice.Mathematics.Box(0, 0, 0, (int)(tx * ty * 8), 1, 1));
        MappedSubresource m = Context.Map(_partialsStaging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            float* f = (float*)m.DataPointer;
            float peak = 0; double sum = 0;
            for (int i = 0; i < tx * ty; i++) { peak = MathF.Max(peak, f[i * 2]); sum += f[i * 2 + 1]; }
            return (peak, (float)(sum / ((double)width * height)));
        }
        finally { Context.Unmap(_partialsStaging!, 0); }
    }

    private void EnsurePartials(uint count)
    {
        if (_partials != null && _partialCount >= count) return;
        _partialsUav?.Dispose(); _partials?.Dispose(); _partialsStaging?.Dispose();
        _partials = Device.CreateBuffer(new BufferDescription(count * 8, BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 8));
        _partialsUav = Device.CreateUnorderedAccessView(_partials, new UnorderedAccessViewDescription(_partials, Format.Unknown, 0, count, BufferUnorderedAccessViewFlags.None));
        _partialsStaging = Device.CreateBuffer(new BufferDescription(count * 8, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, ResourceOptionFlags.None, 0));
        _partialCount = count;
    }

    /// <summary>The zebra bits of a whole frame into <paramref name="bits"/> (<paramref name="stride"/> words a
    /// row). The mask buffer lives only for the call: the zebra is asked for rarely, and at most a few MB.</summary>
    internal void Zebra(ID3D11ShaderResourceView src, int width, int height, int stride, float sdrWhiteScRgb, float exposure, uint[] bits)
    {
        uint bytes = checked((uint)(stride * height * 4));
        using ID3D11Buffer mask = Device.CreateBuffer(new BufferDescription(bytes, BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferAllowRawViews, 0));
        using ID3D11UnorderedAccessView uav = Device.CreateUnorderedAccessView(mask, new UnorderedAccessViewDescription(mask, Format.R32_Typeless, 0, bytes / 4, BufferUnorderedAccessViewFlags.Raw));
        using ID3D11Buffer staging = Device.CreateBuffer(new BufferDescription(bytes, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, ResourceOptionFlags.None, 0));
        var k = new Params { SizeX = (uint)width, SizeY = (uint)height, TilesX = (uint)stride, ZebraWhite = sdrWhiteScRgb, ZebraExposure = exposure };
        SetParams(k);
        Context.CSSetShader(_zebra);
        Context.CSSetConstantBuffer(0, _params);
        Context.CSSetShaderResource(0, src);
        Context.CSSetUnorderedAccessView(2, uav);
        Context.Dispatch((uint)(stride + 7) / 8, (uint)(height + 7) / 8, 1);
        Unbind();
        Context.CopyResource(staging, mask);
        MappedSubresource m = Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try { fixed (uint* d = bits) Buffer.MemoryCopy((void*)m.DataPointer, d, (long)bits.Length * 4, bytes); }
        finally { Context.Unmap(staging, 0); }
    }
}
