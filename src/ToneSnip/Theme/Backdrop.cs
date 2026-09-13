using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ToneSnip.App.Theme;

/// <summary>
/// The backdrop of the Settings and editor windows: Mica where the platform supports it (not Remote Desktop or some
/// VMs), otherwise the SolidWindowRoot style, whose ThemeResource fill follows the window's theme.
/// </summary>
internal static class Backdrop
{
    /// <summary>Whether Mica is used. TONESNIP_NO_MICA=1 forces the solid path, for the harness.</summary>
    public static bool UseMica { get; } = Environment.GetEnvironmentVariable("TONESNIP_NO_MICA") != "1" && MicaController.IsSupported();

    /// <summary>Call after InitializeComponent, on a root Grid that has no Background of its own.</summary>
    public static void Apply(Window window, Grid root)
    {
        if (UseMica) { window.SystemBackdrop = new MicaBackdrop(); return; }
        root.Style = (Style)Application.Current.Resources["SolidWindowRoot"];
    }
}
