using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace YoutubeTerminal.YouTube;

/// <summary>
/// Parsery odpowiedzi InnerTube. Zamiast sztywnych ścieżek przechodzimy rekurencyjnie po drzewie
/// i rozpoznajemy znane renderery — dzięki temu drobne zmiany układu nie psują aplikacji.
/// </summary>
internal static partial class Parsers
{
    private static readonly HashSet<string> SkipKeys =
    [
        "adSlotRenderer", "promotedSparklesWebRenderer", "searchPyvRenderer", "promotedVideoRenderer",
        "menu", "menuRenderer", "thumbnailOverlays", "header", "frameworkUpdates", "responseContext",
        "trackingParams", "loggingContext", "topbar", "microformat", "playerOverlays", "engagementPanels",
        "feedFilterChipBarRenderer", "chipCloudRenderer", "backgroundPromoRenderer", "messageRenderer",
    ];

    public static void CollectItems(JsonNode? node, FeedPage page, HashSet<string>? seen = null, int depth = 0)
    {
        seen ??= new HashSet<string>(page.Items.Select(i => i.GetType().Name[0] + i.Id));
        if (node == null || depth > 60) return;
        if (node is JsonArray arr)
        {
            foreach (var e in arr) CollectItems(e, page, seen, depth + 1);
            return;
        }
        if (node is not JsonObject obj) return;

        foreach (var (key, value) in obj)
        {
            if (value == null || SkipKeys.Contains(key)) continue;
            YtItem? item = null;
            switch (key)
            {
                case "videoRenderer":
                case "compactVideoRenderer":
                case "gridVideoRenderer":
                case "playlistVideoRenderer":
                case "playlistPanelVideoRenderer":
                case "videoWithContextRenderer":
                    item = ParseVideo(value);
                    break;
                case "reelItemRenderer":
                    item = ParseReel(value);
                    break;
                case "shortsLockupViewModel":
                    item = ParseShortsLockup(value);
                    break;
                case "channelRenderer":
                case "gridChannelRenderer":
                    item = ParseChannel(value);
                    break;
                case "playlistRenderer":
                case "gridPlaylistRenderer":
                case "compactPlaylistRenderer":
                case "radioRenderer":
                case "compactRadioRenderer":
                    item = ParsePlaylist(value);
                    break;
                case "lockupViewModel":
                    item = ParseLockup(value);
                    break;
                case "continuationItemRenderer":
                    var token = value.Find("token").Str();
                    if (token.Length > 0) page.Continuation = token;
                    continue;
                default:
                    CollectItems(value, page, seen, depth + 1);
                    continue;
            }
            if (item != null && item.Id.Length > 0 && seen.Add(item.GetType().Name[0] + item.Id))
                page.Items.Add(item);
        }
    }

    private static (string Name, string Id) Byline(JsonNode r)
    {
        foreach (var key in new[] { "ownerText", "shortBylineText", "longBylineText" })
        {
            var t = r.At(key);
            if (t == null) continue;
            string name = t.Text();
            string id = t.Find("browseId").Str();
            if (name.Length > 0) return (name, id);
        }
        return ("", r.Find("browseId").Str());
    }

    public static VideoItem? ParseVideo(JsonNode r)
    {
        var id = r.Str("videoId");
        if (id.Length == 0) return null;
        var v = new VideoItem { Id = id, Title = r.Text("title") };
        if (v.Title.Length == 0) v.Title = r.Str("title", "accessibility", "accessibilityData", "label");
        (v.ChannelName, v.ChannelId) = Byline(r);

        v.DurationText = r.Text("lengthText");
        if (v.DurationText.Length == 0)
        {
            foreach (var o in r.Arr("thumbnailOverlays"))
            {
                var ts = o.At("thumbnailOverlayTimeStatusRenderer");
                if (ts == null) continue;
                var txt = ts.Text("text");
                if (ts.Str("style") == "LIVE") v.IsLive = true;
                else if (ts.Str("style") == "SHORTS") v.IsShort = true;
                else v.DurationText = txt;
            }
        }
        v.DurationSeconds = TextUtilDuration(v.DurationText);
        if (v.DurationSeconds == 0 && r.Str("lengthSeconds") is { Length: > 0 } ls && int.TryParse(ls, out int lsec))
        {
            v.DurationSeconds = lsec;
            v.DurationText = Tui.TextUtil.FormatSeconds(lsec);
        }

        v.ViewsText = r.Text("viewCountText");
        if (v.ViewsText.Length == 0) v.ViewsText = r.Text("shortViewCountText");
        v.PublishedText = r.Text("publishedTimeText");

        if (v.ViewsText.Length == 0 && r.At("videoInfo") is { } info)
        {
            var runs = info.Arr("runs").Select(x => x.Str("text")).Where(x => x.Trim().Length > 1 && x.Trim() != "•").ToList();
            if (runs.Count > 0) v.ViewsText = runs[0];
            if (runs.Count > 1) v.PublishedText = runs[1];
        }

        v.Description = r.At("detailedMetadataSnippets", 0, "snippetText").Text();
        if (v.Description.Length == 0) v.Description = r.Text("descriptionSnippet");

        foreach (var b in r.Arr("badges"))
        {
            var style = b.Str("metadataBadgeRenderer", "style");
            if (style.Contains("LIVE", StringComparison.Ordinal)) v.IsLive = true;
        }
        if (r.At("upcomingEventData") != null) v.IsUpcoming = true;
        if (r.Find("reelWatchEndpoint", 6) != null) v.IsShort = true;
        return v;
    }

    private static int TextUtilDuration(string s) => Tui.TextUtil.ParseDuration(s);

    private static VideoItem? ParseReel(JsonNode r)
    {
        var id = r.Str("videoId");
        if (id.Length == 0) return null;
        return new VideoItem
        {
            Id = id,
            Title = r.Text("headline"),
            ViewsText = r.Text("viewCountText"),
            IsShort = true,
        };
    }

    private static VideoItem? ParseShortsLockup(JsonNode r)
    {
        var id = r.Find("reelWatchEndpoint").Str("videoId");
        if (id.Length == 0)
        {
            var entity = r.Str("entityId");
            const string prefix = "shorts-shelf-item-";
            if (entity.StartsWith(prefix, StringComparison.Ordinal)) id = entity[prefix.Length..];
        }
        if (id.Length == 0) return null;
        var title = r.Text("overlayMetadata", "primaryText");
        var views = r.Text("overlayMetadata", "secondaryText");
        if (title.Length == 0)
        {
            var a11y = r.Str("accessibilityText");
            int comma = a11y.LastIndexOf(',');
            title = comma > 0 ? a11y[..comma] : a11y;
        }
        return new VideoItem { Id = id, Title = title, ViewsText = views, IsShort = true };
    }

    public static ChannelItem? ParseChannel(JsonNode r)
    {
        var id = r.Str("channelId");
        if (id.Length == 0) return null;
        var c = new ChannelItem
        {
            Id = id,
            Title = r.Text("title"),
            Description = r.Text("descriptionSnippet"),
            AvatarUrl = r.At("thumbnail", "thumbnails").BestThumb(176),
        };
        // YouTube zamienił pola: w subscriberCountText bywa @uchwyt, a w videoCountText liczba subskrybentów.
        foreach (var t in new[] { r.Text("subscriberCountText"), r.Text("videoCountText") })
        {
            if (t.Length == 0) continue;
            if (t.StartsWith('@')) c.Handle = t;
            else if (c.SubscribersText.Length == 0) c.SubscribersText = t;
            else c.VideoCountText = t;
        }
        if (c.Handle.Length == 0)
        {
            var url = r.Find("canonicalBaseUrl").Str();
            if (url.StartsWith("/@", StringComparison.Ordinal)) c.Handle = url[1..];
        }
        return c;
    }

    public static PlaylistItem? ParsePlaylist(JsonNode r)
    {
        var id = r.Str("playlistId");
        if (id.Length == 0) return null;
        var p = new PlaylistItem
        {
            Id = id,
            Title = r.Text("title"),
            VideoCountText = r.Text("videoCountText") is { Length: > 0 } vc ? vc : r.Text("videoCountShortText"),
            FirstVideoId = r.Find("watchEndpoint").Str("videoId"),
            IsMix = id.StartsWith("RD", StringComparison.Ordinal),
        };
        if (p.VideoCountText.Length == 0 && r.Str("videoCount") is { Length: > 0 } cnt) p.VideoCountText = int.TryParse(cnt, out int n) ? YouTubeClient.Count(n, "film", "filmy", "filmów", "video", "videos") : cnt;
        (p.ChannelName, p.ChannelId) = Byline(r);
        var thumbs = r.At("thumbnails", 0, "thumbnails") ?? r.At("thumbnail", "thumbnails") ?? r.At("thumbnailRenderer")?.Find("thumbnails");
        p.Thumbnail = thumbs.BestThumb(320);
        return p;
    }

    public static YtItem? ParseLockup(JsonNode r)
    {
        var type = r.Str("contentType");
        var id = r.Str("contentId");
        if (id.Length == 0) return null;
        var meta = r.At("metadata", "lockupMetadataViewModel");
        var title = meta.Text("title");
        var rows = meta.At("metadata", "contentMetadataViewModel", "metadataRows")
            .Arr()
            .Select(row => row.Arr("metadataParts").Select(p => p.Text("text")).Where(s => s.Length > 0).ToList())
            .Where(l => l.Count > 0)
            .ToList();
        var channelId = meta.Find("browseId").Str();
        if (!channelId.StartsWith("UC", StringComparison.Ordinal)) channelId = r.At("metadata").Find("browseId").Str();

        var badges = new List<JsonNode>();
        r.At("contentImage").FindAll("thumbnailBadgeViewModel", badges);
        var badgeText = badges.Select(b => b.Str("text")).FirstOrDefault(t => t.Length > 0) ?? "";

        if (type == "LOCKUP_CONTENT_TYPE_VIDEO")
        {
            var v = new VideoItem
            {
                Id = id,
                Title = title,
                ChannelName = rows.Count > 0 ? rows[0][0] : "",
                ChannelId = channelId,
            };
            if (rows.Count > 1)
            {
                v.ViewsText = rows[1].Count > 0 ? rows[1][0] : "";
                v.PublishedText = rows[1].Count > 1 ? rows[1][1] : "";
            }
            if (Regex.IsMatch(badgeText, @"^\d+(:\d\d)+$")) { v.DurationText = badgeText; v.DurationSeconds = TextUtilDuration(badgeText); }
            else if (badgeText.Length > 0 && badges.Any(b => b.Str("badgeStyle").Contains("LIVE", StringComparison.Ordinal))) v.IsLive = true;
            return v;
        }

        if (type is "LOCKUP_CONTENT_TYPE_PLAYLIST" or "LOCKUP_CONTENT_TYPE_PODCAST" or "LOCKUP_CONTENT_TYPE_ALBUM")
        {
            var images = new List<JsonNode>();
            r.At("contentImage").FindAll("sources", images);
            var p = new PlaylistItem
            {
                Id = id,
                Title = title,
                ChannelName = rows.Count > 0 ? rows[0][0] : "",
                ChannelId = channelId,
                VideoCountText = badgeText,
                FirstVideoId = r.Find("watchEndpoint").Str("videoId"),
                Thumbnail = images.Count > 0 ? images[0].BestThumb(360) : "",
                IsMix = id.StartsWith("RD", StringComparison.Ordinal),
            };
            // Składanki: w miejscu autora jest „YouTube”, a plakietka to etykieta („Składanka”), nie liczba filmów.
            if (p.IsMix && p.ChannelName == "YouTube") p.ChannelName = "";
            if (!p.VideoCountText.Any(char.IsDigit)) p.VideoCountText = "";
            return p;
        }

        if (type == "LOCKUP_CONTENT_TYPE_CHANNEL")
            return new ChannelItem { Id = id, Title = title };
        return null;
    }

    // --------------- Kanał ---------------

    public static ChannelPage ParseChannel(JsonNode root, string channelId)
    {
        var page = new ChannelPage();
        var meta = root.At("metadata", "channelMetadataRenderer");
        var ch = page.Channel;
        ch.Id = meta.Str("externalId") is { Length: > 0 } ext ? ext : channelId;
        ch.Title = meta.Str("title");
        ch.Description = meta.Str("description");
        ch.AvatarUrl = meta.At("avatar", "thumbnails").BestThumb(176);
        var vanity = meta.Str("vanityChannelUrl");
        int at = vanity.IndexOf("/@", StringComparison.Ordinal);
        if (at >= 0) ch.Handle = Uri.UnescapeDataString(vanity[(at + 1)..]);

        var header = root.At("header", "pageHeaderRenderer", "content", "pageHeaderViewModel");
        if (header != null)
        {
            if (ch.Title.Length == 0) ch.Title = header.At("title").Find("content").Str();
            var parts = new List<string>();
            foreach (var row in header.At("metadata", "contentMetadataViewModel", "metadataRows").Arr())
                foreach (var part in row.Arr("metadataParts"))
                {
                    var t = part.Text("text");
                    if (t.Length > 0) parts.Add(t);
                }
            foreach (var t in parts)
            {
                if (t.StartsWith('@')) { if (ch.Handle.Length == 0) ch.Handle = t; }
                else if (ch.SubscribersText.Length == 0) ch.SubscribersText = t;
                else if (ch.VideoCountText.Length == 0) ch.VideoCountText = t;
            }
            var banner = header.At("banner", "imageBannerViewModel", "image", "sources");
            page.BannerUrl = banner.BestThumb(1060);
            if (ch.AvatarUrl.Length == 0)
                ch.AvatarUrl = header.At("image").Find("sources").BestThumb(176);
        }
        else if (root.At("header", "c4TabbedHeaderRenderer") is { } c4)
        {
            ch.SubscribersText = c4.Text("subscriberCountText");
            ch.VideoCountText = c4.Text("videosCountText");
            if (ch.Handle.Length == 0) ch.Handle = c4.Text("channelHandleText");
        }

        foreach (var tab in root.At("contents", "twoColumnBrowseResultsRenderer", "tabs").Arr())
        {
            var tr = tab.At("tabRenderer") ?? tab.At("expandableTabRenderer");
            if (tr == null) continue;
            var t = new ChannelTab
            {
                Title = tr.Str("title"),
                Params = tr.At("endpoint", "browseEndpoint").Str("params"),
                Selected = tr.Str("selected") == "true",
            };
            if (t.Title.Length > 0 && t.Params.Length > 0) page.Tabs.Add(t);
            if (t.Selected)
            {
                var content = tr.At("content");
                CollectItems(content, page.Content);
                page.Sorts = ParseChips(content);
            }
        }
        return page;
    }

    public static List<SortChip> ParseChips(JsonNode? node)
    {
        var chips = new List<SortChip>();
        var list = new List<JsonNode>();
        node.FindAll("chipCloudChipRenderer", list);
        foreach (var c in list)
        {
            var token = c.Find("continuationCommand").Str("token");
            if (token.Length == 0) continue;
            chips.Add(new SortChip { Title = c.Text("text"), Token = token, Selected = c.Str("isSelected") == "true" });
        }
        if (chips.Count == 0)
        {
            list.Clear();
            node.FindAll("chipViewModel", list);
            foreach (var c in list)
            {
                var token = c.Find("continuationCommand").Str("token");
                if (token.Length == 0) continue;
                chips.Add(new SortChip { Title = c.Str("text"), Token = token, Selected = c.Str("selected") == "true" });
            }
        }
        return chips;
    }

    // --------------- Playlista ---------------

    public static PlaylistPage ParsePlaylist(JsonNode root, string playlistId)
    {
        var page = new PlaylistPage();
        var p = page.Playlist;
        p.Id = playlistId;
        p.Title = root.Str("metadata", "playlistMetadataRenderer", "title");
        page.Description = root.Str("metadata", "playlistMetadataRenderer", "description");

        var h = root.At("header", "playlistHeaderRenderer");
        if (h != null)
        {
            if (p.Title.Length == 0) p.Title = h.Text("title");
            p.ChannelName = h.Text("ownerText");
            p.ChannelId = h.At("ownerText").Find("browseId").Str();
            p.VideoCountText = h.Text("numVideosText");
            page.ViewsText = h.Text("viewCountText");
            if (page.Description.Length == 0) page.Description = h.Text("descriptionText");
        }
        else if (root.At("header", "pageHeaderRenderer", "content", "pageHeaderViewModel") is { } ph)
        {
            if (p.Title.Length == 0) p.Title = ph.At("title").Find("content").Str();
            var parts = new List<string>();
            foreach (var row in ph.At("metadata", "contentMetadataViewModel", "metadataRows").Arr())
                foreach (var part in row.Arr("metadataParts"))
                {
                    var t = part.Text("text");
                    // Nowy układ: autor w avatarStackViewModel z tekstem „autor: Nazwa” / „by Name”.
                    if (t.Length == 0 && part.Find("avatarStackViewModel") is { } stack) t = StripAuthorPrefix(stack.Text("text"));
                    if (t.Length > 0) parts.Add(t);
                    if (p.ChannelId.Length == 0 && part.Find("browseId").Str() is { Length: > 0 } bid && bid.StartsWith("UC", StringComparison.Ordinal))
                    {
                        p.ChannelId = bid;
                        p.ChannelName = t;
                    }
                }
            foreach (var t in parts)
            {
                if (t == p.ChannelName || t.StartsWith("Autor", StringComparison.Ordinal) || t.StartsWith("by ", StringComparison.Ordinal)) continue;
                if (p.VideoCountText.Length == 0 && char.IsDigit(t[0]) && (t.Contains("film", StringComparison.OrdinalIgnoreCase) || t.Contains("video", StringComparison.OrdinalIgnoreCase))) p.VideoCountText = t;
                else if (page.ViewsText.Length == 0 && (t.Contains("wyświetl", StringComparison.OrdinalIgnoreCase) || t.Contains("view", StringComparison.OrdinalIgnoreCase))) page.ViewsText = t;
            }
            if (page.Description.Length == 0) page.Description = ph.At("description").Find("content").Str();
        }

        var contents = root.At("contents", "twoColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content");
        CollectItems(contents, page.Content);
        if (page.Content.Items.FirstOrDefault() is VideoItem first) p.FirstVideoId = first.Id;
        return page;
    }

    /// <summary>„autor: Nazwa” / „by Name” → „Nazwa”.</summary>
    internal static string StripAuthorPrefix(string text)
    {
        text = text.Trim();
        if (text.StartsWith("by ", StringComparison.OrdinalIgnoreCase)) return text[3..].Trim();
        int colon = text.IndexOf(": ", StringComparison.Ordinal);
        return colon > 0 && colon < 20 ? text[(colon + 2)..].Trim() : text;
    }

    // --------------- Szczegóły filmu (/next) ---------------

    public static VideoDetails ParseNext(JsonNode root, string videoId)
    {
        var d = new VideoDetails();
        var v = d.Video;
        v.Id = videoId;
        var results = root.At("contents", "twoColumnWatchNextResults", "results", "results", "contents");
        foreach (var c in results.Arr())
        {
            if (c.At("videoPrimaryInfoRenderer") is { } pri)
            {
                v.Title = pri.Text("title");
                d.ViewsFull = pri.At("viewCount", "videoViewCountRenderer").Text("viewCount");
                v.ViewsText = pri.At("viewCount", "videoViewCountRenderer").Text("shortViewCount");
                if (v.ViewsText.Length == 0) v.ViewsText = d.ViewsFull;
                d.DateText = pri.Text("dateText");
                v.PublishedText = pri.Text("relativeDateText");
                var like = pri.Find("segmentedLikeDislikeButtonViewModel") ?? pri.Find("likeButtonViewModel");
                var title = like.Find("buttonViewModel").Str("title");
                if (title.Length == 0) title = like.Find("title").Str();
                d.LikesText = title;
                if (pri.Find("badges") is { } badges && badges.ToJsonString().Contains("LIVE", StringComparison.Ordinal)) v.IsLive = true;
            }
            else if (c.At("videoSecondaryInfoRenderer") is { } sec)
            {
                var owner = sec.At("owner", "videoOwnerRenderer");
                v.ChannelName = owner.Text("title");
                v.ChannelId = owner.Find("browseId").Str();
                d.ChannelSubscribers = owner.Text("subscriberCountText");
                d.ChannelAvatar = owner.At("thumbnail", "thumbnails").BestThumb(88);
                d.Description = sec.Text("attributedDescription");
                if (d.Description.Length == 0) d.Description = sec.Text("description");
            }
            else if (c.At("itemSectionRenderer") is { } isr && isr.Str("sectionIdentifier") == "comment-item-section")
            {
                d.CommentsToken = isr.Find("continuationCommand").Str("token");
            }
        }

        // Rozdziały i liczba komentarzy z paneli
        var markers = new List<JsonNode>();
        root.At("engagementPanels").FindAll("macroMarkersListItemRenderer", markers);
        foreach (var m in markers)
        {
            var t = m.Text("title");
            var startSec = (int)m.Find("watchEndpoint").Long("startTimeSeconds");
            if (t.Length > 0 && !d.Chapters.Any(x => x.StartSeconds == startSec && x.Title == t))
                d.Chapters.Add(new Chapter { Title = t, StartSeconds = startSec });
        }
        if (d.Chapters.Count == 0)
        {
            var chapters = new List<JsonNode>();
            root.At("playerOverlays").FindAll("chapterRenderer", chapters);
            foreach (var ch in chapters)
                d.Chapters.Add(new Chapter { Title = ch.Text("title"), StartSeconds = (int)(ch.Long("timeRangeStartMillis") / 1000) });
        }
        d.Chapters.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));

        foreach (var panel in root.Arr("engagementPanels"))
        {
            var ep = panel.At("engagementPanelSectionListRenderer");
            if (ep?.Str("panelIdentifier") == "engagement-panel-comments-section")
                d.CommentsCount = ep.At("header", "engagementPanelTitleHeaderRenderer").Text("contextualInfo");
        }

        var secondary = root.At("contents", "twoColumnWatchNextResults", "secondaryResults", "secondaryResults", "results");
        CollectItems(secondary, d.Related);

        // Szczegóły z miniatury overlay (czas trwania) — mogą nie być dostępne; uzupełniane później z historii/listy.
        return d;
    }

    /// <summary>Kontynuacja listy podobnych filmów (/next z tokenem).</summary>
    public static FeedPage ParseContinuation(JsonNode root)
    {
        var page = new FeedPage();
        foreach (var key in new[] { "onResponseReceivedActions", "onResponseReceivedCommands", "onResponseReceivedEndpoints" })
        {
            foreach (var a in root.Arr(key))
            {
                var items = a.At("appendContinuationItemsAction", "continuationItems")
                            ?? a.At("reloadContinuationItemsCommand", "continuationItems");
                CollectItems(items, page);
            }
        }
        if (page.Items.Count == 0 && page.Continuation == null)
            CollectItems(root.At("continuationContents"), page);
        return page;
    }

    // --------------- Komentarze ---------------

    public static CommentsPage ParseComments(JsonNode root)
    {
        var page = new CommentsPage();
        var entities = new Dictionary<string, JsonNode>();
        foreach (var m in root.At("frameworkUpdates", "entityBatchUpdate", "mutations").Arr())
        {
            var p = m.At("payload", "commentEntityPayload");
            if (p != null) entities[p.Str("key")] = p;
        }

        foreach (var key in new[] { "onResponseReceivedEndpoints", "onResponseReceivedActions" })
        {
            foreach (var ep in root.Arr(key))
            {
                var items = ep.At("reloadContinuationItemsCommand", "continuationItems")
                            ?? ep.At("appendContinuationItemsAction", "continuationItems");
                foreach (var item in items.Arr())
                {
                    if (item.At("commentsHeaderRenderer") is { } hdr)
                    {
                        page.HeaderCount = hdr.Text("countText");
                        foreach (var sub in hdr.Find("subMenuItems").Arr())
                        {
                            var tok = sub.Find("continuationCommand").Str("token");
                            if (tok.Length > 0)
                                page.Sorts.Add(new SortChip { Title = sub.Str("title"), Token = tok, Selected = sub.Str("selected") == "true" });
                        }
                    }
                    else if (item.At("commentThreadRenderer") is { } thread)
                    {
                        var c = FromViewModel(thread.At("commentViewModel", "commentViewModel"), entities);
                        if (c == null) continue;
                        c.RepliesToken = thread.At("replies").Find("continuationCommand").Str("token") is { Length: > 0 } rt ? rt : null;
                        if (thread.At("commentViewModel").Find("pinnedText") != null) c.IsPinned = true;
                        page.Comments.Add(c);
                    }
                    else if (item.At("commentViewModel") is { } cvm)
                    {
                        var c = FromViewModel(cvm, entities);
                        if (c != null) { c.IsReply = true; page.Comments.Add(c); }
                    }
                    else if (item.At("continuationItemRenderer") is { } cont)
                    {
                        var tok = cont.Find("continuationCommand").Str("token");
                        if (tok.Length > 0) page.Continuation = tok;
                    }
                }
            }
        }
        return page;
    }

    private static Comment? FromViewModel(JsonNode? vm, Dictionary<string, JsonNode> entities)
    {
        if (vm == null) return null;
        var key = vm.Str("commentKey");
        if (!entities.TryGetValue(key, out var e)) return null;
        var c = new Comment
        {
            Author = e.Str("author", "displayName"),
            Text = e.Str("properties", "content", "content"),
            Published = e.Str("properties", "publishedTime"),
            Likes = e.Str("toolbar", "likeCountNotliked").Trim(),
            ReplyCountText = e.Str("toolbar", "replyCount"),
            IsCreator = e.Str("author", "isCreator") == "true",
            IsReply = e.Long("properties", "replyLevel") > 0,
        };
        if (int.TryParse(new string(c.ReplyCountText.Where(char.IsDigit).ToArray()), out int rc)) c.ReplyCount = rc;
        if (vm.Find("pinnedText") != null) c.IsPinned = true;
        return c;
    }
}
