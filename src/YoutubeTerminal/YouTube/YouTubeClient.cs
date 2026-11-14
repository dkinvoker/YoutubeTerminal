using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;

namespace YoutubeTerminal.YouTube;

/// <summary>
/// Klient publicznego API strony YouTube (InnerTube, klient WEB) — wyszukiwanie, kanały,
/// playlisty, szczegóły filmów, komentarze — oraz kanały RSS do subskrypcji.
/// </summary>
internal sealed partial class YouTubeClient
{
    private const string Base = "https://www.youtube.com/youtubei/v1/";
    private const string FallbackVersion = "2.20261112.10.00";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

    private readonly HttpClient _http;
    private string _clientVersion = FallbackVersion;
    private string? _visitorData;
    private Task? _initTask;

    public string Language { get; set; } = "pl";
    public string Region { get; set; } = "PL";

    public YouTubeClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            UseCookies = false,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pl-PL,pl;q=0.9,en;q=0.8");
    }

    public HttpClient Http => _http;

    /// <summary>Pobiera aktualną wersję klienta WEB ze strony głównej (w tle, jednorazowo).</summary>
    public Task EnsureInitAsync() => _initTask ??= InitAsync();

    private async Task InitAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/?hl=" + Language);
            // SOCS=CAI: odrzucenie zgody na ciasteczka (najbardziej prywatna opcja), bez strony consent.
            req.Headers.Add("Cookie", "SOCS=CAI");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var html = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var m = VersionRegex().Match(html);
            if (m.Success) _clientVersion = m.Groups[1].Value;
            var vd = VisitorRegex().Match(html);
            if (vd.Success) _visitorData = vd.Groups[1].Value;
        }
        catch
        {
            // Zostajemy przy wersji zapasowej.
        }
    }

    [GeneratedRegex("\"INNERTUBE_CLIENT_VERSION\":\"([^\"]+)\"")]
    private static partial Regex VersionRegex();

    [GeneratedRegex("\"VISITOR_DATA\":\"([^\"]+)\"")]
    private static partial Regex VisitorRegex();

    private JsonObject Context()
    {
        var client = new JsonObject
        {
            ["clientName"] = "WEB",
            ["clientVersion"] = _clientVersion,
            ["hl"] = Language,
            ["gl"] = Region,
            ["utcOffsetMinutes"] = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes,
        };
        if (_visitorData != null) client["visitorData"] = _visitorData;
        return new JsonObject { ["client"] = client };
    }

    private async Task<JsonNode> PostAsync(string endpoint, JsonObject body, CancellationToken ct)
    {
        await EnsureInitAsync().ConfigureAwait(false);
        body["context"] = Context();
        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, Base + endpoint + "?prettyPrint=false")
                {
                    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                req.Headers.Add("X-YouTube-Client-Name", "1");
                req.Headers.Add("X-YouTube-Client-Version", _clientVersion);
                req.Headers.Add("Origin", "https://www.youtube.com");
                if (_visitorData != null) req.Headers.Add("X-Goog-Visitor-Id", _visitorData);
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    if ((int)resp.StatusCode >= 500) throw new HttpRequestException($"Błąd serwera YouTube ({(int)resp.StatusCode})");
                    throw new InvalidOperationException($"YouTube odrzucił żądanie ({(int)resp.StatusCode}).");
                }
                if (Environment.GetEnvironmentVariable("YT_DUMP") is { Length: > 0 } dumpDir)
                    File.WriteAllText(Path.Combine(dumpDir, $"{endpoint.Replace('/', '_')}_{DateTime.Now:HHmmssfff}.json"), text);
                return JsonNode.Parse(text) ?? throw new InvalidDataException("Pusta odpowiedź");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                last = ex;
                await Task.Delay(400 * (attempt + 1), ct).ConfigureAwait(false);
            }
        }
        throw new IOException("Brak połączenia z YouTube: " + last?.Message, last);
    }

    // ---------------- Wyszukiwanie ----------------

    private static string SearchParams(SearchFilter f, SearchSort s)
    {
        // Ręcznie złożony protobuf: pole 1 = sortowanie, pole 2 = filtry (typ: pole 2 w zagnieżdżeniu).
        var bytes = new List<byte>();
        int sort = s switch { SearchSort.UploadDate => 2, SearchSort.ViewCount => 3, SearchSort.Rating => 1, _ => 0 };
        if (sort != 0) { bytes.Add(0x08); bytes.Add((byte)sort); }
        int type = f switch { SearchFilter.Videos => 1, SearchFilter.Channels => 2, SearchFilter.Playlists => 3, _ => 0 };
        if (type != 0) { bytes.Add(0x12); bytes.Add(0x02); bytes.Add(0x10); bytes.Add((byte)type); }
        return bytes.Count == 0 ? "" : Convert.ToBase64String(bytes.ToArray());
    }

    public async Task<FeedPage> SearchAsync(string query, SearchFilter filter, SearchSort sort, CancellationToken ct)
    {
        var body = new JsonObject { ["query"] = query };
        var p = SearchParams(filter, sort);
        if (p.Length > 0) body["params"] = p;
        var root = await PostAsync("search", body, ct).ConfigureAwait(false);
        var page = new FeedPage();
        Parsers.CollectItems(root.At("contents"), page);
        return ShortsLast(page);
    }

    public async Task<FeedPage> SearchContinuationAsync(string token, CancellationToken ct)
    {
        var root = await PostAsync("search", new JsonObject { ["continuation"] = token }, ct).ConfigureAwait(false);
        return ShortsLast(Parsers.ParseContinuation(root));
    }

    /// <summary>Półki Shorts przenosimy na koniec strony wyników (pionowe filmiki są mniej przydatne w terminalu).</summary>
    private static FeedPage ShortsLast(FeedPage page)
    {
        var normal = page.Items.Where(i => i is not VideoItem { IsShort: true }).ToList();
        var shorts = page.Items.Where(i => i is VideoItem { IsShort: true }).ToList();
        page.Items.Clear();
        page.Items.AddRange(normal);
        page.Items.AddRange(shorts);
        return page;
    }

    public async Task<List<string>> SuggestAsync(string query, CancellationToken ct)
    {
        var url = $"https://suggestqueries.google.com/complete/search?client=firefox&ds=yt&ie=utf-8&oe=utf-8&hl={Language}&gl={Region}&q={Uri.EscapeDataString(query)}";
        var text = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        var list = new List<string>();
        var node = JsonNode.Parse(text);
        foreach (var s in node.Arr(1)) list.Add(s.Str());
        return list;
    }

    // ---------------- Przeglądanie (kanały, playlisty) ----------------

    public const string VideosTabParams = "EgZ2aWRlb3PyBgQKAjoA";

    public async Task<ChannelPage> GetChannelAsync(string channelId, string? tabParams, CancellationToken ct)
    {
        var body = new JsonObject { ["browseId"] = channelId };
        if (!string.IsNullOrEmpty(tabParams)) body["params"] = tabParams;
        var root = await PostAsync("browse", body, ct).ConfigureAwait(false);
        if (root.At("alerts")?.ToJsonString() is { } alerts && root.At("contents") == null)
            throw new InvalidOperationException("Kanał jest niedostępny.");
        return Parsers.ParseChannel(root, channelId);
    }

    public async Task<FeedPage> BrowseContinuationAsync(string token, CancellationToken ct)
    {
        var root = await PostAsync("browse", new JsonObject { ["continuation"] = token }, ct).ConfigureAwait(false);
        return Parsers.ParseContinuation(root);
    }

    public async Task<PlaylistPage> GetPlaylistAsync(string playlistId, string? firstVideoId, CancellationToken ct)
    {
        if (playlistId.StartsWith("RD", StringComparison.Ordinal))
            return await GetMixAsync(playlistId, firstVideoId, ct).ConfigureAwait(false);
        var root = await PostAsync("browse", new JsonObject { ["browseId"] = "VL" + playlistId }, ct).ConfigureAwait(false);
        if (root.At("contents") == null)
        {
            var alert = root.Find("alertRenderer").Text("text");
            throw new InvalidOperationException(alert.Length > 0 ? alert : "Playlista jest niedostępna.");
        }
        return Parsers.ParsePlaylist(root, playlistId);
    }

    private async Task<PlaylistPage> GetMixAsync(string playlistId, string? firstVideoId, CancellationToken ct)
    {
        var body = new JsonObject { ["playlistId"] = playlistId };
        string? vid = !string.IsNullOrEmpty(firstVideoId) ? firstVideoId : playlistId.Length == 13 ? playlistId[2..] : null;
        if (vid != null) body["videoId"] = vid;
        var root = await PostAsync("next", body, ct).ConfigureAwait(false);
        var pl = root.At("contents", "twoColumnWatchNextResults", "playlist", "playlist");
        var page = new PlaylistPage();
        page.Playlist.Id = playlistId;
        page.Playlist.Title = pl.Text("title");
        page.Playlist.ChannelName = pl.Text("ownerName");
        page.Playlist.IsMix = true;
        Parsers.CollectItems(pl.At("contents"), page.Content);
        page.Content.Continuation = null;
        page.Playlist.VideoCountText = page.Content.Items.Count + " filmów";
        return page;
    }

    // ---------------- Film ----------------

    public async Task<VideoDetails> GetVideoAsync(string videoId, CancellationToken ct)
    {
        var root = await PostAsync("next", new JsonObject { ["videoId"] = videoId }, ct).ConfigureAwait(false);
        var d = Parsers.ParseNext(root, videoId);
        if (d.Video.Title.Length == 0)
            throw new InvalidOperationException("Film jest niedostępny.");
        return d;
    }

    public async Task<FeedPage> NextContinuationAsync(string token, CancellationToken ct)
    {
        var root = await PostAsync("next", new JsonObject { ["continuation"] = token }, ct).ConfigureAwait(false);
        return Parsers.ParseContinuation(root);
    }

    public async Task<CommentsPage> GetCommentsAsync(string token, CancellationToken ct)
    {
        var root = await PostAsync("next", new JsonObject { ["continuation"] = token }, ct).ConfigureAwait(false);
        return Parsers.ParseComments(root);
    }

    // ---------------- Rozwiązywanie adresów ----------------

    /// <summary>Wynik rozwiązania adresu/uchwytu: typ i identyfikator.</summary>
    public sealed record Resolved(string Kind, string Id, double StartSeconds = 0);

    /// <summary>Rozpoznaje linki YouTube (filmy, shorts, kanały, @uchwyty, playlisty). Null jeśli to zwykłe zapytanie.</summary>
    public async Task<Resolved?> ResolveAsync(string input, CancellationToken ct)
    {
        var s = input.Trim();
        var local = UrlParser.Parse(s);
        if (local != null && local.Kind != "url") return local;
        if (local == null) return null;

        // Uchwyt lub niestandardowy URL kanału — pytamy serwer.
        var root = await PostAsync("navigation/resolve_url", new JsonObject { ["url"] = local.Id }, ct).ConfigureAwait(false);
        var browse = root.Find("browseEndpoint");
        var bid = browse.Str("browseId");
        if (bid.StartsWith("UC", StringComparison.Ordinal)) return new Resolved("channel", bid);
        if (bid.StartsWith("VL", StringComparison.Ordinal)) return new Resolved("playlist", bid[2..]);
        var watch = root.Find("watchEndpoint");
        if (watch.Str("videoId") is { Length: > 0 } vid) return new Resolved("video", vid);
        throw new InvalidOperationException("Nie rozpoznano adresu.");
    }

    // ---------------- RSS (subskrypcje) ----------------

    public async Task<List<VideoItem>> GetChannelFeedAsync(string channelId, string channelName, CancellationToken ct)
    {
        var list = new List<VideoItem>();
        try
        {
            var xml = await _http.GetStringAsync("https://www.youtube.com/feeds/videos.xml?channel_id=" + channelId, ct).ConfigureAwait(false);
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            VideoItem? cur = null;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                switch (reader.LocalName)
                {
                    case "entry":
                        cur = new VideoItem { ChannelId = channelId, ChannelName = channelName };
                        list.Add(cur);
                        break;
                    case "videoId" when cur != null:
                        cur.Id = reader.ReadElementContentAsString();
                        break;
                    case "title" when cur != null && reader.Depth == 2:
                        cur.Title = reader.ReadElementContentAsString();
                        break;
                    case "name" when cur != null:
                        var n = reader.ReadElementContentAsString();
                        if (cur.ChannelName.Length == 0) cur.ChannelName = n;
                        break;
                    case "published" when cur != null:
                        if (DateTime.TryParse(reader.ReadElementContentAsString(), System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
                            cur.Published = dt;
                        break;
                    case "link" when cur != null:
                        if (reader.GetAttribute("href") is { } href && href.Contains("/shorts/", StringComparison.Ordinal)) cur.IsShort = true;
                        break;
                    case "statistics" when cur != null:
                        if (long.TryParse(reader.GetAttribute("views"), out long views)) cur.ViewsText = FormatViews(views);
                        break;
                    case "description" when cur != null:
                        cur.Description = reader.ReadElementContentAsString();
                        break;
                }
            }
        }
        catch (HttpRequestException)
        {
            // RSS bywa niedostępny — awaryjnie zakładka "Filmy" kanału.
            var page = await GetChannelAsync(channelId, VideosTabParams, ct).ConfigureAwait(false);
            foreach (var v in page.Content.Items.OfType<VideoItem>().Take(15))
            {
                if (v.ChannelName.Length == 0) v.ChannelName = channelName;
                if (v.ChannelId.Length == 0) v.ChannelId = channelId;
                list.Add(v);
            }
        }
        foreach (var v in list)
            if (v.Published is { } p) v.PublishedText = RelativeTime(p);
        return list.Where(v => v.Id.Length > 0).ToList();
    }

    public static string FormatViews(long v) => (v switch
    {
        >= 1_000_000_000 => $"{v / 1e9:0.#} mld wyświetleń",
        >= 1_000_000 => $"{v / 1e6:0.#} mln wyświetleń",
        >= 1_000 => $"{v / 1e3:0.#} tys. wyświetleń",
        1 => "1 wyświetlenie",
        _ => Plural((int)Math.Min(v, int.MaxValue), "wyświetlenie", "wyświetlenia", "wyświetleń"),
    }).Replace('.', ',').Replace("tys,", "tys.");

    public static string RelativeTime(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalMinutes < 1) return "przed chwilą";
        if (d.TotalHours < 1) return Plural((int)d.TotalMinutes, "minutę", "minuty", "minut") + " temu";
        if (d.TotalDays < 1) return Plural((int)d.TotalHours, "godzinę", "godziny", "godzin") + " temu";
        if (d.TotalDays < 7) return Plural((int)d.TotalDays, "dzień", "dni", "dni") + " temu";
        if (d.TotalDays < 31) return Plural((int)(d.TotalDays / 7), "tydzień", "tygodnie", "tygodni") + " temu";
        if (d.TotalDays < 365) return Plural((int)(d.TotalDays / 30), "miesiąc", "miesiące", "miesięcy") + " temu";
        return Plural((int)(d.TotalDays / 365), "rok", "lata", "lat") + " temu";
    }

    public static string Plural(int n, string one, string few, string many)
    {
        if (n == 1) return "1 " + one;
        int m10 = n % 10, m100 = n % 100;
        bool isFew = m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14);
        return $"{n} {(isFew ? few : many)}";
    }
}

/// <summary>Rozpoznawanie adresów YouTube bez połączenia z siecią.</summary>
internal static partial class UrlParser
{
    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdRegex();

    public static YouTubeClient.Resolved? Parse(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return null;
        if (s.StartsWith('@') && !s.Contains(' '))
            return new YouTubeClient.Resolved("url", "https://www.youtube.com/" + s);
        if (s.StartsWith("UC", StringComparison.Ordinal) && s.Length == 24 && !s.Contains(' '))
            return new YouTubeClient.Resolved("channel", s);

        if (!s.Contains("youtu", StringComparison.OrdinalIgnoreCase)) return null;
        if (!s.StartsWith("http", StringComparison.OrdinalIgnoreCase)) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.TrimEnd('/');
        var query = ParseQuery(uri.Query);
        double start = 0;
        if (query.TryGetValue("t", out var t)) start = ParseStart(t);

        if (host.EndsWith("youtu.be", StringComparison.Ordinal))
        {
            var id = path.Trim('/');
            return VideoIdRegex().IsMatch(id) ? new YouTubeClient.Resolved("video", id, start) : null;
        }
        if (!host.Contains("youtube.com", StringComparison.Ordinal)) return null;

        if (query.TryGetValue("v", out var v) && VideoIdRegex().IsMatch(v)) return new YouTubeClient.Resolved("video", v, start);
        if (path.StartsWith("/shorts/", StringComparison.Ordinal) || path.StartsWith("/live/", StringComparison.Ordinal) || path.StartsWith("/embed/", StringComparison.Ordinal))
        {
            var id = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "";
            if (VideoIdRegex().IsMatch(id)) return new YouTubeClient.Resolved("video", id, start);
        }
        if (query.TryGetValue("list", out var list) && list.Length > 0) return new YouTubeClient.Resolved("playlist", list);
        if (path.StartsWith("/channel/", StringComparison.Ordinal))
        {
            var id = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "";
            if (id.StartsWith("UC", StringComparison.Ordinal)) return new YouTubeClient.Resolved("channel", id);
        }
        if (path.StartsWith("/@", StringComparison.Ordinal) || path.StartsWith("/c/", StringComparison.Ordinal) || path.StartsWith("/user/", StringComparison.Ordinal))
        {
            // Bez zakładki (np. /@x/videos) — rozwiązujemy sam kanał.
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string basePath = parts[0].StartsWith('@') ? "/" + parts[0] : "/" + string.Join('/', parts.Take(2));
            return new YouTubeClient.Resolved("url", "https://www.youtube.com" + basePath);
        }
        return null;
    }

    private static Dictionary<string, string> ParseQuery(string q)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in q.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            d[Uri.UnescapeDataString(part[..eq])] = Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return d;
    }

    private static double ParseStart(string t)
    {
        t = t.TrimEnd('s');
        if (double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec)) return sec;
        var m = Regex.Match(t, @"(?:(\d+)h)?(?:(\d+)m)?(?:(\d+))?");
        if (!m.Success) return 0;
        int h = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
        int mi = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        int s = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        return h * 3600 + mi * 60 + s;
    }
}
