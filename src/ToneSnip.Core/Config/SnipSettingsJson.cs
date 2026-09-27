using System.Text.Json.Serialization;

namespace ToneSnip.Core.Config;

/// <summary>
/// settings.json's serializer, generated at compile time: loading the settings at startup then builds no reflection
/// metadata and emits no IL, which also keeps it working under trimming. Its options are <see cref="JsonFile.Options"/>'s,
/// which the other JSON files still use through reflection.
/// <para>
/// Only <see cref="SnipSettings"/> is named: the generator follows its properties, so the nested settings, and any
/// setting added later, are covered without touching this class.
/// </para>
/// <para>
/// <b>Every setting must be <c>{ get; set; }</c>, not <c>init</c>.</b> The generator fills init-only properties through an
/// object initializer that assigns each one, so a property absent from the file gets <c>default</c> instead of its
/// initializer's value: a file without "knee" loaded as Knee = 0, without "showToast" as ShowToast = false.
/// SnipSettingsJsonTests fails for any property this would affect.
/// </para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(SnipSettings))]
public sealed partial class SnipSettingsJson : JsonSerializerContext { }
