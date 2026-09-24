using ToneSnip.Core.Capture;
using ToneSnip.Core.Config;
using ToneSnip.Windows.Tray;

namespace ToneSnip.App;

/// <summary>The tray icon, its menu and the icon it shows.</summary>
public partial class App
{
    /// <summary>The tray icon and its menu: plain Win32 in the Windows library, so no XAML loads at startup.</summary>
    private void BuildTray()
    {
        // An empty chord means "unbound"; the Funcs read Settings.Hotkeys live so a rebinding shows on the menu's
        // next open without rebuilding it.
        static Func<string?> Accelerator(Func<string> chord) => () => { string c = chord(); return string.IsNullOrEmpty(c) ? null : c; };
        // The owner-drawn Win32 menu cannot read XAML brushes, so it gets the theme state directly. High contrast
        // overrides dark/light: the menu then paints from GetSysColor.
        var menu = new TrayMenu(_messages!, Log)
        {
            IsDark = () => Theme.ThemeManager.IsDark,
            IsHighContrast = () => Theme.ThemeManager.IsHighContrast,
        };
        menu.AddHeader("New snip");
        foreach ((string label, SnipMode mode, Func<string?>? accelerator) in new (string, SnipMode, Func<string?>?)[]
        {
            ("Rectangle", SnipMode.Rectangle, Accelerator(() => Settings.Hotkeys.Region)),
            ("Window", SnipMode.Window, Accelerator(() => Settings.Hotkeys.Window)),
            ("Full screen", SnipMode.FullScreen, null),
            ("Freeform", SnipMode.Freeform, null),
            ("All monitors", SnipMode.FullScreenAll, Accelerator(() => Settings.Hotkeys.FullScreenAll)),
            ("Active window", SnipMode.ActiveWindow, Accelerator(() => Settings.Hotkeys.ActiveWindow)),
        })
            menu.Add(label, () => Snip(mode), accelerator: accelerator);
        menu.AddSeparator();
        menu.AddHeader("Delay");
        foreach (int delay in SnipSettings.Delays)
            menu.Add(delay == 0 ? "None" : $"{delay} seconds", () => ApplySettings(Settings with { DefaultDelay = delay }), () => Settings.DefaultDelay == delay);
        menu.AddSeparator();
        menu.Add("Recent snips", ToggleFlyout, accelerator: Accelerator(() => Settings.Hotkeys.History));
        menu.Add("Open last snip", OpenLastSnip);
        menu.Add("Open Screenshots folder", OpenSaveFolder);
        menu.AddSeparator();
        menu.Add("Settings…", ShowSettings);
        menu.Add("Pause hotkeys", () => Paused = !Paused, () => Paused);
        menu.AddSeparator();
        menu.Add("Quit ToneSnip", Quit);

        (IntPtr icon, bool ownsIcon) = TrayImage();
        _tray = new TrayIcon(_messages!, icon, "ToneSnip", Log, ownsIcon);
        _tray.LeftClick += ToggleFlyout;
        _tray.RightClick += (x, y) => menu.Show(x, y);
        // The glyph is drawn in one colour, so it is redrawn when the accent, light/dark mode or its settings change.
        Theme.ThemeManager.Changed += () => RunOnUi(RefreshTrayIcon);
        // A high-contrast switch arrives through the message window; it raises ThemeManager.Changed, which redraws.
        // Posted rather than run inline, to get off the window procedure's stack before touching XAML.
        _messages!.ThemeChanged += () => RunOnUi(Theme.ThemeManager.SystemThemeChanged);
        string trayApplied = Settings.TrayIcon, themeApplied = Settings.Theme;
        // SettingsChanged fires on every settings edit, so redraw only when these values change.
        SettingsChanged += () =>
        {
            if (Settings.TrayIcon == trayApplied && Settings.Theme == themeApplied) return;
            trayApplied = Settings.TrayIcon; themeApplied = Settings.Theme;
            RefreshTrayIcon();
        };
        // The constructor already added the icon, so Shown has fired by now; log directly when it has.
        void TrayShown() => Log.Debug($"tray shown in {Program.Started.ElapsedMilliseconds} ms since process start");
        if (_tray.IsShown) TrayShown(); else _tray.Shown += TrayShown;
    }

    /// <summary>
    /// The tray icon for the current setting: the full-colour .ico, or the glyph drawn at tray size in one colour
    /// (monochrome to match the taskbar, the accent, or COLOR_WINDOWTEXT in high contrast).
    /// </summary>
    private (IntPtr Icon, bool Owns) TrayImage()
    {
        if (Settings.TrayIcon is "mono" or "accent")
        {
            uint argb = TrayGlyph.GlyphArgb(Settings.TrayIcon, Settings.Theme, Theme.ThemeManager.SystemAccentArgb, Theme.ThemeManager.IsHighContrast);
            IntPtr glyph = TrayGlyph.CreateIcon(TrayGlyph.TraySize(), argb);
            if (glyph != IntPtr.Zero) return (glyph, true);
            Log.Warn("tray icon: could not draw the glyph; falling back to icon.ico");
        }
        IntPtr icon = TrayIcon.LoadIconFile(IconFile());
        if (icon != IntPtr.Zero) return (icon, true);
        Log.Warn("tray icon: could not load icon.ico; using the system application icon");
        return (TrayIcon.DefaultIcon(), false);
    }

    /// <summary>Redraws the tray icon; TrayIcon destroys the icon it previously owned.</summary>
    private void RefreshTrayIcon()
    {
        if (_tray == null) return;
        (IntPtr icon, bool owns) = TrayImage();
        _tray.SetIcon(icon, owns);
    }

    /// <summary>The embedded .ico, unpacked into the data folder because LoadImage reads icons from disk.</summary>
    internal string IconFile()
    {
        string path = Path.Combine(AppPaths.Dir, "icon.ico");
        try
        {
            using Stream? s = typeof(App).Assembly.GetManifestResourceStream("tonesnip.icon.ico");
            if (s != null && (!File.Exists(path) || new FileInfo(path).Length != s.Length))
            {
                using FileStream f = File.Create(path);
                s.CopyTo(f);
            }
        }
        catch (Exception e) { Log.Warn("tray icon: " + e.Message); }
        return path;
    }
}
