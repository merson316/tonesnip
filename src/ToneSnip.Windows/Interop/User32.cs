using System.Runtime.InteropServices;

namespace ToneSnip.Windows.Interop;

/// <summary>
/// The user32 imports shared across the app: windows, DCs, cursors, icons, metrics and monitors. Declared once here
/// rather than per file, and source-generated (<c>[LibraryImport]</c>) so the marshalling stubs are built at compile
/// time. The few that take a delegate or a struct with an inline string stay <c>[DllImport]</c>, which the generator
/// cannot marshal.
/// <para>Imports tied to one feature's own structs (the tray menu, the keyboard hook, the clipboard) stay beside that
/// feature.</para>
/// </summary>
public static partial class User32
{
    private const string Dll = "user32.dll";

    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct PaintStruct
    {
        public IntPtr Hdc;
        public int Erase;
        public Rect Paint;
        public int Restore, IncUpdate;
        public fixed byte Reserved[32];   // rgbReserved, at native offset 36: bytes, so no alignment padding creeps in
    }

    /// <summary>WNDCLASSEXW. The name fields are pointers the caller pins, so the struct stays blittable.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WndClassEx
    {
        public uint Size, Style;
        public IntPtr WndProc;
        public int ClsExtra, WndExtra;
        public IntPtr Instance, Icon, Cursor, Background, MenuName, ClassName, IconSm;
    }

    /// <summary>WNDCLASSW, as <see cref="WndClassEx"/> without the size and small icon.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WndClass
    {
        public uint Style;
        public IntPtr WndProc;
        public int ClsExtra, WndExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public IntPtr MenuName, ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IconInfo
    {
        public int IsIcon;              // BOOL: an icon, not a cursor, so the hotspot fields are ignored
        public int HotspotX, HotspotY;
        public IntPtr Mask, Color;
    }

    /// <summary>HIGHCONTRASTW. <c>lpszDefaultScheme</c> is left null: SPI_GETHIGHCONTRAST only copies the scheme name
    /// into a caller-supplied buffer, so the flags come back on their own.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HighContrast
    {
        public uint Size;
        public uint Flags;
        public IntPtr DefaultScheme;
    }

    /// <summary>MONITORINFOEXW. The inline device name makes it non-blittable, so <see cref="GetMonitorInfoW"/> stays
    /// a runtime-marshalled import.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MonitorInfoEx
    {
        public uint Size; public Rect Monitor; public Rect Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    // ----- window classes and windows -----
    [LibraryImport(Dll, SetLastError = true)] public static partial ushort RegisterClassExW(ref WndClassEx c);
    [LibraryImport(Dll, SetLastError = true)] public static partial ushort RegisterClassW(ref WndClass c);
    [LibraryImport(Dll, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UnregisterClassW(string className, IntPtr instance);
    [LibraryImport(Dll, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateWindowExW(int exStyle, string className, string? windowName, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [LibraryImport(Dll)] public static partial IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyWindow(IntPtr hwnd);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ShowWindow(IntPtr hwnd, int command);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UpdateWindow(IntPtr hwnd);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [LibraryImport(Dll)] public static partial int GetWindowLongW(IntPtr hwnd, int index);
    [LibraryImport(Dll)] public static partial int SetWindowLongW(IntPtr hwnd, int index, int value);
    [LibraryImport(Dll, EntryPoint = "GetWindowLongPtrW")] public static partial IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [LibraryImport(Dll, EntryPoint = "SetWindowLongPtrW")] public static partial IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)] public static partial uint RegisterWindowMessageW(string name);
    [LibraryImport(Dll)] public static partial IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)] public static partial IntPtr FindWindowW(string className, string? windowName);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)] public static partial IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? className, string? title);

    // ----- window state and z-order -----
    [LibraryImport(Dll)] public static partial IntPtr GetForegroundWindow();
    /// <summary>LASTINPUTINFO: the tick count of the session's last keyboard or mouse input.</summary>
    [StructLayout(LayoutKind.Sequential)] public struct LastInputInfo { public uint Size; public uint Time; }
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetLastInputInfo(ref LastInputInfo info);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetForegroundWindow(IntPtr hwnd);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool BringWindowToTop(IntPtr hwnd);
    [LibraryImport(Dll)] public static partial IntPtr SetFocus(IntPtr hwnd);
    [LibraryImport(Dll)] public static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool AttachThreadInput(uint attach, uint to, [MarshalAs(UnmanagedType.Bool)] bool flag);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsWindowVisible(IntPtr hwnd);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsIconic(IntPtr hwnd);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsZoomed(IntPtr hwnd);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsWindow(IntPtr hwnd);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetLayeredWindowAttributes(IntPtr hwnd, out uint key, out byte alpha, out uint flags);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)] public static partial int GetWindowTextW(IntPtr hwnd, [Out] char[] text, int max);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ClientToScreen(IntPtr hwnd, ref Point p);

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    /// <summary>Runtime-marshalled: the generator does not marshal delegates.</summary>
    [DllImport(Dll)] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

    // ----- painting -----
    [LibraryImport(Dll)] public static partial IntPtr GetDC(IntPtr hwnd);
    [LibraryImport(Dll)] public static partial int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [LibraryImport(Dll)] public static partial IntPtr BeginPaint(IntPtr hwnd, out PaintStruct ps);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool EndPaint(IntPtr hwnd, ref PaintStruct ps);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool InvalidateRect(IntPtr hwnd, ref Rect r, [MarshalAs(UnmanagedType.Bool)] bool erase);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool InvalidateRect(IntPtr hwnd, IntPtr all, [MarshalAs(UnmanagedType.Bool)] bool erase);
    /// <summary>Copies the window's update region into <paramref name="rgn"/>; returns its type (3 = COMPLEXREGION).</summary>
    [LibraryImport(Dll)] public static partial int GetUpdateRgn(IntPtr hwnd, IntPtr rgn, [MarshalAs(UnmanagedType.Bool)] bool erase);
    [LibraryImport(Dll)] public static partial int FillRect(IntPtr dc, ref Rect rect, IntPtr brush);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)] public static partial int DrawTextW(IntPtr dc, string text, int length, ref Rect rect, uint format);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [LibraryImport(Dll)] public static partial uint GetSysColor(int index);

    // ----- input -----
    [LibraryImport(Dll)] public static partial IntPtr SetCapture(IntPtr hwnd);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ReleaseCapture();
    [LibraryImport(Dll)] public static partial short GetKeyState(int vk);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetCursorPos(out Point p);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetCursorPos(int x, int y);

    // ----- layered windows -----
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

    // ----- cursors and icons -----
    [LibraryImport(Dll)] public static partial IntPtr LoadCursorW(IntPtr instance, IntPtr name);
    [LibraryImport(Dll)] public static partial IntPtr SetCursor(IntPtr cursor);
    [LibraryImport(Dll, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr LoadImageW(IntPtr instance, string name, uint type, int cx, int cy, uint load);
    [LibraryImport(Dll)] public static partial IntPtr LoadIconW(IntPtr instance, IntPtr name);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyIcon(IntPtr icon);
    [LibraryImport(Dll, SetLastError = true)] public static partial IntPtr CreateIconIndirect(ref IconInfo info);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetIconInfo(IntPtr icon, out IconInfo info);

    // ----- metrics, DPI and monitors -----
    [LibraryImport(Dll)] public static partial int GetSystemMetrics(int index);
    [LibraryImport(Dll)] public static partial uint GetDpiForWindow(IntPtr hwnd);
    [LibraryImport(Dll)] public static partial uint GetDpiForSystem();
    [LibraryImport(Dll)] public static partial IntPtr MonitorFromRect(ref Rect rect, uint flags);
    /// <summary>The POINT is passed by value, packed into 64 bits (x low, y high).</summary>
    [LibraryImport(Dll)] public static partial IntPtr MonitorFromPoint(long point, uint flags);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SystemParametersInfoW(uint action, uint param, out Rect value, uint winIni);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SystemParametersInfoW(uint action, uint param, out int value, uint winIni);
    [LibraryImport(Dll, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SystemParametersInfoW(uint action, uint param, ref HighContrast data, uint update);

    public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);
    /// <summary>Runtime-marshalled: the generator does not marshal delegates.</summary>
    [DllImport(Dll)] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    /// <summary>Runtime-marshalled: <see cref="MonitorInfoEx"/> carries an inline string.</summary>
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfoEx info);

    // ----- power and session notifications -----
    [LibraryImport(Dll, SetLastError = true)] public static partial IntPtr RegisterSuspendResumeNotification(IntPtr recipient, int flags);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UnregisterSuspendResumeNotification(IntPtr handle);
    [LibraryImport(Dll, SetLastError = true)] public static partial IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, int flags);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UnregisterPowerSettingNotification(IntPtr handle);

    // ----- diagnostics -----
    /// <summary>GDI (0) or USER (1) handle count of a process.</summary>
    [LibraryImport(Dll)] public static partial uint GetGuiResources(IntPtr process, uint flags);
}
