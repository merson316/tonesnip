namespace ToneSnip.Core.Hotkeys;

/// <summary>Virtual-key names accepted in hotkey chords.</summary>
public static class KeyNames
{
    private static readonly Dictionary<string, int> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PrintScreen"] = 0x2C, ["Snapshot"] = 0x2C, ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23,
        ["PageUp"] = 0x21, ["Prior"] = 0x21, ["PageDown"] = 0x22, ["Next"] = 0x22, ["Pause"] = 0x13, ["Scroll"] = 0x91, ["ScrollLock"] = 0x91,
        ["Space"] = 0x20, ["Tab"] = 0x09, ["Escape"] = 0x1B, ["Enter"] = 0x0D, ["Return"] = 0x0D, ["Back"] = 0x08, ["Backspace"] = 0x08,
        ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27,
        ["NumPad0"] = 0x60, ["NumPad1"] = 0x61, ["NumPad2"] = 0x62, ["NumPad3"] = 0x63, ["NumPad4"] = 0x64,
        ["NumPad5"] = 0x65, ["NumPad6"] = 0x66, ["NumPad7"] = 0x67, ["NumPad8"] = 0x68, ["NumPad9"] = 0x69,
        ["Multiply"] = 0x6A, ["Add"] = 0x6B, ["Subtract"] = 0x6D, ["Decimal"] = 0x6E, ["Divide"] = 0x6F,
        ["Oemtilde"] = 0xC0, ["OemMinus"] = 0xBD, ["Oemplus"] = 0xBB, ["OemOpenBrackets"] = 0xDB, ["OemCloseBrackets"] = 0xDD,
        ["OemPipe"] = 0xDC, ["OemSemicolon"] = 0xBA, ["OemQuotes"] = 0xDE, ["Oemcomma"] = 0xBC, ["OemPeriod"] = 0xBE, ["OemQuestion"] = 0xBF,
    };

    private static readonly Dictionary<int, string> Names = BuildNames();

    private static Dictionary<int, string> BuildNames()
    {
        var names = new Dictionary<int, string>();
        foreach (var (name, code) in Codes) names.TryAdd(code, name);   // first entry wins: PrintScreen over Snapshot
        return names;
    }

    /// <summary>Resolves a key name: a table entry, a single letter or digit, "D0".."D9", or "F1".."F24".</summary>
    public static bool TryGetCode(string name, out int vk)
    {
        vk = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;
        name = name.Trim();
        if (Codes.TryGetValue(name, out vk)) return true;
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0])) { vk = char.ToUpperInvariant(name[0]); return true; }
        if (name.Length == 2 && (name[0] == 'D' || name[0] == 'd') && char.IsAsciiDigit(name[1])) { vk = name[1]; return true; }
        if ((name[0] == 'F' || name[0] == 'f') && int.TryParse(name.AsSpan(1), out int f) && f is >= 1 and <= 24) { vk = 0x70 + f - 1; return true; }
        return false;
    }

    public static string NameOf(int vk)
    {
        if (Names.TryGetValue(vk, out string? n)) return n;
        if (vk is >= 0x30 and <= 0x39 || vk is >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1);
        return "0x" + vk.ToString("X2");
    }
}
