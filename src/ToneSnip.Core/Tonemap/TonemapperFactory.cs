namespace ToneSnip.Core.Tonemap;

public static class TonemapperFactory
{
    public static readonly string[] Names = { "desktop", "hable", "aces" };

    public static ITonemapper Create(string name, TonemapParams p) => name.ToLowerInvariant() switch
    {
        "desktop" => new DesktopTonemapper(p),
        "hable" => new HableTonemapper(p),
        "aces" => new AcesTonemapper(p),
        _ => throw new ArgumentException($"Unknown tonemapper '{name}'. Choose one of: {string.Join(", ", Names)}."),
    };
}
