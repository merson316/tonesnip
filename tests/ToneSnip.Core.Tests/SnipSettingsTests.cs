using ToneSnip.Core.Config;
using ToneSnip.Core.Hotkeys;
using Xunit;

namespace ToneSnip.Core.Tests;

public sealed class SnipSettingsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Temp(string content) => _dir.Write("settings.json", content);

    [Fact]
    public void Defaults_match_spec()
    {
        var s = new SnipSettings();
        Assert.Equal(2, s.Version);
        Assert.Equal("PrintScreen", s.Hotkeys.Region);
        Assert.Equal("Ctrl+PrintScreen", s.Hotkeys.Window);
        Assert.Equal("Shift+PrintScreen", s.Hotkeys.FullScreenAll);
        Assert.Equal("Alt+PrintScreen", s.Hotkeys.ActiveWindow);
        Assert.Equal("Ctrl+Shift+PrintScreen", s.Hotkeys.History);
        Assert.False(s.Hotkeys.ReplaceSnippingTool);
        Assert.True(s.CopyToClipboard); Assert.True(s.AutoSave); Assert.True(s.ShowToast);
        Assert.Equal("png", s.Format); Assert.Equal(90, s.JpegQuality); Assert.Equal("auto", s.Theme);
        Assert.Equal("desktop", s.Tonemap); Assert.False(s.AutoExposure);
        // Monochrome is the Windows 11 convention for a notification-area glyph, so it is what a fresh install gets.
        Assert.Equal("mono", s.TrayIcon);
        // Off, as in Snipping Tool: the pointer is rarely what a snip is for.
        Assert.False(s.CaptureCursor);
        Assert.False(s.PreciseNits);
        // On: permanent deletion has to be chosen.
        Assert.True(s.DeleteToRecycleBin);
        Assert.Equal(20, s.HistoryLimit);
        // The GPU tonemap's kill switch is on; HDR on the clipboard is off.
        Assert.True(s.Hdr.GpuTonemap);
        Assert.False(s.Hdr.Clipboard);
        // The defaults are already clean: sanitising them changes nothing and reports nothing.
        Assert.Equal(s, s.Sanitized(out List<string> fixes));
        Assert.Empty(fixes);
    }

    /// <summary>The names in settings.json are a file format: a setting renamed in code must keep its name in the file,
    /// or the choice is lost on upgrade. Each row is read from a hand-written file under its name, kept through
    /// sanitising, and written back under the same name. (SnipSettingsJsonTests round-trips every setting; this pins
    /// the names, which the generated serializer would otherwise follow from the property.)</summary>
    [Theory]
    [InlineData("preciseNits", "true")]
    [InlineData("captureCursor", "true")]
    [InlineData("deleteToRecycleBin", "false")]
    [InlineData("historyLimit", "200")]
    [InlineData("hdr.clipboard", "true")]
    [InlineData("hdr.gpuTonemap", "false")]
    [InlineData("annotate.textBackground", "true")]
    public void A_setting_is_read_and_written_under_its_json_name(string setting, string value)
    {
        string[] path = setting.Split('.');
        string entry = $"\"{path[^1]}\": {value}";
        string p = Temp(path.Length == 1 ? $"{{ \"version\": 2, {entry} }}" : $"{{ \"version\": 2, \"{path[0]}\": {{ {entry} }} }}");
        (SnipSettings s, string? err) = SnipSettingsFile.Load(p);
        Assert.Null(err);
        object? loaded = Setting(s, path);
        Assert.NotEqual(Setting(new SnipSettings(), path), loaded);   // the file's value, not the default
        SnipSettingsFile.Save(p, s);
        Assert.Contains(entry, File.ReadAllText(p));
        Assert.Equal(loaded, Setting(SnipSettingsFile.Load(p).Settings, path));
    }

    /// <summary>The setting a camelCase JSON path names, read through its properties.</summary>
    private static object? Setting(object settings, string[] path)
    {
        object? value = settings;
        foreach (string name in path) value = value!.GetType().GetProperty(char.ToUpperInvariant(name[0]) + name[1..])!.GetValue(value);
        return value;
    }

    [Fact]
    public void Tray_icon_choice_is_sanitised_like_the_theme()
    {
        Assert.Equal(new[] { "colour", "mono", "accent" }, SnipSettings.TrayIcons);
        Assert.Equal("accent", new SnipSettings { TrayIcon = "ACCENT" }.Sanitized(out _).TrayIcon);
        SnipSettings c = new SnipSettings { TrayIcon = "rainbow" }.Sanitized(out List<string> fixes);
        Assert.Equal("mono", c.TrayIcon);
        Assert.Contains(fixes, f => f.Contains("trayIcon"));
        Assert.Equal("colour", new SnipSettings { TrayIcon = "colour" }.Sanitized(out _).TrayIcon);
    }

    [Fact]
    public void Bindings_include_snipping_tool_key_only_when_enabled()
    {
        var s = new SnipSettings();
        Assert.Equal(5, s.Bindings().Count);
        Assert.Contains(s.Bindings(), b => b.Action == "window" && b.Chord == Chord.Parse("Ctrl+PrintScreen"));
        var t = s with { Hotkeys = s.Hotkeys with { ReplaceSnippingTool = true } };
        Assert.Contains(t.Bindings(), b => b.Action == "region" && b.Chord == Chord.Parse("Win+Shift+S"));
        Assert.Equal(6, t.Bindings().Count);
    }

    [Fact]
    public void Unbound_and_invalid_chords_are_skipped_and_reported()
    {
        var s = new SnipSettings { Hotkeys = new SnipHotkeys { Window = "", ActiveWindow = "Ctrl+Bogus" } };
        SnipSettings clean = s.Sanitized(out List<string> fixes);
        Assert.Equal(3, clean.Bindings().Count);
        Assert.Contains(fixes, f => f.Contains("activeWindow"));
        Assert.Equal("", clean.Hotkeys.ActiveWindow);
    }

    [Fact]
    public void Sanitizes_ranges()
    {
        var s = new SnipSettings { Format = "bmp", JpegQuality = 500, DefaultDelay = 7, Theme = "blue", Exposure = 100f, Knee = 0f };
        SnipSettings c = s.Sanitized(out List<string> fixes);
        Assert.Equal("png", c.Format); Assert.Equal(100, c.JpegQuality); Assert.Equal(5, c.DefaultDelay); Assert.Equal("auto", c.Theme);
        Assert.Equal(16f, c.Exposure); Assert.Equal(0.01f, c.Knee);
        Assert.Equal(6, fixes.Count);
    }

    [Fact]
    public void FlyoutLayout_defaults_to_row_and_is_sanitised()
    {
        Assert.Equal("row", new SnipSettings().RecentFlyoutLayout);
        SnipSettings s = new SnipSettings { RecentFlyoutLayout = "Grid" }.Sanitized(out _);
        Assert.Equal("grid", s.RecentFlyoutLayout);
        SnipSettings bad = new SnipSettings { RecentFlyoutLayout = "tiles" }.Sanitized(out List<string> fixes);
        Assert.Equal("row", bad.RecentFlyoutLayout);
        Assert.Contains(fixes, f => f.Contains("recentFlyoutLayout"));
    }

    [Fact]
    public void SelectionFrame_defaults_to_normal_and_is_sanitised()
    {
        Assert.Equal("normal", new SnipSettings().SelectionFrame);
        Assert.Equal(new[] { "normal", "viewfinder", "guides" }, SnipSettings.SelectionFrames);
        SnipSettings s = new SnipSettings { SelectionFrame = "Viewfinder" }.Sanitized(out _);
        Assert.Equal("viewfinder", s.SelectionFrame);
        SnipSettings bad = new SnipSettings { SelectionFrame = "marching-ants" }.Sanitized(out List<string> fixes);
        Assert.Equal("normal", bad.SelectionFrame);
        Assert.Contains(fixes, f => f.Contains("selectionFrame"));
    }

    [Theory]
    [InlineData("normal", ToneSnip.Core.Capture.FrameStyle.Normal)]
    [InlineData("viewfinder", ToneSnip.Core.Capture.FrameStyle.Viewfinder)]
    [InlineData("GUIDES", ToneSnip.Core.Capture.FrameStyle.Guides)]
    [InlineData("nonsense", ToneSnip.Core.Capture.FrameStyle.Normal)]
    [InlineData(null, ToneSnip.Core.Capture.FrameStyle.Normal)]
    public void The_stored_frame_names_map_to_their_styles(string? name, ToneSnip.Core.Capture.FrameStyle style)
        => Assert.Equal(style, ToneSnip.Core.Capture.FrameStyles.Parse(name));

    [Fact]
    public void Notification_style_defaults_to_the_ToneSnip_card_and_is_sanitised()
    {
        // The card needs no registration; "windows" uses AppNotificationManager and lands in the notification centre.
        Assert.Equal(new[] { "tonesnip", "windows" }, SnipSettings.Notifications);
        Assert.Equal("tonesnip", new SnipSettings().Notification);
        Assert.Equal("windows", new SnipSettings { Notification = "Windows" }.Sanitized(out _).Notification);
        SnipSettings bad = new SnipSettings { Notification = "growl" }.Sanitized(out List<string> fixes);
        Assert.Equal("tonesnip", bad.Notification);
        Assert.Contains(fixes, f => f.Contains("notification"));
        // ShowToast gates both styles.
        Assert.True(new SnipSettings().ShowToast);
    }

    [Fact]
    public void Old_helper_file_without_version_is_treated_as_absent()
    {
        string p = Temp("{ \"tonemap\": \"aces\", \"exposure\": 2 }");
        (SnipSettings s, string? err) = SnipSettingsFile.Load(p);
        Assert.Equal("desktop", s.Tonemap);
        Assert.Contains("no version", err);
    }

    [Fact]
    public void Only_the_newest_copies_of_an_unreadable_file_are_kept()
    {
        string p = Temp("{ not json");
        string[] older = { "20200101-000000", "20210101-000000", "20220101-000000", "20230101-000000" };
        foreach (string stamp in older) File.WriteAllText($"{p}.bad-{stamp}", "old");
        string other = _dir.Write("other.json.bad-20190101-000000", "not ours");

        (_, string? err) = SnipSettingsFile.Load(p);

        Assert.Contains("kept as", err);
        string[] kept = Directory.GetFiles(_dir.Path, "settings.json.bad-*").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToArray()!;
        Assert.Equal(SnipSettingsFile.BadCopiesKept, kept.Length);
        // Today's copy and the two newest old ones survive; another file's copies are not touched.
        Assert.Equal(new[] { "settings.json.bad-20220101-000000", "settings.json.bad-20230101-000000" }, kept.Take(2));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void Round_trips_and_ignores_unknown_fields()
    {
        string p = Temp("{ \"version\": 1, \"tonemap\": \"hable\", \"hotkeys\": { \"region\": \"F9\" }, \"future\": true }");
        (SnipSettings s, string? err) = SnipSettingsFile.Load(p);
        Assert.Null(err);
        Assert.Equal("hable", s.Tonemap);
        Assert.Equal("F9", s.Hotkeys.Region);
        Assert.Equal("Ctrl+PrintScreen", s.Hotkeys.Window);
        SnipSettingsFile.Save(p, s with { JpegQuality = 75 });
        Assert.Equal(75, SnipSettingsFile.Load(p).Settings.JpegQuality);
        Assert.Contains("\"jpegQuality\": 75", File.ReadAllText(p));
    }

    [Fact]
    public void Save_folder_defaults_to_pictures_screenshots()
    {
        Assert.Equal(Path.Combine("P", "Screenshots"), new SnipSettings().ResolvedSaveFolder("P"));
        Assert.Equal("X", new SnipSettings { SaveFolder = "X" }.ResolvedSaveFolder("P"));
    }

    [Fact]
    public void AfterSelect_defaults_to_save_and_bad_values_are_fixed()
    {
        Assert.Equal("save", new SnipSettings().AfterSelect);
        SnipSettings s = new SnipSettings { AfterSelect = "Whatever" }.Sanitized(out List<string> fixes);
        Assert.Equal("save", s.AfterSelect); Assert.Contains(fixes, f => f.Contains("afterSelect"));
        Assert.Equal("annotateFirst", new SnipSettings { AfterSelect = "AnnotateFirst" }.Sanitized(out _).AfterSelect);
    }

    [Fact]
    public void Saving_a_style_keeps_the_settings_a_style_does_not_carry()
    {
        var cur = new AnnotateSettings { PrivacyMode = false, ClipToLasso = false };
        AnnotateSettings saved = cur.WithStyle(new ToneSnip.Core.Annotate.Style(0xFFE53935, 8, 28, TextBox: true), 0xFF123456);
        Assert.False(saved.PrivacyMode); Assert.False(saved.ClipToLasso);
        Assert.Equal("#E53935", saved.Colour); Assert.Equal(8, saved.Width); Assert.Equal(28, saved.TextSize); Assert.True(saved.TextBackground);
    }

    [Fact]
    public void Annotate_defaults_and_style_round_trip()
    {
        var a = new AnnotateSettings();
        Assert.Equal("accent", a.Colour); Assert.Equal(4, a.Width); Assert.Equal(20, a.TextSize); Assert.True(a.PrivacyMode);
        ToneSnip.Core.Annotate.Style st = a.ToStyle(0xFF123456);
        Assert.Equal(0xFF123456u, st.Color);
        AnnotateSettings back = AnnotateSettings.FromStyle(st with { Color = 0xFFE53935, Width = 8 }, 0xFF123456);
        Assert.Equal("#E53935", back.Colour); Assert.Equal(8, back.Width);
        Assert.Equal("accent", AnnotateSettings.FromStyle(st, 0xFF123456).Colour);
        SnipSettings fixedUp = new SnipSettings { Annotate = new AnnotateSettings { Width = 5, TextSize = 99, Colour = "nope" } }.Sanitized(out _);
        Assert.Equal(4, fixedUp.Annotate.Width); Assert.Equal(20, fixedUp.Annotate.TextSize); Assert.Equal("accent", fixedUp.Annotate.Colour);
        // A text size outside 14/20/28 falls back to the default.
        Assert.Equal(20, new SnipSettings { Annotate = new AnnotateSettings { TextSize = 32 } }.Sanitized(out _).Annotate.TextSize);
        Assert.Equal(28, new SnipSettings { Annotate = new AnnotateSettings { TextSize = 28 } }.Sanitized(out _).Annotate.TextSize);
        Assert.Equal(12, new SnipSettings { Annotate = new AnnotateSettings { Width = 12 } }.Sanitized(out _).Annotate.Width);
    }

    [Fact]
    public void Version1_openViewerImmediately_migrates_to_edit()
    {
        string path = Temp("{ \"version\": 1, \"openViewerImmediately\": true }");
        (SnipSettings s, string? err) = SnipSettingsFile.Load(path);
        Assert.Null(err); Assert.Equal("edit", s.AfterSelect); Assert.Equal(2, s.Version);
        File.WriteAllText(path, "{ \"version\": 1, \"openViewerImmediately\": false, \"afterSelect\": \"annotate\" }");
        Assert.Equal("annotate", SnipSettingsFile.Load(path).Settings.AfterSelect);
    }

    [Fact]
    public void Null_annotate_colour_falls_back_to_accent()
    {
        SnipSettings s = new SnipSettings { Annotate = new AnnotateSettings { Colour = null! } }.Sanitized(out _);
        Assert.Equal("accent", s.Annotate.Colour);
        Assert.Equal(0xFF123456u, new AnnotateSettings { Colour = null! }.ToStyle(0xFF123456).Color);
    }

    /// <summary>A colour from the old palette moves to the current colour at the same index, whatever its case, with one
    /// fixes note; a current colour, a custom one and the accent are left alone.</summary>
    [Theory]
    [InlineData("#1E88E5", "#0078D4", true)]   // the legacy blue, index 4
    [InlineData("#1e88e5", "#0078D4", true)]
    [InlineData("#0078D4", "#0078D4", false)]
    [InlineData("#123456", "#123456", false)]
    [InlineData("accent", "accent", false)]
    public void Old_palette_colours_migrate_to_their_new_entry(string stored, string expected, bool migrated)
    {
        SnipSettings s = new SnipSettings { Annotate = new AnnotateSettings { Colour = stored } }.Sanitized(out List<string> fixes);
        Assert.Equal(expected, s.Annotate.Colour);
        if (migrated)
        {
            string fix = Assert.Single(fixes);
            Assert.True(fix.Contains("annotate.colour") && fix.Contains(stored, StringComparison.OrdinalIgnoreCase) && fix.Contains(expected), fix);
        }
        else Assert.DoesNotContain(fixes, f => f.Contains("annotate.colour"));
    }

    [Fact]
    public void String_version_does_not_crash_the_loader()
    {
        string path = Temp("{ \"version\": \"1\", \"openViewerImmediately\": true }");
        (SnipSettings s, string? err) = SnipSettingsFile.Load(path);
        Assert.Equal("save", s.AfterSelect);
    }

    [Fact]
    public void Hdr_defaults_and_sanitising()
    {
        var s = new SnipSettings();
        Assert.Equal("none", s.Hdr.File); Assert.True(s.Hdr.JxrLossless); Assert.Equal(90, s.Hdr.JxrQuality);
        SnipSettings f = new SnipSettings { Hdr = new HdrSettings { File = "JXR", JxrQuality = 500 } }.Sanitized(out List<string> fixes);
        Assert.Equal("jxr", f.Hdr.File); Assert.Equal(100, f.Hdr.JxrQuality);
        Assert.Equal("none", new SnipSettings { Hdr = new HdrSettings { File = "avif" } }.Sanitized(out fixes).Hdr.File);
        Assert.Contains(fixes, x => x.Contains("hdr.file"));
    }

    /// <summary>SaveFolder reaches explorer.exe and the output pipeline as a raw path, so Sanitized validates it.</summary>
    [Fact]
    public void Save_folder_survives_when_it_is_a_well_formed_absolute_path()
    {
        SnipSettings s = new SnipSettings { SaveFolder = @"C:\Users\me\Pictures\Screenshots" }.Sanitized(out List<string> fixes);
        Assert.Equal(@"C:\Users\me\Pictures\Screenshots", s.SaveFolder);
        Assert.DoesNotContain(fixes, x => x.Contains("saveFolder"));
        Assert.Equal(@"\\nas\shots", new SnipSettings { SaveFolder = @"\\nas\shots" }.Sanitized(out _).SaveFolder);
        Assert.Null(new SnipSettings().Sanitized(out List<string> none).SaveFolder);
        Assert.DoesNotContain(none, x => x.Contains("saveFolder"));
    }

    [Theory]
    [InlineData("https://example.com/upload")]
    [InlineData(@"Pictures\Screenshots")]
    [InlineData(@"\\.\PIPE\evil")]
    [InlineData("C:\\Shots\\a\u0001b")]
    [InlineData(@"C:\Shots\*")]
    public void Save_folder_that_is_not_a_safe_absolute_path_falls_back_to_the_default(string folder)
    {
        SnipSettings s = new SnipSettings { SaveFolder = folder }.Sanitized(out List<string> fixes);
        Assert.Null(s.SaveFolder);
        Assert.Contains(fixes, x => x.Contains("saveFolder"));
        Assert.Equal(Path.Combine("P", "Screenshots"), s.ResolvedSaveFolder("P"));
    }

    [Fact]
    public void Blank_save_folder_is_normalised_to_null_without_a_fix_note()
    {
        SnipSettings s = new SnipSettings { SaveFolder = "   " }.Sanitized(out List<string> fixes);
        Assert.Null(s.SaveFolder);
        Assert.DoesNotContain(fixes, x => x.Contains("saveFolder"));
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-5, 20)]
    [InlineData(30, 20)]
    [InlineData(40, 50)]
    [InlineData(100, 100)]
    [InlineData(100000, 200)]
    public void A_history_limit_not_offered_snaps_to_the_nearest_one(int stored, int expected)
    {
        SnipSettings s = new SnipSettings { HistoryLimit = stored }.Sanitized(out List<string> fixes);
        Assert.Equal(expected, s.HistoryLimit);
        Assert.Equal(stored != expected, fixes.Any(f => f.StartsWith("historyLimit", StringComparison.Ordinal)));
    }

}

public class SnipSettingsNullFieldTests
{
    // System.Text.Json writes a null from a hand-edited file straight into a non-nullable property; loading must not throw.
    [Theory]
    [InlineData("hotkeys"), InlineData("tonemap"), InlineData("format"), InlineData("notification"), InlineData("afterSelect"),
     InlineData("annotate"), InlineData("theme"), InlineData("recentFlyoutLayout"), InlineData("trayIcon"), InlineData("hdr"),
     InlineData("selectionFrame")]
    public void A_null_field_loads_as_its_default_and_says_so(string field)
    {
        using var dir = new TempDir();
        string p = dir.Write("settings.json", $"{{\"version\":2,\"{field}\":null}}");
        (SnipSettings s, string? err) = SnipSettingsFile.Load(p);
        var defaults = new SnipSettings();
        Assert.Equal(defaults.Hotkeys, s.Hotkeys);
        Assert.Equal(defaults.Tonemap, s.Tonemap);
        Assert.Equal(defaults.Format, s.Format);
        Assert.Equal(defaults.Notification, s.Notification);
        Assert.Equal(defaults.AfterSelect, s.AfterSelect);
        Assert.Equal(defaults.Annotate, s.Annotate);
        Assert.Equal(defaults.Theme, s.Theme);
        Assert.Equal(defaults.RecentFlyoutLayout, s.RecentFlyoutLayout);
        Assert.Equal(defaults.SelectionFrame, s.SelectionFrame);
        Assert.Equal(defaults.TrayIcon, s.TrayIcon);
        Assert.Equal(defaults.Hdr, s.Hdr);
        Assert.NotNull(err);
        Assert.Contains(field, err);
    }
}

public class SettingsFileBackupTests
{
    [Fact]
    public void An_unreadable_file_is_kept_beside_itself_before_defaults_are_used()
    {
        // Otherwise the next settings save would overwrite the user's file with defaults.
        using var dir = new TempDir();
        string path = dir.Write("settings.json", "{ \"version\": 2, \"hotkeys\": { \"region\": \"F9\" ");   // cut off mid-object
        (SnipSettings s, string? err) = SnipSettingsFile.Load(path);
        Assert.Equal(new SnipSettings().Hotkeys.Region, s.Hotkeys.Region);
        string backup = Assert.Single(Directory.GetFiles(dir.Path, "settings.json.bad-*"));
        Assert.Equal(File.ReadAllText(path), File.ReadAllText(backup));
        Assert.Contains(Path.GetFileName(backup), err);
    }

    [Fact]
    public void A_readable_file_is_not_backed_up()
    {
        using var dir = new TempDir();
        SnipSettingsFile.Load(dir.Write("settings.json", "{ \"version\": 2 }"));
        Assert.Empty(Directory.GetFiles(dir.Path, "settings.json.bad-*"));
    }
}
