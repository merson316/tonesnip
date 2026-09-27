using System.Diagnostics;
using ToneSnip.App.Capture;
using ToneSnip.Core.Annotate;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using Microsoft.UI.Dispatching;

namespace ToneSnip.App;

/// <summary>`--hold drag-bench`: how smooth a shape drag is in the real editor and the real snip screen.</summary>
internal static partial class Screenshots
{
    /// <summary>
    /// Opens the editor on a synthetic wide snip of text (900 x 220), then the snip screen over a grey frame, puts a
    /// rectangle and a magnifier (lens about 650 x 70) on each, and drags the rectangle, the lens and the whole callout
    /// for two seconds each. Positions come from a 1000 Hz feeder thread; like the system's own mouse queue, only the
    /// newest position waits to be handled, so moves coalesce when the UI thread is busy. For each drag it prints the
    /// moves handled, the frames that showed a new position, the time from a position being produced to the first
    /// frame drawn after it was handled, the longest gap between such frames, paint time and garbage collections.
    /// No input is injected.
    /// </summary>
    private static async Task HoldDragBench()
    {
        var results = new List<string>();
        results.AddRange(await EditorDragBench());
        results.AddRange(await OverlayDragBench());
        foreach (string r in results) { Console.WriteLine(r); App.Current.Log.Info(r); }
    }

    private static BgraImage TextImage(int w, int h)
    {
        var img = BgraImage.Blank(w, h);
        var rng = new Random(7);
        for (int i = 0; i < img.Data.Length; i += 4) { img.Data[i] = img.Data[i + 1] = img.Data[i + 2] = 250; img.Data[i + 3] = 255; }
        // Rows of dark "letters": small blocks with gaps, like a paragraph of text.
        for (int line = 12; line + 14 < h; line += 22)
            for (int x = 10; x < w - 10;)
            {
                int glyph = rng.Next(4, 9);
                if (rng.Next(6) > 0)
                    for (int y = line + rng.Next(0, 4); y < line + 12; y++)
                        for (int gx = x; gx < Math.Min(w, x + glyph); gx++) { int i = (y * w + gx) * 4; img.Data[i] = img.Data[i + 1] = img.Data[i + 2] = 30; }
                x += glyph + rng.Next(1, 4);
            }
        return img;
    }

    private static async Task<List<string>> EditorDragBench()
    {
        const int w = 900, h = 220;
        var region = new IntRect(0, 0, w, h);
        var result = new CaptureResult { Image = TextImage(w, h), Region = region, AnyHdr = false, Crops = new List<HalfCrop>() };
        var win = new Viewer.ViewerWindow(result);
        var lines = new List<string>();
        try
        {
            Move(win.AppWindow);
            win.Activate();
            await Settle();
            win.ShowState(annotate: true);
            await Settle();
            Viewer.EditorSurface surface = win.HarnessSurface;
            AnnotationDoc doc = surface.Session.Doc;
            surface.Session.Tool = Tool.Select;
            var box = new BoxShape(doc.NewId(), new IntRect(40, 20, 120, 60), 4, 0xFFFF4A4A, false, false);
            var mag = new MagnifierShape(doc.NewId(), new IntRect(20, 110, 325, 35), 560, 175, 4, 0xFF0078D4);
            doc.Add(box); doc.Add(mag);
            await Settle();
            DispatcherQueue dq = win.DispatcherQueue;
            Action<int, int, int> pointer = surface.HarnessPointer;
            void Hook(Action? a) => Viewer.EditorSurface.HarnessDrawn = a;
            (int, int) Paints() => (Viewer.EditorSurface.HarnessPaints, (int)Viewer.EditorSurface.HarnessPaintMs);
            lines.Add(await DragRun("editor rectangle", dq, doc, box.Id, pointer, (41, 50), (60, 30), Hook, Paints));
            lines.Add(await DragRun("editor lens", dq, doc, mag.Id, pointer, (560, 175), (120, 20), Hook, Paints));
            lines.Add(await DragRun("editor callout", dq, doc, mag.Id, pointer, (180, 127), (60, 20), Hook, Paints));
        }
        finally { Forget(win); win.CloseForHarness(); await Settle(); }
        return lines;
    }

    private static async Task<List<string>> OverlayDragBench()
    {
        App app = App.Current;
        var frame = TextImage(_primary.Width, _primary.Height);
        var info = new OutputInfo(0, @"\\.\DISPLAY1", _primary.Left, _primary.Top, _primary.Width, _primary.Height, false, 80f, 80f, "Primary");
        var outputs = new List<CapturedOutput> { new(info, null, frame) };
        var session = new Overlay.OverlaySession(outputs, null!, _primary, SnipMode.Rectangle, app.Settings with { AfterSelect = "save" }, app.Log);
        Task<Overlay.OverlayOutcome> done = session.Show();
        var lines = new List<string>();
        try
        {
            await Settle();
            session.Annotating = true;
            await Settle();
            EditSession edit = session.EditForHarness!;
            edit.Tool = Tool.Select;
            int ox = _primary.Left + 200, oy = _primary.Top + 200;
            var box = new BoxShape(edit.Doc.NewId(), new IntRect(ox + 40, oy + 20, 120, 60), 4, 0xFFFF4A4A, false, false);
            var mag = new MagnifierShape(edit.Doc.NewId(), new IntRect(ox + 20, oy + 110, 325, 35), ox + 560, oy + 175, 4, 0xFF0078D4);
            edit.Doc.Add(box); edit.Doc.Add(mag);
            await Settle();
            DispatcherQueue dq = DispatcherQueue.GetForCurrentThread();
            void Pointer(int phase, int x, int y)
            {
                if (phase == 0) session.OnMouseDown(x, y);
                else if (phase == 1) session.OnMouseMove(x, y);
                else session.OnMouseUp(x, y);
            }
            int paints = 0;
            void Hook(Action? a) => Overlay.OverlaySession.HarnessChromePainted = a == null ? null : () => { paints++; a(); };
            (int, int) Paints() => (paints, 0);
            lines.Add(await DragRun("overlay rectangle", dq, edit.Doc, box.Id, Pointer, (ox + 41, oy + 50), (60, 30), Hook, Paints));
            lines.Add(await DragRun("overlay lens", dq, edit.Doc, mag.Id, Pointer, (ox + 560, oy + 175), (120, 20), Hook, Paints));
            lines.Add(await DragRun("overlay callout", dq, edit.Doc, mag.Id, Pointer, (ox + 180, oy + 127), (60, 20), Hook, Paints));
        }
        finally
        {
            session.Finish(Overlay.OverlayOutcome.Cancelled);
            await done;
        }
        return lines;
    }

    /// <summary>One two-second drag of shape <paramref name="id"/> from <paramref name="press"/>, the pointer going
    /// round an ellipse of radii <paramref name="radius"/>.</summary>
    private static async Task<string> DragRun(string name, DispatcherQueue dq, AnnotationDoc doc, int id, Action<int, int, int> pointer,
        (int X, int Y) press, (int X, int Y) radius, Action<Action?> hookFrame, Func<(int Count, int Ms)> paints)
    {
        doc.Select(id);
        pointer(0, press.X, press.Y);
        var clock = Stopwatch.StartNew();
        long latestGen = 0, appliedGen = 0, shownGen = 0;
        (int X, int Y) latest = press;
        int queued = 0, produced = 0, handled = 0, frames = 0, contentFrames = 0;
        var latency = new List<double>();
        double lastContent = 0, maxGap = 0;
        object gate = new();
        hookFrame(() =>
        {
            frames++;
            if (appliedGen == shownGen) return;
            shownGen = appliedGen;
            contentFrames++;
            double now = clock.Elapsed.TotalMilliseconds;
            latency.Add(now - appliedGen / 1000.0);
            if (lastContent > 0) maxGap = Math.Max(maxGap, now - lastContent);
            lastContent = now;
        });
        (int paintsBefore, int msBefore) = paints();
        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        const double runMs = 2000;
        Task feeder = Task.Run(() =>
        {
            for (double t = 1; t < runMs; t += 1)
            {
                while (clock.Elapsed.TotalMilliseconds < t) Thread.SpinWait(20);
                double a = t / 1000 * Math.PI * 2;
                (int X, int Y) p = (press.X + (int)Math.Round(radius.X * Math.Sin(a)), press.Y + (int)Math.Round(radius.Y * (1 - Math.Cos(a))) - radius.Y / 2);
                lock (gate) { latest = p; latestGen = (long)(clock.Elapsed.TotalMilliseconds * 1000); }
                produced++;
                // As the system's queue does, a position waiting to be handled is replaced rather than queued behind.
                if (Interlocked.Exchange(ref queued, 1) == 0)
                    dq.TryEnqueue(DispatcherQueuePriority.Normal, () =>
                    {
                        Volatile.Write(ref queued, 0);
                        (int X, int Y) q; long g;
                        lock (gate) { q = latest; g = latestGen; }
                        pointer(1, q.X, q.Y);
                        appliedGen = g;
                        handled++;
                    });
            }
        });
        while (!feeder.IsCompleted) await Task.Delay(50);
        await Task.Delay(200);
        hookFrame(null);
        pointer(2, latest.X, latest.Y);
        await Settle();
        (int paintsAfter, int msAfter) = paints();
        latency.Sort();
        double P(double q) => latency.Count == 0 ? 0 : latency[Math.Min(latency.Count - 1, (int)(latency.Count * q))];
        return $"drag-bench {name}: {produced} positions, {handled} handled, {frames} frames, {contentFrames} with a new position; " +
               $"position-to-frame median {P(0.5):F1} ms, p90 {P(0.9):F1} ms, max {P(1):F1} ms; longest gap {maxGap:F1} ms; " +
               $"paints {paintsAfter - paintsBefore} ({msAfter - msBefore} ms); GC {GC.CollectionCount(0) - gc0}/{GC.CollectionCount(1) - gc1}/{GC.CollectionCount(2) - gc2}";
    }
}
