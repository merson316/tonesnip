using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using ToneSnip.App.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace ToneSnip.App.Controls;

/// <summary>
/// The app's own icon: the About expander's <see cref="ImageIcon"/>, and the window icon drawn by the title bar,
/// taskbar and Alt+Tab.
/// <para>
/// The About icon is rasterised from the embedded <c>assets/icon.svg</c> at the exact pixel size for the window's
/// <see cref="XamlRoot.RasterizationScale"/>, and again when that scale changes, so it stays sharp. The build is
/// unpackaged, so there is no ms-appx URI; the bytes come from the manifest resource.
/// </para>
/// </summary>
internal static class AppIcon
{
    /// <summary>
    /// Sets the window's title bar, taskbar, Alt+Tab and system menu icon.
    /// <para>
    /// The two icons are loaded once and shared through WM_SETICON, which does not take ownership.
    /// <c>AppWindow.SetIcon(path)</c> loads a fresh icon per window that is never destroyed, leaking GDI and USER
    /// handles on every window close.
    /// </para>
    /// </summary>
    internal static void SetWindowIcon(Window window)
    {
        try
        {
            if (_big == IntPtr.Zero)
            {
                string ico = App.Current.IconFile();
                if (!File.Exists(ico)) return;
                _big = LoadImageW(IntPtr.Zero, ico, ImageIconType, GetSystemMetrics(SmCxIcon), GetSystemMetrics(SmCyIcon), LrLoadFromFile);
                _small = LoadImageW(IntPtr.Zero, ico, ImageIconType, GetSystemMetrics(SmCxSmIcon), GetSystemMetrics(SmCySmIcon), LrLoadFromFile);
                if (_big == IntPtr.Zero) { App.Current.Log.Warn("window icon: icon.ico did not load"); return; }
            }
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            SendMessageW(hwnd, WmSetIcon, (IntPtr)IconBig, _big);
            SendMessageW(hwnd, WmSetIcon, (IntPtr)IconSmall, _small != IntPtr.Zero ? _small : _big);
        }
        catch (Exception e) { App.Current.Log.Warn("window icon: " + e.Message); }
    }

    /// <summary>The shared window icons, for the life of the process; never destroyed while a window may hold them.</summary>
    private static IntPtr _big, _small;

    private const uint WmSetIcon = 0x0080, ImageIconType = 1, LrLoadFromFile = 0x10;
    private const int IconSmall = 0, IconBig = 1, SmCxIcon = 11, SmCyIcon = 12, SmCxSmIcon = 49, SmCySmIcon = 50;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImageW(IntPtr instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>The manifest resource name ToneSnip.csproj gives assets/icon.svg.</summary>
    private const string Resource = "tonesnip.icon.svg";

    /// <summary>
    /// Loads the icon into an <see cref="ImageIcon"/>. Do not collapse the icon while it loads: a collapsed ImageIcon
    /// is never rendered, so it would never load.
    /// </summary>
    internal static void Load(ImageIcon icon, Window window, int dips) => Attach(icon, window, dips, source => icon.Source = source);

    /// <summary>
    /// Renders now (at scale 1.0 if the host is not in a tree yet), again on Loaded if the real scale differs, and on
    /// every RasterizationScale change after that. The XamlRoot.Changed subscription is dropped on the window's Closed,
    /// not on Unloaded, which WinUI 3 does not reliably raise for a window's content on Close().
    /// </summary>
    private static void Attach(FrameworkElement host, Window window, int dips, Action<SvgImageSource> assign)
    {
        if (ReadSvg() is not { } svg) return;
        double rendered = 0;
        XamlRoot? hooked = null;
        void Render()
        {
            double scale = host.XamlRoot?.RasterizationScale ?? 1.0;
            if (scale == rendered) return;
            rendered = scale;
            if (Rasterize(svg, dips, scale) is { } source) assign(source);
        }
        void OnRootChanged(XamlRoot _, XamlRootChangedEventArgs __) => Render();
        Render();
        host.Loaded += (_, _) =>
        {
            Render();
            if (hooked == null && host.XamlRoot is { } root) { hooked = root; root.Changed += OnRootChanged; }
        };
        window.WhenClosed(() => { if (hooked is { } root) root.Changed -= OnRootChanged; hooked = null; });
    }

    /// <summary>The SVG at <paramref name="dips"/> DIPs square × <paramref name="scale"/>, or null when the source could
    /// not be set up. The stream is deliberately not disposed on success: SvgImageSource re-reads it whenever it
    /// rasterises again, and a disposed stream turns the icon into an empty box.</summary>
    private static SvgImageSource? Rasterize(byte[] svg, int dips, double scale)
    {
        try
        {
            int pixels = (int)Math.Round(dips * scale);
            var source = new SvgImageSource { RasterizePixelWidth = pixels, RasterizePixelHeight = pixels };
            var bytes = new MemoryStream(svg, writable: false);
            IRandomAccessStream stream = bytes.AsRandomAccessStream();
            _ = source.SetSourceAsync(stream).AsTask()
                .ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully && t.Result == SvgImageSourceLoadStatus.Success) return;
                    Warn(t.IsCompletedSuccessfully
                        ? $"app icon: SVG rejected ({t.Result})"
                        : "app icon: SVG load failed: " + (t.Exception?.GetBaseException().Message ?? "cancelled"));
                    stream.Dispose();
                    bytes.Dispose();
                }, TaskScheduler.Default);
            return source;
        }
        catch (Exception ex) { Warn("app icon: " + ex.Message); return null; }
    }

    private static byte[]? _svg;

    /// <summary>The embedded icon.svg, read once. A build without it costs the icon and a log line.</summary>
    private static byte[]? ReadSvg()
    {
        if (_svg != null) return _svg;
        try
        {
            using Stream? s = typeof(AppIcon).Assembly.GetManifestResourceStream(Resource);
            if (s == null) { Warn("app icon: no embedded " + Resource); return null; }
            using var buffer = new MemoryStream();
            s.CopyTo(buffer);
            return _svg = buffer.ToArray();
        }
        catch (Exception ex) { Warn("app icon: " + ex.Message); return null; }
    }

    /// <summary>Guarded, because this runs on a path a broken install is already on.</summary>
    private static void Warn(string message)
    {
        try { App.Current.Log.Warn(message); }
        catch (Exception) { }
    }
}
