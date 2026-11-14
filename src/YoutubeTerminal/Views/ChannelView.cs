using YoutubeTerminal.Tui;
using YoutubeTerminal.YouTube;

namespace YoutubeTerminal.UI;

/// <summary>Kanał: nagłówek z awatarem, zakładki (Filmy/Shorts/Na żywo/Playlisty…), sortowanie, lista i podgląd.</summary>
internal sealed class ChannelView : View
{
    private readonly string _channelId;
    private ChannelPage? _page;
    private readonly ItemList _list = new();
    private int _tab;
    private int _sort = -1;
    private string? _error;
    private CancellationTokenSource? _cts;

    public ChannelView(string channelId, string? title)
    {
        _channelId = channelId;
        _titleHint = title ?? "";
        _list.LoadMore = (t, ct) => App.YT.BrowseContinuationAsync(t, ct);
        _list.EmptyText = "Ta zakładka jest pusta.";
        _list.Loading = true;
        Load(YouTubeClient.VideosTabParams, initial: true);
    }

    private readonly string _titleHint;
    public override string Title => _page?.Channel.Title is { Length: > 0 } t ? t : _titleHint.Length > 0 ? _titleHint : "Kanał";
    public override ItemList? FocusedList => _list;

    private void Load(string? tabParams, bool initial)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _list.Clear();
        _list.Loading = true;
        App.RunAsync(c => App.YT.GetChannelAsync(_channelId, tabParams, c), page =>
        {
            var prevChannel = _page?.Channel;
            if (page.Channel.Title.Length == 0 && prevChannel != null) page.Channel = prevChannel;
            if (page.Tabs.Count == 0 && _page != null) page.Tabs = _page.Tabs;
            _page = page;
            _tab = Math.Max(0, page.Tabs.FindIndex(t => t.Selected));
            _sort = page.Sorts.FindIndex(s => s.Selected);
            _list.SetItems(page.Content.Items, page.Content.Continuation);
            // Kanał bez zakładki "Filmy" (np. kanały tematyczne) — pokazujemy pierwszą dostępną.
            if (initial && page.Content.Items.Count == 0 && page.Tabs.Count > 0 && !page.Tabs[0].Selected)
                Load(page.Tabs[0].Params, false);
        }, ex =>
        {
            _list.Loading = false;
            _error = ex.Message;
            _list.Error = "Nie udało się wczytać kanału: " + ex.Message;
        }, ct: ct);
    }

    private void SwitchTab(int dir)
    {
        if (_page == null || _page.Tabs.Count == 0) return;
        int n = (_tab + dir + _page.Tabs.Count) % _page.Tabs.Count;
        _tab = n;
        Load(_page.Tabs[n].Params, false);
    }

    private void CycleSort()
    {
        if (_page == null || _page.Sorts.Count == 0) { App.ShowMessage("Ta zakładka nie ma opcji sortowania."); return; }
        _sort = (_sort + 1) % _page.Sorts.Count;
        var chip = _page.Sorts[_sort];
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _list.Clear();
        _list.Loading = true;
        App.RunAsync(c => App.YT.BrowseContinuationAsync(chip.Token, c), page =>
        {
            _list.SetItems(page.Items, page.Continuation);
        }, ex => { _list.Loading = false; _list.Error = ex.Message; }, ct: ct);
    }

    public override IEnumerable<(string Key, string Label)> Hints
    {
        get
        {
            yield return ("← →", "Zakładki");
            if (_page?.Sorts.Count > 0) yield return ("o", "Sortuj");
            yield return ("s", App.Library.IsSubscribed(_channelId) ? "Anuluj sub." : "Subskrybuj");
            yield return ("P", "Odtwórz wszystkie");
            foreach (var h in App.ItemHints(_list.Current).Where(h => h.Item1 is "Enter" or "p" or "w")) yield return h;
        }
    }

    public override bool Tick() => _list.SelectionJustSettled;

    public override bool HandleKey(KeyEvent k)
    {
        if (k.Key == Key.Left || k.Key == Key.BackTab) { SwitchTab(-1); return true; }
        if (k.Key == Key.Right || k.Key == Key.Tab) { SwitchTab(1); return true; }
        if (_list.HandleKey(k)) return true;
        if (k.Is('o')) { CycleSort(); return true; }
        if (k.Is('s'))
        {
            var ch = _page?.Channel ?? new ChannelItem { Id = _channelId, Title = Title };
            if (ch.Id.Length == 0) ch.Id = _channelId;
            App.ToggleSubscribe(ch);
            return true;
        }
        if (k.Is('P'))
        {
            var vids = _list.Items.OfType<VideoItem>().Where(v => !v.IsLive && !v.IsUpcoming).ToList();
            if (vids.Count > 0) App.Play(vids[0], vids, 0);
            return true;
        }
        if (k.Is('c')) return true; // już jesteśmy na kanale
        return App.HandleItemKey(k, _list.Current, _list.Items);
    }

    public override bool HandleMouse(MouseEvent m)
    {
        if (m.Kind == MouseKind.Press && m.Y == _tabsY && _page != null)
        {
            foreach (var (x0, x1, idx) in _tabHits)
                if (m.X >= x0 && m.X < x1) { _tab = idx; Load(_page.Tabs[idx].Params, false); return true; }
        }
        var r = _list.HandleMouse(m);
        if (r == ListMouseResult.Activate && _list.Current is { } it) App.OpenItem(it);
        return r != ListMouseResult.None;
    }

    private int _tabsY;
    private readonly List<(int, int, int)> _tabHits = new();

    public override void Render(ScreenBuffer s, Rect area)
    {
        var app = App;
        var ch = _page?.Channel;
        // Nagłówek
        int headerH = 5;
        var header = new Rect(area.X, area.Y, area.W, headerH);
        s.Fill(header, new Style(Theme.Text, Theme.Panel));
        int avatarRows = headerH - 1;
        int avatarCols = app.ImageMode == Graphics.RenderMode.Sixel
            ? (int)Math.Round(avatarRows * app.CellHeight / (double)app.CellWidth)
            : avatarRows * 2;
        var avRect = new Rect(area.X + 2, area.Y, avatarCols, avatarRows);
        if (ch?.AvatarUrl is { Length: > 0 } av)
        {
            var img = app.Images.Get(av, avRect);
            if (img != null) s.AddImage(img);
        }
        int tx = avRect.Right + 2;
        int tw = area.Right - tx - 2;
        var bold = new Style(Theme.Text, Theme.Panel, Attr.Bold);
        var dim = new Style(Theme.Dim, Theme.Panel);
        s.TextTrunc(tx, area.Y, Title, bold, tw - 20);
        if (app.Library.IsSubscribed(_channelId))
            s.TextRight(area.Right - 2, area.Y, " ✓ Subskrybujesz ", new Style(Theme.Text, 0x303030, Attr.Bold));
        else
            s.TextRight(area.Right - 2, area.Y, " Subskrybuj (s) ", new Style(0x0F0F0F, Theme.Text, Attr.Bold));
        if (ch != null)
        {
            s.TextTrunc(tx, area.Y + 1, ItemList.Join(ch.Handle, ch.SubscribersText, ch.VideoCountText), dim, tw);
            var desc = TextUtil.Wrap(TextUtil.OneLine(ch.Description), tw, 2);
            for (int i = 0; i < desc.Count; i++) s.Text(tx, area.Y + 2 + i, desc[i], new Style(Theme.Muted, Theme.Panel), tw);
        }
        else if (_error != null) s.TextTrunc(tx, area.Y + 1, _error, new Style(Theme.Accent, Theme.Panel), tw);
        else s.Text(tx, area.Y + 1, app.Spinner + " Wczytywanie kanału…", dim);

        // Zakładki
        int ty = area.Y + headerH;
        _tabsY = ty;
        _tabHits.Clear();
        s.Fill(new Rect(area.X, ty, area.W, 1), Theme.Normal);
        int x = area.X + 2;
        if (_page != null)
        {
            for (int i = 0; i < _page.Tabs.Count; i++)
            {
                bool act = i == _tab;
                int start = x;
                x += s.Text(x, ty, " " + _page.Tabs[i].Title + " ", act ? new Style(Theme.Text, Theme.Bg, Attr.Bold | Attr.Underline) : Theme.DimS);
                _tabHits.Add((start, x, i));
                x += 1;
            }
            if (_page.Sorts.Count > 0)
            {
                string sortLbl = "⇅ " + (_sort >= 0 && _sort < _page.Sorts.Count ? _page.Sorts[_sort].Title : _page.Sorts[0].Title) + " (o)";
                s.TextRight(area.Right - 2, ty, sortLbl, Theme.DimS);
            }
        }
        s.HLine(area.X, ty + 1, area.W, new Style(0x272727, Theme.Bg));

        var body = new Rect(area.X, ty + 2, area.W, area.Bottom - ty - 2);
        bool preview = body.W >= 110;
        int listW = preview ? body.W * 58 / 100 : body.W;
        _list.Render(s, new Rect(body.X, body.Y, listW, body.H), true);
        if (preview)
            PreviewPane.Render(s, new Rect(body.X + listW + 1, body.Y, body.W - listW - 1, body.H), _list.Current, _list.SelectionStable);
    }
}
