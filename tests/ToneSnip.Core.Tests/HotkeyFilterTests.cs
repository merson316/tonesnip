using ToneSnip.Core.Hotkeys;
using Xunit;

namespace ToneSnip.Core.Tests;

public class HotkeyFilterTests
{
    private const int PrintScreen = 0x2C, A = 0x41, Escape = 0x1B, Shift = 0xA0;
    private long _now = 10_000;

    private HotkeyFilter Filter(params HotkeyBinding[] bindings)
        => new(() => _now) { Bindings = bindings, Swallow = true };

    private static HotkeyBinding Region => new(new Chord(PrintScreen, false, false, false, false), "region");

    [Fact]
    public void A_bound_press_fires_once_and_swallows_its_down_and_its_up()
    {
        HotkeyFilter f = Filter(Region);
        KeyDecision down = f.OnKey(PrintScreen, isDown: true, injected: false, KeyMods.None);
        Assert.Equal("region", down.Fired?.Action);
        Assert.True(down.Swallow);
        _now += 90;
        KeyDecision up = f.OnKey(PrintScreen, isDown: false, injected: false, KeyMods.None);
        Assert.Null(up.Fired);
        Assert.True(up.Swallow);
    }

    [Fact]
    public void Auto_repeat_of_a_swallowed_press_is_swallowed_and_does_not_fire_again()
    {
        // Otherwise holding Win+Shift+S would let the repeats reach the shell and open the Snipping Tool too.
        HotkeyFilter f = Filter(Region);
        f.OnKey(PrintScreen, true, false, KeyMods.None);
        for (int i = 0; i < 5; i++)
        {
            _now += 33;
            KeyDecision repeat = f.OnKey(PrintScreen, true, false, KeyMods.None);
            Assert.Null(repeat.Fired);
            Assert.True(repeat.Swallow);
        }
    }

    [Fact]
    public void Unbound_keys_pass_untouched()
    {
        HotkeyFilter f = Filter(Region);
        Assert.Equal(new KeyDecision(false, null), f.OnKey(A, true, false, KeyMods.None));
        Assert.Equal(new KeyDecision(false, null), f.OnKey(A, false, false, KeyMods.None));
    }

    [Fact]
    public void Injected_input_passes_and_never_fires()
    {
        HotkeyFilter f = Filter(Region);
        Assert.Equal(new KeyDecision(false, null), f.OnKey(PrintScreen, true, true, KeyMods.None));
        Assert.Equal(new KeyDecision(false, null), f.OnKey(PrintScreen, false, true, KeyMods.None));
    }

    [Fact]
    public void Two_presses_inside_the_debounce_fire_once_and_two_outside_it_fire_twice()
    {
        HotkeyFilter f = Filter(Region);
        Assert.NotNull(f.OnKey(PrintScreen, true, false, KeyMods.None).Fired);
        f.OnKey(PrintScreen, false, false, KeyMods.None);
        _now += 100;
        Assert.Null(f.OnKey(PrintScreen, true, false, KeyMods.None).Fired);
        f.OnKey(PrintScreen, false, false, KeyMods.None);
        _now += 400;
        Assert.NotNull(f.OnKey(PrintScreen, true, false, KeyMods.None).Fired);
    }

    [Fact]
    public void A_press_whose_key_up_was_never_seen_still_fires_the_next_time()
    {
        // Low-level hooks miss key-ups on the secure desktop (lock screen, UAC) and for timed-out callbacks; a lost
        // key-up must not make every later press look like auto-repeat.
        HotkeyFilter f = Filter(Region);
        Assert.NotNull(f.OnKey(PrintScreen, true, false, KeyMods.None).Fired);
        _now += 60_000;                                              // ...its key-up went to the lock screen
        KeyDecision next = f.OnKey(PrintScreen, true, false, KeyMods.None);
        Assert.Equal("region", next.Fired?.Action);
        Assert.True(next.Swallow);
    }

    [Fact]
    public void A_filter_that_does_not_swallow_fires_and_passes_both_edges()
    {
        var cancel = new HotkeyBinding(new Chord(Escape, false, false, false, false), "cancelCountdown");
        HotkeyFilter f = Filter(cancel);
        f.Swallow = false;
        KeyDecision down = f.OnKey(Escape, true, false, KeyMods.None);
        Assert.Equal("cancelCountdown", down.Fired?.Action);
        Assert.False(down.Swallow);
        Assert.False(f.OnKey(Escape, false, false, KeyMods.None).Swallow);
    }

    [Fact]
    public void An_alt_or_win_chord_that_is_swallowed_asks_for_the_modifier_to_be_masked()
    {
        // Alt down, PrintScreen swallowed, Alt up reaches the foreground app as a lone Alt tap and opens its menu bar.
        var active = new HotkeyBinding(new Chord(PrintScreen, false, false, true, false), "activeWindow");
        HotkeyFilter f = Filter(active, Region);
        Assert.True(f.OnKey(PrintScreen, true, false, new KeyMods(false, false, true, false)).MaskModifier);
        _now += 1000;
        f.OnKey(PrintScreen, false, false, KeyMods.None);
        _now += 1000;
        Assert.False(f.OnKey(PrintScreen, true, false, KeyMods.None).MaskModifier);
    }

    [Fact]
    public void A_binding_that_is_not_active_neither_fires_nor_swallows_nor_debounces_another()
    {
        // An inactive Escape binding must not start a debounce that swallows a following PrintScreen.
        var cancel = new HotkeyBinding(new Chord(Escape, false, false, false, false), "cancelCountdown");
        HotkeyFilter f = Filter(cancel, Region);
        f.IsActive = b => b.Action != "cancelCountdown";
        KeyDecision esc = f.OnKey(Escape, true, false, KeyMods.None);
        Assert.Equal(default, esc);
        _now += 50;
        f.OnKey(Escape, false, false, KeyMods.None);
        _now += 50;
        Assert.Equal("region", f.OnKey(PrintScreen, true, false, KeyMods.None).Fired?.Action);
    }

    [Fact]
    public void The_debounce_is_per_binding()
    {
        var full = new HotkeyBinding(new Chord(PrintScreen, false, false, false, true), "fullScreen");
        HotkeyFilter f = Filter(full, Region);
        Assert.NotNull(f.OnKey(PrintScreen, true, false, new KeyMods(false, false, false, true)).Fired);
        _now += 20;
        f.OnKey(PrintScreen, false, false, KeyMods.None);
        _now += 20;
        Assert.Equal("region", f.OnKey(PrintScreen, true, false, KeyMods.None).Fired?.Action);
    }

    [Fact]
    public void Paused_passes_everything_and_fires_nothing()
    {
        HotkeyFilter f = Filter(Region);
        f.Paused = true;
        Assert.Equal(new KeyDecision(false, null), f.OnKey(PrintScreen, true, false, KeyMods.None));
    }

    [Fact]
    public void The_recorder_takes_each_non_modifier_edge_once_and_lets_modifiers_through()
    {
        HotkeyFilter f = Filter(Region);
        var seen = new List<(Chord, bool)>();
        f.Recorder = (c, down) => seen.Add((c, down));
        var ctrl = new KeyMods(true, false, false, false);
        Assert.False(f.OnKey(Shift, true, false, ctrl).Swallow);
        KeyDecision down = f.OnKey(PrintScreen, true, false, ctrl);
        _now += 33;
        f.OnKey(PrintScreen, true, false, ctrl);                     // auto-repeat: not reported again
        KeyDecision up = f.OnKey(PrintScreen, false, false, ctrl);
        Assert.True(down.Swallow); Assert.True(up.Swallow);
        Assert.Null(down.Fired);
        Assert.Equal(new[] { (new Chord(PrintScreen, true, false, false, false), true), (new Chord(PrintScreen, true, false, false, false), false) }, seen);
    }
}
