namespace ToneSnip.Core.Extract;

/// <summary>One recognised word and its box, in the pixels of the image that was recognised.</summary>
public readonly record struct OcrWord(string Text, double X, double Y, double Width, double Height);

/// <summary>One recognised line, its words in reading order.</summary>
public sealed record OcrLine(IReadOnlyList<OcrWord> Words)
{
    public double Top => Words.Count == 0 ? 0 : Words.Min(w => w.Y);
    public double Bottom => Words.Count == 0 ? 0 : Words.Max(w => w.Y + w.Height);
    public double Height => Bottom - Top;
}

/// <summary>
/// Turns what the OCR engine recognised into the plain text "Copy text" puts on the clipboard. Kept out of the app so
/// the joining rules are testable without an engine: Windows.Media.Ocr hands back lines of words, and its own line
/// text puts a space between every pair of words, which reads wrongly for Chinese and Japanese.
/// </summary>
public static class OcrText
{
    /// <summary>A gap between two lines taller than this share of their mean height starts a new paragraph, which is
    /// written as a blank line.</summary>
    public const double ParagraphGap = 0.8;

    /// <summary>The lines as text: words joined by a space (none between two CJK characters), lines by CRLF, and a
    /// blank line where the vertical gap marks a new paragraph. Empty when nothing was recognised.</summary>
    public static string Join(IReadOnlyList<OcrLine> lines)
    {
        var sb = new System.Text.StringBuilder();
        OcrLine? previous = null;
        foreach (OcrLine line in lines)
        {
            string text = JoinWords(line.Words);
            if (text.Length == 0) continue;
            if (previous != null)
            {
                sb.Append("\r\n");
                double gap = line.Top - previous.Bottom, mean = (line.Height + previous.Height) / 2;
                if (mean > 0 && gap > mean * ParagraphGap) sb.Append("\r\n");
            }
            sb.Append(text);
            previous = line;
        }
        return sb.ToString();
    }

    /// <summary>One line's words, with no space where both sides of the join are CJK characters.</summary>
    public static string JoinWords(IReadOnlyList<OcrWord> words)
    {
        var sb = new System.Text.StringBuilder();
        foreach (OcrWord w in words)
        {
            string t = w.Text.Trim();
            if (t.Length == 0) continue;
            if (sb.Length > 0 && !(IsCjk(sb[^1]) && IsCjk(t[0]))) sb.Append(' ');
            sb.Append(t);
        }
        return sb.ToString();
    }

    /// <summary>Scripts written without spaces between words: Han, kana and CJK punctuation. Hangul is left out, since
    /// Korean does space its words.</summary>
    public static bool IsCjk(char c)
        => c is >= '　' and <= 'ヿ'      // CJK punctuation, Hiragana, Katakana
            or >= '㐀' and <= '䶿'       // CJK extension A
            or >= '一' and <= '鿿'       // CJK unified ideographs
            or >= '豈' and <= '﫿'       // compatibility ideographs
            or >= '＀' and <= '￯';      // full-width forms

    /// <summary>
    /// How much to scale a <paramref name="width"/> × <paramref name="height"/> selection before recognition. The
    /// engine reads screen text best at about twice its on-screen size, so a small selection is enlarged (three times
    /// when it is only a line or two tall), never past <paramref name="maxDimension"/>, the engine's limit; one larger
    /// than the limit is shrunk to fit it.
    /// </summary>
    public static double ScaleFor(int width, int height, int maxDimension)
    {
        if (width <= 0 || height <= 0 || maxDimension <= 0) return 1;
        int longest = Math.Max(width, height);
        if (longest > maxDimension) return (double)maxDimension / longest;
        double want = height < 64 ? 3 : (long)width * height <= 1_500_000 ? 2 : 1;
        return Math.Max(1, Math.Min(want, Math.Floor((double)maxDimension / longest)));
    }
}
