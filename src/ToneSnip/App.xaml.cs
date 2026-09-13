using System.Diagnostics;
using System.Runtime;
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
    /// <summary>Reduced copy of the last frozen HDR frame, for the settings preview. Full frames are not retained.</summary>
    /// <remarks>A class rather than a tuple: written on a pool thread and read on the UI thread, a reference swaps
    /// atomically where a two-field struct could be read torn.</remarks>
    public PreviewSnapshot? PreviewFrame { get; private set; }
    public sealed record PreviewSnapshot(Core.Imaging.HalfImage Image, OutputInfo Output);
    public List<OutputInfo> KnownOutputs { get; private set; } = new();
    public IntRect PrimaryMonitor { get; set; } = new(0, 0, 1920, 1080);

    /// <summary>The save folder's last known image count and size, shown by the history flyout until its own scan
    /// completes.</summary>
    public (int Count, long Bytes)? FolderTotals { get; set; }
#if TONESNIP_HARNESS
    /// <summary>True under `--screenshots` and `--hold`: <see cref="ApplySettings"/> updates memory only and skips the
    /// live theme apply, so a held window cannot be observed changing theme.</summary>
    public bool ScreenshotMode { get; private set; }
    /// <summary>True in any harness side mode: nothing writes settings.json.</summary>
    public bool SideMode { get; private set; }
#endif

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
    public bool Paused { get => _paused; set { _paused = value; Hook.Paused = value; _tray?.SetTooltip(value ? "ToneSnip (hotkeys paused)" : "ToneSnip"); Log.Info(value ? "hotkeys paused" : "hotkeys resumed"); } }

    public App()
    {
        InitializeComponent();
        // A tray app has no main window: otherwise the process exits when the last XAML window closes. Quit() calls
        // Exit() explicitly.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
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
        Log.Info($"ToneSnip {typeof(App).Assembly.GetName().Version} started");
        // Logged so a report from Remote Desktop, a VM or with transparency off explains its look.
        if (!Theme.Backdrop.UseMica) Log.Debug("backdrop: Mica unavailable; the settings and editor windows paint a solid root");
        Theme.ThemeManager.Apply(Settings.Theme);
        Theme.ThemeManager.Start();
        UnhandledException += (_, ex) => { Log.Error("unhandled: " + ex.Exception); ex.Handled = true; };

#if TONESNIP_HARNESS
        // Harness side modes start only what they need (no hook, tray, host pipe or autostart), so they never claim
        // anything the running app owns.
        if (Command?.ScreenshotDir is { } screenshotDir)
        {
            ScreenshotMode = SideMode = true;
            History = new SnipHistory(Log, () => Settings.ResolvedSaveFolder(AppPaths.Pictures), () => Settings.DeleteToRecycleBin);   // read-only here
            _ = Screenshots.Run(screenshotDir, Command.ScreenshotTheme);
            return;
        }

        if (Command?.HoldWindow is { } holdWindow)
        {
            ScreenshotMode = SideMode = true;
            History = new SnipHistory(Log, () => Settings.ResolvedSaveFolder(AppPaths.Pictures), () => Settings.DeleteToRecycleBin);
            _ = Screenshots.Hold(holdWindow, Command.HoldSeconds, Command.ScreenshotTheme, Command.HoldFlipTheme);
            return;
        }

        if (Command?.LeakTest == true)
        {
            SideMode = true;
            History = new SnipHistory(Log, () => Settings.ResolvedSaveFolder(AppPaths.Pictures), () => Settings.DeleteToRecycleBin);
            _ = LeakTest.Run();
            return;
        }
#endif

#if TONESNIP_HARNESS
        MemoryProbe.Log = Log;
#endif
        Grabber = new FrameGrabber(() => Settings, Log);
        Grabber.Grabbed += o =>
        {
            KnownOutputs = o.Select(x => x.Info).ToList();
            CapturedOutput? hdr = o.FirstOrDefault(x => x.Half != null);
            if (hdr?.Half != null)
            {
                // The Settings preview is 860 DIP wide, so aim for about 1290 physical pixels (150 % scale).
                int step = Math.Max(1, (int)Math.Round(hdr.Half.Width / 1290.0));
                PreviewFrame = new PreviewSnapshot(hdr.Half.Downsample(step), hdr.Info);
            }
        };
#if TONESNIP_HARNESS
        // The memory harness needs the grabber and its Grabbed handler, but nothing below.
        if (Command?.MemTest == true)
        {
            SideMode = true;
            History = new SnipHistory(Log, () => Settings.ResolvedSaveFolder(AppPaths.Pictures), () => Settings.DeleteToRecycleBin);   // read-only here
            _ = MemTest.Run();
            return;
        }
#endif
        Output = new OutputPipeline(() => Settings, Log);
        Output.KeepAlive = _ => Settings.AfterSelect == "edit";
        Output.Accent = () => Theme.ThemeManager.AccentArgb;
        CaptureResult.Encode = Bitmaps.EncodePng;
        CaptureResult.ClipToLassoSetting = () => Settings.Annotate.ClipToLasso;
        CaptureResult.Decode = Bitmaps.Decode;
        Toasts = new ToastService(Log);
        Toasts.Opened += r => OpenViewer(r);
        Toasts.Edited += r => OpenViewer(r, annotate: true);
        Toasts.Lookup = FindSnipAsync;
        History = new SnipHistory(Log, () => Settings.ResolvedSaveFolder(AppPaths.Pictures), () => Settings.DeleteToRecycleBin);
        History.SweepOrphanThumbs();
        Output.Completed += r =>
        {
            LastResult = r;
            string thumb = History.Add(r);   // one thumbnail per snip, shared by the toast and the history flyout
            if (Settings.AfterSelect == "edit") OpenViewer(r, annotate: true);
            else if (Settings.ShowToast) Toasts.Show(r, thumb);
        };
        Session = new SnipSession(Grabber, Output, () => Settings, Log, () => PrimaryMonitor) { HideOwnWindows = HideOwnWindowsForSnip };
        // On Idle rather than Output.Completed, so a cancelled snip also releases its frames.
        Session.Idle += () => { LogMemory("memory after snip"); ReleaseFramesWhenIdle(); };
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
        BuildTray();
        Toasts.Fallback = (t, x) => _tray?.Balloon(t, x);
        Session.Failed += m => { Log.Warn("snip failed: " + m); _tray?.Balloon("Snip failed", m); };
        _hostPipe = HostPipe.Serve(c => RunOnUi(() => RunCommand(c)), Log);
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

    /// <summary>The tray icon and its menu: plain Win32 in the Windows library, so no XAML loads at startup.</summary>
    private void BuildTray()
    {
        // An empty chord means "unbound"; the Funcs read Settings.Hotkeys live so a rebinding shows on the menu's
        // next open without rebuilding it.
        static Func<string?> Accelerator(Func<string> chord) => () => { string c = chord(); return string.IsNullOrEmpty(c) ? null : c; };
        // The owner-drawn Win32 menu cannot read XAML brushes, so it gets the theme state directly. High contrast
        // overrides dark/light: the menu then paints from GetSysColor.
        var menu = new TrayMenu(_messages!, Log)
        {
            IsDark = () => Theme.ThemeManager.IsDark,
            IsHighContrast = () => Theme.ThemeManager.IsHighContrast,
        };
        menu.AddHeader("New snip");
        foreach ((string label, SnipMode mode, Func<string?>? accelerator) in new (string, SnipMode, Func<string?>?)[]
        {
            ("Rectangle", SnipMode.Rectangle, Accelerator(() => Settings.Hotkeys.Region)),
            ("Window", SnipMode.Window, Accelerator(() => Settings.Hotkeys.Window)),
            ("Full screen", SnipMode.FullScreen, null),
            ("Freeform", SnipMode.Freeform, null),
            ("All monitors", SnipMode.FullScreenAll, Accelerator(() => Settings.Hotkeys.FullScreenAll)),
            ("Active window", SnipMode.ActiveWindow, Accelerator(() => Settings.Hotkeys.ActiveWindow)),
        })
            menu.Add(label, () => Snip(mode), accelerator: accelerator);
        menu.AddSeparator();
        menu.AddHeader("Delay");
        foreach (int delay in SnipSettings.Delays)
            menu.Add(delay == 0 ? "None" : $"{delay} seconds", () => ApplySettings(Settings with { DefaultDelay = delay }), () => Settings.DefaultDelay == delay);
        menu.AddSeparator();
        menu.Add("Recent snips", ToggleFlyout, accelerator: Accelerator(() => Settings.Hotkeys.History));
        menu.Add("Open last snip", OpenLastSnip);
        menu.Add("Open Screenshots folder", OpenSaveFolder);
        menu.AddSeparator();
        menu.Add("Settings…", ShowSettings);
        menu.Add("Pause hotkeys", () => Paused = !Paused, () => Paused);
        menu.AddSeparator();
        menu.Add("Quit ToneSnip", Quit);

        (IntPtr icon, bool ownsIcon) = TrayImage();
        _tray = new TrayIcon(_messages!, icon, "ToneSnip", Log, ownsIcon);
        _tray.LeftClick += ToggleFlyout;
        _tray.RightClick += (x, y) => menu.Show(x, y);
        // The glyph is drawn in one colour, so it is redrawn when the accent, light/dark mode or its settings change.
        Theme.ThemeManager.Changed += () => RunOnUi(RefreshTrayIcon);
        // A high-contrast switch arrives through the message window; it raises ThemeManager.Changed, which redraws.
        // Posted rather than run inline, to get off the window procedure's stack before touching XAML.
        _messages!.ThemeChanged += () => RunOnUi(Theme.ThemeManager.SystemThemeChanged);
        string trayApplied = Settings.TrayIcon, themeApplied = Settings.Theme;
        // SettingsChanged fires on every settings edit, so redraw only when these values change.
        SettingsChanged += () =>
        {
            if (Settings.TrayIcon == trayApplied && Settings.Theme == themeApplied) return;
            trayApplied = Settings.TrayIcon; themeApplied = Settings.Theme;
            RefreshTrayIcon();
        };
        // The constructor already added the icon, so Shown has fired by now; log directly when it has.
        void TrayShown() => Log.Debug($"tray shown in {Program.Started.ElapsedMilliseconds} ms since process start");
        if (_tray.IsShown) TrayShown(); else _tray.Shown += TrayShown;
    }

    /// <summary>
    /// The tray icon for the current setting: the full-colour .ico, or the glyph drawn at tray size in one colour
    /// (monochrome to match the taskbar, the accent, or COLOR_WINDOWTEXT in high contrast).
    /// </summary>
    private (IntPtr Icon, bool Owns) TrayImage()
    {
        if (Settings.TrayIcon is "mono" or "accent")
        {
            uint argb = TrayGlyph.GlyphArgb(Settings.TrayIcon, Settings.Theme, Theme.ThemeManager.SystemAccentArgb, Theme.ThemeManager.IsHighContrast);
            IntPtr glyph = TrayGlyph.CreateIcon(TrayGlyph.TraySize(), argb);
            if (glyph != IntPtr.Zero) return (glyph, true);
            Log.Warn("tray icon: could not draw the glyph; falling back to icon.ico");
        }
        IntPtr icon = TrayIcon.LoadIconFile(IconFile());
        if (icon != IntPtr.Zero) return (icon, true);
        Log.Warn("tray icon: could not load icon.ico; using the system application icon");
        return (TrayIcon.DefaultIcon(), false);
    }

    /// <summary>Redraws the tray icon; TrayIcon destroys the icon it previously owned.</summary>
    private void RefreshTrayIcon()
    {
        if (_tray == null) return;
        (IntPtr icon, bool owns) = TrayImage();
        _tray.SetIcon(icon, owns);
    }

    /// <summary>The embedded .ico, unpacked into the data folder because LoadImage reads icons from disk.</summary>
    internal string IconFile()
    {
        string path = Path.Combine(AppPaths.Dir, "icon.ico");
        try
        {
            using Stream? s = typeof(App).Assembly.GetManifestResourceStream("tonesnip.icon.ico");
            if (s != null && (!File.Exists(path) || new FileInfo(path).Length != s.Length))
            {
                using FileStream f = File.Create(path);
                s.CopyTo(f);
            }
        }
        catch (Exception e) { Log.Warn("tray icon: " + e.Message); }
        return path;
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

    /// <summary>
    /// A blocking, compacting gen 2 collection (including the LOH), then a re-arm of the keyboard hook: a blocking GC
    /// also stalls the hook callback, and Windows silently removes a hook that overruns LowLevelHooksTimeout.
    /// </summary>
    /// <returns>The GC pause, for logging.</returns>
    private TimeSpan CompactingCollect()
    {
        TimeSpan before = GC.GetTotalPauseDuration();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        TimeSpan pause = GC.GetTotalPauseDuration() - before;
        if (pause > TimeSpan.FromMilliseconds(250)) Log.Warn($"memory: the compacting collection paused the process for {pause.TotalMilliseconds:F0} ms; the keyboard hook is re-armed after it");
        Hook?.Rearm();
        return pause;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr hProcess, uint flags);
    /// <summary>GDI (0) or USER (1) handle count of this process, for spotting leaked bitmaps or DCs.</summary>
    private static uint GuiResources(uint flags) => GetGuiResources(Process.GetCurrentProcess().Handle, flags);

    /// <summary>Logs one timed operation (hotkey to overlay, window open, save) at Debug level. Not for per-frame
    /// use.</summary>
    public void Timing(string label, Stopwatch sw) => Log.Debug($"{label} in {sw.ElapsedMilliseconds} ms");

    /// <summary>Set while a reclaim is in flight, so a second snip finishing on top of the first does not queue another.</summary>
    private int _reclaiming;

    /// <summary>How long without a snip before the pooled frame buffers are released. Long enough to cover
    /// "Escape, then try again"; a grab into fresh buffers is not noticeably slower than into pooled ones.</summary>
    private static readonly TimeSpan FrameIdleRelease = TimeSpan.FromMinutes(1);
    /// <summary>Incremented by every <c>Idle</c>; a pending release acts only if the ticket is still the one it was
    /// armed with.</summary>
    private int _idleTicket;

    /// <summary>
    /// After <see cref="FrameIdleRelease"/> with no snip, drops the pooled frame buffers and collects, so an idle tray
    /// app returns to its startup footprint.
    /// <para>Dropping the buffers is safe even mid-snip (a grab holding one keeps it). The blocking collect is skipped
    /// if a snip has started; that snip's own <c>Idle</c> reclaims later. A snip starting during the collect is delayed
    /// by the rest of it, which is accepted.</para>
    /// </summary>
    private void ReleaseFramesWhenIdle()
    {
        int ticket = Interlocked.Increment(ref _idleTicket);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FrameIdleRelease);
                if (Volatile.Read(ref _idleTicket) != ticket || Session?.Busy == true) return;   // a newer idle period owns the release
                long held = Grabber.PooledBytes;
                if (held == 0) return;
                Grabber.ReleaseBuffers();
                if (Session?.Busy == true) { Log.Info($"frames released after {FrameIdleRelease.TotalMinutes:F0} min idle: {held / 1_048_576} MB of pooled buffers; a snip started, so the collect is left to its own reclaim"); return; }
                TimeSpan pause = CompactingCollect();
                using var me = Process.GetCurrentProcess();
                Log.Info($"frames released after {FrameIdleRelease.TotalMinutes:F0} min idle: {held / 1_048_576} MB of pooled buffers, managed now {GC.GetTotalMemory(false) / 1_048_576} MB, private {me.PrivateMemorySize64 / 1_048_576} MB, working set {me.WorkingSet64 / 1_048_576} MB, task manager {Interop.ProcessMemory.PrivateWorkingSet() / 1_048_576} MB, gc pause {pause.TotalMilliseconds:F0} ms");
#if TONESNIP_HARNESS
                MemoryProbe.Mark("after frames released");
#endif
            }
            catch (Exception e) { Log.Warn("frame release: " + e.Message); }
        });
    }

    /// <summary>
    /// Reclaims memory on a background thread a second after a snip ends, then logs where memory landed under
    /// <paramref name="label"/>.
    /// <para>The collection is blocking because the LOH is compacted only by a blocking gen 2 once
    /// <c>CompactOnce</c> is set. Busy is checked after the sleep, since a new snip may have started meanwhile; if so
    /// this gives up, and that snip's own <c>Idle</c> re-arms it.</para>
    /// </summary>
    public void LogMemory(string label)
    {
        if (Interlocked.Exchange(ref _reclaiming, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try
            {
                Thread.Sleep(1000);
                if (Session?.Busy == true) return;   // a snip started meanwhile; its Idle re-arms this
                GC.Collect(); GC.WaitForPendingFinalizers();
                TimeSpan pause = CompactingCollect();
                using var me = Process.GetCurrentProcess();
                GCMemoryInfo gc = GC.GetGCMemoryInfo();
                Log.Info($"{label}: managed {GC.GetTotalMemory(false) / 1_048_576} MB, gc committed {gc.TotalCommittedBytes / 1_048_576} MB, private {me.PrivateMemorySize64 / 1_048_576} MB, working set {me.WorkingSet64 / 1_048_576} MB, task manager {Interop.ProcessMemory.PrivateWorkingSet() / 1_048_576} MB, pooled frames {Grabber?.PooledBytes / 1_048_576 ?? 0} MB, gdi {GuiResources(0)}, user {GuiResources(1)}, gc pause {pause.TotalMilliseconds:F0} ms");
#if TONESNIP_HARNESS
                MemoryProbe.Mark("after reclaim");   // only when TONESNIP_MEMPROBE is set
#endif
            }
            catch (Exception e) { Log.Warn("reclaim: " + e.Message); }
            finally { Volatile.Write(ref _reclaiming, 0); }
        });
    }

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

    /// <summary>Opens an editor on one snip. Several may be open; each reference is dropped when its window
    /// closes.</summary>
    public void OpenViewer(CaptureResult r, bool annotate = false)
    {
        // One editor per snip, so two cannot save over each other.
        if (_viewers.FirstOrDefault(v => v.Shows(r)) is { } open) { open.Activate(); return; }
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
    /// Hands a hotkey from the hook thread to the UI thread, logging a refused enqueue or a pick-up slower than
    /// <see cref="HotkeyPickupBudget"/>, so a swallowed hotkey that never acts leaves a trace.
    /// </summary>
    private void DispatchHotkey(string action)
    {
        long pressed = Stopwatch.GetTimestamp();
        int picked = 0;
        bool queued = Ui.TryEnqueue(() =>
        {
            Volatile.Write(ref picked, 1);
            TimeSpan waited = Stopwatch.GetElapsedTime(pressed);
            if (waited > HotkeyPickupBudget) Log.Warn($"hotkey {action}: the UI thread picked it up {waited.TotalMilliseconds:F0} ms after the press");
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
    /// <see cref="FlushSettings"/> writes a pending save at once (Settings closing, Quit).</summary>
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

    /// <summary>Writes a queued settings save now, if one is waiting.</summary>
    public void FlushSettings()
    {
        if (_settingsSave is not { IsRunning: true } timer) return;
        timer.Stop();
        SaveSettings();
    }

    /// <summary>Writes settings.json, except in a harness side mode.</summary>
    private void SaveSettings()
    {
#if TONESNIP_HARNESS
        if (SideMode) return;
#endif
        try { SnipSettingsFile.Save(AppPaths.SettingsPath, Settings); } catch (Exception ex) { Log.Warn("settings save: " + ex.Message); }
    }


    /// <summary>Persists a change no listener needs (such as the last-used annotation style), without raising
    /// SettingsChanged.</summary>
    public void UpdateSettingsQuiet(Func<SnipSettings, SnipSettings> change)
    {
        Settings = change(Settings).Sanitized(out _);
        SaveSettings();
    }

    /// <summary>Tears everything down and ends the message loop. WinUI has no OnExit, so the tray's Quit calls this.</summary>
    public void Quit()
    {
        Log.Debug("exit");
        FlushSettings();
        _hostPipe?.Dispose(); _tray?.Dispose(); _messages?.Dispose();
        Hook?.Dispose(); Grabber?.Dispose(); Toasts?.Dispose();
        Exit();
    }
}
