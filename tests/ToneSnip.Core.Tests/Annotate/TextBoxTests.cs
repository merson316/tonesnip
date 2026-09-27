using ToneSnip.Core.Annotate;
using ToneSnip.Core.Config;
using ToneSnip.Core.Geometry;
using Xunit;

namespace ToneSnip.Core.Tests.Annotate;

/// <summary>The text tool's background box: the style option, the shape it makes, and the setting that remembers it.</summary>
public class TextBoxTests
{
    [Fact]
    public void Ink_on_a_box_is_white_where_it_passes_AA_and_otherwise_the_stronger_of_black_and_white()
    {
        Assert.Equal(0xFF000000u, Style.InkOn(0xFFFFF100));   // yellow
        Assert.Equal(0xFF000000u, Style.InkOn(0xFFFFFFFF));
        Assert.Equal(0xFFFFFFFFu, Style.InkOn(0xFF000000));
        Assert.Equal(0xFFFFFFFFu, Style.InkOn(0xFF0078D4));   // blue
        Assert.Equal(0xFFFFFFFFu, Style.InkOn(0xFFB146C2));   // purple
    }

    [Fact]
    public void Text_committed_with_the_option_on_is_boxed_and_restyling_follows_it()
    {
        var s = new EditSession(new AnnotationDoc()) { Tool = Tool.Text };
        s.Doc.Current = new Style(0xFFFF0000, 4, 20, TextBox: true);
        s.Begin(10, 10, InputMods.None); s.End(10, 10, InputMods.None);
        s.CommitText("Hello");
        var t = Assert.IsType<TextShape>(Assert.Single(s.Doc.Shapes));
        Assert.True(t.Boxed);
        s.Tool = Tool.Select;
        s.Doc.Select(t.Id);
        s.Style = s.Style with { TextBox = false };
        Assert.False(Assert.IsType<TextShape>(Assert.Single(s.Doc.Shapes)).Boxed);
        Assert.True(s.Doc.CanUndo);
    }

    [Fact]
    public void A_box_grows_the_bounds_on_every_side()
    {
        var plain = new TextShape(1, 100, 50, "Hi", 20, 0xFFFF0000);
        TextShape boxed = plain with { Boxed = true };
        Assert.Equal(0, plain.BoxPadX);
        IntRect a = plain.Bounds, b = boxed.Bounds;
        Assert.True(b.Left < a.Left && b.Top < a.Top && b.Right > a.Right && b.Bottom > a.Bottom);
        Assert.Equal(b, b.Intersect(boxed.DirtyBounds));
        Assert.True(boxed.HitTest(100 - boxed.BoxPadX + 1, 50 - boxed.BoxPadY + 1));   // on the box, off the letters
    }

    [Fact]
    public void The_option_round_trips_through_the_settings_and_defaults_off()
    {
        Assert.False(new AnnotateSettings().TextBackground);
        Assert.False(new AnnotateSettings().ToStyle(0xFF123456).TextBox);
        AnnotateSettings on = AnnotateSettings.FromStyle(new Style(0xFFFF0000, 4, 20, TextBox: true), 0xFF123456);
        Assert.True(on.TextBackground);
        Assert.True(on.ToStyle(0xFF123456).TextBox);

        string dir = Path.Combine(Path.GetTempPath(), "tonesnip-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "settings.json");
        try
        {
            SnipSettingsFile.Save(path, new SnipSettings { Annotate = on });
            Assert.Contains("\"textBackground\": true", File.ReadAllText(path));
            (SnipSettings back, string? err) = SnipSettingsFile.Load(path);
            Assert.Null(err);
            Assert.True(back.Annotate.TextBackground);
        }
        finally { Directory.Delete(dir, true); }
    }
}
