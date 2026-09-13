using ToneSnip.Core.Config;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace ToneSnip.App.Controls;

/// <summary>The delay choices as a band under a tool row, shared by the overlay toolbar and the Recent flyout. Like
/// <see cref="ModeGroup"/> it only shows the choice: a click reports it and the host sets <see cref="Seconds"/> back.</summary>
public sealed partial class DelayBand : UserControl
{
    private readonly RadioButton[] _chips = new RadioButton[SnipSettings.Delays.Length];
    private int _seconds;

    public DelayBand()
    {
        InitializeComponent();
        // A missing style key must not throw and take the flyout or overlay bar down; an unstyled chip still works.
        Style? style = AppStyles.Get("DelayChip");
        for (int i = 0; i < SnipSettings.Delays.Length; i++)
        {
            int seconds = SnipSettings.Delays[i];
            // RadioButtons so the chips are announced as one single-select set; the DelayChip ToggleButton style applies
            // because RadioButton derives from ToggleButton. No GroupName: grouping by parent keeps the toolbar's band
            // and the flyout's band apart.
            var chip = new RadioButton { Content = seconds == 0 ? "None" : $"{seconds} s", Tag = i };
            if (style != null) chip.Style = style;
            // The id names the delay itself (Overlay_Delay3), not its position in the table.
            AutomationProperties.SetPositionInSet(chip, i + 1);
            AutomationProperties.SetSizeOfSet(chip, SnipSettings.Delays.Length);
            AutomationProperties.SetAutomationId(chip, $"{_idPrefix}_Delay{seconds}");
            chip.Click += OnClick;
            _chips[i] = chip;
            Chips.Children.Add(chip);
        }
        Show();
    }

    private string _idPrefix = "Overlay";

    /// <summary>The window part of the chips' AutomationIds ("Flyout" gives Flyout_Delay3).</summary>
    public string IdPrefix
    {
        get => _idPrefix;
        set
        {
            _idPrefix = value;
            for (int i = 0; i < _chips.Length; i++)
                if (_chips[i] is { } chip) AutomationProperties.SetAutomationId(chip, $"{value}_Delay{SnipSettings.Delays[i]}");
        }
    }

    /// <summary>The delay drawn as checked, one of <see cref="SnipSettings.Delays"/>. Setting it never raises <see cref="Picked"/>.</summary>
    public int Seconds
    {
        get => _seconds;
        set { _seconds = value; Show(); }
    }

    /// <summary>A chip was clicked, in seconds. The band still shows <see cref="Seconds"/>.</summary>
    public event Action<int>? Picked;

    private void OnClick(object sender, RoutedEventArgs e)
    {
        int i = (int)((RadioButton)sender).Tag;
        Show();   // the click has already checked this chip; the host decides whether the delay moves
        Picked?.Invoke(SnipSettings.Delays[i]);
    }

    private void Show()
    {
        for (int i = 0; i < _chips.Length; i++) _chips[i].IsChecked = SnipSettings.Delays[i] == _seconds;
    }

    /// <summary>A UserControl has no peer of its own; without this the band's AutomationProperties never reach UIA.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new ControlGroupAutomationPeer(this);
}
