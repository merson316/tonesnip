using System.Collections.Concurrent;
using System.Globalization;

namespace ToneSnip.Core.Diagnostics;

/// <summary>Append-only text log, truncated when it passes 1 MB. Never throws.</summary>
public sealed class FileLog : ILog
{
    /// <summary>
    /// The lowest level every <see cref="FileLog"/> in the process writes. tonesnip-debug.exe lowers it to
    /// <see cref="LogLevel.Debug"/> at startup. Static because logs are constructed in several places and must agree.
    /// </summary>
    public static LogLevel Minimum { get; set; } = LogLevel.Info;

    /// <summary>One lock per file, shared by every instance writing it, so concurrent appends do not fail with a
    /// sharing violation.</summary>
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock;
    public string Path { get; }

    public FileLog(string path)
    {
        Path = path;
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
                if (File.Exists(Path) && new FileInfo(Path).Length > 1_000_000) File.WriteAllText(Path, "");
                // Invariant culture keeps the timestamp format fixed regardless of the user's time separator.
                File.AppendAllText(Path, $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {tag}: {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
