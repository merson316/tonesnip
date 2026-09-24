using ToneSnip.Core.Color;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Hdr;
using ToneSnip.Core.Imaging;
using ToneSnip.Core.Tonemap;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ToneSnip.Windows.Capture;

/// <summary>
/// A captured HDR frame kept on the graphics card: the half-float pixels never come back whole. The overlay's readouts
/// read one pixel or two numbers, the exposure preview a tonemapped image, and the snip a crop of the selection.
/// <para>Disposed when the snip is built (or cancelled), which frees the texture at once; the only thing a snip leaves
/// in the frame pool is its BGRA image.</para>
/// <para><b>Threading.</b> Any thread. Each call takes the capture's device lock; the two the UI thread makes on
/// every mouse move (<see cref="TrySample"/>, <see cref="TryStats"/>) do not wait for it at all, and report false
/// when a capture holds it, so the pointer never stalls behind a GPU copy. Nor do they throw: a failed readout is
/// logged once and reported as false, since an exception there would end the overlay.</para>
/// </summary>
public sealed unsafe class GpuHdrFrame : IHdrFrame
{
    private readonly GpuTonemapper _gpu;
    private ID3D11Texture2D? _frame;
    private ID3D11ShaderResourceView? _srv;
    /// <summary>The tonemap target, frame-sized packed BGRA (R32_UINT); made on the first tonemap.</summary>
    private ID3D11Texture2D? _out;
    private ID3D11UnorderedAccessView? _outUav;

    internal GpuHdrFrame(GpuTonemapper gpu, ID3D11Texture2D frame, ID3D11ShaderResourceView srv)
    {
        _gpu = gpu;
        _frame = frame;
        _srv = srv;
        Texture2DDescription d = frame.Description;
        Width = (int)d.Width; Height = (int)d.Height;
    }

    public int Width { get; }
    public int Height { get; }
    public bool Readable => _frame != null && !_gpu.Lost;

    public bool TrySample(int x, int y, out float r, out float g, out float b)
    {
        r = g = b = 0;
        if (!Readable || (uint)x >= (uint)Width || (uint)y >= (uint)Height) return false;
        if (!Monitor.TryEnter(_gpu.Gate)) return false;
        try
        {
            if (_frame == null) return false;
            (ushort hr, ushort hg, ushort hb) = _gpu.ReadPixel(_frame, x, y);
            r = Transfer.HalfToFloat(hr); g = Transfer.HalfToFloat(hg); b = Transfer.HalfToFloat(hb);
            return true;
        }
        catch (Exception e) when (_gpu.IsDeviceLoss(e)) { _gpu.Lose(e); return false; }
        catch (Exception e) { _gpu.ReadoutFailed(e); return false; }
        finally { _gpu.Settle(); Monitor.Exit(_gpu.Gate); }
    }

    /// <summary>Exact: every pixel of the rectangle is counted.</summary>
    public bool TryStats(IntRect rect, out float peak, out float mean)
    {
        peak = mean = 0;
        rect = rect.Intersect(new IntRect(0, 0, Width, Height));
        if (!Readable) return false;
        if (rect.IsEmpty) return true;
        if (!Monitor.TryEnter(_gpu.Gate)) return false;
        try
        {
            if (_srv == null) return false;
            (peak, mean) = _gpu.Stats(_srv, rect.Left, rect.Top, rect.Width, rect.Height);
            return true;
        }
        catch (Exception e) when (_gpu.IsDeviceLoss(e)) { _gpu.Lose(e); return false; }
        catch (Exception e) { _gpu.ReadoutFailed(e); return false; }
        finally { _gpu.Settle(); Monitor.Exit(_gpu.Gate); }
    }

    public void Tonemap(TonemapCurve curve, IntRect source, BgraImage target, int x, int y)
    {
        if (source.IsEmpty || source.Left < 0 || source.Top < 0 || source.Right > Width || source.Bottom > Height)
            throw new ArgumentOutOfRangeException(nameof(source), $"{source} is outside {Width}x{Height}");
        if (x < 0 || y < 0 || x + source.Width > target.Width || y + source.Height > target.Height)
            throw new ArgumentOutOfRangeException(nameof(x), $"{source.Width}x{source.Height} at ({x}, {y}) is outside {target.Width}x{target.Height}");
        Run(() =>
        {
            if (_out == null)
            {
                _out = _gpu.Device.CreateTexture2D(new Texture2DDescription(Format.R32_UInt, (uint)Width, (uint)Height, 1, 1, BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None));
                _outUav = _gpu.Device.CreateUnorderedAccessView(_out);
            }
            _gpu.DispatchTonemap(curve, _srv!, _outUav!, source.Left, source.Top, source.Width, source.Height);
            _gpu.ReadBgra(_out, source.Width, source.Height, target.Data, target.Width, x, y);
        });
    }

    /// <summary>A region copy into half-float staging bands, so only the crop crosses the bus.</summary>
    public HalfImage Crop(IntRect rect)
    {
        if (rect.IsEmpty || rect.Left < 0 || rect.Top < 0 || rect.Right > Width || rect.Bottom > Height)
            throw new ArgumentOutOfRangeException(nameof(rect), $"{rect} is outside {Width}x{Height}");
        var img = new HalfImage(rect.Width, rect.Height);
        Run(() => ReadHalf(rect.Width, rect.Height, RowsOf(rect),
            (row, y) => row.CopyTo(img.Data.AsSpan(y * rect.Width * 4, rect.Width * 4))));
        return img;
    }

    /// <summary>Copies rows [band, band + rows) of <paramref name="rect"/> into a staging band.</summary>
    private Action<ID3D11Texture2D, int, int> RowsOf(IntRect rect)
        => (staging, band, rows) => _gpu.Context.CopySubresourceRegion(staging, 0, 0, 0, 0, _frame!, 0,
            new Vortice.Mathematics.Box(rect.Left, rect.Top + band, 0, rect.Right, rect.Top + band + rows, 1));

    /// <summary>Reads the rectangle through the same staging bands as <see cref="Crop"/>, keeping only the samples:
    /// the snip's auto exposure costs a float per sample (at most a few MB) instead of a crop, which at 8K was a 265 MB
    /// array.</summary>
    public float[] Luminances(IntRect rect, int step)
    {
        if (rect.IsEmpty || rect.Left < 0 || rect.Top < 0 || rect.Right > Width || rect.Bottom > Height)
            throw new ArgumentOutOfRangeException(nameof(rect), $"{rect} is outside {Width}x{Height}");
        ArgumentOutOfRangeException.ThrowIfLessThan(step, 1);
        int w = rect.Width;
        var nits = new float[(w * rect.Height + step - 1) / step];
        Run(() => ReadHalf(w, rect.Height, RowsOf(rect), (row, y) =>
        {
            // Sample n is pixel n * step of the crop in row-major order; the first one on this row follows from where
            // the row starts.
            int start = y * w, x = (step - start % step) % step;
            for (; x < w; x += step) nits[(start + x) / step] = AutoExposure.Nits(row.Slice(x * 4, 3));
        }));
        return nits;
    }

    /// <summary>Copies only the rows that are kept, then keeps every step-th pixel of each: the settings preview's
    /// reduced frame without reading the whole one back.</summary>
    public HalfImage Downsample(int step)
    {
        int w = Math.Max(1, Width / step), h = Math.Max(1, Height / step);
        var img = new HalfImage(w, h);
        Run(() => ReadHalf(Width, h, (staging, band, rows) =>
        {
            for (int r = 0; r < rows; r++)
            {
                int sy = (band + r) * step;
                _gpu.Context.CopySubresourceRegion(staging, 0, 0, (uint)r, 0, _frame!, 0, new Vortice.Mathematics.Box(0, sy, 0, Width, sy + 1, 1));
            }
        }, (row, y) =>
        {
            Span<ushort> dst = img.Data.AsSpan(y * w * 4, w * 4);
            for (int x = 0; x < w; x++) row.Slice(x * step * 4, 4).CopyTo(dst.Slice(x * 4, 4));
        }));
        return img;
    }

    /// <summary>
    /// Reads <paramref name="height"/> rows of <paramref name="width"/> half-float pixels through one staging band at a
    /// time: <paramref name="fill"/> copies rows [band, band + rows) of the result into the band, and
    /// <paramref name="read"/> takes each row out. Called with the device lock held.
    /// </summary>
    private void ReadHalf(int width, int height, Action<ID3D11Texture2D, int, int> fill, ReadRow read)
    {
        int rows = GpuTonemapper.HalfBandRows(width, height);
        using ID3D11Texture2D staging = _gpu.Device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float, (uint)width, (uint)rows, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        for (int band = 0; band < height; band += rows)
        {
            int n = Math.Min(rows, height - band);
            fill(staging, band, n);
            MappedSubresource m = _gpu.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                for (int r = 0; r < n; r++)
                    read(new ReadOnlySpan<ushort>((byte*)m.DataPointer + (long)r * m.RowPitch, width * 4), band + r);
            }
            finally { _gpu.Context.Unmap(staging, 0); }
        }
    }

    private delegate void ReadRow(ReadOnlySpan<ushort> row, int y);

    public ZebraMask Zebra(float sdrWhiteScRgb, float exposure)
    {
        var mask = new ZebraMask(Width, Height);
        Run(() => _gpu.Zebra(_srv!, Width, Height, mask.Stride, sdrWhiteScRgb, exposure, mask.Bits));
        return mask;
    }

    /// <summary>Runs one readback under the device lock, turning a lost device into <see cref="HdrFrameLostException"/>
    /// for every frame on it. Each call that held the lock settles, on its way out, the frames disposed while it held
    /// it too long (<see cref="GpuTonemapper.Settle"/>).</summary>
    private void Run(Action body)
    {
        if (_gpu.Lost) throw new HdrFrameLostException("the graphics device holding the HDR frame was lost");
        _gpu.Enter();
        try
        {
            ObjectDisposedException.ThrowIf(_frame == null, this);
            body();
        }
        catch (Exception e) when (e is not ObjectDisposedException && _gpu.IsDeviceLoss(e)) { throw _gpu.Lose(e); }
        finally { _gpu.Settle(); Monitor.Exit(_gpu.Gate); }
    }

    /// <summary>Frees the texture and the tonemap target. Safe to call twice, and from any thread.</summary>
    public void Dispose()
    {
        if (Volatile.Read(ref _frame) == null) return;   // a second release does not wait for the device again
        if (!Monitor.TryEnter(_gpu.Gate, GpuTonemapper.GateBudgetMs))
        {
            // Past the budget another call is stuck in the driver, perhaps reading the shared scratch or about to. This
            // frame's own references are safe to release without the lock (the device is multithread-protected), and
            // holding the frame for ever would be worse; the shared scratch and the device are left to the next holder
            // of the lock.
            if (FreeOwn()) _gpu.FrameReleasedLater();
            return;
        }
        try { if (FreeOwn()) _gpu.FrameReleased(); }
        finally { _gpu.Settle(); Monitor.Exit(_gpu.Gate); }
    }

    /// <summary>Releases this frame's textures and views, once: true for the call that did.</summary>
    private bool FreeOwn()
    {
        ID3D11Texture2D? frame = Interlocked.Exchange(ref _frame, null);
        if (frame == null) return false;
        _outUav?.Dispose(); _outUav = null;
        _out?.Dispose(); _out = null;
        _srv?.Dispose(); _srv = null;
        frame.Dispose();
        return true;
    }
}
