using ToneSnip.Core.Config;

namespace ToneSnip.App;

/// <summary>
/// The fixed data every shot-taking harness mode runs with: the same ten history rows, the same settings and one
/// frozen instant for relative ages, so runs are comparable across machines and over time.
/// <para>Nothing here is written to <see cref="AppPaths.Dir"/>: rows go through <c>SnipHistory.SeedForHarness</c>
/// (in memory only) and settings through <c>App.ApplySettings</c>, which is an in-memory update in screenshot
/// mode.</para>
/// </summary>
internal static class HarnessData
{
    /// <summary>
    /// The instant a shot is taken "at". Row ages are measured back from it, and <see cref="HistoryRowNowUtc"/>
    /// substitutes it for <c>DateTime.UtcNow</c> so "3 h ago" reads the same in every run.
    /// <para>Midday UTC, so the local-time date shown for the oldest row is the same day from UTC-12 to UTC+11.</para>
    /// </summary>
    internal static readonly DateTime ReferenceUtc = new(2026, 3, 14, 12, 9, 26, DateTimeKind.Utc);

    /// <summary>Non-null once <see cref="Freeze"/> has run (shot-taking modes only); <c>HistoryRow</c> reads it in
    /// place of the wall clock.</summary>
    internal static DateTime? HistoryRowNowUtc { get; private set; }

    /// <summary>Freezes the clock the history subtitles are measured against. Called by the history seed.</summary>
    internal static void Freeze() => HistoryRowNowUtc = ReferenceUtc;

    /// <summary>
    /// The settings every shot is taken under: defaults, except a save folder on a fictional profile, so the
    /// Location row never shows a real user name. Nothing is written to that path.
    /// </summary>
    internal static SnipSettings Settings { get; } = new SnipSettings
    {
        SaveFolder = @"C:\Users\Sample\Pictures\Screenshots",
    }.Sanitized(out _);

    /// <summary>
    /// One seeded snip. <paramref name="Age"/> is measured back from <see cref="ReferenceUtc"/>;
    /// <paramref name="HdrFile"/> is the format of the HDR copy ("jxr", "png", "jpeg") or null for none;
    /// <paramref name="Hdr"/> is whether the snip came off an HDR display, independent of having a copy.
    /// </summary>
    /// <param name="Copied">Copied to the clipboard and never saved: no path, so the row reads "Copied only".</param>
    /// <param name="Lost">A saved snip whose file is not on disk: gone from its folder, or gone with its folder, so the
    /// probe drops the row; or on a drive that is not there, so the row is kept but hidden.</param>
    /// <param name="Kind">The kind tag: a window snip, or (with <paramref name="Copied"/>, as they are never saved) a
    /// pin or a Copy text snip from the snip screen.</param>
    /// <param name="Text">A Copy text snip's recognised text, which becomes its title.</param>
    internal readonly record struct Row(TimeSpan Age, int Width, int Height, bool Hdr, string? HdrFile, bool Copied,
                                        Core.Output.HistoryKind Kind = Core.Output.HistoryKind.None, string? Text = null, Lost Lost = Lost.No);

    /// <summary>What has become of a seeded row's file (<see cref="Row.Lost"/>).</summary>
    internal enum Lost { No, Gone, FolderGone, Offline }

    /// <summary>
    /// The thirteen rows, newest first. The first ten are shown; together they cover every branch of
    /// <c>HistoryList.TimeAgo</c>, both badges, all three HDR-copy tags, the three kind tags, an HDR capture saved as
    /// SDR only and a clipboard-only snip; the scrolled shots need six rows and the armed-delete shot four. The last
    /// three are never shown: one whose file was deleted (the probe drops it, and its HDR copy stays on disk), one whose
    /// folder was deleted with it (dropped too) and one on a drive letter that is not in use (kept but hidden).
    /// <para>The newest row (1280 x 720) is the one the editor and toast shots open.</para>
    /// </summary>
    internal static readonly Row[] Rows =
    {
        new(TimeSpan.FromSeconds(40),                  1280,  720, Hdr: false, HdrFile: null,   Copied: false),
        new(TimeSpan.FromMinutes(6),                   1920, 1080, Hdr: true,  HdrFile: "jxr",  Copied: false),
        new(TimeSpan.FromMinutes(24),                  1600,  900, Hdr: true,  HdrFile: null,   Copied: false, Kind: Core.Output.HistoryKind.Window),
        new(TimeSpan.FromMinutes(100),                  900,  500, Hdr: true,  HdrFile: null,   Copied: true,  Kind: Core.Output.HistoryKind.Pin),
        new(TimeSpan.FromMinutes(190),                 2560, 1080, Hdr: true,  HdrFile: "png",  Copied: false),
        new(TimeSpan.FromMinutes(570),                  800,  600, Hdr: false, HdrFile: null,   Copied: true),
        new(TimeSpan.FromHours(30),                    1440,  900, Hdr: true,  HdrFile: "jpeg", Copied: false),
        new(TimeSpan.FromHours(50),                     640,  200, Hdr: false, HdrFile: null,   Copied: true,  Kind: Core.Output.HistoryKind.Text,
            Text: "Quarterly figures, draft 3\r\nRevenue up 4 % on the year"),
        new(TimeSpan.FromHours(76),                    1024,  768, Hdr: false, HdrFile: null,   Copied: false),
        new(TimeSpan.FromDays(40) + TimeSpan.FromHours(3), 640, 480, Hdr: false, HdrFile: null, Copied: false),
        new(TimeSpan.FromDays(50),                      800,  450, Hdr: true,  HdrFile: "jxr",  Copied: false, Lost: Lost.Gone),
        new(TimeSpan.FromDays(60),                      720,  540, Hdr: false, HdrFile: null,   Copied: false, Lost: Lost.FolderGone),
        new(TimeSpan.FromDays(70),                      600,  400, Hdr: false, HdrFile: null,   Copied: false, Lost: Lost.Offline),
    };

    /// <summary>
    /// A seeded row's file name, in the app's convention (<c>Core.Output.FileNaming.Build</c>) but from the UTC
    /// instant, so it is the same string in every time zone.
    /// </summary>
    internal static string FileName(Row row)
        => Core.Output.FileNaming.Build(ReferenceUtc - row.Age, "png");

    /// <summary>The instant a row was taken.</summary>
    internal static DateTime TakenUtc(Row row) => ReferenceUtc - row.Age;
}
