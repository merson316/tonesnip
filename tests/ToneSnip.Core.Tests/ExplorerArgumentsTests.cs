using ToneSnip.Core.Config;
using Xunit;

namespace ToneSnip.Core.Tests;

public class ExplorerArgumentsTests
{
    [Fact]
    public void The_path_is_quoted_after_the_select_switch_not_with_it()
    {
        // Explorer recognises /select,"C:\a b\c.png" but not a fully quoted "/select,C:\a b\c.png" token.
        Assert.Equal(@"/select,""D:\Screen Grabs\Snip 2026-01-01 120000.png""",
            ExplorerArguments.Select(@"D:\Screen Grabs\Snip 2026-01-01 120000.png"));
    }

    [Theory]
    [InlineData("C:\\a\" /e,\"C:\\Windows")]   // a quote would end the argument and let the rest become switches
    [InlineData("C:\\a\\b\\")]                  // a trailing backslash would escape the closing quote
    public void A_path_that_could_break_out_of_its_quotes_is_refused(string path)
    {
        Assert.Null(ExplorerArguments.Select(path));
    }
}

public class ExplorerFolderArgumentsTests
{
    [Theory]
    [InlineData(@"D:\Shots,2026", @"""D:\Shots,2026""")]              // a comma is a switch separator to Explorer unless quoted
    [InlineData(@"D:\Screen Grabs", @"""D:\Screen Grabs""")]
    [InlineData(@"D:\", @"""D:\.""")]                                    // a trailing backslash would escape the closing quote
    [InlineData(@"\\nas\shots\", @"""\\nas\shots\.""")]
    public void A_folder_is_always_quoted_whole(string folder, string expected)
    {
        Assert.Equal(expected, ExplorerArguments.Folder(folder));
    }

    [Fact]
    public void A_folder_a_quote_could_escape_is_refused()
    {
        Assert.Null(ExplorerArguments.Folder("C:\\a\" /select,\"C:\\Windows"));
    }
}
