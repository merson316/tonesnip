namespace ToneSnip.Core.Hotkeys;

/// <summary>The modifier keys held when a key event arrived, as the hook reads them from the async key state.</summary>
public readonly record struct KeyMods(bool Ctrl, bool Shift, bool Alt, bool Win)
{
    public static readonly KeyMods None = default;
}

/// <summary>What the hook does with one key event: swallow it or pass it on, and the binding it fired, if any.
/// <paramref name="MaskModifier"/> asks the hook to inject a no-op key while Alt or Win is held: with the chord's own
/// key swallowed, the foreground app would otherwise see a lone Alt tap (its menu bar) or Win tap (Start).</summary>
public readonly record struct KeyDecision(bool Swallow, HotkeyBinding? Fired, bool MaskModifier = false);

/// <summary>
/// The low-level keyboard hook's decisions, separated from the native callback so they can be unit-tested: which events
/// are swallowed, which press fires a binding, what the settings recorder is told. Single-threaded: only the hook
/// thread calls it. No key is remembered beyond while it is held.
/// </summary>
public sealed class HotkeyFilter(Func<long> nowMs)
{
    /// <summary>Two presses of a binding closer together than this fire once.</summary>
    public const long DebounceMs = 250;

    /// <summary>
    /// A key-down for a key already held is auto-repeat unless the previous key-down is older than this. Windows' longest
    /// repeat delay is 1 s, so a longer gap means a key-up was lost (low-level hooks miss input on the secure desktop
    /// and when a callback times out) and the press is treated as fresh.
    /// </summary>
    public const long StaleHoldMs = 1100;

    private readonly Dictionary<int, long> _held = new();          // key -> time of its last key-down
    private readonly HashSet<int> _swallowed = new();              // held keys whose key-down was swallowed
    /// <summary>When each binding last fired; per binding so an unrelated press (such as Escape) does not debounce
    /// the next hotkey.</summary>
    private readonly Dictionary<HotkeyBinding, long> _lastFire = new();

    public IReadOnlyList<HotkeyBinding> Bindings { get; set; } = Array.Empty<HotkeyBinding>();
    public bool Paused { get; set; }
    /// <summary>When true, a matching key-down is swallowed so no other app sees it.</summary>
    public bool Swallow { get; set; }
    /// <summary>Per-binding override of <see cref="Swallow"/>; null means use the flag.</summary>
    public Func<HotkeyBinding, bool>? ShouldSwallow { get; set; }
    /// <summary>Whether a binding applies right now; one that does not is not matched at all — no fire, no swallow, no
    /// debounce. Escape's cancelCountdown applies only while a countdown is running. Null means every binding does.</summary>
    public Func<HotkeyBinding, bool>? IsActive { get; set; }
    /// <summary>While set, every non-modifier key-down and key-up is reported here (chord, isDown) and swallowed, and no
    /// binding fires.</summary>
    public Action<Chord, bool>? Recorder { get; set; }

    public KeyDecision OnKey(int vk, bool isDown, bool injected, KeyMods mods)
    {
        long now = nowMs();
        bool repeat = false;
        if (isDown)
        {
            repeat = _held.TryGetValue(vk, out long previous) && now - previous <= StaleHoldMs;
            if (!repeat) _swallowed.Remove(vk);                    // a fresh press clears state left by a lost key-up
            _held[vk] = now;
        }
        else _held.Remove(vk);

        // Synthetic input is let through untouched, and does not fire: this app injects none, and a binding fired by
        // another program's SendInput is not the user pressing the key.
        if (injected) { if (!isDown) _swallowed.Remove(vk); return default; }

        if (IsModifier(vk)) return default;                      // modifiers pass, so their state stays readable

        if (Recorder is { } recorder)
        {
            if (!isDown) _swallowed.Remove(vk);
            if (!repeat) recorder(new Chord(vk, mods.Ctrl, mods.Shift, mods.Alt, mods.Win), isDown);
            return new KeyDecision(true, null);
        }

        // The rest of a swallowed press: its auto-repeats and its key-up. Windows never saw the key go down, so it must
        // not see it repeat or come up either.
        if (_swallowed.Contains(vk))
        {
            if (!isDown) _swallowed.Remove(vk);
            return new KeyDecision(true, null);
        }

        if (!isDown || repeat || Paused) return default;
        foreach (HotkeyBinding b in Bindings)
        {
            if (!b.Chord.Matches(vk, mods.Ctrl, mods.Shift, mods.Alt, mods.Win)) continue;
            if (IsActive != null && !IsActive(b)) continue;
            HotkeyBinding? fired = null;
            if (!_lastFire.TryGetValue(b, out long last) || now - last > DebounceMs) { _lastFire[b] = now; fired = b; }
            bool swallow = ShouldSwallow?.Invoke(b) ?? Swallow;
            if (swallow) _swallowed.Add(vk);
            return new KeyDecision(swallow, fired, swallow && (mods.Alt || mods.Win));
        }
        return default;
    }

    private static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;
}
