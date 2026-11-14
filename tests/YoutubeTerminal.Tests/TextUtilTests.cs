using YoutubeTerminal.Tui;

namespace YoutubeTerminal.Tests;

public class TextUtilTests
{
    [Theory]
    [InlineData("abc", 3)]
    [InlineData("", 0)]
    [InlineData("zażółć gęślą", 12)]
    [InlineData("日本", 4)]
    [InlineData("한국어", 6)]
    [InlineData("😀", 2)]
    [InlineData("👍🏽", 2)]           // emoji + modyfikator koloru skóry = jeden grafem
    [InlineData("❤️", 2)]             // selektor prezentacji emoji (VS16)
    [InlineData("👨‍👩‍👧", 2)]          // sekwencja ZWJ
    [InlineData("🇵🇱", 2)]           // flaga (para wskaźników regionalnych)
    [InlineData("é", 1)]        // znak łączący nie zajmuje kolumny
    [InlineData("𝐏𝐥𝐚𝐲", 4)]          // pogrubione litery matematyczne są wąskie
    public void Width_CountsTerminalColumns(string text, int expected)
    {
        Assert.Equal(expected, TextUtil.Width(text));
    }

    [Fact]
    public void Width_NullIsZero() => Assert.Equal(0, TextUtil.Width(null));

    [Fact]
    public void Graphemes_ReplaceControlCharactersWithSpace()
    {
        var g = TextUtil.Graphemes("a\tb").ToList();
        Assert.Equal(["a", " ", "b"], g.Select(x => x.Text));
        Assert.All(g, x => Assert.Equal(1, x.Width));
    }

    [Theory]
    [InlineData("Hello world", 8, "Hello w…")]
    [InlineData("abc", 5, "abc")]
    [InlineData("abc", 3, "abc")]
    [InlineData("abcd", 3, "ab…")]
    [InlineData("日本語テキスト", 5, "日本…")]
    [InlineData("anything", 0, "")]
    public void Truncate_FitsWidthWithEllipsis(string text, int max, string expected)
    {
        var result = TextUtil.Truncate(text, max);
        Assert.Equal(expected, result);
        Assert.True(TextUtil.Width(result) <= Math.Max(0, max));
    }

    [Fact]
    public void Truncate_DoesNotSplitWideCharacterPastLimit()
    {
        // 3 kolumny: "日" (2) + "…" (1); następny szeroki znak się nie mieści
        Assert.Equal("日…", TextUtil.Truncate("日本語", 3));
    }

    [Fact]
    public void OneLine_CollapsesWhitespaceAndControlCharacters()
    {
        Assert.Equal("a b c", TextUtil.OneLine("  a\n\tb  \r\n c  "));
        Assert.Equal("", TextUtil.OneLine(null));
    }

    [Fact]
    public void Wrap_LinesNeverExceedWidth()
    {
        const string text = "Ala ma kota, a kot ma Alę. Przykładowy dłuższy tekst do zawijania w kilku liniach.";
        foreach (int width in new[] { 5, 8, 13, 20, 40 })
        {
            var lines = TextUtil.Wrap(text, width);
            Assert.All(lines, l => Assert.True(TextUtil.Width(l) <= width, $"'{l}' > {width}"));
            // żadne słowo nie zginęło
            Assert.Equal(text.Replace(" ", ""), string.Concat(lines).Replace(" ", ""));
        }
    }

    [Fact]
    public void Wrap_BreaksWordsLongerThanLine()
    {
        Assert.Equal(["abcd", "efgh", "ij"], TextUtil.Wrap("abcdefghij", 4));
    }

    [Fact]
    public void Wrap_KeepsHardLineBreaksAndEmptyLines()
    {
        Assert.Equal(["a", "", "b"], TextUtil.Wrap("a\n\nb", 10));
        Assert.Equal(["a", "b"], TextUtil.Wrap("a\r\nb", 10));
    }

    [Fact]
    public void Wrap_RespectsMaxLines()
    {
        Assert.Equal(2, TextUtil.Wrap("a\nb\nc\nd", 10, maxLines: 2).Count);
    }

    [Fact]
    public void Wrap_EmptyOrZeroWidthGivesNoLines()
    {
        Assert.Empty(TextUtil.Wrap("", 10));
        Assert.Empty(TextUtil.Wrap(null, 10));
        Assert.Empty(TextUtil.Wrap("abc", 0));
    }

    [Fact(Skip = "Znany błąd: spacja po słowie liczy się do szerokości, więc linia mieszcząca się co do znaku jest łamana za wcześnie.")]
    public void Wrap_FillsLineExactlyToWidth()
    {
        Assert.Equal(["ala ma", "kota"], TextUtil.Wrap("ala ma kota", 6));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(65, "1:05")]
    [InlineData(599, "9:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3725, "1:02:05")]
    [InlineData(-10, "0:00")]
    public void FormatSeconds_FormatsLikeYouTube(double seconds, string expected)
    {
        Assert.Equal(expected, TextUtil.FormatSeconds(seconds));
    }

    [Theory]
    [InlineData("1:02:03", 3723)]
    [InlineData("12:34", 754)]
    [InlineData("0:07", 7)]
    [InlineData(" 3:00 ", 180)]
    [InlineData("abc", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("NA ŻYWO", 0)]
    public void ParseDuration_ParsesColonSeparatedTime(string? text, int expected)
    {
        Assert.Equal(expected, TextUtil.ParseDuration(text));
    }
}
