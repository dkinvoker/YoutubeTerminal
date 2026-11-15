using System.Runtime.CompilerServices;
using YoutubeTerminal.Storage;
using YoutubeTerminal.YouTube;

namespace YoutubeTerminal.Tests;

internal static class TestLanguage
{
    /// <summary>
    /// Testy powstały dla polskiego interfejsu; ustawiamy go raz dla całego zestawu,
    /// a testy innych języków podają język jawnie (bez zmiany stanu globalnego).
    /// </summary>
    [ModuleInitializer]
    internal static void Init() => L.Current = Lang.Pl;
}

public class LocalizationTests
{
    [Theory]
    [InlineData(1, "1 view")]
    [InlineData(999, "999 views")]
    [InlineData(1500, "1.5K views")]
    [InlineData(1234567, "1.2M views")]
    [InlineData(2500000000, "2.5B views")]
    public void FormatViews_English(long views, string expected)
    {
        Assert.Equal(expected, YouTubeClient.FormatViews(views, Lang.En));
    }

    [Theory]
    [InlineData(30, "just now")]
    [InlineData(60, "1 minute ago")]
    [InlineData(300, "5 minutes ago")]
    [InlineData(7200, "2 hours ago")]
    [InlineData(93600, "1 day ago")]
    [InlineData(1209600, "2 weeks ago")]
    [InlineData(34560000, "1 year ago")]
    public void RelativeTime_English(int secondsAgo, string expected)
    {
        Assert.Equal(expected, YouTubeClient.RelativeTime(DateTime.UtcNow.AddSeconds(-secondsAgo), Lang.En));
    }

    [Theory]
    [InlineData("pl_PL.UTF-8", Lang.Pl)]
    [InlineData("pl-PL", Lang.Pl)]
    [InlineData("en_US.UTF-8", Lang.En)]
    [InlineData("de_DE", Lang.En)]
    public void FromTag_PolishOrEnglish(string tag, object expected)
    {
        Assert.Equal((Lang)expected, L.FromTag(tag));
    }

    [Theory]
    [InlineData("pl", Lang.Pl)]
    [InlineData("en", Lang.En)]
    [InlineData("EN", Lang.En)]
    public void Resolve_ExplicitSetting(string setting, object expected)
    {
        Assert.Equal((Lang)expected, L.Resolve(setting));
    }

    [Fact]
    public void Settings_OldHardcodedPolishResultsBecomeAuto()
    {
        var s = new Settings { Language = "pl", Region = "PL", Version = 0 };
        s.Migrate();
        Assert.Equal("auto", s.Language);
        Assert.Equal("auto", s.Region);
        Assert.Equal(Settings.CurrentVersion, s.Version);
    }

    [Fact]
    public void Settings_UserChosenResultsLanguageIsKept()
    {
        var s = new Settings { Language = "de", Region = "DE", Version = 0 };
        s.Migrate();
        Assert.Equal("de", s.Language);
        Assert.Equal("de", s.EffectiveLanguage);
        Assert.Equal("DE", s.EffectiveRegion);
    }

    [Fact]
    public void Settings_AutoResultsLanguageFollowsInterface()
    {
        var s = new Settings();
        Assert.Equal(L.Code, s.EffectiveLanguage);
    }
}
