using System.Diagnostics;
using System.Runtime.InteropServices;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using ToneSnip.Core.Tonemap;
using ToneSnip.Windows.Imaging;

namespace ToneSnip.App;

/// <summary>
/// `tonesnip-debug.exe --selftest`: grabs one frame per output through Windows.Graphics.Capture, runs the snip path
/// in memory and prints timings. `--no-capture` runs only the checks that work on synthetic images (the annotation
/// rasterizer, WIC round-trips, HDR encoders). The only file written is an 8 x 8 PNG under %TEMP% that the Recycle
/// Bin check creates and recycles.
/// </summary>
public static class SelfTest
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);

    /// <summary>The pinned rasterizer fingerprints. Re-pin only together with a deliberate rasterizer change.</summary>
    private const string ExpectedAnnotateHash = "D2CC8BB51C078E86", ExpectedChromeHash = "3B0E390B82FD6C83";

    /// <summary>Share of sampled pixels that are black: a GPU frame taken before anything was composed comes back all black.</summary>
    private static double BlackShare(BgraImage img)
    {
        int n = img.Width * img.Height, step = Math.Max(1, n / 20000), black = 0, seen = 0;
        for (int i = 0; i < n; i += step, seen++)
            if (img.Data[i * 4] <= 1 && img.Data[i * 4 + 1] <= 1 && img.Data[i * 4 + 2] <= 1) black++;
        return seen == 0 ? 1 : black / (double)seen;
    }

    /// <param name="strictHashes">Fail the run when the rasterizer fingerprints differ. Off by default: text goes
    /// through GDI+ DrawString, so the bytes depend on the installed fonts (a CI runner without Segoe UI Variable
    /// will not match). The fingerprints are printed either way.</param>
    public static int Run(bool noCapture = false, bool strictHashes = false)
    {
        AttachConsole(-1);   // WinExe: reattach to the launching console so Console.WriteLine is visible
        var log = new FileLog(AppPaths.LogPath);
        var sw = Stopwatch.StartNew();
        int ok = 1;

        Capture.FrameGrabber? grabber = null;
        List<Capture.CapturedOutput> grabbed = new();
        Capture.CaptureResult? result = null;
        IntRect desktop = IntRect.Empty;

        try
        {
            if (noCapture)
            {
                Console.WriteLine("--no-capture: skipping the grabs and the live snip path");
            }
            else
            {
                // Windows.Graphics.Capture is not exclusive, so this can run beside ToneSnip. Each output must come
                // back through the GPU path (not the GDI fallback), at its own size, not black, and as a half image
                // exactly when the monitor is in HDR.
                grabber = new Capture.FrameGrabber(() => new Core.Config.SnipSettings(), log);
                Console.WriteLine($"capture: Windows.Graphics.Capture supported={ToneSnip.Windows.Capture.ScreenCapture.Supported}");
                ok = 0;
                for (int pass = 1; pass <= 2; pass++)   // the first pass makes the device, the second is a warm snip
                {
                    sw.Restart();
                    grabbed = grabber.GrabAll();
                    long tPass = sw.ElapsedMilliseconds;
                    Console.WriteLine($"capture pass {pass}: {grabbed.Count} output(s) in {tPass} ms");
                }
                List<OutputInfo> described = grabber.Outputs();
                ok = 1;
                foreach (Capture.CapturedOutput o in grabbed)
                {
                    OutputInfo want = described.First(d => d.Index == o.Info.Index);
                    double black = BlackShare(o.Sdr);
                    bool viaGpu = !grabber.LastFallbacks.Contains(want.DeviceName);
                    bool right = o.Sdr.Width == want.Width && o.Sdr.Height == want.Height && black < 0.98 && viaGpu && (o.Half != null) == want.Hdr;
                    Console.WriteLine($"{want.DeviceName}: {want.Bounds} hdr={want.Hdr} sdrWhite={want.SdrWhiteNits:F0} peak={want.PeakNits:F0} frame {o.Sdr.Width}x{o.Sdr.Height} {(o.Half != null ? "half" : "bgra8")} black={black:P0} gpu={viaGpu}{(right ? "" : " FAILED")}");
                    if (!right) ok = 0;
                }

                // End-to-end through the real snip path (half frames, tonemap, composite, PNG encode), all in memory, and a check
                // that the half path matches the float path on live frames.
                sw.Restart();
                grabbed = grabber.GrabAll();
                long tGrabAll = sw.ElapsedMilliseconds;
                desktop = grabbed.Aggregate(IntRect.Empty, (r, o) => r.Union(o.Info.Bounds));
                sw.Restart();
                result = Capture.CaptureResult.Build(grabbed, desktop, null, grabber, new Core.Config.SnipSettings());
                long tBuild = sw.ElapsedMilliseconds;
                sw.Restart();
                byte[] png = Bitmaps.EncodePng(result.Image);
                Console.WriteLine($"snip path: grab all {tGrabAll} ms, composite {result.Image.Width}x{result.Image.Height} in {tBuild} ms, png {png.Length / 1024} KB in {sw.ElapsedMilliseconds} ms, hdr={result.AnyHdr}, crops={result.Crops.Count}");

                foreach (Capture.CapturedOutput o in grabbed.Where(o => o.Half != null))
                {
                    var p = new TonemapParams { SdrWhiteNits = o.Info.SdrWhiteNits, PeakNits = o.Info.PeakNits };
                    BgraImage viaFloat = PixelConvert.ToBgra8(o.Half!.ToFloat(), TonemapperFactory.Create("desktop", p));
                    int worst = 0;
                    for (int i = 0; i < viaFloat.Data.Length; i++) worst = Math.Max(worst, Math.Abs(viaFloat.Data[i] - o.Sdr.Data[i]));
                    Console.WriteLine($"{o.Info.DeviceName}: half path vs float path worst difference {worst} code values");
                    if (worst > 1) ok = 0;
                }
            }

            // WIC: a PNG must round-trip byte-identical and a JPEG at the same size. Without a frame the subject is
            // a synthetic gradient.
            BgraImage wicSubject = result?.Image ?? Synthetic(320, 200);
            byte[] wicPng = Bitmaps.EncodePng(wicSubject);
            BgraImage wicPngBack = Bitmaps.Decode(wicPng);
            bool wicPngOk = wicPngBack.Width == wicSubject.Width && wicPngBack.Height == wicSubject.Height && wicPngBack.Data.AsSpan().SequenceEqual(wicSubject.Data);
            byte[] wicJpg = Bitmaps.EncodeJpeg(wicSubject, 90);
            BgraImage wicJpgBack = Bitmaps.Decode(wicJpg);
            bool wicJpgOk = wicJpgBack.Width == wicSubject.Width && wicJpgBack.Height == wicSubject.Height;
            Console.WriteLine($"wic png roundtrip={wicPngOk}, wic jpeg roundtrip={wicJpgOk}");
            if (!wicPngOk || !wicJpgOk) ok = 0;

            // The Recycle Bin path behind SnipSettings.DeleteToRecycleBin: one uniquely named temp file is recycled
            // and asserted gone. This proves the SHFileOperation interop; the bin itself is never enumerated, since
            // that would read the user's deleted files.
            {
                string bait = Path.Combine(Path.GetTempPath(), "tonesnip-selftest-recycle-" + Guid.NewGuid().ToString("N") + ".png");
                bool recycled = false, gone = false;
                try
                {
                    File.WriteAllBytes(bait, Bitmaps.EncodePng(Synthetic(8, 8)));
                    recycled = ToneSnip.Windows.Shell.Recycle(bait, log);
                    gone = !File.Exists(bait);
                }
                catch (Exception e) { Console.WriteLine("recycle: FAILED " + e.Message); }
                finally { try { if (File.Exists(bait)) File.Delete(bait); } catch { } }
                Console.WriteLine($"recycle: one temp file to the Recycle Bin, shell accepted={recycled}, gone from its path={gone}");
                if (!recycled || !gone) ok = 0;
            }

            // The pooled grab's fill-in-place decode (in ToneSnip.Windows, so out of reach of the Core tests), checked
            // against the allocating decode over a frame built in unmanaged memory. The row pitch is wider than the
            // row and the target is pre-filled, so a skipped byte or misread pitch shows up as a mismatch.
            {
                const int fw = 37, fh = 23;                 // odd and prime-ish: a stride bug cannot hide behind a round number
                int halfPitch = fw * 8 + 48, bgraPitch = fw * 4 + 48;
                IntPtr halfBuf = Marshal.AllocHGlobal(halfPitch * fh), bgraBuf = Marshal.AllocHGlobal(bgraPitch * fh);
                bool intoOk = true;
                try
                {
                    unsafe
                    {
                        for (int y = 0; y < fh; y++)
                        {
                            ushort* hrow = (ushort*)((byte*)halfBuf + y * halfPitch);
                            byte* brow = (byte*)bgraBuf + y * bgraPitch;
                            for (int x = 0; x < fw; x++)
                            {
                                hrow[x * 4] = BitConverter.HalfToUInt16Bits((Half)((x + y) / 8f));
                                hrow[x * 4 + 1] = BitConverter.HalfToUInt16Bits((Half)(x / 16f));
                                hrow[x * 4 + 2] = BitConverter.HalfToUInt16Bits((Half)(y / 16f));
                                hrow[x * 4 + 3] = 0x3C00;
                                brow[x * 4] = (byte)(x * 7); brow[x * 4 + 1] = (byte)(y * 11); brow[x * 4 + 2] = (byte)(x + y); brow[x * 4 + 3] = 0;
                            }
                        }
                    }
                    var fp16 = Vortice.DXGI.Format.R16G16B16A16_Float;
                    // ToHalfInto / ToBgra8Into against the allocating decode, unrotated.
                    var halfInto = new HalfImage(fw, fh);
                    Array.Fill(halfInto.Data, (ushort)0xDEAD);
                    ToneSnip.Windows.Display.FrameConverter.ToHalfInto(halfBuf, halfPitch, fw, fh, fp16, halfInto);
                    intoOk &= halfInto.Data.AsSpan().SequenceEqual(ToneSnip.Windows.Display.FrameConverter.ToHalf(halfBuf, halfPitch, fw, fh, fp16, Vortice.DXGI.ModeRotation.Identity).Data);
                    BgraImage bgraInto = BgraImage.Blank(fw, fh);
                    Array.Fill(bgraInto.Data, (byte)0xAB);
                    ToneSnip.Windows.Display.FrameConverter.ToBgra8Into(bgraBuf, bgraPitch, fw, fh, bgraInto);
                    intoOk &= bgraInto.Data.AsSpan().SequenceEqual(ToneSnip.Windows.Display.FrameConverter.ToBgra8(bgraBuf, bgraPitch, fw, fh, Vortice.DXGI.ModeRotation.Identity).Data);
                }
                finally { Marshal.FreeHGlobal(halfBuf); Marshal.FreeHGlobal(bgraBuf); }

                // The GDI fallback's fill-in-place form. Two blits of the live screen need not agree, so this checks
                // the length guard and that the whole buffer is filled (opaque alpha in every pixel).
                bool gdiOk;
                try
                {
                    var tile = new IntRect(0, 0, 16, 16);
                    var into = new byte[16 * 16 * 4];
                    Array.Fill(into, (byte)0x7F);
                    ToneSnip.Windows.Capture.GdiCapture.CaptureBgra8Into(tile, into);
                    gdiOk = true;
                    for (int i = 3; i < into.Length; i += 4) gdiOk &= into[i] == 255;
                    try { ToneSnip.Windows.Capture.GdiCapture.CaptureBgra8Into(tile, new byte[4]); gdiOk = false; }
                    catch (ArgumentException) { }
                }
                catch (Exception e) { Console.WriteLine("frames: GDI fill FAILED " + e.Message); gdiOk = false; }

                Console.WriteLine($"frames: decode into a pooled buffer matches the allocating decode={intoOk}, gdi fill-in-place={gdiOk}");
                if (!intoOk || !gdiOk) ok = 0;
            }

            // Annotation rasterizer: the same document must render byte-identically twice, and every shape kind must leave marks inside its bounds.
            {
                BgraImage baseImg = Synthetic(200, 120);
                var doc = new Core.Annotate.AnnotationDoc();
                doc.Add(new Core.Annotate.PenShape(doc.NewId(), new[] { (10, 10), (60, 20), (80, 50) }, 4, 0xFFE53935, false));
                doc.Add(new Core.Annotate.PenShape(doc.NewId(), new[] { (10, 60), (90, 60) }, 8, 0xFFFDD835, true));
                doc.Add(new Core.Annotate.LineShape(doc.NewId(), 100, 10, 180, 40, 4, 0xFF1E88E5, false));
                doc.Add(new Core.Annotate.LineShape(doc.NewId(), 100, 50, 180, 90, 4, 0xFF43A047, true));
                doc.Add(new Core.Annotate.BoxShape(doc.NewId(), new IntRect(10, 70, 40, 30), 2, 0xFFFFFFFF, false, false));
                doc.Add(new Core.Annotate.BoxShape(doc.NewId(), new IntRect(60, 70, 40, 30), 2, 0xFF8E24AA, true, true));
                // Part of the pinned fingerprint: changing this string changes ExpectedAnnotateHash.
                doc.Add(new Core.Annotate.TextShape(doc.NewId(), 110, 95, "ToneSnip", 14, 0xFFFFFFFF));
                doc.Add(new Core.Annotate.CounterShape(doc.NewId(), 190, 105, 1, 14, 0xFFE53935));
                doc.Add(new Core.Annotate.RedactShape(doc.NewId(), new IntRect(120, 60, 30, 30), 4, false, true, 5));
                doc.Add(new Core.Annotate.RedactShape(doc.NewId(), new IntRect(155, 60, 30, 30), 4, true, false, 5));
                var viewport = new IntRect(0, 0, 200, 120);
                var t1 = BgraImage.Blank(200, 120); var t2 = BgraImage.Blank(200, 120);
                sw.Restart();
                Annotate.ShapeRenderer.Render(doc, baseImg, viewport, t1);
                long tRender = sw.ElapsedMilliseconds;
                Annotate.ShapeRenderer.Render(doc, baseImg, viewport, t2);
                bool same = t1.Data.AsSpan().SequenceEqual(t2.Data);
                int marked = 0;
                foreach (Core.Annotate.Shape s in doc.Shapes)
                {
                    IntRect b = s.Bounds.Intersect(viewport); bool any = false;
                    for (int y = b.Top; y < b.Bottom && !any; y++) for (int x = b.Left; x < b.Right; x++) { int i = (y * 200 + x) * 4; if (t1.Data[i] != baseImg.Data[i] || t1.Data[i + 1] != baseImg.Data[i + 1] || t1.Data[i + 2] != baseImg.Data[i + 2]) { any = true; break; } }
                    if (any) marked++;
                }
                var t3 = BgraImage.Blank(200, 120);
                for (int ty = 0; ty < 120; ty += 29) for (int tx = 0; tx < 200; tx += 37)
                    Annotate.ShapeRenderer.Render(doc, baseImg, viewport, t3, new IntRect(tx, ty, 37, 29));
                bool tiled = t1.Data.AsSpan().SequenceEqual(t3.Data);

                // OutputPipeline.Run renders the document into result.Output, and Compact must keep that rendered
                // image (marks included), not the clean composite. Replay Output-then-Compact and check the box's
                // outline differs from the clean pixels after decoding the compacted PNG.
                bool compactKeepsMarks = true;
                if (grabber != null)
                {
                    var markDoc = new Core.Annotate.AnnotationDoc();
                    var box = new Core.Annotate.BoxShape(markDoc.NewId(), new IntRect(10, 10, 40, 30).Offset(desktop.Left, desktop.Top), 4, 0xFFE53935, false, false);
                    markDoc.Add(box);
                    Capture.CaptureResult.Encode = Bitmaps.EncodePng;
                    Capture.CaptureResult.Decode = Bitmaps.Decode;
                    Capture.CaptureResult markedResult = Capture.CaptureResult.Build(grabbed, desktop, null, grabber, new Core.Config.SnipSettings(), markDoc);
                    byte[] cleanBytes = (byte[])markedResult.Image.Data.Clone();
                    markedResult.Output = markedResult.Rendered();   // mirrors OutputPipeline.Run before Compact
                    markedResult.Compact();
                    BgraImage compacted = markedResult.Image;   // decoded back from the PNG Compact kept
                    compactKeepsMarks = false;
                    IntRect check = box.Bounds.Offset(-desktop.Left, -desktop.Top).Intersect(new IntRect(0, 0, compacted.Width, compacted.Height));
                    for (int y = check.Top; y < check.Bottom && !compactKeepsMarks; y++)
                        for (int x = check.Left; x < check.Right; x++)
                        {
                            int i = (y * compacted.Width + x) * 4;
                            if (compacted.Data[i] != cleanBytes[i] || compacted.Data[i + 1] != cleanBytes[i + 1] || compacted.Data[i + 2] != cleanBytes[i + 2]) { compactKeepsMarks = true; break; }
                        }
                }

                Console.WriteLine($"annotate: {doc.Shapes.Count} shapes rendered in {tRender} ms, deterministic={same}, shapes leaving marks={marked}/{doc.Shapes.Count}, tiledMatchesFull={tiled}, compactKeepsMarks={compactKeepsMarks}");
                if (!same || marked != doc.Shapes.Count || !tiled || !compactKeepsMarks) ok = 0;

                // Rasterizer fingerprints against the pinned values: subtle GDI+ state changes (PenAlignment,
                // PixelOffsetMode, StringFormat) show up here and nowhere else.
                string annotateHash = Hash(t1.Data), chromeHash = ChromeHash(viewport), zebraHash = ZebraHash(baseImg, viewport);
                Console.WriteLine($"annotate hash={annotateHash} expected={ExpectedAnnotateHash} match={annotateHash == ExpectedAnnotateHash}");
                Console.WriteLine($"annotate chrome hash={chromeHash} expected={ExpectedChromeHash} match={chromeHash == ExpectedChromeHash}");
                Console.WriteLine($"annotate zebra hash={zebraHash}");
                bool hashesMatch = annotateHash == ExpectedAnnotateHash && chromeHash == ExpectedChromeHash;
                if (!hashesMatch) Console.WriteLine(strictHashes ? "annotate: FAILED fingerprints differ (--strict-hashes)" : "annotate: fingerprints differ (not fatal without --strict-hashes; expected on machines without the pinned fonts)");
                if (!hashesMatch && strictHashes) ok = 0;
            }
        }
        finally { grabber?.Dispose(); }

        // HDR sidecar encoders, in memory: JPEG XR must round-trip bit-for-bit; the PNG and the gain-map JPEG must decode.
        try
        {
            var settings = new Core.Config.SnipSettings();
            HalfImage canvas;
            BgraImage rendered;
            float white;
            bool sizeOk = true, redactOk = true;
            if (result != null && grabber != null)
            {
                var hdrDoc = new Core.Annotate.AnnotationDoc();
                hdrDoc.Add(new Core.Annotate.PenShape(hdrDoc.NewId(), new[] { (desktop.Left + 10, desktop.Top + 10), (desktop.Left + 60, desktop.Top + 20) }, 4, 0xFFE53935, false));
                // A redaction under a crop must reach the HDR canvas at the crop's size, and the redacted blocks must
                // differ from a clean (unredacted) lift.
                var redactStrength = 4;
                var redactRect = new IntRect(desktop.Left + 20, desktop.Top + 30, 40, 24);
                hdrDoc.Add(new Core.Annotate.RedactShape(hdrDoc.NewId(), redactRect, redactStrength, Blur: false, Private: true, 12345));   // private: the block field is a jittered mean, so it differs from the clean lift even over a flat wallpaper
                hdrDoc.SetCrop(new IntRect(desktop.Left + 5, desktop.Top + 5, Math.Min(300, result.Region.Width - 10), Math.Min(200, result.Region.Height - 10)));
                result.Doc = hdrDoc; result.Exposure = 1f;
                (canvas, _, white) = Output.HdrOutput.BuildCanvas(result, settings, Annotate.ShapeRenderer.DefaultAccent);
                rendered = result.Rendered();
                sizeOk = canvas.Width == rendered.Width && canvas.Height == rendered.Height;
                var cleanDoc = new Core.Annotate.AnnotationDoc();
                cleanDoc.SetCrop(hdrDoc.Crop);
                result.Doc = cleanDoc;
                (HalfImage cleanCanvas, _, _) = Output.HdrOutput.BuildCanvas(result, settings, Annotate.ShapeRenderer.DefaultAccent);
                result.Doc = hdrDoc;
                IntRect redactLocal = redactRect.Offset(-hdrDoc.Crop.Left, -hdrDoc.Crop.Top).Intersect(new IntRect(0, 0, canvas.Width, canvas.Height));
                bool redactDiffers = false, cleanBlack = true;   // a pure black patch has a zero mean, and jitter of zero is zero: nothing to redact there
                for (int y = redactLocal.Top; y < redactLocal.Bottom; y++)
                    for (int x = redactLocal.Left; x < redactLocal.Right; x++)
                    {
                        int i = (y * canvas.Width + x) * 4;
                        if (cleanCanvas.Data[i] != 0 || cleanCanvas.Data[i + 1] != 0 || cleanCanvas.Data[i + 2] != 0) cleanBlack = false;
                        if (canvas.Data[i] != cleanCanvas.Data[i] || canvas.Data[i + 1] != cleanCanvas.Data[i + 1] || canvas.Data[i + 2] != cleanCanvas.Data[i + 2]) redactDiffers = true;
                    }
                int block = Core.Annotate.Style.PixelBlock(redactStrength);
                bool blockUniform = redactLocal.Width >= block && redactLocal.Height >= block;
                if (blockUniform)
                {
                    int baseI = (redactLocal.Top * canvas.Width + redactLocal.Left) * 4;
                    ushort baseR = canvas.Data[baseI];
                    for (int y = redactLocal.Top; y < redactLocal.Top + block && blockUniform; y++)
                        for (int x = redactLocal.Left; x < redactLocal.Left + block; x++)
                            if (canvas.Data[(y * canvas.Width + x) * 4] != baseR) { blockUniform = false; break; }
                }
                redactOk = (redactDiffers || cleanBlack) && blockUniform;
            }
            else
            {
                // No frame: a synthetic scRGB canvas exercises the same three encoders, minus the crop and redaction
                // invariants, which need a real composited result.
                (canvas, rendered, white) = SyntheticHdr(240, 160);
                Console.WriteLine("hdr: synthetic canvas (--no-capture); crop and redaction invariants need a frame and were skipped");
            }

            sw.Restart();
            byte[] jxr = Output.HdrOutput.Encode(canvas, rendered, "jxr", settings, white);
            long tJxr = sw.ElapsedMilliseconds;
            HalfImage back = JxrDecoder.DecodeHalf(jxr);
            bool jxrOk = back.Width == canvas.Width && back.Height == canvas.Height && back.Data.AsSpan().SequenceEqual(canvas.Data);
            sw.Restart();
            byte[] hdrPng = Output.HdrOutput.Encode(canvas, rendered, "png", settings, white);
            long tPng = sw.ElapsedMilliseconds;
            // WIC normalises the 64-bpp HDR PNG to BGRA8 on decode, so this is a size check, not a depth check; the
            // bit-exact guarantee is the JPEG XR round-trip above.
            BgraImage pngDec = Bitmaps.Decode(hdrPng);
            bool pngOk = pngDec.Width == canvas.Width && pngDec.Height == canvas.Height;
            sw.Restart();
            byte[] jpg = Output.HdrOutput.Encode(canvas, rendered, "jpeg", settings, white);
            long tJpg = sw.ElapsedMilliseconds;
            BgraImage jpgDec = Bitmaps.Decode(jpg);
            bool jpgOk = jpgDec.Width == canvas.Width && jpgDec.Height == canvas.Height;
            Console.WriteLine($"hdr: canvas {canvas.Width}x{canvas.Height} white {white:F0} nits; jxr {jxr.Length / 1024} KB in {tJxr} ms roundtrip={jxrOk}; png {hdrPng.Length / 1024} KB in {tPng} ms decodes={pngOk}; gainmap jpg {jpg.Length / 1024} KB in {tJpg} ms decodes={jpgOk}; cropSize={sizeOk} redaction={redactOk}");
            if (!jxrOk || !pngOk || !jpgOk || !sizeOk || !redactOk) ok = 0;
        }
        catch (Exception e) { Console.WriteLine("hdr: FAILED " + e); ok = 0; }
        return ok > 0 ? 0 : 1;
    }

    /// <summary>The first 16 hex digits of the SHA-256 of a rendered buffer: short enough to read off a console, long
    /// enough that a one-pixel difference shows.</summary>
    private static string Hash(params byte[][] buffers)
    {
        using var h = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (byte[] b in buffers) h.AppendData(b);
        return Convert.ToHexString(h.GetHashAndReset())[..16];
    }

    /// <summary>
    /// Fingerprints <see cref="Annotate.ShapeRenderer.Zebra"/> over a synthetic half-float crop at a fixed white level
    /// and exposure. Printed, not asserted against a pinned value.
    /// </summary>
    private static string ZebraHash(BgraImage baseImg, IntRect viewport)
    {
        var half = new HalfImage(viewport.Width, viewport.Height);
        for (int y = 0; y < viewport.Height; y++)
            for (int x = 0; x < viewport.Width; x++)
            {
                // 0..12 in scene-linear (1.0 = 80 nits), so the ramp crosses SDR white and every stripe band is hit.
                ushort v = BitConverter.HalfToUInt16Bits((Half)((x + y) / (float)(viewport.Width + viewport.Height) * 12f));
                int i = (y * viewport.Width + x) * 4;
                half.Data[i] = v; half.Data[i + 1] = v; half.Data[i + 2] = v; half.Data[i + 3] = 0x3C00;
            }
        var target = BgraImage.Blank(viewport.Width, viewport.Height);
        Buffer.BlockCopy(baseImg.Data, 0, target.Data, 0, target.Data.Length);
        Annotate.ShapeRenderer.Zebra(target, viewport, new[] { (viewport, half) }, 203f / 80f, 1f, viewport);
        return Hash(target.Data);
    }

    /// <summary>
    /// Fingerprints <see cref="Annotate.ShapeRenderer.Chrome"/> over its three branches: a move with selection
    /// handles, a redaction preview, and a crop marquee. Independent of any frame, clock or random seed.
    /// </summary>
    private static string ChromeHash(IntRect viewport)
    {
        var a = BgraImage.Blank(viewport.Width, viewport.Height);
        var b = BgraImage.Blank(viewport.Width, viewport.Height);
        var c = BgraImage.Blank(viewport.Width, viewport.Height);

        var selDoc = new Core.Annotate.AnnotationDoc();
        var box = new Core.Annotate.BoxShape(selDoc.NewId(), new IntRect(30, 20, 60, 40), 4, 0xFF1E88E5, false, false);
        selDoc.Add(box);
        selDoc.Select(box.Id);
        var selSession = new Core.Annotate.EditSession(selDoc);
        selSession.Begin(50, 40, Core.Annotate.InputMods.None);
        selSession.Move(70, 55, Core.Annotate.InputMods.None);   // InProgress = the moved copy; Tool stays Select
        Annotate.ShapeRenderer.Chrome(selSession, viewport, a, Annotate.ShapeRenderer.DefaultAccent);
        selSession.Detach();

        var redDoc = new Core.Annotate.AnnotationDoc();
        var redSession = new Core.Annotate.EditSession(redDoc) { Tool = Core.Annotate.Tool.Blur };
        redSession.Begin(20, 20, Core.Annotate.InputMods.None);
        redSession.Move(120, 90, Core.Annotate.InputMods.None);
        Annotate.ShapeRenderer.Chrome(redSession, viewport, b, Annotate.ShapeRenderer.DefaultAccent);
        redSession.Detach();

        var cropDoc = new Core.Annotate.AnnotationDoc();
        var cropSession = new Core.Annotate.EditSession(cropDoc) { Tool = Core.Annotate.Tool.Crop };
        cropSession.Begin(25, 15, Core.Annotate.InputMods.None);
        cropSession.Move(160, 100, Core.Annotate.InputMods.None);
        cropSession.End(160, 100, Core.Annotate.InputMods.None);   // marquee survives the release
        Annotate.ShapeRenderer.Chrome(cropSession, viewport, c, Annotate.ShapeRenderer.DefaultAccent);
        cropSession.Detach();

        return Hash(a.Data, b.Data, c.Data);
    }

    /// <summary>An opaque BGRA gradient: a deterministic stand-in for a captured frame.</summary>
    private static BgraImage Synthetic(int width, int height)
    {
        BgraImage img = BgraImage.Blank(width, height);
        for (int i = 0; i < img.Data.Length; i += 4)
        {
            int px = i / 4;
            img.Data[i] = (byte)(px % 200);
            img.Data[i + 1] = (byte)(px / width * 2 % 256);
            img.Data[i + 2] = 40;
            img.Data[i + 3] = 255;
        }
        return img;
    }

    /// <summary>A half-float scRGB canvas with a bright corner, plus its SDR counterpart, for the HDR encoders.</summary>
    private static (HalfImage Canvas, BgraImage Rendered, float White) SyntheticHdr(int width, int height)
    {
        var canvas = new HalfImage(width, height);
        BgraImage rendered = Synthetic(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                float v = (x + y) / (float)(width + height);
                float boost = x > width * 3 / 4 && y < height / 4 ? 6f : 1f;   // a highlight above SDR white
                canvas.Data[i] = BitConverter.HalfToUInt16Bits((Half)(v * boost));
                canvas.Data[i + 1] = BitConverter.HalfToUInt16Bits((Half)(v * 0.6f * boost));
                canvas.Data[i + 2] = BitConverter.HalfToUInt16Bits((Half)(v * 0.3f * boost));
                canvas.Data[i + 3] = 0x3C00;   // 1.0
            }
        return (canvas, rendered, 200f);
    }
}
