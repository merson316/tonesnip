using ToneSnip.Core.Hotkeys;
using Xunit;

namespace ToneSnip.Core.Tests;

public class ChordTests
{
    [Theory]
    [InlineData("PrintScreen", 0x2C, false, false, false, false)]
    [InlineData("Ctrl+PrintScreen", 0x2C, true, false, false, false)]
    [InlineData("ctrl + shift + s", 0x53, true, true, false, false)]
    [InlineData("Win+Shift+S", 0x53, false, true, false, true)]
    [InlineData("Alt+F5", 0x74, false, false, true, false)]
    [InlineData("Control+Snapshot", 0x2C, true, false, false, false)]
    public void Parses_chords(string text, int vk, bool ctrl, bool shift, bool alt, bool win)
    {
        Assert.True(Chord.TryParse(text, out Chord c));
        Assert.Equal(new Chord(vk, ctrl, shift, alt, win), c);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Bogus")]
    [InlineData("Ctrl+Shift")]
    public void Rejects_bad_chords(string text) => Assert.False(Chord.TryParse(text, out _));

    [Fact]
    public void Formats_canonically()
    {
        Assert.Equal("Ctrl+Shift+PrintScreen", Chord.Parse("shift+ctrl+printscreen").ToString());
        Assert.Equal("Win+Shift+S", Chord.Parse("win+shift+s").ToString());
        Assert.Equal("F12", Chord.Parse("F12").ToString());
        Assert.Equal("", Chord.None.ToString());
    }

    [Fact]
    public void Matches_exact_modifiers_only()
    {
        Chord c = Chord.Parse("Ctrl+PrintScreen");
        Assert.True(c.Matches(0x2C, true, false, false, false));
        Assert.False(c.Matches(0x2C, true, true, false, false));
        Assert.False(c.Matches(0x2C, false, false, false, false));
        Assert.False(Chord.None.Matches(0, false, false, false, false));
    }

    [Fact]
    public void HasModifier_is_false_for_bare_keys()
    {
        Assert.False(Chord.Parse("PrintScreen").HasModifier);
        Assert.True(Chord.Parse("Alt+PrintScreen").HasModifier);
    }

    [Fact]
    public void Finds_conflicts()
    {
        var b = new[]
        {
            new HotkeyBinding(Chord.Parse("PrintScreen"), "region"),
            new HotkeyBinding(Chord.Parse("Ctrl+PrintScreen"), "window"),
            new HotkeyBinding(Chord.Parse("PrintScreen"), "activeWindow"),
            new HotkeyBinding(Chord.None, "fullScreenAll"),
            new HotkeyBinding(Chord.None, "other"),
        };
        var conflicts = HotkeyConflicts.Find(b);
        Assert.Single(conflicts);
        Assert.Equal(("region", "activeWindow"), (conflicts[0].A.Action, conflicts[0].B.Action));
    }
}
