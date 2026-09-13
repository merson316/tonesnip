using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;

namespace ToneSnip.App.Controls;

/// <summary>
/// Tells a screen reader a live region changed. AutomationProperties.LiveSetting only marks the element; Narrator
/// speaks only when LiveRegionChanged is raised, which a name change alone does not do.
/// </summary>
internal static class LiveRegion
{
    public static void Announce(UIElement element)
    {
        // Skipped unless an assistive tool is listening. A peer is created if the element has none yet, since in a
        // freshly opened window FromElement alone returns null.
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
        (FrameworkElementAutomationPeer.FromElement(element) ?? FrameworkElementAutomationPeer.CreatePeerForElement(element))
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
