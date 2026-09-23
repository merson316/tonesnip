using ToneSnip.Core.Capture;
using ToneSnip.Core.Config;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using ToneSnip.Core.Tonemap;
using ToneSnip.Windows.Capture;
using ToneSnip.Windows.Display;
using Vortice.DXGI;

namespace ToneSnip.App.Capture;

/// <summary>
/// Captures every monitor for a snip with Windows.Graphics.Capture (<see cref="ScreenCapture"/>), falling back to GDI
/// per monitor, into the pooled half-float and BGRA images the overlay and result are built from.
/// </summary>
public sealed class FrameGrabber(Func<SnipSettings> settings, ILog log) : IDisposable
{
    /// <summary>How long one monitor may take to deliver its frame before GDI is used instead; typical captures take
    /// well under 200 ms.</summary>
    private const int FrameTimeoutMs = 1500;

    private AcesLut? _acesLut;
    private TonemapParams? _acesKey;
    private readonly object _lutLock = new();
    private readonly ScreenCapture _capture = new(log);
    /// <summary>One grab at a time, and no display-change refresh in the middle of one.</summary>
    private readonly object _grabGate = new();
    /// <summary>The per-output frame buffers, reused by every grab. See <see cref="FramePool"/> for the ownership
    /// contract: the next grab overwrites them.</summary>
    private readonly FramePool _pool = new();
    public event Action<List<CapturedOutput>>? Grabbed;

    /// <summary>The outputs the last grab had to copy through GDI, by device name.</summary>
    public IReadOnlyList<string> LastFallbacks { get; private set; } = Array.Empty<string>();

    /// <summary>Bytes held by the pooled frame buffers.</summary>
    public long PooledBytes => _pool.Bytes;

    /// <summary>How long the idle release waits for a grab in progress before leaving the device for the next
    /// release. A grab that has not let go by then is stuck, and waiting on it would park the thread for good.</summary>
    private const int ReleaseWaitMs = 2000;
    /// <summary>How long <see cref="Dispose"/> waits for a grab in progress. The process is exiting, which frees the
    /// device anyway, so Quit and the end of the Windows session must not hang behind a stuck grab.</summary>
    private const int DisposeWaitMs = 250;

    /// <summary>Releases the pooled buffers and the capture device. The next grab allocates both again; a session
    /// still holding old buffers keeps them alive until it lets go. The device is kept, with a warning, while a grab
    /// holds it past <see cref="ReleaseWaitMs"/>.</summary>
    public void ReleaseBuffers()
    {
        _pool.Clear();
        if (!Monitor.TryEnter(_grabGate, ReleaseWaitMs)) { log.Warn("capture: the graphics device was not released, a grab is still running"); return; }
        try { _capture.ReleaseDevice(); }
        finally { Monitor.Exit(_grabGate); }
    }

    /// <summary>The overlay window's annotated copy of one output's frame, pooled like the frame itself.</summary>
    public BgraImage BackBuffer(OutputInfo o, int width, int height) => _pool.Bgra(o.Index, FramePool.Back, width, height);

    /// <summary>The monitors as the rest of the app describes them.</summary>
    public List<OutputInfo> Outputs()
    {
        lock (_grabGate) return Describe(_capture.Outputs);
    }

    /// <summary>Enumerates the monitors again, after a display change.</summary>
    public void RefreshOutputs()
    {
        lock (_grabGate) { _displaysChanged = false; _capture.Refresh(); }
    }

    /// <summary>Set on a display change, so a snip taken before the app's delayed refresh re-enumerates first.</summary>
    private volatile bool _displaysChanged;

    /// <summary>The displays changed: the next grab enumerates them again before it captures.</summary>
    public void DisplaysChanged() => _displaysChanged = true;

    private static List<OutputInfo> Describe(IReadOnlyList<OutputHandle> handles)
    {
        return handles.Select((h, i) => new OutputInfo(i, h.DeviceName, h.Left, h.Top, h.Width, h.Height, h.Hdr, h.SdrWhiteNits, h.MaxLuminance, h.FriendlyName)).ToList();
    }

    /// <summary>One frame of every monitor, as the snip's overlay and result need them.</summary>
    public List<CapturedOutput> GrabAll()
    {
        lock (_grabGate)
        {
            if (_displaysChanged) { _displaysChanged = false; _capture.Refresh(); }
            else _capture.RefreshWhiteLevels();
            IReadOnlyList<OutputHandle> handles = _capture.Outputs;
            List<OutputInfo> outputs = Describe(handles);
            if (outputs.Count == 0) throw new InvalidOperationException("no monitors found");
            _pool.BeginGrab();
            var captured = new CapturedOutput?[outputs.Count];
            var copies = new CopyState[outputs.Count];
            for (int i = 0; i < copies.Length; i++) copies[i] = new CopyState();
            string?[] reasons = _capture.Capture(handles, (i, data, pitch, w, h, format) => Convert(data, pitch, w, h, format, outputs[i], copies[i], c => captured[i] = c), FrameTimeoutMs);
            var result = new List<CapturedOutput>(outputs.Count);
            var fallbacks = new List<string>();
            for (int i = 0; i < outputs.Count; i++)
            {
                OutputInfo o = outputs[i];
                CapturedOutput? c = reasons[i] == null ? captured[i] : null;
                if (c == null)
                {
                    log.Warn($"{o.DeviceName}: using GDI ({reasons[i] ?? "the frame did not match the monitor"})");
                    bool stillCopying = reasons[i] == ScreenCapture.StillCopyingReason;
                    if (stillCopying)
                    {
                        // The copy, or its tonemap, may still be running and may write into the buffers it was
                        // given. They leave the pool, so the next grab does not get them, and the copy may not ask
                        // for more (an idle release could have emptied the pool by the time it does).
                        lock (copies[i]) copies[i].Abandoned = true;
                        _pool.DropBgra(o.Index, FramePool.Frame);
                    }
                    _pool.DropHalf(o.Index, FramePool.Frame);   // an HDR copy that began and failed asked for one
                    // An unfinished copy may still be writing this output's pooled buffer, so GDI gets its own.
                    BgraImage sdr = stillCopying
                        ? BgraImage.Blank(o.Width, o.Height)
                        : _pool.Bgra(o.Index, FramePool.Frame, o.Width, o.Height);
                    GdiCapture.CaptureBgra8Into(o.Bounds, sdr.Data);
                    c = new CapturedOutput(o with { Hdr = false }, null, sdr);
                    fallbacks.Add(o.DeviceName);
                }
                result.Add(c);
            }
            // Only after a completed grab: a partial record would prune buffers the next grab still needs.
            _pool.EndGrab(outputs.Select(o => o.Index).ToList());
            LastFallbacks = fallbacks;
            log.Debug($"grab: {result.Count} output(s), {_pool.Bytes / 1_048_576} MB of pooled frame buffers");
            Grabbed?.Invoke(result);
            return result;
        }
    }

    /// <summary>
    /// Copies one monitor's mapped frame (desktop orientation) into pooled buffers and hands the result to
    /// <paramref name="done"/>: fp16 becomes a half image plus its tonemapped SDR copy, BGRA8 is copied as is. Nothing
    /// is handed over when the frame size does not match the monitor (a mode change mid-grab), so the grab falls back
    /// to GDI.
    /// <para>Runs with the capture device locked and the frame mapped, so for fp16 only the copy happens here; the
    /// tonemap, which reads just the half copy, is returned for <see cref="ScreenCapture"/> to run once the device is
    /// free, so a second monitor's copy does not wait behind it.</para>
    /// <para>A copy that runs past the grab's wait (<see cref="ScreenCapture.StillCopyingReason"/>) is abandoned by
    /// <see cref="GrabAll"/> through <paramref name="copy"/>. Both buffers are taken from the pool here, under that
    /// state's lock, so a late copy never adds a buffer to the pool after the grab has ended; the tonemap writes only
    /// into the buffer it was given, which the abandoning grab has taken out of the pool, and is skipped once
    /// abandoned.</para>
    /// </summary>
    private Action? Convert(IntPtr data, int rowPitch, int width, int height, Format format, OutputInfo o, CopyState copy, Action<CapturedOutput> done)
    {
        if (width != o.Width || height != o.Height)
        {
            log.Warn($"{o.DeviceName}: frame {width}x{height} is not the monitor's {o.Width}x{o.Height}");
            return null;
        }
        bool hdr = format == Format.R16G16B16A16_Float;
        HalfImage? half;
        BgraImage target;
        lock (copy)
        {
            if (copy.Abandoned) return null;
            half = hdr ? _pool.Half(o.Index, FramePool.Frame, width, height) : null;
            target = _pool.Bgra(o.Index, FramePool.Frame, width, height);
        }
        if (half != null)
        {
            FrameConverter.ToHalfInto(data, rowPitch, width, height, half);
            return () =>
            {
                lock (copy) { if (copy.Abandoned) return; }
                TonemapInto(half, o, target);
                done(new CapturedOutput(o, half, target));
            };
        }
        FrameConverter.ToBgra8Into(data, rowPitch, width, height, target);
        done(new CapturedOutput(o with { Hdr = false }, null, target));
        return null;
    }

    /// <summary>One output's copy in one grab: set abandoned, under its own lock, when the grab stops waiting for it.</summary>
    private sealed class CopyState
    {
        public bool Abandoned;
    }

    public void Dispose()
    {
        if (!Monitor.TryEnter(_grabGate, DisposeWaitMs)) { log.Warn("capture: a grab is still running at exit; its device is left to the process exit"); return; }
        try { _capture.Dispose(); }
        finally { Monitor.Exit(_grabGate); }
    }

    /// <summary>Tonemaps an HDR frame into a new image with the current settings; <paramref name="exposureOverride"/>
    /// replaces the configured exposure when positive. The allocating form, for anything that outlives a snip.</summary>
    public BgraImage Tonemap(HalfImage img, OutputInfo o, float exposureOverride = -1f)
    {
        var target = new BgraImage(img.Width, img.Height, new byte[checked(img.Width * img.Height * 4)]);
        TonemapCore(img, o, exposureOverride, target.Data);
        return target;
    }

    /// <summary>Tonemaps with the configured exposure into an existing target buffer (no allocation); used by the grab.</summary>
    public void TonemapInto(HalfImage img, OutputInfo o, BgraImage target) => TonemapCore(img, o, -1f, target.Data);

    /// <summary>Tonemaps into an existing target buffer in place (no allocation); used by live exposure in the overlay.</summary>
    public void TonemapInto(HalfImage img, OutputInfo o, float exposure, BgraImage target) => TonemapCore(img, o, exposure, target.Data);

    /// <summary>Tonemaps the part of <paramref name="img"/> inside <paramref name="source"/> at the given exposure
    /// straight into <paramref name="target"/> at (<paramref name="x"/>, <paramref name="y"/>); used to build a snip's
    /// composite without an intermediate crop.</summary>
    public void TonemapInto(HalfImage img, IntRect source, OutputInfo o, float exposure, BgraImage target, int x, int y)
    {
        (ITonemapper? tm, AcesLut? lut) = TonemapperFor(o, exposure);
        PixelConvert.ToBgra8Into(img, source, tm, lut, target, x, y);
    }

    private void TonemapCore(HalfImage img, OutputInfo o, float exposureOverride, byte[] dst)
    {
        (ITonemapper? tm, AcesLut? lut) = TonemapperFor(o, exposureOverride);
        PixelConvert.ToBgra8Into(img, tm, lut, dst);
    }

    /// <summary>The configured curve for this monitor: the cached LUT for ACES, a tonemapper otherwise.</summary>
    private (ITonemapper?, AcesLut?) TonemapperFor(OutputInfo o, float exposureOverride)
    {
        SnipSettings s = settings();
        TonemapParams p = s.ToTonemapParams(o.SdrWhiteNits, o.PeakNits);
        if (exposureOverride > 0f) p = p with { Exposure = exposureOverride };
        return s.Tonemap == "aces" ? (null, LutFor(p)) : (TonemapperFactory.Create(s.Tonemap, p), null);
    }

#if TONESNIP_HARNESS
    /// <summary>
    /// `--memtest` only: one grab's worth of synthetic frames for a given layout, allocated through the same pool as
    /// <see cref="GrabAll"/>, without touching the screen.
    /// </summary>
    /// <param name="seed">Varies the picture, so consecutive grabs differ and pool overwrites are detectable.</param>
    public List<CapturedOutput> GrabSynthetic(IReadOnlyList<OutputInfo> outputs, int seed = 0)
    {
        var result = new List<CapturedOutput>(outputs.Count);
        _pool.BeginGrab();
        foreach (OutputInfo o in outputs)
        {
            BgraImage sdr = _pool.Bgra(o.Index, FramePool.Frame, o.Width, o.Height);
            HalfImage? half = null;
            if (o.Hdr)
            {
                half = _pool.Half(o.Index, FramePool.Frame, o.Width, o.Height);
                FillHalf(half, seed);
                TonemapInto(half, o, sdr);
            }
            else FillBgra(sdr, seed);
            result.Add(new CapturedOutput(o, half, sdr));
        }
        _pool.EndGrab(outputs.Select(o => o.Index).ToList());
        Grabbed?.Invoke(result);
        return result;
    }

    /// <summary>A cheap gradient over the whole buffer, touching every page as a real full-frame copy does.</summary>
    private static void FillHalf(HalfImage img, int seed)
    {
        ushort[] d = img.Data;
        int w = img.Width;
        Parallel.For(0, img.Height, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                float v = ((x + y + seed * 37) & 0xFF) / 255f * 4f;
                d[i] = BitConverter.HalfToUInt16Bits((Half)v);
                d[i + 1] = BitConverter.HalfToUInt16Bits((Half)(v * 0.6f));
                d[i + 2] = BitConverter.HalfToUInt16Bits((Half)(v * 0.3f));
                d[i + 3] = 0x3C00;
            }
        });
    }

    private static void FillBgra(BgraImage img, int seed)
    {
        byte[] d = img.Data;
        int w = img.Width;
        Parallel.For(0, img.Height, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                d[i] = (byte)((x + seed * 37) & 0xFF); d[i + 1] = (byte)((y + seed * 53) & 0xFF); d[i + 2] = (byte)((x + y + seed) & 0xFF); d[i + 3] = 255;
            }
        });
    }
#endif

    /// <summary>
    /// The cached ACES LUT, locked because the overlay's exposure preview and <see cref="CaptureResult.Build"/> can
    /// tonemap on different threads.
    /// </summary>
    private AcesLut LutFor(TonemapParams p)
    {
        lock (_lutLock)
        {
            if (_acesLut == null || _acesKey != p) { _acesLut = new AcesLut(p); _acesKey = p; }
            return _acesLut;
        }
    }
}
