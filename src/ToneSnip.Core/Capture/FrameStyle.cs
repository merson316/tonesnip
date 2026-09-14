namespace ToneSnip.Core.Capture;

/// <summary>
/// How the snip overlay marks the selection. Normal is the 1.0.1 outline with the size and nits in the cursor pill;
/// Viewfinder puts accent brackets on the corners and the size in a chip; Guides extends the edges across the monitor
/// and puts a pixel loupe by the cursor.
/// </summary>
public enum FrameStyle { Normal, Viewfinder, Guides }

public static class FrameStyles
{
    /// <summary>The style a stored <c>selectionFrame</c> names; anything else is Normal.</summary>
    public static FrameStyle Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "viewfinder" => FrameStyle.Viewfinder,
        "guides" => FrameStyle.Guides,
        _ => FrameStyle.Normal,
    };
}
