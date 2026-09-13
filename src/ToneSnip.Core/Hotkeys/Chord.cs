namespace ToneSnip.Core.Hotkeys;

/// <summary>A key plus modifier flags, written "Ctrl+Shift+PrintScreen". <see cref="None"/> means unbound.</summary>
public readonly record struct Chord(int VirtualKey, bool Ctrl, bool Shift, bool Alt, bool Win)
{
    public static readonly Chord None = default;
    public bool IsNone => VirtualKey == 0;
    public bool HasModifier => Ctrl || Shift || Alt || Win;

    public bool Matches(int vk, bool ctrl, bool shift, bool alt, bool win)
        => !IsNone && vk == VirtualKey && ctrl == Ctrl && shift == Shift && alt == Alt && win == Win;

    public static bool TryParse(string? text, out Chord chord)
    {
        chord = None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        bool ctrl = false, shift = false, alt = false, win = false; int vk = 0;
        foreach (string raw in text.Split('+', ','))
        {
            string part = raw.Trim();
            if (part.Length == 0) return false;
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": ctrl = true; continue;
                case "shift": shift = true; continue;
                case "alt": alt = true; continue;
                case "win": case "windows": win = true; continue;
            }
            if (vk != 0 || !KeyNames.TryGetCode(part, out vk)) return false;
        }
        if (vk == 0) return false;
        chord = new Chord(vk, ctrl, shift, alt, win);
        return true;
    }

    public static Chord Parse(string text)
        => TryParse(text, out Chord c) ? c : throw new FormatException($"'{text}' is not a hotkey chord");

    public override string ToString()
    {
        if (IsNone) return "";
        var parts = new List<string>(5);
        if (Win) parts.Add("Win");
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        parts.Add(KeyNames.NameOf(VirtualKey));
        return string.Join("+", parts);
    }
}
