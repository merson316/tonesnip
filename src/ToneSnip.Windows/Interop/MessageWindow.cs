using System.Runtime.InteropServices;
using ToneSnip.Core.Diagnostics;

namespace ToneSnip.Windows.Interop;

/// <summary>
/// The app's hidden window: it receives the tray icon's callback, the tray menu's owner-draw messages and system
/// broadcasts. It is top-level rather than HWND_MESSAGE because WM_DISPLAYCHANGE and "TaskbarCreated" only reach
/// top-level windows, and it is shown without activation at (-32000, -32000) with zero size because
/// SetForegroundWindow, which the tray menu needs, refuses a hidden window.
/// Create it on the UI thread: its messages are pumped by that thread's loop.
/// </summary>
public sealed class MessageWindow : IDisposable
{
    /// <summary>The tray icon's callback message (WM_APP + 1).</summary>
    public const int TrayCallback = 0x8000 + 1;

    public const int WmDisplayChange = 0x007E, WmCommand = 0x0111, WmMeasureItem = 0x002C, WmDrawItem = 0x002B,
                     WmInitMenuPopup = 0x0117, WmNull = 0x0000, WmSettingChange = 0x001A, WmThemeChanged = 0x031A,
                     WmPowerBroadcast = 0x0218, WmWtsSessionChange = 0x02B1;

    private const int PbtApmSuspend = 0x4, PbtApmResumeAutomatic = 0x12;
    private const int WtsSessionLock = 0x7, WtsSessionUnlock = 0x8;
    private const int DeviceNotifyWindowHandle = 0, NotifyForThisSession = 0;

    /// <summary>SPI_SETHIGHCONTRAST: the wParam WM_SETTINGCHANGE carries when a contrast theme is switched on or off.</summary>
    private const int SpiSetHighContrast = 0x0043;
    private const string ImmersiveColorSet = "ImmersiveColorSet";

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Style;
        public IntPtr WndProc;
        public int ClsExtra, WndExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public IntPtr MenuName, ClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassW(ref WndClass c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool UnregisterClassW(string className, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowExW(int exStyle, string className, string? windowName, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr RegisterSuspendResumeNotification(IntPtr recipient, int flags);
    [DllImport("user32.dll")] private static extern bool UnregisterSuspendResumeNotification(IntPtr handle);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSRegisterSessionNotification(IntPtr hwnd, int flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);

    private const int WsExToolWindow = 0x00000080, WsPopup = unchecked((int)0x80000000);
    private const int SwShowNa = 8;   // show at the current z-order without activating
    private const int OffScreen = -32000;
    private const string ClassName = "tonesnip-messages";

    // One registration per process, with a static window procedure that finds the instance by HWND: a per-instance
    // procedure would leave a second window routing its messages to the first instance's (possibly dead) delegate.
    private static readonly WndProcDelegate StaticProc = Proc;   // static field: the class holds a raw function pointer to it
    private static readonly Dictionary<IntPtr, MessageWindow> Live = new();
    private static readonly object Gate = new();
    private static bool _registered;

    private readonly uint _taskbarCreated;
    private readonly ILog? _log;
    private bool _disposed;

    public IntPtr Handle { get; }

    /// <summary>WM_DISPLAYCHANGE: monitors were added, removed or reconfigured.</summary>
    public event Action? DisplayChanged;
    /// <summary>Explorer restarted and every tray icon has to be re-added.</summary>
    public event Action? TaskbarCreated;
    /// <summary>The tray icon's callback: (notification message, anchor x, anchor y) in screen pixels.</summary>
    public event Action<int, int, int>? TrayMessage;
    /// <summary>
    /// The desktop's theme changed: WM_THEMECHANGED, the SPI_SETHIGHCONTRAST flavour of WM_SETTINGCHANGE, or the
    /// "ImmersiveColorSet" broadcast. Raised on the UI thread inside the window procedure; a handler that opens
    /// windows or repaints should post the work instead.
    /// </summary>
    /// <remarks>
    /// The app's listener for contrast themes being switched on or off, preferred over UISettings.ColorValuesChanged,
    /// which is a colour signal raised on a pool thread.
    /// </remarks>
    public event Action? ThemeChanged;

    /// <summary>The machine is going to sleep (false) or has woken (true). Registered for explicitly so Modern Standby
    /// machines report it too.</summary>
    public event Action<bool>? PowerChanged;
    /// <summary>The session was locked (true) or unlocked (false).</summary>
    public event Action<bool>? SessionLockChanged;

    private IntPtr _suspendResume;

    private Func<uint, IntPtr, IntPtr, IntPtr?>? _hook;

    /// <summary>
    /// Raw message hook, called before the built-in dispatch; returning non-null makes that the window procedure's
    /// result. The tray menu uses it for WM_MEASUREITEM/WM_DRAWITEM. Only one hook at a time, since a multicast
    /// delegate would discard every result but the last.
    /// </summary>
    public Func<uint, IntPtr, IntPtr, IntPtr?>? Hook
    {
        get => _hook;
        set
        {
            if (value != null && _hook != null) throw new InvalidOperationException("MessageWindow.Hook is already set; clear it first");
            _hook = value;
        }
    }

    public MessageWindow(ILog? log = null)
    {
        _log = log;
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        IntPtr instance = GetModuleHandleW(null);
        lock (Gate)
        {
            if (!_registered)
            {
                IntPtr className = Marshal.StringToHGlobalUni(ClassName);
                try
                {
                    var wc = new WndClass { WndProc = Marshal.GetFunctionPointerForDelegate(StaticProc), Instance = instance, ClassName = className };
                    if (RegisterClassW(ref wc) == 0) throw new InvalidOperationException("RegisterClassW failed: " + Marshal.GetLastWin32Error());
                }
                finally { Marshal.FreeHGlobal(className); }
                _registered = true;
            }
            Handle = CreateWindowExW(WsExToolWindow, ClassName, ClassName, WsPopup, OffScreen, OffScreen, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (Handle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (Live.Count == 0) UnregisterClass(instance);
                throw new InvalidOperationException("CreateWindowExW failed: " + error);
            }
            Live[Handle] = this;
        }
        ShowWindow(Handle, SwShowNa);   // SetForegroundWindow (which the tray menu needs) refuses a window that is not visible
        _suspendResume = RegisterSuspendResumeNotification(Handle, DeviceNotifyWindowHandle);
        if (_suspendResume == IntPtr.Zero) _log?.Warn($"message window: no suspend/resume notifications, error {Marshal.GetLastWin32Error()}");
        if (!WTSRegisterSessionNotification(Handle, NotifyForThisSession)) _log?.Warn($"message window: no session lock notifications, error {Marshal.GetLastWin32Error()}");
    }

    private static IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        MessageWindow? self;
        lock (Gate) Live.TryGetValue(hwnd, out self);
        // Messages that arrive before CreateWindowExW returns (WM_NCCREATE, WM_CREATE) have no instance yet.
        return self != null ? self.Dispatch(hwnd, msg, wParam, lParam) : DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// <see cref="Proc"/> is a raw function pointer held by the window class, so an exception from a hook or event
    /// handler must not unwind through <c>user32!DispatchMessage</c>, which silently kills the process.
    /// </summary>
    private IntPtr Dispatch(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try { return DispatchCore(hwnd, msg, wParam, lParam); }
        catch (Exception e)
        {
            _log?.Error($"message window: msg 0x{msg:X4}: {e}");
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    private IntPtr DispatchCore(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        IntPtr? hooked = _hook?.Invoke(msg, wParam, lParam);
        if (hooked != null) return hooked.Value;
        switch (msg)
        {
            case WmDisplayChange: DisplayChanged?.Invoke(); return IntPtr.Zero;
            case TrayCallback:
                TrayMessage?.Invoke(Low(lParam), Low(wParam), High(wParam));
                return IntPtr.Zero;
            case WmPowerBroadcast:
                if (wParam == (IntPtr)PbtApmSuspend) PowerChanged?.Invoke(false);
                else if (wParam == (IntPtr)PbtApmResumeAutomatic) PowerChanged?.Invoke(true);
                return (IntPtr)1;   // TRUE: nothing here refuses a suspend
            case WmWtsSessionChange:
                if (wParam == (IntPtr)WtsSessionLock) SessionLockChanged?.Invoke(true);
                else if (wParam == (IntPtr)WtsSessionUnlock) SessionLockChanged?.Invoke(false);
                return IntPtr.Zero;
            case WmThemeChanged:
                ThemeChanged?.Invoke();
                return DefWindowProcW(hwnd, msg, wParam, lParam);
            case WmSettingChange:
                // lParam is a string pointer only for some senders, so it is read only in the shape the shell uses
                // (wParam 0); the contrast switch is recognised by wParam alone.
                if (wParam == (IntPtr)SpiSetHighContrast ||
                    (wParam == IntPtr.Zero && lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == ImmersiveColorSet))
                    ThemeChanged?.Invoke();
                return DefWindowProcW(hwnd, msg, wParam, lParam);
            default:
                if (msg == _taskbarCreated) { TaskbarCreated?.Invoke(); return IntPtr.Zero; }
                return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    /// <summary>
    /// Drops the class registration. UnregisterClassW fails while any window of the class is alive, so the flag follows
    /// the call's result; otherwise a later MessageWindow would fail to register an existing class.
    /// </summary>
    private static void UnregisterClass(IntPtr instance) => _registered = !UnregisterClassW(ClassName, instance);

    private static int Low(IntPtr v) => (short)(v.ToInt64() & 0xFFFF);
    private static int High(IntPtr v) => (short)((v.ToInt64() >> 16) & 0xFFFF);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Handle == IntPtr.Zero) return;
        if (_suspendResume != IntPtr.Zero) UnregisterSuspendResumeNotification(_suspendResume);
        WTSUnRegisterSessionNotification(Handle);
        DestroyWindow(Handle);
        lock (Gate)
        {
            Live.Remove(Handle);
            // The last window unregisters the class; a later MessageWindow registers it again.
            if (Live.Count == 0 && _registered) UnregisterClass(GetModuleHandleW(null));
        }
    }
}
