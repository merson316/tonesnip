using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ToneSnip.Core.Config;
using Xunit;

namespace ToneSnip.Core.Tests;

/// <summary>
/// settings.json is read and written through generated code (<see cref="SnipSettingsJson"/>); these check it against the
/// reflection serializer the app used before, over every setting there is, found by reflection so that a setting added
/// later is covered without editing this file. The first attempt at the generator loaded a file without "knee" as
/// Knee = 0 and one without "showToast" as ShowToast = false: init-only properties lose their defaults there.
/// </summary>
public class SnipSettingsJsonTests
{
    /// <summary>The reflection path: the same options, no generated metadata.</summary>
    private static SnipSettings Reflected(string json) => JsonSerializer.Deserialize<SnipSettings>(json, JsonFile.Options)!;
    private static SnipSettings Generated(string json) => JsonSerializer.Deserialize(json, SnipSettingsJson.Default.SnipSettings)!;

    /// <summary>A settings record: one of the Config records a setting can hold, which the walk descends into.</summary>
    private static bool IsGroup(Type t) => t.IsClass && t != typeof(string) && t.Namespace == typeof(SnipSettings).Namespace;

    /// <summary>Every serialised setting, as the chain of properties that reaches it: public instance properties with a
    /// getter, as System.Text.Json sees them.</summary>
    private static IEnumerable<PropertyInfo[]> Leaves(Type t, PropertyInfo[] prefix)
    {
        foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetMethod == null || p.GetIndexParameters().Length > 0) continue;
            PropertyInfo[] path = [.. prefix, p];
            if (IsGroup(p.PropertyType)) { foreach (PropertyInfo[] leaf in Leaves(p.PropertyType, path)) yield return leaf; }
            else yield return path;
        }
    }

    private static IEnumerable<PropertyInfo> Groups(Type t)
        => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => IsGroup(p.PropertyType)).SelectMany(p => Groups(p.PropertyType).Prepend(p));

    private static string JsonName(PropertyInfo p) => JsonNamingPolicy.CamelCase.ConvertName(p.Name);
    private static string Dotted(PropertyInfo[] path) => string.Join(".", path.Select(JsonName));

    public static TheoryData<string> Settings()
    {
        var data = new TheoryData<string>();
        foreach (PropertyInfo[] path in Leaves(typeof(SnipSettings), [])) data.Add(Dotted(path));
        return data;
    }

    private static PropertyInfo[] PathOf(string dotted) => Leaves(typeof(SnipSettings), []).Single(p => Dotted(p) == dotted);

    /// <summary>A value of the setting's type other than <paramref name="current"/>. A setting of a type not listed here
    /// fails loudly, so a new kind of setting gets a case rather than going unchecked.</summary>
    private static object? Other(Type t, object? current) => t switch
    {
        _ when t == typeof(bool) => !(bool)current!,
        _ when t == typeof(int) => (int)current! + 7,
        _ when t == typeof(long) => (long)current! + 7,
        _ when t == typeof(float) => (float)current! + 0.25f,
        _ when t == typeof(double) => (double)current! + 0.25,
        _ when t == typeof(float?) => current == null ? 150f : null,
        _ when t == typeof(int?) => current == null ? 3 : null,
        _ when t == typeof(string) => current == null ? @"C:\Snips" : current + "-changed",
        _ when t.IsEnum => Enum.GetValues(t).Cast<object>().First(v => !v.Equals(current)),
        _ => throw new NotSupportedException($"no changed value for a setting of type {t}; add one to {nameof(Other)}"),
    };

    /// <summary>Settings with every value changed from its default, set through the properties so that a record's
    /// equality covers them all.</summary>
    private static SnipSettings Changed()
    {
        var s = new SnipSettings();
        foreach (PropertyInfo[] path in Leaves(typeof(SnipSettings), []))
        {
            object owner = s;
            foreach (PropertyInfo step in path[..^1]) owner = step.GetValue(owner)!;
            PropertyInfo leaf = path[^1];
            leaf.SetValue(owner, Other(leaf.PropertyType, leaf.GetValue(owner)));
        }
        return s;
    }

    private static bool IsInitOnly(PropertyInfo p)
        => p.SetMethod?.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)) == true;

    [Fact]
    public void Every_setting_has_a_setter_rather_than_init()
    {
        IEnumerable<string> initOnly = Leaves(typeof(SnipSettings), []).Where(p => p[^1].SetMethod == null || IsInitOnly(p[^1])).Select(Dotted)
            .Concat(Groups(typeof(SnipSettings)).Where(p => p.SetMethod == null || IsInitOnly(p)).Select(JsonName));
        Assert.True(!initOnly.Any(), "make these { get; set; }: the generated serializer resets an init-only setting absent from the file to default, not to its initializer: " + string.Join(", ", initOnly));
    }

    [Fact]
    public void The_settings_walk_finds_every_group_and_setting()
    {
        // A guard on the walk itself, so the tests below cannot pass by finding nothing.
        string[] all = Leaves(typeof(SnipSettings), []).Select(Dotted).ToArray();
        Assert.Contains("knee", all);
        Assert.Contains("showToast", all);
        Assert.Contains("hotkeys.region", all);
        Assert.Contains("annotate.clipToLasso", all);
        Assert.Contains("hdr.gpuTonemap", all);
        Assert.True(all.Length >= 40, $"{all.Length} settings");
    }

    [Fact]
    public void Defaults_and_changed_settings_serialise_to_the_same_text()
    {
        foreach (SnipSettings s in new[] { new SnipSettings(), Changed() })
            Assert.Equal(JsonSerializer.Serialize(s, JsonFile.Options), JsonSerializer.Serialize(s, SnipSettingsJson.Default.SnipSettings));
    }

    [Fact]
    public void Every_changed_setting_round_trips_through_the_file()
    {
        SnipSettings changed = Changed();
        Assert.NotEqual(new SnipSettings(), changed);
        string json = JsonSerializer.Serialize(changed, SnipSettingsJson.Default.SnipSettings);
        Assert.Equal(changed, Generated(json));
        Assert.Equal(Reflected(json), Generated(json));
    }

    /// <summary>The bug that sank the first attempt, one setting at a time: a file written before a setting existed has
    /// every other setting changed and that one missing, and must load it at its default, as reflection does.</summary>
    [Theory]
    [MemberData(nameof(Settings))]
    public void A_file_without_a_setting_loads_its_default(string setting)
    {
        PropertyInfo[] path = PathOf(setting);
        JsonObject root = JsonNode.Parse(JsonSerializer.Serialize(Changed(), SnipSettingsJson.Default.SnipSettings))!.AsObject();
        JsonObject owner = root;
        foreach (PropertyInfo step in path[..^1]) owner = owner[JsonName(step)]!.AsObject();
        Assert.True(owner.Remove(JsonName(path[^1])), $"{setting} is not in the file");
        string json = root.ToJsonString();

        SnipSettings generated = Generated(json), reflected = Reflected(json);
        Assert.Equal(reflected, generated);
        object? value = generated, fallback = new SnipSettings();
        foreach (PropertyInfo step in path) { value = step.GetValue(value); fallback = step.GetValue(fallback); }
        Assert.Equal(fallback, value);
    }

    /// <summary>A whole group missing ("hdr" in a 1.0.2 file) comes back as the group's defaults.</summary>
    [Fact]
    public void A_file_without_a_group_loads_its_defaults()
    {
        foreach (PropertyInfo group in Groups(typeof(SnipSettings)).Where(g => g.DeclaringType == typeof(SnipSettings)))
        {
            JsonObject root = JsonNode.Parse(JsonSerializer.Serialize(Changed(), SnipSettingsJson.Default.SnipSettings))!.AsObject();
            root.Remove(JsonName(group));
            SnipSettings generated = Generated(root.ToJsonString());
            Assert.Equal(Reflected(root.ToJsonString()), generated);
            Assert.Equal(group.GetValue(new SnipSettings()), group.GetValue(generated));
        }
    }

    [Fact]
    public void A_file_with_only_a_version_loads_every_default()
    {
        Assert.Equal(new SnipSettings(), Generated("{ \"version\": 2 }"));
        Assert.Equal(Reflected("{ \"version\": 2 }"), Generated("{ \"version\": 2 }"));
    }

    /// <summary>
    /// A version-1 file as 1.0.0 wrote it, hand-edited the ways people do: comments, a trailing comma, a number in
    /// quotes, names in another case, settings that no longer exist and several that were added since. Both paths read
    /// it alike, and the loader still migrates openViewerImmediately.
    /// </summary>
    [Fact]
    public void An_old_hand_edited_file_loads_as_reflection_loaded_it()
    {
        const string Old = """
            {
              // written by ToneSnip 1.0.0
              "version": 1,
              "Hotkeys": { "region": "PrintScreen", "window": "Ctrl+PrintScreen" },
              "tonemap": "Hable",
              "exposure": "1.5",
              "JPEGQUALITY": 80,
              "openViewerImmediately": true,
              "daemonIdleSeconds": 30,
              "annotate": { "colour": "#FF0000", },
            }
            """;
        Assert.Equal(Reflected(Old), Generated(Old));
        string path = Path.Combine(Path.GetTempPath(), "tonesnip-test-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, Old);
        try
        {
            (SnipSettings loaded, _) = SnipSettingsFile.Load(path);
            SnipSettings expected = Reflected(Old).Sanitized(out _) with { AfterSelect = "edit" };
            Assert.Equal(expected, loaded);
            Assert.Equal((1.5f, 80, "hable", 1f, true), (loaded.Exposure, loaded.JpegQuality, loaded.Tonemap, loaded.Knee, loaded.ShowToast));
        }
        finally { File.Delete(path); }
    }

    /// <summary>What the app writes, the reflection serializer reads back to the same settings: a downgrade to a build
    /// before the generator still loads the file.</summary>
    [Fact]
    public void A_saved_file_reads_back_the_same_either_way()
    {
        string path = Path.Combine(Path.GetTempPath(), "tonesnip-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            SnipSettings changed = Changed();
            SnipSettingsFile.Save(path, changed);
            string text = File.ReadAllText(path);
            Assert.Equal(changed, Reflected(text));
            Assert.Equal(changed, Generated(text));
        }
        finally { File.Delete(path); }
    }
}
