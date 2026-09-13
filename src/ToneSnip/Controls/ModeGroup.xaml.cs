using ToneSnip.App.Theme;
using ToneSnip.Core.Capture;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace ToneSnip.App.Controls;

/// <summary>The four capture modes as one radio group under the sliding accent indicator, shared by the overlay
/// toolbar and the Recent flyout. It only shows the mode: a click reports it and leaves the decision to the host.</summary>
public sealed partial class ModeGroup : UserControl
{
    /// <summary>The overlay modes, in the order the buttons sit in.</summary>
    private static readonly SnipMode[] Modes = { SnipMode.Rectangle, SnipMode.Window, SnipMode.FullScreen, SnipMode.Freeform };

    /// <summary>Tooltip text with no shortcut, in button order; also the accessible name, which never carries the
    /// letter (a screen reader user is not the overlay's keyboard shortcut audience).</summary>
    private static readonly string[] BaseTips = { "Rectangle", "Window", "Full screen", "Freeform" };
    /// <summary>The overlay-only shortcut letters, same order.</summary>
    private static readonly string[] ShortcutKeys = { "R", "W", "F", "L" };

    private readonly RadioButton[] _buttons;
    private Storyboard? _slide;
    /// <summary>Where the indicator is, or is sliding to; NaN until the first placement, which never compares equal.</summary>
    private double _target = double.NaN;
    private SnipMode _mode = SnipMode.Rectangle;
    private bool _showShortcuts;

    public ModeGroup()
    {
        InitializeComponent();
        _buttons = new[] { BRect, BWindow, BFull, BFree };
        Show(animate: false);
    }

    /// <summary>Appends the overlay-only shortcut letters to each tooltip. The toolbar sets this true; the Recent
    /// flyout's mode group leaves it false, because those letters mean nothing away from the overlay.</summary>
    public bool ShowShortcuts
    {
        get => _showShortcuts;
        set { _showShortcuts = value; ApplyTooltips(); }
    }

    private void ApplyTooltips()
    {
        for (int i = 0; i < _buttons.Length; i++)
            ToolTipService.SetToolTip(_buttons[i], _showShortcuts ? $"{BaseTips[i]} ({ShortcutKeys[i]})" : BaseTips[i]);
    }

    /// <summary>The mode drawn as checked. Setting it slides the indicator; it never raises <see cref="ModeClicked"/>.</summary>
    public SnipMode Mode
    {
        get => _mode;
        set { _mode = value; Show(animate: true); }
    }

    /// <summary>A mode button was clicked. The group has already gone back to showing <see cref="Mode"/>.</summary>
    public event Action<SnipMode>? ModeClicked;

    private void OnClick(object sender, RoutedEventArgs e)
    {
        int i = Array.IndexOf(_buttons, (RadioButton)sender);
        if (i < 0) return;
        // The click has already checked the button; the host decides whether the mode moves, so Show restores whatever
        // Mode still says. A RadioButton raises Click even when already checked, so clicking the live mode can re-run
        // a snip.
        Show(animate: false);
        ModeClicked?.Invoke(Modes[i]);
    }

    private void Show(bool animate)
    {
        int i = IndexOf(_mode);
        for (int b = 0; b < _buttons.Length; b++) _buttons[b].IsChecked = b == i;
        SlideTo(i * 36 + 8, animate);   // 36 = button + gap; 8 centres the 16 wide indicator under a 32 wide button
    }

    /// <summary>The two hotkey-only modes have no button of their own and read as full screen.</summary>
    private static int IndexOf(SnipMode mode) => mode switch
    {
        SnipMode.Window => 1,
        SnipMode.FullScreen or SnipMode.FullScreenAll or SnipMode.ActiveWindow => 2,
        SnipMode.Freeform => 3,
        _ => 0,
    };

    private void SlideTo(double x, bool animate)
    {
        // Hosts re-assert the mode on every refresh, so a repeat of the current target is ignored to avoid restarting
        // the slide and flickering.
        if (x == _target) return;
        _target = x;
        // Stopping a held storyboard resets Offset.X to its base value (the first slot), so the current value is read
        // before the Stop and written back after it, and the animation starts explicitly From it.
        double cur = Offset.X;
        _slide?.Stop();
        _slide = null;
        if (!animate || !ThemeManager.AnimationsEnabled) { Offset.X = x; return; }
        Offset.X = cur;
        var move = new DoubleAnimation
        {
            From = cur,
            To = x,
            Duration = new Duration(TimeSpan.FromMilliseconds(167)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(move, Offset);
        Storyboard.SetTargetProperty(move, "X");
        // Held so it keeps the value it animated to; the next change stops it.
        _slide = new Storyboard();
        _slide.Children.Add(move);
        _slide.Begin();
    }

    /// <summary>A UserControl has no peer of its own; without this the group's AutomationProperties never reach UIA.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new ControlGroupAutomationPeer(this);
}
