namespace ToneSnip.Core.Extract;

/// <summary>The colour picker's text: what the overlay's C key and the editor's picker copy and confirm.</summary>
public static class ColorText
{
    /// <summary>The formats the <c>colorFormat</c> setting accepts, in the order Settings lists them.</summary>
    public static readonly string[] Formats = { "hex", "rgb" };

    /// <summary>A BGRA pixel (0xAARRGGBB as read from the frame) as <c>#RRGGBB</c> or <c>rgb(r, g, b)</c>. Alpha is
    /// dropped: the frozen desktop is opaque.</summary>
    public static string Format(uint argb, string format)
    {
        byte r = (byte)(argb >> 16), g = (byte)(argb >> 8), b = (byte)argb;
        return format == "rgb" ? $"rgb({r}, {g}, {b})" : $"#{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>The colour with the luminance of the HDR pixel under it, e.g. "#FFFFFF, 480 nits": what Shift+C copies,
    /// and what the confirmation shows. Without nits (an SDR monitor) it is the colour alone.</summary>
    public static string WithNits(string colour, float? nits) => nits is float n ? $"{colour}, {n:F0} nits" : colour;

    /// <summary>The confirmation after a pick: what was copied, and the nits beside it (not as part of it) when only the
    /// colour was copied from an HDR pixel.</summary>
    public static string Confirmation(string colour, float? nits, bool copiedNits)
        => copiedNits ? "Copied " + WithNits(colour, nits) : nits is float n ? $"Copied {colour}  ·  {n:F0} nits" : "Copied " + colour;

    /// <summary>What the picker's loupe reads under it: the colour as it would be copied, and the nits beside it on an
    /// HDR pixel.</summary>
    public static string Loupe(uint argb, string format, float? nits)
        => nits is float n ? $"{Format(argb, format)}  ·  {n:F0} nits" : Format(argb, format);
}
