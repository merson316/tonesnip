using System.Text.Json;

namespace ToneSnip.Core.Config;

public static class SnipSettingsFile
{
    /// <summary>Attempts for a read that fails with an IOException, since antivirus scanning at logon can briefly hold
    /// the file open.</summary>
    private const int ReadAttempts = 3;

    public static (SnipSettings Settings, string? Error) Load(string path)
    {
        if (!File.Exists(path)) return (new SnipSettings(), null);
        string text;
        try { text = ReadWithRetry(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return Unreadable(path, $"cannot read {path}: {e.Message}"); }

        bool migrateEdit = false;
        try
        {
            using JsonDocument probe = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!probe.RootElement.TryGetProperty("version", out _))
                return Unreadable(path, $"{path} has no version field (old helper settings?)");
            migrateEdit = probe.RootElement.TryGetProperty("version", out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int ver) && ver < 2
                && probe.RootElement.TryGetProperty("openViewerImmediately", out JsonElement ov) && ov.ValueKind == JsonValueKind.True
                && !probe.RootElement.TryGetProperty("afterSelect", out _);
        }
        catch (JsonException e) { return Unreadable(path, $"cannot read {path}: {e.Message}"); }

        SnipSettings? parsed;
        try { parsed = JsonSerializer.Deserialize<SnipSettings>(text, JsonFile.Options); }
        catch (JsonException e) { return Unreadable(path, $"cannot read {path}: {e.Message}"); }
        if (parsed == null) return Unreadable(path, $"{path} is empty");
        SnipSettings clean = parsed.Sanitized(out List<string> fixes);
        if (migrateEdit) clean = clean with { AfterSelect = "edit" };
        return (clean, fixes.Count == 0 ? null : $"{path}: " + string.Join("; ", fixes));
    }

    private static string ReadWithRetry(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (attempt < ReadAttempts && File.Exists(path)) { Thread.Sleep(250); }
        }
    }

    /// <summary>
    /// Defaults, after copying the file to <c>settings.json.bad-yyyyMMdd-HHmmss</c> so the next settings save does not
    /// destroy a hand-edited file with a typo in it.
    /// </summary>
    private static (SnipSettings, string) Unreadable(string path, string reason)
    {
        string backup = $"{path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            if (!File.Exists(backup)) File.Copy(path, backup);
            return (new SnipSettings(), $"{reason}; using defaults, and the file was kept as {Path.GetFileName(backup)}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (new SnipSettings(), $"{reason}; using defaults, and it could not be kept ({e.Message})");
        }
    }

    public static void Save(string path, SnipSettings settings) => JsonFile.Save(path, settings);
}
