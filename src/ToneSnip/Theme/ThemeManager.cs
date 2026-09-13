using ToneSnip.App.Interop;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using global::Windows.UI.ViewManagement;

namespace ToneSnip.App.Theme;

/// <summary>The app's light/dark decision and the live accent. Win32 renderers read <see cref="IsDark"/> and
/// <see cref="AccentArgb"/>; XAML windows call <see cref="Attach(Window)"/> to follow the setting.</summary>
/// <remarks>A <c>{ThemeResource}</c> in markup re-resolves on a theme change; a brush read in code does not, so such
/// code needs an <c>ActualThemeChanged</c> handler. <see cref="Changed"/> is too early for that: it fires before the
/// framework has updated the tree. Annotation ink colours (overlay and viewer accent, palette swatches) are read once
/// on purpose, since they end up in the saved image.</remarks>
public static class ThemeManager
{
    private static readonly UISettings SystemSettings = new();
    private static string _mode = "auto";

    /// <summary>True when the app is showing its dark tokens.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>True when Windows is in a contrast theme. Renderers that cannot read XAML brushes (tray menu, tray
    /// glyph) must check this first; <see cref="IsDark"/> is meaningless in a contrast theme.</summary>
    /// <remarks>A contrast switch arrives both as <c>ColorValuesChanged</c> and through
    /// <see cref="SystemThemeChanged"/>; both end in <see cref="Apply"/>, so a duplicate only repaints.</remarks>
    public static bool IsHighContrast { get; private set; }

    /// <summary>The current accent as 0xAARRGGBB, for the renderers that cannot read XAML brushes.</summary>
    public static uint AccentArgb { get; private set; } = 0xFF0078D4;

    /// <summary>The unshaded system accent (<c>UIColorType.Accent</c>), for the tray glyph: the taskbar is not an app
    /// surface, so it does not take the dark-theme shade of <see cref="AccentArgb"/>.</summary>
    public static uint SystemAccentArgb { get; private set; } = 0xFF0078D4;
    /// <summary>Windows "Show animations"; true when unreadable. The app's own transitions are skipped when false.
    /// UISettings raises no event for it, so it is re-read only on ColorValuesChanged.</summary>
    public static bool AnimationsEnabled { get; internal set; } = true;

#if TONESNIP_HARNESS
    /// <summary>Set by the screenshot harness to keep <see cref="AnimationsEnabled"/> off, so captures show the settled
    /// state.</summary>
    internal static bool AnimationsPinned { get; set; }
#endif

    /// <summary>Raised on the thread that changed the colours; listeners that touch UI must marshal themselves.</summary>
    public static event Action? Changed;

    public static void Start()
    {
        // Fires for both accent and system light/dark changes.
        SystemSettings.ColorValuesChanged += (_, _) => { ReadAnimations(); ReadHighContrast(); Apply(_mode); };
        ReadAnimations();
        ReadHighContrast();
    }

    private static void ReadHighContrast() => IsHighContrast = global::ToneSnip.Windows.SystemTheme.IsHighContrast();

    /// <summary>Called on WM_THEMECHANGED: re-reads <see cref="IsHighContrast"/> and re-applies the current mode.</summary>
    public static void SystemThemeChanged() => Apply(_mode);

    /// <summary>UISettings can throw while the shell is coming up; animated is the safe default.</summary>
    private static void ReadAnimations()
    {
#if TONESNIP_HARNESS
        if (AnimationsPinned) return;
#endif
        try { AnimationsEnabled = SystemSettings.AnimationsEnabled; } catch { }
    }

    /// <summary>Follows the setting on a window's content root for as long as the window is open.</summary>
    public static void Attach(Window w)
    {
        if (Root(w) is not { } root) return;
        Action off = Follow(root);
        // Unloaded is unreliable on Close(); without this, short-lived windows would leak their roots into Changed.
        w.WhenClosed(off);
    }

    /// <summary>Applies the setting now and on every change. Returns the unsubscribe, which Unloaded also calls; a
    /// second removal is a no-op.</summary>
    private static Action Follow(FrameworkElement root)
    {
        ApplyTo(root);
        void OnChanged() => root.DispatcherQueue.TryEnqueue(() => ApplyTo(root));
        Changed += OnChanged;
        void Off() => Changed -= OnChanged;
        root.Unloaded += (_, _) => Off();
        return Off;
    }

    private static FrameworkElement? Root(Window w) => w.Content as FrameworkElement;

    /// <summary>Draws the system title bar in the app's theme rather than the desktop's; a contrast theme keeps the
    /// system caption. Call again from <see cref="Changed"/>, on the window's thread.</summary>
    public static void ApplyTitleBarTheme(Microsoft.UI.Windowing.AppWindow window)
    {
        try
        {
            window.TitleBar.PreferredTheme = IsHighContrast
                ? Microsoft.UI.Windowing.TitleBarTheme.UseDefaultAppMode
                : IsDark ? Microsoft.UI.Windowing.TitleBarTheme.Dark : Microsoft.UI.Windowing.TitleBarTheme.Light;
        }
        catch { }   // a window already closed, or a system without the preference: the default caption stands
    }

    /// <summary>Always Default in a contrast theme: XAML only consults the HighContrast theme dictionary while the
    /// element's theme is Default.</summary>
    private static void ApplyTo(FrameworkElement root)
        => root.RequestedTheme = IsHighContrast
            ? ElementTheme.Default
            : _mode switch { "dark" => ElementTheme.Dark, "light" => ElementTheme.Light, _ => ElementTheme.Default };

    public static void Apply(string mode)
    {
        // App.OnLaunched calls this before Start(), so contrast has not been read yet on the first call.
        ReadHighContrast();
        _mode = mode;
        IsDark = mode == "dark" || (mode == "auto" && SystemPrefersDark());
        AccentArgb = SystemAccent(IsDark);
        SystemAccentArgb = SystemAccent(dark: false);
        // ColorValuesChanged arrives off the UI thread and a brush belongs to the thread that made it.
        if (Application.Current is App app) app.RunOnUi(ApplyAccentTokens);
        Changed?.Invoke();
    }

    /// <summary>Sets the Light and Dark Accent and AccentText tokens to the desktop's accent; the colours in
    /// Theme/Dark.xaml and Theme/Light.xaml are the fallback.</summary>
    /// <remarks>The existing brushes are mutated rather than replaced: a ThemeResource holds the dictionary's
    /// instance, so changing its colour reaches everything on screen.</remarks>
    private static void ApplyAccentTokens()
    {
        if (Application.Current?.Resources is not { } app) return;
        // Never "HighContrast": its accent is the user's SystemColorHighlightColor.
        foreach ((string theme, bool dark) in new[] { ("Light", false), ("Dark", true) })
        {
            uint argb = SystemAccent(dark);
            var accent = global::Windows.UI.Color.FromArgb(255, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            if (ThemeBrush(app, theme, "Accent") is { } a) a.Color = accent;
            if (ThemeBrush(app, theme, "AccentText") is { } t) t.Color = OnAccent(accent);
        }
    }

    /// <summary>Black or white over <paramref name="c"/>, whichever has the higher WCAG 2 contrast ratio (crossover
    /// at a relative luminance of about 0.179).</summary>
    internal static global::Windows.UI.Color OnAccent(global::Windows.UI.Color c)
    {
        double l = RelativeLuminance(c);
        double onBlack = (l + 0.05) / 0.05;
        double onWhite = 1.05 / (l + 0.05);
        return onBlack >= onWhite ? Colors.Black : Colors.White;
    }

    /// <summary>WCAG 2 relative luminance: each channel un-gamma'd, then the 0.2126 / 0.7152 / 0.0722 sum.</summary>
    private static double RelativeLuminance(global::Windows.UI.Color c)
    {
        static double Linear(byte v)
        {
            double s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    /// <summary>The applied theme's <c>SurfaceBase</c>, for the harness: <c>RenderTargetBitmap</c> does not draw the
    /// backdrop. Looked up by theme name because the resource indexer follows the desktop's theme.</summary>
#if TONESNIP_HARNESS
    internal static global::Windows.UI.Color BackdropColor()
    {
        string theme = IsHighContrast ? "HighContrast" : IsDark ? "Dark" : "Light";
        if (Application.Current?.Resources is { } app && ThemeBrush(app, theme, "SurfaceBase") is { } brush)
            return brush.Color;
        return global::Windows.UI.Color.FromArgb(255, 0, 0, 0);
    }
#endif

    /// <summary>The brush under <paramref name="key"/> in the named theme dictionary, searched through merged
    /// dictionaries (XamlControlsResources has theme dictionaries of its own).</summary>
    private static SolidColorBrush? ThemeBrush(ResourceDictionary d, string theme, string key)
    {
        if (d.ThemeDictionaries.TryGetValue(theme, out object? t) && t is ResourceDictionary td && Entry(td, key) is { } hit) return hit;
        foreach (ResourceDictionary m in d.MergedDictionaries)
            if (ThemeBrush(m, theme, key) is { } deeper) return deeper;
        return null;
    }

    private static SolidColorBrush? Entry(ResourceDictionary d, string key)
    {
        if (d.TryGetValue(key, out object? v)) return v as SolidColorBrush;
        foreach (ResourceDictionary m in d.MergedDictionaries)
            if (Entry(m, key) is { } deeper) return deeper;
        return null;
    }

    private static bool SystemPrefersDark()
    {
        using RegistryKey? k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    /// <summary>The accent as 0xAARRGGBB: <c>AccentLight2</c> in dark, the plain accent in light.</summary>
    /// <remarks>In dark this matches the SDK's accented controls, which use <c>SystemAccentColorLight2</c>. In light the
    /// SDK uses <c>SystemAccentColorDark1</c>, so Theme/Light.xaml aliases the stock keys onto this accent.</remarks>
    private static uint SystemAccent(bool dark)
    {
        try
        {
            global::Windows.UI.Color c = SystemSettings.GetColorValue(dark ? UIColorType.AccentLight2 : UIColorType.Accent);
            return 0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        }
        catch { }
        return dark ? 0xFF60CDFFu : 0xFF005FB8u;
    }
}
