using System.Runtime.InteropServices;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Hotkeys;
using ToneSnip.Windows.Interop;

namespace ToneSnip.Windows.Hotkeys;

/// <summary>
/// Low-level keyboard hook that fires when a bound chord is pressed. Every decision is <see cref="HotkeyFilter"/>'s:
/// this class is the native plumbing around it. No key is recorded or logged, except a press of a bound chord that the
/// hook swallowed (<see cref="LogSwallowed"/>).
/// The hook lives on its own message-loop thread so UI work can never stall it (Windows silently removes
/// a low-level hook whose thread stops answering), and it is re-armed every few minutes as a safety net.
/// </summary>
public sealed partial class KeyboardHook : IDisposable
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
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool UnhookWindowsHookEx(IntPtr hhk);
    [LibraryImport("user32.dll")] private static partial IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [LibraryImport("user32.dll")] private static partial short GetAsyncKeyState(int vKey);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetMessageW(out Msg msg, IntPtr hWnd, uint min, uint max);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial uint SendInput(uint count, [In] Input[] inputs, int size);

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
    /// <summary>When true, matching key-downs are swallowed so no other app sees them. Read on the hook thread.</summary>
    public bool Swallow { get => _swallow; set => _swallow = value; }
    private volatile bool _swallow;
    /// <summary>Whether a binding applies right now (<see cref="HotkeyFilter.IsActive"/>); null means every binding does.</summary>
    public Func<HotkeyBinding, bool>? IsActive { get; set; }
    /// <summary>
    /// While set, every non-modifier key-down and key-up is reported here (chord, isDown) and swallowed, and no binding
    /// fires. Used by the settings hotkey recorder so PrintScreen and friends never reach Windows while recording.
    /// </summary>
    public Action<Chord, bool>? Recorder { get => _recorder; set => _recorder = value; }
    private volatile Action<Chord, bool>? _recorder;
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
        _threadId = Kernel32.GetCurrentThreadId();
        Arm(null);
        ready.Set();
        while (GetMessageW(out Msg msg, IntPtr.Zero, 0, 0))
        {
            if (msg.message == RearmMessage) Arm(Interlocked.Exchange(ref _rearmReason, null));
            if (msg.message == WmQuit) break;
        }
        Disarm();
    }

    /// <param name="reason">Why the re-arm was asked for, logged at Info; null for the periodic safety net, which is
    /// logged at Debug only.</param>
    private void Arm(string? reason)
    {
        Disarm();
        _hook = SetWindowsHookExW(WhKeyboardLl, _proc, Kernel32.GetModuleHandleW(null), 0);
        _installed = _hook != IntPtr.Zero;
        if (!_installed) { _log.Error($"keyboard hook failed: {Marshal.GetLastWin32Error()}"); return; }
        Interlocked.Exchange(ref _armedAt, DateTime.Now.Ticks);
        Interlocked.Increment(ref _arms);
        // Info on the first arm so the log always shows the hook came up, and on a re-arm someone asked for; Debug on
        // the periodic re-arms.
        if (!_everArmed) { _everArmed = true; _log.Info($"keyboard hook armed, {_bindings.Count} bindings"); }
        else if (reason != null) _log.Info($"keyboard hook re-armed ({reason}), {_bindings.Count} bindings");
        else _log.Debug($"keyboard hook re-armed, {_bindings.Count} bindings");
    }

    /// <summary>Whether the hook is installed right now.</summary>
    public bool Armed => _installed;
    /// <summary>When the hook was last installed, by the first arm or any re-arm; null before the first.</summary>
    /// <remarks>Kept as ticks: written on the hook thread and read on others, and a long cannot be read torn.</remarks>
    public DateTime? ArmedAt => Interlocked.Read(ref _armedAt) is var t and not 0 ? new DateTime(t) : null;
    private long _armedAt;
    /// <summary>How many times the hook has been installed, re-arms included; a re-arm that went through bumps it.</summary>
    public int Arms => Volatile.Read(ref _arms);
    private int _arms;
    /// <summary>The bindings the hook matches, unbound chords left out.</summary>
    public int BindingCount => _bindings.Count;
    /// <summary>Why the next re-arm was asked for, taken by the hook thread when it runs it.</summary>
    private string? _rearmReason;

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
            int msg = (int)wParam.ToInt64();
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
            if (d.Swallow && d.Matched is { } matched) LogSwallowed(matched, fired: d.Fired != null);
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

    /// <summary>
    /// Logs a bound chord the hook swallowed, from a pool thread: the hook thread must never wait on the disk, or a
    /// slow write could overrun LowLevelHooksTimeout and Windows would remove the hook. Only bound chords reach here,
    /// never other keys. With the app's own "picked up" line this shows where a dead hotkey stopped: a press with no
    /// line at all never reached the hook, and one swallowed but never picked up was lost between the hook and the UI
    /// thread.
    /// </summary>
    private void LogSwallowed(HotkeyBinding binding, bool fired)
    {
        long pressed = Environment.TickCount64;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            long queued = Environment.TickCount64 - pressed;
            _log.Info(fired
                ? $"hotkey {binding.Action}: the hook swallowed {binding.Chord} and fired it{(queued > 1000 ? $" (logged {queued} ms later)" : "")}"
                : $"hotkey {binding.Action}: the hook swallowed {binding.Chord} without firing it: within {HotkeyFilter.DebounceMs} ms of its last press");
        }, null);
    }

    /// <summary>Re-installs the hook now rather than at the next periodic re-arm; for use after a resume, an unlock or a
    /// long GC pause, any of which can make Windows remove it, and when the user asks for it from the tray.</summary>
    /// <param name="reason">Said in the log line the re-arm writes, at Info. Null logs it at Debug, like the periodic
    /// re-arm.</param>
    public void Rearm(string? reason = null)
    {
        if (reason != null) Volatile.Write(ref _rearmReason, reason);
        if (_threadId != 0 && !PostThreadMessageW(_threadId, RearmMessage, IntPtr.Zero, IntPtr.Zero)) _log.Warn($"keyboard hook: the re-arm was not posted ({Marshal.GetLastWin32Error()})");
    }

    public void Dispose()
    {
        _rearm?.Dispose();
        if (_threadId != 0) PostThreadMessageW(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
    }
}
