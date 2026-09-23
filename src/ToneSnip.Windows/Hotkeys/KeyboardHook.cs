using System.Runtime.InteropServices;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Hotkeys;

namespace ToneSnip.Windows.Hotkeys;

/// <summary>
/// Low-level keyboard hook that fires when a bound chord is pressed. Every decision is <see cref="HotkeyFilter"/>'s:
/// this class is the native plumbing around it. No key is recorded or logged.
/// The hook lives on its own message-loop thread so UI work can never stall it (Windows silently removes
/// a low-level hook whose thread stops answering), and it is re-armed every few minutes as a safety net.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100, WmSyskeydown = 0x0104, WmKeyup = 0x0101, WmSyskeyup = 0x0105, WmQuit = 0x0012, WmUser = 0x0400;
    private const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkLwin = 0x5B, VkRwin = 0x5C;
    private const uint LlkhfInjected = 0x10;
    private const int RearmMessage = WmUser + 1;
    private static readonly TimeSpan RearmInterval = TimeSpan.FromMinutes(5);

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern bool GetMessageW(out Msg msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? lpModuleName);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, [In] Input[] inputs, int size);

    /// <summary>INPUT with its keyboard member, laid out for x64: type, 4 bytes of padding, then KEYBDINPUT padded to the
    /// union's 32 bytes (MOUSEINPUT is the largest member).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public ushort wVk;
        [FieldOffset(10)] public ushort wScan;
        [FieldOffset(12)] public uint dwFlags;
        [FieldOffset(16)] public uint time;
        [FieldOffset(24)] public IntPtr dwExtraInfo;
    }

    /// <summary>A virtual-key code Windows leaves unassigned: pressing it does nothing anywhere, which is the point.</summary>
    private const ushort VkUnassigned = 0xE8;
    private const uint InputKeyboard = 1, KeyeventfKeyup = 0x2;

    /// <summary>
    /// Puts a no-op key between a held Alt or Win and its release. The chord's own key was swallowed, so otherwise the
    /// foreground app sees a bare Alt press (menu-bar activation) or Win press (Start). The injected key comes back
    /// through this hook flagged injected and the filter passes it through.
    /// </summary>
    private static bool MaskModifier()
    {
        var inputs = new Input[2];
        inputs[0] = new Input { type = InputKeyboard, wVk = VkUnassigned };
        inputs[1] = new Input { type = InputKeyboard, wVk = VkUnassigned, dwFlags = KeyeventfKeyup };
        return SendInput(2, inputs, Marshal.SizeOf<Input>()) == 2;
    }

    private readonly HookProc _proc;   // kept alive for the hook's lifetime
    private readonly ILog _log;
    private IntPtr _hook;
    private volatile IReadOnlyList<HotkeyBinding> _bindings = Array.Empty<HotkeyBinding>();
    private Thread? _thread;
    private uint _threadId;
    private System.Threading.Timer? _rearm;
    private volatile bool _installed;
    /// <summary>The swallow/fire/record decisions, used on the hook thread only. Uses Environment.TickCount64 because the
    /// wall clock can step back after a resume and stall the debounce.</summary>
    private readonly HotkeyFilter _filter = new(() => Environment.TickCount64);
    /// <summary>Failure kinds already warned about, so a broken handler does not log a line per keystroke.</summary>
    private readonly HashSet<string> _warned = new();

    /// <summary>Raised on the hook thread; handlers must return immediately.</summary>
    public event Action<HotkeyBinding>? Pressed;
    /// <summary>When true, matching key-downs are swallowed so no other app sees them.</summary>
    public volatile bool Swallow;
    /// <summary>Whether a binding applies right now (<see cref="HotkeyFilter.IsActive"/>); null means every binding does.</summary>
    public Func<HotkeyBinding, bool>? IsActive { get; set; }
    /// <summary>
    /// While set, every non-modifier key-down and key-up is reported here (chord, isDown) and swallowed, and no binding
    /// fires. Used by the settings hotkey recorder so PrintScreen and friends never reach Windows while recording.
    /// </summary>
    public volatile Action<Chord, bool>? Recorder;
    public bool Paused { get; set; }

    public KeyboardHook(ILog log)
    {
        _log = log;
        _proc = Callback;
    }

    public void SetBindings(IReadOnlyList<HotkeyBinding> bindings)
    {
        _bindings = bindings.Where(b => !b.Chord.IsNone).ToList();
        if (_installed) _log.Debug($"hotkeys: {string.Join(", ", _bindings.Select(b => $"{b.Chord}={b.Action}"))}");
    }

    public void Install()
    {
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() => HookThread(ready)) { IsBackground = true, Name = "keyboard hook" };
        _thread.Start();
        ready.Wait(2000);
        _rearm = new System.Threading.Timer(_ => { if (_threadId != 0) PostThreadMessageW(_threadId, RearmMessage, IntPtr.Zero, IntPtr.Zero); }, null, RearmInterval, RearmInterval);
    }

    private void HookThread(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        Arm();
        ready.Set();
        while (GetMessageW(out Msg msg, IntPtr.Zero, 0, 0))
        {
            if (msg.message == RearmMessage) Arm();
            if (msg.message == WmQuit) break;
        }
        Disarm();
    }

    private void Arm()
    {
        Disarm();
        _hook = SetWindowsHookExW(WhKeyboardLl, _proc, GetModuleHandleW(null), 0);
        _installed = _hook != IntPtr.Zero;
        if (!_installed) _log.Error($"keyboard hook failed: {Marshal.GetLastWin32Error()}");
        // Info on the first arm so the log always shows the hook came up; Debug on the periodic re-arms.
        else if (!_everArmed) { _everArmed = true; _log.Info($"keyboard hook armed, {_bindings.Count} bindings"); }
        else _log.Debug($"keyboard hook re-armed, {_bindings.Count} bindings");
    }

    /// <summary>Set by the first successful <see cref="Arm"/>.</summary>
    private bool _everArmed;

    private void Disarm()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        _installed = false;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);
        // An exception escaping a native callback fail-fasts the process with nothing logged; on any failure the key
        // goes on to the next hook.
        try
        {
            int msg = (int)wParam;
            bool isDown = msg == WmKeydown || msg == WmSyskeydown, isUp = msg == WmKeyup || msg == WmSyskeyup;
            if (!isDown && !isUp) return CallNextHookEx(_hook, nCode, wParam, lParam);
            int vk = Marshal.ReadInt32(lParam);   // KBDLLHOOKSTRUCT.vkCode
            bool injected = ((uint)Marshal.ReadInt32(lParam, 8) & LlkhfInjected) != 0;

            _filter.Bindings = _bindings;
            _filter.Paused = Paused;
            _filter.Swallow = Swallow;
            _filter.IsActive = IsActive;
            _filter.Recorder = Recorder;
            KeyDecision d = _filter.OnKey(vk, isDown, injected, new KeyMods(Down(VkControl), Down(VkShift), Down(VkMenu), Down(VkLwin) || Down(VkRwin)));
            if (d.MaskModifier && !MaskModifier() && _warned.Add("mask")) _log.Warn($"keyboard hook: the modifier mask was not sent ({Marshal.GetLastWin32Error()})");
            if (d.Fired is { } fired) Pressed?.Invoke(fired);
            if (d.Swallow) return (IntPtr)1;
        }
        catch (Exception e)
        {
            // The type and message only: never the key.
            if (_warned.Add(e.GetType().Name)) _log.Warn($"keyboard hook callback: {e.GetType().Name}: {e.Message}");
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Re-installs the hook now rather than at the next periodic re-arm; for use after a resume, an unlock or a
    /// long GC pause, any of which can make Windows remove it.</summary>
    public void Rearm()
    {
        if (_threadId != 0) PostThreadMessageW(_threadId, RearmMessage, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        _rearm?.Dispose();
        if (_threadId != 0) PostThreadMessageW(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
    }
}
