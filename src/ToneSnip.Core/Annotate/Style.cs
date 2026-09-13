namespace ToneSnip.Core.Annotate;

/// <summary>Current drawing style. Colours are ARGB; widths and sizes are physical pixels.</summary>
public sealed record Style(uint Color, int Width, int TextSize)
{
    public const uint AccentPlaceholder = 0x00000001;   // "use the Windows accent"; hosts substitute the real colour
    // Swatches in picker order; the accent stays last, after the picker's divider.
    public static readonly uint[] Palette = { 0xFFFF4A4A, 0xFFFFB900, 0xFFFFF100, 0xFF16C60C, 0xFF0078D4, 0xFFB146C2, 0xFFFFFFFF, 0xFF000000, AccentPlaceholder };
    // Earlier swatches, index-aligned with Palette; SnipSettings.Sanitized maps a stored one to Palette.
    public static readonly uint[] LegacyPalette = { 0xFFE53935, 0xFFFB8C00, 0xFFFDD835, 0xFF43A047, 0xFF1E88E5, 0xFF8E24AA, 0xFFFFFFFF, 0xFF000000 };
    public static readonly int[] Widths = { 2, 4, 8, 12 };
    public static readonly int[] TextSizes = { 14, 20, 28 };
    public static Style Default => new(AccentPlaceholder, 4, 20);

    public static int BlurRadius(int width) => width switch { <= 2 => 4, <= 4 => 8, _ => 16 };
    public static int PixelBlock(int width) => width switch { <= 2 => 6, <= 4 => 12, _ => 24 };
}
