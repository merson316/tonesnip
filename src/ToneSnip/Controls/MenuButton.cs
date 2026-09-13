using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace ToneSnip.App.Controls;

/// <summary>
/// A <see cref="Button"/> whose only job is to open its <see cref="Button.Flyout"/>, told to UI Automation as such.
/// </summary>
/// <remarks>
/// <para>
/// <c>DropDownButton</c> has the <c>ExpandCollapse</c> pattern built in, but it draws a chevron that the default
/// template hard-codes and lightweight styling cannot hide. Rather than replace the template, this keeps a plain
/// <see cref="Button"/> look and adds the semantics through its own automation peer.
/// </para>
/// <para>
/// WinUI 3 has no <c>ExpandCollapseState</c> attached property, and <c>FlyoutBase</c> exposes no <c>IsOpen</c>, so the
/// state is tracked from the flyout's <c>Opened</c>/<c>Closed</c> events.
/// </para>
/// </remarks>
public sealed partial class MenuButton : Button
{
    private bool _open;

    public MenuButton()
    {
        // Flyout is set in markup after the constructor runs, so it is hooked on Loaded.
        Loaded += (_, _) => Hook();
        Unloaded += (_, _) => Unhook();
    }

    private FlyoutBase? _hooked;

    private void Hook()
    {
        if (ReferenceEquals(_hooked, Flyout)) return;
        Unhook();
        _hooked = Flyout;
        if (_hooked == null) return;
        _hooked.Opened += OnFlyoutOpened;
        _hooked.Closed += OnFlyoutClosed;
    }

    private void Unhook()
    {
        if (_hooked == null) return;
        _hooked.Opened -= OnFlyoutOpened;
        _hooked.Closed -= OnFlyoutClosed;
        _hooked = null;
    }

    private void OnFlyoutOpened(object? sender, object e) => SetOpen(true);
    private void OnFlyoutClosed(object? sender, object e) => SetOpen(false);

    private void SetOpen(bool open)
    {
        if (_open == open) return;
        _open = open;
        // FromElement answers null unless a UIA client is listening, so this costs nothing when nobody is.
        if (FrameworkElementAutomationPeer.FromElement(this) is MenuButtonAutomationPeer peer) peer.RaiseStateChanged(open);
    }

    /// <summary>Whether the flyout is showing, as the peer reports it.</summary>
    internal bool IsFlyoutOpen => _open;

    internal void OpenFlyout() { Hook(); Flyout?.ShowAt(this); }
    internal void CloseFlyout() => Flyout?.Hide();

    protected override AutomationPeer OnCreateAutomationPeer() => new MenuButtonAutomationPeer(this);
}

/// <summary>Adds <see cref="IExpandCollapseProvider"/> to the stock button peer, so a UIA client can see that the
/// button opens a menu, and open or close it.</summary>
internal sealed partial class MenuButtonAutomationPeer : ButtonAutomationPeer, IExpandCollapseProvider
{
    private readonly MenuButton _owner;

    public MenuButtonAutomationPeer(MenuButton owner) : base(owner) => _owner = owner;

    protected override object? GetPatternCore(PatternInterface pattern)
        => pattern == PatternInterface.ExpandCollapse ? this : base.GetPatternCore(pattern);

    public ExpandCollapseState ExpandCollapseState
        => _owner.IsFlyoutOpen ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

    public void Expand() => _owner.OpenFlyout();
    public void Collapse() => _owner.CloseFlyout();

    internal void RaiseStateChanged(bool open)
        => RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
                                     open ? ExpandCollapseState.Collapsed : ExpandCollapseState.Expanded,
                                     open ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);
}
