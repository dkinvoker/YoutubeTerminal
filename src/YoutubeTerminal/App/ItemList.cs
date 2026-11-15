using YoutubeTerminal.Graphics;
using YoutubeTerminal.Tui;
using YoutubeTerminal.YouTube;

namespace YoutubeTerminal.UI;

internal enum ListMouseResult { None, Moved, Activate }

/// <summary>
/// Lista filmów/kanałów/playlist z miniaturami (Sixel), zaznaczeniem, przewijaniem
/// i automatycznym doładowywaniem kolejnych stron (token kontynuacji).
/// </summary>
internal sealed class ItemList
{
    public List<YtItem> Items { get; } = new();
    public int Selected { get; private set; }
    private int _scroll;
    private Rect _area;
    private int _itemH = 3;
    private DateTime _selChanged = DateTime.UtcNow;
    private DateTime _lastClick;
    private int _lastClickIndex = -1;
    private CancellationTokenSource? _loadCts;

    public string? Continuation;
    public Func<string, CancellationToken, Task<FeedPage>>? LoadMore;
    public bool LoadingMore { get; private set; }
    public bool Loading;
    public string? Error;
    public string EmptyText = L.T("Brak wyników.", "No results.");
    public Func<YtItem, double>? ProgressOf;
    public bool AllowThumbnails = true;

    public YtItem? Current => Selected >= 0 && Selected < Items.Count ? Items[Selected] : null;
    /// <summary>Kiedy zmieniło się zaznaczenie.</summary>
    public DateTime SelectedAt => _selChanged;
    public bool SelectionStable => (DateTime.UtcNow - _selChanged).TotalMilliseconds > 140;
    public bool SelectionJustSettled
    {
        get
        {
            var ms = (DateTime.UtcNow - _selChanged).TotalMilliseconds;
            return ms > 140 && ms < 400;
        }
    }

    public void SetItems(IEnumerable<YtItem> items, string? continuation)
    {
        _loadCts?.Cancel();
        LoadingMore = false;
        Items.Clear();
        Items.AddRange(items);
        Continuation = continuation;
        Selected = 0;
        _scroll = 0;
        Loading = false;
        Error = null;
        _selChanged = DateTime.UtcNow;
    }

    public void Clear()
    {
        SetItems([], null);
    }

    public void Select(int index)
    {
        if (Items.Count == 0) { Selected = 0; return; }
        int n = Math.Clamp(index, 0, Items.Count - 1);
        if (n != Selected) _selChanged = DateTime.UtcNow;
        Selected = n;
        EnsureVisible();
        MaybeLoadMore();
    }

    private bool Thumbs => AllowThumbnails && App.Current.ListThumbnails;

    public int ItemHeight => Thumbs ? 4 : 3;

    private int VisibleCount => Math.Max(1, _area.H / Math.Max(1, _itemH));

    private void EnsureVisible()
    {
        int vis = VisibleCount;
        if (Selected < _scroll) _scroll = Selected;
        if (Selected >= _scroll + vis) _scroll = Selected - vis + 1;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, Items.Count - vis));
    }

    public void MaybeLoadMore()
    {
        if (LoadMore == null || Continuation == null || LoadingMore) return;
        if (Selected < Items.Count - Math.Max(6, VisibleCount)) return;
        var token = Continuation;
        LoadingMore = true;
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        App.Current.RunAsync(c => LoadMore(token, c), page =>
        {
            if (ct.IsCancellationRequested) return;
            var seen = new HashSet<string>(Items.Select(i => i.GetType().Name + i.Id));
            foreach (var it in page.Items)
                if (seen.Add(it.GetType().Name + it.Id)) Items.Add(it);
            Continuation = page.Items.Count == 0 ? null : page.Continuation;
            LoadingMore = false;
            // Strona mogła zawierać tylko duplikaty — próbujemy dalej.
            if (Continuation != null) MaybeLoadMore();
        }, ex =>
        {
            LoadingMore = false;
            Continuation = null;
            App.Current.ShowError(L.T("Nie udało się wczytać kolejnych wyników: ", "Could not load more results: ") + ex.Message);
        }, quiet: true, ct: ct);
    }

    public bool HandleKey(KeyEvent k)
    {
        if (Items.Count == 0) return false;
        int page = Math.Max(1, VisibleCount - 1);
        switch (k.Key)
        {
            case Key.Up: Select(Selected - 1); return true;
            case Key.Down: Select(Selected + 1); return true;
            case Key.PageUp: Select(Selected - page); return true;
            case Key.PageDown: Select(Selected + page); return true;
            case Key.Home: Select(0); return true;
            case Key.End: Select(Items.Count - 1); return true;
        }
        if (k.Is('k')) { Select(Selected - 1); return true; }
        if (k.Is('j')) { Select(Selected + 1); return true; }
        return false;
    }

    public ListMouseResult HandleMouse(MouseEvent m)
    {
        if (!_area.Contains(m.X, m.Y)) return ListMouseResult.None;
        switch (m.Kind)
        {
            case MouseKind.WheelUp: Select(Selected - 2); return ListMouseResult.Moved;
            case MouseKind.WheelDown: Select(Selected + 2); return ListMouseResult.Moved;
            case MouseKind.Press when m.Button == 0:
                int idx = _scroll + (m.Y - _area.Y) / Math.Max(1, _itemH);
                if (idx < 0 || idx >= Items.Count) return ListMouseResult.None;
                bool dbl = idx == _lastClickIndex && (DateTime.UtcNow - _lastClick).TotalMilliseconds < 450;
                _lastClick = DateTime.UtcNow;
                _lastClickIndex = idx;
                if (idx == Selected && dbl) return ListMouseResult.Activate;
                Select(idx);
                return ListMouseResult.Moved;
        }
        return ListMouseResult.None;
    }

    public void Render(ScreenBuffer s, Rect area, bool focused)
    {
        _area = area;
        _itemH = ItemHeight;
        if (area.W < 10 || area.H < 2) return;

        if (Items.Count == 0)
        {
            string msg = Loading ? App.Current.Spinner + L.T(" Wczytywanie…", " Loading…") : Error ?? EmptyText;
            s.TextCenter(area, area.Y + Math.Min(2, area.H - 1), msg, Error != null ? new Style(Theme.Accent, Theme.Bg) : Theme.DimS);
            return;
        }

        EnsureVisible();
        bool thumbs = Thumbs;
        var app = App.Current;
        int thumbRows = _itemH - 1;
        int thumbCols = thumbs ? Math.Max(4, (int)Math.Round(thumbRows * app.CellHeight * 16.0 / 9.0 / app.CellWidth)) : 0;

        for (int i = _scroll; i < Items.Count; i++)
        {
            int y = area.Y + (i - _scroll) * _itemH;
            if (y + _itemH - 1 > area.Bottom) break;
            var item = Items[i];
            bool sel = i == Selected;
            int bg = sel ? (focused ? Theme.SelectionFocus : Theme.Selection) : Theme.Bg;
            var rowRect = new Rect(area.X, y, area.W, _itemH - 1);
            s.Fill(rowRect, new Style(Theme.Text, bg));
            if (sel && focused) s.VLine(area.X, y, _itemH - 1, new Style(Theme.Accent, bg), "▌");

            int tx = area.X + 2;
            if (thumbs)
            {
                var thumbRect = new Rect(area.X + 2, y, thumbCols, thumbRows);
                if (item is ChannelItem)
                {
                    int sq = Math.Max(2, (int)Math.Round(thumbRows * app.CellHeight / (double)app.CellWidth));
                    thumbRect = new Rect(area.X + 2 + (thumbCols - sq) / 2, y, sq, thumbRows);
                }
                var img = string.IsNullOrEmpty(item.ThumbnailUrl) ? null : app.Images.Get(item.ThumbnailUrl, thumbRect, cover: true);
                if (img != null) s.AddImage(img);
                else s.Fill(thumbRect, new Style(Theme.Muted, Theme.Raised));
                tx = area.X + 2 + thumbCols + 2;
            }
            int tw = area.Right - tx - 1;
            if (tw < 8) continue;
            RenderText(s, item, tx, y, tw, bg, sel);
        }

        // Pasek przewijania
        if (Items.Count > VisibleCount)
        {
            int trackH = area.H;
            int thumbH = Math.Max(1, trackH * VisibleCount / Items.Count);
            int pos = (int)((long)(trackH - thumbH) * _scroll / Math.Max(1, Items.Count - VisibleCount));
            for (int r = 0; r < trackH; r++)
                s.Put(area.Right - 1, area.Y + r, r >= pos && r < pos + thumbH ? "┃" : "│",
                    new Style(r >= pos && r < pos + thumbH ? Theme.Dim : 0x2A2A2A, Theme.Bg));
        }

        if (LoadingMore)
        {
            int ly = area.Y + (Math.Min(Items.Count, _scroll + VisibleCount) - _scroll) * _itemH;
            if (ly < area.Bottom) s.Text(area.X + 2, ly, app.Spinner + L.T(" Wczytywanie kolejnych…", " Loading more…"), Theme.DimS);
        }
    }

    private void RenderText(ScreenBuffer s, YtItem item, int x, int y, int w, int bg, bool sel)
    {
        var title = new Style(Theme.Text, bg, Attr.Bold);
        var dim = new Style(Theme.Dim, bg);
        var muted = new Style(Theme.Muted, bg);
        int lines = _itemH - 1;
        // Znacznik „do obejrzenia” przy filmach
        string? badge = item is VideoItem bv && App.Current.Library.IsInWatchLater(bv.Id) ? "⏲" : null;

        switch (item)
        {
            case VideoItem v:
            {
                int cx = x;
                if (v.IsLive) cx += s.Text(cx, y, L.T(" NA ŻYWO ", " LIVE "), Theme.LiveS) + 1;
                else if (v.IsUpcoming) cx += s.Text(cx, y, L.T(" PREMIERA ", " PREMIERE "), new Style(0xFFFFFF, 0x065FD4, Attr.Bold)) + 1;
                else if (v.IsShort) cx += s.Text(cx, y, " Shorts ", new Style(0xFFFFFF, Theme.AccentSoft, Attr.Bold)) + 1;
                string dur = v.DurationText;
                int durW = dur.Length > 0 ? TextUtil.Width(dur) + 2 : 0;
                s.TextTrunc(cx, y, TextUtil.OneLine(v.Title), title, x + w - cx - durW - 1);
                if (durW > 0) s.Text(x + w - durW, y, " " + dur + " ", new Style(Theme.Text, 0x000000 | 0x303030, Attr.Bold));

                var meta = Join(v.ChannelName, v.ViewsText, v.PublishedText);
                int bw = badge != null ? TextUtil.Width(badge) + 1 : 0;
                if (lines >= 2) s.TextTrunc(x, y + 1, meta, dim, w - bw);
                if (badge != null && lines >= 2) s.Text(x + w - bw + 1, y + 1, badge, new Style(Theme.Warn, bg));
                if (lines >= 3)
                {
                    double prog = ProgressOf?.Invoke(item) ?? -1;
                    if (prog >= 0) DrawProgress(s, x, y + 2, Math.Min(w, 40), prog, bg);
                    else if (v.Description.Length > 0) s.TextTrunc(x, y + 2, TextUtil.OneLine(v.Description), muted, w);
                }
                break;
            }
            case ChannelItem c:
            {
                int cx = x + s.Text(x, y, "◉ ", new Style(Theme.Accent, bg));
                s.TextTrunc(cx, y, c.Title, title, w - 2);
                if (lines >= 2) s.TextTrunc(x, y + 1, Join(L.T("Kanał", "Channel"), c.Handle, c.SubscribersText, c.VideoCountText), dim, w);
                if (lines >= 3)
                {
                    if (App.Current.Library.IsSubscribed(c.Id)) s.Text(x, y + 2, L.T("✓ Subskrybujesz", "✓ Subscribed"), new Style(Theme.Good, bg));
                    else if (c.Description.Length > 0) s.TextTrunc(x, y + 2, TextUtil.OneLine(c.Description), muted, w);
                }
                break;
            }
            case PlaylistItem p:
            {
                int cx = x + s.Text(x, y, p.IsMix ? "◎ " : "≡ ", new Style(Theme.Link, bg, Attr.Bold));
                s.TextTrunc(cx, y, TextUtil.OneLine(p.Title), title, w - 2);
                if (lines >= 2) s.TextTrunc(x, y + 1, Join(p.IsMix ? L.T("Składanka", "Mix") : L.T("Playlista", "Playlist"), p.ChannelName, p.VideoCountText), dim, w);
                break;
            }
        }
        _ = sel;
    }

    public static void DrawProgress(ScreenBuffer s, int x, int y, int w, double frac, int bg)
    {
        frac = Math.Clamp(frac, 0, 1);
        int filled = (int)Math.Round(frac * w);
        for (int i = 0; i < w; i++)
            s.Put(x + i, y, "━", new Style(i < filled ? Theme.Accent : 0x505050, bg));
    }

    public static string Join(params string?[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => TextUtil.OneLine(p)));
}

/// <summary>Panel podglądu zaznaczonego elementu (duża miniatura + szczegóły).</summary>
internal static class PreviewPane
{
    public static void Render(ScreenBuffer s, Rect area, YtItem? item, bool stable)
    {
        if (area.W < 20 || area.H < 6 || item == null) return;
        var app = App.Current;
        s.Fill(area, new Style(Theme.Text, Theme.Panel));
        var inner = area.Inset(2, 1);
        int y = inner.Y;

        // Miniatura 16:9 na całą szerokość panelu (max połowa wysokości).
        int imgRows;
        if (app.ImageMode == RenderMode.Sixel)
            imgRows = (int)Math.Round(inner.W * app.CellWidth * 9.0 / 16.0 / app.CellHeight);
        else
            imgRows = (int)Math.Round(inner.W * 9.0 / 16.0 / 2.0 * (app.CellWidth / (app.CellHeight / 2.0)));
        if (item is ChannelItem) imgRows = Math.Min(imgRows, 8);
        imgRows = Math.Clamp(imgRows, 3, Math.Max(3, inner.H / 2));
        var imgRect = new Rect(inner.X, y, inner.W, imgRows);
        if (stable)
        {
            string url = item.ThumbnailUrl;
            string? fallback = null;
            if (item is VideoItem v && !v.IsShort) { url = v.LargeThumbnailUrl; fallback = v.ThumbnailUrl; }
            if (item is ChannelItem ch) url = ch.AvatarUrl.Replace("s176-", "s400-", StringComparison.Ordinal).Replace("=s88", "=s400", StringComparison.Ordinal);
            var img = string.IsNullOrEmpty(url) ? null : app.Images.Get(url, imgRect, fallback);
            if (img != null) s.AddImage(img);
            else s.Fill(imgRect, new Style(Theme.Muted, Theme.Raised));
        }
        else s.Fill(imgRect, new Style(Theme.Muted, Theme.Raised));
        y += imgRows + 1;

        var bold = new Style(Theme.Text, Theme.Panel, Attr.Bold);
        var dim = new Style(Theme.Dim, Theme.Panel);
        var txt = new Style(Theme.Text, Theme.Panel);
        var restRect = new Rect(inner.X, y, inner.W, inner.Bottom - y);

        switch (item)
        {
            case VideoItem v:
            {
                y += s.Paragraph(new Rect(inner.X, y, inner.W, Math.Min(3, inner.Bottom - y)), TextUtil.OneLine(v.Title), bold);
                y++;
                if (v.ChannelName.Length > 0 && y < inner.Bottom) { s.TextTrunc(inner.X, y++, v.ChannelName, new Style(Theme.Text, Theme.Panel), inner.W); }
                var meta = ItemList.Join(v.ViewsText, v.PublishedText, v.DurationText.Length > 0 ? "⏱ " + v.DurationText : null);
                if (y < inner.Bottom) s.TextTrunc(inner.X, y++, meta, dim, inner.W);
                var flags = new List<string>();
                if (app.Library.IsInWatchLater(v.Id)) flags.Add(L.T("⏲ Do obejrzenia", "⏲ Watch later"));
                if (app.Library.GetHistory(v.Id) is { } h && h.PositionSeconds > 5) flags.Add(L.T("▶ Obejrzano do ", "▶ Watched up to ") + TextUtil.FormatSeconds(h.PositionSeconds));
                if (v.ChannelId.Length > 0 && app.Library.IsSubscribed(v.ChannelId)) flags.Add(L.T("✓ Subskrybujesz", "✓ Subscribed"));
                if (flags.Count > 0 && y < inner.Bottom) s.TextTrunc(inner.X, y++, string.Join("   ", flags), new Style(Theme.Warn, Theme.Panel), inner.W);
                y++;
                if (v.Description.Length > 0 && y < inner.Bottom)
                    s.Paragraph(new Rect(inner.X, y, inner.W, inner.Bottom - y), v.Description, dim);
                break;
            }
            case ChannelItem c:
            {
                y += s.Paragraph(new Rect(inner.X, y, inner.W, 2), c.Title, bold);
                if (y < inner.Bottom) s.TextTrunc(inner.X, y++, ItemList.Join(c.Handle, c.SubscribersText, c.VideoCountText), dim, inner.W);
                if (app.Library.IsSubscribed(c.Id) && y < inner.Bottom) s.Text(inner.X, y++, L.T("✓ Subskrybujesz", "✓ Subscribed"), new Style(Theme.Good, Theme.Panel));
                y++;
                if (c.Description.Length > 0 && y < inner.Bottom)
                    s.Paragraph(new Rect(inner.X, y, inner.W, inner.Bottom - y), c.Description, txt);
                break;
            }
            case PlaylistItem p:
            {
                y += s.Paragraph(new Rect(inner.X, y, inner.W, 3), p.Title, bold);
                y++;
                if (y < inner.Bottom) s.TextTrunc(inner.X, y++, ItemList.Join(p.IsMix ? L.T("Składanka", "Mix") : L.T("Playlista", "Playlist"), p.VideoCountText), dim, inner.W);
                if (p.ChannelName.Length > 0 && y < inner.Bottom) s.TextTrunc(inner.X, y++, p.ChannelName, txt, inner.W);
                y++;
                if (y < inner.Bottom) s.TextTrunc(inner.X, y, L.T("Enter — otwórz playlistę   p — odtwórz wszystko", "Enter — open the playlist   p — play all"), dim, inner.W);
                break;
            }
        }
        _ = restRect;
    }
}
