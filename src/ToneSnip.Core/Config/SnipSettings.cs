using ToneSnip.Core.Hotkeys;
using ToneSnip.Core.Tonemap;

namespace ToneSnip.Core.Config;

public sealed record SnipHotkeys
{
    public string Region { get; init; } = "PrintScreen";
    public string Window { get; init; } = "Ctrl+PrintScreen";
    public string FullScreenAll { get; init; } = "Shift+PrintScreen";
    public string ActiveWindow { get; init; } = "Alt+PrintScreen";
    /// <summary>Opens the recent-snips flyout.</summary>
    public string History { get; init; } = "Ctrl+Shift+PrintScreen";
    public bool ReplaceSnippingTool { get; init; }
}

public sealed record AnnotateSettings
{
    /// <summary>"accent" or "#RRGGBB".</summary>
    public string Colour { get; init; } = "accent";
    public int Width { get; init; } = 4;
    public int TextSize { get; init; } = 20;
    /// <summary>Blur and Pixelate hide content with a generated pattern instead of the real pixels.</summary>
    public bool PrivacyMode { get; init; } = true;
    /// <summary>Freeform snips: cut annotations outside the lasso (true) or let them extend over the transparent area (false).</summary>
    public bool ClipToLasso { get; init; } = true;

    public ToneSnip.Core.Annotate.Style ToStyle(uint accentArgb)
    {
        uint c = accentArgb;
        if (Colour is { Length: 7 } && Colour[0] == '#' && uint.TryParse(Colour.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out uint rgb)) c = 0xFF000000 | rgb;
        return new ToneSnip.Core.Annotate.Style(c, Width, TextSize);
    }

    public static AnnotateSettings FromStyle(ToneSnip.Core.Annotate.Style s, uint accentArgb) => new()
    {
        Colour = s.Color == accentArgb || s.Color == ToneSnip.Core.Annotate.Style.AccentPlaceholder ? "accent" : $"#{s.Color & 0xFFFFFF:X6}",
        Width = s.Width, TextSize = s.TextSize,
    };
}

public sealed record HdrSettings
{
    /// <summary>none | jxr | png | jpeg: the HDR copy written next to the SDR file.</summary>
    public string File { get; init; } = "none";
    public bool JxrLossless { get; init; } = true;
    public int JxrQuality { get; init; } = 90;
    /// <summary>Tonemap HDR frames on the graphics card and keep them there while the overlay is up, reading back only
    /// what is asked for. Off falls back to the CPU path, which copies each frame into memory. No UI: a kill switch,
    /// overridden by TONESNIP_GPU_TONEMAP=0 or 1.</summary>
    public bool GpuTonemap { get; init; } = true;
}

/// <summary>Persistent settings of the snipping tool (%LOCALAPPDATA%\tonesnip\settings.json).</summary>
public sealed record SnipSettings
{
    public static readonly string[] Formats = { "png", "jpeg" };
    public static readonly string[] Themes = { "auto", "light", "dark" };
    public static readonly int[] Delays = { 0, 3, 5, 10 };
    public static readonly string[] HdrFiles = { "none", "jxr", "png", "jpeg" };
    public static readonly string[] FlyoutLayouts = { "row", "grid" };
    /// <summary>The overlay's selection frame, in the order Settings shows them; see <see cref="Capture.FrameStyle"/>.</summary>
    public static readonly string[] SelectionFrames = { "normal", "viewfinder", "guides" };
    /// <summary>How the notification-area icon is drawn: the full-colour app icon, a monochrome glyph that follows the
    /// taskbar's own light/dark setting, or the glyph in the Windows accent colour.</summary>
    public static readonly string[] TrayIcons = { "colour", "mono", "accent" };
    /// <summary>Which notification a finished snip shows: ToneSnip's own card above the tray, or a Windows app
    /// notification (which also lands in the notification centre). <see cref="ShowToast"/> gates both.</summary>
    public static readonly string[] Notifications = { "tonesnip", "windows" };

    public int Version { get; init; } = 2;
    public SnipHotkeys Hotkeys { get; init; } = new();
    public string Tonemap { get; init; } = "desktop";
    public float Exposure { get; init; } = 1f;
    public float Knee { get; init; } = 1f;
    public float? SdrWhiteNits { get; init; }
    public float? PeakNits { get; init; }
    public bool AutoExposure { get; init; }
    public bool CopyToClipboard { get; init; } = true;
    public bool AutoSave { get; init; } = true;
    /// <summary>null = Pictures\Screenshots.</summary>
    public string? SaveFolder { get; init; }
    public string Format { get; init; } = "png";
    public int JpegQuality { get; init; } = 90;
    public bool ShowToast { get; init; } = true;
    /// <summary>tonesnip | windows. The card is the default: it is the app's own window and needs no AUMID registration.</summary>
    public string Notification { get; init; } = "tonesnip";
    public static readonly string[] AfterSelects = { "save", "annotate", "annotateFirst", "edit" };
    /// <summary>What happens after a selection: save | annotate | annotateFirst | edit (open the editor after saving).</summary>
    public string AfterSelect { get; init; } = "save";
    public AnnotateSettings Annotate { get; init; } = new();
    public int DefaultDelay { get; init; }
    public string Theme { get; init; } = "auto";
    /// <summary>How the recent-snips flyout lists its snips: row | grid. A change applies the next time it opens.</summary>
    public string RecentFlyoutLayout { get; init; } = "row";
    /// <summary>normal | viewfinder | guides. Normal is the default: the outline 1.0.1 had. A change applies to the next snip.</summary>
    public string SelectionFrame { get; init; } = "normal";
    /// <summary>colour | mono | accent. Monochrome is the default: the Windows 11 convention for a tray glyph.</summary>
    public string TrayIcon { get; init; } = "mono";
    public bool ShowNitsReadout { get; init; } = true;
    /// <summary>hex | rgb: how the colour picker (C in the overlay, the editor's picker) writes the colour it copies.</summary>
    public string ColorFormat { get; init; } = "hex";
    /// <summary>Include the mouse pointer in what is captured: an instant snip's image, and the frozen screen a
    /// selection snip starts from (the pointer as it was when the snip began, not the overlay's own crosshair). Off by
    /// default, as in Snipping Tool. Monitors that fall back to GDI never show it.</summary>
    public bool CaptureCursor { get; init; }
    public bool StartWithWindows { get; init; }
    /// <summary>Whether a snip removed from the Recent list goes to the Recycle Bin (with its HDR copy) rather than
    /// being deleted outright; on by default because a hover delete button is easy to mis-click. The cached thumbnail
    /// under %LOCALAPPDATA% is always deleted.</summary>
    public bool DeleteToRecycleBin { get; init; } = true;
    public HdrSettings Hdr { get; init; } = new();

    /// <summary>
    /// The folder under Pictures a snip is saved to when none is set. The harness build uses its own folder so its
    /// snips never mix with (or get recycled from) the release build's Screenshots folder.
    /// </summary>
#if TONESNIP_HARNESS
    public const string DefaultSaveFolderName = "Screenshots (ToneSnip debug)";
#else
    public const string DefaultSaveFolderName = "Screenshots";
#endif

    public string ResolvedSaveFolder(string picturesFolder)
        => string.IsNullOrWhiteSpace(SaveFolder) ? Path.Combine(picturesFolder, DefaultSaveFolderName) : SaveFolder;

    /// <summary><see cref="ResolvedSaveFolder"/>, or null when that is not a safe absolute path (for example an empty
    /// Pictures folder, which GetFolderPath returns as "" when it does not exist).</summary>
    public string? SafeSaveFolder(string picturesFolder)
    {
        if (string.IsNullOrWhiteSpace(SaveFolder) && string.IsNullOrWhiteSpace(picturesFolder)) return null;
        string folder = ResolvedSaveFolder(picturesFolder);
        return PathGuard.IsSafeAbsolute(folder) ? folder : null;
    }

    public TonemapParams ToTonemapParams(float monitorSdrWhiteNits, float monitorPeakNits) => new()
    {
        SdrWhiteNits = SdrWhiteNits ?? monitorSdrWhiteNits,
        PeakNits = PeakNits ?? monitorPeakNits,
        Exposure = Exposure,
        Knee = Knee,
    };

    /// <summary>Hook bindings; empty or unparsable chords are left out.</summary>
    public List<HotkeyBinding> Bindings()
    {
        var list = new List<HotkeyBinding>(6);
        Add(Hotkeys.Region, "region"); Add(Hotkeys.Window, "window"); Add(Hotkeys.FullScreenAll, "fullScreenAll"); Add(Hotkeys.ActiveWindow, "activeWindow"); Add(Hotkeys.History, "history");
        if (Hotkeys.ReplaceSnippingTool) list.Add(new HotkeyBinding(Chord.Parse("Win+Shift+S"), "region"));
        return list;
        void Add(string text, string action) { if (Chord.TryParse(text, out Chord c)) list.Add(new HotkeyBinding(c, action)); }
    }

    /// <summary>
    /// System.Text.Json writes a <c>null</c> from a hand-edited settings.json straight into a non-nullable property,
    /// so each reference-typed field is reset to its default before any other check dereferences it.
    /// </summary>
    private static SnipSettings WithoutNulls(SnipSettings s, List<string> fixes)
    {
        var d = new SnipSettings();
        T Or<T>(T? value, T fallback, string name) where T : class
        {
            if (value != null) return value;
            fixes.Add($"{name} null -> default");
            return fallback;
        }
        return s with
        {
            Hotkeys = Or(s.Hotkeys, d.Hotkeys, "hotkeys"),
            Tonemap = Or(s.Tonemap, d.Tonemap, "tonemap"),
            Format = Or(s.Format, d.Format, "format"),
            Notification = Or(s.Notification, d.Notification, "notification"),
            AfterSelect = Or(s.AfterSelect, d.AfterSelect, "afterSelect"),
            Annotate = Or(s.Annotate, d.Annotate, "annotate"),
            Theme = Or(s.Theme, d.Theme, "theme"),
            RecentFlyoutLayout = Or(s.RecentFlyoutLayout, d.RecentFlyoutLayout, "recentFlyoutLayout"),
            SelectionFrame = Or(s.SelectionFrame, d.SelectionFrame, "selectionFrame"),
            TrayIcon = Or(s.TrayIcon, d.TrayIcon, "trayIcon"),
            ColorFormat = Or(s.ColorFormat, d.ColorFormat, "colorFormat"),
            Hdr = Or(s.Hdr, d.Hdr, "hdr"),
        };
    }

    public SnipSettings Sanitized(out List<string> fixes)
    {
        fixes = new List<string>();
        List<string> fixList = fixes;
        SnipSettings s = WithoutNulls(this, fixList);
        string FixChord(string text, string name)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            if (Chord.TryParse(text, out Chord c)) return c.ToString();
            fixList.Add($"hotkeys.{name} '{text}' -> unbound"); return "";
        }
        s = s with { Hotkeys = s.Hotkeys with
        {
            Region = FixChord(s.Hotkeys.Region, "region"), Window = FixChord(s.Hotkeys.Window, "window"),
            FullScreenAll = FixChord(s.Hotkeys.FullScreenAll, "fullScreenAll"), ActiveWindow = FixChord(s.Hotkeys.ActiveWindow, "activeWindow"),
            History = FixChord(s.Hotkeys.History, "history"),
        } };
        if (!TonemapperFactory.Names.Contains(s.Tonemap.ToLowerInvariant())) { fixes.Add($"tonemap '{s.Tonemap}' -> desktop"); s = s with { Tonemap = "desktop" }; }
        else s = s with { Tonemap = s.Tonemap.ToLowerInvariant() };
        if (!(s.Exposure >= 0.01f && s.Exposure <= 16f)) { fixes.Add($"exposure {s.Exposure} -> clamped"); s = s with { Exposure = Math.Clamp(float.IsNaN(s.Exposure) ? 1f : s.Exposure, 0.01f, 16f) }; }
        if (!(s.Knee > 0f && s.Knee <= 1f)) { fixes.Add($"knee {s.Knee} -> clamped"); s = s with { Knee = Math.Clamp(float.IsNaN(s.Knee) ? 1f : s.Knee, 0.01f, 1f) }; }
        if (s.SdrWhiteNits is <= 0f) { fixes.Add("sdrWhiteNits <= 0 -> auto"); s = s with { SdrWhiteNits = null }; }
        if (s.PeakNits is <= 0f) { fixes.Add("peakNits <= 0 -> auto"); s = s with { PeakNits = null }; }
        if (!Formats.Contains(s.Format.ToLowerInvariant())) { fixes.Add($"format '{s.Format}' -> png"); s = s with { Format = "png" }; }
        else s = s with { Format = s.Format.ToLowerInvariant() };
        if (s.JpegQuality is < 1 or > 100) { fixes.Add($"jpegQuality {s.JpegQuality} -> clamped"); s = s with { JpegQuality = Math.Clamp(s.JpegQuality, 1, 100) }; }
        // SaveFolder is passed on as a raw path (explorer.exe, file saves), so anything that is not a safe absolute
        // path falls back to the default. Blank means "not set" and normalises without a note.
        if (string.IsNullOrWhiteSpace(s.SaveFolder)) s = s with { SaveFolder = null };
        else if (!PathGuard.IsSafeAbsolute(s.SaveFolder.Trim())) { fixes.Add($"saveFolder '{s.SaveFolder}' -> default"); s = s with { SaveFolder = null }; }
        else s = s with { SaveFolder = s.SaveFolder.Trim() };
        if (!Delays.Contains(s.DefaultDelay)) { int d = Delays.MinBy(x => Math.Abs(x - s.DefaultDelay)); fixes.Add($"defaultDelay {s.DefaultDelay} -> {d}"); s = s with { DefaultDelay = d }; }
        if (!Themes.Contains(s.Theme.ToLowerInvariant())) { fixes.Add($"theme '{s.Theme}' -> auto"); s = s with { Theme = "auto" }; }
        else s = s with { Theme = s.Theme.ToLowerInvariant() };
        if (!FlyoutLayouts.Contains(s.RecentFlyoutLayout.ToLowerInvariant())) { fixes.Add($"recentFlyoutLayout '{s.RecentFlyoutLayout}' -> row"); s = s with { RecentFlyoutLayout = "row" }; }
        else s = s with { RecentFlyoutLayout = s.RecentFlyoutLayout.ToLowerInvariant() };
        if (!SelectionFrames.Contains(s.SelectionFrame.ToLowerInvariant())) { fixes.Add($"selectionFrame '{s.SelectionFrame}' -> normal"); s = s with { SelectionFrame = "normal" }; }
        else s = s with { SelectionFrame = s.SelectionFrame.ToLowerInvariant() };
        if (!TrayIcons.Contains(s.TrayIcon.ToLowerInvariant())) { fixes.Add($"trayIcon '{s.TrayIcon}' -> mono"); s = s with { TrayIcon = "mono" }; }
        else s = s with { TrayIcon = s.TrayIcon.ToLowerInvariant() };
        if (!Notifications.Contains(s.Notification.ToLowerInvariant())) { fixes.Add($"notification '{s.Notification}' -> tonesnip"); s = s with { Notification = "tonesnip" }; }
        else s = s with { Notification = s.Notification.ToLowerInvariant() };
        if (!Extract.ColorText.Formats.Contains(s.ColorFormat.ToLowerInvariant())) { fixes.Add($"colorFormat '{s.ColorFormat}' -> hex"); s = s with { ColorFormat = "hex" }; }
        else s = s with { ColorFormat = s.ColorFormat.ToLowerInvariant() };
        string? after = AfterSelects.FirstOrDefault(a => string.Equals(a, s.AfterSelect, StringComparison.OrdinalIgnoreCase));
        if (after == null) { fixes.Add($"afterSelect '{s.AfterSelect}' -> save"); s = s with { AfterSelect = "save" }; } else s = s with { AfterSelect = after };
        AnnotateSettings an = s.Annotate ?? new AnnotateSettings();
        if (!ToneSnip.Core.Annotate.Style.Widths.Contains(an.Width)) an = an with { Width = 4 };
        if (!ToneSnip.Core.Annotate.Style.TextSizes.Contains(an.TextSize)) an = an with { TextSize = 20 };
        string? col = an.Colour;
        bool isHex = col is { Length: 7 } && col[0] == '#' && uint.TryParse(col.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out _);
        bool okColour = col == "accent" || isHex;
        if (!okColour) an = an with { Colour = "accent" };
        else if (isHex && uint.TryParse(col!.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out uint colRgb))
        {
            // A swatch from the legacy palette maps to the current swatch at the same index; custom colours are left alone.
            int legacyIndex = Array.FindIndex(ToneSnip.Core.Annotate.Style.LegacyPalette, c => (c & 0xFFFFFFu) == colRgb);
            if (legacyIndex >= 0)
            {
                string newCol = $"#{ToneSnip.Core.Annotate.Style.Palette[legacyIndex] & 0xFFFFFFu:X6}";
                fixes.Add($"annotate.colour {col} -> {newCol} (palette updated)");
                an = an with { Colour = newCol };
            }
        }
        s = s with { Annotate = an };
        HdrSettings hd = s.Hdr ?? new HdrSettings();
        string? hf = HdrFiles.FirstOrDefault(x => string.Equals(x, hd.File, StringComparison.OrdinalIgnoreCase));
        if (hf == null) { fixes.Add($"hdr.file '{hd.File}' -> none"); hd = hd with { File = "none" }; } else hd = hd with { File = hf };
        if (hd.JxrQuality is < 1 or > 100) hd = hd with { JxrQuality = Math.Clamp(hd.JxrQuality, 1, 100) };
        s = s with { Hdr = hd };
        if (s.Version < 2) s = s with { Version = 2 };
        return s;
    }
}
