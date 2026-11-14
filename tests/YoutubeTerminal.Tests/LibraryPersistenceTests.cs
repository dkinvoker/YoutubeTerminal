using YoutubeTerminal.Storage;
using YoutubeTerminal.YouTube;

namespace YoutubeTerminal.Tests;

/// <summary>Biblioteka z zapisem do pliku w folderze tymczasowym (nie dotyka danych użytkownika).</summary>
public class LibraryPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yt-terminal-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "library.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static VideoItem Video(string id) => new() { Id = id, Title = "Film " + id, ChannelName = "K", ChannelId = "UC1", DurationSeconds = 60 };

    [Fact]
    public void SaveAndLoadRoundTrip()
    {
        var lib = Library.Load(FilePath);
        lib.ToggleWatchLater(Video("a"));
        lib.ToggleSubscription(new ChannelItem { Id = "UCb", Title = "Beta" });
        lib.UpdateHistory(Video("h"), 30, 60);
        lib.Save();

        Assert.True(File.Exists(FilePath));
        var back = Library.Load(FilePath);
        Assert.True(back.IsInWatchLater("a"));
        Assert.True(back.IsSubscribed("UCb"));
        Assert.Equal(30, back.GetHistory("h")!.PositionSeconds);
    }

    [Fact]
    public void ToggleWatchLaterAddsAndRemoves()
    {
        var lib = Library.Load(FilePath);
        Assert.True(lib.ToggleWatchLater(Video("a")));
        Assert.True(lib.ToggleWatchLater(Video("b")));
        Assert.Equal(["b", "a"], lib.Data.WatchLater.Select(w => w.VideoId)); // najnowsze na górze
        Assert.False(lib.ToggleWatchLater(Video("a")));
        Assert.False(Library.Load(FilePath).IsInWatchLater("a")); // zapisane od razu
    }

    [Fact]
    public void SubscriptionsAreSortedByTitle()
    {
        var lib = Library.Load(FilePath);
        lib.ToggleSubscription(new ChannelItem { Id = "UC3", Title = "żaba" });
        lib.ToggleSubscription(new ChannelItem { Id = "UC1", Title = "Alfa" });
        lib.ToggleSubscription(new ChannelItem { Id = "UC2", Title = "beta" });
        Assert.Equal(["Alfa", "beta", "żaba"], lib.Data.Subscriptions.Select(s => s.Title));
        Assert.False(lib.ToggleSubscription(new ChannelItem { Id = "UC2", Title = "beta" }));
        Assert.Equal(2, lib.Data.Subscriptions.Count);
    }

    [Fact]
    public void SearchHistoryDeduplicatesAndKeepsFifty()
    {
        var lib = Library.Load(FilePath);
        lib.AddSearch("Lofi");
        lib.AddSearch("minecraft");
        lib.AddSearch("  lofi  ");
        lib.AddSearch("");
        Assert.Equal(["lofi", "minecraft"], lib.Data.SearchHistory);

        for (int i = 0; i < 60; i++) lib.AddSearch("q" + i);
        Assert.Equal(50, lib.Data.SearchHistory.Count);
        Assert.Equal("q59", lib.Data.SearchHistory[0]);
    }

    [Fact]
    public void RemoveAndClearHistory()
    {
        var lib = Library.Load(FilePath);
        lib.UpdateHistory(Video("a"), 1, 60);
        lib.UpdateHistory(Video("b"), 1, 60);
        lib.RemoveHistory("a");
        Assert.Equal(["b"], lib.Data.History.Select(h => h.VideoId));
        lib.ClearHistory();
        Assert.Empty(Library.Load(FilePath).Data.History);
    }

    [Fact]
    public void CorruptedFileIsBackedUpAndLibraryStartsEmpty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ to nie jest json");
        var lib = Library.Load(FilePath);
        Assert.Empty(lib.Data.History);
        Assert.True(File.Exists(FilePath + ".bak"));
    }

    [Fact]
    public void InMemoryLibraryNeverWritesFiles()
    {
        var lib = new Library();
        lib.ToggleWatchLater(Video("a"));
        lib.AddSearch("x");
        lib.Save();
        Assert.False(Directory.Exists(_dir));
        Assert.True(lib.IsInWatchLater("a"));
    }
}
