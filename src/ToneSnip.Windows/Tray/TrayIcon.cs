using System.Runtime.InteropServices;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Windows.Interop;

namespace ToneSnip.Windows.Tray;

/// <summary>
/// The notification-area icon, straight on Shell_NotifyIconW so the app needs no WinForms and no XAML at startup.
/// Clicks arrive on <see cref="MessageWindow"/>'s callback message; the icon re-adds itself when Explorer restarts.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int CbSize;
        public IntPtr Hwnd;
        public int Id, Flags, CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public int State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public int VersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public int InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Shell_NotifyIconW(int message, ref NotifyIconData data);

    private const int NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const int NifMessage = 0x01, NifIcon = 0x02, NifTip = 0x04, NifInfo = 0x10, NifShowTip = 0x80;
    private const int NotifyIconVersion4 = 4;
    private const int SmCxSmIcon = 49, SmCySmIcon = 50;
    private const uint ImageIcon = 1, LrLoadFromFile = 0x0010;
    // Version-4 notifications: a plain left click arrives as NIN_SELECT, not WM_LBUTTONUP.
    private const int WmLButtonUp = 0x0202, WmRButtonUp = 0x0205, WmContextMenu = 0x007B, NinSelect = 0x0400, NinKeySelect = 0x0401;

    private readonly MessageWindow _window;
    private readonly ILog _log;
    private IntPtr _icon;
    private bool _ownsIcon;
    private string _tip;
    private bool _added, _disposed;

    /// <summary>Raised each time the icon is (re-)added to the notification area.</summary>
    public event Action? Shown;
    /// <summary>True once the icon is in the tray. The constructor adds it, so a subscriber attached afterwards must check this instead of waiting for <see cref="Shown"/>.</summary>
    public bool IsShown => _added;
    public event Action? LeftClick;
    /// <summary>Right click or the keyboard menu key, with the anchor point in screen pixels.</summary>
    public event Action<int, int>? RightClick;

    /// <summary>Loads an .ico file at the notification area's icon size. Returns IntPtr.Zero when the file cannot be read.</summary>
    public static IntPtr LoadIconFile(string path)
        => User32.LoadImageW(IntPtr.Zero, path, ImageIcon, User32.GetSystemMetrics(SmCxSmIcon), User32.GetSystemMetrics(SmCySmIcon), LrLoadFromFile);

    /// <summary>IDI_APPLICATION: the shared system icon to fall back on. It belongs to the system — never destroy it, so pass ownsIcon: false.</summary>
    public static IntPtr DefaultIcon() => User32.LoadIconW(IntPtr.Zero, (IntPtr)32512);

    public TrayIcon(MessageWindow window, IntPtr icon, string tooltip, ILog log, bool ownsIcon = true)
    {
        _window = window;
        _log = log;
        _icon = icon;
        _ownsIcon = ownsIcon;
        _tip = tooltip;
        _window.TrayMessage += OnTrayMessage;
        _window.TaskbarCreated += Add;
        Add();
    }

    private NotifyIconData Data(int flags) => new()
    {
        CbSize = Marshal.SizeOf<NotifyIconData>(),
        Hwnd = _window.Handle,
        Id = 1,
        Flags = flags,
        CallbackMessage = MessageWindow.TrayCallback,
        Icon = _icon,
        Tip = _tip,
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    private void Add()
    {
        NotifyIconData d = Data(NifMessage | NifIcon | NifTip | NifShowTip);
        if (!Shell_NotifyIconW(NimAdd, ref d))
        {
            // Most often the notification area is not ready yet (an early logon); the TaskbarCreated broadcast retries.
            _log.Warn($"tray icon: Shell_NotifyIconW(NIM_ADD) failed, error {Marshal.GetLastWin32Error()}");
            return;
        }
        NotifyIconData v = Data(0);
        v.VersionOrTimeout = NotifyIconVersion4;
        Shell_NotifyIconW(NimSetVersion, ref v);
        _added = true;
        Shown?.Invoke();
    }

    private void OnTrayMessage(int message, int x, int y)
    {
        switch (message)
        {
            case WmLButtonUp:
            case NinSelect:
            case NinKeySelect: LeftClick?.Invoke(); break;
            case WmRButtonUp:
            case WmContextMenu: RightClick?.Invoke(x, y); break;
        }
    }

    /// <summary>
    /// Swaps the icon in place (NIM_MODIFY with NIF_ICON), destroying the one it replaces when this object owned it.
    /// </summary>
    /// <param name="owns">True when the caller hands ownership over — a run-time glyph does, the shared system icon does not.</param>
    public void SetIcon(IntPtr icon, bool owns)
    {
        if (_disposed || icon == IntPtr.Zero || icon == _icon) return;
        IntPtr old = _icon;
        bool ownedOld = _ownsIcon;
        _icon = icon;
        _ownsIcon = owns;
        if (_added)
        {
            NotifyIconData d = Data(NifIcon);
            if (!Shell_NotifyIconW(NimModify, ref d)) _log.Warn($"tray icon: NIM_MODIFY(NIF_ICON) failed, error {Marshal.GetLastWin32Error()}");
        }
        // Destroyed only after the shell has the new icon, so there is no moment with nothing to draw.
        if (ownedOld && old != IntPtr.Zero) User32.DestroyIcon(old);
    }

    public void SetTooltip(string tooltip)
    {
        _tip = tooltip;
        if (!_added) return;
        NotifyIconData d = Data(NifTip | NifShowTip);
        Shell_NotifyIconW(NimModify, ref d);
    }

    /// <summary>The notification-area balloon, used when the richer toast is unavailable.</summary>
    public void Balloon(string title, string text)
    {
        if (!_added) return;
        NotifyIconData d = Data(NifInfo);
        d.InfoTitle = Trim(title, 63);
        d.Info = Trim(text, 255);
        d.VersionOrTimeout = 4000;   // honoured only on systems that still respect uTimeout; newer ones use the accessibility setting
        Shell_NotifyIconW(NimModify, ref d);
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.TrayMessage -= OnTrayMessage;
        _window.TaskbarCreated -= Add;
        if (_added) { NotifyIconData d = Data(0); Shell_NotifyIconW(NimDelete, ref d); }
        if (_ownsIcon && _icon != IntPtr.Zero) User32.DestroyIcon(_icon);
    }
}
