namespace ToneSnip.Core.Config;

/// <summary>Explorer's own command-line forms, which do not follow the quoting rules ProcessStartInfo.ArgumentList
/// applies.</summary>
public static class ExplorerArguments
{
    /// <summary>
    /// <c>/select,"path"</c>: Explorer opens the file's folder with the file selected. The quotes must surround only the
    /// path; Explorer does not recognise a fully quoted <c>"/select,…"</c> token (which ArgumentList produces for a path
    /// with a space). Null for a path containing a double quote or ending in a backslash, either of which would break
    /// the quoting.
    /// </summary>
    public static string? Select(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('"') || path.EndsWith('\\')) return null;
        return "/select,\"" + path + "\"";
    }

    /// <summary>
    /// A folder for Explorer to open, always quoted, because Explorer treats an unquoted comma as a switch separator.
    /// A trailing backslash would escape the closing quote, so a root is written as <c>D:\.</c>. Null for a folder
    /// containing a double quote.
    /// </summary>
    public static string? Folder(string folder)
    {
        if (string.IsNullOrEmpty(folder) || folder.Contains('"')) return null;
        return "\"" + (folder.EndsWith('\\') ? folder + "." : folder) + "\"";
    }
}
