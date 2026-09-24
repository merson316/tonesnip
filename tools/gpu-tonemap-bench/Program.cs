using System.Diagnostics;
using System.Globalization;
using ToneSnip.Core.Color;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Hdr;
using ToneSnip.Core.Imaging;
using ToneSnip.Core.Tonemap;
using ToneSnip.Windows.Capture;
using ToneSnip.Windows.Display;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GpuTonemapBench;

/// <summary>Measures the app's GPU tonemap path against its CPU path on this machine. See README.md.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        string mode = args.Length > 0 ? args[0] : "help";
        try
        {
            return mode switch
            {
                "list" => List(),
                "compile" => CompileBench(),
                "diff" => Diff(Int(args, "--rounds", 2)),
                "run" => Run(Str(args, "--path") ?? "cpu", Int(args, "--runs", 8), Int(args, "--gap", 1500), Str(args, "--tonemap") ?? "desktop"),
                _ => Usage(),
            };
        }
        catch (Exception e) { Console.WriteLine("FAILED: " + e); return 1; }
    }

    private static int Usage()
    {
        Console.WriteLine("gpu-tonemap-bench list | compile | diff [--rounds N] | run --path cpu|gpu [--runs N] [--gap ms] [--tonemap t]");
        return 2;
    }

    private static string? Str(string[] a, string n) { int i = Array.IndexOf(a, n); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
    private static int Int(string[] a, string n, int d) => Str(a, n) is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : d;

    private static int List()
    {
        using var cap = new ScreenCapture(new ConsoleLog(false));
        foreach (OutputHandle o in cap.Outputs) Console.WriteLine(o);
        using var am = new AdapterMemory();
        foreach (var kv in am.Read()) Console.WriteLine($"adapter {kv.Key}: dedicated {kv.Value.Dedicated:F0} MB, shared {kv.Value.Shared:F0} MB");
        return 0;
    }

    // --------------------------------------------------------------------------------------------------- compile

    /// <summary>What a run-time compile would cost, which the embedded bytecode avoids.</summary>
    private static int CompileBench()
    {
        string src = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Tonemap.hlsl"));
        var times = new List<double>();
        foreach (int _ in Enumerable.Range(0, 4))
        {
            var sw = Stopwatch.StartNew();
            foreach (string entry in ShaderCompiler.EntryPoints) ShaderCompiler.Compile(src, entry);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine($"D3DCompile of {ShaderCompiler.EntryPoints.Length} entry points: first {times[0]:F1} ms (loads d3dcompiler_47.dll), then {string.Join(", ", times.Skip(1).Select(t => t.ToString("F1")))} ms");
        return 0;
    }

    // ------------------------------------------------------------------------------------------------------ diff

    private static readonly (string Tm, float Exposure, float Knee)[] Cases =
    [
        ("desktop", 1f, 1f), ("desktop", 1f, 0.5f), ("desktop", 3f, 1f), ("desktop", 3f, 0.6f),
        ("hable", 1f, 1f), ("hable", 3f, 1f), ("aces", 1f, 1f), ("aces", 3f, 1f),
    ];

    private sealed record DiffStat(long Pixels, int MaxDiff, long Differing, long Off1, long OffMore);

    private static DiffStat Compare(BgraImage a, BgraImage b)
    {
        byte[] x = a.Data, y = b.Data;
        long differing = 0, off1 = 0, more = 0; int max = 0;
        for (int i = 0; i < x.Length; i += 4)
        {
            int m = 0;
            for (int c = 0; c < 3; c++) m = Math.Max(m, Math.Abs(x[i + c] - y[i + c]));
            if (m > 0) { differing++; if (m == 1) off1++; else more++; }
            max = Math.Max(max, m);
        }
        return new DiffStat(x.Length / 4, max, differing, off1, more);
    }

    private static string Fmt(DiffStat d) => $"max {d.MaxDiff} LSB, {d.Differing} px differ ({100.0 * d.Differing / d.Pixels:F4} %): {d.Off1} by 1, {d.OffMore} by more";

    private static BgraImage Cpu(HalfImage src, string tm, TonemapParams p)
    {
        BgraImage dst = BgraImage.Blank(src.Width, src.Height);
        HalfFrame.Tonemap(src, new TonemapCurve(tm, p), new IntRect(0, 0, src.Width, src.Height), dst, 0, 0);
        return dst;
    }

    private static BgraImage Gpu(GpuHdrFrame f, string tm, TonemapParams p)
    {
        BgraImage dst = BgraImage.Blank(f.Width, f.Height);
        f.Tonemap(new TonemapCurve(tm, p), new IntRect(0, 0, f.Width, f.Height), dst, 0, 0);
        return dst;
    }

    /// <summary>Every half bit pattern in every channel (NaN, inf, negatives, subnormals included), and a hue ramp.</summary>
    private static HalfImage Stress()
    {
        var img = new HalfImage(256, 512);
        ushort[] d = img.Data;
        for (int i = 0; i < 65536; i++)
        {
            d[i * 4] = (ushort)i; d[i * 4 + 1] = (ushort)((i * 7919) & 0xFFFF); d[i * 4 + 2] = (ushort)((i * 104729) & 0xFFFF); d[i * 4 + 3] = 0x3C00;
        }
        for (int i = 65536; i < 131072; i++)
        {
            int k = i - 65536;
            float l = MathF.Pow(2f, -14f + 30f * (k % 256) / 255f);
            float h = k / 256 / 256f * 6.2831853f;
            float r = l * (0.6f + 0.4f * MathF.Cos(h)), g = l * (0.6f + 0.4f * MathF.Cos(h - 2.094f)), b = l * (0.6f + 0.4f * MathF.Cos(h + 2.094f));
            d[i * 4] = Transfer.FloatToHalf(r); d[i * 4 + 1] = Transfer.FloatToHalf(g); d[i * 4 + 2] = Transfer.FloatToHalf(b); d[i * 4 + 3] = 0x3C00;
        }
        return img;
    }

    /// <summary>
    /// Real frames: each HDR frame is kept on the GPU as the app keeps it, its pixels read back whole (bit-exact, which
    /// the crop check confirms), and every case tonemapped both ways. Then the stress image on every adapter and WARP.
    /// </summary>
    private static int Diff(int rounds)
    {
        var log = new ConsoleLog(false);
        using var cap = new ScreenCapture(log);
        IReadOnlyList<OutputHandle> outs = cap.Outputs;
        for (int round = 0; round < rounds; round++)
        {
            var kept = new GpuHdrFrame?[outs.Count];
            string?[] reasons = cap.Capture(outs, (_, _, _, _, _, _) => null, 3000, (i, frame) => { kept[i] = frame; return null; });
            for (int i = 0; i < outs.Count; i++)
            {
                OutputHandle o = outs[i];
                using GpuHdrFrame? g = kept[i];
                if (g == null) { Console.WriteLine($"{o.DeviceName}: SDR or no frame ({reasons[i] ?? "SDR"})"); continue; }
                HalfImage half = g.Crop(new IntRect(0, 0, g.Width, g.Height));
                Console.WriteLine($"round {round + 1}, {o.DeviceName} {o.Width}x{o.Height}, SDR white {o.SdrWhiteNits:F0}, peak {o.MaxLuminance:F0}");
                foreach (var c in Cases)
                {
                    var p = new TonemapParams { SdrWhiteNits = o.SdrWhiteNits, PeakNits = o.MaxLuminance, Exposure = c.Exposure, Knee = c.Knee };
                    Console.WriteLine($"  {c.Tm,-7} exp {c.Exposure} knee {c.Knee}: " + Fmt(Compare(Cpu(half, c.Tm, p), Gpu(g, c.Tm, p))));
                }
                Consumers(o, half, g);
            }
            Thread.Sleep(500);
        }
        cap.ReleaseDevice();
        StressEveryAdapter();
        return 0;
    }

    /// <summary>The stress image on a device of every hardware adapter and WARP: pow and exp2 precision is the
    /// driver's, so parity on one vendor says nothing about another.</summary>
    private static void StressEveryAdapter()
    {
        HalfImage s = Stress();
        var devices = new List<(string Name, ID3D11Device Device)>();
        using (IDXGIFactory1 f = DXGI.CreateDXGIFactory1<IDXGIFactory1>())
        {
            for (uint i = 0; f.EnumAdapters1(i, out IDXGIAdapter1 a).Success; i++)
            {
                using (a)
                {
                    if ((a.Description1.Flags & AdapterFlags.Software) != 0) continue;
                    if (D3D11.D3D11CreateDevice(a, Vortice.Direct3D.DriverType.Unknown, DeviceCreationFlags.BgraSupport, [Vortice.Direct3D.FeatureLevel.Level_11_0], out ID3D11Device? d).Success)
                        devices.Add((a.Description1.Description, d!));
                }
            }
        }
        if (D3D11.D3D11CreateDevice(null, Vortice.Direct3D.DriverType.Warp, DeviceCreationFlags.BgraSupport, [Vortice.Direct3D.FeatureLevel.Level_11_0], out ID3D11Device? w).Success)
            devices.Add(("WARP", w!));
        foreach (var (name, device) in devices)
        {
            object gate = new();
            GpuTonemapper tm;
            lock (gate) tm = GpuTonemapper.Create(device, gate, new ConsoleLog(false));
            using (GpuHdrFrame g = tm.Upload(s.Width, s.Height, (y, row) => s.Data.AsSpan(y * s.Width * 4, s.Width * 4).CopyTo(row)))
            {
                Console.WriteLine($"stress image on {name}:");
                foreach (var c in Cases)
                {
                    var p = new TonemapParams { SdrWhiteNits = 212, PeakNits = 1000, Exposure = c.Exposure, Knee = c.Knee };
                    Console.WriteLine($"  {c.Tm,-7} exp {c.Exposure} knee {c.Knee}: " + Fmt(Compare(Cpu(s, c.Tm, p), Gpu(g, c.Tm, p))));
                }
                ZebraMask zg = g.Zebra(212f / 80f, 1.3f), zc = ZebraMask.Of(s, 212f / 80f, 1.3f);
                Console.WriteLine($"  zebra: {zg.Bits.Where((v, i) => v != zc.Bits[i]).Count()} of {zg.Bits.Length} mask words differ");
            }
            lock (gate) tm.Release();
            device.Dispose();
        }
    }

    /// <summary>The GPU answers for the HDR consumers against the CPU ones on the same frame.</summary>
    private static void Consumers(OutputHandle o, HalfImage half, GpuHdrFrame g)
    {
        var rng = new Random(1);
        int bad = 0;
        var sw = Stopwatch.StartNew();
        for (int k = 0; k < 200; k++)
        {
            int x = rng.Next(o.Width), y = rng.Next(o.Height);
            if (!g.TrySample(x, y, out float r, out float gg, out float b) || (r, gg, b) != half.Sample(x, y)) bad++;
        }
        Console.WriteLine($"  nits under cursor: 200 one-pixel readbacks, {sw.Elapsed.TotalMilliseconds / 200:F3} ms each, {bad} mismatches");
        var rect = new IntRect(o.Width / 5, o.Height / 5, o.Width / 2, o.Height / 2);
        sw.Restart();
        g.TryStats(rect, out float gp, out float gm);
        double gms = sw.Elapsed.TotalMilliseconds;
        float cp = 0; double cs = 0;
        for (int y = rect.Top; y < rect.Bottom; y++)
            for (int x = rect.Left; x < rect.Right; x++)
            {
                var (r, gg, b) = half.Sample(x, y);
                float v = Transfer.Luminance709(r, gg, b) * 80f;
                if (float.IsNaN(v)) continue;
                cp = MathF.Max(cp, v); cs += v;
            }
        new HalfFrame(half).TryStats(rect, out float sp, out float sm);
        Console.WriteLine($"  selection {rect.Width}x{rect.Height}: GPU peak {gp:F1} mean {gm:F2} ({gms:F2} ms) | every pixel peak {cp:F1} mean {cs / ((double)rect.Width * rect.Height):F2} | CPU path's sampled readout peak {sp:F1} mean {sm:F2}");
        sw.Restart();
        HalfImage crop = g.Crop(rect);
        Console.WriteLine($"  fp16 crop {rect.Width}x{rect.Height}: {sw.Elapsed.TotalMilliseconds:F2} ms, identical to the CPU crop: {crop.Data.AsSpan().SequenceEqual(half.Crop(rect).Data)}");
        float white = o.SdrWhiteNits / 80f;
        sw.Restart();
        ZebraMask z = g.Zebra(white, 1f);
        Console.WriteLine($"  zebra mask {sw.Elapsed.TotalMilliseconds:F2} ms, identical to the CPU mask: {z.Bits.AsSpan().SequenceEqual(ZebraMask.Of(half, white, 1f).Bits)}");
    }

    // ------------------------------------------------------------------------------------------------------- run

    private sealed class Snip
    {
        public double GrabMs, MemInGrab, MemAfterGrab, MemAfterRelease, GpuAfterGrab, CursorMs, StatsMs, CropMs, ExposureMs;
    }

    private static double Median(IEnumerable<double> v) { var a = v.OrderBy(x => x).ToArray(); return a.Length == 0 ? double.NaN : a[a.Length / 2]; }

    private static void Settle()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
    }

    /// <summary>
    /// The app's snip lifecycle, per path, in a process of its own: grab (pooled BGRA, plus the pooled half frame on
    /// the CPU path or the frame kept on the GPU), the overlay's readouts on the first HDR output, a crop, five exposure
    /// re-tonemaps, then the GPU frame freed as the app frees it after the build.
    /// </summary>
    private static int Run(string path, int runs, int gap, string tonemap)
    {
        bool gpu = path == "gpu";
        var log = new ConsoleLog(false);
        using var am = new AdapterMemory();
        Settle(); Thread.Sleep(1000);
        double baseMem = ProcessMemory.Mb(), baseGpu = am.MainDedicatedMb();
        Console.WriteLine($"path {path}, tonemap {tonemap}: baseline private WS {baseMem:F1} MB, adapter dedicated {baseGpu:F0} MB");
        var cap = new ScreenCapture(log);
        IReadOnlyList<OutputHandle> outs = cap.Outputs;
        var halfPool = new Dictionary<int, HalfImage>();
        var bgraPool = new Dictionary<int, BgraImage>();
        var snips = new List<Snip>();
        double peakMem = 0;
        for (int r = 0; r < runs; r++)
        {
            if (r > 0) Thread.Sleep(gap);
            Settle();
            var s = new Snip();
            var frames = new IHdrFrame?[outs.Count];
            var sw = Stopwatch.StartNew();
            string?[] reasons = cap.Capture(outs,
                (i, data, pitch, w, h, fmt) =>
                {
                    OutputHandle o = outs[i];
                    BgraImage bgra = Pooled(bgraPool, i, w, h);
                    if (fmt != Format.R16G16B16A16_Float) { FrameConverter.ToBgra8Into(data, pitch, w, h, bgra); return null; }
                    HalfImage half = halfPool.TryGetValue(i, out var hh) && hh.Width == w && hh.Height == h ? hh : halfPool[i] = new HalfImage(w, h);
                    FrameConverter.ToHalfInto(data, pitch, w, h, half);
                    s.MemInGrab = Math.Max(s.MemInGrab, ProcessMemory.Mb());   // staging still mapped: the path's peak
                    return () => { var f = new HalfFrame(half); f.Tonemap(Curve(o, tonemap, 1f), new IntRect(0, 0, w, h), bgra, 0, 0); frames[i] = f; };
                },
                3000,
                gpu ? (i, frame) => () =>
                {
                    frame.Tonemap(Curve(outs[i], tonemap, 1f), new IntRect(0, 0, frame.Width, frame.Height), Pooled(bgraPool, i, frame.Width, frame.Height), 0, 0);
                    s.MemInGrab = Math.Max(s.MemInGrab, ProcessMemory.Mb());
                    frames[i] = frame;
                } : null);
            s.GrabMs = sw.Elapsed.TotalMilliseconds;
            s.MemAfterGrab = ProcessMemory.Mb(); s.GpuAfterGrab = am.MainDedicatedMb();
            if (reasons.Any(x => x != null)) Console.WriteLine("  fallbacks: " + string.Join("; ", reasons.Where(x => x != null)));
            int hi = Enumerable.Range(0, outs.Count).FirstOrDefault(i => frames[i] != null, -1);
            if (hi >= 0)
            {
                OutputHandle o = outs[hi];
                IHdrFrame f = frames[hi]!;
                var rect = new IntRect(o.Width / 5, o.Height / 5, o.Width / 2, o.Height / 2);
                var rng = new Random(r);
                var c = Stopwatch.StartNew();
                for (int k = 0; k < 60; k++) f.TrySample(rng.Next(o.Width), rng.Next(o.Height), out _, out _, out _);
                s.CursorMs = c.Elapsed.TotalMilliseconds / 60; c.Restart();
                for (int k = 0; k < 10; k++) f.TryStats(new IntRect(rect.Left, rect.Top, rect.Width - k * 8, rect.Height - k * 8), out _, out _);
                s.StatsMs = c.Elapsed.TotalMilliseconds / 10; c.Restart();
                HalfImage crop = f.Crop(rect);
                AutoExposure.Compute(crop, o.SdrWhiteNits, 1f);
                s.CropMs = c.Elapsed.TotalMilliseconds; c.Restart();
                var passes = new double[5];
                for (int k = 0; k < 5; k++)
                {
                    c.Restart();
                    f.Tonemap(Curve(o, tonemap, 1f + k * 0.3f), new IntRect(0, 0, o.Width, o.Height), bgraPool[hi], 0, 0);
                    passes[k] = c.Elapsed.TotalMilliseconds;
                }
                s.ExposureMs = Median(passes);
                if (Environment.GetEnvironmentVariable("BENCH_VERBOSE") == "1") Console.WriteLine("  exposure passes: " + string.Join(", ", passes.Select(x => x.ToString("F1"))));
                GC.KeepAlive(crop);
            }
            // The end of the app's snip: the HDR frames go once the result is built; only the pooled buffers stay.
            foreach (IHdrFrame? f in frames) f?.Dispose();
            Settle();
            s.MemAfterRelease = ProcessMemory.Mb();
            peakMem = Math.Max(peakMem, Math.Max(s.MemInGrab, s.MemAfterGrab));
            snips.Add(s);
            Console.WriteLine($"  snip {r + 1}: grab {s.GrabMs:F1} ms | WS in grab {s.MemInGrab:F0}, after grab {s.MemAfterGrab:F0}, after release+GC {s.MemAfterRelease:F0} MB | adapter {s.GpuAfterGrab:F0} MB | " +
                              $"cursor {s.CursorMs:F3} stats {s.StatsMs:F2} crop+AE {s.CropMs:F1} exposure {s.ExposureMs:F1} ms");
        }
        Settle(); Thread.Sleep(2000);
        double heldMem = ProcessMemory.Mb(), heldGpu = am.MainDedicatedMb();
        halfPool.Clear(); bgraPool.Clear();
        cap.ReleaseDevice();
        Settle(); Thread.Sleep(3000);
        double relMem = ProcessMemory.Mb(), relGpu = am.MainDedicatedMb();
        cap.Dispose();
        var rest = snips.Skip(1).ToList();
        Console.WriteLine($"SUMMARY path={path} tonemap={tonemap} firstGrabMs={snips[0].GrabMs:F1} medGrabMs={Median(rest.Select(x => x.GrabMs)):F1} " +
                          $"baseWS={baseMem:F1} peakWS={peakMem:F1} heldWS={heldMem:F1} releasedWS={relMem:F1} baseGpu={baseGpu:F0} heldGpu={heldGpu:F0} releasedGpu={relGpu:F0} " +
                          $"cursorMs={Median(rest.Select(x => x.CursorMs)):F3} statsMs={Median(rest.Select(x => x.StatsMs)):F2} cropAeMs={Median(rest.Select(x => x.CropMs)):F1} exposureMs={Median(rest.Select(x => x.ExposureMs)):F1}");
        return 0;
    }

    private static BgraImage Pooled(Dictionary<int, BgraImage> pool, int i, int w, int h)
    {
        lock (pool) return pool.TryGetValue(i, out var b) && b.Width == w && b.Height == h ? b : pool[i] = BgraImage.Blank(w, h);
    }

    private static TonemapCurve Curve(OutputHandle o, string tonemap, float exposure)
        => new(tonemap, new TonemapParams { SdrWhiteNits = o.SdrWhiteNits, PeakNits = o.MaxLuminance, Exposure = exposure, Knee = 1f });
}
