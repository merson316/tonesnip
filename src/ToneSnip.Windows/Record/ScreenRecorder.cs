using System.Diagnostics;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Hdr;
using ToneSnip.Core.Record;
using ToneSnip.Windows.Capture;
using ToneSnip.Windows.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ToneSnip.Windows.Record;

/// <summary>What to record: <paramref name="Area"/> of the monitor at <paramref name="Monitor"/>, both in
/// virtual-desktop pixels, the area with even sides (<see cref="RecordingArea"/>). An HDR monitor's frames are
/// tonemapped with <paramref name="Curve"/>, as its snips are.</summary>
public sealed record RecordingRequest(IntRect Monitor, IntRect Area, bool Hdr, TonemapCurve Curve, int FrameRate, bool Cursor, string Path);

/// <summary>A finished recording: where it is, how long it plays, and what it was encoded as.</summary>
public sealed record RecordingResult(string Path, TimeSpan Duration, VideoCodec Codec, int Width, int Height, int Frames);

/// <summary>
/// Records an area of one monitor into an MP4 until stopped. A capture session stays open on the monitor for the
/// whole recording (unlike a snip's, which takes one frame); each frame the capture hands over is tonemapped and
/// converted on the graphics card (<see cref="VideoConverter"/>) and encoded (<see cref="Mp4Writer"/>), at most one
/// per slot of the frame rate (<see cref="FramePacing"/>), on the recorder's own thread. Pausing cuts the paused time
/// out of the video (<see cref="RecordingClock"/>).
/// <para>The recorder has its own graphics device, so a snip taken meanwhile neither waits for it nor shares its
/// frames. Everything it holds goes when the recording ends.</para>
/// <para><b>Threading.</b> <see cref="Open"/> on any thread but the UI's; <see cref="Begin"/>, <see cref="Pause"/>,
/// <see cref="Resume"/>, <see cref="Stop"/> and the properties from any thread. Once begun, the frames, the device and
/// the file belong to the recorder's thread.</para>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The recorder's own thread releases everything it holds when the recording ends; Stop is how a caller ends it.")]
public sealed unsafe class ScreenRecorder
{
    private const uint MonitorDefaultToNull = 0;
    /// <summary>Frames the capture may queue while the recorder is busy. The recorder only ever keeps the newest.</summary>
    private const int PoolFrames = 3;

    private readonly RecordingRequest _request;
    private readonly ILog _log;
    /// <summary>The area in the monitor's own pixels, which its frames are.</summary>
    private readonly IntRect _area;
    /// <summary>The recorder's device lock, which its <see cref="GpuTonemapper"/> is made with.</summary>
    private readonly object _gate = new();
    /// <summary>Guards the clock and the stop request, which the UI thread reaches.</summary>
    private readonly object _control = new();
    private readonly RecordingClock _clock = new();
    private readonly FramePacing _pacing;
    private readonly AutoResetEvent _wake = new(false);
    private readonly TaskCompletionSource<RecordingResult?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;

    private ID3D11Device? _device;
    private IDirect3DDevice? _winrt;
    private GpuTonemapper? _gpu;
    private VideoConverter? _converter;
    private Mp4Writer? _writer;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    /// <summary>The newest frame not yet written, waiting for its slot.</summary>
    private Direct3D11CaptureFrame? _held;
    /// <summary>The last frame written, written once more at the end so the video lasts until the recording
    /// stopped.</summary>
    private IMFMediaBuffer? _last;
    private int _frames;
    private bool _begun, _stop, _keep = true;
    private string? _failure;

    private ScreenRecorder(RecordingRequest request, ILog log)
    {
        _request = request;
        _log = log;
        _area = request.Area.Offset(-request.Monitor.Left, -request.Monitor.Top);
        _pacing = new FramePacing(request.FrameRate);
        _thread = new Thread(Run) { IsBackground = true, Name = "ToneSnip recorder", Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>
    /// Opens the device, the file and the capture, ready to <see cref="Begin"/>: the slow part (the encoder starts up),
    /// which a countdown can hide, so not on the UI thread. Throws when any of them cannot be had: no graphics device,
    /// no encoder for the size, the monitor gone, or capture refused; nothing is left behind then.
    /// </summary>
    public static ScreenRecorder Open(RecordingRequest request, ILog log)
    {
        var recorder = new ScreenRecorder(request, log);
        try { recorder.OpenCore(); }
        catch
        {
            recorder.Release();
            TryDelete(request.Path, log);
            throw;
        }
        return recorder;
    }

    /// <summary>Starts recording: the capture runs from here, and the video's first frame is the screen now. Once, and
    /// not after <see cref="Stop"/>.</summary>
    public void Begin()
    {
        bool stopped;
        lock (_control)
        {
            if (_begun) return;
            stopped = _stop;
            if (!stopped)
            {
                _begun = true;
                _clock.Start(Now());
            }
        }
        if (stopped)
        {
            // Stopped before it began: the monitor went away during the countdown. There is no thread to end it.
            if (!_done.Task.IsCompleted) { Release(); TryDelete(_request.Path, _log); _done.TrySetResult(null); }
            return;
        }
        try { _session!.StartCapture(); }
        catch (Exception e)
        {
            // Capture refused at the last moment: nothing was recorded, and there is no thread to clean up after it.
            _log.Warn("record: the capture did not start: " + e);
            lock (_control) { _failure = Describe(e); _stop = true; }
            Release();
            TryDelete(_request.Path, _log);
            _done.TrySetResult(null);
            return;
        }
        _thread.Start();
        _log.Info($"record: started, {_area.Width}x{_area.Height} at ({_request.Area.Left},{_request.Area.Top}), {(_request.Hdr ? "HDR tonemapped with " + _request.Curve.Name : "SDR")}, cursor {(_request.Cursor ? "on" : "off")}");
    }

    /// <summary>Completes when the recording has ended, however it ended, with the file, or null when nothing was
    /// kept (discarded, or failed before a frame).</summary>
    public Task<RecordingResult?> Completion => _done.Task;

    /// <summary>Why the recording ended by itself (the monitor went away, the graphics device was lost, the encoder
    /// failed), once <see cref="Completion"/> has completed; null when it was stopped.</summary>
    public string? Failure { get { lock (_control) return _failure; } }

    /// <summary>The time recorded so far, pauses excluded.</summary>
    public TimeSpan Elapsed { get { lock (_control) return _clock.Elapsed(Now()); } }

    public bool Paused { get { lock (_control) return _clock.Paused; } }

    public RecordingRequest Request => _request;

    public bool Pause()
    {
        lock (_control) return !_stop && _clock.Pause(Now());
    }

    public bool Resume()
    {
        bool resumed;
        lock (_control) resumed = !_stop && _clock.Resume(Now());
        if (resumed) Wake();
        return resumed;
    }

    /// <summary>Ends the recording: kept, the file is finished and returned; otherwise it is deleted. The first call
    /// decides. Returns <see cref="Completion"/>.</summary>
    public Task<RecordingResult?> Stop(bool keep = true)
    {
        bool begun;
        lock (_control)
        {
            if (!_stop) { _stop = true; _keep = keep; }
            begun = _begun;
        }
        if (!begun && !_done.Task.IsCompleted)
        {
            // Opened and never begun (cancelled during the countdown): there is no thread to end it.
            Release();
            TryDelete(_request.Path, _log);
            _done.TrySetResult(null);
        }
        Wake();
        return _done.Task;
    }

    private void Wake()
    {
        try { _wake.Set(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>The capture clock: QPC, which Windows.Graphics.Capture stamps its frames on, as a TimeSpan.</summary>
    private static TimeSpan Now() => TimeSpan.FromTicks((long)(Stopwatch.GetTimestamp() * TicksPerQpc));

    private static readonly double TicksPerQpc = (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;

    private void OpenCore()
    {
        IntRect m = _request.Monitor;
        long centre = ((long)(uint)(m.Top + m.Height / 2) << 32) | (uint)(m.Left + m.Width / 2);
        IntPtr monitor = User32.MonitorFromPoint(centre, MonitorDefaultToNull);
        if (monitor == IntPtr.Zero) throw new InvalidOperationException("The monitor to record is no longer connected");

        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out ID3D11Device? created).CheckError();
        ID3D11Device device = _device = created ?? throw new InvalidOperationException("No graphics device was made");
        using (ID3D11Multithread mt = device.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
        _winrt = ScreenCapture.WinRtDevice(device);
        Format format = _request.Hdr ? Format.R16G16B16A16_Float : Format.B8G8R8A8_UNorm;
        lock (_gate)
        {
            _gpu = GpuTonemapper.Create(device, _gate, _log);
            _converter = new VideoConverter(_gpu, format, _area.Width, _area.Height);
        }
        _writer = Mp4Writer.Open(_request.Path, _area.Width, _area.Height, _request.FrameRate, _log);

        _item = ScreenCapture.CreateItem(monitor);
        if (_item.Size.Width != m.Width || _item.Size.Height != m.Height)
            throw new InvalidOperationException($"The monitor is {_item.Size.Width} x {_item.Size.Height} now, not {m.Width} x {m.Height}");
        _item.Closed += OnItemClosed;
        var pixels = _request.Hdr ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrt, pixels, PoolFrames, _item.Size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = _request.Cursor;
        try { _session.IsBorderRequired = false; } catch (Exception e) { _log.Debug("record: the capture border stays on: " + e.Message); }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object args) => Wake();

    private void OnItemClosed(GraphicsCaptureItem item, object args)
    {
        lock (_control)
        {
            _failure ??= "The monitor being recorded was disconnected";
            _stop = true;
        }
        Wake();
    }

    private void Run()
    {
        RecordingResult? result = null;
        try { Loop(); }
        catch (Exception e)
        {
            // Set before Finish, which then writes nothing more and only finishes what was recorded until now.
            lock (_control) _failure ??= Describe(e);
            _log.Warn("record: stopped by an error: " + e);
        }
        try { result = Finish(); }
        catch (Exception e)
        {
            lock (_control) _failure ??= Describe(e);
            _log.Warn("record: the file was not finished, and is deleted: " + e);
            result = null;
        }
        finally { Release(); }
        // After Release, which shuts the writer down: until then Media Foundation can still hold the file.
        if (result == null) TryDelete(_request.Path, _log);
        _done.TrySetResult(result);
    }

    private static string Describe(Exception e) => e is SharpGen.Runtime.SharpGenException s
        ? $"{s.ResultCode.Description} (0x{s.ResultCode.Code:X8})"
        : e.Message;

    /// <summary>Writes each new frame as its slot opens, until stopped. Waits for the next frame, or, with one held, for
    /// the next slot.</summary>
    private void Loop()
    {
        while (true)
        {
            bool paused;
            TimeSpan now;
            lock (_control)
            {
                if (_stop) return;
                paused = _clock.Paused;
                now = _clock.Elapsed(Now());
            }
            TakeNewest();
            if (_held != null && !paused && _pacing.IsOpen(now)) WriteHeld();
            int wait = Timeout.Infinite;
            if (_held != null && !paused) wait = Math.Max(1, (int)Math.Ceiling((_pacing.Next - now).TotalMilliseconds));
            _wake.WaitOne(wait);
        }
    }

    /// <summary>Takes every frame the capture has queued, keeping only the newest.</summary>
    private void TakeNewest()
    {
        while (_pool?.TryGetNextFrame() is { } frame)
        {
            _held?.Dispose();
            _held = frame;
        }
    }

    private void WriteHeld()
    {
        Direct3D11CaptureFrame frame = _held!;
        _held = null;
        try
        {
            global::Windows.Graphics.SizeInt32 size = frame.ContentSize;
            if (size.Width != _request.Monitor.Width || size.Height != _request.Monitor.Height)
                throw new InvalidOperationException($"The monitor changed to {size.Width} x {size.Height} during the recording");
            TimeSpan captured;
            lock (_control) captured = _clock.Elapsed(frame.SystemRelativeTime);
            TimeSpan stamp = _pacing.Take(captured);
            IMFMediaBuffer buffer;
            using (IDirect3DSurface surface = frame.Surface)
            using (ID3D11Texture2D texture = ScreenCapture.TextureOf(surface))
            {
                TonemapCurve? curve = _request.Hdr ? _request.Curve : null;
                lock (_gate) buffer = _writer!.Frame(p => _converter!.Convert(texture, _area.Left, _area.Top, curve, (byte*)p));
            }
            try { _writer.Write(buffer, stamp); }
            catch { buffer.Dispose(); throw; }
            _last?.Dispose();
            _last = buffer;
            _frames++;
        }
        finally { frame.Dispose(); }
    }

    /// <summary>
    /// Stops the capture and finishes the file, or returns null when it is not kept (<see cref="Run"/> deletes it).
    /// The newest frame is written if its slot is still to come, and the last one once more just before the stop, so
    /// the video does not end at the last change on screen. Stopped while paused, the frames of the pause are dropped:
    /// whatever was on screen then was meant to stay out of the video. Neither extra write may lose the frames already
    /// encoded, so a failure in them only ends the video sooner.
    /// </summary>
    private RecordingResult? Finish()
    {
        bool keep, failed, paused;
        TimeSpan end;
        lock (_control) { keep = _keep; failed = _failure != null; paused = _clock.Paused; end = _clock.Elapsed(Now()); }
        if (keep && _writer != null && !failed && !paused)
        {
            try
            {
                TakeNewest();
                if (_held != null) WriteHeld();
            }
            catch (Exception e) { failed = true; _log.Warn("record: the last frame was not written: " + e.Message); }
        }
        StopCapture();
        if (!keep || _writer == null || _frames == 0)
        {
            _writer?.Dispose();
            _writer = null;
            if (keep) _log.Info("record: nothing was captured; no file is kept");
            else _log.Info($"record: discarded after {end.TotalSeconds:F1} s");
            return null;
        }
        TimeSpan tail = end - _pacing.Interval;
        if (!failed && _last != null && _pacing.IsOpen(tail))
        {
            try { _writer.Write(_last, _pacing.Take(tail)); }
            catch (Exception e) { _log.Warn("record: the video was not held to the stop: " + e.Message); }
        }
        TimeSpan duration = (_pacing.Last ?? TimeSpan.Zero) + _pacing.Interval;
        _writer.Finish();
        var result = new RecordingResult(_request.Path, duration, _writer.Codec, _area.Width, _area.Height, _frames);
        _log.Info($"record: saved {Path.GetFileName(_request.Path)}, {duration.TotalSeconds:F1} s, {_frames} frames, {result.Codec}");
        return result;
    }

    private void StopCapture()
    {
        if (_pool != null) _pool.FrameArrived -= OnFrameArrived;
        if (_item != null) _item.Closed -= OnItemClosed;
        _held?.Dispose(); _held = null;
        _session?.Dispose(); _session = null;
        _pool?.Dispose(); _pool = null;
    }

    /// <summary>Frees everything the recording held, in the reverse of the order it was made in. Safe after a partial
    /// <see cref="Open"/>.</summary>
    private void Release()
    {
        try { StopCapture(); } catch (Exception e) { _log.Debug("record: capture teardown: " + e.Message); }
        if (_item != null) { ((IWinRTObject)_item).NativeObject.Dispose(); _item = null; }
        _last?.Dispose(); _last = null;
        try { _writer?.Dispose(); } catch (Exception e) { _log.Debug("record: writer teardown: " + e.Message); }
        _writer = null;
        lock (_gate)
        {
            _converter?.Dispose(); _converter = null;
            _gpu?.Release(); _gpu = null;
        }
        _winrt?.Dispose(); _winrt = null;
        if (_device != null)
        {
            // The device's own reference to its context: each read of ImmediateContext takes one, released here.
            using (ID3D11DeviceContext context = _device.ImmediateContext) { context.ClearState(); context.Flush(); }
            _device.Dispose();
            _device = null;
        }
        try { _wake.Dispose(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Deletes a recording that is not kept. Media Foundation can hold the file a moment after its writer is
    /// let go, so a refused delete is tried again briefly, and logged if it never succeeds.</summary>
    private static void TryDelete(string path, ILog log)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { File.Delete(path); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4) { log.Warn($"record: {Path.GetFileName(path)} could not be deleted: {e.Message}"); return; }
                Thread.Sleep(50);
            }
        }
    }
}
