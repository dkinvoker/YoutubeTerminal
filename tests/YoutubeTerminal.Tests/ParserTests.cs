using System.Text.Json.Nodes;
using YoutubeTerminal.YouTube;

namespace YoutubeTerminal.Tests;

/// <summary>Parsery odpowiedzi InnerTube — próbki JSON odwzorowują strukturę prawdziwych odpowiedzi YouTube.</summary>
public class ParserTests
{
    private static JsonNode N(string json) => JsonNode.Parse(json)!;

    private const string VideoRenderer = """
        {
          "videoId": "abcdefghijk",
          "title": { "runs": [ { "text": "Tytuł " }, { "text": "filmu" } ] },
          "ownerText": { "runs": [ { "text": "Kanał", "navigationEndpoint": { "browseEndpoint": { "browseId": "UC1234567890123456789012" } } } ] },
          "lengthText": { "simpleText": "12:34" },
          "viewCountText": { "simpleText": "1 234 wyświetlenia" },
          "publishedTimeText": { "simpleText": "2 dni temu" },
          "detailedMetadataSnippets": [ { "snippetText": { "runs": [ { "text": "Opis " }, { "text": "skrócony" } ] } } ]
        }
        """;

    [Fact]
    public void ParseVideo_ReadsAllFields()
    {
        var v = Parsers.ParseVideo(N(VideoRenderer))!;
        Assert.Equal("abcdefghijk", v.Id);
        Assert.Equal("Tytuł filmu", v.Title);
        Assert.Equal("Kanał", v.ChannelName);
        Assert.Equal("UC1234567890123456789012", v.ChannelId);
        Assert.Equal("12:34", v.DurationText);
        Assert.Equal(754, v.DurationSeconds);
        Assert.Equal("1 234 wyświetlenia", v.ViewsText);
        Assert.Equal("2 dni temu", v.PublishedText);
        Assert.Equal("Opis skrócony", v.Description);
        Assert.False(v.IsLive);
        Assert.False(v.IsShort);
        Assert.Equal("https://i.ytimg.com/vi/abcdefghijk/mqdefault.jpg", v.ThumbnailUrl);
        Assert.Equal("https://www.youtube.com/watch?v=abcdefghijk", v.Url);
    }

    [Fact]
    public void ParseVideo_WithoutIdReturnsNull()
    {
        Assert.Null(Parsers.ParseVideo(N("""{"title":{"simpleText":"x"}}""")));
    }

    [Fact]
    public void ParseVideo_LiveAndShortsFromOverlays()
    {
        var live = Parsers.ParseVideo(N("""
            {"videoId":"live0000001","title":{"simpleText":"Radio"},
             "thumbnailOverlays":[{"thumbnailOverlayTimeStatusRenderer":{"text":{"simpleText":"NA ŻYWO"},"style":"LIVE"}}]}
            """))!;
        Assert.True(live.IsLive);
        Assert.Equal("", live.DurationText);

        var shortVid = Parsers.ParseVideo(N("""
            {"videoId":"short000001","title":{"simpleText":"Short"},
             "thumbnailOverlays":[{"thumbnailOverlayTimeStatusRenderer":{"text":{"simpleText":"0:30"},"style":"SHORTS"}}]}
            """))!;
        Assert.True(shortVid.IsShort);
        Assert.Equal("https://www.youtube.com/shorts/short000001", shortVid.Url);
    }

    [Fact]
    public void ParseVideo_LiveBadgeAndUpcoming()
    {
        var v = Parsers.ParseVideo(N("""
            {"videoId":"live0000002","title":{"simpleText":"x"},
             "badges":[{"metadataBadgeRenderer":{"style":"BADGE_STYLE_TYPE_LIVE_NOW","label":"NA ŻYWO"}}],
             "upcomingEventData":{"startTime":"1800000000"}}
            """))!;
        Assert.True(v.IsLive);
        Assert.True(v.IsUpcoming);
    }

    [Fact]
    public void ParseVideo_PlaylistVideoInfoRunsAndLengthSeconds()
    {
        var v = Parsers.ParseVideo(N("""
            {"videoId":"plvideo0001","title":{"runs":[{"text":"W playliście"}]},
             "shortBylineText":{"runs":[{"text":"Autor","navigationEndpoint":{"browseEndpoint":{"browseId":"UCaaaaaaaaaaaaaaaaaaaaaa"}}}]},
             "lengthSeconds":"125",
             "videoInfo":{"runs":[{"text":"1,2 mln wyświetleń"},{"text":" • "},{"text":"3 lata temu"}]}}
            """))!;
        Assert.Equal("Autor", v.ChannelName);
        Assert.Equal("UCaaaaaaaaaaaaaaaaaaaaaa", v.ChannelId);
        Assert.Equal(125, v.DurationSeconds);
        Assert.Equal("2:05", v.DurationText);
        Assert.Equal("1,2 mln wyświetleń", v.ViewsText);
        Assert.Equal("3 lata temu", v.PublishedText);
    }

    [Fact]
    public void ParseChannel_SwapsHandleAndSubscribers()
    {
        // YouTube zwraca @uchwyt w polu subscriberCountText, a subskrybentów w videoCountText
        var c = Parsers.ParseChannel(N("""
            {"channelId":"UCSJ4gkVC6NrvII8umztf0Ow","title":{"simpleText":"Lofi Girl"},
             "subscriberCountText":{"simpleText":"@LofiGirl"},
             "videoCountText":{"simpleText":"15,8 mln subskrybentów"},
             "descriptionSnippet":{"runs":[{"text":"Opis kanału"}]},
             "thumbnail":{"thumbnails":[{"url":"//yt3.ggpht.com/a=s88","width":88},{"url":"//yt3.ggpht.com/a=s176","width":176}]}}
            """))!;
        Assert.Equal("UCSJ4gkVC6NrvII8umztf0Ow", c.Id);
        Assert.Equal("Lofi Girl", c.Title);
        Assert.Equal("@LofiGirl", c.Handle);
        Assert.Equal("15,8 mln subskrybentów", c.SubscribersText);
        Assert.Equal("Opis kanału", c.Description);
        Assert.Equal("https://yt3.ggpht.com/a=s176", c.AvatarUrl);
    }

    [Fact]
    public void ParseChannel_HandleFromCanonicalUrl()
    {
        var c = Parsers.ParseChannel(N("""
            {"channelId":"UCbbbbbbbbbbbbbbbbbbbbbb","title":{"simpleText":"X"},
             "subscriberCountText":{"simpleText":"100 tys. subskrybentów"},
             "navigationEndpoint":{"browseEndpoint":{"canonicalBaseUrl":"/@xchannel"}}}
            """))!;
        Assert.Equal("@xchannel", c.Handle);
        Assert.Equal("100 tys. subskrybentów", c.SubscribersText);
    }

    [Fact]
    public void ParseLockup_Video()
    {
        var item = Parsers.ParseLockup(N("""
            {"contentId":"lockupvid01","contentType":"LOCKUP_CONTENT_TYPE_VIDEO",
             "contentImage":{"thumbnailViewModel":{"overlays":[{"thumbnailOverlayBadgeViewModel":{"thumbnailBadges":[{"thumbnailBadgeViewModel":{"text":"1:02:03"}}]}}]}},
             "metadata":{"lockupMetadataViewModel":{
                "title":{"content":"Podobny film"},
                "image":{"decoratedAvatarViewModel":{"rendererContext":{"commandContext":{"onTap":{"innertubeCommand":{"browseEndpoint":{"browseId":"UCcccccccccccccccccccccc"}}}}}}},
                "metadata":{"contentMetadataViewModel":{"metadataRows":[
                   {"metadataParts":[{"text":{"content":"Autor"}}]},
                   {"metadataParts":[{"text":{"content":"650 tys."}},{"text":{"content":"1 mies. temu"}}]}]}}}}}
            """));
        var v = Assert.IsType<VideoItem>(item);
        Assert.Equal("lockupvid01", v.Id);
        Assert.Equal("Podobny film", v.Title);
        Assert.Equal("Autor", v.ChannelName);
        Assert.Equal("UCcccccccccccccccccccccc", v.ChannelId);
        Assert.Equal("650 tys.", v.ViewsText);
        Assert.Equal("1 mies. temu", v.PublishedText);
        Assert.Equal("1:02:03", v.DurationText);
        Assert.Equal(3723, v.DurationSeconds);
    }

    [Fact]
    public void ParseLockup_PlaylistAndMix()
    {
        var item = Parsers.ParseLockup(N("""
            {"contentId":"PLxyz","contentType":"LOCKUP_CONTENT_TYPE_PLAYLIST",
             "contentImage":{"collectionThumbnailViewModel":{"primaryThumbnail":{"thumbnailViewModel":{
                "image":{"sources":[{"url":"https://i.ytimg.com/vi/first000001/hq.jpg","width":360}]},
                "overlays":[{"thumbnailOverlayBadgeViewModel":{"thumbnailBadges":[{"thumbnailBadgeViewModel":{"text":"76 filmów"}}]}}]}}}},
             "metadata":{"lockupMetadataViewModel":{"title":{"content":"Moja playlista"},
                "metadata":{"contentMetadataViewModel":{"metadataRows":[{"metadataParts":[{"text":{"content":"Twórca"}}]}]}}}},
             "rendererContext":{"commandContext":{"onTap":{"innertubeCommand":{"watchEndpoint":{"videoId":"first000001","playlistId":"PLxyz"}}}}}}
            """));
        var p = Assert.IsType<PlaylistItem>(item);
        Assert.Equal("PLxyz", p.Id);
        Assert.Equal("Moja playlista", p.Title);
        Assert.Equal("Twórca", p.ChannelName);
        Assert.Equal("76 filmów", p.VideoCountText);
        Assert.Equal("first000001", p.FirstVideoId);
        Assert.Equal("https://i.ytimg.com/vi/first000001/hq.jpg", p.ThumbnailUrl);
        Assert.False(p.IsMix);

        var mix = Assert.IsType<PlaylistItem>(Parsers.ParseLockup(N("""
            {"contentId":"RDabc","contentType":"LOCKUP_CONTENT_TYPE_PLAYLIST","metadata":{"lockupMetadataViewModel":{"title":{"content":"Składanka"}}}}
            """)));
        Assert.True(mix.IsMix);
    }

    [Fact]
    public void ParseLockup_UnknownTypeIsIgnored()
    {
        Assert.Null(Parsers.ParseLockup(N("""{"contentId":"x","contentType":"LOCKUP_CONTENT_TYPE_SOMETHING"}""")));
        Assert.Null(Parsers.ParseLockup(N("""{"contentType":"LOCKUP_CONTENT_TYPE_VIDEO"}""")));
    }

    [Fact]
    public void ParsePlaylistRenderer()
    {
        var p = Parsers.ParsePlaylist(N("""
            {"playlistId":"PLold","title":{"simpleText":"Stara playlista"},"videoCount":"12",
             "shortBylineText":{"runs":[{"text":"Autor","navigationEndpoint":{"browseEndpoint":{"browseId":"UCdddddddddddddddddddddd"}}}]},
             "navigationEndpoint":{"watchEndpoint":{"videoId":"oldfirst001"}}}
            """))!;
        Assert.Equal("PLold", p.Id);
        Assert.Equal("12 filmów", p.VideoCountText);
        Assert.Equal("oldfirst001", p.FirstVideoId);
        Assert.Equal("Autor", p.ChannelName);
        Assert.Equal("https://i.ytimg.com/vi/oldfirst001/mqdefault.jpg", p.ThumbnailUrl);
    }

    [Fact]
    public void CollectItems_KeepsOrderSkipsAdsDeduplicatesAndFindsContinuation()
    {
        var root = N("""
            {"contents":{"sectionListRenderer":{"contents":[
              {"itemSectionRenderer":{"contents":[
                 {"adSlotRenderer":{"videoRenderer":{"videoId":"adadadadada","title":{"simpleText":"Reklama"}}}},
                 {"videoRenderer":"__VIDEO__"},
                 {"channelRenderer":{"channelId":"UCeeeeeeeeeeeeeeeeeeeeee","title":{"simpleText":"Kanał"}}},
                 {"shelfRenderer":{"content":{"verticalListRenderer":{"items":[
                    {"videoRenderer":"__VIDEO__"},
                    {"videoRenderer":{"videoId":"second00001","title":{"simpleText":"Drugi"}}}]}}}},
                 {"reelShelfRenderer":{"items":[{"shortsLockupViewModel":{
                    "entityId":"shorts-shelf-item-short000002",
                    "overlayMetadata":{"primaryText":{"content":"Krótki"},"secondaryText":{"content":"5 tys. wyświetleń"}},
                    "onTap":{"innertubeCommand":{"reelWatchEndpoint":{"videoId":"short000002"}}}}}]}}
              ]}},
              {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"NEXT_PAGE"}}}}
            ]}}}
            """.Replace("\"__VIDEO__\"", VideoRenderer));
        var page = new FeedPage();
        Parsers.CollectItems(root, page);

        Assert.Equal(["abcdefghijk", "UCeeeeeeeeeeeeeeeeeeeeee", "second00001", "short000002"], page.Items.Select(i => i.Id));
        Assert.DoesNotContain(page.Items, i => i.Id == "adadadadada");
        var s = Assert.IsType<VideoItem>(page.Items[3]);
        Assert.True(s.IsShort);
        Assert.Equal("Krótki", s.Title);
        Assert.Equal("5 tys. wyświetleń", s.ViewsText);
        Assert.Equal("NEXT_PAGE", page.Continuation);
    }

    [Fact]
    public void ParseContinuation_AppendAction()
    {
        var root = N("""
            {"onResponseReceivedCommands":[{"appendContinuationItemsAction":{"continuationItems":[
               {"itemSectionRenderer":{"contents":[{"videoRenderer":{"videoId":"cont0000001","title":{"simpleText":"c"}}}]}},
               {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"PAGE3"}}}}]}}]}
            """);
        var page = Parsers.ParseContinuation(root);
        Assert.Equal("cont0000001", Assert.Single(page.Items).Id);
        Assert.Equal("PAGE3", page.Continuation);
    }

    [Fact]
    public void ParseChips_ReadsSortOptions()
    {
        var chips = Parsers.ParseChips(N("""
            {"feedFilterChipBarRenderer":{"contents":[
               {"chipCloudChipRenderer":{"text":{"simpleText":"Najnowsze"},"isSelected":true,"navigationEndpoint":{"continuationCommand":{"token":"T1"}}}},
               {"chipCloudChipRenderer":{"text":{"simpleText":"Popularne"},"navigationEndpoint":{"continuationCommand":{"token":"T2"}}}}]}}
            """));
        Assert.Equal(2, chips.Count);
        Assert.Equal(("Najnowsze", "T1", true), (chips[0].Title, chips[0].Token, chips[0].Selected));
        Assert.Equal(("Popularne", "T2", false), (chips[1].Title, chips[1].Token, chips[1].Selected));
    }

    [Fact]
    public void ParseNext_DetailsChaptersCommentsRelated()
    {
        var root = N("""
            {"contents":{"twoColumnWatchNextResults":{
               "results":{"results":{"contents":[
                  {"videoPrimaryInfoRenderer":{
                     "title":{"runs":[{"text":"Wielki film"}]},
                     "viewCount":{"videoViewCountRenderer":{"viewCount":{"simpleText":"1 234 567 wyświetleń"},"shortViewCount":{"simpleText":"1,2 mln wyświetleń"}}},
                     "dateText":{"simpleText":"4 lip 2026"},
                     "relativeDateText":{"simpleText":"4 miesiące temu"},
                     "videoActions":{"menuRenderer":{"topLevelButtons":[{"segmentedLikeDislikeButtonViewModel":{"likeButtonViewModel":{"likeButtonViewModel":{"toggleButtonViewModel":{"toggleButtonViewModel":{"defaultButtonViewModel":{"buttonViewModel":{"title":"2,6 tys."}}}}}}}}]}}}},
                  {"videoSecondaryInfoRenderer":{
                     "owner":{"videoOwnerRenderer":{"title":{"runs":[{"text":"Twórca","navigationEndpoint":{"browseEndpoint":{"browseId":"UCffffffffffffffffffffff"}}}]},
                        "subscriberCountText":{"simpleText":"613 tys. subskrybentów"},
                        "thumbnail":{"thumbnails":[{"url":"https://yt3/avatar","width":88}]}}},
                     "attributedDescription":{"content":"Pełny opis\nz linkiem"}}},
                  {"itemSectionRenderer":{"sectionIdentifier":"comment-item-section","contents":[
                     {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"COMMENTS"}}}}]}}]}},
               "secondaryResults":{"secondaryResults":{"results":[
                  {"lockupViewModel":{"contentId":"related0001","contentType":"LOCKUP_CONTENT_TYPE_VIDEO","metadata":{"lockupMetadataViewModel":{"title":{"content":"Podobny"}}}}},
                  {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"RELATED2"}}}}]}}}},
             "engagementPanels":[
               {"engagementPanelSectionListRenderer":{"panelIdentifier":"engagement-panel-comments-section",
                  "header":{"engagementPanelTitleHeaderRenderer":{"contextualInfo":{"runs":[{"text":"164"}]}}}}},
               {"engagementPanelSectionListRenderer":{"panelIdentifier":"engagement-panel-macro-markers-description-chapters",
                  "content":{"macroMarkersListRenderer":{"contents":[
                     {"macroMarkersListItemRenderer":{"title":{"simpleText":"Drugi"},"onTap":{"watchEndpoint":{"startTimeSeconds":95}}}},
                     {"macroMarkersListItemRenderer":{"title":{"simpleText":"Wstęp"},"onTap":{"watchEndpoint":{"startTimeSeconds":0}}}},
                     {"macroMarkersListItemRenderer":{"title":{"simpleText":"Wstęp"},"onTap":{"watchEndpoint":{"startTimeSeconds":0}}}}]}}}}]}
            """);
        var d = Parsers.ParseNext(root, "bigvideo001");

        Assert.Equal("bigvideo001", d.Video.Id);
        Assert.Equal("Wielki film", d.Video.Title);
        Assert.Equal("1 234 567 wyświetleń", d.ViewsFull);
        Assert.Equal("1,2 mln wyświetleń", d.Video.ViewsText);
        Assert.Equal("4 lip 2026", d.DateText);
        Assert.Equal("4 miesiące temu", d.Video.PublishedText);
        Assert.Equal("2,6 tys.", d.LikesText);
        Assert.Equal("Twórca", d.Video.ChannelName);
        Assert.Equal("UCffffffffffffffffffffff", d.Video.ChannelId);
        Assert.Equal("613 tys. subskrybentów", d.ChannelSubscribers);
        Assert.Equal("Pełny opis\nz linkiem", d.Description);
        Assert.Equal("COMMENTS", d.CommentsToken);
        Assert.Equal("164", d.CommentsCount);

        // Rozdziały posortowane i bez duplikatów
        Assert.Equal([(0, "Wstęp"), (95, "Drugi")], d.Chapters.Select(c => (c.StartSeconds, c.Title)));

        Assert.Equal("related0001", Assert.Single(d.Related.Items).Id);
        Assert.Equal("RELATED2", d.Related.Continuation);
    }

    [Fact]
    public void ParseComments_JoinsEntitiesWithThreads()
    {
        var root = N("""
            {"onResponseReceivedEndpoints":[
               {"reloadContinuationItemsCommand":{"slot":"RELOAD_CONTINUATION_SLOT_HEADER","continuationItems":[
                  {"commentsHeaderRenderer":{"countText":{"runs":[{"text":"164"},{"text":" komentarze"}]},
                     "sortMenu":{"sortFilterSubMenuRenderer":{"subMenuItems":[
                        {"title":"Najpopularniejsze","selected":true,"serviceEndpoint":{"continuationCommand":{"token":"SORT_TOP"}}},
                        {"title":"Najnowsze","selected":false,"serviceEndpoint":{"continuationCommand":{"token":"SORT_NEW"}}}]}}}}]}},
               {"reloadContinuationItemsCommand":{"slot":"RELOAD_CONTINUATION_SLOT_BODY","continuationItems":[
                  {"commentThreadRenderer":{
                     "commentViewModel":{"commentViewModel":{"commentKey":"K1","pinnedText":"Przypięty"}},
                     "replies":{"commentRepliesRenderer":{"contents":[{"continuationItemRenderer":{"button":{"buttonRenderer":{"command":{"continuationCommand":{"token":"REPLIES1"}}}}}}]}}}},
                  {"commentThreadRenderer":{"commentViewModel":{"commentViewModel":{"commentKey":"K2"}}}},
                  {"commentThreadRenderer":{"commentViewModel":{"commentViewModel":{"commentKey":"BRAK"}}}},
                  {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"MORE_COMMENTS"}}}}]}}],
             "frameworkUpdates":{"entityBatchUpdate":{"mutations":[
               {"payload":{"commentEntityPayload":{"key":"K1",
                  "properties":{"content":{"content":"Świetny film!"},"publishedTime":"2 dni temu","replyLevel":0},
                  "author":{"displayName":"@autor","isCreator":true},
                  "toolbar":{"likeCountNotliked":"1,2 tys.","replyCount":"12"}}}},
               {"payload":{"commentEntityPayload":{"key":"K2",
                  "properties":{"content":{"content":"Drugi"},"publishedTime":"1 godz. temu"},
                  "author":{"displayName":"@ktos"},
                  "toolbar":{"likeCountNotliked":"","replyCount":""}}}}]}}}
            """);
        var page = Parsers.ParseComments(root);

        Assert.Equal("164 komentarze", page.HeaderCount);
        Assert.Equal(["Najpopularniejsze", "Najnowsze"], page.Sorts.Select(s => s.Title));
        Assert.Equal("SORT_NEW", page.Sorts[1].Token);
        Assert.Equal("MORE_COMMENTS", page.Continuation);
        Assert.Equal(2, page.Comments.Count); // komentarz bez danych (BRAK) jest pomijany

        var c1 = page.Comments[0];
        Assert.Equal("@autor", c1.Author);
        Assert.Equal("Świetny film!", c1.Text);
        Assert.Equal("2 dni temu", c1.Published);
        Assert.Equal("1,2 tys.", c1.Likes);
        Assert.Equal(12, c1.ReplyCount);
        Assert.Equal("REPLIES1", c1.RepliesToken);
        Assert.True(c1.IsCreator);
        Assert.True(c1.IsPinned);
        Assert.False(c1.IsReply);

        var c2 = page.Comments[1];
        Assert.Equal(0, c2.ReplyCount);
        Assert.Null(c2.RepliesToken);
        Assert.False(c2.IsPinned);
    }

    [Fact]
    public void ParseComments_RepliesAreMarked()
    {
        var root = N("""
            {"onResponseReceivedEndpoints":[{"appendContinuationItemsAction":{"continuationItems":[
               {"commentViewModel":{"commentKey":"R1"}}]}}],
             "frameworkUpdates":{"entityBatchUpdate":{"mutations":[
               {"payload":{"commentEntityPayload":{"key":"R1","properties":{"content":{"content":"Odpowiedź"},"replyLevel":1},"author":{"displayName":"@b"},"toolbar":{}}}}]}}}
            """);
        var r = Assert.Single(Parsers.ParseComments(root).Comments);
        Assert.True(r.IsReply);
        Assert.Equal("Odpowiedź", r.Text);
    }

    [Fact]
    public void ParseChannelPage_HeaderTabsAndItems()
    {
        var root = N("""
            {"metadata":{"channelMetadataRenderer":{"title":"Lofi Girl","description":"Opis","externalId":"UCSJ4gkVC6NrvII8umztf0Ow",
                "vanityChannelUrl":"http://www.youtube.com/@LofiGirl",
                "avatar":{"thumbnails":[{"url":"https://yt3/avatar=s900","width":900}]}}},
             "header":{"pageHeaderRenderer":{"content":{"pageHeaderViewModel":{
                "title":{"dynamicTextViewModel":{"text":{"content":"Lofi Girl"}}},
                "metadata":{"contentMetadataViewModel":{"metadataRows":[
                   {"metadataParts":[{"text":{"content":"@LofiGirl"}}]},
                   {"metadataParts":[{"text":{"content":"15,8 mln subskrybentów"}},{"text":{"content":"484 filmy"}}]}]}},
                "banner":{"imageBannerViewModel":{"image":{"sources":[{"url":"https://yt3/banner","width":1060}]}}}}}}},
             "contents":{"twoColumnBrowseResultsRenderer":{"tabs":[
                {"tabRenderer":{"title":"Główna","endpoint":{"browseEndpoint":{"params":"HOME"}}}},
                {"tabRenderer":{"title":"Wideo","selected":true,"endpoint":{"browseEndpoint":{"params":"VIDEOS"}},
                   "content":{"richGridRenderer":{
                      "header":{"feedFilterChipBarRenderer":{"contents":[{"chipCloudChipRenderer":{"text":{"simpleText":"Najnowsze"},"isSelected":true,"navigationEndpoint":{"continuationCommand":{"token":"S1"}}}}]}},
                      "contents":[{"richItemRenderer":{"content":{"videoRenderer":{"videoId":"chanvid0001","title":{"simpleText":"Film kanału"}}}}},
                                  {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"CH_NEXT"}}}}]}}}},
                {"expandableTabRenderer":{"title":"Szukaj"}}]}}}
            """);
        var page = Parsers.ParseChannel(root, "UCSJ4gkVC6NrvII8umztf0Ow");

        Assert.Equal("Lofi Girl", page.Channel.Title);
        Assert.Equal("@LofiGirl", page.Channel.Handle);
        Assert.Equal("15,8 mln subskrybentów", page.Channel.SubscribersText);
        Assert.Equal("484 filmy", page.Channel.VideoCountText);
        Assert.Equal("https://yt3/banner", page.BannerUrl);
        Assert.Equal(["Główna", "Wideo"], page.Tabs.Select(t => t.Title));
        Assert.True(page.Tabs[1].Selected);
        Assert.Equal("VIDEOS", page.Tabs[1].Params);
        Assert.Equal("chanvid0001", Assert.Single(page.Content.Items).Id);
        Assert.Equal("CH_NEXT", page.Content.Continuation);
        Assert.Equal("Najnowsze", Assert.Single(page.Sorts).Title);
    }

    [Fact]
    public void ParsePlaylistPage_NewHeaderWithAvatarStackAuthor()
    {
        // Struktura z prawdziwej odpowiedzi YouTube (listopad 2026): autor w avatarStackViewModel
        var root = N("""
            {"metadata":{"playlistMetadataRenderer":{"title":"Lofi Hip Hop"}},
             "header":{"pageHeaderRenderer":{"content":{"pageHeaderViewModel":{
                "title":{"dynamicTextViewModel":{"text":{"content":"Lofi Hip Hop"}}},
                "metadata":{"contentMetadataViewModel":{"metadataRows":[
                   {"metadataParts":[{"avatarStack":{"avatarStackViewModel":{
                      "avatars":[{"avatarViewModel":{"image":{"sources":[{"url":"https://yt3/a","width":48}]}}}],
                      "text":{"content":"autor: Mimi Lofi Chill","commandRuns":[{"startIndex":0,"length":22,"onTap":{"innertubeCommand":{"browseEndpoint":{"browseId":"UCq9J_CUURRfbP7vm4VN_3xg","canonicalBaseUrl":"/@mimilofichill"}}}}]}}}}]},
                   {"metadataParts":[{"text":{"content":"Playlista"}},{"text":{"content":"76 filmów"}},{"text":{"content":"132 660 wyświetleń"}}]}]}}}}}},
             "contents":{"twoColumnBrowseResultsRenderer":{"tabs":[{"tabRenderer":{"content":{}}}]}}}
            """);
        var page = Parsers.ParsePlaylist(root, "PLXIclLvfETS3AgCnZg4N6QqHu_T27XKIq");

        Assert.Equal("Mimi Lofi Chill", page.Playlist.ChannelName);
        Assert.Equal("UCq9J_CUURRfbP7vm4VN_3xg", page.Playlist.ChannelId);
        Assert.Equal("76 filmów", page.Playlist.VideoCountText);
        Assert.Equal("132 660 wyświetleń", page.ViewsText);
    }

    [Theory]
    [InlineData("autor: Mimi Lofi Chill", "Mimi Lofi Chill")]
    [InlineData("by Lofi Girl", "Lofi Girl")]
    [InlineData("Lofi Girl", "Lofi Girl")]
    [InlineData("autor: Lofi: beats", "Lofi: beats")]
    [InlineData("Bardzo długa nazwa kanału: druga część", "Bardzo długa nazwa kanału: druga część")]
    public void StripAuthorPrefix(string input, string expected)
    {
        Assert.Equal(expected, Parsers.StripAuthorPrefix(input));
    }

    [Fact]
    public void ParseLockup_MixHasNoFakeAuthorOrCount()
    {
        var mix = Assert.IsType<PlaylistItem>(Parsers.ParseLockup(N("""
            {"contentId":"RDEMO2FjWQVcgyvtgcHiWlf5-Q","contentType":"LOCKUP_CONTENT_TYPE_PLAYLIST",
             "contentImage":{"collectionThumbnailViewModel":{"primaryThumbnail":{"thumbnailViewModel":{
                "overlays":[{"thumbnailOverlayBadgeViewModel":{"thumbnailBadges":[{"thumbnailBadgeViewModel":{"text":"Składanka"}}]}}]}}}},
             "metadata":{"lockupMetadataViewModel":{"title":{"content":"Składanka – Lofi Hip Hop"},
                "metadata":{"contentMetadataViewModel":{"metadataRows":[{"metadataParts":[{"text":{"content":"YouTube"}},{"text":{"content":"Playlista"}}]}]}}}}}
            """)));
        Assert.True(mix.IsMix);
        Assert.Equal("", mix.ChannelName);
        Assert.Equal("", mix.VideoCountText);
    }

    [Fact]
    public void ParsePlaylistPage_ClassicHeader()
    {
        var root = N("""
            {"metadata":{"playlistMetadataRenderer":{"title":"Lista","description":"Opis listy"}},
             "header":{"playlistHeaderRenderer":{
                "ownerText":{"runs":[{"text":"Autor","navigationEndpoint":{"browseEndpoint":{"browseId":"UCgggggggggggggggggggggg"}}}]},
                "numVideosText":{"runs":[{"text":"2"},{"text":" filmy"}]},
                "viewCountText":{"simpleText":"663 wyświetlenia"}}},
             "contents":{"twoColumnBrowseResultsRenderer":{"tabs":[{"tabRenderer":{"content":{"sectionListRenderer":{"contents":[
                {"itemSectionRenderer":{"contents":[{"playlistVideoListRenderer":{"contents":[
                   {"playlistVideoRenderer":{"videoId":"plv00000001","title":{"runs":[{"text":"Pierwszy"}]}}},
                   {"playlistVideoRenderer":{"videoId":"plv00000002","title":{"runs":[{"text":"Drugi"}]}}}]}}]}}]}}}}]}}}
            """);
        var page = Parsers.ParsePlaylist(root, "PLlist");

        Assert.Equal("Lista", page.Playlist.Title);
        Assert.Equal("Autor", page.Playlist.ChannelName);
        Assert.Equal("UCgggggggggggggggggggggg", page.Playlist.ChannelId);
        Assert.Equal("2 filmy", page.Playlist.VideoCountText);
        Assert.Equal("663 wyświetlenia", page.ViewsText);
        Assert.Equal("Opis listy", page.Description);
        Assert.Equal(["plv00000001", "plv00000002"], page.Content.Items.Select(i => i.Id));
        Assert.Equal("plv00000001", page.Playlist.FirstVideoId);
    }
}
