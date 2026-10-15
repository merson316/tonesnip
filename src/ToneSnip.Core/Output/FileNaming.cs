using System.Globalization;

namespace ToneSnip.Core.Output;

public static class FileNaming
{
    /// <summary>Formatted with <see cref="CultureInfo.InvariantCulture"/>: the current culture's calendar (e.g. Thai
    /// Buddhist) would change the year and break name sorting.</summary>
    public static string Build(DateTime local, string ext)
        => $"Snip {local.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture)}.{ext.TrimStart('.')}";

    /// <summary>A screen recording's name, dated as a snip's is.</summary>
    public static string BuildRecording(DateTime local)
        => $"Recording {local.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture)}.mp4";

    public static string Resolve(string folder, string fileName, Func<string, bool> exists)
    {
        string path = Path.Combine(folder, fileName);
        if (!exists(path)) return path;
        string stem = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
        for (int n = 2; ; n++)
        {
            path = Path.Combine(folder, $"{stem} ({n}){ext}");
            if (!exists(path)) return path;
        }
    }
}
