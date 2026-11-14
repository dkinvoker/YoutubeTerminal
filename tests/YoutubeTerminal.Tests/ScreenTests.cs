using System.Text;
using YoutubeTerminal.Tui;

namespace YoutubeTerminal.Tests;

public class ScreenTests
{
    private static readonly Style S = new(0xFFFFFF, 0x000000);

    private static string Row(ScreenBuffer b, int y) =>
        string.Concat(Enumerable.Range(0, b.Width).Select(x => b.Cells[y * b.Width + x].Text ?? "·"));

    [Fact]
    public void Text_WritesAndReturnsColumns()
    {
        var b = new ScreenBuffer(10, 2);
        b.Clear(S);
        Assert.Equal(3, b.Text(1, 0, "abc", S));
        Assert.Equal(" abc      ", Row(b, 0));
    }

    [Fact]
    public void Text_WideCharacterOccupiesTwoCells()
    {
        var b = new ScreenBuffer(6, 1);
        b.Clear(S);
        Assert.Equal(4, b.Text(0, 0, "日本", S));
        Assert.Equal("日·本·  ", Row(b, 0)); // · = druga połowa szerokiego znaku
    }

    [Fact]
    public void Text_WideCharacterThatDoesNotFitIsSkipped()
    {
        var b = new ScreenBuffer(3, 1);
        b.Clear(S);
        b.Text(0, 0, "a日本", S);
        Assert.Equal("a日·", Row(b, 0));
    }

    [Fact]
    public void Text_OverwritingHalfOfWideCharacterClearsIt()
    {
        var b = new ScreenBuffer(4, 1);
        b.Clear(S);
        b.Text(0, 0, "日", S);
        b.Text(1, 0, "x", S);
        Assert.Equal(" x  ", Row(b, 0));
    }

    [Fact]
    public void Text_RespectsMaxWidthAndClip()
    {
        var b = new ScreenBuffer(10, 3);
        b.Clear(S);
        b.Text(0, 0, "abcdef", S, maxWidth: 3);
        Assert.Equal("abc       ", Row(b, 0));

        using (b.Clip(new Rect(2, 1, 3, 1)))
        {
            b.Text(0, 1, "0123456789", S);
            b.Text(0, 2, "zzz", S); // poza obszarem przycięcia
        }
        Assert.Equal("  234     ", Row(b, 1));
        Assert.Equal("          ", Row(b, 2));
    }

    [Fact]
    public void TextTrunc_AddsEllipsis()
    {
        var b = new ScreenBuffer(10, 1);
        b.Clear(S);
        b.TextTrunc(0, 0, "abcdefghij-xyz", S, 5);
        Assert.Equal("abcd…     ", Row(b, 0));
    }

    [Fact]
    public void AddImage_IgnoresImagesOutsideClip()
    {
        var b = new ScreenBuffer(20, 10);
        using (b.Clip(new Rect(0, 0, 10, 5)))
        {
            b.AddImage(new ImagePlacement { Area = new Rect(1, 1, 3, 2) });
            b.AddImage(new ImagePlacement { Area = new Rect(8, 3, 4, 3) }); // wystaje
            b.AddImage(new ImagePlacement { Area = new Rect(1, 1, 0, 2) }); // pusty
        }
        Assert.Single(b.Images);
    }

    [Fact]
    public void Rect_Helpers()
    {
        var r = new Rect(2, 3, 10, 6);
        Assert.Equal((12, 9), (r.Right, r.Bottom));
        Assert.True(r.Contains(2, 3));
        Assert.False(r.Contains(12, 3));
        Assert.Equal(new Rect(3, 4, 8, 4), r.Inset(1, 1));
        Assert.Equal(new Rect(2, 5, 10, 4), r.SkipTop(2));
        Assert.Equal(new Rect(2, 7, 10, 2), r.TakeBottom(2));
        Assert.True(new Rect(0, 0, 0, 5).IsEmpty);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(7, "7")]
    [InlineData(42, "42")]
    [InlineData(999, "999")]
    [InlineData(1000, "1000")]
    [InlineData(12345678, "12345678")]
    [InlineData(-42, "-42")]
    public void ByteBuilder_AppendInt(int value, string expected)
    {
        var bb = new ByteBuilder(2);
        bb.AppendInt(value);
        Assert.Equal(expected, Encoding.ASCII.GetString(bb.Span));
    }

    [Fact]
    public void ByteBuilder_GrowsAndMovesCursor()
    {
        var bb = new ByteBuilder(4);
        bb.MoveTo(0, 0);
        bb.AppendUtf8("żółw");
        bb.AppendAscii("!");
        Assert.Equal("\x1b[1;1Hżółw!", Encoding.UTF8.GetString(bb.ToArray()));
        bb.Clear();
        Assert.Equal(0, bb.Length);
    }
}
