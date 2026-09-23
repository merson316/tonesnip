using System.Collections.Concurrent;
using System.Globalization;

namespace ToneSnip.Core.Diagnostics;

/// <summary>
/// Append-only text log. Past <see cref="MaxBytes"/> the file is rolled to <c>name.1.ext</c>, replacing the previous
/// one, so the lines just before a failure survive the roll. Never throws.
/// </summary>
public sealed class FileLog : ILog
{
    /// <summary>
    /// The lowest level every <see cref="FileLog"/> in the process writes. tonesnip-debug.exe lowers it to
    /// <see cref="LogLevel.Debug"/> at startup. Static because logs are constructed in several places and must agree.
    /// </summary>
    public static LogLevel Minimum { get; set; } = LogLevel.Info;

    /// <summary>
    /// Written as the first line of every new file, so a rolled log still says which build and process wrote it. Set
    /// once at startup (the app version and PID); null writes no header.
    /// </summary>
    public static string? Header { get; set; }

    /// <summary>One lock per file, shared by every instance writing it, so concurrent appends do not fail with a
    /// sharing violation.</summary>
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock;
    public string Path { get; }
    /// <summary>The size past which the next write rolls the file.</summary>
    public long MaxBytes { get; }
    /// <summary>Where the previous file goes: <c>tonesnip.log</c> becomes <c>tonesnip.1.log</c>.</summary>
    public string PreviousPath { get; }

    public FileLog(string path, long maxBytes = 1_000_000)
    {
        Path = path;
        MaxBytes = maxBytes;
        PreviousPath = System.IO.Path.ChangeExtension(path, ".1" + System.IO.Path.GetExtension(path));
        string key;
        try { key = System.IO.Path.GetFullPath(path); } catch { key = path; }
        _lock = Locks.GetOrAdd(key, _ => new object());
        try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); } catch { }
    }

    public void Debug(string message) => Write(LogLevel.Debug, "debug", message);
    public void Info(string message) => Write(LogLevel.Info, "info", message);
    public void Warn(string message) => Write(LogLevel.Warn, "warn", message);
    public void Error(string message) => Write(LogLevel.Error, "error", message);

    private void Write(LogLevel level, string tag, string message)
    {
        if (level < Minimum) return;
        lock (_lock)
        {
            try
            {
                var file = new FileInfo(Path);
                bool fresh = !file.Exists;
                if (file.Exists && file.Length > MaxBytes) fresh = Roll();
                string line = Line(tag, message);
                File.AppendAllText(Path, fresh && Header != null ? Line("info", Header) + line : line);
            }
            catch { }
        }
    }

    /// <summary>
    /// Moves the full file aside. If that fails (another process has the old file open), the log is emptied instead,
    /// as it always was, so it cannot grow without bound. Returns whether a new file starts.
    /// </summary>
    private bool Roll()
    {
        try { File.Move(Path, PreviousPath, overwrite: true); }
        catch { try { File.WriteAllText(Path, ""); } catch { return false; } }
        return true;
    }

    // Invariant culture keeps the timestamp format fixed regardless of the user's time separator.
    private static string Line(string tag, string message)
        => $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {tag}: {message}{Environment.NewLine}";
}
