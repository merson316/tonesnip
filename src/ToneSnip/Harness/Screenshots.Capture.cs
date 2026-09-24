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

/// <summary>Settling, photographing and writing one window, shared by every capture.</summary>
internal static partial class Screenshots
{
    // ----- capture -------------------------------------------------------------------------------------------------

    /// <summary>Top-left of the primary monitor's work area, with the inset the design's own cards keep.</summary>
    private static void Move(Microsoft.UI.Windowing.AppWindow window)
        => window.Move(new PointInt32(_work.Left + (int)(24 * _scale), _work.Top + (int)(24 * _scale)));

    /// <summary>Low-priority dispatcher turns around a short delay, so queued layout work has run.
    /// <para>Kept at 150 ms: DWM stops refreshing the surface PrintWindow reads once a window sits still, so a longer
    /// wait produces stale captures. Stock NavigationView and Expander animations may still be moving in some
    /// shots.</para></summary>
    private static async Task Settle()
    {
        await Turn();
        await Turn();
        await Task.Delay(150);
        await Turn();
    }

    private static Task Turn()
    {
        var done = new TaskCompletionSource();
        if (!App.Current.Ui.TryEnqueue(DispatcherQueuePriority.Low, () => done.TrySetResult())) done.TrySetResult();
        return done.Task;
    }

    /// <summary>The two capture methods, as recorded in index.json.</summary>
    private const string PrintMethod = "PrintWindow", RenderMethod = "RenderTargetBitmap";

    /// <summary>The digest and method of each window's previous shot: two identical consecutive captures by the same
    /// method mean a stale DWM surface. Keyed by window object, since HWND values are reused.</summary>
    private static readonly Dictionary<Window, (string Digest, string Method)> LastShot = new();

    /// <summary>Drops a window's entry as it closes, so the dictionary does not keep closed windows alive. Every
    /// capture site calls this beside its Close/Dismiss.</summary>
    private static void Forget(Window window) => LastShot.Remove(window);

    /// <summary>Settles, photographs and writes the PNG. <c>PrintWindow(PW_RENDERFULLCONTENT)</c> keeps Mica, corners
    /// and shadow; a blank result falls back to rendering the XAML content root.
    /// <para>DWM stops refreshing a still window's surface, so a capture can show the previous state. A shot identical
    /// to the window's previous one is re-rendered from the live XAML tree, which cannot be stale. Waiting longer only
    /// makes this worse.</para>
    /// <para>The comparison only works between shots taken by the same method, so once a window falls back to the
    /// live tree, all its later shots do too.</para></summary>
    private static async Task Save(string dir, string name, string theme, Window window, bool preferRender = false)
    {
        // A window whose last shot came from the live tree keeps using it, so consecutive shots stay comparable.
        bool pinned = LastShot.TryGetValue(window, out (string Digest, string Method) last) && last.Method == RenderMethod;
        preferRender = preferRender || pinned;
        if (pinned) App.Current.Log.Info($"screenshots: {name}-{theme}.png takes the live tree; this window's previous shot did");
        // Activating makes DWM compose a fresh frame; only needed for PrintWindow, so the desktop is not disturbed
        // otherwise.
        if (!preferRender) window.Activate();
        await Settle();
        QuiesceScrollBars(window.Content);
        (BgraImage? img, string method) = await Photograph(window, preferRender);
        string file = $"{name}-{theme}.png";
        if (img == null)
        {
            _failures++;
            App.Current.Log.Warn($"screenshots: {file} produced no pixels");
            return;
        }
        string digest = Digest(img);
        // Same window, same method, same bytes: the DWM surface is stale.
        if (LastShot.TryGetValue(window, out last) && last.Method == method && last.Digest == digest)
        {
            App.Current.Log.Warn($"screenshots: {file} is byte-identical to the previous shot of this window; DWM's surface is stale, rendering the live tree instead");
            (BgraImage? live, string liveMethod) = await Photograph(window, preferRender: true);
            if (live != null) { img = live; method = liveMethod; digest = Digest(img); }
            if (last.Digest == digest) App.Current.Log.Warn($"screenshots: {file} is still identical after the re-render; the two states really do look the same");
        }
        LastShot[window] = (digest, method);
        File.WriteAllBytes(Path.Combine(dir, file), Bitmaps.EncodePng(img));
        int dash = name.IndexOf('-');
        Index.Add(new Shot(file, dash < 0 ? name : name[..dash], dash < 0 ? "default" : name[(dash + 1)..], theme, method, img.Width, img.Height, _scale));
        App.Current.Log.Info($"screenshots: {file} {img.Width}x{img.Height} via {method}");
    }

    /// <summary>
    /// Puts every <c>ScrollBar</c> under this window into its resting, invisible state before the shot. WinUI shows
    /// the panning indicator whenever a <c>ScrollViewer</c>'s extent changes, which otherwise makes shots vary run
    /// to run.
    /// <para>Uses the template's own <c>NoIndicator</c> and <c>Collapsed</c> states without transitions: the state the
    /// fade would reach by itself.</para>
    /// </summary>
    private static void QuiesceScrollBars(DependencyObject? root)
    {
        if (root == null) return;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is Microsoft.UI.Xaml.Controls.Primitives.ScrollBar bar)
            {
                VisualStateManager.GoToState(bar, "NoIndicator", useTransitions: false);
                VisualStateManager.GoToState(bar, "Collapsed", useTransitions: false);
            }
            QuiesceScrollBars(child);
        }
    }

    /// <summary>A cheap content hash of a shot, for the repeat check alone.</summary>
    private static string Digest(BgraImage image)
        => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(image.Data));

    /// <summary>
    /// The window's pixels through PrintWindow, or the rendered XAML content root (no Mica, corners or shadow) when
    /// that comes back blank or <paramref name="preferRender"/> is set.
    /// </summary>
    private static async Task<(BgraImage? Img, string Method)> Photograph(Window window, bool preferRender = false)
    {
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        BgraImage? shot = preferRender ? null : PrintWindowShot(hwnd);
        if (shot != null && !IsBlank(shot)) return (shot, PrintMethod);
        BgraImage? rendered = await RenderShot(window);
        // RenderTargetBitmap uses the content root's last layout size, which for a popup parked off-screen can be
        // larger than the window. Trim a larger render to the window; a smaller one (Mica frame) is kept whole.
        if (rendered != null && User32.GetWindowRect(hwnd, out User32.Rect r) && Trim(rendered, r.Right - r.Left, r.Bottom - r.Top) is { } trimmed)
        {
            App.Current.Log.Info($"screenshots: fallback render was {rendered.Width}x{rendered.Height} for a {r.Right - r.Left}x{r.Bottom - r.Top} window; trimmed to the window");
            return (trimmed, RenderMethod);
        }
        return (rendered, RenderMethod);
    }

    /// <summary>The top-left <paramref name="width"/> × <paramref name="height"/> of an image that is bigger than that;
    /// null when it is not, which is the ordinary case.</summary>
    private static BgraImage? Trim(BgraImage img, int width, int height)
    {
        if (width <= 0 || height <= 0 || (img.Width <= width && img.Height <= height)) return null;
        int w = Math.Min(img.Width, width), h = Math.Min(img.Height, height);
        BgraImage cut = BgraImage.Blank(w, h);
        for (int y = 0; y < h; y++) Array.Copy(img.Data, y * img.Width * 4, cut.Data, y * w * 4, w * 4);
        return cut;
    }

    /// <summary>The window's own pixels into a top-down 32-bpp DIB the size of its window rect.</summary>
    private static BgraImage? PrintWindowShot(IntPtr hwnd)
    {
        if (!User32.GetWindowRect(hwnd, out User32.Rect r)) return null;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return null;
        IntPtr screen = User32.GetDC(IntPtr.Zero);
        IntPtr dc = Gdi32.CreateCompatibleDC(screen);
        var header = new Gdi32.BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<Gdi32.BitmapInfoHeader>(),
            Width = w, Height = -h,   // negative: top-down, so the rows line up with BgraImage's
            Planes = 1, BitCount = 32,
        };
        IntPtr dib = Gdi32.CreateDIBSection(dc, ref header, 0 /*DIB_RGB_COLORS*/, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = dib == IntPtr.Zero ? IntPtr.Zero : Gdi32.SelectObject(dc, dib);
        BgraImage? img = null;
        if (dib != IntPtr.Zero && User32.PrintWindow(hwnd, dc, PwRenderFullContent))
        {
            Gdi32.GdiFlush();
            img = BgraImage.Blank(w, h);
            Marshal.Copy(bits, img.Data, 0, img.Data.Length);
            // DWM can leave alpha at zero for an opaque window. Keep per-pixel alpha when present (rounded corners),
            // otherwise make the image opaque.
            int opaque = 0;
            for (int i = 3; i < img.Data.Length; i += 4) if (img.Data[i] != 0) opaque++;
            if (opaque * 100 < w * h) for (int i = 3; i < img.Data.Length; i += 4) img.Data[i] = 255;
        }
        if (old != IntPtr.Zero) Gdi32.SelectObject(dc, old);
        if (dib != IntPtr.Zero) Gdi32.DeleteObject(dib);
        Gdi32.DeleteDC(dc);
        User32.ReleaseDC(IntPtr.Zero, screen);
        return img;
    }

    /// <summary>The XAML content root only: no Mica, no window frame, no shadow. The fallback for a window PrintWindow
    /// hands back black.</summary>
    private static async Task<BgraImage?> RenderShot(Window window)
    {
        if (window.Content is not UIElement root) return null;
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(root);
        if (rtb.PixelWidth <= 0 || rtb.PixelHeight <= 0) return null;
        var img = BgraImage.Blank(rtb.PixelWidth, rtb.PixelHeight);
        using (Stream stream = (await rtb.GetPixelsAsync()).AsStream()) stream.ReadExactly(img.Data);
        // RenderTargetBitmap leaves unpainted pixels (most of a Mica page) transparent, so composite over the theme's
        // backdrop colour. The pixels are premultiplied: src + backdrop x (1 - a).
        Color backdrop = ThemeManager.BackdropColor();
        for (int i = 0; i < img.Data.Length; i += 4)
        {
            int inverse = 255 - img.Data[i + 3];
            if (inverse != 0)
            {
                img.Data[i] = (byte)Math.Min(255, img.Data[i] + backdrop.B * inverse / 255);
                img.Data[i + 1] = (byte)Math.Min(255, img.Data[i + 1] + backdrop.G * inverse / 255);
                img.Data[i + 2] = (byte)Math.Min(255, img.Data[i + 2] + backdrop.R * inverse / 255);
            }
            img.Data[i + 3] = 255;
        }
        return img;
    }

    /// <summary>
    /// True when the print came back as an empty frame: the interior inside the caption strip and border is one flat
    /// colour, which no real window of this app is.
    /// </summary>
    private static bool IsBlank(BgraImage img)
    {
        int left = Math.Min(8, img.Width / 4), right = img.Width - left;
        int top = Math.Min(48, img.Height / 4), bottom = img.Height - Math.Min(8, img.Height / 4);
        if (right - left < 8 || bottom - top < 8) return false;
        int i0 = (top * img.Width + left) * 4;
        byte b = img.Data[i0], g = img.Data[i0 + 1], r = img.Data[i0 + 2];
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                int i = (y * img.Width + x) * 4;
                if (img.Data[i] != b || img.Data[i + 1] != g || img.Data[i + 2] != r) return false;
            }
        return true;
    }
}
