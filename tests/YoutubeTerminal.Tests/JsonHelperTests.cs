using System.Text.Json.Nodes;
using YoutubeTerminal.YouTube;

namespace YoutubeTerminal.Tests;

public class JsonHelperTests
{
    private static JsonNode N(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void Text_ReadsSimpleTextRunsAndContent()
    {
        Assert.Equal("a", N("""{"simpleText":"a"}""").Text());
        Assert.Equal("ab c", N("""{"runs":[{"text":"ab"},{"text":" c"}]}""").Text());
        Assert.Equal("c", N("""{"content":"c"}""").Text());
        Assert.Equal("x", N("""{"t":{"simpleText":"x"}}""").Text("t"));
        Assert.Equal("", N("""{"other":1}""").Text());
        Assert.Equal("", ((JsonNode?)null).Text());
    }

    [Fact]
    public void At_NavigatesObjectsAndArrays()
    {
        var n = N("""{"a":[{"b":"x"},{"b":"y"}]}""");
        Assert.Equal("y", n.Str("a", 1, "b"));
        Assert.Null(n.At("a", 5, "b"));
        Assert.Null(n.At("missing", "b"));
    }

    [Fact]
    public void Str_ConvertsNumbersAndBooleans()
    {
        var n = N("""{"i":42,"d":1.5,"t":true,"s":"tekst"}""");
        Assert.Equal("42", n.Str("i"));
        Assert.Equal("1.5", n.Str("d"));
        Assert.Equal("true", n.Str("t"));
        Assert.Equal("tekst", n.Str("s"));
        Assert.Equal("", n.Str("brak"));
    }

    [Fact]
    public void Long_ParsesNumbersAndNumericStrings()
    {
        var n = N("""{"a":123,"b":"456","c":"x"}""");
        Assert.Equal(123, n.Long("a"));
        Assert.Equal(456, n.Long("b"));
        Assert.Equal(0, n.Long("c"));
    }

    [Fact]
    public void Find_ReturnsFirstMatchDepthFirst()
    {
        var n = N("""{"x":{"y":[{"token":"t1"}]},"token":"top"}""");
        Assert.Equal("top", n.Find("token").Str());
        Assert.Equal("t1", n.At("x").Find("token").Str());
    }

    [Fact]
    public void FindAll_ReturnsAllInDocumentOrder()
    {
        var list = new List<JsonNode>();
        N("""{"a":{"k":1},"b":[{"k":2},{"c":{"k":3}}]}""").FindAll("k", list);
        Assert.Equal(["1", "2", "3"], list.Select(x => x.Str()));
    }

    [Fact]
    public void BestThumb_PicksClosestWidthAndAddsScheme()
    {
        var thumbs = N("""[{"url":"//a/88.jpg","width":88},{"url":"//a/176.jpg","width":176},{"url":"//a/800.jpg","width":800}]""");
        Assert.Equal("https://a/176.jpg", thumbs.BestThumb(200));
        Assert.Equal("https://a/800.jpg", thumbs.BestThumb(700));
        Assert.Equal("", N("[]").BestThumb());
    }
}
