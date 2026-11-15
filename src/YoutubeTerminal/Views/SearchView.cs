using YoutubeTerminal.Tui;
using YoutubeTerminal.YouTube;

namespace YoutubeTerminal.UI;

/// <summary>Wyszukiwarka z podpowiedziami, filtrami, sortowaniem, wynikami i podglądem.</summary>
internal sealed class SearchView : View
{
    private readonly TextInput _input = new() { Placeholder = L.T("Szukaj w YouTube lub wklej link…", "Search YouTube or paste a link…") };
    private readonly ItemList _list = new();
    private bool _inputFocused = true;
    private SearchFilter _filter = SearchFilter.All;
    private SearchSort _sort = SearchSort.Relevance;
    private string _lastQuery = "";
    private List<string> _suggestions = new();
    private int _suggestionIndex = -1;
    private CancellationTokenSource? _suggestCts;
    private CancellationTokenSource? _searchCts;
    private DateTime _lastEdit;
    private bool _suggestPending;
    private int _historyIndex;

    private static string[] FilterNames => [L.T("Wszystko", "All"), L.T("Filmy", "Videos"), L.T("Kanały", "Channels"), L.T("Playlisty", "Playlists")];
    private static string[] SortNames => [L.T("Trafność", "Relevance"), L.T("Data przesłania", "Upload date"), L.T("Wyświetlenia", "View count"), L.T("Ocena", "Rating")];

    public SearchView()
    {
        _input.Changed += () =>
        {
            _lastEdit = DateTime.UtcNow;
            _suggestPending = true;
            _suggestionIndex = -1;
        };
        _list.LoadMore = (token, ct) => App.YT.SearchContinuationAsync(token, ct);
        _list.EmptyText = L.T("Brak wyników.", "No results.");
    }

    public override string Title => _lastQuery.Length > 0 ? L.T($"„{_lastQuery}”", $"“{_lastQuery}”") : L.T("Szukaj", "Search");
    public override ItemList? FocusedList => _inputFocused ? null : _list;
    public override bool CapturesText => _inputFocused;

    public void FocusInput()
    {
        _inputFocused = true;
        App.Invalidate();
    }

    /// <summary>Wyszukuje (lub otwiera link) — np. z argumentów linii poleceń.</summary>
    public void Submit(string query)
    {
        _input.Text = query;
        DoSearch(query);
    }

    public override IEnumerable<(string Key, string Label)> Hints
    {
        get
        {
            if (_inputFocused)
            {
                yield return ("Enter", L.T("Szukaj", "Search"));
                yield return ("Tab", L.T("Filtr", "Filter"));
                yield return ("S-Tab", L.T("Sortuj", "Sort"));
                if (_list.Items.Count > 0) yield return ("↓", L.T("Wyniki", "Results"));
            }
            else
            {
                foreach (var h in App.ItemHints(_list.Current)) yield return h;
                yield return ("/", L.T("Szukaj", "Search"));
                yield return ("Tab", L.T("Filtr", "Filter"));
            }
        }
    }

    public override bool Tick()
    {
        bool redraw = false;
        if (_suggestPending && (DateTime.UtcNow - _lastEdit).TotalMilliseconds > 220)
        {
            _suggestPending = false;
            RequestSuggestions();
        }
        if (_list.SelectionJustSettled) redraw = true;
        return redraw;
    }

    private void RequestSuggestions()
    {
        _suggestCts?.Cancel();
        var q = _input.Text.Trim();
        if (q.Length < 2 || !_inputFocused || UrlParser.Parse(q) != null)
        {
            _suggestions.Clear();
            App.Invalidate();
            return;
        }
        _suggestCts = new CancellationTokenSource();
        var ct = _suggestCts.Token;
        App.RunAsync(c => App.YT.SuggestAsync(q, c), list =>
        {
            if (_input.Text.Trim() != q) return;
            _suggestions = list.Take(8).ToList();
            _suggestionIndex = -1;
        }, _ => { }, quiet: true, ct: ct);
    }

    private void DoSearch(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return;
        _suggestCts?.Cancel();
        _suggestions.Clear();
        _suggestPending = false;
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        // Link YouTube lub @uchwyt — otwieramy bezpośrednio.
        if (UrlParser.Parse(query) != null)
        {
            App.RunAsync(c => App.YT.ResolveAsync(query, c), r =>
            {
                if (r == null) { RunSearch(query, ct); return; }
                switch (r.Kind)
                {
                    case "video":
                        App.OpenVideo(new VideoItem { Id = r.Id }, r.StartSeconds);
                        if (r.StartSeconds > 0) App.ShowMessage(L.T($"Link wskazuje {TextUtil.FormatSeconds(r.StartSeconds)} — użyj p, aby odtworzyć.", $"The link points to {TextUtil.FormatSeconds(r.StartSeconds)} — press p to play from there."));
                        break;
                    case "channel": App.OpenChannel(r.Id); break;
                    case "playlist": App.OpenPlaylist(new PlaylistItem { Id = r.Id, Title = L.T("Playlista", "Playlist") }); break;
                }
            }, ex => App.ShowError(L.T("Nie rozpoznano linku: ", "Unrecognized link: ") + ex.Message), ct: ct);
            return;
        }
        RunSearch(query, ct);
    }

    private void RunSearch(string query, CancellationToken ct)
    {
        _lastQuery = query;
        App.Library.AddSearch(query);
        _list.Clear();
        _list.Loading = true;
        _inputFocused = false;
        var filter = _filter;
        var sort = _sort;
        App.RunAsync(c => App.YT.SearchAsync(query, filter, sort, c), page =>
        {
            _list.SetItems(page.Items, page.Continuation);
            if (page.Items.Count == 0) _inputFocused = true;
        }, ex =>
        {
            _list.Loading = false;
            _list.Error = L.T("Błąd wyszukiwania: ", "Search error: ") + ex.Message;
            _inputFocused = true;
        }, ct: ct);
    }

    private void CycleFilter(int dir)
    {
        _filter = (SearchFilter)(((int)_filter + dir + 4) % 4);
        if (_lastQuery.Length > 0) RunSearch(_lastQuery, CancellationToken.None);
    }

    private void CycleSort()
    {
        _sort = (SearchSort)(((int)_sort + 1) % 4);
        if (_lastQuery.Length > 0) RunSearch(_lastQuery, CancellationToken.None);
    }

    public override void HandlePaste(string text)
    {
        _inputFocused = true;
        _input.Insert(text.Trim());
    }

    public override bool HandleKey(KeyEvent k)
    {
        if (k.Key == Key.Tab) { CycleFilter(1); return true; }
        if (k.Key == Key.BackTab) { CycleSort(); return true; }

        if (_inputFocused)
        {
            if (_suggestions.Count > 0)
            {
                if (k.Key == Key.Down) { _suggestionIndex = Math.Min(_suggestions.Count - 1, _suggestionIndex + 1); return true; }
                if (k.Key == Key.Up) { _suggestionIndex = Math.Max(-1, _suggestionIndex - 1); return true; }
                if (k.Key == Key.Escape) { _suggestions.Clear(); return true; }
            }
            switch (k.Key)
            {
                case Key.Enter:
                    var q = _suggestionIndex >= 0 && _suggestionIndex < _suggestions.Count ? _suggestions[_suggestionIndex] : _input.Text;
                    if (_suggestionIndex >= 0) _input.Text = q;
                    DoSearch(q);
                    return true;
                case Key.Down:
                    if (_list.Items.Count > 0) { _inputFocused = false; return true; }
                    var hist = App.Library.Data.SearchHistory;
                    if (hist.Count > 0 && _input.Text.Length == 0) { _historyIndex = 0; _inputFocused = false; return true; }
                    return true;
                case Key.Escape:
                    if (_input.Text.Length > 0 && _list.Items.Count == 0) { _input.Text = ""; return true; }
                    if (_list.Items.Count > 0) { _inputFocused = false; return true; }
                    return false;
            }
            return _input.HandleKey(k);
        }

        // Fokus na wynikach (lub na historii wyszukiwania, gdy brak wyników)
        if (_list.Items.Count == 0)
        {
            var hist = App.Library.Data.SearchHistory;
            switch (k.Key)
            {
                case Key.Up:
                    if (_historyIndex == 0) _inputFocused = true; else _historyIndex--;
                    return true;
                case Key.Down: _historyIndex = Math.Min(hist.Count - 1, _historyIndex + 1); return true;
                case Key.Enter:
                    if (_historyIndex < hist.Count) { _input.Text = hist[_historyIndex]; DoSearch(hist[_historyIndex]); }
                    return true;
                case Key.Delete:
                    if (_historyIndex < hist.Count) { hist.RemoveAt(_historyIndex); App.Library.Save(); _historyIndex = Math.Max(0, Math.Min(_historyIndex, hist.Count - 1)); }
                    if (hist.Count == 0) _inputFocused = true;
                    return true;
            }
            if (k.Is('/') || k.Is('i') || k.Key == Key.Escape) { _inputFocused = true; return true; }
            return false;
        }

        if (k.Is('/') || k.Is('i')) { _inputFocused = true; return true; }
        if (k.Key == Key.Up && _list.Selected == 0) { _inputFocused = true; return true; }
        if (_list.HandleKey(k)) return true;
        if (k.Is('f')) { CycleFilter(1); return true; }
        if (k.Is('o') && _list.Current == null) { CycleSort(); return true; }
        return App.HandleItemKey(k, _list.Current, _list.Items);
    }

    public override bool HandleMouse(MouseEvent m)
    {
        if (m.Kind == MouseKind.Press && m.Y == _inputY) { _inputFocused = true; return true; }
        var r = _list.HandleMouse(m);
        if (r != ListMouseResult.None) _inputFocused = false;
        if (r == ListMouseResult.Activate && _list.Current is { } it) App.OpenItem(it);
        return r != ListMouseResult.None;
    }

    private int _inputY;

    public override void Render(ScreenBuffer s, Rect area)
    {
        // Pasek wyszukiwania
        int y = area.Y + 1;
        _inputY = y;
        int filterW = 0;
        string filterText = $" {FilterNames[(int)_filter]} ▾ ";
        string sortText = _sort == SearchSort.Relevance ? "" : $" ⇅ {SortNames[(int)_sort]} ";
        filterW = TextUtil.Width(filterText) + TextUtil.Width(sortText) + 2;
        int boxW = Math.Max(20, Math.Min(area.W - filterW - 6, 100));
        int bx = area.X + 2;
        var boxStyle = new Style(Theme.Text, _inputFocused ? 0x121212 : Theme.Bg);
        var borderSt = new Style(_inputFocused ? 0x1C62B9 : Theme.Border, Theme.Bg);
        s.Box(new Rect(bx, y - 1, boxW, 3), borderSt);
        s.Text(bx + 2, y, "⌕", new Style(Theme.Dim, Theme.Bg));
        s.Fill(new Rect(bx + 4, y, boxW - 6, 1), boxStyle);
        _input.Render(s, bx + 4, y, boxW - 6, boxStyle, _inputFocused);
        int fx = bx + boxW + 2;
        fx += s.Text(fx, y, filterText, new Style(Theme.Text, 0x272727, Attr.Bold));
        if (sortText.Length > 0) s.Text(fx + 1, y, sortText, new Style(Theme.Text, 0x272727));

        var body = new Rect(area.X, y + 2, area.W, area.Bottom - (y + 2));
        if (_list.Items.Count == 0 && !_list.Loading && _list.Error == null)
            RenderWelcome(s, body);
        else
        {
            bool preview = body.W >= 110;
            int listW = preview ? body.W * 58 / 100 : body.W;
            var listRect = new Rect(body.X, body.Y, listW, body.H);
            _list.Render(s, listRect, !_inputFocused);
            if (preview)
                PreviewPane.Render(s, new Rect(body.X + listW + 1, body.Y, body.W - listW - 1, body.H), _list.Current, _list.SelectionStable);
        }

        // Podpowiedzi (nad wynikami)
        if (_inputFocused && _suggestions.Count > 0)
        {
            var sr = new Rect(bx + 2, y + 1, Math.Min(boxW - 4, 70), _suggestions.Count + 2);
            s.Images.RemoveAll(i => i.Area.Y < sr.Bottom && i.Area.Bottom > sr.Y && i.Area.X < sr.Right && i.Area.Right > sr.X);
            s.Fill(sr, new Style(Theme.Text, Theme.Raised));
            s.Box(sr, new Style(Theme.Border, Theme.Raised));
            for (int i = 0; i < _suggestions.Count; i++)
            {
                bool sel = i == _suggestionIndex;
                var st = new Style(Theme.Text, sel ? Theme.SelectionFocus : Theme.Raised, sel ? Attr.Bold : Attr.None);
                s.Fill(new Rect(sr.X + 1, sr.Y + 1 + i, sr.W - 2, 1), st);
                s.TextTrunc(sr.X + 2, sr.Y + 1 + i, "⌕ " + _suggestions[i], st, sr.W - 4);
            }
        }
    }

    private void RenderWelcome(ScreenBuffer s, Rect r)
    {
        if (r.H < 4) return;
        int y = r.Y + 1;
        var big = new[]
        {
            "██╗   ██╗ ██████╗ ██╗   ██╗████████╗██╗   ██╗██████╗ ███████╗",
            "╚██╗ ██╔╝██╔═══██╗██║   ██║╚══██╔══╝██║   ██║██╔══██╗██╔════╝",
            " ╚████╔╝ ██║   ██║██║   ██║   ██║   ██║   ██║██████╔╝█████╗  ",
            "  ╚██╔╝  ██║   ██║██║   ██║   ██║   ██║   ██║██╔══██╗██╔══╝  ",
            "   ██║   ╚██████╔╝╚██████╔╝   ██║   ╚██████╔╝██████╔╝███████╗",
            "   ╚═╝    ╚═════╝  ╚═════╝    ╚═╝    ╚═════╝ ╚═════╝ ╚══════╝",
        };
        if (r.W >= 70 && r.H >= 18)
        {
            for (int i = 0; i < big.Length; i++)
                s.TextCenter(r, y + i, big[i], new Style(i < 3 ? Theme.Accent : Theme.AccentSoft, Theme.Bg, Attr.Bold));
            y += big.Length + 1;
            s.TextCenter(r, y++, "T E R M I N A L", new Style(Theme.Dim, Theme.Bg, Attr.Bold));
            y++;
        }
        var app = App;
        string gfx = app.ImageMode == Graphics.RenderMode.Sixel
            ? L.T($"Grafika: Sixel ({app.CellWidth}×{app.CellHeight} px/komórkę)", $"Graphics: Sixel ({app.CellWidth}×{app.CellHeight} px per cell)")
            : app.ImageMode == Graphics.RenderMode.Octant ? L.T("Grafika: oktanty (2×4 na komórkę)", "Graphics: octants (2×4 per cell)") : L.T("Grafika: półbloki (Sixel niedostępny)", "Graphics: half blocks (Sixel unavailable)");
        s.TextCenter(r, y++, gfx, Theme.MutedS);
        y++;

        var hist = app.Library.Data.SearchHistory;
        if (hist.Count > 0 && y < r.Bottom - 2)
        {
            int w = Math.Min(60, r.W - 4);
            int x = r.X + (r.W - w) / 2;
            s.Text(x, y++, L.T("Ostatnie wyszukiwania", "Recent searches"), new Style(Theme.Text, Theme.Bg, Attr.Bold));
            for (int i = 0; i < hist.Count && y < r.Bottom; i++, y++)
            {
                bool sel = !_inputFocused && i == _historyIndex;
                var st = new Style(sel ? Theme.Text : Theme.Dim, sel ? Theme.SelectionFocus : Theme.Bg);
                s.Fill(new Rect(x, y, w, 1), st);
                s.TextTrunc(x + 1, y, "↺  " + hist[i], st, w - 2);
            }
            if (!_inputFocused && y < r.Bottom) s.Text(x, y, L.T("Enter — szukaj ponownie · Del — usuń z historii", "Enter — search again · Del — remove from history"), Theme.MutedS);
        }
        else
        {
            s.TextCenter(r, y++, L.T("Wpisz frazę i naciśnij Enter. Możesz też wkleić link do filmu, kanału lub playlisty.", "Type a query and press Enter. You can also paste a link to a video, channel or playlist."), Theme.DimS);
            s.TextCenter(r, y++, L.T("Tab zmienia filtr (filmy/kanały/playlisty), Shift+Tab — sortowanie, F1 — pomoc.", "Tab changes the filter (videos/channels/playlists), Shift+Tab — sort, F1 — help."), Theme.MutedS);
        }
    }
}
