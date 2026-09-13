using ToneSnip.App.Interop;
using ToneSnip.Core.Geometry;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

namespace ToneSnip.App.Overlay;

/// <summary>The seconds left before a delayed snip, in the top-right corner of the monitor that will be snipped.
/// Never takes focus.</summary>
public sealed partial class CountdownWindow : PopupWindow
{
    private readonly IntRect _monitor;
    private readonly CountdownLayout _layout;

    /// <summary>The countdown's handle while one is up, so an overlay opening over it treats it as one of the app's own
    /// windows rather than a snip target. UI thread only.</summary>
    public static IntPtr? Live { get; private set; }

    /// <summary>
    /// The countdown's window text. Never shown, but deliberately kept in release builds: it lets another process
    /// (the debug harness and its test scripts) detect that a snip is in flight before it opens anything.
    /// </summary>
    public const string ProbeTitle = "ToneSnip countdown";

    public CountdownWindow(IntRect monitor) : base(activate: false)
    {
        _monitor = monitor;
        TargetArea = monitor;                 // place at this monitor's scale, not the parking corner's
        Title = ProbeTitle;
        InitializeComponent();
        Theme.ThemeManager.Attach(this);
        _layout = CountdownLayout.For(monitor, Native.ScaleAt(monitor));
        Card.Width = Card.Height = _layout.Size;
        Label.FontSize = _layout.FontSize;
        Paint();
        Live = Hwnd;
        this.WhenClosed(() => { if (Live == Hwnd) Live = null; });
        ShowPopup(Place);
    }

    /// <summary>Top-right of its monitor, in physical pixels (<see cref="CountdownLayout.Place"/>).</summary>
    private void Place()
    {
        (int w, int h) = ContentSize();
        PlacePhysical(_layout.Place(_monitor, Scale, w, h));
    }

    /// <summary>
    /// Paints the system accent colour itself (not the lighter shade the app's Accent token uses on dark surfaces), with
    /// black or white text, whichever reads better. High contrast keeps the markup's Accent and AccentText pair.
    /// </summary>
    private void Paint()
    {
        if (Theme.ThemeManager.IsHighContrast) return;
        uint argb = Theme.ThemeManager.SystemAccentArgb;
        var accent = global::Windows.UI.Color.FromArgb(255, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        Card.Background = new SolidColorBrush(accent);
        Label.Foreground = new SolidColorBrush(Theme.ThemeManager.OnAccent(accent));
    }

    /// <summary>Shows the seconds left. The accessible name is set alongside the text because the name, not the Text,
    /// is what a live region announces, and "3 seconds" says more than "3".</summary>
    public void Set(int seconds)
    {
        Label.Text = seconds.ToString();
        AutomationProperties.SetName(Label, seconds == 1 ? "1 second" : $"{seconds} seconds");
        Controls.LiveRegion.Announce(Label);
    }
}
