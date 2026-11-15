using System.Text.Json;
using YoutubeTerminal.Storage;
using YoutubeTerminal.YouTube;

namespace YoutubeTerminal.Tests;

/// <summary>
/// Biblioteka i ustawienia. Testujemy tylko operacje bez zapisu na dysk:
/// metody zapisujące od razu do %LOCALAPPDATA% nie dają się dziś przekierować do folderu testowego.
/// </summary>
public class LibraryTests
{
    private static VideoItem Video(string id, string title = "T", int dur = 100) =>
        new() { Id = id, Title = title, ChannelName = "Kanał", ChannelId = "UC1", DurationSeconds = dur };

    [Fact]
    public void UpdateHistory_NewestFirstAndUpdatesExisting()
    {
        var lib = new Library();
        lib.UpdateHistory(Video("a"), 10, 100);
        lib.UpdateHistory(Video("b"), 20, 200);
        lib.UpdateHistory(Video("a", title: ""), 50, 0);

        Assert.Equal(["a", "b"], lib.Data.History.Select(h => h.VideoId));
        var a = lib.GetHistory("a")!;
        Assert.Equal(50, a.PositionSeconds);
        Assert.Equal("T", a.Title);            // pusty tytuł nie nadpisuje zapamiętanego
        Assert.Equal(100, a.DurationSeconds);  // czas 0 nie nadpisuje znanego
        Assert.Null(lib.GetHistory("zzz"));
    }

    [Fact]
    public void UpdateHistory_KeepsAtMost1000Entries()
    {
        var lib = new Library();
        for (int i = 0; i < 1005; i++) lib.UpdateHistory(Video("v" + i), 1, 10);
        Assert.Equal(1000, lib.Data.History.Count);
        Assert.Equal("v1004", lib.Data.History[0].VideoId);
        Assert.Null(lib.GetHistory("v0"));
    }

    [Fact]
    public void QueriesOnEmptyLibrary()
    {
        var lib = new Library();
        Assert.False(lib.IsSubscribed("UC1"));
        Assert.False(lib.IsInWatchLater("abc"));
    }

    [Fact]
    public void HistoryEntry_ToVideo()
    {
        var v = new HistoryEntry { VideoId = "a", Title = "T", DurationSeconds = 125, WatchedAt = DateTime.UtcNow }.ToVideo();
        Assert.Equal("2:05", v.DurationText);
        Assert.StartsWith("obejrzano ", v.PublishedText);
    }

    [Fact]
    public void SavedVideo_RoundTrip()
    {
        var saved = SavedVideo.From(Video("x", "Film", 61));
        var back = saved.ToVideo();
        Assert.Equal(("x", "Film", 61, "1:01"), (back.Id, back.Title, back.DurationSeconds, back.DurationText));
        Assert.StartsWith("dodano ", back.PublishedText);
    }

    [Fact]
    public void LibraryData_JsonRoundTrip()
    {
        var data = new LibraryData();
        data.Subscriptions.Add(new Subscription { ChannelId = "UC1", Title = "Kanał", Handle = "@k" });
        data.History.Add(new HistoryEntry { VideoId = "v", Title = "Żółć", PositionSeconds = 12.5 });
        data.WatchLater.Add(new SavedVideo { VideoId = "w" });
        data.SearchHistory.Add("lofi");

        var json = JsonSerializer.Serialize(data, StorageJson.Default.LibraryData);
        Assert.Contains("\"subscriptions\"", json);   // camelCase
        var back = JsonSerializer.Deserialize(json, StorageJson.Default.LibraryData)!;

        Assert.Equal("@k", back.Subscriptions[0].Handle);
        Assert.Equal("Żółć", back.History[0].Title);
        Assert.Equal(12.5, back.History[0].PositionSeconds);
        Assert.Equal("w", back.WatchLater[0].VideoId);
        Assert.Equal("lofi", back.SearchHistory[0]);
    }

    [Fact]
    public void Settings_DefaultsAndJsonRoundTrip()
    {
        var s = new Settings();
        Assert.Equal("auto", s.RenderMode);
        Assert.Equal(128, s.VideoColors);
        Assert.Equal(960, s.MaxVideoWidth);
        Assert.Equal(30, s.MaxFps);

        s.Volume = 35;
        s.RenderMode = "halfblock";
        var back = JsonSerializer.Deserialize(JsonSerializer.Serialize(s, StorageJson.Default.Settings), StorageJson.Default.Settings)!;
        Assert.Equal(35, back.Volume);
        Assert.Equal("halfblock", back.RenderMode);
    }

    [Fact]
    public void Settings_MissingFieldsKeepDefaults()
    {
        var s = JsonSerializer.Deserialize("""{"volume":10}""", StorageJson.Default.Settings)!;
        Assert.Equal(10, s.Volume);
        Assert.True(s.Autoplay);
        Assert.Equal("auto", s.Language);
        Assert.Equal("auto", s.UiLanguage);
    }
}
