using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;

namespace ToneSnip.App.Controls;

/// <summary>
/// Reports a composite <see cref="UserControl"/> to UI Automation as one named <see cref="AutomationControlType.Group"/>
/// around its own children.
/// </summary>
/// <remarks>
/// <see cref="UserControl"/> has no automation peer by default, so it is absent from the UIA tree and any
/// <c>AutomationProperties</c> set on it are ignored. The base peer already reads the name and AutomationId from the
/// owner and walks the visual tree for children, so only the control type and class name are overridden.
/// </remarks>
internal sealed partial class ControlGroupAutomationPeer : FrameworkElementAutomationPeer
{
    public ControlGroupAutomationPeer(FrameworkElement owner) : base(owner) { }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

    protected override string GetClassNameCore() => Owner.GetType().Name;
}
