namespace ToneSnip.Core.Capture;

public enum SnipMode { Rectangle, Window, FullScreen, Freeform, FullScreenAll, ActiveWindow }

public static class SnipModes
{
    private static readonly (string Name, SnipMode Mode)[] Table =
    {
        ("region", SnipMode.Rectangle), ("rectangle", SnipMode.Rectangle), ("window", SnipMode.Window),
        ("fullscreen", SnipMode.FullScreen), ("freeform", SnipMode.Freeform),
        ("fullScreenAll", SnipMode.FullScreenAll), ("activeWindow", SnipMode.ActiveWindow),
    };

    /// <summary>Every spelling <see cref="TryParse"/> accepts, in table order, for command-line help and error text.</summary>
    public static readonly string[] Names = Table.Select(t => t.Name).ToArray();

    public static bool TryParse(string? text, out SnipMode mode)
    {
        foreach (var (name, m) in Table)
            if (string.Equals(name, text?.Trim(), StringComparison.OrdinalIgnoreCase)) { mode = m; return true; }
        mode = default;
        return false;
    }

    public static string Name(SnipMode mode) => Table.First(t => t.Mode == mode).Name;

    /// <summary>Instant modes capture without showing the overlay.</summary>
    public static bool IsInstant(SnipMode mode) => mode is SnipMode.FullScreenAll or SnipMode.ActiveWindow;
}

/// <summary>What a finished selection is for: an ordinary snip (clipboard, save, notification), its text copied, or
/// the snip pinned to the screen. The last two leave the clipboard's image, the save folder and Recent alone.</summary>
public enum SnipAction { Snip, CopyText, Pin }
