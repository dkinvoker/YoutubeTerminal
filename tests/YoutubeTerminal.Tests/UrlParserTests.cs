using YoutubeTerminal.YouTube;

namespace YoutubeTerminal.Tests;

public class UrlParserTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ&feature=share", "dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/abcdefghijk", "abcdefghijk")]
    [InlineData("https://www.youtube.com/live/abcdefghijk?si=x", "abcdefghijk")]
    [InlineData("https://www.youtube.com/embed/abcdefghijk", "abcdefghijk")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PL123", "dQw4w9WgXcQ")]
    public void VideoLinks(string url, string id)
    {
        var r = UrlParser.Parse(url);
        Assert.NotNull(r);
        Assert.Equal("video", r.Kind);
        Assert.Equal(id, r.Id);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=90", 90)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=90s", 90)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=1m30s", 90)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=1h2m3s", 3723)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", 0)]
    public void StartTimeFromLink(string url, double seconds)
    {
        Assert.Equal(seconds, UrlParser.Parse(url)!.StartSeconds);
    }

    [Fact]
    public void PlaylistLink()
    {
        var r = UrlParser.Parse("https://www.youtube.com/playlist?list=PLabc_123");
        Assert.Equal(new YouTubeClient.Resolved("playlist", "PLabc_123"), r);
    }

    [Fact]
    public void ChannelIdLinkAndBareId()
    {
        Assert.Equal("channel", UrlParser.Parse("https://www.youtube.com/channel/UCSJ4gkVC6NrvII8umztf0Ow")!.Kind);
        var bare = UrlParser.Parse("UCSJ4gkVC6NrvII8umztf0Ow");
        Assert.Equal(new YouTubeClient.Resolved("channel", "UCSJ4gkVC6NrvII8umztf0Ow"), bare);
    }

    [Theory]
    [InlineData("@LofiGirl", "https://www.youtube.com/@LofiGirl")]
    [InlineData("https://www.youtube.com/@LofiGirl", "https://www.youtube.com/@LofiGirl")]
    [InlineData("https://www.youtube.com/@LofiGirl/videos", "https://www.youtube.com/@LofiGirl")]
    [InlineData("https://www.youtube.com/c/SomeName/about", "https://www.youtube.com/c/SomeName")]
    [InlineData("https://www.youtube.com/user/olduser", "https://www.youtube.com/user/olduser")]
    public void HandlesNeedServerResolution(string input, string url)
    {
        var r = UrlParser.Parse(input);
        Assert.NotNull(r);
        Assert.Equal("url", r.Kind);
        Assert.Equal(url, r.Id);
    }

    [Theory]
    [InlineData("lofi hip hop")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=tooShort")]
    [InlineData("@two words")]
    [InlineData("https://www.youtube.com/feed/trending")]
    public void NotALink(string input)
    {
        Assert.Null(UrlParser.Parse(input));
    }
}
