using YoutubeTerminal.YouTube;

namespace YoutubeTerminal.Tests;

public class FormattingTests
{
    [Theory]
    [InlineData(1, "film", "filmy", "filmów", "1 film")]
    [InlineData(2, "film", "filmy", "filmów", "2 filmy")]
    [InlineData(4, "film", "filmy", "filmów", "4 filmy")]
    [InlineData(5, "film", "filmy", "filmów", "5 filmów")]
    [InlineData(12, "film", "filmy", "filmów", "12 filmów")]
    [InlineData(14, "film", "filmy", "filmów", "14 filmów")]
    [InlineData(22, "film", "filmy", "filmów", "22 filmy")]
    [InlineData(112, "film", "filmy", "filmów", "112 filmów")]
    [InlineData(0, "film", "filmy", "filmów", "0 filmów")]
    public void Plural_PolishRules(int n, string one, string few, string many, string expected)
    {
        Assert.Equal(expected, YouTubeClient.Plural(n, one, few, many));
    }

    [Theory]
    [InlineData(1, "1 wyświetlenie")]
    [InlineData(3, "3 wyświetlenia")]
    [InlineData(5, "5 wyświetleń")]
    [InlineData(22, "22 wyświetlenia")]
    [InlineData(999, "999 wyświetleń")]
    [InlineData(1_500, "1,5 tys. wyświetleń")]
    [InlineData(1_000, "1 tys. wyświetleń")]
    [InlineData(1_234_567, "1,2 mln wyświetleń")]
    [InlineData(2_500_000_000, "2,5 mld wyświetleń")]
    public void FormatViews_PolishNumbers(long views, string expected)
    {
        Assert.Equal(expected, YouTubeClient.FormatViews(views));
    }

    [Theory]
    [InlineData(30, "przed chwilą")]
    [InlineData(5 * 60, "5 minut temu")]
    [InlineData(2 * 3600, "2 godziny temu")]
    [InlineData(26 * 3600, "1 dzień temu")]
    [InlineData(3 * 86400, "3 dni temu")]
    [InlineData(14 * 86400, "2 tygodnie temu")]
    [InlineData(65 * 86400, "2 miesiące temu")]
    [InlineData(400 * 86400, "1 rok temu")]
    [InlineData(3 * 365 * 86400, "3 lata temu")]
    public void RelativeTime_Polish(int secondsAgo, string expected)
    {
        Assert.Equal(expected, YouTubeClient.RelativeTime(DateTime.UtcNow.AddSeconds(-secondsAgo)));
    }

    [Fact]
    public void ModelUrls()
    {
        Assert.Equal("https://www.youtube.com/channel/UC1", new ChannelItem { Id = "UC1" }.Url);
        Assert.Equal("https://www.youtube.com/playlist?list=PL1", new PlaylistItem { Id = "PL1" }.Url);
        Assert.Equal("https://i.ytimg.com/vi/abc/hq720.jpg", new VideoItem { Id = "abc" }.LargeThumbnailUrl);
        Assert.Equal("", new PlaylistItem { Id = "PL1" }.ThumbnailUrl);
    }
}
