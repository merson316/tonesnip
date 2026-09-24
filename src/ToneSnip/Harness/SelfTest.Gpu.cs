using System.Diagnostics;
using ToneSnip.Core.Color;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Hdr;
using ToneSnip.Core.Imaging;
using ToneSnip.Core.Tonemap;
using ToneSnip.Windows.Capture;

namespace ToneSnip.App;

/// <summary>
/// The self-test's HDR frame checks. On the GPU path the frame's pixels are read back whole once, and everything the
/// snip asks the GPU for (the tonemap, one pixel, a rectangle's peak and mean, a crop, the zebra mask, the preview's
/// downsample) is compared with the CPU answer over those same pixels. The shaders mirror the CPU code
/// (Shaders/Tonemap.hlsl), and precision is the driver's, so this is where a vendor that rounds differently shows up.
/// </summary>
public static partial class SelfTest
{
    /// <summary>The largest difference, in 8-bit code values, allowed between the GPU and CPU tonemaps of one pixel.</summary>
    private const int TonemapToleranceLsb = 1;

    private static bool HdrFrameChecks(Capture.FrameGrabber grabber, List<Capture.CapturedOutput> grabbed)
    {
        bool ok = true;
        var sw = new Stopwatch();
        foreach (Capture.CapturedOutput o in grabbed)
        {
            if (o.Hdr is not { } f) continue;
            var full = new IntRect(0, 0, f.Width, f.Height);
            sw.Restart();
            HalfImage pixels = f.Crop(full);
            double tWhole = sw.Elapsed.TotalMilliseconds;
            (int worst, long differ) = Compare(grabber.Tonemap(pixels, o.Info), o.Sdr);
            string kind = f is GpuHdrFrame ? "GPU" : "CPU";
            Console.WriteLine($"{o.Info.DeviceName}: grabbed SDR copy ({kind} tonemap) vs the CPU tonemap of its pixels: worst {worst} code values, {Share(differ, pixels)} of pixels differ");
            if (worst > TonemapToleranceLsb) ok = false;
            if (!ThinSnip(grabber, grabbed, o, pixels)) ok = false;
            if (f is not GpuHdrFrame) continue;

            // The readouts, against the same pixels in memory.
            var rng = new Random(1);
            int sampleMisses = 0, sampleWrong = 0;
            sw.Restart();
            for (int k = 0; k < 200; k++)
            {
                int x = rng.Next(f.Width), y = rng.Next(f.Height);
                if (!f.TrySample(x, y, out float r, out float g, out float b)) { sampleMisses++; continue; }
                if ((r, g, b) != pixels.Sample(x, y)) sampleWrong++;
            }
            double tSample = sw.Elapsed.TotalMilliseconds / 200;
            var rect = new IntRect(f.Width / 5, f.Height / 5, f.Width / 2, f.Height / 2);
            sw.Restart();
            bool statsRead = f.TryStats(rect, out float gpuPeak, out float gpuMean);
            double tStats = sw.Elapsed.TotalMilliseconds;
            (float cpuPeak, double cpuMean) = ExactStats(pixels, rect);
            bool statsOk = statsRead && Close(gpuPeak, cpuPeak) && Close(gpuMean, cpuMean);
            sw.Restart();
            HalfImage crop = f.Crop(rect);
            double tCrop = sw.Elapsed.TotalMilliseconds;
            bool cropOk = crop.Data.AsSpan().SequenceEqual(pixels.Crop(rect).Data);
            // The snip's auto exposure reads back only its samples: every one, and every 7th (a step that does not
            // divide the width), must be the CPU's to the bit, so the exposure is the same on both paths.
            var cpuFrame = new HalfFrame(pixels);
            sw.Restart();
            bool samplesOk = f.Luminances(rect, 1).AsSpan().SequenceEqual(cpuFrame.Luminances(rect, 1))
                             && f.Luminances(rect, 7).AsSpan().SequenceEqual(cpuFrame.Luminances(rect, 7))
                             && AutoExposure.Compute(f, rect, o.Info.SdrWhiteNits, 1f) == AutoExposure.Compute(pixels, rect, o.Info.SdrWhiteNits, 1f);
            double tSamples = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"{o.Info.DeviceName}: GPU auto exposure samples over {rect.Width}x{rect.Height}: {tSamples:F1} ms, identical={samplesOk}");
            if (!samplesOk) ok = false;
            float white = o.Info.SdrWhiteNits / 80f;
            sw.Restart();
            ZebraMask zebra = f.Zebra(white, 1.5f);
            double tZebra = sw.Elapsed.TotalMilliseconds;
            long zebraWrong = CountDifferent(zebra.Bits, ZebraMask.Of(pixels, white, 1.5f).Bits);
            sw.Restart();
            HalfImage small = f.Downsample(3);
            double tDown = sw.Elapsed.TotalMilliseconds;
            bool downOk = small.Data.AsSpan().SequenceEqual(pixels.Downsample(3).Data);
            // The exposure slider's re-tonemap at another exposure.
            BgraImage gpuExposed = BgraImage.Blank(f.Width, f.Height);
            sw.Restart();
            f.Tonemap(grabber.CurveFor(o.Info, 2f), full, gpuExposed, 0, 0);
            double tExposure = sw.Elapsed.TotalMilliseconds;
            (int exposedWorst, long exposedDiffer) = Compare(grabber.Tonemap(pixels, o.Info, 2f), gpuExposed);
            Console.WriteLine($"{o.Info.DeviceName}: GPU readouts: whole frame {tWhole:F1} ms; 1-px sample {tSample:F3} ms, {sampleWrong} wrong, {sampleMisses} busy of 200; " +
                              $"stats over {rect.Width}x{rect.Height} {tStats:F2} ms peak {gpuPeak:F1}/{cpuPeak:F1} mean {gpuMean:F2}/{cpuMean:F2} ok={statsOk}; " +
                              $"crop {tCrop:F1} ms identical={cropOk}; zebra {tZebra:F1} ms {zebraWrong} words differ; downsample {tDown:F1} ms identical={downOk}; " +
                              $"exposure x2 re-tonemap {tExposure:F1} ms worst {exposedWorst} ({Share(exposedDiffer, pixels)} differ)");
            if (sampleWrong > 0 || sampleMisses > 0 || !statsOk || !cropOk || zebraWrong > 0 || !downOk || exposedWorst > TonemapToleranceLsb) ok = false;
        }
        return ok;
    }

    /// <summary>
    /// A 7-pixel-wide snip of an HDR monitor with auto exposure on, through the real build: the exposure measured from
    /// the frame without a crop, then the selection tonemapped at it. On the GPU path this lost the snip while a
    /// readback band that narrow was taller than D3D11 allows. Checked against the CPU over the same pixels.
    /// </summary>
    private static bool ThinSnip(Capture.FrameGrabber grabber, List<Capture.CapturedOutput> grabbed, Capture.CapturedOutput o, HalfImage pixels)
    {
        var settings = new Core.Config.SnipSettings { AutoExposure = true };
        var local = new IntRect(Math.Min(100, o.Info.Width - 7), 0, 7, o.Info.Height);
        try
        {
            Capture.CaptureResult thin = Capture.CaptureResult.Build(grabbed, local.Offset(o.Info.Left, o.Info.Top), null, grabber, settings, keepCrops: false);
            float exposure = Capture.CaptureResult.BaseExposure(new HalfFrame(pixels), local, null, o.Info, settings);
            BgraImage cpu = BgraImage.Blank(local.Width, local.Height);
            HalfFrame.Tonemap(pixels, grabber.CurveFor(o.Info, exposure), local, cpu, 0, 0);
            (int worst, long differ) = Compare(cpu, thin.Image);
            Console.WriteLine($"{o.Info.DeviceName}: thin snip {local.Width}x{local.Height} with auto exposure {exposure:F3}: hdr={thin.AnyHdr}, worst {worst} code values vs the CPU ({differ} pixels differ)");
            return thin.AnyHdr && worst <= TonemapToleranceLsb;
        }
        catch (Exception e)
        {
            Console.WriteLine($"{o.Info.DeviceName}: thin snip FAILED: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// On the GPU path: every tonemap curve over a stress image (every half-float bit pattern in each channel, so NaN,
    /// infinities, negatives and subnormals, and a 30-stop hue ramp) against the CPU; then the cost of the cursor's
    /// one-pixel readout while grabs hold the device, which is what the overlay's UI thread would pay.
    /// </summary>
    private static bool GpuStressAndContention(Capture.FrameGrabber grabber, List<Capture.CapturedOutput> grabbed)
    {
        if (!grabber.UseGpu()) return true;
        bool ok = true;
        HalfImage stress = StressImage();
        using (GpuHdrFrame? gpu = grabber.CaptureForHarness.Upload(stress.Width, stress.Height, (y, row) => stress.Data.AsSpan(y * stress.Width * 4, stress.Width * 4).CopyTo(row)))
        {
            if (gpu == null) { Console.WriteLine("gpu: FAILED the GPU path is on but unavailable on this device"); return false; }
            var full = new IntRect(0, 0, stress.Width, stress.Height);
            foreach ((string curve, float exposure, float knee) in StressCases)
            {
                var p = new TonemapParams { SdrWhiteNits = 212, PeakNits = 1000, Exposure = exposure, Knee = knee };
                BgraImage cpu = BgraImage.Blank(stress.Width, stress.Height), gpuOut = BgraImage.Blank(stress.Width, stress.Height);
                HalfFrame.Tonemap(stress, new TonemapCurve(curve, p), full, cpu, 0, 0);
                gpu.Tonemap(new TonemapCurve(curve, p), full, gpuOut, 0, 0);
                (int worst, long differ) = Compare(cpu, gpuOut);
                Console.WriteLine($"gpu stress: {curve,-7} exposure {exposure} knee {knee}: worst {worst} code values, {Share(differ, stress)} of pixels differ");
                if (worst > TonemapToleranceLsb) ok = false;
            }

            // A thin selection or lasso: a readback band a few pixels wide used to be taller than D3D11 allows, and the
            // snip was lost. Each width once, then again after a wider read has grown the bands.
            var narrow = new TonemapCurve("desktop", new TonemapParams { SdrWhiteNits = 212, PeakNits = 1000, Exposure = 1.5f, Knee = 1f });
            foreach (int width in new[] { 1, 7, 63, 200, 1, 63 })
            {
                var strip = new IntRect(13, 0, width, stress.Height);
                BgraImage cpu = BgraImage.Blank(width, stress.Height), gpuOut = BgraImage.Blank(width, stress.Height);
                HalfFrame.Tonemap(stress, narrow, strip, cpu, 0, 0);
                try
                {
                    gpu.Tonemap(narrow, strip, gpuOut, 0, 0);
                    (int worst, long differ) = Compare(cpu, gpuOut);
                    Console.WriteLine($"gpu narrow: {width,3} x {stress.Height} tonemap: worst {worst} code values, {Share(differ, stress)} of pixels differ");
                    if (worst > TonemapToleranceLsb) ok = false;
                }
                catch (Exception e)
                {
                    Console.WriteLine($"gpu narrow: FAILED {width} x {stress.Height}: {e.GetType().Name}: {e.Message}");
                    ok = false;
                }
            }
        }

        // The overlay's cursor readout does not wait for the device: measured while back-to-back grabs hold it.
        if (grabbed.FirstOrDefault(o => o.Hdr is GpuHdrFrame)?.Hdr is not GpuHdrFrame frame) return ok;
        var times = new List<double>();
        int misses = 0;
        var sw = new Stopwatch();
        Task grabs = Task.Run(() => { for (int i = 0; i < Grabs; i++) Capture.FrameGrabber.Release(grabber.GrabAll()); });
        while (!grabs.IsCompleted)
        {
            sw.Restart();
            if (!frame.TrySample(frame.Width / 2, frame.Height / 2, out _, out _, out _)) misses++;
            times.Add(sw.Elapsed.TotalMilliseconds);
            Thread.SpinWait(20_000);   // a mouse-move cadence without the 15 ms timer tick of a Sleep
        }
        grabs.Wait();
        times.Sort();
        Console.WriteLine($"gpu: cursor readout during {Grabs} grabs: {times.Count} calls, median {times[times.Count / 2]:F3} ms, worst {times[^1]:F3} ms, {misses} found the device busy");
        return ok;
    }

    /// <summary>Back-to-back grabs the cursor readout is timed against.</summary>
    private const int Grabs = 20;

    private static readonly (string Curve, float Exposure, float Knee)[] StressCases =
    [
        ("desktop", 1f, 1f), ("desktop", 1f, 0.5f), ("desktop", 3f, 1f), ("desktop", 3f, 0.6f),
        ("hable", 1f, 1f), ("hable", 3f, 1f), ("aces", 1f, 1f), ("aces", 3f, 1f),
    ];

    /// <summary>Every half bit pattern in every channel (NaN, infinities, negatives, subnormals), then a hue ramp from
    /// 2^-14 to 2^16 scRGB.</summary>
    private static HalfImage StressImage()
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

    /// <summary>Worst per-channel difference in code values, and how many pixels differ at all.</summary>
    private static (int Worst, long Differ) Compare(BgraImage a, BgraImage b)
    {
        int worst = 0; long differ = 0;
        for (int i = 0; i < a.Data.Length; i += 4)
        {
            int m = Math.Max(Math.Abs(a.Data[i] - b.Data[i]), Math.Max(Math.Abs(a.Data[i + 1] - b.Data[i + 1]), Math.Abs(a.Data[i + 2] - b.Data[i + 2])));
            if (m > 0) differ++;
            worst = Math.Max(worst, m);
        }
        return (worst, differ);
    }

    private static string Share(long differ, HalfImage img) => $"{100.0 * differ / ((long)img.Width * img.Height):F4} %";

    private static long CountDifferent(uint[] a, uint[] b)
    {
        long n = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) n++;
        return n;
    }

    /// <summary>Peak and mean luminance in nits over every pixel of <paramref name="rect"/>, summed in double.</summary>
    private static (float Peak, double Mean) ExactStats(HalfImage img, IntRect rect)
    {
        float peak = 0; double sum = 0;
        for (int y = rect.Top; y < rect.Bottom; y++)
            for (int x = rect.Left; x < rect.Right; x++)
            {
                (float r, float g, float b) = img.Sample(x, y);
                float v = Transfer.Luminance709(r, g, b) * 80f;
                if (float.IsNaN(v)) continue;
                peak = MathF.Max(peak, v); sum += v;
            }
        return (peak, sum / ((double)rect.Width * rect.Height));
    }

    /// <summary>Within 0.01 %, or 0.01 nits near black: the GPU sums a tile in single precision.</summary>
    private static bool Close(double gpu, double cpu) => Math.Abs(gpu - cpu) <= Math.Max(0.01, Math.Abs(cpu) * 1e-4);
}
