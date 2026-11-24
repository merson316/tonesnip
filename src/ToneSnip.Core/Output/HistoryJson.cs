using System.Text.Json.Serialization;

namespace ToneSnip.Core.Output;

/// <summary>
/// history.json's serializer, generated at compile time as <see cref="Config.SnipSettingsJson"/> is: Recent's list is
/// read at startup, before the tray icon, and through reflection that built the metadata and emitted IL first. The
/// options are <see cref="Config.JsonFile.Options"/>'s; HistoryTests checks the two read and write the same JSON.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(List<HistoryEntry>))]
public sealed partial class HistoryJson : JsonSerializerContext { }
