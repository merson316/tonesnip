using ToneSnip.Core.Config;
using Xunit;

namespace ToneSnip.Core.Tests;

/// <summary>Windows-shaped paths are asserted literally: the guard does its own rootedness and normalisation, so the
/// answers are the same on a Linux test host.</summary>
public class PathGuardTests
{
    [Theory]
    [InlineData(@"C:\Users\me\Pictures\Screenshots")]
    [InlineData(@"C:\")]
    [InlineData(@"D:/Shots/a.png")]
    [InlineData("/home/me/Pictures")]
    public void Accepts_fully_qualified_paths(string path) => Assert.True(PathGuard.IsSafeAbsolute(path));

    [Theory]
    [InlineData(@"\\nas\photos\Screenshots")]
    [InlineData(@"\\nas\photos")]
    [InlineData(@"\\nas\photos\sub\..\Screenshots")]   // back down to the share root, then in again
    public void Accepts_unc_shares(string path) => Assert.True(PathGuard.IsSafeAbsolute(path));

    /// <summary>\\server\share is a two-component root: a bare server is rejected, and nothing may climb out of the share.</summary>
    [Theory]
    [InlineData(@"\\nas")]                            // a server names no share, so no file
    [InlineData(@"\\nas\")]
    [InlineData(@"\\nas\photos\..\Screenshots")]      // ".." past the share names a *different* share
    [InlineData(@"\\nas\photos\..\..\x.png")]        // pops the share
    [InlineData(@"\\nas\photos\..\..\..\x.png")]
    [InlineData(@"\\nas\photos\sub\..\..\other\x.png")]
    [InlineData(@"//nas/photos/../../x.png")]
    public void Unc_shares_are_a_fixed_two_component_root(string path) => Assert.False(PathGuard.IsSafeAbsolute(path));

    [Fact]
    public void Is_under_cannot_be_escaped_sideways_out_of_a_share()
    {
        Assert.True(PathGuard.IsUnder(@"\\nas\photos\sub\..\a.png", @"\\nas\photos"));
        Assert.False(PathGuard.IsUnder(@"\\nas\photos\..\..\other\a.png", @"\\nas\photos"));
        Assert.False(PathGuard.IsUnder(@"\\nas\other\a.png", @"\\nas\photos"));
    }

    /// <summary>MS-DOS device names resolve to a device in any segment, whatever the extension.</summary>
    [Theory]
    [InlineData(@"C:\Shots\CON")]
    [InlineData(@"C:\Shots\con")]
    [InlineData(@"C:\Shots\CON.png")]
    [InlineData(@"C:\Shots\CoN.PnG")]
    [InlineData(@"C:\Shots\nul")]
    [InlineData(@"C:\Shots\NUL.png")]
    [InlineData(@"C:\Shots\prn.jpg")]
    [InlineData(@"C:\Shots\AUX")]
    [InlineData(@"C:\Shots\COM1")]
    [InlineData(@"C:\Shots\com9.png")]
    [InlineData(@"C:\Shots\LPT1.jxr")]
    [InlineData(@"C:\Shots\lpt9")]
    [InlineData(@"C:\CON\a.png")]                     // a directory segment counts too
    [InlineData(@"C:\Shots\com4\a.png")]
    [InlineData(@"C:\Shots\CON.a.png")]               // the stem is everything before the first dot
    [InlineData(@"\\nas\photos\NUL.png")]
    [InlineData(@"C:\Shots\COM¹.png")]              // Windows reserves the superscript-digit ports too
    [InlineData(@"C:\Shots\com²")]
    [InlineData(@"C:\Shots\LPT³.png")]
    [InlineData(@"C:\Shots\CONIN$")]
    [InlineData(@"C:\Shots\conout$.png")]
    public void Rejects_ms_dos_device_names_in_any_segment(string path) => Assert.False(PathGuard.IsSafeAbsolute(path));

    [Theory]
    [InlineData(@"C:\Shots\CONSOLE.png")]
    [InlineData(@"C:\Shots\CONTACTS.png")]
    [InlineData(@"C:\Shots\COM0.png")]
    [InlineData(@"C:\Shots\COM10.png")]
    [InlineData(@"C:\Shots\LPT0.png")]
    [InlineData(@"C:\Shots\AUXILIARY.png")]
    [InlineData(@"C:\Shots\NULL.png")]
    [InlineData(@"C:\Comics\a.png")]
    public void Accepts_names_that_merely_start_like_a_device(string path) => Assert.True(PathGuard.IsSafeAbsolute(path));

    [Fact]
    public void Describe_shows_a_rejected_path_without_carrying_its_control_characters()
    {
        Assert.Equal("<null>", PathGuard.Describe(null));
        Assert.Equal(@"C:\Shots\a.png", PathGuard.Describe(@"C:\Shots\a.png"));
        Assert.Equal("C:\\Shots\\a\uFFFDb.png", PathGuard.Describe("C:\\Shots\\a\u0001b.png"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"Pictures\Screenshots")]
    [InlineData(@"..\..\Windows")]
    [InlineData(@"C:Screenshots")]          // drive-relative, not rooted
    [InlineData(@"\Screenshots")]           // rooted but drive-less
    [InlineData("https://example.com/x")]
    public void Rejects_paths_that_are_not_fully_qualified(string? path) => Assert.False(PathGuard.IsSafeAbsolute(path));

    [Theory]
    [InlineData("C:\\Users\\me\\a\u0001b")]
    [InlineData("C:\\Users\\me\\a\nb")]
    [InlineData("C:\\Users\\me\\a\0b")]
    public void Rejects_control_characters(string path) => Assert.False(PathGuard.IsSafeAbsolute(path));

    [Theory]
    [InlineData(@"C:\Shots\a|b.png")]
    [InlineData(@"C:\Shots\*.png")]
    [InlineData(@"C:\Shots\a<b.png")]
    [InlineData("C:\\Shots\\a\"b.png")]
    public void Rejects_wildcards_and_other_characters_a_file_name_cannot_hold(string path)
        => Assert.False(PathGuard.IsSafeAbsolute(path));

    [Theory]
    [InlineData(@"\\.\PIPE\x")]
    [InlineData(@"\\?\C:\x")]
    [InlineData(@"//./PIPE/x")]
    [InlineData(@"\\.\C:\Shots\a.png")]
    public void Rejects_device_paths(string path) => Assert.False(PathGuard.IsSafeAbsolute(path));

    [Fact]
    public void Rejects_a_path_that_climbs_above_its_own_root()
        => Assert.False(PathGuard.IsSafeAbsolute(@"C:\Shots\..\..\..\x.png"));

    [Fact]
    public void Is_under_accepts_the_root_itself_and_anything_below_it()
    {
        Assert.True(PathGuard.IsUnder(@"C:\Shots\a.png", @"C:\Shots"));
        Assert.True(PathGuard.IsUnder(@"C:\Shots\2026\a.png", @"C:\Shots"));
        Assert.True(PathGuard.IsUnder(@"C:\Shots", @"C:\Shots"));
        Assert.True(PathGuard.IsUnder(@"C:\Shots\a.png", @"C:\Other", @"C:\Shots\"));
        Assert.True(PathGuard.IsUnder(@"c:/SHOTS/a.png", @"C:\Shots"));   // case- and separator-insensitive
    }

    [Fact]
    public void Is_under_rejects_a_sibling_that_merely_shares_a_prefix()
    {
        Assert.False(PathGuard.IsUnder(@"C:\Shots2\a.png", @"C:\Shots"));
        Assert.False(PathGuard.IsUnder(@"C:\Other\a.png", @"C:\Shots"));
        Assert.False(PathGuard.IsUnder(@"C:\Shots\a.png"));   // no roots at all
    }

    [Fact]
    public void Is_under_rejects_a_traversal_that_escapes_the_root()
    {
        Assert.False(PathGuard.IsUnder(@"C:\Shots\..\Windows\System32\drivers\etc\hosts", @"C:\Shots"));
        Assert.False(PathGuard.IsUnder(@"C:\Shots\sub\..\..\Windows\x.png", @"C:\Shots"));
        Assert.True(PathGuard.IsUnder(@"C:\Shots\sub\..\a.png", @"C:\Shots"));   // stays inside: still under
    }

    [Fact]
    public void Is_under_rejects_paths_and_roots_that_are_not_safe()
    {
        Assert.False(PathGuard.IsUnder(null, @"C:\Shots"));
        Assert.False(PathGuard.IsUnder(@"C:\Shots\a.png", @"Shots"));
        Assert.False(PathGuard.IsUnder(@"\\?\C:\Shots\a.png", @"\\?\C:\Shots"));
    }

    [Fact]
    public void Extension_allow_list_is_case_insensitive_and_dot_agnostic()
    {
        Assert.True(PathGuard.HasExtension(@"C:\Shots\a.PNG", ".png", ".jpg"));
        Assert.True(PathGuard.HasExtension(@"C:\Shots\a.png", "png"));
        Assert.True(PathGuard.HasExtension(@"C:\Shots\a.JxR", ".jxr"));
        Assert.False(PathGuard.HasExtension(@"C:\Shots\a.exe", ".png", ".jpg", ".jpeg", ".jxr"));
        Assert.False(PathGuard.HasExtension(@"C:\Shots\a", ".png"));
        Assert.False(PathGuard.HasExtension(@"C:\Shots\a.png"));            // empty allow-list allows nothing
        Assert.False(PathGuard.HasExtension(@"Shots\a.png", ".png"));       // not fully qualified
        Assert.False(PathGuard.HasExtension(null, ".png"));
    }
}
