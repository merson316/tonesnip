using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ToneSnip.App;

public partial class App
{
    /// <summary>Set once the application's resource dictionaries are merged (<see cref="EnsureResources"/>). UI
    /// thread only.</summary>
    private static bool _resourcesMerged;

    /// <summary>
    /// Merges the application's resource dictionaries the first time a XAML window is made, rather than at startup.
    /// A tray app spends most of its life with no window open, and the WinUI control styles alone are several MB of
    /// parsed resources that the tray icon, the keyboard hook and the capture never read. Every XAML window calls this
    /// first thing in its constructor, before its own markup resolves a resource; once merged they stay.
    /// <para>The order is App.xaml's used to be, and matters: a dictionary sees only what was merged ahead of it.</para>
    /// </summary>
    internal static void EnsureResources()
    {
        if (_resourcesMerged) return;
        _resourcesMerged = true;
        IList<ResourceDictionary> merged = Current.Resources.MergedDictionaries;
        // Required by every WinUI control.
        merged.Add(new XamlControlsResources());
        // Icon codepoints, ahead of Controls.xaml, whose templates read them.
        merged.Add(Dictionary("Theme/Glyphs.xaml"));
        // The app's brushes and control styles.
        merged.Add(Dictionary("Theme/Controls.xaml"));
        // BasedOn the SDK's DefaultToggleSwitchStyle, so merged at this level, where a StaticResource can see
        // XamlControlsResources.
        merged.Add(Dictionary("Theme/SettingsToggle.xaml"));
        // The accent tokens live in Controls.xaml's theme dictionaries, which did not exist when the theme was applied
        // at startup.
        Theme.ThemeManager.ApplyAccentTokens();
        Current.Log.Debug("application resources merged for the first window");
    }

    private static ResourceDictionary Dictionary(string path) => new() { Source = new Uri("ms-appx:///" + path) };
}
