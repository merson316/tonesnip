namespace ToneSnip.Core.Output;

/// <summary>
/// What a finished snip's notification says, based on what actually happened (saved, copied, or failed).
/// </summary>
public sealed record SnipNotice(string Title, string Detail)
{
    /// <param name="savedPath">The file written, or null when none was.</param>
    /// <param name="saveAttempted">Auto-save was on, so a null path is a failure rather than a choice.</param>
    /// <param name="copyAttempted">The clipboard setting was on.</param>
    /// <param name="copied">The clipboard write succeeded.</param>
    public static SnipNotice For(string? savedPath, bool saveAttempted, bool copyAttempted, bool copied)
    {
        if (savedPath != null)
        {
            string file = savedPath[(savedPath.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];   // Windows paths, on any test host
            return new("Snip saved", copied ? $"{file}  ·  copied to clipboard" : file);
        }
        if (saveAttempted) return new("Snip not saved", copied ? "Copied to clipboard; the save failed" : "Nothing was saved or copied");
        if (copied) return new("Snip copied", "Copied to clipboard");
        return copyAttempted ? new("Snip not copied", "The clipboard was busy; nothing was copied") : new("Snip taken", "Not copied or saved");
    }
}
