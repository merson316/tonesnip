using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace ToneSnip.Core.Config;

public static class JsonFile
{
    /// <summary>The options every JSON file is written with. The app reads and writes through generated contexts
    /// (<see cref="SnipSettingsJson"/>, <see cref="Output.HistoryJson"/>) declared with these same values; the tests
    /// check each against this reflection-based reference.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Null value with null error means the file does not exist.</summary>
    public static (T? Value, string? Error) Load<T>(string path, JsonTypeInfo<T> type) where T : class
    {
        if (!File.Exists(path)) return (null, null);
        try { return (JsonSerializer.Deserialize(File.ReadAllText(path), type), null); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        { return (null, $"cannot read {path}: {e.Message}; using defaults"); }
    }

    /// <summary>
    /// Writes to a uniquely named temporary file, flushes it to disk, and moves it over <paramref name="path"/>. Saves
    /// of one file are serialised; the flush keeps a power loss after the move from leaving an empty file.
    /// </summary>
    public static void Save<T>(string path, T value, JsonTypeInfo<T> type) => Save(path, stream => JsonSerializer.Serialize(stream, value, type));

    private static void Save(string path, Action<Stream> write)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        lock (SaveLocks.GetOrAdd(Path.GetFullPath(path), _ => new object()))
        {
            string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    write(stream);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                throw;
            }
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> SaveLocks = new(StringComparer.OrdinalIgnoreCase);
}
