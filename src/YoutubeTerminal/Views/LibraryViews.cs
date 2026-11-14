using YoutubeTerminal.Storage;
using YoutubeTerminal.Tui;
using YoutubeTerminal.YouTube;

namespace YoutubeTerminal.UI;

/// <summary>Wspólny układ: lista po lewej, podgląd po prawej.</summary>
internal static class Layouts
{
    public static void ListWithPreview(ScreenBuffer s, Rect body, ItemList list, bool focused = true)
    {
        bool preview = body.W >= 110;
        int listW = preview ? body.W * 58 / 100 : body.W;
        list.Render(s, new Rect(body.X, body.Y, listW, body.H), focused);
        if (preview)
            PreviewPane.Render(s, new Rect(body.X + listW + 1, body.Y, body.W - listW - 1, body.H), list.Current, list.SelectionStable);
    }

    public static void SectionHeader(ScreenBuffer s, Rect area, string title, string subtitle)
    {
        s.Text(area.X + 2, area.Y + 1, title, new Style(Theme.Text, Theme.Bg, Attr.Bold));
        if (subtitle.Length > 0) s.TextTrunc(area.X + 4 + TextUtil.Width(title), area.Y + 1, subtitle, Theme.DimS, area.W - TextUtil.Width(title) - 6);
        s.HLine(area.X, area.Y + 2, area.W, new Style(0x272727, Theme.Bg));
    }
}

/// <summary>Subskrypcje: najnowsze filmy z subskrybowanych kanałów (RSS) i lista kanałów.</summary>
internal sealed class SubscriptionsView : View
{
    private readonly ItemList _feed = new() { EmptyText = "Brak filmów. Zasubskrybuj kanały klawiszem s." };
    private readonly ItemList _channels = new() { EmptyText = "Nie subskrybujesz jeszcze żadnych kanałów. Na kanale lub filmie naciśnij s." };
    private int _tab;
    private bool _dirty = true;
    private DateTime _loadedAt;
    private bool _shorts;
    private int _loadedCount, _totalCount;
    private CancellationTokenSource? _cts;
    private List<VideoItem> _allVideos = new();

    public override string Title => "Subskrypcje";

    public void MarkDirty() => _dirty = true;

    public override void OnShow()
    {
        RefreshChannels();
        if (_dirty || (DateTime.UtcNow - _loadedAt).TotalMinutes > 15) Reload();
    }

    private void RefreshChannels()
    {
        var keep = _channels.Selected;
        _channels.SetItems(App.Library.Data.Subscriptions.Select(s => (YtItem)s.ToChannel()), null);
        _channels.Select(keep);
    }

    private void Reload()
    {
        _dirty = false;
        _loadedAt = DateTime.UtcNow;
        var subs = App.Library.Data.Subscriptions.ToList();
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        if (subs.Count == 0) { _feed.SetItems([], null); return; }
        _feed.Clear();
        _feed.Loading = true;
        _loadedCount = 0;
        _totalCount = subs.Count;
        var yt = App.YT;
        App.RunAsync(async c =>
        {
            var all = new List<VideoItem>();
            var errors = 0;
            using var sem = new SemaphoreSlim(6);
            var tasks = subs.Select(async sub =>
            {
                await sem.WaitAsync(c);
                try
                {
                    var vids = await yt.GetChannelFeedAsync(sub.ChannelId, sub.Title, c);
                    lock (all) all.AddRange(vids);
                }
                catch { Interlocked.Increment(ref errors); }
                finally
                {
                    sem.Release();
                    Interlocked.Increment(ref _loadedCount);
                    App.Current.Invalidate();
                }
            });
            await Task.WhenAll(tasks);
            return (all, errors);
        }, r =>
        {
            _allVideos = r.all.OrderByDescending(v => v.Published ?? DateTime.MinValue).ToList();
            ApplyFeedFilter();
            if (r.errors > 0) App.ShowError($"Nie udało się pobrać {r.errors} kanał(ów).");
        }, ex =>
        {
            _feed.Loading = false;
            _feed.Error = "Błąd: " + ex.Message;
        }, ct: ct);
    }

    private void ApplyFeedFilter()
    {
        var items = _allVideos.Where(v => _shorts || !v.IsShort).Cast<YtItem>();
        _feed.SetItems(items, null);
    }

    private ItemList Current => _tab == 0 ? _feed : _channels;

    public override IEnumerable<(string Key, string Label)> Hints
    {
        get
        {
            yield return ("Tab", _tab == 0 ? "Kanały" : "Najnowsze");
            if (_tab == 0)
            {
                yield return ("r", "Odśwież");
                yield return ("h", _shorts ? "Ukryj Shorts" : "Pokaż Shorts");
                foreach (var h in App.ItemHints(_feed.Current).Where(h => h.Item1 is "Enter" or "p" or "w" or "c")) yield return h;
            }
            else
            {
                yield return ("Enter", "Otwórz kanał");
                yield return ("Del", "Anuluj subskrypcję");
            }
        }
    }

    public override bool Tick() => Current.SelectionJustSettled || (_feed.Loading && App.Busy);

    public override bool HandleKey(KeyEvent k)
    {
        if (k.Key == Key.Tab || k.Key == Key.BackTab || k.Key == Key.Left || k.Key == Key.Right) { _tab = 1 - _tab; return true; }
        var list = Current;
        if (list.HandleKey(k)) return true;
        if (k.Is('r')) { Reload(); App.ShowMessage("Odświeżanie subskrypcji…"); return true; }
        if (k.Is('h') && _tab == 0) { _shorts = !_shorts; ApplyFeedFilter(); return true; }
        if (_tab == 1 && (k.Key == Key.Delete || k.Is('x') || k.Is('s')) && list.Current is ChannelItem ch)
        {
            App.Confirm("Anulować subskrypcję?", $"Kanał „{ch.Title}” zniknie z subskrypcji.", () =>
            {
                App.ToggleSubscribe(ch);
                RefreshChannels();
                _dirty = true;
            });
            return true;
        }
        return App.HandleItemKey(k, list.Current, list.Items);
    }

    public override bool HandleMouse(MouseEvent m)
    {
        var r = Current.HandleMouse(m);
        if (r == ListMouseResult.Activate && Current.Current is { } it) App.OpenItem(it);
        return r != ListMouseResult.None;
    }

    public override void Render(ScreenBuffer s, Rect area)
    {
        int x = area.X + 2;
        int y = area.Y + 1;
        x += s.Text(x, y, " Najnowsze filmy ", _tab == 0 ? new Style(Theme.Text, Theme.Bg, Attr.Bold | Attr.Underline) : Theme.DimS) + 1;
        x += s.Text(x, y, $" Kanały ({App.Library.Data.Subscriptions.Count}) ", _tab == 1 ? new Style(Theme.Text, Theme.Bg, Attr.Bold | Attr.Underline) : Theme.DimS);
        if (_tab == 0 && _feed.Loading && _totalCount > 0)
            s.TextRight(area.Right - 2, y, $"{App.Spinner} kanały {_loadedCount}/{_totalCount}", Theme.DimS);
        else if (_tab == 0 && _feed.Items.Count > 0)
            s.TextRight(area.Right - 2, y, $"odświeżono {YouTubeClient.RelativeTime(_loadedAt)}" + (_shorts ? "" : " · bez Shorts"), Theme.MutedS);
        s.HLine(area.X, y + 1, area.W, new Style(0x272727, Theme.Bg));
        Layouts.ListWithPreview(s, new Rect(area.X, y + 2, area.W, area.Bottom - y - 2), Current);
    }
}

/// <summary>Historia oglądania z postępem i wznawianiem.</summary>
internal sealed class HistoryView : View
{
    private readonly ItemList _list = new() { EmptyText = "Historia jest pusta." };

    public HistoryView()
    {
        _list.ProgressOf = item =>
        {
            if (item is not VideoItem v) return -1;
            var h = App.Library.GetHistory(v.Id);
            if (h == null || h.DurationSeconds <= 0) return -1;
            return h.PositionSeconds / h.DurationSeconds;
        };
    }

    public override string Title => "Historia";

    public override void OnShow() => Refresh();

    private void Refresh()
    {
        int keep = _list.Selected;
        _list.SetItems(App.Library.Data.History.Select(h => (YtItem)h.ToVideo()), null);
        _list.Select(keep);
    }

    public override IEnumerable<(string Key, string Label)> Hints
    {
        get
        {
            yield return ("Enter", "Szczegóły");
            yield return ("p", "Wznów");
            yield return ("Del", "Usuń");
            yield return ("C", "Wyczyść");
            yield return ("c", "Kanał");
        }
    }

    public override bool Tick() => _list.SelectionJustSettled;

    public override bool HandleKey(KeyEvent k)
    {
        if (_list.HandleKey(k)) return true;
        if ((k.Key == Key.Delete || k.Is('x')) && _list.Current is VideoItem v)
        {
            App.Library.RemoveHistory(v.Id);
            Refresh();
            return true;
        }
        if (k.Is('C'))
        {
            App.Confirm("Wyczyścić historię?", "Wszystkie wpisy historii oglądania zostaną usunięte.", () =>
            {
                App.Library.ClearHistory();
                Refresh();
            });
            return true;
        }
        if ((k.Is('p') || k.Is(' ')) && _list.Current is VideoItem pv)
        {
            App.Play(pv); // wznowienie z historii odbywa się automatycznie
            return true;
        }
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
        Layouts.SectionHeader(s, area, "Historia oglądania", $"{YouTubeClient.Plural(App.Library.Data.History.Count, "film", "filmy", "filmów")} · zapisywana lokalnie");
        Layouts.ListWithPreview(s, new Rect(area.X, area.Y + 3, area.W, area.H - 3), _list);
    }
}

/// <summary>Lista „Do obejrzenia”.</summary>
internal sealed class WatchLaterView : View
{
    private readonly ItemList _list = new() { EmptyText = "Lista jest pusta. Dodawaj filmy klawiszem w." };

    public override string Title => "Do obejrzenia";

    public override void OnShow()
    {
        int keep = _list.Selected;
        _list.SetItems(App.Library.Data.WatchLater.Select(w => (YtItem)w.ToVideo()), null);
        _list.Select(keep);
    }

    public override IEnumerable<(string Key, string Label)> Hints
    {
        get
        {
            yield return ("Enter", "Szczegóły");
            yield return ("p", "Odtwórz");
            yield return ("P", "Odtwórz wszystkie");
            yield return ("Del", "Usuń");
        }
    }

    public override bool Tick() => _list.SelectionJustSettled;

    public override bool HandleKey(KeyEvent k)
    {
        if (_list.HandleKey(k)) return true;
        if ((k.Key == Key.Delete || k.Is('x') || k.Is('w')) && _list.Current is VideoItem v)
        {
            App.Library.ToggleWatchLater(v);
            OnShow();
            App.ShowMessage("Usunięto z „Do obejrzenia”");
            return true;
        }
        if (k.Is('P'))
        {
            var vids = _list.Items.OfType<VideoItem>().ToList();
            if (vids.Count > 0) App.Play(vids[0], vids, 0);
            return true;
        }
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
        Layouts.SectionHeader(s, area, "Do obejrzenia", YouTubeClient.Plural(App.Library.Data.WatchLater.Count, "film", "filmy", "filmów"));
        Layouts.ListWithPreview(s, new Rect(area.X, area.Y + 3, area.W, area.H - 3), _list);
    }
}
