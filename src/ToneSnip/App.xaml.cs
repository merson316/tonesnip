using System.Diagnostics;
using System.Runtime.InteropServices;
using ToneSnip.App.Capture;
using ToneSnip.App.Interop;
using ToneSnip.App.Output;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Config;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Hotkeys;
using ToneSnip.Windows.Hotkeys;
using ToneSnip.Windows.Imaging;
using ToneSnip.Windows.Interop;
using ToneSnip.Windows.Tray;
using Win32 = ToneSnip.Windows.Overlay.Win32;
using Shell = ToneSnip.Windows.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using SettingsWindow = ToneSnip.App.Settings.SettingsWindow;

namespace ToneSnip.App;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;
    public StartupCommand? Command { get; set; }
    public ILog Log { get; } = new FileLog(AppPaths.LogPath);
    public SnipSettings Settings { get; private set; } = new();
    public event Action? SettingsChanged;
    public FrameGrabber Grabber { get; private set; } = null!;
    public OutputPipeline Output { get; private set; } = null!;
    public SnipSession Session { get; private set; } = null!;
    public KeyboardHook Hook { get; private set; } = null!;
    public ToastService Toasts { get; private set; } = null!;
    public SnipHistory History { get; private set; } = null!;
    public CaptureResult? LastResult { get; private set; }
    /// <summary>The mode the last snip was started with, from any source; set only by <see cref="StartSnip"/>. The
    /// tray flyout preselects it.</summary>
    public SnipMode LastMode { get; private set; } = SnipMode.Rectangle;
    /// <summary>Reduced copy of the last frozen HDR frame, for the settings preview, packed while idle
    /// (<see cref="PackPreviewFrame"/>). Full frames are not retained.</summary>
    /// <remarks>A class rather than a tuple: written on a pool thread and read on the UI thread, a reference swaps
    /// atomically where a two-field struct could be read torn.</remarks>
    public Settings.PreviewSnapshot? PreviewFrame { get; private set; }
    public List<OutputInfo> KnownOutputs { get; private set; } = new();
    public IntRect PrimaryMonitor { get; set; } = new(0, 0, 1920, 1080);

    /// <summary>The save folder's last known image count and size, shown by the history flyout until its own scan
    /// completes.</summary>
    public (int Count, long Bytes)? FolderTotals { get; set; }

    /// <summary>The UI thread's dispatcher queue.</summary>
    public DispatcherQueue Ui { get; } = DispatcherQueue.GetForCurrentThread();
    public void RunOnUi(Action a) => Ui.TryEnqueue(() => a());

    private TrayIcon? _tray;
    private Tray.HistoryFlyout? _flyout;
    private SettingsWindow? _settingsWindow;
    /// <summary>Open editors. A WinUI Window with no reference is collected even while on screen.</summary>
    private readonly List<Viewer.ViewerWindow> _viewers = new();
    private IDisposable? _hostPipe;
    private MessageWindow? _messages;
    private DispatcherQueueTimer? _snipDelay;
    private (SnipMode Mode, int Delay)? _pendingSnip;
    private bool _paused;
    private readonly MemoryReclaimer _memory;
    public bool Paused { get => _paused; set { _paused = value; Hook.Paused = value; _tray?.SetTooltip(value ? "ToneSnip (hotkeys paused)" : "ToneSnip"); Log.Info(value ? "hotkeys paused" : "hotkeys resumed"); } }

    public App()
    {
        InitializeComponent();
        // A tray app has no main window: otherwise the process exits when the last XAML window closes. Quit() calls
        // Exit() explicitly.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        _memory = new MemoryReclaimer(Log, () => Grabber, () => Session, () => Hook, () =>
        {
            // An open Settings window is showing the preview and would only decode the frame again at its next render;
            // its close packs it instead.
            if (Volatile.Read(ref _settingsWindow) == null) PackPreviewFrame();
        });
    }

    /// <summary>
    /// Startup, fenced: a failure is logged and the process exits, releasing the single-instance mutex, rather than
    /// lingering with no tray icon or host pipe.
    /// </summary>
    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        try { Launch(); }
        catch (Exception ex)
        {
            try { Log.Error("startup failed: " + ex); } catch { }
            try { _hostPipe?.Dispose(); _tray?.Dispose(); _messages?.Dispose(); Hook?.Dispose(); Grabber?.Dispose(); Toasts?.Dispose(); } catch { }
            Exit();
        }
    }

    private void Launch()
    {
        Directory.CreateDirectory(AppPaths.Dir);
        (Settings, string? err) = SnipSettingsFile.Load(AppPaths.SettingsPath);
        if (err != null) Log.Warn("settings: " + err);
        Log.Info($"ToneSnip {typeof(App).Assembly.GetName().Version} started, pid {Environment.ProcessId}");
        // Logged so a report from Remote Desktop, a VM or with transparency off explains its look.
        if (!Theme.Backdrop.UseMica) Log.Debug("backdrop: Mica unavailable; the settings and editor windows paint a solid root");
        Theme.ThemeManager.Apply(Settings.Theme);
        Theme.ThemeManager.Start();
        UnhandledException += (_, ex) => { Log.Error("unhandled: " + ex.Exception); ex.Handled = true; };

#if TONESNIP_HARNESS
        if (StartSideMode()) return;   // App.SideModes.cs
#endif

        DebugHooks.AttachMemoryProbe(Log);
        Grabber = new FrameGrabber(() => Settings, Log);
        Grabber.Grabbed += o =>
        {
            KnownOutputs = o.Select(x => x.Info).ToList();
            CapturedOutput? hdr = o.FirstOrDefault(x => x.ReadableHdr != null);
            if (hdr?.ReadableHdr is { } frame)
            {
                // The Settings preview is 860 DIP wide, so aim for about 1290 physical pixels (150 % scale).
                int step = Math.Max(1, (int)Math.Round(frame.Width / 1290.0));
                try { PreviewFrame = new Settings.PreviewSnapshot(frame.Downsample(step), hdr.Info); }
                catch (Core.Hdr.HdrFrameLostException) { }   // logged where it was found; the last preview stays
                // Anything else (the device busy past its budget, out of memory) costs only the preview, not the snip.
                catch (Exception e) { Log.Warn("settings preview: the HDR frame was not downsampled: " + e.Message); }
            }
        };
#if TONESNIP_HARNESS
        if (StartMemTest()) return;   // the memory harness needs the grabber and its Grabbed handler, but nothing below
#endif
        Output = new OutputPipeline(() => Settings, Log);
        Output.KeepAlive = s => s.AfterSelect == "edit";
        Output.Accent = () => Theme.ThemeManager.AccentArgb;
        CaptureResult.Encode = Bitmaps.EncodePng;
        CaptureResult.ClipToLassoSetting = () => Settings.Annotate.ClipToLasso;
        CaptureResult.Decode = Bitmaps.Decode;
        CaptureResult.Log = Log;
        Toasts = new ToastService(Log);
        Toasts.Opened += r => OpenViewer(r);
        Toasts.Edited += r => OpenViewer(r, annotate: true);
        Toasts.Pinned += r => _ = PinAsync(r);
        Toasts.Lookup = FindSnipAsync;
        History = new SnipHistory(Log, () => Settings.ResolvedSaveFolder(AppPaths.Pictures), () => Settings.DeleteToRecycleBin);
        History.SweepOrphanThumbs();
        Output.Completed += r =>
        {
            LastResult = r;
            // One thumbnail per snip, shared by the toast and the history flyout. It is encoded on the thread pool, so
            // the toast waits for the file rather than the UI thread waiting for the encode.
            Task<string> thumb = History.Add(r);
            if (Settings.AfterSelect == "edit") OpenViewer(r, annotate: true);
            else if (Settings.ShowToast) _ = ShowToastWhenThumbnailed(r, thumb);
        };
        Session = new SnipSession(Grabber, Output, () => Settings, Log, () => PrimaryMonitor) { HideOwnWindows = HideOwnWindowsForSnip, Divert = DivertSnip };
        // On Idle rather than Output.Completed, so a cancelled snip also releases its frames.
        Session.Idle += () => { ReclaimMemory("memory after snip"); _memory.ReleaseFramesWhenIdle(); };
        // Escape (cancelCountdown) is a hotkey only while a countdown runs.
        Hook = new KeyboardHook(Log) { Swallow = true, IsActive = b => b.Action != "cancelCountdown" || Session.CountingDown };
        Hook.Pressed += b => DispatchHotkey(b.Action);
        ApplyBindings();
        Hook.Install();
        SettingsChanged += ApplyBindings;

        _messages = new MessageWindow(Log);
        _messages.DisplayChanged += OnDisplayChanged;
        // Logged for context on hotkey problems, and both re-arm the keyboard hook: Windows can silently drop a
        // low-level hook across sleep or the secure desktop.
        _messages.PowerChanged += awake => { Log.Info(awake ? "system resumed from sleep" : "system going to sleep"); if (awake) Hook?.Rearm(); };
        _messages.SessionLockChanged += locked => { Log.Info(locked ? "session locked" : "session unlocked"); if (!locked) Hook?.Rearm(); };
        // Idle display-off leaves the machine awake, so neither of the above fires for it. On the way back the hook is
        // re-armed and the next grab enumerates the displays afresh, since a monitor can come back reconfigured.
        _messages.DisplayPowerChanged += on =>
        {
            Log.Info(on ? "displays on" : "displays off");
            if (on) { Hook?.Rearm(); Grabber.DisplaysChanged(); }
        };
        _messages.SessionEnding += OnSessionEnding;
        // Only an installer's close keeps the restart registration (see RegisterRestart); a shutdown that is called
        // off puts it back.
        _messages.SessionEndQueried += closeApp => { if (!closeApp) UnregisterRestart(); };
        _messages.SessionEndCancelled += RegisterRestart;
        BuildTray();
        Toasts.Fallback = (t, x) => _tray?.Balloon(t, x);
        Session.Failed += m => { Log.Warn("snip failed: " + m); _tray?.Balloon("Snip failed", m); };
        _hostPipe = HostPipe.Serve(c => RunOnUi(() => RunCommand(c)), Log);
        RegisterRestart();
        Autostart.Log = Log;
        // Autostart.Apply itself does nothing in the debug build.
        Autostart.Apply(Settings.StartWithWindows, AppPaths.ExePath);
        bool autostartApplied = Settings.StartWithWindows;
        // SettingsChanged fires on every settings edit, so apply only when this value changes.
        SettingsChanged += () =>
        {
            if (Settings.StartWithWindows == autostartApplied) return;
            autostartApplied = Settings.StartWithWindows;
            Autostart.Apply(autostartApplied, AppPaths.ExePath);
        };

        _ = Task.Run(() =>
        {
            try { PublishOutputs(Grabber.Outputs()); }
            catch (Exception e) { Log.Warn("monitors: " + e.Message); }
        });
        if (Command != null) RunCommand(Command);

        // A toast click can launch the app fresh. The activation is reported as AppNotification only once the COM
        // activator is registered, so registration is forced first on that path; an ordinary start still defers it.
        try
        {
            if (CommandLine.IsToastActivation(Environment.GetCommandLineArgs())) Toasts.EnsureRegistered();
            AppActivationArguments activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation?.Kind == ExtendedActivationKind.AppNotification)
            {
                Toasts.EnsureRegistered();
                Log.Debug("launched from a toast notification");
                // The click's arguments carry the snip's path; without one, open the newest snip.
                if (activation.Data is AppNotificationActivatedEventArgs clicked && clicked.Arguments.ContainsKey("path")) Toasts.Activate(clicked.Arguments);
                else OpenLastSnip();
            }
        }
        catch (Exception ex) { Log.Warn("toast activation: " + ex.Message); }
    }

    /// <summary>Set while a delayed refresh is pending, so a burst of WM_DISPLAYCHANGE enumerates once.</summary>
    private int _displayRefreshPending;

    /// <summary>
    /// The next grab re-enumerates immediately (<see cref="FrameGrabber.DisplaysChanged"/>); the rest of the app gets
    /// the new layout a second later, once Windows has settled.
    /// </summary>
    private void OnDisplayChanged()
    {
        Log.Debug("display change");
        Grabber.DisplaysChanged();
        if (Interlocked.Exchange(ref _displayRefreshPending, 1) == 1) return;
        _ = Task.Run(() =>
        {
            Thread.Sleep(1000);
            Volatile.Write(ref _displayRefreshPending, 0);
            try
            {
                Grabber.RefreshOutputs();
                PublishOutputs(Grabber.Outputs());
            }
            catch (Exception e) { Log.Warn("display change refresh: " + e.Message); }
        });
    }

    /// <summary>
    /// Publishes a fresh output list on the UI thread, where its readers run; <see cref="PrimaryMonitor"/> is a
    /// multi-field struct that must not be written from a pool thread.
    /// </summary>
    private void PublishOutputs(List<OutputInfo> outputs)
    {
        if (outputs.Count == 0) return;   // the enumeration found nothing: keep what we knew
        RunOnUi(() =>
        {
            KnownOutputs = outputs;
            OutputInfo? primary = outputs.FirstOrDefault(x => x.Left == 0 && x.Top == 0) ?? outputs.FirstOrDefault();
            if (primary != null) PrimaryMonitor = primary.Bounds;
        });
    }

    private void RunCommand(StartupCommand c)
    {
        if (c.OpenSettings) ShowSettings();
        if (c.OpenHistory) ToggleFlyout();
        if (c.Mode != null) StartSnip(c.Mode.Value, c.Delay ?? Settings.DefaultDelay);
    }

    /// <summary>Hides the toast card and the Recent flyout before a snip captures the screen (<see cref="SnipSession.HideOwnWindows"/>).</summary>
    private bool HideOwnWindowsForSnip()
    {
        bool hid = false;
        try { hid |= Toasts.HideForSnip(); } catch (Exception e) { Log.Warn("hide card for snip: " + e.Message); }
        if (_flyout is { } flyout)
        {
            try { flyout.AppWindow.Hide(); flyout.Dismiss(); hid = true; }
            catch (Exception e) { Log.Warn("hide flyout for snip: " + e.Message); }
        }
        return hid;
    }

    /// <summary>The single path every snip starts through, so <see cref="LastMode"/> is always recorded.</summary>
    private void StartSnip(SnipMode mode, int delay)
    {
        LastMode = mode;
        _ = Session.Run(mode, delay);
    }

    /// <summary>Snip from a menu or the mode bar, after a short wait for the menu and flyout to close. The timer is a
    /// field because an unreferenced DispatcherQueueTimer can be collected before it ticks.</summary>
    public void Snip(SnipMode mode, int? delay = null)
    {
        _snipDelay ??= Ui.CreateTimer();
        _snipDelay.Stop();
        _snipDelay.Interval = TimeSpan.FromMilliseconds(350);
        _snipDelay.IsRepeating = false;
        _snipDelay.Tick -= OnSnipDelayTick;   // never subscribe twice
        _snipDelay.Tick += OnSnipDelayTick;
        _pendingSnip = (mode, delay ?? Settings.DefaultDelay);
        _snipDelay.Start();
    }

    private void OnSnipDelayTick(DispatcherQueueTimer timer, object args)
    {
        timer.Stop();
        timer.Tick -= OnSnipDelayTick;
        if (_pendingSnip is { } pending) StartSnip(pending.Mode, pending.Delay);
        _pendingSnip = null;
    }

    /// <summary>Logs one timed operation (hotkey to overlay, window open, save) at Debug level. Not for per-frame
    /// use.</summary>
    public void Timing(string label, Stopwatch sw) => Log.Debug($"{label} in {sw.ElapsedMilliseconds} ms");

    /// <summary>
    /// Swaps the Settings preview's frame for its compressed copy, with the idle frame release and when Settings
    /// closes; Settings decodes it again when it next renders the preview. Pool thread: the first pack encodes.
    /// </summary>
    public void PackPreviewFrame()
    {
        if (PreviewFrame is not { } preview) return;
        try
        {
            long packed = preview.Pack();
            Log.Debug($"settings preview frame packed: {preview.Width}x{preview.Height}, {packed / 1024} KB");
        }
        catch (Exception e) { Log.Warn("settings preview frame not packed: " + e.Message); }
    }

    /// <summary>Reclaims memory a second after a snip ends or an editor closes (<see cref="MemoryReclaimer.Reclaim"/>),
    /// logging where it landed under <paramref name="label"/>.</summary>
    public void ReclaimMemory(string label) => _memory.Reclaim(label);

    /// <summary>Tray "Open last snip": the newest history row, which survives a restart and reflects saved edits,
    /// falling back to the last in-memory result.</summary>
    public async void OpenLastSnip()
    {
        try
        {
            HistoryItem? newest = History.Items.FirstOrDefault();
            // ToResult may decode the saved file, so run it off the UI thread.
            CaptureResult? r = (newest != null ? await Task.Run(() => History.ToResult(newest)) : null) ?? LastResult;
            if (r != null) OpenViewer(r);
        }
        catch (Exception e) { Log.Warn("open last snip: " + e.Message); }
    }

    /// <summary>The snip a notification names by its saved path, decoded off the UI thread. Null when no row has it.</summary>
    private async Task<CaptureResult?> FindSnipAsync(string path)
    {
        HistoryItem? item = History.Items.FirstOrDefault(i => string.Equals(i.Entry.Path, path, StringComparison.OrdinalIgnoreCase));
        return item == null ? null : await Task.Run(() => History.ToResult(item));
    }

    /// <summary>The tray's "Open Screenshots folder" and the flyout's folder button. The path is validated before it is
    /// created, since a hand-edited SaveFolder could name anything.</summary>
    public void OpenSaveFolder()
    {
        string? f = Settings.SafeSaveFolder(AppPaths.Pictures);
        if (f == null) { Log.Warn("open folder refused: " + Core.Config.PathGuard.Describe(Settings.ResolvedSaveFolder(AppPaths.Pictures))); return; }
        try { Directory.CreateDirectory(f); }
        catch (Exception ex) { Log.Warn($"open folder '{f}': {ex.Message}"); return; }
        Shell.OpenFolder(f, Log);
    }

    /// <summary>The last toast waiting for its thumbnail, so toasts show in snip order even if a later snip's
    /// thumbnail is written first.</summary>
    private Task _toastInLine = Task.CompletedTask;

    /// <summary>Shows the snip's toast once its thumbnail file is written. The write logs its own failures and still
    /// completes, so the toast always shows, without the picture if there is none.</summary>
    private Task ShowToastWhenThumbnailed(CaptureResult r, Task<string> thumb)
    {
        Task before = _toastInLine;
        return _toastInLine = ShowAfter();

        async Task ShowAfter()
        {
            try
            {
                await before;
                Toasts.Show(r, await thumb);
            }
            catch (Exception e) { Log.Warn("toast: " + e.Message); }
        }
    }

    /// <summary>Opens an editor on one snip. Several may be open; each reference is dropped when its window
    /// closes.</summary>
    public void OpenViewer(CaptureResult r, bool annotate = false)
    {
        // One editor per snip, so two cannot save over each other.
        if (_viewers.FirstOrDefault(v => v.Shows(r)) is { } open)
        {
            // Activate shows a minimized window but leaves it minimized.
            if (open.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } p) p.Restore();
            open.Activate();
            BringToFront(WinRT.Interop.WindowNative.GetWindowHandle(open), "editor");
            return;
        }
        var sw = Stopwatch.StartNew();
        var win = new Viewer.ViewerWindow(r, annotate);
        _viewers.Add(win);
        void OnActivated(object s, WindowActivatedEventArgs e)
        {
            win.Activated -= OnActivated;
            Timing("editor opened", sw);
        }
        win.Activated += OnActivated;
        // Detached on close too, in case the window never activated: a handler on Window.Activated that references
        // the window keeps it alive (WindowLifetime).
        win.WhenClosed(() => { win.Activated -= OnActivated; _viewers.Remove(win); });
        win.Activate();
        BringToFront(WinRT.Interop.WindowNative.GetWindowHandle(win), "editor");
    }

    /// <summary>How long after a window is brought forward a foreground taken from it without any input is taken
    /// back.</summary>
    private static readonly TimeSpan ReclaimForegroundAfter = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Puts a window this process just opened or re-activated on top with the keyboard. <see cref="Window.Activate"/>
    /// alone is refused by the foreground lock whenever this process does not own the foreground, and it does not
    /// when the editor is opened from a Windows notification: the click lands in the shell, the activation reaches
    /// this process on a pool thread and is queued to the UI thread, and by the time the window exists the
    /// foreground right the activation carried has lapsed, so the editor opened behind the active app.
    /// <see cref="Win32.ForceForeground"/> takes the foreground regardless. The notification's own flyout can then
    /// hand the foreground back to the app it came from as it closes, so once, a moment later, a foreground lost
    /// with no keyboard or mouse input since is taken back; a user who clicked elsewhere meanwhile keeps their choice.
    /// </summary>
    private void BringToFront(IntPtr hwnd, string what)
    {
        uint inputAt = Win32.LastInputTick();
        if (User32.GetForegroundWindow() == hwnd) Log.Debug($"{what}: Activate took the foreground");
        else Log.Debug($"{what}: Activate was refused the foreground; forcing it");
        Win32.ForceForeground(hwnd, Log);
        if (User32.GetForegroundWindow() != hwnd) Log.Info($"{what}: not in the foreground after taking it");
        DispatcherQueueTimer timer = Ui.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = ReclaimForegroundAfter;
        // Holds only the handle, not the window, so a window closed meanwhile is not kept alive; the handler detaches
        // itself, since a stopped timer still holds its Tick.
        void Tick(DispatcherQueueTimer t, object _)
        {
            t.Stop();
            t.Tick -= Tick;
            if (!User32.IsWindow(hwnd) || User32.GetForegroundWindow() == hwnd || Win32.LastInputTick() != inputAt) return;
            Log.Info($"{what}: the foreground went elsewhere with no input; taking it back");
            Win32.ForceForeground(hwnd, Log);
        }
        timer.Tick += Tick;
        timer.Start();
    }

    /// <summary>Opens the settings window, or brings the open one to the front.</summary>
    public void ShowSettings()
    {
        if (_settingsWindow is { } open) { open.Activate(); return; }
        var sw = Stopwatch.StartNew();
        var win = new SettingsWindow();
        _settingsWindow = win;
        void OnActivated(object s, WindowActivatedEventArgs e)
        {
            win.Activated -= OnActivated;
            Timing("settings opened", sw);
        }
        win.Activated += OnActivated;
        win.WhenClosed(() => { win.Activated -= OnActivated; if (_settingsWindow == win) _settingsWindow = null; });
        win.Activate();
    }

    /// <summary>Opens the Recent snips flyout, or closes the open one.</summary>
    public void ToggleFlyout()
    {
        // The flyout occupies the toast card's corner and lists the same snip, so the card is dismissed.
        Toasts?.Dismiss();
        if (_flyout != null) { _flyout.Dismiss(); return; }
        var sw = Stopwatch.StartNew();
        var flyout = new Tray.HistoryFlyout();
        _flyout = flyout;
        bool logged = false;   // Activated fires twice on this window before the removal below is seen
        void OnActivated(object s, WindowActivatedEventArgs e)
        {
            flyout.Activated -= OnActivated;
            if (logged) return;
            logged = true;
            Timing("flyout opened", sw);
        }
        flyout.Activated += OnActivated;
        flyout.WhenClosed(() => { flyout.Activated -= OnActivated; if (_flyout == flyout) _flyout = null; });
        flyout.Open();
    }

    public void ApplyBindings()
    {
        List<HotkeyBinding> bindings = Settings.Bindings();
        bindings.Add(new HotkeyBinding(Chord.Parse("Escape"), "cancelCountdown"));
        Hook.SetBindings(bindings);
    }

    /// <summary>How long a hotkey may wait for the UI thread before the log says so.</summary>
    private static readonly TimeSpan HotkeyPickupBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Hands a hotkey from the hook thread to the UI thread and logs its pick-up (a Warn when it was refused or slower
    /// than <see cref="HotkeyPickupBudget"/>), so a swallowed hotkey that never acts leaves a trace.
    /// </summary>
    private void DispatchHotkey(string action)
    {
        long pressed = Stopwatch.GetTimestamp();
        int picked = 0;
        bool queued = Ui.TryEnqueue(() =>
        {
            Volatile.Write(ref picked, 1);
            TimeSpan waited = Stopwatch.GetElapsedTime(pressed);
            // Info for every accepted hotkey, logged here rather than on the hook thread, whose callback must not wait
            // on the disk: the first line of a snip's trail in the production log.
            if (waited > HotkeyPickupBudget) Log.Warn($"hotkey {action}: the UI thread picked it up {waited.TotalMilliseconds:F0} ms after the press");
            else Log.Info($"hotkey {action}, picked up in {waited.TotalMilliseconds:F0} ms");
            OnHotkey(action);
        });
        if (!queued) { Log.Warn($"hotkey {action}: the UI thread's queue refused it"); return; }
        _ = Task.Delay(HotkeyPickupBudget).ContinueWith(_ =>
        {
            if (Volatile.Read(ref picked) == 0) Log.Warn($"hotkey {action}: still waiting for the UI thread {HotkeyPickupBudget.TotalSeconds:F0} s after the press");
        }, TaskScheduler.Default);
    }

    private void OnHotkey(string action)
    {
        switch (action)
        {
            case "region": StartSnip(SnipMode.Rectangle, Settings.DefaultDelay); break;
            case "window": StartSnip(SnipMode.Window, Settings.DefaultDelay); break;
            case "fullScreenAll": StartSnip(SnipMode.FullScreenAll, Settings.DefaultDelay); break;
            case "activeWindow": StartSnip(SnipMode.ActiveWindow, Settings.DefaultDelay); break;
            case "history": ToggleFlyout(); break;
            case "cancelCountdown": Session.CancelCountdown(); break;
        }
    }

    private const int RestartNoCrash = 1, RestartNoHang = 2, RestartNoReboot = 8;
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegisterApplicationRestart(string commandLine, int flags);
    [LibraryImport("kernel32.dll")]
    private static partial int UnregisterApplicationRestart();

    /// <summary>
    /// Asks Windows to start the app again when an installer's Restart Manager closes it for an update, with
    /// <see cref="Autostart.BackgroundSwitch"/> so the restart comes back to the tray instead of opening Settings. Not
    /// after a crash or a hang, and not after a reboot, where "Start with Windows" alone decides.
    /// </summary>
    private void RegisterRestart()
    {
        int hr = RegisterApplicationRestart(Autostart.BackgroundSwitch, RestartNoCrash | RestartNoHang | RestartNoReboot);
        if (hr != 0) Log.Warn($"restart after update not registered: 0x{hr:X8}");
    }

    /// <summary>
    /// Withdraws <see cref="RegisterRestart"/> when the whole session is ending. RESTART_NO_REBOOT covers an update
    /// reboot, but not necessarily Windows' "restart apps after signing in", which relaunches registered apps at the
    /// next sign-in and would start the app even with "Start with Windows" off.
    /// </summary>
    private void UnregisterRestart()
    {
        int hr = UnregisterApplicationRestart();
        if (hr != 0) Log.Warn($"restart after update not withdrawn: 0x{hr:X8}");
    }

    /// <summary>
    /// Windows is signing out, shutting down or closing the app for an update (<paramref name="closeApp"/>). Pending
    /// settings are written and the hook and tray icon are released before the handler returns, since the process can
    /// be terminated straight after. The rest of <see cref="Quit"/> is posted rather than run here, because it destroys
    /// the message window whose window procedure this is; a Restart Manager close expects the app to exit on its own.
    /// Only that close keeps the restart registration.
    /// </summary>
    private void OnSessionEnding(bool closeApp)
    {
        Log.Info(closeApp ? "the app is being closed for an update" : "the Windows session is ending");
        if (!closeApp) UnregisterRestart();
        FlushSettings(wait: true);
        _hostPipe?.Dispose(); _tray?.Dispose(); Hook?.Dispose();
        _hostPipe = null; _tray = null;
        RunOnUi(Quit);
    }

    /// <summary>Tears everything down and ends the message loop. WinUI has no OnExit, so the tray's Quit calls this.</summary>
    public void Quit()
    {
        Log.Debug("exit");
        FlushSettings(wait: true);
        _hostPipe?.Dispose(); _tray?.Dispose(); _messages?.Dispose();
        Hook?.Dispose(); Grabber?.Dispose(); Toasts?.Dispose();
        Exit();
    }
}
