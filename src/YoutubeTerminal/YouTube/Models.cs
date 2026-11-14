namespace YoutubeTerminal.YouTube;

/// <summary>Wspólna baza elementów list (film, kanał, playlista).</summary>
internal abstract class YtItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public abstract string ThumbnailUrl { get; }
}

internal sealed class VideoItem : YtItem
{
    public string ChannelName { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string DurationText { get; set; } = "";
    public int DurationSeconds { get; set; }
    public string ViewsText { get; set; } = "";
    public string PublishedText { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsLive { get; set; }
    public bool IsUpcoming { get; set; }
    public bool IsShort { get; set; }
    public DateTime? Published { get; set; }

    public override string ThumbnailUrl => $"https://i.ytimg.com/vi/{Id}/mqdefault.jpg";
    public string LargeThumbnailUrl => $"https://i.ytimg.com/vi/{Id}/hq720.jpg";
    public string Url => IsShort ? $"https://www.youtube.com/shorts/{Id}" : $"https://www.youtube.com/watch?v={Id}";
}

internal sealed class ChannelItem : YtItem
{
    public string Handle { get; set; } = "";
    public string SubscribersText { get; set; } = "";
    public string VideoCountText { get; set; } = "";
    public string Description { get; set; } = "";
    public string AvatarUrl { get; set; } = "";

    public override string ThumbnailUrl => AvatarUrl;
    public string Url => $"https://www.youtube.com/channel/{Id}";
}

internal sealed class PlaylistItem : YtItem
{
    public string ChannelName { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string VideoCountText { get; set; } = "";
    public string FirstVideoId { get; set; } = "";
    public string Thumbnail { get; set; } = "";
    public bool IsMix { get; set; }

    public override string ThumbnailUrl => !string.IsNullOrEmpty(Thumbnail) ? Thumbnail
        : !string.IsNullOrEmpty(FirstVideoId) ? $"https://i.ytimg.com/vi/{FirstVideoId}/mqdefault.jpg" : "";
    public string Url => $"https://www.youtube.com/playlist?list={Id}";
}

/// <summary>Strona wyników z tokenem kontynuacji (przewijanie w nieskończoność).</summary>
internal sealed class FeedPage
{
    public List<YtItem> Items { get; } = new();
    public string? Continuation { get; set; }
}

internal sealed class ChannelTab
{
    public string Title = "";
    public string Params = "";
    public bool Selected;
}

internal sealed class SortChip
{
    public string Title = "";
    public string Token = "";
    public bool Selected;
}

internal sealed class ChannelPage
{
    public ChannelItem Channel = new();
    public string BannerUrl = "";
    public List<ChannelTab> Tabs = new();
    public FeedPage Content = new();
    public List<SortChip> Sorts = new();
}

internal sealed class PlaylistPage
{
    public PlaylistItem Playlist = new();
    public string Description = "";
    public string ViewsText = "";
    public FeedPage Content = new();
}

internal sealed class Chapter
{
    public string Title = "";
    public int StartSeconds;
}

internal sealed class VideoDetails
{
    public VideoItem Video = new();
    public string ViewsFull = "";
    public string DateText = "";
    public string LikesText = "";
    public string ChannelSubscribers = "";
    public string ChannelAvatar = "";
    public string Description = "";
    public string CommentsCount = "";
    public string? CommentsToken;
    public List<Chapter> Chapters = new();
    public FeedPage Related = new();
}

internal sealed class Comment
{
    public string Author = "";
    public string Text = "";
    public string Published = "";
    public string Likes = "";
    public int ReplyCount;
    public string ReplyCountText = "";
    public string? RepliesToken;
    public bool IsCreator;
    public bool IsPinned;
    public bool IsReply;
}

internal sealed class CommentsPage
{
    public List<Comment> Comments = new();
    public string? Continuation;
    public string HeaderCount = "";
    public List<SortChip> Sorts = new();
}

internal enum SearchFilter { All, Videos, Channels, Playlists }
internal enum SearchSort { Relevance, UploadDate, ViewCount, Rating }
