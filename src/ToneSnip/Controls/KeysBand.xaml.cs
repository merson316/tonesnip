using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace ToneSnip.App.Controls;

/// <summary>The snip screen's keys, as a band the overlay bar opens with its keys toggle, F1 or ?. Reported to UI
/// Automation as a named group whose items read "keys: what they do".</summary>
public sealed partial class KeysBand : UserControl
{
    /// <summary>
    /// The keys, as the band lists them: (keys, what they do), two pairs to a row, left then right. Kept terse so the
    /// band is no wider than the bar; the mode letters are also on the mode buttons, and the frozen desktop's window
    /// title spells each key out for a screen reader.
    /// </summary>
    internal static readonly (string Keys, string Does)[] Keys =
    {
        ("Arrows", "Move pointer"), ("Shift+Arrows", "10 px steps"),
        ("Space", "Start, finish"), ("Alt+Arrows", "Resize"),
        ("Tab", "Next window"), ("Esc", "Cancel"),
        ("R W F L", "Modes"), ("A", "Annotate"),
        ("T", "Copy text"), ("P", "Pin"),
        ("C", "Pick colour"), ("F1", "These keys"),
    };

    public KeysBand()
    {
        InitializeComponent();
        // A missing style key must not throw and take the overlay bar down; unstyled text still reads.
        Style? chip = AppStyles.Get("HotkeyChip"), chipText = AppStyles.Get("HotkeyChipText"), caption = AppStyles.Get("Caption");
        for (int i = 0; i < Keys.Length; i++)
        {
            int row = i / 2, column = i % 2 * 3;
            if (column == 0) Rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            (string keys, string does) = Keys[i];
            var capText = new TextBlock { Text = keys };
            if (chipText != null) capText.Style = chipText;
            var cap = new Border { HorizontalAlignment = HorizontalAlignment.Left, Child = capText };
            if (chip != null) cap.Style = chip;
            var text = new TextBlock { Text = does, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
            if (caption != null) text.Style = caption;
            // One item per pair: the keycap's text is left out of the tree and the action is named for both, so a
            // screen reader reads "Space: Start, finish" rather than two unrelated words.
            AutomationProperties.SetAccessibilityView(capText, AccessibilityView.Raw);
            AutomationProperties.SetName(text, keys + ": " + does);
            Grid.SetRow(cap, row); Grid.SetColumn(cap, column);
            Grid.SetRow(text, row); Grid.SetColumn(text, column + 1);
            Rows.Children.Add(cap);
            Rows.Children.Add(text);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ControlGroupAutomationPeer(this);
}
