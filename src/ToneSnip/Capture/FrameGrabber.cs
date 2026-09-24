using System.Runtime.InteropServices;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Color;
using ToneSnip.Core.Config;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Hdr;
using ToneSnip.Core.Imaging;
using ToneSnip.Core.Tonemap;
using ToneSnip.Windows.Capture;
using ToneSnip.Windows.Display;
using ToneSnip.Windows.Interop;
using Vortice.DXGI;

namespace ToneSnip.App.Capture;

/// <summary>
/// Captures every monitor for a snip with Windows.Graphics.Capture (<see cref="ScreenCapture"/>), falling back to GDI
/// per monitor, into the pooled BGRA images the overlay and result are built from, plus each HDR monitor's frame.
/// <para>An HDR frame stays on the graphics card (<see cref="GpuHdrFrame"/>) and is tonemapped there, unless the GPU
/// path is switched off (<see cref="UseGpu"/>) or unavailable: then it is copied into a pooled half-float image
/// (<see cref="HalfFrame"/>) and tonemapped on the CPU.</para>
/// </summary>
public sealed class FrameGrabber(Func<SnipSettings> settings, ILog log) : IDisposable
{
    /// <summary>How long one monitor may take to deliver its frame before GDI is used instead; typical captures take
    /// well under 200 ms.</summary>
    private const int FrameTimeoutMs = 1500;

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

    /// <summary>
    /// The kill switch for the GPU path: TONESNIP_GPU_TONEMAP=0 or 1 when set, otherwise the <c>hdr.gpuTonemap</c>
    /// setting. Read on every grab, so a change applies to the next snip; the path is logged when it changes.
    /// </summary>
    public bool UseGpu()
    {
        string? forced = Environment.GetEnvironmentVariable("TONESNIP_GPU_TONEMAP");
        bool on = forced == "1" || (forced != "0" && settings().Hdr.GpuTonemap);
        if (on != _gpuLogged)
        {
            _gpuLogged = on;
            log.Info($"capture: HDR frames are tonemapped on the {(on ? "GPU" : "CPU")}{(forced is "0" or "1" ? " (TONESNIP_GPU_TONEMAP)" : "")}");
        }
        return on;
    }

    private bool? _gpuLogged;

    /// <summary>
    /// Frees each output's HDR frame, once the snip has been built from them or cancelled. A frame on the graphics card
    /// goes at once, with its readback buffers; a pooled half-float image stays in the pool and only stops being
    /// readable through the frame.
    /// </summary>
    public static void Release(IEnumerable<CapturedOutput> outputs)
    {
        foreach (CapturedOutput o in outputs) o.Hdr?.Dispose();
    }

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

    /// <summary>One frame of every monitor, as the snip's overlay and result need them. The caller owns the HDR frames
    /// and passes the list to <see cref="Release"/> when the snip is done with them.</summary>
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
            string?[] reasons = _capture.Capture(handles, (i, data, pitch, w, h, format) => Convert(data, pitch, w, h, format, outputs[i], copies[i], c => captured[i] = c), FrameTimeoutMs,
                UseGpu() ? (i, frame) => Keep(frame, outputs[i], copies[i], c => captured[i] = c) : null, settings().CaptureCursor);
            // From here the grab owns the frames the capture handed over: if anything below throws (a GDI copy of
            // another monitor, or a Grabbed handler), nobody else will ever dispose them, and each one on the graphics
            // card holds a monitor-sized texture and keeps its device alive.
            try
            {
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
                        // The copy, or its tonemap, may still be running. Abandoned, it writes nothing more and asks the
                        // pool for nothing more (an idle release could have emptied the pool by the time it does). A frame
                        // on the graphics card it already handed over is freed here; one it hands over later, it frees
                        // itself, since it checks Abandoned under the same lock.
                        lock (copies[i]) { copies[i].Abandoned = true; captured[i]?.Hdr?.Dispose(); }
                        // Its buffers leave the pool, so the next grab does not get them.
                        if (stillCopying) _pool.DropBgra(o.Index, FramePool.Frame);
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
            catch
            {
                // A late copy hands its frame over under its own lock, so each is read under that lock too.
                for (int i = 0; i < captured.Length; i++) lock (copies[i]) { copies[i].Abandoned = true; captured[i]?.Hdr?.Dispose(); }
                throw;
            }
        }
    }

    /// <summary>
    /// An active-window snip's frame, captured from the window itself rather than cropped from every monitor: the parts
    /// other windows cover are in it, the desktop behind its rounded corners is not, and only one window-sized frame is
    /// held. Null, with the reason logged, when the snip should be cropped from a grab of every monitor as before: the
    /// window is minimised, see-through, elevated, off every monitor, or the capture failed or came back blank.
    /// <para>The frame borrows one monitor's HDR state and white level (<see cref="WindowSnip.OutputFor"/>). Its buffers
    /// are its own, not the pool's, since the pool is sized per monitor; the caller releases the HDR frame with
    /// <see cref="Release"/> as for <see cref="GrabAll"/>.</para>
    /// </summary>
    public WindowGrab? GrabWindow(IntPtr window)
    {
        lock (_grabGate)
        {
            if (_displaysChanged) { _displaysChanged = false; _capture.Refresh(); }
            else _capture.RefreshWhiteLevels();
            // A kill switch, as TONESNIP_GPU_TONEMAP is for the GPU path: every active-window snip is cropped as before.
            if (Environment.GetEnvironmentVariable("TONESNIP_WINDOW_CAPTURE") == "0") { log.Info("window capture: off (TONESNIP_WINDOW_CAPTURE=0); cropping the screen instead"); return null; }
            if (WindowFinder.CaptureRefusal(window) is { } refusal) { log.Info($"window capture: {refusal}; cropping the screen instead"); return null; }
            (IntRect frame, IntRect rect) = WindowFinder.Rects(window);
            List<OutputInfo> outputs = Describe(_capture.Outputs);
            if (frame.IsEmpty || WindowSnip.OutputFor(frame, outputs) is not { } monitor) { log.Info($"window capture: the window at {frame} is on no monitor; cropping the screen instead"); return null; }
            log.Debug($"window capture: {frame} (window {rect}) with {monitor.DeviceName}'s values, hdr={monitor.Hdr}, SDR white {monitor.SdrWhiteNits:F0} nits");
            int dpi = (int)User32.GetDpiForWindow(window);
            int corner = WindowCorners.SizeFor(dpi);
            double radius = WindowFinder.CornerRadius(window, dpi);
            var copy = new CopyState();
            WindowGrab? grab = null;
            void Done(WindowGrab g)
            {
                lock (copy)
                {
                    if (copy.Abandoned) g.Release();
                    else grab = g;
                }
            }
            string? reason = _capture.CaptureWindow(window, monitor.Hdr,
                (_, data, pitch, w, h, format) => CopyWindow(window, data, pitch, w, h, format, monitor, corner, radius, copy, Done), FrameTimeoutMs,
                UseGpu() ? (_, f) => KeepWindow(window, f, monitor, corner, radius, copy, Done) : null, settings().CaptureCursor);
            WindowGrab? got;
            lock (copy)
            {
                got = reason == null ? grab : null;
                if (got == null) { copy.Abandoned = true; grab?.Release(); }
            }
            if (got == null)
            {
                _capture.ForgetWindow(window);
                log.Warn($"window capture: {reason ?? "no frame was handed over"}; cropping the screen instead");
                return null;
            }
            if (got.Blank)
            {
                got.Release();
                log.Warn("window capture: the frame came back blank; cropping the screen instead");
                return null;
            }
            return got;
        }
    }

    /// <summary>
    /// The staging path of <see cref="GrabWindow"/>: a BGRA8 frame is copied as it is and an HDR one (when the GPU path
    /// is off) as a half image with its tonemapped copy. The corners' alpha is read from the capture before the copy is
    /// made opaque. Placed on the desktop here, as the frame arrives, so a window that moved since the capture began
    /// lands where it is now.
    /// </summary>
    private Action? CopyWindow(IntPtr window, IntPtr data, int pitch, int w, int h, Format format, OutputInfo monitor, int corner, double radius, CopyState copy, Action<WindowGrab> done)
    {
        lock (copy) { if (copy.Abandoned) return null; }
        (IntRect at, IntRect keep) = Place(window, w, h);
        IntRect local = keep.Offset(-at.Left, -at.Top);
        var sdr = BgraImage.Blank(w, h);
        if (format == Format.R16G16B16A16_Float)
        {
            HalfImage half = FrameConverter.ToHalfInto(data, pitch, w, h, new HalfImage(w, h));
            return () =>
            {
                lock (copy) { if (copy.Abandoned) return; }
                var hdr = new HalfFrame(half);
                OutputInfo info = monitor with { Left = at.Left, Top = at.Top, Width = w, Height = h };
                hdr.Tonemap(CurveFor(info), new IntRect(0, 0, w, h), sdr, 0, 0);
                WindowCorners? corners = WindowCorners.Read(keep.Width, keep.Height, corner, r => HalfPatch(half.Crop(r.Offset(local.Left, local.Top))), radius);
                done(new WindowGrab(new CapturedOutput(info, hdr, sdr), keep, corners, WindowSnip.IsBlank(half, BlankStep(w, h))));
            };
        }
        // Not FrameConverter.ToBgra8Into, which makes the copy opaque before the corners could be read.
        for (int y = 0; y < h; y++) Marshal.Copy(data + y * pitch, sdr.Data, y * w * 4, w * 4);
        WindowCorners? read = WindowCorners.Read(keep.Width, keep.Height, corner, r => BgraPatch(sdr, r.Offset(local.Left, local.Top)), radius);
        bool blank = WindowSnip.IsBlank(sdr, BlankStep(w, h));
        for (int i = 3; i < sdr.Data.Length; i += 4) sdr.Data[i] = 255;
        done(new WindowGrab(new CapturedOutput(monitor with { Left = at.Left, Top = at.Top, Width = w, Height = h, Hdr = false }, null, sdr), keep, read, blank));
        return null;
    }

    /// <summary>The GPU path of <see cref="GrabWindow"/>, as <see cref="Keep"/> is <see cref="GrabAll"/>'s: only the
    /// tonemapped copy and the four corner squares are read back.</summary>
    private Action? KeepWindow(IntPtr window, GpuHdrFrame frame, OutputInfo monitor, int corner, double radius, CopyState copy, Action<WindowGrab> done)
    {
        lock (copy) { if (copy.Abandoned) { frame.Dispose(); return null; } }
        (IntRect at, IntRect keep) = Place(window, frame.Width, frame.Height);
        IntRect local = keep.Offset(-at.Left, -at.Top);
        return () =>
        {
            try
            {
                lock (copy) { if (copy.Abandoned) { frame.Dispose(); return; } }
                OutputInfo info = monitor with { Left = at.Left, Top = at.Top, Width = frame.Width, Height = frame.Height };
                var sdr = BgraImage.Blank(frame.Width, frame.Height);
                frame.Tonemap(CurveFor(info), new IntRect(0, 0, frame.Width, frame.Height), sdr, 0, 0);
                WindowCorners? corners = WindowCorners.Read(keep.Width, keep.Height, corner, r => HalfPatch(frame.Crop(r.Offset(local.Left, local.Top))), radius);
                // Read back on a grid, alpha included, rather than judged by its luminance: a black window is not blank.
                bool blank = WindowSnip.IsBlank(frame.Downsample(BlankStep(frame.Width, frame.Height)), 1);
                done(new WindowGrab(new CapturedOutput(info, frame, sdr), keep, corners, blank));
            }
            catch { frame.Dispose(); throw; }
        };
    }

    /// <summary>Where a window's <paramref name="w"/> x <paramref name="h"/> frame lands, from its rectangles as they
    /// are now (<see cref="WindowSnip.Place"/>).</summary>
    private (IntRect At, IntRect Keep) Place(IntPtr window, int w, int h)
    {
        (IntRect frame, IntRect rect) = WindowFinder.Rects(window);
        (IntRect at, IntRect keep) = WindowSnip.Place(frame, rect, w, h);
        if (at.Width != frame.Width || at.Height != frame.Height) log.Debug($"window capture: the frame is {w}x{h}, the window's visible frame {frame}, its rectangle {rect}; kept {keep}");
        return (at, keep);
    }

    private static WindowCorners.Patch BgraPatch(BgraImage img, IntRect r)
    {
        var a = new byte[r.Width * r.Height];
        var empty = new bool[a.Length];
        for (int y = 0; y < r.Height; y++)
            for (int x = 0; x < r.Width; x++)
            {
                int i = ((r.Top + y) * img.Width + r.Left + x) * 4;
                a[y * r.Width + x] = img.Data[i + 3];
                empty[y * r.Width + x] = (img.Data[i] | img.Data[i + 1] | img.Data[i + 2] | img.Data[i + 3]) == 0;
            }
        return new WindowCorners.Patch(a, empty);
    }

    private static WindowCorners.Patch HalfPatch(HalfImage img)
    {
        var a = new byte[img.Width * img.Height];
        var empty = new bool[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            a[i] = (byte)Math.Round(Math.Clamp(Transfer.HalfToFloat(img.Data[i * 4 + 3]), 0f, 1f) * 255f);
            empty[i] = WindowSnip.IsEmpty(img.Data.AsSpan(i * 4, 4));
        }
        return new WindowCorners.Patch(a, empty);
    }

    /// <summary>The grid <see cref="WindowSnip.IsBlank(BgraImage, int)"/> samples a window's frame on: about ten
    /// thousand pixels.</summary>
    private static int BlankStep(int w, int h) => Math.Max(1, (int)Math.Sqrt(w * (long)h / 10_000.0));

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
                var frame = new HalfFrame(half);
                frame.Tonemap(CurveFor(o), new IntRect(0, 0, width, height), target, 0, 0);
                done(new CapturedOutput(o, frame, target));
            };
        }
        FrameConverter.ToBgra8Into(data, rowPitch, width, height, target);
        done(new CapturedOutput(o with { Hdr = false }, null, target));
        return null;
    }

    /// <summary>
    /// The GPU path's <see cref="Convert"/>: the frame stays on the graphics card, and only its tonemapped BGRA copy is
    /// read back, into the pooled buffer, on the returned action (the device is free by then). The frame is disposed on
    /// every path that does not hand it over, and it is handed over under the copy's lock, so a grab that abandons the
    /// output frees it either way.
    /// </summary>
    private Action? Keep(GpuHdrFrame frame, OutputInfo o, CopyState copy, Action<CapturedOutput> done)
    {
        if (frame.Width != o.Width || frame.Height != o.Height)
        {
            log.Warn($"{o.DeviceName}: frame {frame.Width}x{frame.Height} is not the monitor's {o.Width}x{o.Height}");
            frame.Dispose();
            return null;
        }
        BgraImage target;
        lock (copy)
        {
            if (copy.Abandoned) { frame.Dispose(); return null; }
            target = _pool.Bgra(o.Index, FramePool.Frame, o.Width, o.Height);
        }
        return () =>
        {
            try
            {
                lock (copy) { if (copy.Abandoned) { frame.Dispose(); return; } }
                frame.Tonemap(CurveFor(o), new IntRect(0, 0, o.Width, o.Height), target, 0, 0);
                lock (copy)
                {
                    if (copy.Abandoned) frame.Dispose();
                    else done(new CapturedOutput(o, frame, target));
                }
            }
            catch { frame.Dispose(); throw; }
        };
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

    /// <summary>Tonemaps a half-float image into a new image with the current settings; <paramref name="exposureOverride"/>
    /// replaces the configured exposure when positive. The allocating form, for anything that outlives a snip.</summary>
    public BgraImage Tonemap(HalfImage img, OutputInfo o, float exposureOverride = -1f)
    {
        var target = new BgraImage(img.Width, img.Height, new byte[checked(img.Width * img.Height * 4)]);
        TonemapInto(img, o, exposureOverride, target);
        return target;
    }

    /// <summary>Tonemaps a half-float image into an existing target of its size (no allocation); used by the editor's
    /// live exposure.</summary>
    public void TonemapInto(HalfImage img, OutputInfo o, float exposure, BgraImage target)
        => HalfFrame.Tonemap(img, CurveFor(o, exposure), new IntRect(0, 0, img.Width, img.Height), target, 0, 0);

    /// <summary>The configured curve for this monitor; <paramref name="exposureOverride"/> replaces the configured
    /// exposure when positive.</summary>
    public TonemapCurve CurveFor(OutputInfo o, float exposureOverride = -1f)
    {
        SnipSettings s = settings();
        TonemapParams p = s.ToTonemapParams(o.SdrWhiteNits, o.PeakNits);
        if (exposureOverride > 0f) p = p with { Exposure = exposureOverride };
        return new TonemapCurve(s.Tonemap, p);
    }

#if TONESNIP_HARNESS
    /// <summary>The capture itself, for the self-test's GPU parity checks.</summary>
    internal ScreenCapture CaptureForHarness => _capture;

    /// <summary>
    /// `--memtest` only: one grab's worth of synthetic frames for a given layout, allocated through the same pool as
    /// <see cref="GrabAll"/>, without touching the screen. With the GPU path on, an HDR frame is uploaded to the
    /// graphics card a row at a time, so the harness measures that path's memory: no half-float frame in the pool.
    /// </summary>
    /// <param name="seed">Varies the picture, so consecutive grabs differ and pool overwrites are detectable.</param>
    public List<CapturedOutput> GrabSynthetic(IReadOnlyList<OutputInfo> outputs, int seed = 0)
    {
        var result = new List<CapturedOutput>(outputs.Count);
        bool gpu = UseGpu();
        _pool.BeginGrab();
        foreach (OutputInfo o in outputs)
        {
            BgraImage sdr = _pool.Bgra(o.Index, FramePool.Frame, o.Width, o.Height);
            IHdrFrame? frame = null;
            if (o.Hdr)
            {
                if (gpu) frame = _capture.Upload(o.Width, o.Height, (y, row) => FillHalfRow(row, o.Width, y, seed));
                if (frame == null)
                {
                    HalfImage half = _pool.Half(o.Index, FramePool.Frame, o.Width, o.Height);
                    FillHalf(half, seed);
                    frame = new HalfFrame(half);
                }
                frame.Tonemap(CurveFor(o), new IntRect(0, 0, o.Width, o.Height), sdr, 0, 0);
            }
            else FillBgra(sdr, seed);
            result.Add(new CapturedOutput(o, frame, sdr));
        }
        _pool.EndGrab(outputs.Select(o => o.Index).ToList());
        Grabbed?.Invoke(result);
        return result;
    }

    /// <summary>A cheap gradient over the whole buffer, touching every page as a real full-frame copy does.</summary>
    private static void FillHalf(HalfImage img, int seed)
    {
        int w = img.Width;
        Parallel.For(0, img.Height, y => FillHalfRow(img.Data.AsSpan(y * w * 4, w * 4), w, y, seed));
    }

    private static void FillHalfRow(Span<ushort> d, int w, int y, int seed)
    {
        for (int x = 0; x < w; x++)
        {
            int i = x * 4;
            float v = ((x + y + seed * 37) & 0xFF) / 255f * 4f;
            d[i] = BitConverter.HalfToUInt16Bits((Half)v);
            d[i + 1] = BitConverter.HalfToUInt16Bits((Half)(v * 0.6f));
            d[i + 2] = BitConverter.HalfToUInt16Bits((Half)(v * 0.3f));
            d[i + 3] = 0x3C00;
        }
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
}
