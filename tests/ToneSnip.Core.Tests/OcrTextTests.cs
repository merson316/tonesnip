using ToneSnip.Core.Extract;
using ToneSnip.Core.Imaging;
using Xunit;

namespace ToneSnip.Core.Tests;

public class OcrTextTests
{
    private static OcrLine Line(double top, double height, params string[] words)
        => new(words.Select((w, i) => new OcrWord(w, i * 40, top, 30, height)).ToList());

    [Fact]
    public void Words_are_joined_by_spaces_and_lines_by_crlf()
        => Assert.Equal("Hello world\r\nsecond line", OcrText.Join(new[] { Line(0, 14, "Hello", "world"), Line(18, 14, "second", "line") }));

    [Fact]
    public void A_tall_gap_between_lines_is_a_paragraph_break()
        => Assert.Equal("One\r\n\r\nTwo", OcrText.Join(new[] { Line(0, 14, "One"), Line(40, 14, "Two") }));

    [Fact]
    public void Cjk_words_are_joined_without_spaces()
        => Assert.Equal("日本語のテキスト", OcrText.JoinWords(new[] { new OcrWord("日本語", 0, 0, 30, 14), new OcrWord("の", 30, 0, 10, 14), new OcrWord("テキスト", 40, 0, 40, 14) }));

    [Fact]
    public void Latin_next_to_cjk_keeps_its_space()
        => Assert.Equal("ToneSnip 日本", OcrText.JoinWords(new[] { new OcrWord("ToneSnip", 0, 0, 60, 14), new OcrWord("日本", 60, 0, 20, 14) }));

    [Fact]
    public void Korean_keeps_its_spaces()
        => Assert.Equal("안녕 세계", OcrText.JoinWords(new[] { new OcrWord("안녕", 0, 0, 20, 14), new OcrWord("세계", 30, 0, 20, 14) }));

    [Fact]
    public void Nothing_recognised_is_empty()
    {
        Assert.Equal("", OcrText.Join(Array.Empty<OcrLine>()));
        Assert.Equal("", OcrText.Join(new[] { new OcrLine(new[] { new OcrWord("  ", 0, 0, 5, 5) }) }));
    }

    [Theory]
    [InlineData(300, 40, 4000, 3)]      // a line of text: three times
    [InlineData(800, 400, 4000, 2)]     // a small panel: twice
    [InlineData(1920, 1080, 4000, 1)]   // a monitor: as it is
    [InlineData(1500, 50, 4000, 2)]     // tall enough for three times, but that would pass the limit
    public void Small_selections_are_enlarged_within_the_limit(int w, int h, int max, double scale)
        => Assert.Equal(scale, OcrText.ScaleFor(w, h, max));

    [Fact]
    public void A_selection_past_the_limit_is_shrunk_to_fit()
        => Assert.Equal(0.5, OcrText.ScaleFor(8000, 2000, 4000));

    [Fact]
    public void Resample_doubles_a_flat_image_without_changing_its_colour()
    {
        var src = new BgraImage(2, 1, new byte[] { 10, 20, 30, 255, 10, 20, 30, 255 });
        BgraImage big = Resample.Scale(src, 2);
        Assert.Equal((4, 2), (big.Width, big.Height));
        for (int i = 0; i < big.Data.Length; i += 4) Assert.Equal(new byte[] { 10, 20, 30, 255 }, big.Data[i..(i + 4)]);
    }

    [Fact]
    public void Resample_interpolates_between_neighbours()
    {
        var src = new BgraImage(2, 1, new byte[] { 0, 0, 0, 255, 200, 200, 200, 255 });
        BgraImage big = Resample.Scale(src, 2);
        // Pixel centres at 0.25 and 0.75 of the way from the first source pixel to the second.
        Assert.Equal(0, big.Data[0]);
        Assert.Equal(50, big.Data[4]);
        Assert.Equal(150, big.Data[8]);
        Assert.Equal(200, big.Data[12]);
        Assert.Same(src, Resample.Scale(src, 1));
    }

    [Fact]
    public void PngInfo_reads_the_size_from_the_header()
    {
        byte[] header = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0x07, 0x80, 0, 0, 0x04, 0x38 };
        Assert.True(PngInfo.TrySize(header, out int w, out int h));
        Assert.Equal((1920, 1080), (w, h));
        Assert.False(PngInfo.TrySize(header.AsSpan(0, 20), out _, out _));
        header[1] = 0;
        Assert.False(PngInfo.TrySize(header, out _, out _));
    }
}
