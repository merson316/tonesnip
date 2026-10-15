using System.Runtime.InteropServices;
using ToneSnip.App.Interop;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Record;
using ToneSnip.Windows.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ToneSnip.App.Record;

/// <summary>
/// The recording's controls beside the recorded area (<see cref="RecordBarLayout"/>): the countdown before it starts,
/// then the time recorded, Pause and Resume, Stop and Discard. Borderless, topmost and never activated, like the
/// overlay's toolbar, so a click leaves the keyboard with the app being recorded; and left out of every capture.
/// </summary>
public sealed partial class RecordBar : PopupWindow
{
    private readonly RecordingSession _session;
    private readonly IntRect _area, _monitor;
    private bool _ready;
    /// <summary>What the pause button last showed; null before the recording starts.</summary>
    private bool? _shownPaused;

    public RecordBar(RecordingSession session, IntRect area, IntRect monitor) : base(activate: false)
    {
        _session = session;
        _area = area;
        _monitor = monitor;
        TargetArea = monitor;                 // measure and place at this monitor's scale, not the parking corner's
        InitializeComponent();
        Theme.ThemeManager.Attach(this);
        if (!User32.SetWindowDisplayAffinity(Hwnd, User32.WdaExcludeFromCapture))
            App.Current.Log.Warn($"record: the bar could not be left out of the capture ({Marshal.GetLastWin32Error()}); it may show in the video");
        ShowPopup(OnPlaced);
    }

    private void OnPlaced()
    {
        if (IsClosed) return;
        _ready = true;
        Place();
    }

    private void Place()
    {
        if (!_ready || IsClosed) return;
        (int w, int h) = ContentSize();
        PlacePhysical(RecordBarLayout.Place(_area, _monitor, w, h, (int)Math.Round(8 * Scale)));
    }

    /// <summary>The seconds before the recording starts. Pause has nothing to pause yet; Stop and Discard both cancel.</summary>
    public void ShowCountdown(int seconds)
    {
        if (IsClosed) return;
        Time.Text = $"Starting in {seconds}";
        if (seconds == 3) Announce("Recording starts in 3 seconds");
    }

    /// <summary>The time recorded so far, and whether it is paused. Called a few times a second.</summary>
    public void ShowRecording(TimeSpan elapsed, bool paused)
    {
        if (IsClosed) return;
        string text = RecordingClock.Format(elapsed);
        if (Time.Text != text) Time.Text = text;
        if (_shownPaused == null) { BtnPause.IsEnabled = true; Announce("Recording"); }
        else if (_shownPaused == paused) return;
        else Announce(paused ? "Recording paused" : "Recording resumed");
        _shownPaused = paused;
        BtnPause.IsChecked = paused;
        Dot.Opacity = paused ? 0.35 : 1;
        PauseGlyph.Glyph = (string)Application.Current.Resources[paused ? "GlyphRecordResume" : "GlyphRecordPause"];
        string name = paused ? "Resume" : "Pause";
        AutomationProperties.SetName(BtnPause, name);
        ToolTipService.SetToolTip(BtnPause, name);
    }

    /// <summary>Speaks <paramref name="sentence"/> through the bar's polite live region.</summary>
    public void Announce(string sentence)
    {
        if (IsClosed) return;
        Announcer.Text = sentence;
        AutomationProperties.SetName(Announcer, sentence);
        Controls.LiveRegion.Announce(Announcer);
    }

    // Deferred: Stop and Discard close this window, which must not happen inside its own button's click.
    private void OnPause(object sender, RoutedEventArgs e) => DispatcherQueue.TryEnqueue(_session.TogglePause);
    private void OnStop(object sender, RoutedEventArgs e) => DispatcherQueue.TryEnqueue(() => _session.Stop(keep: true));
    private void OnDiscard(object sender, RoutedEventArgs e) => DispatcherQueue.TryEnqueue(() => _session.Stop(keep: false));
}
