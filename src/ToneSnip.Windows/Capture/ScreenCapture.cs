using System.Runtime.InteropServices;
using SharpGen.Runtime;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Windows.Display;
using ToneSnip.Windows.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Security.Authorization.AppCapabilityAccess;
using WinRT;

namespace ToneSnip.Windows.Capture;

/// <summary>Called once per output, on a worker thread, with that output's frame mapped for reading: B8G8R8A8_UNorm for
/// an SDR output, R16G16B16A16_Float (scRGB) for an HDR one, in desktop orientation.</summary>
public delegate void FrameConsumer(int output, IntPtr data, int rowPitch, int width, int height, Format format);

/// <summary>
/// Takes one frame of every monitor through Windows.Graphics.Capture, on demand.
/// <para>
/// A capture session per snip rather than one kept open: little is held between snips, one device serves monitors on
/// any adapter, and it works with the displays asleep.
/// </para>
/// <para>
/// Not thread-safe across callers: one <see cref="Capture"/> at a time, which the grabber guarantees.
/// </para>
/// </summary>
public sealed class ScreenCapture(ILog log) : IDisposable
{
    /// <summary>The reason <see cref="Capture"/> gives for an output whose copy out of its frame began and did not
    /// finish in time. Its consumer may still be writing, so the caller must not fill that output's own buffers.</summary>
    public const string StillCopyingReason = "the frame copy did not finish";

    /// <summary>How long any one lock or copy in a capture may be waited for. Every wait is bounded so a WGC or driver
    /// call that never returns cannot block every later snip.</summary>
    private const int GateBudgetMs = 2000;

    /// <summary>Guards the D3D device and its immediate context: created, used, flushed and released under it.</summary>
    private readonly object _gate = new();
    /// <summary>Guards the monitor enumeration. Never held across a call into the capture system or the driver.</summary>
    private readonly object _displayGate = new();
    /// <summary>Guards the kept capture items. Held only to read or change the dictionary; items are created outside it.</summary>
    private readonly object _streamGate = new();
    private DisplaySet? _displays;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private bool _accessAsked;

    /// <summary>False on a Windows without Windows.Graphics.Capture; every output then goes to GDI.</summary>
    public static bool Supported
    {
        get { try { return GraphicsCaptureSession.IsSupported(); } catch { return false; } }
    }

    /// <summary>The monitors, enumerated on first use and again after <see cref="Refresh"/>.</summary>
    public IReadOnlyList<OutputHandle> Outputs
    {
        get { lock (_displayGate) return (_displays ??= Enumerate()).Outputs; }
    }

    /// <summary>Enumerates the monitors again after a display change. The new set is built before the old one is
    /// disposed, so a failed enumeration leaves the old set in place.</summary>
    public void Refresh()
    {
        DisplaySet fresh = Enumerate();
        DisplaySet? old;
        lock (_displayGate) { old = _displays; _displays = fresh; }
        ReleaseStreams();   // monitor handles do not survive a display change
        old?.Dispose();
    }

    /// <summary>
    /// Re-reads each monitor's SDR white level, which the "SDR content brightness" slider changes without a display
    /// change. Cheap: one QueryDisplayConfig.
    /// </summary>
    public void RefreshWhiteLevels()
    {
        Dictionary<string, DisplayInfo> info;
        try { info = DisplayConfigInterop.Query(); }
        catch (Exception e) { log.Debug("DisplayConfig: " + e.Message); return; }
        lock (_displayGate)
        {
            if (_displays == null) return;
            foreach (OutputHandle o in _displays.Outputs)
            {
                if (!info.TryGetValue(o.DeviceName, out DisplayInfo? d) || Math.Abs(d.SdrWhiteNits - o.SdrWhiteNits) < 0.5f) continue;
                log.Info($"{o.DeviceName}: SDR white {o.SdrWhiteNits:F0} -> {d.SdrWhiteNits:F0} nits");
                o.SdrWhiteNits = d.SdrWhiteNits;
            }
        }
    }

    private DisplaySet Enumerate()
    {
        Dictionary<string, DisplayInfo> info;
        try { info = DisplayConfigInterop.Query(); } catch (Exception e) { log.Warn("DisplayConfig: " + e.Message); info = new(); }
        DisplaySet set = OutputEnumerator.Enumerate(info, log.Info);
        foreach (OutputHandle o in set.Outputs) log.Debug($"output: {o}");
        return set;
    }

    /// <summary>
    /// One frame of each output in <paramref name="outputs"/>, in parallel, handed to <paramref name="consume"/>.
    /// Returns, per output, null when its frame was consumed, or why it was not, so the caller can copy it another way.
    /// An output still waiting when <paramref name="timeoutMs"/> runs out is abandoned and its consumer is not called
    /// afterwards, except for <see cref="StillCopyingReason"/>, whose consumer may still be running.
    /// </summary>
    public string?[] Capture(IReadOnlyList<OutputHandle> outputs, FrameConsumer consume, int timeoutMs)
    {
        var reasons = new string?[outputs.Count];
        if (!Supported) { Array.Fill(reasons, "Windows.Graphics.Capture is not available"); return reasons; }
        ID3D11Device device; ID3D11DeviceContext context; IDirect3DDevice winrt;
        if (!Monitor.TryEnter(_gate, GateBudgetMs))
        {
            log.Warn($"capture: the graphics device was still busy after {GateBudgetMs} ms");
            Array.Fill(reasons, "the graphics device was busy");
            return reasons;
        }
        try { (device, context, winrt) = EnsureDeviceLocked(); }
        catch (Exception e)
        {
            // No hardware device (a VM, some Remote Desktop sessions): every output goes to GDI rather than the snip failing.
            Array.Fill(reasons, "no graphics device: " + e.Message);
            ReleaseDeviceLocked();
            return reasons;
        }
        finally { Monitor.Exit(_gate); }
        AskForBorderlessOnce();
        var states = new OutputState[outputs.Count];
        var tasks = new Task[outputs.Count];
        for (int i = 0; i < outputs.Count; i++)
        {
            var state = states[i] = new OutputState();
            int index = i;
            OutputHandle o = outputs[i];
            tasks[i] = Task.Run(() => One(index, o, device, context, winrt, state, consume, timeoutMs));
        }
        try { Task.WaitAll(tasks, timeoutMs + 500); } catch (AggregateException) { }
        bool lost = false;
        for (int i = 0; i < outputs.Count; i++)
        {
            lock (states[i])
            {
                if (states[i].Done) { reasons[i] = states[i].Reason; lost |= states[i].DeviceLost; continue; }
                if (states[i].Converting)
                {
                    // The copy had already begun: wait for it (bounded) rather than let the caller's fallback race it on
                    // the same buffer.
                    long deadline = Environment.TickCount64 + GateBudgetMs;
                    while (!states[i].Done && Environment.TickCount64 < deadline) Monitor.Wait(states[i], (int)Math.Max(1, deadline - Environment.TickCount64));
                    if (states[i].Done) { reasons[i] = states[i].Reason; lost |= states[i].DeviceLost; continue; }
                    states[i].Abandoned = true;
                    reasons[i] = StillCopyingReason;
                    continue;
                }
                states[i].Abandoned = true;
                reasons[i] = $"no frame in {timeoutMs} ms";
            }
        }
        if (Monitor.TryEnter(_gate, GateBudgetMs))
        {
            try
            {
                try { context.Flush(); } catch { }
                // Trim releases the driver pools grown by the staging copies, which would otherwise stay in the working set.
                try { using IDXGIDevice3 dxgi = device.QueryInterface<IDXGIDevice3>(); dxgi.Trim(); } catch { }
                // WGC can report a driver reset as its own COM failure, so ask the device directly.
                try { if (device.DeviceRemovedReason.Failure) lost = true; } catch { lost = true; }
            }
            finally { Monitor.Exit(_gate); }
        }
        else log.Warn("capture: the flush and trim were skipped, the graphics device was still busy");
        if (lost) { log.Warn("capture: the graphics device was lost; a new one is made for the next snip"); ReleaseDevice(); }
        return reasons;
    }

    private sealed class OutputState
    {
        public bool Converting, Done, Abandoned, DeviceLost;
        public string? Reason = "not started";
    }

    private void One(int index, OutputHandle o, ID3D11Device device, ID3D11DeviceContext context, IDirect3DDevice winrt,
                     OutputState state, FrameConsumer consume, int timeoutMs)
    {
        string? reason = null;
        bool lost = false;
        long deadline = Environment.TickCount64 + timeoutMs;
        int Remaining() => (int)Math.Max(0, deadline - Environment.TickCount64);
        try
        {
            Stream stream = StreamFor(o);
            using Direct3D11CaptureFramePool pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrt, stream.Format, 1, stream.Size);
            using var arrived = new ManualResetEventSlim();
            pool.FrameArrived += (_, _) => { try { arrived.Set(); } catch (ObjectDisposedException) { } };   // a frame racing a timed-out teardown
            using GraphicsCaptureSession session = pool.CreateCaptureSession(stream.Item);
            session.IsCursorCaptureEnabled = false;
            try { session.IsBorderRequired = false; } catch (Exception e) { log.Debug("capture: the border stays on: " + e.Message); }
            session.StartCapture();
            if (!arrived.Wait(Remaining())) { reason = $"no frame in {timeoutMs} ms"; return; }
            using Direct3D11CaptureFrame? frame = pool.TryGetNextFrame();
            if (frame == null) { reason = "the frame pool was empty"; return; }
            // Check the frame's content size, not the pool's: after a mode change or rotation the enumeration can be stale
            // and a pool created from it would always match.
            global::Windows.Graphics.SizeInt32 content = frame.ContentSize;
            if (content.Width != o.Width || content.Height != o.Height) { reason = $"the frame is {content.Width}x{content.Height}, the monitor {o.Width}x{o.Height}"; return; }
            // The surface is its own IClosable WinRT object: closed here rather than left to a finalizer holding a
            // monitor-sized texture.
            using IDirect3DSurface surface = frame.Surface;
            using ID3D11Texture2D texture = TextureOf(surface);
            Texture2DDescription d = texture.Description;
            lock (state) { if (state.Abandoned) return; }
            if (!Monitor.TryEnter(_gate, Math.Max(Remaining(), 250))) { reason = "the graphics device was busy"; return; }
            try
            {
                // Converting is set only with the gate held, so an output abandoned while waiting for it never starts a copy.
                lock (state)
                {
                    if (state.Abandoned) return;
                    state.Converting = true;
                }
                using ID3D11Texture2D staging = device.CreateTexture2D(new Texture2DDescription(d.Format, d.Width, d.Height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
                context.CopyResource(staging, texture);
                MappedSubresource map = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try { consume(index, map.DataPointer, (int)map.RowPitch, (int)d.Width, (int)d.Height, d.Format); }
                finally { context.Unmap(staging, 0); }
            }
            finally { Monitor.Exit(_gate); }
        }
        catch (SharpGenException e) when (e.ResultCode == Vortice.DXGI.ResultCode.DeviceRemoved || e.ResultCode == Vortice.DXGI.ResultCode.DeviceReset)
        {
            reason = "graphics device lost: " + e.ResultCode.Description;
            lost = true;
        }
        catch (Exception e) { reason = e.GetType().Name + ": " + e.Message; }
        finally
        {
            lock (state)
            {
                state.Reason = reason;
                state.DeviceLost = lost;
                state.Done = true;
                Monitor.PulseAll(state);
            }
        }
    }

    /// <summary>
    /// The D3D11 device every capture uses, created on first use and kept until <see cref="ReleaseDevice"/> because
    /// creating it is slow. Multithread-protected, since each frame pool raises FrameArrived on its own thread; the
    /// context is only used under the gate. Called with <see cref="_gate"/> held.
    /// </summary>
    private (ID3D11Device, ID3D11DeviceContext, IDirect3DDevice) EnsureDeviceLocked()
    {
        if (_device == null)
        {
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
            using (ID3D11Multithread mt = device.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
            _device = device;
            _context = context;
            _winrtDevice = WinRtDevice(device);
            log.Debug("capture: graphics device created");
        }
        return (_device, _context!, _winrtDevice!);
    }

    /// <summary>Releases the device between bursts of snips (the grabber calls this when it releases its frame
    /// buffers); the next snip creates a new one. Skipped, with a warning, while the device is busy.</summary>
    public void ReleaseDevice()
    {
        ReleaseStreams();   // released with the device, so a new device starts from new items
        if (!Monitor.TryEnter(_gate, GateBudgetMs)) { log.Warn("capture: the graphics device was not released, it was still busy"); return; }
        try { ReleaseDeviceLocked(); }
        finally { Monitor.Exit(_gate); }
    }

    private void ReleaseDeviceLocked()
    {
        if (_device == null) return;
        _winrtDevice?.Dispose(); _winrtDevice = null;
        _context?.ClearState(); _context?.Flush(); _context?.Dispose(); _context = null;
        _device.Dispose(); _device = null;
        log.Debug("capture: graphics device released");
    }

    /// <summary>
    /// Asked once per process. An unpackaged app is allowed borderless capture outright; a packaged one declares
    /// graphicsCaptureWithoutBorder and is asked once per install. A refusal only shows the yellow border briefly.
    /// </summary>
    private void AskForBorderlessOnce()
    {
        if (_accessAsked) return;
        _accessAsked = true;
        try
        {
            // A packaged app's first request shows a consent prompt. The snip waits for the answer and for the prompt to
            // close; capturing sooner puts the prompt in the snip.
            bool prompt = BorderlessPromptPending();
            BorderlessConsent.Plan plan = BorderlessConsent.PlanFor(prompt);
            var ask = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask();
            bool answered = ask.Wait(plan.AnswerWaitMs);
            log.Info("capture: borderless access " + (answered ? ask.Result.ToString() : $"not answered in {plan.AnswerWaitMs / 1000} s") + (prompt ? " (the user was asked)" : ""));
            if (plan.SettleMs > 0)
            {
                Thread.Sleep(plan.SettleMs);
                Dwm.Flush();
                Dwm.Flush();
            }
        }
        catch (Exception e) { log.Debug("capture: borderless access not asked: " + e.Message); }
    }

    /// <summary>Whether asking for a borderless capture would show the user a prompt, checked without showing it.</summary>
    private bool BorderlessPromptPending()
    {
        try
        {
            return AppCapability.Create("graphicsCaptureWithoutBorder").CheckAccess() == AppCapabilityAccessStatus.UserPromptRequired;
        }
        catch (Exception e) { log.Debug("capture: borderless access state unknown: " + e.Message); return false; }
    }

    public void Dispose()
    {
        ReleaseDevice();
        lock (_displayGate) { _displays?.Dispose(); _displays = null; }
    }

    // ----- WinRT interop -----

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private static Guid _itemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    /// <summary>The GraphicsCaptureItem factory's interop interface, fetched once so each snip does not create a COM
    /// wrapper that only a finalizer releases.</summary>
    private static readonly Lazy<IGraphicsCaptureItemInterop> ItemFactory = new(() => GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>());

    /// <summary>
    /// A monitor's capture item, kept across snips and released with the device or on a display change, because an item
    /// created per snip leaks ALPC ports and completion packets. The frame pool and session cannot be kept (a second
    /// session on a pool fails with E_UNEXPECTED, even after Recreate), so both are created and closed per snip.
    /// </summary>
    private sealed class Stream : IDisposable
    {
        public required DirectXPixelFormat Format;
        public required global::Windows.Graphics.SizeInt32 Size;
        public required GraphicsCaptureItem Item;
        public required IObjectReference ItemReference;

        public void Dispose() => ItemReference.Dispose();   // GraphicsCaptureItem is not IClosable: released here, not by a finalizer
    }

    /// <summary>The kept streams by HMONITOR. Touched only under <see cref="_streamGate"/>.</summary>
    private readonly Dictionary<IntPtr, Stream> _streams = new();

    private Stream StreamFor(OutputHandle o)
    {
        var format = o.Hdr ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized;
        lock (_streamGate)
        {
            if (_streams.TryGetValue(o.Monitor, out Stream? kept))
            {
                if (kept.Format == format && kept.Size.Width == o.Width && kept.Size.Height == o.Height) return kept;
                kept.Dispose();   // the monitor went in or out of HDR, or changed mode
                _streams.Remove(o.Monitor);
            }
        }
        // Created outside the lock: a capture-system call that never returns must not block other monitors' lookups.
        GraphicsCaptureItem item = CreateItem(o.Monitor);
        var stream = new Stream
        {
            Format = format,
            Size = item.Size,
            Item = item,
            ItemReference = ((IWinRTObject)item).NativeObject,
        };
        lock (_streamGate)
        {
            if (_streams.TryGetValue(o.Monitor, out Stream? raced)) { stream.Dispose(); return raced; }
            _streams[o.Monitor] = stream;
        }
        return stream;
    }

    private void ReleaseStreams()
    {
        lock (_streamGate)
        {
            foreach (Stream s in _streams.Values) { try { s.Dispose(); } catch { } }
            _streams.Clear();
        }
    }

    private static GraphicsCaptureItem CreateItem(IntPtr monitor)
    {
        IntPtr p = ItemFactory.Value.CreateForMonitor(monitor, ref _itemIid);
        try { return GraphicsCaptureItem.FromAbi(p); }
        finally { Marshal.Release(p); }
    }

    private static IDirect3DDevice WinRtDevice(ID3D11Device device)
    {
        using IDXGIDevice dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr p));
        try { return MarshalInterface<IDirect3DDevice>.FromAbi(p); }
        finally { Marshal.Release(p); }
    }

    private static ID3D11Texture2D TextureOf(IDirect3DSurface surface)
    {
        var access = WinRT.CastExtensions.As<IDirect3DDxgiInterfaceAccess>(surface);
        try
        {
            Guid iid = typeof(ID3D11Texture2D).GUID;
            return new ID3D11Texture2D(access.GetInterface(ref iid));
        }
        finally { Marshal.ReleaseComObject(access); }
    }
}
