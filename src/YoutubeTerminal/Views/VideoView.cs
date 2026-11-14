using YoutubeTerminal.Playback;
using YoutubeTerminal.Tui;
using YoutubeTerminal.YouTube;

namespace YoutubeTerminal.UI;

/// <summary>Szczegóły filmu: miniatura, statystyki, opis, rozdziały, komentarze, podobne filmy.</summary>
internal sealed class VideoView : View
{
    private VideoItem _video;
    private VideoDetails? _details;
    private string? _error;
    private int _tab; // 0 opis, 1 rozdziały, 2 komentarze, 3 podobne
    private int _descScroll;
    private int _chapterSel;
    private readonly ItemList _related = new();

    // Komentarze
    private readonly List<Comment> _comments = new();
    private string? _commentsCont;
    private bool _commentsLoading;
    private bool _commentsLoaded;
    private string _commentsHeader = "";
    private List<SortChip> _commentSorts = new();
    private int _commentSort;
    private int _commentSel;
    private int _commentScroll;
    private readonly HashSet<int> _expandedLoading = new();
    private CancellationTokenSource? _commentsCts;

    private static readonly string[] TabNames = ["Opis", "Rozdziały", "Komentarze", "Podobne"];

    public VideoView(VideoItem v)
    {
        _video = v;
        _related.LoadMore = (t, ct) => App.YT.NextContinuationAsync(t, ct);
        _related.EmptyText = "Brak podobnych filmów.";
        _related.AllowThumbnails = true;
        Load();
    }

    public override string Title => TextUtil.Truncate(_video.Title.Length > 0 ? _video.Title : "Film", 40);
    public override ItemList? FocusedList => _tab == 3 ? _related : null;

    private void Load()
    {
        App.RunAsync(ct => App.YT.GetVideoAsync(_video.Id, ct), d =>
        {
            _details = d;
            // Uzupełniamy dane, których brakowało na liście.
            var v = d.Video;
            if (v.DurationSeconds == 0) { v.DurationSeconds = _video.DurationSeconds; v.DurationText = _video.DurationText; }
            if (v.Description.Length == 0) v.Description = d.Description;
            v.IsShort = _video.IsShort;
            _video = v;
            _related.SetItems(d.Related.Items, d.Related.Continuation);
            if (d.Chapters.Count == 0 && _tab == 1) _tab = 0;
            // Komentarze pobieramy od razu w tle — zakładka jest gotowa, zanim się na nią wejdzie.
            if (d.CommentsToken != null && !_commentsLoaded && !_commentsLoading) LoadComments(true);
            PrefetchStream();
        }, ex => _error = ex.Message);
    }

    /// <summary>
    /// Wstępne pobranie adresów strumieni (yt-dlp działa kilka sekund). Przy okazji uzupełnia
    /// czas trwania, którego nie ma w odpowiedzi YouTube dla strony filmu (np. film otwarty z linku).
    /// </summary>
    private void PrefetchStream()
    {
        if (_video.IsUpcoming || YtDlp.FindExecutable() == null) return;
        var video = _video;
        Prefetch.Start(video);
        if (video.DurationSeconds > 0 || video.IsLive) return;
        int height = App.StreamHeight();
        App.RunAsync(ct => YtDlp.ResolveAsync(video.Id, height, false, ct), info =>
        {
            if (info.Duration > 0 && video.DurationSeconds == 0)
            {
                video.DurationSeconds = (int)Math.Round(info.Duration);
                video.DurationText = TextUtil.FormatSeconds(video.DurationSeconds);
            }
        }, _ => { }, quiet: true);
    }

    private void LoadComments(bool reset)
    {
        if (_details?.CommentsToken == null && reset) return;
        string? token = reset ? (_commentSorts.Count > _commentSort && _commentSorts.Count > 0 ? _commentSorts[_commentSort].Token : _details!.CommentsToken) : _commentsCont;
        if (token == null || _commentsLoading) return;
        if (reset)
        {
            _commentsCts?.Cancel();
            _comments.Clear();
            _commentSel = 0;
            _commentScroll = 0;
        }
        _commentsCts ??= new CancellationTokenSource();
        var ct = _commentsCts.Token;
        _commentsLoading = true;
        App.RunAsync(c => App.YT.GetCommentsAsync(token, c), page =>
        {
            _commentsLoading = false;
            _commentsLoaded = true;
            _comments.AddRange(page.Comments);
            _commentsCont = page.Continuation;
            if (page.HeaderCount.Length > 0) _commentsHeader = page.HeaderCount;
            if (page.Sorts.Count > 0 && _commentSorts.Count == 0) _commentSorts = page.Sorts;
        }, ex =>
        {
            _commentsLoading = false;
            _commentsLoaded = true;
            App.ShowError("Komentarze: " + ex.Message);
        }, quiet: true, ct: ct);
    }

    private void ToggleReplies(int index)
    {
        var c = _comments[index];
        if (c.RepliesToken == null || _expandedLoading.Contains(index)) return;
        // Odpowiedzi już rozwinięte? Zwijamy.
        if (index + 1 < _comments.Count && _comments[index + 1].IsReply)
        {
            int n = 0;
            while (index + 1 + n < _comments.Count && _comments[index + 1 + n].IsReply) n++;
            _comments.RemoveRange(index + 1, n);
            return;
        }
        _expandedLoading.Add(index);
        var token = c.RepliesToken;
        App.RunAsync(ct => App.YT.GetCommentsAsync(token, ct), page =>
        {
            _expandedLoading.Remove(index);
            int pos = _comments.IndexOf(c);
            if (pos < 0) return;
            foreach (var r in page.Comments) r.IsReply = true;
            _comments.InsertRange(pos + 1, page.Comments);
        }, ex => { _expandedLoading.Remove(index); App.ShowError(ex.Message); }, quiet: true);
    }

    public override IEnumerable<(string Key, string Label)> Hints
    {
        get
        {
            yield return ("Enter/p", "Odtwórz");
            yield return ("a", "Dźwięk");
            if (App.Library.GetHistory(_video.Id) is { PositionSeconds: > 5 }) yield return ("r", "Od początku");
            yield return ("Tab", "Zakładki");
            yield return ("c", "Kanał");
            yield return ("s", App.Library.IsSubscribed(_video.ChannelId) ? "Anuluj sub." : "Subskrybuj");
            yield return ("w", "Do obejrzenia");
            yield return ("y", "Kopiuj link");
            yield return ("o", "Przeglądarka");
        }
    }

    public override bool Tick() => _tab == 3 && _related.SelectionJustSettled;

    private void Play(bool audioOnly = false, TimeSpan? start = null)
    {
        var queue = _related.Items.OfType<VideoItem>().Where(v => !v.IsLive && !v.IsUpcoming && !v.IsShort).ToList();
        queue.Insert(0, _video);
        App.Play(_video, queue, 0, start, audioOnly);
    }

    public override bool HandleKey(KeyEvent k)
    {
        if (k.Key == Key.Tab) { NextTab(1); return true; }
        if (k.Key == Key.BackTab) { NextTab(-1); return true; }

        switch (_tab)
        {
            case 0:
                if (k.Key == Key.Down) { _descScroll++; return true; }
                if (k.Key == Key.Up) { _descScroll = Math.Max(0, _descScroll - 1); return true; }
                if (k.Key == Key.PageDown) { _descScroll += 10; return true; }
                if (k.Key == Key.PageUp) { _descScroll = Math.Max(0, _descScroll - 10); return true; }
                if (k.Key == Key.Home) { _descScroll = 0; return true; }
                break;
            case 1 when _details?.Chapters.Count > 0:
                var chs = _details.Chapters;
                if (k.Key == Key.Down) { _chapterSel = Math.Min(chs.Count - 1, _chapterSel + 1); return true; }
                if (k.Key == Key.Up) { _chapterSel = Math.Max(0, _chapterSel - 1); return true; }
                if (k.Key == Key.Enter) { Play(start: TimeSpan.FromSeconds(chs[_chapterSel].StartSeconds)); return true; }
                break;
            case 2:
                if (k.Key == Key.Down) { _commentSel = Math.Min(_comments.Count - 1, _commentSel + 1); MaybeMoreComments(); return true; }
                if (k.Key == Key.Up) { _commentSel = Math.Max(0, _commentSel - 1); return true; }
                if (k.Key == Key.PageDown) { _commentSel = Math.Min(_comments.Count - 1, _commentSel + 5); MaybeMoreComments(); return true; }
                if (k.Key == Key.PageUp) { _commentSel = Math.Max(0, _commentSel - 5); return true; }
                if (k.Key == Key.Enter && _commentSel < _comments.Count) { ToggleReplies(_commentSel); return true; }
                if (k.Is('o') && _commentSorts.Count > 1)
                {
                    _commentSort = (_commentSort + 1) % _commentSorts.Count;
                    _commentsLoading = false;
                    LoadComments(true);
                    App.ShowMessage("Sortowanie komentarzy: " + _commentSorts[_commentSort].Title);
                    return true;
                }
                break;
            case 3:
                if (_related.HandleKey(k)) return true;
                if (k.Key == Key.Enter || k.Is('p') || k.Is(' ') || k.Is('a') || k.Is('w') || k.Is('y'))
                    return App.HandleItemKey(k, _related.Current, _related.Items);
                break;
        }

        if (k.Key == Key.Enter || k.Is('p') || k.Is(' ')) { Play(); return true; }
        if (k.Is('a')) { Play(audioOnly: true); return true; }
        if (k.Is('r')) { Play(start: TimeSpan.Zero); return true; }
        if (k.Is('c')) { App.OpenChannel(_video.ChannelId, _video.ChannelName); return true; }
        if (k.Is('s')) { App.ToggleSubscribe(_video.ChannelId, _video.ChannelName); return true; }
        if (k.Is('w')) { App.ToggleWatchLater(_video); return true; }
        if (k.Is('y')) { App.CopyLink(_video); return true; }
        if (k.Is('o')) { App.OpenInBrowser(_video); return true; }
        if (k.Key == Key.Right) { NextTab(1); return true; }
        if (k.Key == Key.Left) { NextTab(-1); return true; }
        return false;
    }

    private void MaybeMoreComments()
    {
        if (_commentSel >= _comments.Count - 5 && _commentsCont != null && !_commentsLoading) LoadComments(false);
    }

    private void NextTab(int dir)
    {
        int t = _tab;
        for (int i = 0; i < 4; i++)
        {
            t = (t + dir + 4) % 4;
            if (t == 1 && (_details?.Chapters.Count ?? 0) == 0) continue;
            break;
        }
        SetTab(t);
    }

    private void SetTab(int t)
    {
        _tab = t;
        if (_tab == 2 && !_commentsLoaded && !_commentsLoading) LoadComments(true);
    }

    public override bool HandleMouse(MouseEvent m)
    {
        if (m.Kind == MouseKind.Press && m.Y == _tabsY)
        {
            foreach (var (x0, x1, idx) in _tabHits)
                if (m.X >= x0 && m.X < x1) { SetTab(idx); return true; }
        }
        if (_tab == 3)
        {
            var r = _related.HandleMouse(m);
            if (r == ListMouseResult.Activate && _related.Current is { } it) App.OpenItem(it);
            return r != ListMouseResult.None;
        }
        if (m.Kind == MouseKind.WheelDown)
        {
            if (_tab == 0) _descScroll += 3;
            else if (_tab == 2) { _commentSel = Math.Min(_comments.Count - 1, _commentSel + 2); MaybeMoreComments(); }
            else if (_tab == 1 && _details != null) _chapterSel = Math.Min(_details.Chapters.Count - 1, _chapterSel + 1);
            return true;
        }
        if (m.Kind == MouseKind.WheelUp)
        {
            if (_tab == 0) _descScroll = Math.Max(0, _descScroll - 3);
            else if (_tab == 2) _commentSel = Math.Max(0, _commentSel - 2);
            else if (_tab == 1) _chapterSel = Math.Max(0, _chapterSel - 1);
            return true;
        }
        return false;
    }

    private int _tabsY;
    private readonly List<(int, int, int)> _tabHits = new();

    public override void Render(ScreenBuffer s, Rect area)
    {
        var app = App;
        // Górna część: miniatura + informacje
        int imgW = Math.Clamp(area.W * 2 / 5, 20, 80);
        int imgRows = app.ImageMode == Graphics.RenderMode.Sixel
            ? (int)Math.Round(imgW * app.CellWidth * 9.0 / 16.0 / app.CellHeight)
            : (int)Math.Round(imgW * 9.0 / 16.0 * app.CellWidth / app.CellHeight);
        imgRows = Math.Clamp(imgRows, 4, Math.Max(4, area.H / 2 - 1));
        var top = new Rect(area.X, area.Y, area.W, imgRows + 2);
        s.Fill(top, new Style(Theme.Text, Theme.Panel));
        var imgRect = new Rect(area.X + 2, area.Y + 1, imgW, imgRows);
        string url = _video.IsShort ? _video.ThumbnailUrl : _video.LargeThumbnailUrl;
        var img = app.Images.Get(url, imgRect, _video.ThumbnailUrl);
        if (img != null) s.AddImage(img);
        else s.Fill(imgRect, new Style(Theme.Muted, Theme.Raised));

        int ix = imgRect.Right + 3;
        int iw = area.Right - ix - 2;
        int y = area.Y + 1;
        var bold = new Style(Theme.Text, Theme.Panel, Attr.Bold);
        var dim = new Style(Theme.Dim, Theme.Panel);
        var txt = new Style(Theme.Text, Theme.Panel);
        if (iw > 10)
        {
            if (_video.IsLive) y += 0 * s.Text(ix, y, " NA ŻYWO ", Theme.LiveS) + 1;
            y += s.Paragraph(new Rect(ix, y, iw, 3), TextUtil.OneLine(_video.Title.Length > 0 ? _video.Title : "Wczytywanie…"), bold);
            y++;
            var d = _details;
            if (_video.ChannelName.Length > 0)
            {
                int cx = ix + s.Text(ix, y, _video.ChannelName, new Style(Theme.Text, Theme.Panel, Attr.Bold));
                if (d?.ChannelSubscribers.Length > 0) s.TextTrunc(cx + 2, y, d.ChannelSubscribers, dim, ix + iw - cx - 2);
                if (app.Library.IsSubscribed(_video.ChannelId)) s.TextRight(ix + iw, y, "✓ Subskrybujesz", new Style(Theme.Good, Theme.Panel));
                y++;
            }
            y++;
            if (d != null)
            {
                string views = d.ViewsFull.Length > 0 ? d.ViewsFull : _video.ViewsText;
                s.TextTrunc(ix, y++, ItemList.Join(views, d.DateText.Length > 0 ? d.DateText : _video.PublishedText), txt, iw);
                var stats = new List<string>();
                if (d.LikesText.Length > 0) stats.Add("👍 " + d.LikesText);
                if (_video.DurationText.Length > 0) stats.Add("⏱ " + _video.DurationText);
                if (d.CommentsCount.Length > 0) stats.Add("💬 " + d.CommentsCount);
                if (d.Chapters.Count > 0) stats.Add($"☰ {d.Chapters.Count} rozdz.");
                if (stats.Count > 0) s.TextTrunc(ix, y++, string.Join("   ", stats), dim, iw);
            }
            else if (_error != null) s.Paragraph(new Rect(ix, y, iw, 3), "Błąd: " + _error, new Style(Theme.Accent, Theme.Panel));
            else s.Text(ix, y, app.Spinner + " Wczytywanie szczegółów…", dim);

            y++;
            var flags = new List<string>();
            if (app.Library.GetHistory(_video.Id) is { } h && h.PositionSeconds > 5)
                flags.Add($"▶ Wznowienie od {TextUtil.FormatSeconds(h.PositionSeconds)}");
            if (app.Library.IsInWatchLater(_video.Id)) flags.Add("⏲ Na liście „Do obejrzenia”");
            if (flags.Count > 0 && y < top.Bottom - 1) s.TextTrunc(ix, y++, string.Join("   ", flags), new Style(Theme.Warn, Theme.Panel), iw);
            if (top.Bottom - 2 > y)
                s.Text(ix, top.Bottom - 2, " ▶ Odtwórz (Enter) ", new Style(0x0F0F0F, Theme.Text, Attr.Bold));
        }

        // Zakładki
        int ty = top.Bottom;
        _tabsY = ty;
        _tabHits.Clear();
        int tx = area.X + 2;
        for (int i = 0; i < 4; i++)
        {
            if (i == 1 && (_details?.Chapters.Count ?? 0) == 0) continue;
            string label = TabNames[i];
            if (i == 2 && (_commentsHeader.Length > 0 || _details?.CommentsCount.Length > 0)) label += " (" + (_details?.CommentsCount.Length > 0 ? _details.CommentsCount : _commentsHeader) + ")";
            if (i == 1) label += $" ({_details!.Chapters.Count})";
            bool act = i == _tab;
            int start = tx;
            tx += s.Text(tx, ty, " " + label + " ", act ? new Style(Theme.Text, Theme.Bg, Attr.Bold | Attr.Underline) : Theme.DimS);
            _tabHits.Add((start, tx, i));
            tx += 1;
        }
        s.HLine(area.X, ty + 1, area.W, new Style(0x272727, Theme.Bg));
        var body = new Rect(area.X + 2, ty + 2, area.W - 4, area.Bottom - ty - 2);
        if (body.H <= 0) return;

        switch (_tab)
        {
            case 0: RenderDescription(s, body); break;
            case 1: RenderChapters(s, body); break;
            case 2: RenderComments(s, body); break;
            case 3:
                bool preview = area.W >= 120;
                int lw = preview ? body.W * 60 / 100 : body.W;
                _related.Render(s, new Rect(body.X - 2, body.Y, lw + 2, body.H), true);
                if (preview) PreviewPane.Render(s, new Rect(body.X + lw + 1, body.Y, body.Right - body.X - lw - 1, body.H), _related.Current, _related.SelectionStable);
                break;
        }
    }

    private void RenderDescription(ScreenBuffer s, Rect r)
    {
        var desc = _details?.Description ?? _video.Description;
        if (string.IsNullOrWhiteSpace(desc))
        {
            s.Text(r.X, r.Y, _details == null ? "" : "Brak opisu.", Theme.DimS);
            return;
        }
        var lines = TextUtil.Wrap(desc, Math.Min(r.W, 120));
        _descScroll = Math.Clamp(_descScroll, 0, Math.Max(0, lines.Count - r.H));
        for (int i = 0; i < r.H && i + _descScroll < lines.Count; i++)
        {
            var line = lines[i + _descScroll];
            var st = line.StartsWith("http", StringComparison.Ordinal) ? Theme.LinkS : Theme.Normal;
            s.Text(r.X, r.Y + i, line, st, r.W);
        }
        if (lines.Count > r.H)
            s.TextRight(r.Right, r.Bottom - 1, $" {_descScroll + 1}–{Math.Min(lines.Count, _descScroll + r.H)}/{lines.Count} ", Theme.MutedS);
    }

    private void RenderChapters(ScreenBuffer s, Rect r)
    {
        var chs = _details?.Chapters;
        if (chs == null || chs.Count == 0) return;
        int scroll = Math.Max(0, _chapterSel - r.H + 1);
        for (int i = 0; i < r.H && i + scroll < chs.Count; i++)
        {
            var c = chs[i + scroll];
            bool sel = i + scroll == _chapterSel;
            var st = new Style(Theme.Text, sel ? Theme.SelectionFocus : Theme.Bg, sel ? Attr.Bold : Attr.None);
            s.Fill(new Rect(r.X, r.Y + i, r.W, 1), st);
            s.Text(r.X + 1, r.Y + i, TextUtil.FormatSeconds(c.StartSeconds).PadLeft(8), new Style(Theme.Link, st.Bg));
            s.TextTrunc(r.X + 11, r.Y + i, c.Title, st, r.W - 12);
        }
    }

    private void RenderComments(ScreenBuffer s, Rect r)
    {
        if (_details != null && _details.CommentsToken == null)
        {
            s.Text(r.X, r.Y, "Komentarze są wyłączone lub niedostępne.", Theme.DimS);
            return;
        }
        if (_comments.Count == 0)
        {
            s.Text(r.X, r.Y, _commentsLoading || !_commentsLoaded ? App.Spinner + " Wczytywanie komentarzy…" : "Brak komentarzy.", Theme.DimS);
            return;
        }
        if (_commentSorts.Count > 1)
            s.TextRight(r.Right, r.Y - 2, $"⇅ {_commentSorts[_commentSort].Title} (o)", Theme.DimS);

        int w = Math.Min(r.W, 130);
        // Każdy komentarz: nagłówek + tekst (max 6 linii) + odstęp
        var blocks = new List<(int Index, List<string> Lines)>();
        if (_commentSel < _commentScroll) _commentScroll = _commentSel;

        int Height(int i)
        {
            var c = _comments[i];
            int indent = c.IsReply ? 4 : 0;
            return 2 + Math.Min(6, TextUtil.Wrap(c.Text, w - indent - 2).Count);
        }

        // Przewijanie tak, by zaznaczony był widoczny
        while (true)
        {
            int hsum = 0;
            for (int i = _commentScroll; i <= _commentSel && i < _comments.Count; i++) hsum += Height(i);
            if (hsum <= r.H || _commentScroll >= _commentSel) break;
            _commentScroll++;
        }

        int y = r.Y;
        for (int i = _commentScroll; i < _comments.Count && y < r.Bottom; i++)
        {
            var c = _comments[i];
            int indent = c.IsReply ? 4 : 0;
            bool sel = i == _commentSel;
            int bg = sel ? Theme.Selection : Theme.Bg;
            int h = Height(i);
            s.Fill(new Rect(r.X, y, w, Math.Min(h - 1, r.Bottom - y)), new Style(Theme.Text, bg));
            if (sel) s.VLine(r.X, y, Math.Min(h - 1, r.Bottom - y), new Style(Theme.Accent, bg), "▌");
            int x = r.X + 2 + indent;
            if (c.IsReply) s.Text(r.X + 2, y, "↳", new Style(Theme.Muted, bg));
            var authorSt = c.IsCreator ? new Style(0x0F0F0F, Theme.Dim, Attr.Bold) : new Style(Theme.Text, bg, Attr.Bold);
            x += s.Text(x, y, c.IsCreator ? " " + c.Author + " " : c.Author, authorSt);
            if (c.IsPinned) x += s.Text(x + 1, y, "📌", new Style(Theme.Text, bg)) + 1;
            s.TextTrunc(x + 2, y, ItemList.Join(c.Published, c.Likes.Length > 0 ? "👍 " + c.Likes : null,
                c.ReplyCount > 0 && !c.IsReply ? $"💬 {c.ReplyCount} odp. (Enter)" : null), new Style(Theme.Dim, bg), r.X + w - x - 2);
            var lines = TextUtil.Wrap(c.Text, w - indent - 4);
            for (int l = 0; l < Math.Min(6, lines.Count) && y + 1 + l < r.Bottom; l++)
            {
                string text = l == 5 && lines.Count > 6 ? TextUtil.Truncate(lines[l], w - indent - 6) + " …" : lines[l];
                s.Text(r.X + 2 + indent, y + 1 + l, text, new Style(Theme.Text, bg), w - indent - 4);
            }
            if (_expandedLoading.Contains(i) && y + h - 1 < r.Bottom)
                s.Text(r.X + 6, y + h - 1, App.Spinner + " wczytywanie odpowiedzi…", Theme.DimS);
            y += h;
        }
        if (_commentsLoading && y < r.Bottom) s.Text(r.X + 2, y, App.Spinner + " Wczytywanie kolejnych…", Theme.DimS);
        _ = blocks;
    }
}
