using ToneSnip.Core.Capture;

namespace ToneSnip.App;

/// <summary>What a launch asked for: `--snip &lt;mode&gt; [--delay n]`, `--settings`, or nothing (run in the tray).
/// <para>The harness parameters exist only in the debug build; in the shipping app their flags are reported as
/// unknown, which <c>Program</c> refuses.</para></summary>
#if TONESNIP_HARNESS
/// <param name="ScreenshotDir">`--screenshots &lt;dir&gt;`: renders every window to PNG there and exits.</param>
/// <param name="ScreenshotTheme">`--theme dark|light|both`; null means both.</param>
/// <param name="LeakTest">`--leaktest`: opens and closes every window five times and reports what survived.</param>
/// <param name="HoldWindow">`--hold &lt;window&gt;`: opens one window for real and leaves it up so `winapp ui` can
/// drive it. <c>Screenshots.HoldWindows</c> is the list.</param>
/// <param name="HoldSeconds">How long `--hold` waits before dismissing the window. Default 30.</param>
/// <param name="HoldFlipTheme">`--flip-theme`: half way through a `--hold`, apply the other theme to the window
/// (<c>Screenshots.ArmThemeFlip</c>). The console prints `hold: theme flipped &lt;from&gt; -&gt; &lt;to&gt;` when it
/// happens, which an outside UIA driver keys on.</param>
/// <param name="MemTest">`--memtest [cycles]`: runs the snip lifecycle end to end and weighs the process.</param>
/// <param name="MemTestCycles">Cycles for `--memtest`. Default 6.</param>
/// <remarks>Harness modes are never forwarded to a running instance: each runs before the mutex.</remarks>
#endif
public sealed record StartupCommand(SnipMode? Mode, int? Delay, bool OpenSettings, bool OpenHistory = false
#if TONESNIP_HARNESS
                                    , string? ScreenshotDir = null, string? ScreenshotTheme = null, bool LeakTest = false,
                                    string? HoldWindow = null, int HoldSeconds = 30,
                                    bool MemTest = false, int MemTestCycles = 6, bool HoldFlipTheme = false
#endif
                                    )
{
    public bool IsEmpty => Mode == null && !OpenSettings && !OpenHistory;
}

public static class CommandLine
{
    /// <summary>`--help` or `-h` anywhere on the line, case-insensitively like <see cref="Parse"/>. Answered before
    /// anything else, so not part of <see cref="StartupCommand"/>.</summary>
    public static bool WantsHelp(string[] args)
        => args.Any(a => a.Equals("--help", StringComparison.OrdinalIgnoreCase) || a.Equals("-h", StringComparison.OrdinalIgnoreCase));

    /// <summary>The prefix the Windows App SDK puts on arguments for a Windows activation, such as
    /// `----AppNotificationActivated:&lt;payload&gt;` for a toast click. Read by <c>GetActivatedEventArgs()</c>, not
    /// by this parser.</summary>
    private const string ActivationPrefix = "----";

    private const string NotificationActivation = "AppNotificationActivated";

    /// <summary>
    /// True for an argument Windows put on the line rather than an option of this app, so it is not reported as
    /// unknown. Every option here begins with exactly two dashes (<c>-h</c> is a <c>case</c> label); Windows writes the
    /// SDK's four-dash sentinel or COM's single-dash form (<c>-Embedding</c>).
    /// <para>A single-dash typo therefore passes silently, which is safe because a second launch whose line reduces to
    /// nothing exits without forwarding (see <c>Program.Main</c>).</para>
    /// </summary>
    public static bool IsForeignArgument(string arg)
        => arg.StartsWith(ActivationPrefix, StringComparison.Ordinal)
        || !arg.StartsWith("--", StringComparison.Ordinal);

    /// <summary>True when launched by a toast click, which must register the notification COM activator before
    /// <c>GetActivatedEventArgs</c> reports it as anything but Launch.</summary>
    public static bool IsToastActivation(string[] args)
        => args.Any(a => a.Contains(NotificationActivation, StringComparison.OrdinalIgnoreCase));

#if TONESNIP_HARNESS
    /// <summary>What `--theme` accepts; "both" is explicit so a typo is refused rather than meaning both.</summary>
    internal static readonly string[] ShotThemes = { "dark", "light", "both" };
#endif

    /// <summary>The exe name from <c>&lt;AssemblyName&gt;</c>, so the debug build's help names itself. Not
    /// <c>Environment.ProcessPath</c>, which differs when the exe has been copied or renamed.</summary>
    private static string ExeName => (typeof(CommandLine).Assembly.GetName().Name ?? "tonesnip") + ".exe";

    /// <summary>The text `--help` prints. Kept beside <see cref="Parse"/> so the two are edited together.</summary>
    public static string HelpText()
    {
        var text = new System.Text.StringBuilder();
        // ASCII throughout: this goes to a console whose code page is whatever the shell is set to.
        text.AppendLine($"ToneSnip {typeof(CommandLine).Assembly.GetName().Version?.ToString(3)} - HDR-aware screen snipping for Windows 11");
        text.AppendLine();
        text.AppendLine($"usage: {ExeName} [options]");
        text.AppendLine();
        // Mirrors SnipModes.Names, wrapped by hand.
        text.AppendLine("  --snip <mode>      take a snip now; mode is region (or rectangle), window,");
        text.AppendLine("                     fullscreen, freeform, fullScreenAll or activeWindow");
        text.AppendLine($"  --delay <seconds>  count down before the snip starts (with --snip); 0 to {MaxDelaySeconds}");
        text.AppendLine("  --settings         open the settings window");
        text.AppendLine("  --history          open the Recent flyout");
        text.AppendLine("  --help, -h         this text");
#if TONESNIP_HARNESS
        text.AppendLine();
        text.AppendLine($"{ExeName} only - the harness modes. Each runs before the single-instance mutex,");
        text.AppendLine("claims nothing, forwards nothing, and waits out a snip already in flight:");
        text.AppendLine("  --selftest         run the self-test and exit; --no-capture skips the capture checks,");
        text.AppendLine("                     --strict-hashes fails on a rasterizer fingerprint change");
        text.AppendLine("  --screenshots <dir>  photograph every window into <dir> and exit");
        text.AppendLine("  --theme <t>        dark, light or both, for --screenshots and --hold");
        text.AppendLine("  --leaktest         open and close every window five times and count what survived");
        text.AppendLine("  --memtest [cycles] run the snip lifecycle end to end and weigh the process (default 6)");
        text.AppendLine("  --hold <window> [seconds]  open one window for real and leave it up for `winapp ui`");
        text.AppendLine("                     (default 30). flyout-row, flyout-grid, settings-<page>, editor,");
        text.AppendLine("                     editor-hdr, toolbar, toolbar-annotate, countdown, textentry,");
        text.AppendLine("                     toast-saved, toast-copied, toast-dwell");
        text.AppendLine("  --flip-theme       half way through a --hold, apply the other theme to the live window");
#endif
        text.AppendLine();
        text.AppendLine("With no options ToneSnip starts in the notification area and waits for its hotkeys.");
        text.Append("When it is already running, an option is forwarded to that instance instead.");
        return text.ToString();
    }

    public static StartupCommand Parse(string[] args) => Parse(args, out _);

    /// <summary>The longest `--delay` accepted; it exists to refuse typos rather than to limit real use.</summary>
    private const int MaxDelaySeconds = 3600;

    /// <param name="errors">One line per argument that cannot be acted on: an unknown option, or a known one with a
    /// missing or invalid value. <c>Program</c> refuses the launch; <c>HostPipe</c> ignores them, since its line was
    /// written by <see cref="ToArgs"/>.</param>
    public static StartupCommand Parse(string[] args, out IReadOnlyList<string> errors)
    {
        var faults = new List<string>();
        errors = faults;
        SnipMode? mode = null; int? delay = null; bool settings = false, history = false;
#if TONESNIP_HARNESS
        bool leaks = false, memory = false, flip = false;
        string? shots = null, theme = null, hold = null;
        int holdSeconds = 30, memCycles = 6;
#endif
        for (int i = 0; i < args.Length; i++)
        {
            // Lowercased for the switch only; values keep their original case.
            string flag = args[i].ToLowerInvariant();
            switch (flag)
            {
                case "--snip":
                {
                    string wants = $"a snip mode ({string.Join(", ", SnipModes.Names)})";
                    if (Value(args, ref i, flag, wants, faults, out string? snip))
                    {
                        if (SnipModes.TryParse(snip, out SnipMode m)) mode = m;
                        else faults.Add(Not(flag, snip!, wants));
                    }
                    break;
                }
                case "--delay":
                {
                    string wants = $"a whole number of seconds from 0 to {MaxDelaySeconds}";
                    if (Value(args, ref i, flag, wants, faults, out string? seconds))
                    {
                        if (int.TryParse(seconds, out int d) && d >= 0 && d <= MaxDelaySeconds) delay = d;
                        else faults.Add(Not(flag, seconds!, wants));
                    }
                    break;
                }
                case "--settings": settings = true; break;
                case "--history": history = true; break;
                // Recognised so it is not reported as unknown; read early in Program.
                case "--help": case "-h": break;
#if TONESNIP_HARNESS
                case "--screenshots":
                    if (Value(args, ref i, flag, "a folder to write the PNGs into", faults, out string? dir)) shots = dir;
                    break;
                case "--theme":
                {
                    string wants = $"a theme ({string.Join(", ", ShotThemes)})";
                    if (Value(args, ref i, flag, wants, faults, out string? t))
                    {
                        if (ShotThemes.Contains(t!.ToLowerInvariant())) theme = t.ToLowerInvariant();
                        else faults.Add(Not(flag, t, wants));
                    }
                    break;
                }
                case "--leaktest": leaks = true; break;
                case "--memtest":
                    memory = true;
                    if (Count(args, ref i, flag, "cycles", faults, out int mc)) memCycles = mc;
                    break;
                case "--hold":
                {
                    string wants = $"a window ({string.Join(", ", Screenshots.HoldWindows)})";
                    if (Value(args, ref i, flag, wants, faults, out string? window))
                    {
                        if (Screenshots.HoldWindows.Contains(window!.ToLowerInvariant())) hold = window.ToLowerInvariant();
                        else faults.Add(Not(flag, window, wants));
                    }
                    if (Count(args, ref i, flag, "seconds", faults, out int hs)) holdSeconds = hs;
                    break;
                }
                case "--flip-theme": flip = true; break;
                // Read by Program directly from args.
                case "--selftest": case "--no-capture": case "--strict-hashes": break;
#endif
                default:
                    // Activation arguments carry a payload, so they cannot be `case` labels.
                    if (IsForeignArgument(args[i])) break;
                    faults.Add($"unknown option: {args[i]}");
                    break;
            }
        }
#if TONESNIP_HARNESS
        return new StartupCommand(mode, delay, settings, history, shots, theme, leaks, hold, holdSeconds, memory, memCycles, flip);
#else
        return new StartupCommand(mode, delay, settings, history);
#endif

        // The value after an option, or a fault. Another option counts as no value: `--snip --settings` is a missing
        // mode. A lone '-' can still start a value.
        static bool Value(string[] args, ref int i, string flag, string wants, List<string> faults, out string? value)
        {
            value = i + 1 < args.Length ? args[i + 1] : null;
            if (value == null || value.StartsWith("--", StringComparison.Ordinal))
            {
                value = null;
                faults.Add($"{flag} needs {wants}");
                return false;
            }
            i++;
            return true;
        }

#if TONESNIP_HARNESS
        // The optional trailing count for `--memtest [cycles]` and `--hold <window> [seconds]`. Absent keeps the
        // default; present and not a non-negative number is a fault.
        static bool Count(string[] args, ref int i, string flag, string what, List<string> faults, out int count)
        {
            count = 0;
            string? next = i + 1 < args.Length ? args[i + 1] : null;
            if (next == null || next.StartsWith("--", StringComparison.Ordinal)) return false;
            i++;
            if (int.TryParse(next, out count) && count >= 0) return true;
            faults.Add(Not(flag, next, $"a number of {what}, 0 or more"));
            return false;
        }
#endif
    }

    private static string Not(string flag, string value, string wants) => $"{flag}: '{value}' is not {wants}";

    public static string[] ToArgs(StartupCommand c)
    {
        var a = new List<string>();
        if (c.Mode != null) { a.Add("--snip"); a.Add(SnipModes.Name(c.Mode.Value)); }
        if (c.Delay != null) { a.Add("--delay"); a.Add(c.Delay.Value.ToString()); }
        if (c.OpenSettings) a.Add("--settings");
        if (c.OpenHistory) a.Add("--history");
        return a.ToArray();
    }
}
