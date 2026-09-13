using ToneSnip.Core.Config;
using Xunit;

namespace ToneSnip.Core.Tests;

public class RecycleBinTests
{
    private const int Removable = 2, Fixed = 3, Remote = 4;

    [Fact]
    public void A_fixed_drive_has_a_recycle_bin()
        => Assert.True(RecycleBin.Covers(@"D:\Shots\a.png", root => root == @"D:\" ? Fixed : 0));

    [Theory]
    [InlineData(Removable)]
    [InlineData(Remote)]
    [InlineData(0)]
    public void A_removable_network_or_unknown_drive_has_none(int driveType)
    {
        // FOF_ALLOWUNDO deletes outright where there is no bin, and FOF_NOCONFIRMATION hides the prompt saying so.
        Assert.False(RecycleBin.Covers(@"E:\Shots\a.png", _ => driveType));
    }

    [Fact]
    public void A_unc_share_has_none_and_the_drive_type_is_not_asked()
        => Assert.False(RecycleBin.Covers(@"\\server\share\a.png", _ => throw new InvalidOperationException()));

    [Fact]
    public void A_path_that_is_not_safe_has_none()
        => Assert.False(RecycleBin.Covers(@"Shots\a.png", _ => Fixed));
}

public class SafeSaveFolderTests
{
    [Fact]
    public void An_empty_pictures_folder_does_not_give_a_relative_save_folder()
    {
        // GetFolderPath(MyPictures) returns "" when the folder does not exist; Path.Combine would then give a relative path.
        Assert.Null(new SnipSettings().SafeSaveFolder(""));
    }

    [Fact]
    public void A_relative_folder_from_settings_is_refused()
        => Assert.Null(new SnipSettings { SaveFolder = @"Shots" }.SafeSaveFolder(@"C:\Users\me\Pictures"));

    [Fact]
    public void A_normal_folder_is_returned()
        => Assert.Equal(@"C:\Users\me\Pictures\" + SnipSettings.DefaultSaveFolderName.Replace('/', '\\'),
            new SnipSettings().SafeSaveFolder(@"C:\Users\me\Pictures")?.Replace('/', '\\'));
}
