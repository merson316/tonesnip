namespace ToneSnip.Core.Hotkeys;

/// <summary>A chord bound to a named action ("region", "window", "cancelCountdown", ...).</summary>
public sealed record HotkeyBinding(Chord Chord, string Action);

public static class HotkeyConflicts
{
    /// <summary>Pairs of bindings that share a non-empty chord, in input order.</summary>
    public static List<(HotkeyBinding A, HotkeyBinding B)> Find(IEnumerable<HotkeyBinding> bindings)
    {
        var list = bindings.Where(b => !b.Chord.IsNone).ToList();
        var result = new List<(HotkeyBinding, HotkeyBinding)>();
        for (int i = 0; i < list.Count; i++)
            for (int j = i + 1; j < list.Count; j++)
                if (list[i].Chord == list[j].Chord) result.Add((list[i], list[j]));
        return result;
    }
}
