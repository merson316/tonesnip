using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ToneSnip.App.Capture;
using ToneSnip.App.Interop;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Config;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using ToneSnip.Windows.Display;
using ToneSnip.Windows.Overlay;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ToneSnip.App;

/// <summary>
/// `tonesnip-debug.exe --memtest [cycles]`: runs the snip lifecycle (frames, the frozen-frame overlay, then Escape)
/// <c>cycles</c> times and prints the process footprint after each. Complements <see cref="LeakTest"/>: full-monitor
/// frames live on the Large Object Heap, so growth can happen without any object surviving its close.
/// <para>
/// Passes when the last cycle's private bytes are within <see cref="PlateauPercent"/> % of cycle 2's, nothing from
/// cycle 1 is still rooted after a forced compacting collect, and every ownership check holds.
/// </para>
/// <para>
/// It opens real overlay windows, so it runs behind <see cref="HarnessGuard"/>; it takes no mutex, tray, hook or host
/// pipe. Frames are synthetic but match the real monitor layout and sizes.
/// </para>
/// </summary>
internal static class MemTest
{
    /// <summary>How far the last cycle's private bytes may sit above cycle 2's and still count as a plateau.</summary>
    private const double PlateauPercent = 15.0;

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);

    private static int _cycles = 6;
    private static int _exit;
    private static IntRect _desktop;
    private static List<OutputInfo> _outputs = new();

    /// <summary>The region each cycle's <see cref="CaptureResult"/> covers: 256 x 256, so the result exercises the
    /// crop and composite without its own allocations distorting the memory table.</summary>
    private static IntRect _region;

    /// <summary>The previous cycle's finished snip and its pixel fingerprint. If a <see cref="CaptureResult"/> held a
    /// pooled buffer instead of a copy, the next grab would change it.</summary>
    private static CaptureResult? _previous;
    private static ulong _previousHash;
    private static int _ownershipFailures;

    /// <summary>Starts the XAML application for this mode, as the leak harness does.</summary>
    internal static int Start(StartupCommand command)
    {
        AttachConsole(-1);   // WinExe: reattach to the launching console so Console.WriteLine is visible
        _cycles = Math.Clamp(command.MemTestCycles, 2, 50);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>   // not `_`: the discard below would bind to the parameter instead
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App { Command = command };
        });
        return _exit;
    }

    internal static async Task Run()
    {
        App app = App.Current;
        try
        {
            _outputs = Describe();
            _desktop = _outputs.Aggregate(IntRect.Empty, (r, o) => r.Union(o.Bounds));
            app.PrimaryMonitor = _outputs[0].Bounds;
            OutputInfo anchor = _outputs.FirstOrDefault(o => o.Hdr) ?? _outputs[0];   // on an HDR panel where there is one, so the half-crop path runs
            _region = new IntRect(anchor.Left + 32, anchor.Top + 32, Math.Min(256, anchor.Width - 32), Math.Min(256, anchor.Height - 32));
            Line($"memtest: {_cycles} cycles of grab + overlay + Escape, synthetic frames on the real monitor layout");
            foreach (OutputInfo o in _outputs) Line($"memtest: {o.DeviceName} {o.Width}x{o.Height} {(o.Hdr ? "hdr" : "sdr")}, {Mb(Bytes(o))} MB of frames");
            Line($"memtest: {Mb(_outputs.Sum(Bytes))} MB of frames per grab across {_outputs.Count} output(s)");
            Line($"memtest: each cycle also builds a real CaptureResult over {_region}, kept alive across the next grab");

            var tracked = new List<(string What, WeakReference<object> Ref)>();
            var pixels = new List<(string What, WeakReference<object> Ref)>();
            var privates = new long[_cycles + 1];
            for (int i = 1; i <= _cycles; i++)
            {
                await Cycle(i, i == 1 ? tracked : null, i == 1 ? pixels : null);
                privates[i] = Report(i);
                if (i == 1 || i == _cycles || i % 50 == 0) Census($"cycle {i}");
            }

            // After the table, so its full-size half crop does not distort the plateau.
            OwnershipAtFullRect();
            _previous = null;

            Collect();
            long settled = Report("settled");
            // Read before the pool is released below: pooled buffers are expected to be alive while the pool holds them.
            Line("memtest: retention after a blocking compacting gen-2 collect (cycle 1's objects)");
            int rooted = 0;
            foreach ((string what, WeakReference<object> r) in tracked)
            {
                bool alive = r.TryGetTarget(out _);
                if (alive) rooted++;
                Line($"memtest:   {what,-22} {(alive ? "ALIVE - still rooted" : "collected")}");
            }
            foreach ((string what, WeakReference<object> r) in pixels)
                Line($"memtest:   {what,-22} {(r.TryGetTarget(out _) ? "alive" : "collected")}   (pooled buffers are alive by design; see the report)");
            Line(_ownershipFailures == 0
                     ? $"memtest: ownership: every finished snip kept its own composite and half crops when the next grab overwrote the pool ({_cycles - 1} sub-rect cycles plus the full-rect pass)"
                     : $"memtest: ownership: FAIL, {_ownershipFailures} check(s) failed");

            // What the app's idle release does: the "released" row is the footprint an idle ToneSnip returns to.
            long pooled = app.Grabber.PooledBytes;
            app.Grabber.ReleaseBuffers();
            Collect();
            long released = Report("released");
            Line($"memtest: {Mb(pooled)} MB of pooled buffers released, private {Mb(settled)} -> {Mb(released)} MB");

            double growth = privates[2] == 0 ? 0 : (privates[_cycles] - privates[2]) * 100.0 / privates[2];
            bool plateau = growth <= PlateauPercent;
            Line($"memtest: cycle 2 private {Mb(privates[2])} MB, cycle {_cycles} private {Mb(privates[_cycles])} MB, {(growth >= 0 ? "+" : "")}{growth:F1} % - {(plateau ? "PLATEAU" : "RAMP")}");
            if (rooted > 0) Line($"memtest: {rooted} of cycle 1's session objects are still rooted");
            bool pass = plateau && rooted == 0 && _ownershipFailures == 0;
            Line(pass ? "memtest: PASS" : "memtest: FAIL");
            _exit = pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            _exit = 1;
            Line("memtest: " + ex);
        }
        app.Exit();
    }

    /// <summary>
    /// One session: frames, overlay windows and toolbar, then Escape. Nothing created here is referenced outside
    /// this method except through weak references.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task Cycle(int cycle, List<(string, WeakReference<object>)>? track, List<(string, WeakReference<object>)>? pixels)
    {
        App app = App.Current;
        // A different picture every cycle, so a result that aliased the pool would be detected.
        List<CapturedOutput> outputs = RealCapture ? app.Grabber.GrabAll() : app.Grabber.GrabSynthetic(_outputs, cycle);
        if (_previous != null && !RealCapture)
        {
            // The grab above overwrote every pooled buffer; last cycle's composite and half crops must be unchanged.
            if (Fingerprint(_previous) != _previousHash)
            {
                _ownershipFailures++;
                Line($"memtest: OWNERSHIP FAIL - cycle {cycle - 1}'s CaptureResult changed when cycle {cycle}'s grab overwrote the pool");
            }
        }
        var session = new Overlay.OverlaySession(outputs, app.Grabber, _desktop, SnipMode.Rectangle, app.Settings, app.Log);
        if (track != null)
        {
            track.Add(("OverlaySession", new WeakReference<object>(session)));
            track.Add(("CapturedOutput list", new WeakReference<object>(outputs)));
        }
        if (pixels != null)
            foreach (CapturedOutput o in outputs)
            {
                if (o.Half != null) pixels.Add(($"HalfImage {o.Info.Index}", new WeakReference<object>(o.Half.Data)));
                pixels.Add(($"BgraImage {o.Info.Index}", new WeakReference<object>(o.Sdr.Data)));
            }
        Task<Overlay.OverlayOutcome> shown = session.Show();
        if (track != null)
            foreach (OverlayWindow w in session.WindowsForHarness) track.Add(($"OverlayWindow {w.Hwnd:X}", new WeakReference<object>(w)));
        await Settle();
        session.Finish(Overlay.OverlayOutcome.Cancelled);   // what Escape does
        await shown;
        await Settle();

        // The rest of a real snip (crop and composite), on a worker thread as SnipSession does it.
        CaptureResult result = await Task.Run(() => CaptureResult.Build(outputs, _region, null, app.Grabber, app.Settings));
        ulong hash = Fingerprint(result);
        if (_previous != null && hash == _previousHash && !RealCapture)
        {
            _ownershipFailures++;
            Line($"memtest: OWNERSHIP FAIL - cycles {cycle - 1} and {cycle} produced identical pixels, so the check proves nothing");
        }
        _previous = result;
        _previousHash = hash;
    }

    /// <summary>
    /// The ownership check for a snip covering exactly one output, where a full-rect shortcut in
    /// <see cref="HalfImage.Crop"/> or <see cref="Compositor.Compose"/> could hand out a pooled buffer. Fingerprints
    /// the composite and every half crop, grabs a different picture, and checks nothing changed.
    /// </summary>
    private static void OwnershipAtFullRect()
    {
        App app = App.Current;
        OutputInfo anchor = _outputs.FirstOrDefault(o => o.Hdr) ?? _outputs[0];
        int at = _outputs.IndexOf(anchor);
        // AutoExposure off: with it on, Build composites a freshly tonemapped image and the pooled frame is never used.
        SnipSettings settings = app.Settings with { AutoExposure = false, Exposure = 1f };

        List<CapturedOutput> frames = app.Grabber.GrabSynthetic(_outputs, 101);
        CaptureResult result = CaptureResult.Build(frames, anchor.Bounds, null, app.Grabber, settings);
        ulong image = Hash(result.Image.Data);
        List<ulong> crops = result.Crops.Select(c => Hash(c.Image.Data)).ToList();
        ulong pooled = Hash(frames[at].Sdr.Data);
        Line($"memtest: ownership at the full rect of {anchor.DeviceName} ({anchor.Width}x{anchor.Height}): composite {result.Image.Width}x{result.Image.Height} and {crops.Count} half crop(s) fingerprinted");

        app.Grabber.GrabSynthetic(_outputs, 202);   // every pooled buffer overwritten, with a picture that differs everywhere
        if (Hash(frames[at].Sdr.Data) == pooled)
        {
            _ownershipFailures++;   // the check has no teeth if the second grab did not actually change the buffers
            Line("memtest: OWNERSHIP FAIL - the second full-rect grab left the pooled frame unchanged, so nothing below proves anything");
        }
        if (Hash(result.Image.Data) != image)
        {
            _ownershipFailures++;
            Line("memtest: OWNERSHIP FAIL - the full-rect snip's composite changed when the next grab overwrote the pool");
        }
        for (int i = 0; i < crops.Count; i++)
            if (Hash(result.Crops[i].Image.Data) != crops[i])
            {
                _ownershipFailures++;
                Line($"memtest: OWNERSHIP FAIL - the full-rect snip's half crop {i} changed when the next grab overwrote the pool");
            }
    }

    /// <summary>Fingerprint of everything a finished snip cut out of pooled buffers: the composite and every half crop.</summary>
    private static ulong Fingerprint(CaptureResult r)
    {
        ulong h = Hash(r.Image.Data);
        foreach (HalfCrop c in r.Crops) h ^= Hash(c.Image.Data) * 1099511628211;
        return h;
    }

    /// <summary>FNV-1a over a stride of the pixels: content-sensitive, and cheap enough to run twice a cycle.</summary>
    private static ulong Hash(byte[] data)
    {
        ulong h = 14695981039346656037;
        for (int i = 0; i < data.Length; i += 7) { h ^= data[i]; h *= 1099511628211; }
        return h;
    }

    private static ulong Hash(ushort[] data)
    {
        ulong h = 14695981039346656037;
        for (int i = 0; i < data.Length; i += 7) { h ^= data[i]; h *= 1099511628211; }
        return h;
    }

    /// <summary>The real monitor layout with each panel's HDR flag and SDR white level, so synthetic frames match what
    /// a real grab produces.</summary>
    private static List<OutputInfo> Describe()
    {
        Dictionary<string, DisplayInfo> displays;
        try { displays = DisplayConfigInterop.Query(); }
        catch (Exception e) { App.Current.Log.Warn("memtest: display config: " + e.Message); displays = new(); }
        var list = new List<OutputInfo>();
        int index = 0;
        foreach ((string device, IntRect bounds) in Native.Monitors())
        {
            displays.TryGetValue(device, out DisplayInfo? d);
            list.Add(new OutputInfo(index++, device, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                                    d?.AdvancedColorEnabled ?? false, d?.SdrWhiteNits ?? 80f, 1000f, d?.FriendlyName));
        }
        if (list.Count == 0) list.Add(new OutputInfo(0, @"\\.\DISPLAY1", 0, 0, 1920, 1080, false, 80f, 400f, "Fallback"));
        return list;
    }

    /// <summary>Managed bytes one output costs a grab: the half frame for an HDR panel, plus the BGRA frame for every panel.</summary>
    private static long Bytes(OutputInfo o) => (long)o.Width * o.Height * (o.Hdr ? 12 : 4);

    // ----- the numbers -------------------------------------------------------------------------------------------

    private static long Report(int cycle) => Report($"cycle {cycle}");

    private static long Report(string label)
    {
        Collect();   // handles held by finalizable wrappers count only once those have run
        using var me = Process.GetCurrentProcess();
        long priv = me.PrivateMemorySize64;
        Line($"memtest: {label,-9} private {Mb(priv),7} MB  task manager {Mb(Interop.ProcessMemory.PrivateWorkingSet()),7} MB  managed {Mb(GC.GetTotalMemory(false)),7} MB  " +
             $"gc {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}  handles {me.HandleCount}  threads {me.Threads.Count}  " +
             $"gdi {GetGuiResources(me.Handle, 0)}  user {GetGuiResources(me.Handle, 1)}");
        return priv;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);

    private static Dictionary<string, int>? _censusStart;

    private static void Census(string label)
    {
        Dictionary<string, int> now = HandleCensus.Take();
        if (_censusStart == null) { _censusStart = now; Line($"memtest: handles at {label}: " + string.Join(", ", now.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}"))); return; }
        var grew = now.Select(k => (k.Key, Delta: k.Value - _censusStart.GetValueOrDefault(k.Key))).Where(d => d.Delta != 0).OrderByDescending(d => d.Delta);
        Line($"memtest: handles grown by {label}: " + string.Join(", ", grew.Select(d => $"{d.Key} {(d.Delta > 0 ? "+" : "")}{d.Delta}")));
    }

    /// <summary>TONESNIP_MEMTEST_REAL=1: grab the real screen instead of synthetic frames, to include the capture's
    /// own cost. Ownership checks are skipped, since two grabs of a still desktop are legitimately identical.</summary>
    private static readonly bool RealCapture = Environment.GetEnvironmentVariable("TONESNIP_MEMTEST_REAL") == "1";

    /// <summary>A full blocking, compacting collect, including the Large Object Heap.</summary>
    private static void Collect()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    /// <summary>As <see cref="LeakTest"/> settles: the overlay's paints and the toolbar's queued close have run.</summary>
    private static async Task Settle()
    {
        await Turn();
        await Turn();
        await Task.Delay(200);
        await Turn();
    }

    private static Task Turn()
    {
        var done = new TaskCompletionSource();
        if (!App.Current.Ui.TryEnqueue(DispatcherQueuePriority.Low, () => done.TrySetResult())) done.TrySetResult();
        return done.Task;
    }

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0");

    private static void Line(string text)
    {
        Console.WriteLine(text);
        App.Current.Log.Info(text);
    }
}
