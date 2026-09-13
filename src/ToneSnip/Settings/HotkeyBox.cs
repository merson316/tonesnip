using ToneSnip.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using KeyChord = ToneSnip.Core.Hotkeys.Chord;

namespace ToneSnip.App.Settings;

/// <summary>
/// A hotkey recorder: click (or focus and press Space or Enter) to arm, press a combination, release to commit.
/// Escape cancels; Backspace or Delete unassigns. Keys are read through the global hook, so PrintScreen and similar
/// never reach Windows while recording.
/// <para>
/// A templated <see cref="Control"/> (Theme/Controls.xaml, key "HotkeyBox") rather than a TextBox, which would bring
/// a caret, IME and context menu and fight the hook. Keys are drawn as chips.
/// </para>
/// </summary>
public sealed class HotkeyBox : Control
{
    /// <summary>Shown as the tooltip and also exposed as UIA help text, since a tooltip only reaches pointer users.</summary>
    private const string Instruction = "Click, press the combination, release. Esc cancels, Backspace clears.";

    private const string NotSet = "Not set";

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(HotkeyBox), new PropertyMetadata("", (d, _) => ((HotkeyBox)d).Render()));

    private static HotkeyBox? _armed;
    private readonly Style? _chipStyle;
    private readonly Style? _chipTextStyle;
    private readonly Style? _captionStyle;
    private string _chord = "";
    private KeyChord _pending = KeyChord.None;
    private bool _recording;
    /// <summary>A press landed on this box and its release has not come yet; see the constructor.</summary>
    private bool _pressed;
    /// <summary>Text shown instead of the chips, such as a prompt while recording; empty shows the chips.</summary>
    private string _message = "";
    private StackPanel? _chips;
    private UIElement? _recordLine;

    public event Action? ChordChanged;

    /// <summary>The row this recorder belongs to ("Rectangle snip"), used in the accessible name; the settings card's
    /// header does not label the control for screen readers.</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Chord
    {
        get => _chord;
        set { _chord = value; if (!_recording) Render(); }
    }

    public HotkeyBox()
    {
        // Styles rather than brushes, so ThemeResources resolve against this element's theme. AppStyles.Get tolerates
        // a missing key, so a styling problem cannot stop the box recording.
        if (AppStyles.Get("HotkeyBox") is { } style) Style = style;
        _chipStyle = AppStyles.Get("HotkeyChip");
        _chipTextStyle = AppStyles.Get("HotkeyChipText");
        _captionStyle = AppStyles.Get("Caption");
        ToolTipService.SetToolTip(this, Instruction);
        AutomationProperties.SetHelpText(this, Instruction);
        AutomationProperties.SetLocalizedControlType(this, "hotkey recorder");
        // handledEventsToo, because template parts can mark PointerPressed handled first.
        // Armed on release, not press: clicking an inactive window activates it and WinUI then restores the previous
        // focus after the press, which would immediately cancel a box armed on the press. Only a release that follows
        // a press on this box arms it.
        AddHandler(PointerPressedEvent, (PointerEventHandler)((_, e) => { _pressed = true; e.Handled = true; }), true);
        AddHandler(PointerReleasedEvent, (PointerEventHandler)((_, e) => { if (_pressed) { _pressed = false; Arm(); } e.Handled = true; }), true);
        PointerCaptureLost += (_, _) => _pressed = false;
        PointerExited += (_, _) => _pressed = false;
        LostFocus += (_, _) => { if (_recording) Cancel(); };
        Unloaded += (_, _) => { if (_recording) Cancel(); };
    }

    /// <summary>Exposes the chord through a read-only Value pattern and lets recording prompts be announced.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new HotkeyBoxAutomationPeer(this);

    private sealed class HotkeyBoxAutomationPeer : FrameworkElementAutomationPeer, IValueProvider
    {
        private readonly HotkeyBox _owner;

        internal HotkeyBoxAutomationPeer(HotkeyBox owner) : base(owner) => _owner = owner;

        protected override object GetPatternCore(PatternInterface pattern)
            => pattern == PatternInterface.Value ? this : base.GetPatternCore(pattern);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;

        protected override string GetClassNameCore() => nameof(HotkeyBox);

        public bool IsReadOnly => true;

        public string Value => _owner._chord;

        /// <summary>Always throws, as UIA expects of a read-only value provider: chords come only from real key
        /// presses.</summary>
        public void SetValue(string value)
            => throw new InvalidOperationException("A hotkey is recorded by pressing the combination, not by setting a value.");
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _chips = GetTemplateChild("Chips") as StackPanel;
        _recordLine = GetTemplateChild("RecordLine") as UIElement;
        Render();
    }

    /// <summary>Space or Enter arms the focused box. That key's own release is not taken as the chord, because
    /// <see cref="OnHookKey"/> commits only on the key-up of a pending chord, which <see cref="Arm"/> clears.</summary>
    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (!_recording && e.Key is VirtualKey.Space or VirtualKey.Enter)
        {
            e.Handled = true;
            Arm();
            return;
        }
        base.OnKeyDown(e);
    }

    private void Arm()
    {
        _armed?.Cancel();
        _armed = this;
        _recording = true; _pending = KeyChord.None;
        if (_recordLine != null) _recordLine.Opacity = 1;
        Say("Press a key combination…");
        App.Current.Hook.Recorder = (chord, down) => DispatcherQueue.TryEnqueue(() => OnHookKey(chord, down));
        Focus(FocusState.Programmatic);
    }

    private void OnHookKey(KeyChord chord, bool down)
    {
        if (!_recording) return;
        if (down)
        {
            if (chord.VirtualKey == 0x1B) { Cancel(); return; }                            // Escape: keep the current hotkey
            if (chord.VirtualKey is 0x08 or 0x2E) { Commit(""); return; }                // Backspace, Delete: unassign
            bool plain = chord.VirtualKey is >= 0x30 and <= 0x39 || chord.VirtualKey is >= 0x41 and <= 0x5A;
            if (plain && !chord.HasModifier) { Say("Add Ctrl, Alt, Shift or Win"); _pending = KeyChord.None; return; }
            _pending = chord;
            Say(chord + "  (release to set)");
        }
        else if (!_pending.IsNone && chord.VirtualKey == _pending.VirtualKey) Commit(_pending.ToString());
    }

    private void Commit(string chord)
    {
        Disarm();
        Chord = chord;
        ChordChanged?.Invoke();
    }

    private void Cancel() { Disarm(); Chord = _chord; }

    /// <summary>Cancels whichever box is armed and clears the hook recorder. Called from SettingsWindow's Closed
    /// handler: Unloaded is unreliable on Close(), and a recorder left set would swallow every key system-wide.</summary>
    public static void DisarmAll() => _armed?.Cancel();

    private void Disarm()
    {
        _recording = false;
        if (_armed == this) _armed = null;
        App.Current.Hook.Recorder = null;
        if (_recordLine != null) _recordLine.Opacity = 0;
        _message = "";
    }

    /// <summary>Shows one line of text in place of the chips and announces it to screen readers. FromElement returns
    /// null unless a UIA client is listening, so this is free otherwise.</summary>
    private void Say(string message)
    {
        _message = message;
        Render();
        if (FrameworkElementAutomationPeer.FromElement(this) is { } peer)
            peer.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.MostRecent, message, "ToneSnipHotkeyBox");
    }

    /// <summary>The chord as chips, or whatever <see cref="_message"/> says, or "Not set" when it is unbound.</summary>
    private void Render()
    {
        // Accessible name "<row title>, <chord>", e.g. "Rectangle snip, Ctrl + PrintScreen"; set even before the
        // template is applied.
        AutomationProperties.SetName(this, $"{Label}, {(_chord.Length == 0 ? NotSet : _chord)}");
        if (_chips == null) return;
        _chips.Children.Clear();
        string message = _message.Length > 0 ? _message : _chord.Length == 0 ? NotSet : "";
        if (message.Length > 0)
        {
            _chips.Children.Add(Caption(message));
            return;
        }
        bool first = true;
        foreach (string key in _chord.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!first) _chips.Children.Add(Caption("+"));
            first = false;
            var chip = new Border { Style = _chipStyle, Child = new TextBlock { Style = _chipTextStyle, Text = key } };
            // Raw: the chord is already exposed through this control's Name and Value.
            AutomationProperties.SetAccessibilityView(chip, AccessibilityView.Raw);
            _chips.Children.Add(chip);
        }
    }

    private TextBlock Caption(string text) => new() { Style = _captionStyle, Text = text, TextWrapping = TextWrapping.NoWrap };
}
