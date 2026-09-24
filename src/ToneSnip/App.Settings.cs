using ToneSnip.Core.Config;
using Microsoft.UI.Dispatching;

namespace ToneSnip.App;

/// <summary>Settings changes: applying one, the debounced write of settings.json, and the flush on the way out.</summary>
public partial class App
{
    /// <summary>Saves and applies new settings; listeners (hook, theme, tray, autostart) react through SettingsChanged.</summary>
    public void ApplySettings(SnipSettings settings)
    {
        string previousTheme = Settings.Theme;
        Settings = settings.Sanitized(out _);
        // Called on every settings edit: the save is debounced, and the theme is re-applied only when it changed.
        QueueSettingsSave();
        if (Settings.Theme != previousTheme)
        {
#if TONESNIP_HARNESS
            // The screenshot harness drives the theme itself; applying it here would change colours mid-capture.
            if (!ScreenshotMode) Theme.ThemeManager.Apply(Settings.Theme);
#else
            Theme.ThemeManager.Apply(Settings.Theme);
#endif
        }
        SettingsChanged?.Invoke();
    }

    /// <summary>How long settings.json waits after the last change before it is written.</summary>
    private static readonly TimeSpan SettingsSaveDelay = TimeSpan.FromMilliseconds(400);
    private DispatcherQueueTimer? _settingsSave;

    /// <summary>Writes settings.json once <see cref="SettingsSaveDelay"/> has passed without another change.
    /// <see cref="FlushSettings"/> starts a pending save at once (Settings closing), or finishes it (Quit).</summary>
    private void QueueSettingsSave()
    {
        if (!Ui.HasThreadAccess) { RunOnUi(QueueSettingsSave); return; }
        if (_settingsSave == null)
        {
            _settingsSave = Ui.CreateTimer();
            _settingsSave.Interval = SettingsSaveDelay;
            _settingsSave.IsRepeating = false;
            _settingsSave.Tick += (t, _) => { t.Stop(); SaveSettings(); };
        }
        _settingsSave.Stop();
        _settingsSave.Start();
    }

    /// <summary>
    /// Starts a queued settings save now, if one is waiting. With <paramref name="wait"/> it also blocks until every
    /// save has reached the disk, or <see cref="SettingsFlushBudget"/> has passed: for Quit and the end of the Windows
    /// session, after which the process may be gone. UI thread only.
    /// </summary>
    public void FlushSettings(bool wait = false)
    {
        if (_settingsSave is { IsRunning: true } timer)
        {
            timer.Stop();
            SaveSettings();
        }
        if (!wait) return;
        // A blocking wait on the UI thread, on purpose: the process may end as soon as this returns. It cannot deadlock,
        // as the writes run on the pool and never need this thread, and it is capped at SettingsFlushBudget.
        Task writes;
        lock (_settingsWriteGate) writes = _settingsWrites;
        if (!writes.Wait(SettingsFlushBudget)) Log.Warn($"settings save: still writing after {SettingsFlushBudget.TotalSeconds:F0} s; the last change may be lost");
    }

    /// <summary>How long a waiting <see cref="FlushSettings"/> gives the writes in flight.</summary>
    private static readonly TimeSpan SettingsFlushBudget = TimeSpan.FromSeconds(2);
    /// <summary>The settings writes in flight, chained so they reach the disk in the order they were made.</summary>
    private Task _settingsWrites = Task.CompletedTask;
    private readonly object _settingsWriteGate = new();

    /// <summary>
    /// Writes settings.json on a pool thread, except in a harness side mode. The write flushes to disk, which can take
    /// tens of milliseconds, too long to hold the UI thread (an overlay closing, a hotkey waiting behind it). The
    /// settings are an immutable record, so the pool thread serializes exactly the state it was handed.
    /// </summary>
    private void SaveSettings()
    {
#if TONESNIP_HARNESS
        if (SideMode) return;
#endif
        SnipSettings snapshot = Settings;
        lock (_settingsWriteGate)
        {
            _settingsWrites = _settingsWrites.ContinueWith(_ =>
            {
                try { SnipSettingsFile.Save(AppPaths.SettingsPath, snapshot); } catch (Exception ex) { Log.Warn("settings save: " + ex.Message); }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>Persists a change no listener needs (such as the last-used annotation style), without raising
    /// SettingsChanged. Saved through the same debounce as every other change.</summary>
    public void UpdateSettingsQuiet(Func<SnipSettings, SnipSettings> change)
    {
        Settings = change(Settings).Sanitized(out _);
        QueueSettingsSave();
    }

    /// <summary>
    /// Keeps the annotation colour, width and text size last used for the next snip, quietly, unless they are what
    /// <paramref name="shown"/> already gives. The overlay and the editor call this once, when they close, rather than
    /// on every palette click. Privacy mode is not part of the style and is left as it is.
    /// </summary>
    public void RememberAnnotateStyle(Core.Annotate.Style? style, AnnotateSettings shown, uint accent)
    {
        if (style is not Core.Annotate.Style s || s == shown.ToStyle(accent)) return;
        UpdateSettingsQuiet(cur => cur with { Annotate = AnnotateSettings.FromStyle(s, accent) with { PrivacyMode = cur.Annotate.PrivacyMode } });
    }
}
