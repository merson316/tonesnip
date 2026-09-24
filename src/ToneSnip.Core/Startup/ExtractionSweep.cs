namespace ToneSnip.Core.Startup;

/// <summary>
/// Which of the single-file host's extraction folders a starting tonesnip.exe may delete.
/// <para>The Windows App SDK needs the single-file exe to extract all of itself (IncludeAllContentForSelfExtract), and
/// the .NET host extracts into %TEMP%\.net\&lt;exe name&gt;\&lt;bundle hash&gt;\, one folder per build, and never deletes
/// one. Every upgrade therefore left the previous build's 100+ MB behind.</para>
/// <para>Only the folders beside this process's own are candidates, and only once they have gone untouched for
/// <see cref="MinimumAge"/>: a folder that new is either being extracted by another build starting at this moment or
/// belongs to one that has only just run.</para>
/// </summary>
public static class ExtractionSweep
{
    /// <summary>How long a sibling folder must have gone unwritten before it counts as stale.</summary>
    public static readonly TimeSpan MinimumAge = TimeSpan.FromHours(1);

    /// <summary>
    /// The folder that holds every build's extraction folder for this exe name, or null when
    /// <paramref name="baseDirectory"/> is not a default single-file extraction folder (a folder publish, the MSIX, a
    /// debugger, or DOTNET_BUNDLE_EXTRACT_BASE_DIR pointing somewhere else), in which case nothing is swept.
    /// </summary>
    /// <param name="baseDirectory">AppContext.BaseDirectory, which for an extracted single-file app is its hash folder.</param>
    /// <param name="exeName">The running exe's file name without ".exe"; the host names the middle folder after it.</param>
    public static string? BundleRoot(string baseDirectory, string exeName)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory) || string.IsNullOrWhiteSpace(exeName)) return null;
        string hashDir = Path.TrimEndingDirectorySeparator(baseDirectory);
        string? nameDir = Path.GetDirectoryName(hashDir);
        string? dotnetDir = nameDir == null ? null : Path.GetDirectoryName(nameDir);
        if (nameDir == null || dotnetDir == null) return null;
        bool shaped = Path.GetFileName(nameDir).Equals(exeName, StringComparison.OrdinalIgnoreCase)
                   && Path.GetFileName(dotnetDir).Equals(".net", StringComparison.OrdinalIgnoreCase);
        return shaped ? nameDir : null;
    }

    /// <summary>The sibling folders to delete: every one but <paramref name="current"/> that has gone unwritten for at
    /// least <see cref="MinimumAge"/>.</summary>
    public static IReadOnlyList<string> Stale(string current, IEnumerable<(string Path, DateTime LastWriteUtc)> siblings, DateTime nowUtc)
    {
        string mine = Path.TrimEndingDirectorySeparator(current);
        return siblings
            .Where(s => !Path.TrimEndingDirectorySeparator(s.Path).Equals(mine, StringComparison.OrdinalIgnoreCase))
            .Where(s => nowUtc - s.LastWriteUtc >= MinimumAge)
            .Select(s => s.Path)
            .ToList();
    }
}
