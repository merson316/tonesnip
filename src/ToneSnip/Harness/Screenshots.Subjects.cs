using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using ToneSnip.App.Capture;
using ToneSnip.App.Interop;
using ToneSnip.App.Theme;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using ToneSnip.Windows;
using ToneSnip.Windows.Imaging;
using ToneSnip.Windows.Interop;
using ToneSnip.Windows.Tray;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.UI;

namespace ToneSnip.App;

/// <summary>The synthetic history, settings and snips every capture and hold is taken over.</summary>
internal static partial class Screenshots
{
    // ----- synthetic subjects --------------------------------------------------------------------------------------

    /// <summary>
    /// Replaces the history with <see cref="HarnessData"/>'s rows and freezes the clock. Called before any window
    /// exists.
    /// <para>The rows are held in memory only (<c>SnipHistory.SeedForHarness</c>). Each row's PNG and thumbnail are
    /// rewritten every run under a temp folder, never under <see cref="AppPaths.Dir"/>.</para>
    /// </summary>
    private static void SeedHistory()
    {
        App app = App.Current;
        string dir = Path.Combine(Path.GetTempPath(), "tonesnip-harness-history");
        var entries = new List<Core.Output.HistoryEntry>();
        try
        {
            Directory.CreateDirectory(dir);
            for (int i = 0; i < HarnessData.Rows.Length; i++)
            {
                HarnessData.Row row = HarnessData.Rows[i];
                BgraImage img = SyntheticImage(row.Width, row.Height);
                string name = HarnessData.FileName(row);
                string png = Path.Combine(dir, name);
                string thumb = Path.Combine(dir, $"thumb-{i + 1}.png");
                // The "file moved or deleted" row has no PNG; every row, missing or not, keeps its thumbnail.
                if (row.Missing) { try { File.Delete(png); } catch { /* already absent */ } }
                else File.WriteAllBytes(png, Bitmaps.EncodePng(img));
                File.WriteAllBytes(thumb, Bitmaps.EncodePng(Bitmaps.Thumbnail(img, Output.SnipHistory.ThumbMaxEdge)));
                // The HDR copy is named but not written: its badge and format tag come from the path alone.
                string? hdrPath = row.HdrFile == null ? null : Output.HdrOutput.PathFor(png, row.HdrFile);
                entries.Add(new Core.Output.HistoryEntry($"harness{i + 1}", row.Copied ? null : png,
                                                         HarnessData.TakenUtc(row), row.Width, row.Height, row.Hdr, thumb,
                                                         row.Copied ? null : hdrPath));
            }
        }
        catch (Exception ex) { _failures++; app.Log.Error("screenshots: history seed: " + ex); return; }
        HarnessData.Freeze();
        _seededRows = entries;
        app.History.SeedForHarness(entries);
        app.Log.Info($"screenshots: seeded {entries.Count} synthetic history rows from {dir}, ages measured from {HarnessData.ReferenceUtc:yyyy-MM-dd HH:mm:ss}Z");
    }

    /// <summary>
    /// Applies <see cref="HarnessData.Settings"/> in memory, ignoring any settings.json the debug build may have.
    /// The theme goes through <c>ThemeManager</c> directly, because <c>ApplySettings</c> skips the live theme apply in
    /// screenshot mode.</summary>
    private static void SeedSettings()
    {
        App app = App.Current;
        app.ApplySettings(HarnessData.Settings);
        ThemeManager.Apply(app.Settings.Theme);
        app.Log.Info($"screenshots: seeded settings (save folder {app.Settings.ResolvedSaveFolder(AppPaths.Pictures)}, theme {app.Settings.Theme})");
    }

    /// <summary>A stand-in snip: a diagonal gradient with a few flat rectangles, so scaling and cropping are
    /// visible.</summary>
    private static BgraImage SyntheticImage(int width, int height)
    {
        BgraImage img = BgraImage.Blank(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                img.Data[i] = (byte)(40 + 180 * y / height);
                img.Data[i + 1] = (byte)(60 + 150 * x / width);
                img.Data[i + 2] = (byte)(90 + 120 * (x + y) / (width + height));
                img.Data[i + 3] = 255;
            }
        foreach ((IntRect r, byte b, byte g, byte rd) in new[]
        {
            (new IntRect(width / 12, height / 8, width / 4, height / 5), (byte)0x20, (byte)0x20, (byte)0xE0),
            (new IntRect(width / 2, height / 3, width / 5, height / 4), (byte)0xE0, (byte)0xC0, (byte)0x20),
            (new IntRect(width / 6, height * 3 / 5, width / 3, height / 6), (byte)0xF0, (byte)0xF0, (byte)0xF0),
        })
            for (int y = r.Top; y < r.Bottom; y++)
                for (int x = r.Left; x < r.Right; x++)
                {
                    int i = (y * width + x) * 4;
                    img.Data[i] = b; img.Data[i + 1] = g; img.Data[i + 2] = rd; img.Data[i + 3] = 255;
                }
        return img;
    }

    /// <summary>The scRGB half-float twin of <see cref="SyntheticImage"/> (1.0 = 80 nits), with a corner well above SDR
    /// white so the zebra stripes have something to mark.</summary>
    private static HalfImage SyntheticHalf(int width, int height)
    {
        var canvas = new HalfImage(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                float v = (x + y) / (float)(width + height);
                float boost = x > width * 3 / 4 && y < height / 4 ? 6f : 1f;
                canvas.Data[i] = BitConverter.HalfToUInt16Bits((Half)(v * boost));
                canvas.Data[i + 1] = BitConverter.HalfToUInt16Bits((Half)(v * 0.6f * boost));
                canvas.Data[i + 2] = BitConverter.HalfToUInt16Bits((Half)(v * 0.3f * boost));
                canvas.Data[i + 3] = 0x3C00;   // 1.0
            }
        return canvas;
    }

    /// <summary>A stand-in snip for the editor and toast here and in <see cref="LeakTest"/>.</summary>
    internal static CaptureResult SyntheticResult(bool hdr)
    {
        var region = new IntRect(0, 0, 1280, 720);
        BgraImage image = SyntheticImage(region.Width, region.Height);
        var crops = new List<HalfCrop>();
        if (hdr)
        {
            var info = new OutputInfo(0, @"\\.\DISPLAY1", 0, 0, region.Width, region.Height, true, 203f, 1000f, "Synthetic HDR");
            HalfImage half = SyntheticHalf(region.Width, region.Height);
            crops.Add(new HalfCrop(region, half, info, CaptureResult.BaseExposure(new Core.Hdr.HalfFrame(half), region, half, info, App.Current.Settings)));
        }
        return new CaptureResult { Image = image, Region = region, AnyHdr = hdr, Crops = crops };
    }
}
