using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using ToneSnip.App.Interop;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Config;
using ToneSnip.Core.Hotkeys;
using ToneSnip.Core.Imaging;
using Shell = ToneSnip.Windows.Shell;
// Aliases, because the Toolkit namespace also has types whose names clash with WinUI's (ColorPicker).
using SettingsCard = CommunityToolkit.WinUI.Controls.SettingsCard;
using SettingsExpander = CommunityToolkit.WinUI.Controls.SettingsExpander;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace ToneSnip.App.Settings;

public partial class SettingsWindow : Window
{
    /// <summary>The page titles, in the order the nav items' Tags number them (About is a footer item).</summary>
    private static readonly string[] PageTitles = { "General", "Hotkeys", "Tonemap", "Output", "About" };

    private const int GeneralPage = 0, HotkeysPage = 1, TonemapPage = 2, OutputPage = 3, AboutPage = 4;

    private readonly IntPtr _hwnd;
    /// <summary>Which pages have been filled from the settings. An unvisited page's controls still hold XAML defaults,
    /// so anything reading a control checks this first.</summary>
    private readonly bool[] _realised = new bool[PageTitles.Length];
    private bool _loading = true;
    /// <summary>True while HdrFile sits in the HDR-copy expander rather than the plain card (<see cref="PlaceHdrFile"/>).</summary>
    private bool _hdrNested;
    /// <summary>True while Format sits in the format expander rather than the plain card (<see cref="PlaceFormat"/>).</summary>
    private bool _formatNested;
    private bool _closed;
    private int _previewVersion;
    /// <summary>A preview render is running, and another was requested meanwhile. One render at a time, with a final
    /// one on the latest values, so a slider drag does not queue a render per tick.</summary>
    private bool _previewRunning, _previewAgain;
    /// <summary>The window's dispatcher, read once on the UI thread for handlers raised on other threads.</summary>
    private readonly DispatcherQueue _ui;
    /// <summary>The slider values set by Load. A slider clamps stored values outside its range, so while a slider is
    /// still at its loaded position <see cref="Collect"/> keeps the stored value instead of the clamped one.</summary>
    private double _loadedExposure = double.NaN, _loadedKnee = double.NaN, _loadedJpegQuality = double.NaN;
    private WriteableBitmap? _previewBitmap;
    /// <summary>The chosen save folder, or null/empty for Pictures\Screenshots. Shown as read-only text, so the value
    /// is kept here. Only meaningful once the Output page has been realised.</summary>
    private string? _saveFolder;
#if TONESNIP_HARNESS
    /// <summary>The Save automatically switch was on before <see cref="ShowPage"/> turned it off for a screenshot.</summary>
    private bool _autoSaveWasOn;
#endif

    public SettingsWindow()
    {
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _ui = DispatcherQueue;

        Theme.Backdrop.Apply(this, RootGrid);
        ResizeToDefault();
        ClampMinSize();
        Theme.ThemeManager.ApplyTitleBarTheme(AppWindow);
        Theme.ThemeManager.Changed += OnThemeChanged;
        this.WhenClosed(() =>
        {
            _closed = true;
            App.Current.FlushSettings();   // the last change may still be waiting on the save debounce
            Theme.ThemeManager.Changed -= OnThemeChanged;
            // Unloaded is unreliable on Close(); an armed recorder left behind would swallow every key system-wide.
            HotkeyBox.DisarmAll();
            _previewBitmap = null;
        });

        Theme.ThemeManager.Attach(this);
        Nav.SelectedItem = Nav.MenuItems[0];
        SelectPage(GeneralPage);
        Controls.AppIcon.SetWindowIcon(this);
        // Loaded here rather than in LoadAbout because it loads asynchronously and should be ready when About opens.
        Controls.AppIcon.Load(AboutIcon, this, 32);
        _loading = false;
        ReconcileAutostart();
    }

    /// <summary>1180 × 760 DIPs, which fits the 860 × 360 tonemap preview.</summary>
    private void ResizeToDefault()
    {
        double scale = Native.Scale(_hwnd);
        AppWindow.Resize(new SizeInt32((int)(1180 * scale), (int)(760 * scale)));
    }

    /// <summary>
    /// Keeps the window at least 900 × 600 DIPs. PreferredMinimum* is in physical pixels and does not follow a DPI
    /// change (microsoft/microsoft-ui-xaml#10452), so it is reapplied whenever the window moves or resizes.
    /// </summary>
    private void ClampMinSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        void Apply()
        {
            double scale = Native.Scale(_hwnd);
            int minW = (int)(900 * scale), minH = (int)(600 * scale);
            if (presenter.PreferredMinimumWidth != minW) presenter.PreferredMinimumWidth = minW;
            if (presenter.PreferredMinimumHeight != minH) presenter.PreferredMinimumHeight = minH;
        }
        Apply();
        AppWindow.Changed += (sender, e) =>
        {
            if ((!e.DidSizeChange && !e.DidPositionChange) || presenter.State == OverlappedPresenterState.Minimized) return;
            Apply();
        };
    }

    /// <summary>ThemeManager.Changed can arrive on any thread, so this marshals to the UI thread and ignores a closed
    /// window.</summary>
    private void OnThemeChanged() => _ui.TryEnqueue(() => { if (!_closed) Theme.ThemeManager.ApplyTitleBarTheme(AppWindow); });

    /// <summary>The first HDR output: the one the preview line and the Advanced placeholders describe.</summary>
    private static OutputInfo? HdrOutput() => App.Current.KnownOutputs.FirstOrDefault(o => o.Hdr);

    /// <summary>Whole nits without a group separator ("1000", not "1 000", which reads as two numbers).</summary>
    private static string Nits(float value) => value.ToString("F0", CultureInfo.CurrentCulture);

    // ----- pages ----------------------------------------------------------------------------------------------------

    private FrameworkElement? Page(int idx) => idx switch
    {
        GeneralPage => PageGeneral,
        HotkeysPage => PageHotkeys,
        TonemapPage => PageTonemap,
        OutputPage => PageOutput,
        AboutPage => PageAbout,
        _ => null,
    };

    /// <summary>
    /// Fills a page from <c>App.Current.Settings</c> the first time it is opened; every change goes through
    /// <see cref="App.ApplySettings"/>, so those settings are always current.
    /// </summary>
    private void Realise(int idx)
    {
        if (idx < 0 || idx >= _realised.Length || _realised[idx]) return;
        // Set before loading: the load raises change events, and Collect must already treat this page as realised.
        _realised[idx] = true;
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            switch (idx)
            {
                case GeneralPage: LoadGeneral(App.Current.Settings); break;
                case HotkeysPage: LoadHotkeys(App.Current.Settings); break;
                case TonemapPage: LoadTonemap(App.Current.Settings); break;
                case OutputPage: LoadOutput(App.Current.Settings); break;
                case AboutPage: LoadAbout(); break;
            }
        }
        finally { _loading = wasLoading; }
    }

    private void LoadGeneral(SnipSettings s)
    {
        ThemeCombo.SelectedIndex = Math.Max(0, Array.IndexOf(SnipSettings.Themes, s.Theme));
        TrayIconCombo.SelectedIndex = Math.Max(0, Array.IndexOf(SnipSettings.TrayIcons, s.TrayIcon));
        StartWithWindows.IsOn = s.StartWithWindows; NitsReadout.IsOn = s.ShowNitsReadout;
        DefaultDelay.SelectedIndex = Math.Max(0, Array.IndexOf(SnipSettings.Delays, s.DefaultDelay));
        AfterSelect.SelectedIndex = Math.Max(0, Array.IndexOf(SnipSettings.AfterSelects, s.AfterSelect));
        PrivacyMode.IsOn = s.Annotate.PrivacyMode; ClipToLasso.IsOn = s.Annotate.ClipToLasso;
        DeleteToRecycleBin.IsOn = s.DeleteToRecycleBin;
        // Matched by each radio's Tag rather than by position in FlyoutLayouts.
        bool grid = LayoutValue(LayoutGrid).Equals(s.RecentFlyoutLayout, StringComparison.OrdinalIgnoreCase);
        LayoutGrid.IsChecked = grid; LayoutRow.IsChecked = !grid;
        // Also by Tag; the sanitised setting always names one of them, and Normal stands in if it somehow does not.
        RadioButton[] frames = FrameRadios();
        RadioButton frame = frames.FirstOrDefault(r => string.Equals(r.Tag as string, s.SelectionFrame, StringComparison.OrdinalIgnoreCase)) ?? FrameNormal;
        foreach (RadioButton r in frames) r.IsChecked = r == frame;
    }

    /// <summary>The selection frame radios, in the order Settings shows them.</summary>
    private RadioButton[] FrameRadios() => new[] { FrameNormal, FrameViewfinder, FrameGuides };

    private void LoadHotkeys(SnipSettings s)
    {
        HkRegion.Chord = s.Hotkeys.Region; HkWindow.Chord = s.Hotkeys.Window; HkFullAll.Chord = s.Hotkeys.FullScreenAll;
        HkActive.Chord = s.Hotkeys.ActiveWindow; HkHistory.Chord = s.Hotkeys.History;
        ReplaceSnippingTool.IsOn = s.Hotkeys.ReplaceSnippingTool;
        foreach (HotkeyBox box in HotkeyBoxes()) box.ChordChanged += OnChanged;
        ShowConflicts(s);
    }

    /// <summary>The five recorders. Call only once the Hotkeys page has been realised.</summary>
    private HotkeyBox[] HotkeyBoxes() => new[] { HkRegion, HkWindow, HkFullAll, HkActive, HkHistory };

    private void LoadTonemap(SnipSettings s)
    {
        Tonemap.SelectedIndex = Math.Max(0, Array.IndexOf(Core.Tonemap.TonemapperFactory.Names, s.Tonemap));
        Exposure.Value = Math.Log2(s.Exposure); Knee.Value = s.Knee; AutoExposure.IsOn = s.AutoExposure;
        _loadedExposure = Exposure.Value; _loadedKnee = Knee.Value;
        // NaN shows an empty NumberBox, meaning "use the monitor's reported value".
        SdrWhite.Value = s.SdrWhiteNits ?? double.NaN; Peak.Value = s.PeakNits ?? double.NaN;

        OutputInfo? hdr = HdrOutput();
        PreviewExpander.Description = hdr is { } o
            // The monitor's friendly name where Windows has one, otherwise the GDI device name.
            ? $"{o.FriendlyName ?? o.DeviceName} · {o.Width} × {o.Height} · HDR on · peak {Nits(o.PeakNits)} nits · SDR white {Nits(o.SdrWhiteNits)} nits"
            : "No HDR monitor reported yet.";
        SdrWhite.PlaceholderText = hdr is { } p1 ? Nits(p1.SdrWhiteNits) : "";
        Peak.PlaceholderText = hdr is { } p2 ? Nits(p2.PeakNits) : "";
        SdrWhiteRow.Description = hdr is { } c1 ? $"Windows reports {Nits(c1.SdrWhiteNits)} nits." : "";
        PeakRow.Description = hdr is { } c2 ? $"Monitor reports {Nits(c2.PeakNits)} nits." : "";
        UpdateTonemapLabels();
        RenderPreview();
    }

    private void LoadOutput(SnipSettings s)
    {
        CopyToClipboard.IsOn = s.CopyToClipboard; AutoSave.IsOn = s.AutoSave; _saveFolder = s.SaveFolder;
        Format.SelectedIndex = Math.Max(0, Array.IndexOf(SnipSettings.Formats, s.Format)); JpegQuality.Value = s.JpegQuality; _loadedJpegQuality = JpegQuality.Value;
        ShowToast.IsOn = s.ShowToast;
        NotificationStyle.SelectedIndex = Math.Max(0, Array.IndexOf(SnipSettings.Notifications, s.Notification));
        HdrFile.SelectedIndex = Math.Max(0, Array.IndexOf(SnipSettings.HdrFiles, s.Hdr.File)); JxrLossless.IsOn = s.Hdr.JxrLossless;
        UpdateOutputLabels();
    }

    private void LoadAbout()
    {
        AboutExpander.Description = $"{typeof(App).Assembly.GetName().Version?.ToString(3)} · Windows App SDK 2.4";
    }

    /// <summary>
    /// The settings as shown in this window: control values for realised pages, stored values for the rest, so
    /// unvisited pages never write their XAML defaults.
    /// </summary>
    private SnipSettings Collect()
    {
        SnipSettings s = App.Current.Settings;
        if (_realised[GeneralPage])
            s = s with
            {
                Theme = SnipSettings.Themes[Math.Max(0, ThemeCombo.SelectedIndex)],
                TrayIcon = SnipSettings.TrayIcons[Math.Max(0, TrayIconCombo.SelectedIndex)],
                StartWithWindows = StartWithWindows.IsOn,
                ShowNitsReadout = NitsReadout.IsOn,
                DefaultDelay = SnipSettings.Delays[Math.Max(0, DefaultDelay.SelectedIndex)],
                AfterSelect = SnipSettings.AfterSelects[Math.Max(0, AfterSelect.SelectedIndex)],
                Annotate = s.Annotate with { PrivacyMode = PrivacyMode.IsOn, ClipToLasso = ClipToLasso.IsOn },
                DeleteToRecycleBin = DeleteToRecycleBin.IsOn,
                RecentFlyoutLayout = LayoutValue(LayoutGrid.IsChecked == true ? LayoutGrid : LayoutRow),
                SelectionFrame = FrameRadios().FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? s.SelectionFrame,
            };
        if (_realised[HotkeysPage])
            s = s with
            {
                Hotkeys = new SnipHotkeys
                {
                    Region = HkRegion.Chord, Window = HkWindow.Chord, FullScreenAll = HkFullAll.Chord,
                    ActiveWindow = HkActive.Chord, History = HkHistory.Chord,
                    ReplaceSnippingTool = ReplaceSnippingTool.IsOn,
                },
            };
        if (_realised[TonemapPage])
            s = s with
            {
                Tonemap = TagOf(Tonemap, s.Tonemap),
                Exposure = Exposure.Value == _loadedExposure ? s.Exposure : (float)Math.Pow(2, Exposure.Value),
                Knee = Knee.Value == _loadedKnee ? s.Knee : (float)Knee.Value,
                AutoExposure = AutoExposure.IsOn,
                SdrWhiteNits = Override(SdrWhite), PeakNits = Override(Peak),
            };
        if (_realised[OutputPage])
            s = s with
            {
                CopyToClipboard = CopyToClipboard.IsOn, AutoSave = AutoSave.IsOn,
                SaveFolder = string.IsNullOrWhiteSpace(_saveFolder) ? null : _saveFolder.Trim(),
                Format = TagOf(Format, s.Format), JpegQuality = JpegQuality.Value == _loadedJpegQuality ? s.JpegQuality : (int)JpegQuality.Value,
                ShowToast = ShowToast.IsOn, Notification = TagOf(NotificationStyle, s.Notification),
                Hdr = s.Hdr with { File = TagOf(HdrFile, s.Hdr.File), JxrLossless = JxrLossless.IsOn },
            };
        return s;
    }

    /// <summary>A nits override: blank (NaN) means "use whatever the monitor reports".</summary>
    private static float? Override(NumberBox box) => double.IsNaN(box.Value) || box.Value <= 0 ? null : (float)box.Value;

    /// <summary>The selected item's Tag, or <paramref name="fallback"/> when nothing is selected (a stored value not in
    /// the list).</summary>
    private static string TagOf(ComboBox box, string fallback)
        => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    /// <summary>The setting value a Recent flyout layout radio stands for; its Content carries the label.</summary>
    private static string LayoutValue(RadioButton radio) => radio.Tag as string ?? SnipSettings.FlyoutLayouts[0];

    private void OnComboChanged(object sender, SelectionChangedEventArgs e) => OnChanged();
    private void OnToggleChanged(object sender, RoutedEventArgs e) => OnChanged();
    private void OnSliderChanged(object sender, RangeBaseValueChangedEventArgs e) => OnChanged();
    private void OnNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs e) => OnChanged();
    /// <summary>A card in either picker (Recent flyout layout, selection frame) was chosen.</summary>
    private void OnPickerChecked(object sender, RoutedEventArgs e) => OnChanged();

    private void OnChanged()
    {
        if (_loading) return;
        UpdateLabels();
        SnipSettings s = Collect();
        ShowConflicts(s);
        App.Current.ApplySettings(s);
        RenderPreview();
    }

    /// <summary>No-op until the Hotkeys page is realised; LoadHotkeys calls it again then.</summary>
    private void ShowConflicts(SnipSettings s)
    {
        if (!_realised[HotkeysPage]) return;
        var conflicts = HotkeyConflicts.Find(s.Bindings());
        Conflicts.Text = conflicts.Count == 0 ? "" : string.Join("\n", conflicts.Select(c => $"{c.A.Chord} is bound to both {c.A.Action} and {c.B.Action}; only the first will fire."));
    }

    private void UpdateLabels()
    {
        if (_realised[TonemapPage]) UpdateTonemapLabels();
        if (_realised[OutputPage]) UpdateOutputLabels();
    }

    private void UpdateTonemapLabels()
    {
        // Numbers the user reads use CurrentCulture; numbers that are data (file names, Tags) use InvariantCulture.
        ExposureText.Text = Exposure.Value.ToString("+0.00;-0.00;+0.00", CultureInfo.CurrentCulture) + " EV";
        KneeText.Text = Knee.Value.ToString("0.00", CultureInfo.CurrentCulture);

        // The collapsed Advanced header lists active overrides so they are not hidden.
        var overrides = new List<string>(2);
        if (Override(SdrWhite) is { } sdr) overrides.Add($"Override SDR white {Nits(sdr)} nits");
        if (Override(Peak) is { } peak) overrides.Add($"Override peak {Nits(peak)} nits");
        AdvancedExpander.Description = overrides.Count == 0
            ? "Leave blank to use the monitor's reported values." : string.Join(" · ", overrides);
    }

    private void UpdateOutputLabels()
    {
        JpegQualityText.Text = ((int)JpegQuality.Value).ToString(CultureInfo.CurrentCulture);
        // JPEG quality is a nested row of Format, so the combo moves between a plain card and an expander. Same
        // timing as the HDR row below.
        bool jpeg = TagOf(Format, App.Current.Settings.Format) == "jpeg";
        if (_loading) PlaceFormat(jpeg);
        else if (_formatNested != jpeg)
            DispatcherQueue.TryEnqueue(() => { if (!_closed) PlaceFormat(TagOf(Format, App.Current.Settings.Format) == "jpeg"); });
        SaveFolderText.Text = string.IsNullOrWhiteSpace(_saveFolder)
            ? Path.Combine(AppPaths.Pictures, SnipSettings.DefaultSaveFolderName) : _saveFolder.Trim();
        // The row, not its expander: the switch that controls it is in the expander's header.
        SaveFolderRow.IsEnabled = AutoSave.IsOn;

        string hdrFile = TagOf(HdrFile, App.Current.Settings.Hdr.File);
        HdrCard.Description = HdrExpander.Description = hdrFile switch
        {
            "jxr" => "Opens in Windows Photos and the HDR + WCG viewer with highlights intact. Written next to the SDR file as .jxr.",
            "png" => "16-bit PNG with cICP metadata (PQ, BT.2020): HDR in Chrome and Edge, flat in older viewers. Written as .hdr.png.",
            "jpeg" => "A normal JPEG plus an embedded gain map: SDR everywhere, HDR in Chrome, Android and Apple Photos. Written as .hdr.jpg.",
            _ => "Saved next to the SDR file with the same name.",
        };
        // Disabling the whole card lets the Toolkit's own disabled state dim everything in it.
        HdrCard.IsEnabled = HdrExpander.IsEnabled = AutoSave.IsOn;
        // Immediately while loading; otherwise on the next dispatcher turn, since this runs inside the combo's own
        // SelectionChanged and must not re-parent it mid-event. The value is re-read in the callback so the latest
        // change wins.
        if (_loading) PlaceHdrFile(hdrFile == "jxr");
        else if (_hdrNested != (hdrFile == "jxr")) DispatcherQueue.TryEnqueue(() => { if (!_closed) PlaceHdrFile(TagOf(HdrFile, App.Current.Settings.Hdr.File) == "jxr"); });
        NotificationStyleRow.IsEnabled = ShowToast.IsOn;
    }

    /// <summary>
    /// Moves a row's control into the plain <paramref name="card"/>, or into the <paramref name="expander"/> when its
    /// value has nested options, and shows only that container. The single control is moved rather than duplicated,
    /// and keyboard focus is restored after the move.
    /// </summary>
    /// <param name="placed">The caller's record of where the control is now; this updates it.</param>
    private void PlaceInCardOrExpander(ref bool placed, bool nested, SettingsCard card, SettingsExpander expander, Control control)
    {
        if (nested == placed) return;
        if (!nested) expander.IsExpanded = false;
        FocusState focus = control.FocusState;
        // ClearValue rather than = null: both Toolkit Content properties are declared non-nullable.
        if (nested) { card.ClearValue(ContentControl.ContentProperty); expander.Content = control; }
        else { expander.ClearValue(SettingsExpander.ContentProperty); card.Content = control; }
        card.Visibility = nested ? Visibility.Collapsed : Visibility.Visible;
        expander.Visibility = nested ? Visibility.Visible : Visibility.Collapsed;
        placed = nested;
        // The move drops focus, and Focus is refused until the control is laid out again, which a dispatcher callback
        // can precede; so refocus once, on the next Loaded.
        if (focus == FocusState.Unfocused) return;
        void OnReloaded(object sender, RoutedEventArgs e)
        {
            control.Loaded -= OnReloaded;
            if (!_closed) control.Focus(focus);
        }
        control.Loaded += OnReloaded;
    }

    /// <summary>The HdrFile combo sits in the expander only for JPEG XR, the one HDR format with a nested option
    /// ("Lossless"); every other format leaves it in the plain card.</summary>
    private void PlaceHdrFile(bool nested) => PlaceInCardOrExpander(ref _hdrNested, nested, HdrCard, HdrExpander, HdrFile);

    /// <summary>The Format combo sits in the expander only for JPEG, whose nested option is the quality row; PNG
    /// leaves it in the plain card.</summary>
    private void PlaceFormat(bool nested) => PlaceInCardOrExpander(ref _formatNested, nested, FormatCard, FormatExpander, Format);

    /// <summary>
    /// The user can disable autostart in Windows Settings, so the switch shows what Windows reports rather than the
    /// stored setting. Queried on the thread pool (StartupTask is async) and reconciled on the UI thread if they differ.
    /// </summary>
    private void ReconcileAutostart()
    {
        // Captured on the UI thread: Window is thread-affine.
        DispatcherQueue ui = DispatcherQueue;
        _ = Task.Run(() =>
        {
            bool actual = Autostart.IsEnabled();
            ui.TryEnqueue(() =>
            {
                if (_closed || !_realised[GeneralPage] || StartWithWindows.IsOn == actual) return;
                App.Current.Log.Debug($"settings: Windows reports start with Windows = {actual}; the stored value said {!actual}");
                bool wasLoading = _loading;
                _loading = true;                       // reconciling is not the user changing the setting
                StartWithWindows.IsOn = actual;
                _loading = wasLoading;
                App.Current.ApplySettings(Collect());  // bring settings.json in line with Windows
            });
        });
    }

#if TONESNIP_HARNESS
    /// <summary>Screenshot harness: selects a page by nav index and optionally opens an expander. <paramref name="hdr"/>
    /// and <paramref name="jpeg"/> also pick JPEG XR or JPEG in memory, since those expanders exist only for that
    /// format.</summary>
    /// <param name="location">0: Save automatically collapsed; 1: expanded; 2: expanded with the switch off in memory,
    /// restored on the next call. Nothing is written to settings.json in harness mode.</param>
    internal void ShowPage(int idx, bool advanced = false, bool hdr = false, int location = 0, bool jpeg = false)
    {
        Nav.SelectedItem = idx < Nav.MenuItems.Count ? Nav.MenuItems[idx] : Nav.FooterMenuItems[idx - Nav.MenuItems.Count];
        // Also called directly: SelectionChanged may not be delivered before the screenshot.
        SelectPage(idx);
        if (_realised[TonemapPage]) AdvancedExpander.IsExpanded = advanced;
        if (_realised[OutputPage])
        {
            HdrExpander.IsExpanded = hdr; if (hdr) PickHdrFile("jxr");
            FormatExpander.IsExpanded = jpeg; if (jpeg) PickFormat("jpeg");
        }
        if (_realised[OutputPage])
        {
            SaveExpander.IsExpanded = location > 0;
            if (location == 2 && AutoSave.IsOn) { _autoSaveWasOn = true; AutoSave.IsOn = false; }
            else if (location != 2 && _autoSaveWasOn) { _autoSaveWasOn = false; AutoSave.IsOn = true; }
        }
        // StartBringIntoView needs an arranged tree.
        (Content as FrameworkElement)?.UpdateLayout();
        // These expanders are below the fold at the default window size.
        if (advanced && _realised[TonemapPage]) AdvancedExpander.StartBringIntoView();
        else if (hdr && _realised[OutputPage]) HdrExpander.StartBringIntoView();
        else if (jpeg && _realised[OutputPage]) FormatExpander.StartBringIntoView();
    }

    /// <summary>Screenshot harness: picks an HDR copy format in memory (<see cref="App.ApplySettings"/> writes nothing
    /// in screenshot mode).</summary>
    internal void PickHdrFile(string tag)
    {
        if (!_realised[OutputPage]) return;
        HdrFile.SelectedItem = HdrFile.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == tag) ?? HdrFile.SelectedItem;
        // The selection change queued the container move; do it now so the screenshot sees it.
        PlaceHdrFile(tag == "jxr");
        (Content as FrameworkElement)?.UpdateLayout();
        if (_hdrNested && HdrExpander.IsExpanded) HdrExpander.StartBringIntoView();
    }

    /// <summary>Screenshot harness: picks an output format in memory, like <see cref="PickHdrFile"/>.</summary>
    internal void PickFormat(string tag)
    {
        if (!_realised[OutputPage]) return;
        Format.SelectedItem = Format.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == tag) ?? Format.SelectedItem;
        PlaceFormat(tag == "jpeg");
        (Content as FrameworkElement)?.UpdateLayout();
        if (_formatNested && FormatExpander.IsExpanded) FormatExpander.StartBringIntoView();
    }
#endif

    private void SelectPage(int idx)
    {
        Realise(idx);
        for (int i = 0; i < PageTitles.Length; i++)
            if (Page(i) is { } page) page.Visibility = i == idx ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = PageTitles[idx];
    }

    /// <summary>About is a footer item, so the page number comes from each item's Tag, not from its position.</summary>
    private void OnNav(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag }
            && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)
            && idx >= 0 && idx < PageTitles.Length)
            SelectPage(idx);
    }

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");   // required by the picker even though only a folder is chosen
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        StorageFolder? folder;
        // The picker throws when elevated, and returns an empty Path for virtual folders (libraries, phones); both are
        // logged and ignored.
        try { folder = await picker.PickSingleFolderAsync(); }
        catch (Exception ex) { App.Current.Log.Warn("save folder picker: " + ex.Message); return; }
        if (folder == null) return;
        if (string.IsNullOrWhiteSpace(folder.Path)) { App.Current.Log.Warn($"save folder picker: '{folder.Name}' has no file-system path"); return; }
        _saveFolder = folder.Path;
        OnChanged();   // saves and redraws the Location row
    }

    private void OnOpenLog(object sender, RoutedEventArgs e) => StartShell(AppPaths.LogPath, "open log");

    /// <summary>ShellExecute on an app-owned target; failures (no file association, blocked browser) are logged.</summary>
    private static void StartShell(string target, string what)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { App.Current.Log.Warn($"{what}: {ex.Message}"); }
    }
    // Shell.OpenFolder is the app's single way of opening Explorer; the log above uses ShellExecute to open in the
    // user's .log handler.
    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e) => Shell.OpenFolder(AppPaths.Dir, App.Current.Log);
    private void OnGitHub(object sender, RoutedEventArgs e) => StartShell("https://github.com/merson316/tonesnip", "open GitHub");

    /// <summary>The preview's maximum size in DIPs.</summary>
    private const double PreviewWidth = 860, PreviewMaxHeight = 360;

    /// <summary>Tonemaps the quarter-size copy of the last frozen HDR frame off the UI thread, then copies it into a
    /// reused <see cref="WriteableBitmap"/> (the preview is opaque, so premultiplication does not matter).</summary>
    private void RenderPreview()
    {
        if (!_realised[TonemapPage]) return;   // the preview card does not exist until the page has been opened
        if (App.Current.PreviewFrame is not var (small, info))
        {
            PreviewBox.Visibility = Visibility.Collapsed;
            PreviewHint.Text = "Take a snip on an HDR monitor to see a preview here.";
            return;
        }
        PreviewBox.Height = small.Width > 0 ? Math.Min(PreviewMaxHeight, PreviewWidth * small.Height / (double)small.Width) : PreviewMaxHeight;
        PreviewBox.Visibility = Visibility.Visible;
        PreviewHint.Text = "Live preview of the last frozen HDR frame. Take a snip on an HDR monitor to update it.";
        if (_previewRunning) { _previewAgain = true; return; }
        _previewRunning = true;
        int version = ++_previewVersion;
        DispatcherQueue ui = _ui;
        _ = Task.Run(() =>
        {
            BgraImage? rgba = null;
            try { rgba = App.Current.Grabber.Tonemap(small, info); }
            catch (Exception ex) { App.Current.Log.Warn("settings preview: " + ex.Message); }
            ui.TryEnqueue(() =>
            {
                _previewRunning = false;
                if (_closed) return;
                if (rgba != null && version == _previewVersion)
                {
                    if (_previewBitmap == null || _previewBitmap.PixelWidth != rgba.Width || _previewBitmap.PixelHeight != rgba.Height)
                        _previewBitmap = new WriteableBitmap(rgba.Width, rgba.Height);
                    using (Stream stream = _previewBitmap.PixelBuffer.AsStream()) stream.Write(rgba.Data, 0, rgba.Data.Length);
                    _previewBitmap.Invalidate();
                    Preview.Source = _previewBitmap;
                }
                if (_previewAgain) { _previewAgain = false; RenderPreview(); }
            });
        });
    }
}
