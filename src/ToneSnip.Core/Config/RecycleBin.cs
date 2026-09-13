namespace ToneSnip.Core.Config;

/// <summary>
/// Whether a delete to the Recycle Bin can actually land there. Only fixed local volumes have a bin; elsewhere
/// <c>FOF_ALLOWUNDO</c> deletes outright, and <c>FOF_NOCONFIRMATION</c> hides the prompt saying so, so the UI asks
/// this to warn about a permanent delete.
/// </summary>
public static class RecycleBin
{
    /// <summary>GetDriveType's DRIVE_FIXED.</summary>
    public const int DriveFixed = 3;

    /// <param name="driveTypeOf">GetDriveType for a root such as <c>D:\</c>; asked only for a drive-letter path.</param>
    public static bool Covers(string? path, Func<string, int> driveTypeOf)
    {
        if (!PathGuard.IsSafeAbsolute(path)) return false;
        string p = path!.Replace('/', '\\');
        if (p.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        if (p.Length < 3 || p[1] != ':' || !char.IsAsciiLetter(p[0])) return false;
        return driveTypeOf(char.ToUpperInvariant(p[0]) + @":\") == DriveFixed;
    }
}
