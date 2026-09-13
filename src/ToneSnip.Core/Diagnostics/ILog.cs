namespace ToneSnip.Core.Diagnostics;

/// <summary>
/// Log severity. The release build writes <see cref="Info"/> and above; tonesnip-debug.exe also writes
/// <see cref="Debug"/>.
/// <para>
/// <see cref="Info"/> is what a bug report needs (startup, snip outcomes, files written, environment changes);
/// <see cref="Debug"/> is anything that repeats with the work (per frame, per window, timings).
/// </para>
/// </summary>
public enum LogLevel { Debug, Info, Warn, Error }

public interface ILog
{
    /// <summary>Repetitive diagnostics; written only by tonesnip-debug.exe.</summary>
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}
