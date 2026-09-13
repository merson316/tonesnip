namespace ToneSnip.Core.Config;

/// <summary>
/// Validates stored paths from <c>settings.json</c> and <c>history.json</c> (user-writable, so treated as external
/// input) before they reach <c>explorer.exe</c> or <see cref="File.Delete"/>.
/// <para>
/// Parsed by hand rather than with <see cref="Path.IsPathFullyQualified(string)"/> and
/// <see cref="Path.GetFullPath(string)"/>, which follow the host platform: Core is tested on Linux, where
/// <c>C:\Users\me</c> is relative. These are the Windows rules, and they answer the same on either host.
/// </para>
/// </summary>
public static class PathGuard
{
    /// <summary>Characters no NTFS path component may hold. <c>?</c> and <c>*</c> are wildcards, and <c>?</c> also
    /// rules out the <c>\\?\</c> device prefix.</summary>
    private static readonly char[] Forbidden = { '<', '>', '"', '|', '?', '*' };

    /// <summary>MS-DOS device names, which Windows resolves ahead of a file of the same name in any directory
    /// (<c>C:\Shots\CON.png</c> is the console).</summary>
    private static readonly string[] Reserved =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        // Windows reserves the superscript-digit ports as well, and the console's two halves.
        "COM\u00B9", "COM\u00B2", "COM\u00B3", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
        "CONIN$", "CONOUT$",
    };

    /// <summary>The separator the comparison form is written with.</summary>
    private const char Sep = '\\';

    /// <summary>Windows matches a device name on the segment's stem — everything before the <i>first</i> dot — so
    /// <c>CON</c>, <c>CON.png</c> and <c>CON.a.png</c> are all the console, and <c>CONSOLE.png</c> is a file.</summary>
    private static bool IsReserved(string segment)
    {
        int dot = segment.IndexOf('.');
        string stem = dot < 0 ? segment : segment[..dot];
        return Array.Exists(Reserved, name => stem.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The path in a form safe to write to the log: control characters become U+FFFD.</summary>
    public static string Describe(string? path)
        => path == null ? "<null>" : string.Concat(path.Select(c => char.IsControl(c) ? '\uFFFD' : c));

    /// <summary>Fully qualified (a drive root, a UNC share or a POSIX root), free of control characters and of
    /// characters a file name cannot hold, not a device path (<c>\\.\</c>, <c>\\?\</c>), and not climbing above its
    /// own root. A UNC share is deliberately allowed: a save folder may legitimately live on one.</summary>
    public static bool IsSafeAbsolute(string? path) => Normalize(path) != null;

    /// <summary><see cref="IsSafeAbsolute"/> and, after <c>.</c>/<c>..</c> are resolved, the path is one of
    /// <paramref name="roots"/> or sits below it. Comparison is case-insensitive and separator-agnostic, so a
    /// traversal that leaves the root (<c>C:\Shots\..\Windows</c>) is rejected rather than string-matched.</summary>
    public static bool IsUnder(string? path, params string[] roots)
    {
        string? p = Normalize(path);
        if (p == null || roots is not { Length: > 0 }) return false;
        foreach (string root in roots)
        {
            string? r = Normalize(root);
            if (r == null) continue;
            if (p.Equals(r, StringComparison.OrdinalIgnoreCase)) return true;
            string prefix = r.EndsWith(Sep) ? r : r + Sep;
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary><see cref="IsSafeAbsolute"/> and the file's extension is in the allow-list. Entries may be written
    /// with or without the leading dot; matching is case-insensitive. An empty allow-list allows nothing.</summary>
    public static bool HasExtension(string? path, params string[] extensions)
    {
        string? p = Normalize(path);
        if (p == null || extensions is not { Length: > 0 }) return false;
        int slash = p.LastIndexOf(Sep);
        string name = slash >= 0 ? p[(slash + 1)..] : p;
        int dot = name.LastIndexOf('.');
        if (dot <= 0) return false;   // no extension at all, or a name that is nothing but an extension
        string ext = name[dot..];
        foreach (string wanted in extensions)
        {
            if (string.IsNullOrEmpty(wanted)) continue;
            string want = wanted[0] == '.' ? wanted : "." + wanted;
            if (ext.Equals(want, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>The comparison form of a safe path — root plus the resolved segments, joined with one separator —
    /// or null when the path is not safe. Case is left alone; every comparison above is ordinal-ignore-case.</summary>
    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (char c in path) if (char.IsControl(c) || Array.IndexOf(Forbidden, c) >= 0) return null;
        // Additive only: on Windows this is control characters plus '|', all rejected above; on Linux it is '\0'.
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;

        string root;
        int start, fixedSegments = 0;
        if (path.Length >= 2 && IsSep(path[0]) && IsSep(path[1]))
        {
            root = @"\\";              // UNC: \\server\share. The device namespaces \\.\ and \\?\ are rejected below.
            start = 2;
            // \\server\share is a two-component root: ".." may not climb out of the share.
            fixedSegments = 2;
        }
        else if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && IsSep(path[2]))
        {
            root = path[..2] + Sep;    // C:\ — a drive-relative "C:dir" is not fully qualified and falls through
            start = 3;
        }
        else if (path[0] == '/')
        {
            root = "/";                // POSIX root: the Linux test host's own shape, never a Windows save folder
            start = 1;
        }
        else return null;

        // Device paths: \\?\ is already rejected via Forbidden; \\.\ must be caught on the raw text because the loop
        // below skips "." segments.
        if (root == @"\\" && (start >= path.Length || path[start..].Split('\\', '/')[0] is "." or "")) return null;

        var segments = new List<string>();
        foreach (string raw in path[start..].Split('\\', '/'))
        {
            if (raw.Length == 0) continue;                 // a doubled or trailing separator
            if (raw == ".") continue;
            if (raw == "..")
            {
                if (segments.Count <= fixedSegments) return null;   // climbs above its own root
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            // A colon past the drive letter is an alternate data stream; Windows strips a trailing dot or space, so
            // such a name resolves to a different file than it reads as.
            if (raw.Contains(':') || raw[^1] == '.' || raw[^1] == ' ') return null;
            if (IsReserved(raw)) return null;
            segments.Add(raw);
        }
        if (segments.Count < fixedSegments) return null;   // "\\nas" names a server, and a server holds no files
        return segments.Count == 0 ? root : root + string.Join(Sep, segments);
    }

    private static bool IsSep(char c) => c == '\\' || c == '/';
}
