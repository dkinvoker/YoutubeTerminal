using System.Text.Json;
using System.Text.Json.Serialization;
using YoutubeTerminal.YouTube;

namespace YoutubeTerminal.Storage;

internal sealed class Settings
{
    /// <summary>auto | sixel | octant | halfblock</summary>
    public string RenderMode { get; set; } = "auto";
    public bool ListThumbnails { get; set; } = true;
    /// <summary>Maksymalna wysokość strumienia: 0 = automatycznie.</summary>
    public int StreamQuality { get; set; } = 0;
    public int MaxFps { get; set; } = 30;
    public int VideoScale { get; set; } = 100;
    public bool Dither { get; set; }
    /// <summary>Liczba kolorów palety adaptacyjnej dla wideo (mniej = mniejsze klatki).</summary>
    public int VideoColors { get; set; } = 128;
    /// <summary>Maksymalna szerokość obrazu wideo w pikselach (0 = bez limitu).</summary>
    public int MaxVideoWidth { get; set; } = 960;
    public int Volume { get; set; } = 80;
    public bool Autoplay { get; set; } = true;
    public bool ResumePlayback { get; set; } = true;
    public bool SaveHistory { get; set; } = true;
    public bool SyncOutput { get; set; } = true;
    /// <summary>Język interfejsu: auto (wg systemu) | pl | en.</summary>
    public string UiLanguage { get; set; } = "auto";
    /// <summary>Język i region wyników YouTube; „auto” = język interfejsu i region systemu.</summary>
    public string Language { get; set; } = "auto";
    public string Region { get; set; } = "auto";

    /// <summary>Wersja formatu ustawień (do migracji starszych plików).</summary>
    public int Version { get; set; }

    /// <summary>Język wyników YouTube (parametr hl).</summary>
    [JsonIgnore]
    public string EffectiveLanguage => Language is "" or "auto" ? L.Code : Language;

    /// <summary>Region wyników YouTube (parametr gl).</summary>
    [JsonIgnore]
    public string EffectiveRegion => Region is "" or "auto" ? L.DetectRegion() ?? (L.IsPolish ? "PL" : "US") : Region;

    /// <summary>Ręczny rozmiar komórki w pikselach (0 = wykryj).</summary>
    public int CellWidth { get; set; }
    public int CellHeight { get; set; }

    public const int CurrentVersion = 2;

    public static Settings Load() => Load(AppPaths.SettingsFile);

    public static Settings Load(string path)
    {
        Settings? s = null;
        try
        {
            if (File.Exists(path))
                s = JsonSerializer.Deserialize(File.ReadAllText(path), StorageJson.Default.Settings);
        }
        catch { }
        s ??= new Settings();
        s.Migrate();
        return s;
    }

    /// <summary>Starsze wersje zapisywały na sztywno pl/PL — zamieniamy to na „auto” (wg języka interfejsu).</summary>
    internal void Migrate()
    {
        if (Version >= CurrentVersion) return;
        if (Language == "pl" && Region == "PL") { Language = "auto"; Region = "auto"; }
        Version = CurrentVersion;
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureCreated();
            AtomicWrite(AppPaths.SettingsFile, JsonSerializer.Serialize(this, StorageJson.Default.Settings));
        }
        catch { }
    }

    internal static void AtomicWrite(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}

internal sealed class HistoryEntry
{
    public string VideoId { get; set; } = "";
    public string Title { get; set; } = "";
    public string ChannelName { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public int DurationSeconds { get; set; }
    public double PositionSeconds { get; set; }
    public DateTime WatchedAt { get; set; }

    public VideoItem ToVideo() => new()
    {
        Id = VideoId,
        Title = Title,
        ChannelName = ChannelName,
        ChannelId = ChannelId,
        DurationSeconds = DurationSeconds,
        DurationText = DurationSeconds > 0 ? Tui.TextUtil.FormatSeconds(DurationSeconds) : "",
        PublishedText = L.T("obejrzano ", "watched ") + YouTubeClient.RelativeTime(WatchedAt.ToUniversalTime()),
    };
}

internal sealed class SavedVideo
{
    public string VideoId { get; set; } = "";
    public string Title { get; set; } = "";
    public string ChannelName { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public int DurationSeconds { get; set; }
    public string ViewsText { get; set; } = "";
    public DateTime AddedAt { get; set; }

    public static SavedVideo From(VideoItem v) => new()
    {
        VideoId = v.Id,
        Title = v.Title,
        ChannelName = v.ChannelName,
        ChannelId = v.ChannelId,
        DurationSeconds = v.DurationSeconds,
        ViewsText = v.ViewsText,
        AddedAt = DateTime.UtcNow,
    };

    public VideoItem ToVideo() => new()
    {
        Id = VideoId,
        Title = Title,
        ChannelName = ChannelName,
        ChannelId = ChannelId,
        DurationSeconds = DurationSeconds,
        DurationText = DurationSeconds > 0 ? Tui.TextUtil.FormatSeconds(DurationSeconds) : "",
        ViewsText = ViewsText,
        PublishedText = L.T("dodano ", "added ") + YouTubeClient.RelativeTime(AddedAt),
    };
}

internal sealed class Subscription
{
    public string ChannelId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Handle { get; set; } = "";
    public string AvatarUrl { get; set; } = "";
    public DateTime SubscribedAt { get; set; }

    public ChannelItem ToChannel() => new()
    {
        Id = ChannelId,
        Title = Title,
        Handle = Handle,
        AvatarUrl = AvatarUrl,
        SubscribersText = L.T("subskrybujesz od ", "subscribed since ") + SubscribedAt.ToLocalTime().ToString("yyyy-MM-dd"),
    };
}

internal sealed class LibraryData
{
    public List<Subscription> Subscriptions { get; set; } = new();
    public List<HistoryEntry> History { get; set; } = new();
    public List<SavedVideo> WatchLater { get; set; } = new();
    public List<string> SearchHistory { get; set; } = new();
}

/// <summary>Lokalna biblioteka: subskrypcje, historia oglądania, „do obejrzenia”, historia wyszukiwania.</summary>
internal sealed class Library
{
    private readonly object _lock = new();
    private readonly string? _path;
    public LibraryData Data { get; private set; } = new();

    /// <param name="path">Plik biblioteki; null = biblioteka tylko w pamięci (bez zapisu na dysk).</param>
    public Library(string? path = null) => _path = path;

    public static Library Load() => Load(AppPaths.LibraryFile);

    public static Library Load(string path)
    {
        var lib = new Library(path);
        try
        {
            if (File.Exists(path))
                lib.Data = JsonSerializer.Deserialize(File.ReadAllText(path), StorageJson.Default.LibraryData) ?? new LibraryData();
        }
        catch
        {
            // Uszkodzony plik — zachowujemy kopię i zaczynamy od nowa.
            try { File.Copy(path, path + ".bak", true); } catch { }
        }
        return lib;
    }

    public void Save()
    {
        if (_path == null) return;
        lock (_lock)
        {
            try
            {
                if (Path.GetDirectoryName(_path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
                Settings.AtomicWrite(_path, JsonSerializer.Serialize(Data, StorageJson.Default.LibraryData));
            }
            catch { }
        }
    }

    public bool IsSubscribed(string channelId) => Data.Subscriptions.Any(s => s.ChannelId == channelId);

    public bool ToggleSubscription(ChannelItem c)
    {
        lock (_lock)
        {
            var existing = Data.Subscriptions.FirstOrDefault(s => s.ChannelId == c.Id);
            if (existing != null)
            {
                Data.Subscriptions.Remove(existing);
                Save();
                return false;
            }
            Data.Subscriptions.Add(new Subscription
            {
                ChannelId = c.Id,
                Title = c.Title,
                Handle = c.Handle,
                AvatarUrl = c.AvatarUrl,
                SubscribedAt = DateTime.UtcNow,
            });
            Data.Subscriptions.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
            Save();
            return true;
        }
    }

    public HistoryEntry? GetHistory(string videoId) => Data.History.FirstOrDefault(h => h.VideoId == videoId);

    public void UpdateHistory(VideoItem v, double position, double duration)
    {
        lock (_lock)
        {
            var e = Data.History.FirstOrDefault(h => h.VideoId == v.Id);
            if (e == null)
            {
                e = new HistoryEntry { VideoId = v.Id };
            }
            else Data.History.Remove(e);
            e.Title = v.Title.Length > 0 ? v.Title : e.Title;
            e.ChannelName = v.ChannelName.Length > 0 ? v.ChannelName : e.ChannelName;
            e.ChannelId = v.ChannelId.Length > 0 ? v.ChannelId : e.ChannelId;
            if (duration > 0) e.DurationSeconds = (int)duration;
            e.PositionSeconds = position;
            e.WatchedAt = DateTime.UtcNow;
            Data.History.Insert(0, e);
            if (Data.History.Count > 1000) Data.History.RemoveRange(1000, Data.History.Count - 1000);
        }
    }

    public void RemoveHistory(string videoId)
    {
        lock (_lock) Data.History.RemoveAll(h => h.VideoId == videoId);
        Save();
    }

    public void ClearHistory()
    {
        lock (_lock) Data.History.Clear();
        Save();
    }

    public bool IsInWatchLater(string videoId) => Data.WatchLater.Any(w => w.VideoId == videoId);

    public bool ToggleWatchLater(VideoItem v)
    {
        lock (_lock)
        {
            var e = Data.WatchLater.FirstOrDefault(w => w.VideoId == v.Id);
            if (e != null)
            {
                Data.WatchLater.Remove(e);
                Save();
                return false;
            }
            Data.WatchLater.Insert(0, SavedVideo.From(v));
            Save();
            return true;
        }
    }

    public void AddSearch(string q)
    {
        q = q.Trim();
        if (q.Length == 0) return;
        lock (_lock)
        {
            Data.SearchHistory.RemoveAll(s => string.Equals(s, q, StringComparison.OrdinalIgnoreCase));
            Data.SearchHistory.Insert(0, q);
            if (Data.SearchHistory.Count > 50) Data.SearchHistory.RemoveRange(50, Data.SearchHistory.Count - 50);
        }
        Save();
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(LibraryData))]
internal sealed partial class StorageJson : JsonSerializerContext;
