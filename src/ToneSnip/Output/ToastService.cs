using ToneSnip.App.Capture;
using ToneSnip.App.Interop;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Output;
using Shell = ToneSnip.Windows.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace ToneSnip.App.Output;

/// <summary>
/// The notification a finished snip shows, in the style <c>Settings.Notification</c> names:
/// <list type="bullet">
/// <item><b>"tonesnip"</b> (the default): <see cref="ToastWindow"/>, the app's own card above the tray, with no
/// registration.</item>
/// <item><b>"windows"</b>: a Windows App SDK app notification with an inline thumbnail, falling back to a tray balloon
/// through <see cref="Fallback"/>.</item>
/// </list>
/// Registration is lazy, on the first Windows-style <see cref="Show"/>, to keep AppNotificationManager off the
/// cold-start path.
/// <para>Both styles keep the last <see cref="Keep"/> results alive (see <see cref="Retain"/>) so a notification can
/// still act on its snip.</para>
/// </summary>
public sealed class ToastService : IDisposable
{
    private const int Keep = 3;
    private readonly ILog _log;
    private readonly DispatcherQueue _ui;
    private readonly object _lock = new();
    private readonly Dictionary<string, CaptureResult> _recent = new();
    private readonly Queue<string> _order = new();
    private bool _registered;
    /// <summary>The card on screen, or null; a newer snip re-binds it rather than adding a second.</summary>
    private ToastWindow? _card;
    /// <summary>The snip waiting for the overlay to come down, and the timer that watches for it.</summary>
    private (CaptureResult Result, string Thumb)? _deferred;
    private DispatcherQueueTimer? _defer;
    private DateTime _deferUntil;
    public event Action<CaptureResult>? Opened;
    public event Action<CaptureResult>? Edited;
    public Action<string, string>? Fallback { get; set; }
    /// <summary>Finds a snip by its saved path, for a notification whose result is no longer held (an earlier run, or
    /// older than <see cref="Keep"/>). Called on the UI thread.</summary>
    public Func<string, Task<CaptureResult?>>? Lookup { get; set; }

    public ToastService(ILog log)
    {
        _log = log;
        _ui = DispatcherQueue.GetForCurrentThread();
    }

    public void Show(CaptureResult r, string thumbPath)
    {
        // Retained first, for both styles, so it does not depend on a registration that can throw.
        string id = Retain(r);
        if (!string.Equals(App.Current.Settings.Notification, "windows", StringComparison.OrdinalIgnoreCase))
        {
            ShowCardWhenIdle(r, thumbPath);
            return;
        }
        ShowWindowsToast(r, id, thumbPath);
    }

    /// <summary>Keeps <paramref name="r"/> under a fresh id, compacts the results already held, and drops any beyond
    /// <see cref="Keep"/>.</summary>
    private string Retain(CaptureResult r)
    {
        string id = Guid.NewGuid().ToString("N");
        lock (_lock)
        {
            foreach (CaptureResult old in _recent.Values) old.Compact();
            _recent[id] = r; _order.Enqueue(id);
            while (_order.Count > Keep) _recent.Remove(_order.Dequeue());
        }
        return id;
    }

    private void ShowWindowsToast(CaptureResult r, string id, string thumbPath)
    {
        SnipNotice notice = NoticeFor(r);
        string title = notice.Title, text = notice.Detail;
        try
        {
            EnsureRegistered();
            var builder = new AppNotificationBuilder().AddArgument("open", id).AddText(title).AddText(text);
            if (File.Exists(thumbPath)) builder.SetInlineImage(new Uri(thumbPath));
            // The body click carries "open"; each button carries "action" and "id". All carry the saved path, which a
            // click can still act on once the id is no longer held.
            AppNotificationButton Button(string label, string action)
            {
                var b = new AppNotificationButton(label).AddArgument("action", action).AddArgument("id", id);
                return r.SavedPath != null ? b.AddArgument("path", r.SavedPath) : b;
            }
            if (r.SavedPath != null) builder.AddArgument("path", r.SavedPath);
            builder.AddButton(Button("Open", "open"));
            if (r.SavedPath != null) builder.AddButton(Button("Show in folder", "folder"));
            builder.AddButton(Button("Edit", "edit"));
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception e)
        {
            _log.Warn("toast: " + e.Message);
            Fallback?.Invoke(title, text);
        }
    }

    // ----- the ToneSnip card ------------------------------------------------------------------------------------------

    /// <summary>
    /// Shows the card once no snip session is busy. <c>Output.Completed</c> is raised inside <c>SnipSession.Run</c>, so
    /// the session is still busy at first and the card usually waits one poll; it gives up after
    /// <see cref="DeferCap"/>.
    /// </summary>
    private void ShowCardWhenIdle(CaptureResult r, string thumbPath)
    {
        _deferred = (r, thumbPath);
        _deferUntil = DateTime.UtcNow + DeferCap;
        if (!_ui.TryEnqueue(TryShowDeferred)) TryShowDeferred();
    }

    /// <summary>How long the card will wait for a snip session to end before giving up on itself.</summary>
    private static readonly TimeSpan DeferCap = TimeSpan.FromSeconds(30);

    private void TryShowDeferred()
    {
        if (_deferred is not { } pending) return;
        // App.Session is null in the harness side modes, which build the card themselves.
        if (App.Current.Session is { Busy: true })
        {
            if (DateTime.UtcNow > _deferUntil)
            {
                _deferred = null;
                _defer?.Stop();
                _log.Warn("toast card: a snip was still running after 30 s; the card for the previous snip was dropped");
                return;
            }
            _defer ??= CreateDeferTimer();
            _defer.Start();
            return;
        }
        _defer?.Stop();
        _deferred = null;
        ShowCard(pending.Result, pending.Thumb);
    }

    private DispatcherQueueTimer CreateDeferTimer()
    {
        DispatcherQueueTimer timer = _ui.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(200);
        timer.IsRepeating = false;
        timer.Tick += (t, _) => { t.Stop(); TryShowDeferred(); };
        return timer;
    }

    /// <summary>Re-binds the card on screen (restarting its dwell), or builds a new one if none is up or the current
    /// one is already fading out to close.</summary>
    private void ShowCard(CaptureResult r, string thumbPath)
    {
        try
        {
            if (_card is { IsDismissing: false } live) { live.Bind(r, thumbPath); return; }
            var card = new ToastWindow(_log, x => Opened?.Invoke(x), x => Edited?.Invoke(x));
            _card = card;
            card.WhenClosed(() => { if (_card == card) _card = null; });
            card.Bind(r, thumbPath);
        }
        catch (Exception e)
        {
            _card = null;
            _log.Warn("toast card: " + e.Message);
            SnipNotice notice = NoticeFor(r);
            Fallback?.Invoke(notice.Title, notice.Detail);
        }
    }

    internal static SnipNotice NoticeFor(CaptureResult r) => SnipNotice.For(r.SavedPath, r.SaveAttempted, r.CopyAttempted, r.Copied);

    /// <summary>Hides the card immediately, without fading, so it is not captured. True when one was on
    /// screen.</summary>
    public bool HideForSnip()
    {
        _deferred = null;
        _defer?.Stop();
        if (_card is not { IsDismissing: false } card) return false;
        card.HideNow();
        return true;
    }

    /// <summary>Takes the card down, if one is up (for example when the Recent flyout opens in the same
    /// corner).</summary>
    public void Dismiss()
    {
        _deferred = null;
        _defer?.Stop();
        _card?.Dismiss();
    }

    /// <summary>
    /// Subscribes NotificationInvoked and registers, once. A packaged process must call the parameterless
    /// <c>Register()</c> (identity comes from the manifest); <c>Register(displayName, iconUri)</c> is for unpackaged
    /// apps and throws in the MSIX build.
    /// <para>Normally lazy, but <c>App.OnLaunched</c> calls it first for a toast-click launch, because the activation is
    /// delivered through the COM activator this installs.</para>
    /// </summary>
    internal void EnsureRegistered()
    {
        if (_registered) return;
        _registered = true;   // set first: a failed Register should not be retried on every snip
        AppNotificationManager.Default.NotificationInvoked += OnInvoked;
#if TONESNIP_HARNESS
        // The debug build registers nothing: the unpackaged registration is keyed on the display name, so it could
        // overwrite the installed app's and route its toast clicks here. The Windows style falls back to the balloon.
        _log.Debug("toast: no AppNotification registration in the debug build; the Windows style falls back to the tray balloon");
#else
        PreloadInsightsResource();
        if (Autostart.IsPackaged) { AppNotificationManager.Default.Register(); return; }
        string icon = Path.Combine(AppPaths.Dir, "icon.ico");
        // Unpackaged: pass the display name and the extracted icon; without the icon, register without one.
        if (File.Exists(icon)) AppNotificationManager.Default.Register("ToneSnip", new Uri(icon));
        else AppNotificationManager.Default.Register();
#endif
    }

#if !TONESNIP_HARNESS
    private const string InsightsResourceDll = "Microsoft.WindowsAppRuntime.Insights.Resource.dll";

    /// <summary>
    /// Loads the Windows App SDK's Insights resource DLL by full path, so Register's bare-name <c>LoadLibraryW</c> gets
    /// the already-loaded module. Without it Register throws ERROR_MOD_NOT_FOUND (microsoft/WindowsAppSDK#6071; see
    /// the IncludeInsightsResourceDll target in ToneSnip.csproj).
    /// <para>Needed for the single-file exe, which extracts the DLL off the search path.
    /// <c>AppContext.BaseDirectory</c> is the right folder in every build layout.</para>
    /// </summary>
    private void PreloadInsightsResource()
    {
        string path = Path.Combine(AppContext.BaseDirectory, InsightsResourceDll);
        if (!System.Runtime.InteropServices.NativeLibrary.TryLoad(path, out _))
            _log.Warn($"toast: {InsightsResourceDll} not loaded from {AppContext.BaseDirectory}; the Windows-style toast registration will likely fail");
    }
#endif

    /// <summary>Raised on a background thread.</summary>
    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) => Activate(args.Arguments);

    /// <summary>Handles a notification click, from <see cref="OnInvoked"/> or a toast-click launch. A body click (only
    /// "open") means action "open". A result still held is used directly; otherwise the saved path is revealed or looked
    /// up in history. Any thread.</summary>
    internal void Activate(IDictionary<string, string> arguments)
    {
        string action = arguments.TryGetValue("action", out string? a) && a != null ? a : "open";
        arguments.TryGetValue("path", out string? path);
        if (!arguments.TryGetValue("id", out string? id) || id == null) arguments.TryGetValue("open", out id);
        CaptureResult? r = null;
        if (id != null) lock (_lock) _recent.TryGetValue(id, out r);
        if (r == null)
        {
            if (string.IsNullOrEmpty(path)) { _log.Debug("toast: clicked, but its snip is no longer held and it names no file"); return; }
            _ui.TryEnqueue(async () =>
            {
                try
                {
                    if (action == "folder") { Shell.Reveal(path, _log); return; }
                    if (Lookup == null || await Lookup(path) is not { } found) { _log.Debug("toast: clicked, but its snip is no longer in the history: " + path); return; }
                    if (action == "edit") Edited?.Invoke(found); else Opened?.Invoke(found);
                }
                catch (Exception e) { _log.Warn("toast click: " + e.Message); }
            });
            return;
        }
        switch (action)
        {
            case "folder":
                if (r.SavedPath != null) _ui.TryEnqueue(() => Shell.Reveal(r.SavedPath, _log));
                break;
            case "edit":
                _ui.TryEnqueue(() => Edited?.Invoke(r));
                break;
            default:
                _ui.TryEnqueue(() => Opened?.Invoke(r));
                break;
        }
    }

    /// <summary>Stops the deferral timer and the card. Unregister() is deliberately not called, so an older toast still
    /// launches the app when clicked.</summary>
    public void Dispose()
    {
        _deferred = null;
        _defer?.Stop();
        _card?.Dismiss();
    }
}
