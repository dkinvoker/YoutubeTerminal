using YoutubeTerminal.Tui;

namespace YoutubeTerminal.UI;

/// <summary>Bazowa klasa ekranu aplikacji.</summary>
internal abstract class View
{
    protected static App App => App.Current;

    public virtual string Title => "";
    /// <summary>Ekran zajmuje cały terminal (bez nagłówka i paska stanu).</summary>
    public virtual bool FullScreen => false;
    /// <summary>Widok przyjmuje teraz wpisywany tekst — skróty globalne (cyfry, q) są wyłączone.</summary>
    public virtual bool CapturesText => false;

    public abstract void Render(ScreenBuffer s, Rect area);
    public virtual bool HandleKey(KeyEvent k) => false;
    public virtual bool HandleMouse(MouseEvent m) => false;
    public virtual void HandlePaste(string text) { }
    public virtual void OnShow() { }
    public virtual void OnHide() { }
    public virtual void OnClose() { }
    /// <summary>Wywoływane cyklicznie (~10–20×/s). Zwraca true, gdy trzeba przerysować.</summary>
    public virtual bool Tick() => false;
    public virtual IEnumerable<(string Key, string Label)> Hints => [];

    /// <summary>Lista, na której jest teraz fokus (do pobierania strumieni z wyprzedzeniem).</summary>
    public virtual ItemList? FocusedList => null;
}

/// <summary>Okno modalne (np. potwierdzenie, pomoc).</summary>
internal abstract class Modal
{
    protected static App App => App.Current;
    public abstract void Render(ScreenBuffer s, Rect screen);
    /// <summary>Zwraca true, gdy okno należy zamknąć.</summary>
    public abstract bool HandleKey(KeyEvent k);
    public virtual bool HandleMouse(MouseEvent m) => false;

    protected static Rect Centered(Rect screen, int w, int h)
    {
        w = Math.Min(w, screen.W - 2);
        h = Math.Min(h, screen.H - 2);
        return new Rect(screen.X + (screen.W - w) / 2, screen.Y + (screen.H - h) / 2, w, h);
    }

    protected static void Frame(ScreenBuffer s, Rect r, string title)
    {
        s.Fill(r, new Style(Theme.Text, Theme.Raised));
        s.Box(r, new Style(Theme.Border, Theme.Raised), title, new Style(Theme.Text, Theme.Raised, Attr.Bold));
    }
}

internal sealed class ConfirmModal : Modal
{
    private readonly string _title;
    private readonly string _message;
    private readonly Action _onYes;

    public ConfirmModal(string title, string message, Action onYes)
    {
        _title = title;
        _message = message;
        _onYes = onYes;
    }

    public override void Render(ScreenBuffer s, Rect screen)
    {
        var lines = TextUtil.Wrap(_message, Math.Min(60, screen.W - 8));
        int w = Math.Min(64, screen.W - 4);
        var r = Centered(screen, w, lines.Count + 6);
        Frame(s, r, _title);
        var st = new Style(Theme.Text, Theme.Raised);
        for (int i = 0; i < lines.Count; i++) s.Text(r.X + 3, r.Y + 2 + i, lines[i], st, r.W - 6);
        int by = r.Bottom - 2;
        int x = r.X + 3;
        x += s.Text(x, by, L.T(" T ", " Y "), Theme.KeyS) + 1;
        x += s.Text(x, by, L.T("Tak", "Yes"), st) + 3;
        x += s.Text(x, by, " N ", Theme.KeyS) + 1;
        s.Text(x, by, L.T("Nie", "No"), st);
    }

    public override bool HandleKey(KeyEvent k)
    {
        if (k.Is('t') || k.Is('T') || k.Is('y') || k.Is('Y') || k.Key == Key.Enter)
        {
            _onYes();
            return true;
        }
        return k.Key == Key.Escape || k.Is('n') || k.Is('N') || k.Is('q');
    }
}

internal sealed class HelpModal : Modal
{
    private int _scroll;

    private static (string Section, (string Key, string Text)[] Items)[] Groups =>
    [
        (L.T("Ogólne", "General"),
        [
            ("1 … 5", L.T("Szukaj · Subskrypcje · Historia · Do obejrzenia · Ustawienia", "Search · Subscriptions · History · Watch later · Settings")),
            ("/", L.T("Przejdź do wyszukiwarki", "Go to search")),
            ("Esc / Backspace", L.T("Wstecz", "Back")),
            ("q", L.T("Wstecz / wyjście z programu", "Back / quit")),
            ("Ctrl+C", L.T("Wyjście", "Quit")),
            ("Ctrl+L", L.T("Odśwież ekran", "Redraw the screen")),
            ("F1 / ?", L.T("Ta pomoc", "This help")),
        ]),
        (L.T("Listy", "Lists"),
        [
            ("↑ ↓  PgUp PgDn  Home End", L.T("Nawigacja (kółko myszy też działa)", "Navigate (the mouse wheel works too)")),
            ("Enter", L.T("Otwórz film / kanał / playlistę", "Open a video / channel / playlist")),
            (L.T("p / Spacja", "p / Space"), L.T("Odtwórz od razu", "Play now")),
            ("a", L.T("Odtwórz tylko dźwięk", "Play audio only")),
            ("c", L.T("Kanał autora", "Go to the channel")),
            ("w", L.T("Dodaj/usuń z „Do obejrzenia”", "Add to / remove from Watch later")),
            ("s", L.T("Subskrybuj / anuluj subskrypcję", "Subscribe / unsubscribe")),
            ("y / o", L.T("Kopiuj link / otwórz w przeglądarce", "Copy link / open in the browser")),
            ("Tab", L.T("Filtr wyników (Wszystko/Filmy/Kanały/Playlisty) lub zakładki", "Result filter (All/Videos/Channels/Playlists) or tabs")),
            ("Shift+Tab", L.T("Sortowanie", "Sort order")),
        ]),
        (L.T("Odtwarzacz", "Player"),
        [
            (L.T("Spacja / k", "Space / k"), L.T("Pauza / wznów", "Pause / resume")),
            ("← →", L.T("Przewiń 5 s", "Seek 5 s")),
            ("j l", L.T("Przewiń 10 s", "Seek 10 s")),
            ("PgUp PgDn / Shift+← →", L.T("Przewiń 60 s", "Seek 60 s")),
            ("0 … 9", L.T("Skocz do 0%–90%", "Jump to 0%–90%")),
            ("Home End", L.T("Początek / koniec", "Start / end")),
            ("↑ ↓ / + -", L.T("Głośność", "Volume")),
            ("m", L.T("Wycisz", "Mute")),
            (", .", L.T("Poprzedni / następny rozdział", "Previous / next chapter")),
            ("n / N", L.T("Następny / poprzedni film z kolejki", "Next / previous video in the queue")),
            ("f", L.T("Tryb pełnoekranowy", "Full screen")),
            ("v", L.T("Tryb obrazu: Sixel → oktanty → półbloki", "Picture mode: Sixel → octants → half blocks")),
            ("d", L.T("Paleta adaptacyjna ↔ stała z ditheringiem", "Adaptive palette ↔ fixed palette with dithering")),
            ("[ ]", L.T("Rozmiar obrazu", "Picture size")),
            ("a", L.T("Tylko dźwięk", "Audio only")),
            ("i", L.T("Statystyki", "Statistics")),
            ("q / Esc", L.T("Zamknij odtwarzacz", "Close the player")),
        ]),
    ];

    public override void Render(ScreenBuffer s, Rect screen)
    {
        var r = Centered(screen, 84, screen.H - 2);
        Frame(s, r, L.T("Pomoc — skróty klawiszowe", "Help — keyboard shortcuts"));
        var inner = r.Inset(3, 2);
        var lines = new List<(string, string, bool)>();
        foreach (var g in Groups)
        {
            lines.Add((g.Section, "", true));
            foreach (var it in g.Items) lines.Add((it.Key, it.Text, false));
            lines.Add(("", "", false));
        }
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, lines.Count - inner.H));
        var st = new Style(Theme.Text, Theme.Raised);
        var keySt = new Style(Theme.Warn, Theme.Raised, Attr.Bold);
        var secSt = new Style(Theme.Accent, Theme.Raised, Attr.Bold);
        for (int i = 0; i < inner.H && i + _scroll < lines.Count; i++)
        {
            var (a, b, sec) = lines[i + _scroll];
            if (sec) s.Text(inner.X, inner.Y + i, a, secSt);
            else
            {
                s.TextTrunc(inner.X + 2, inner.Y + i, a, keySt, 26);
                s.TextTrunc(inner.X + 30, inner.Y + i, b, st, inner.W - 30);
            }
        }
        s.TextRight(r.Right - 3, r.Bottom - 1, L.T(" Esc — zamknij ", " Esc — close "), new Style(Theme.Dim, Theme.Raised));
    }

    public override bool HandleKey(KeyEvent k)
    {
        switch (k.Key)
        {
            case Key.Down: _scroll++; return false;
            case Key.Up: _scroll = Math.Max(0, _scroll - 1); return false;
            case Key.PageDown: _scroll += 10; return false;
            case Key.PageUp: _scroll = Math.Max(0, _scroll - 10); return false;
        }
        return true;
    }
}
