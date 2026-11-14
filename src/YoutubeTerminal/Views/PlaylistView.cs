using YoutubeTerminal.Tui;
using YoutubeTerminal.YouTube;

namespace YoutubeTerminal.UI;

/// <summary>Playlista: nagłówek, lista filmów, odtwarzanie całości lub od wybranego.</summary>
internal sealed class PlaylistView : View
{
    private readonly PlaylistItem _item;
    private PlaylistPage? _page;
    private readonly ItemList _list = new();

    public PlaylistView(PlaylistItem item)
    {
        _item = item;
        _list.LoadMore = (t, ct) => App.YT.BrowseContinuationAsync(t, ct);
        _list.Loading = true;
        _list.EmptyText = "Playlista jest pusta.";
        App.RunAsync(ct => App.YT.GetPlaylistAsync(item.Id, item.FirstVideoId, ct), page =>
        {
            _page = page;
            _list.SetItems(page.Content.Items, page.Content.Continuation);
        }, ex =>
        {
            _list.Loading = false;
            _list.Error = "Nie udało się wczytać playlisty: " + ex.Message;
        });
    }

    public override string Title => _page?.Playlist.Title is { Length: > 0 } t ? t : _item.Title.Length > 0 ? _item.Title : "Playlista";
    public override ItemList? FocusedList => _list;

    private List<VideoItem> Videos => _list.Items.OfType<VideoItem>().Where(v => !v.IsUpcoming).ToList();

    public override IEnumerable<(string Key, string Label)> Hints
    {
        get
        {
            yield return ("Enter", "Otwórz");
            yield return ("p", "Odtwórz stąd");
            yield return ("P", "Odtwórz od początku");
            yield return ("r", "Losowo");
            yield return ("c", "Kanał");
            yield return ("w", "Do obejrzenia");
        }
    }

    public override bool Tick() => _list.SelectionJustSettled;

    public override bool HandleKey(KeyEvent k)
    {
        if (_list.HandleKey(k)) return true;
        var vids = Videos;
        if (k.Is('P') && vids.Count > 0) { App.Play(vids[0], vids, 0); return true; }
        if (k.Is('r') && vids.Count > 0)
        {
            var shuffled = vids.OrderBy(_ => Random.Shared.Next()).ToList();
            App.Play(shuffled[0], shuffled, 0);
            App.ShowMessage("Odtwarzanie losowe");
            return true;
        }
        if ((k.Is('p') || k.Is(' ')) && _list.Current is VideoItem v)
        {
            int idx = vids.FindIndex(x => x.Id == v.Id);
            App.Play(v, vids, Math.Max(0, idx));
            return true;
        }
        if (k.Is('c') && _list.Current == null && _page != null) { App.OpenChannel(_page.Playlist.ChannelId, _page.Playlist.ChannelName); return true; }
        return App.HandleItemKey(k, _list.Current, _list.Items);
    }

    public override bool HandleMouse(MouseEvent m)
    {
        var r = _list.HandleMouse(m);
        if (r == ListMouseResult.Activate && _list.Current is { } it) App.OpenItem(it);
        return r != ListMouseResult.None;
    }

    public override void Render(ScreenBuffer s, Rect area)
    {
        var header = new Rect(area.X, area.Y, area.W, 4);
        s.Fill(header, new Style(Theme.Text, Theme.Panel));
        var bold = new Style(Theme.Text, Theme.Panel, Attr.Bold);
        var dim = new Style(Theme.Dim, Theme.Panel);
        int x = area.X + 2, w = area.W - 4;
        s.Text(x, area.Y, _item.IsMix || _page?.Playlist.IsMix == true ? "◎ SKŁADANKA" : "≡ PLAYLISTA", new Style(Theme.Link, Theme.Panel, Attr.Bold));
        s.TextTrunc(x, area.Y + 1, TextUtil.OneLine(Title), bold, w);
        var p = _page?.Playlist;
        var meta = ItemList.Join(p?.ChannelName ?? _item.ChannelName, p?.VideoCountText is { Length: > 0 } vc ? vc : _item.VideoCountText, _page?.ViewsText);
        s.TextTrunc(x, area.Y + 2, meta, dim, w);
        if (_page?.Description is { Length: > 0 } d)
            s.TextTrunc(x, area.Y + 3, TextUtil.OneLine(d), new Style(Theme.Muted, Theme.Panel), w);

        var body = new Rect(area.X, area.Y + 5, area.W, area.H - 5);
        bool preview = body.W >= 110;
        int listW = preview ? body.W * 58 / 100 : body.W;
        _list.Render(s, new Rect(body.X, body.Y, listW, body.H), true);
        if (preview)
            PreviewPane.Render(s, new Rect(body.X + listW + 1, body.Y, body.W - listW - 1, body.H), _list.Current, _list.SelectionStable);
    }
}
