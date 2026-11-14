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
        s.Text(r.X + 3, by, " T ", Theme.KeyS);
        s.Text(r.X + 7, by, "Tak", st);
        s.Text(r.X + 13, by, " N ", Theme.KeyS);
        s.Text(r.X + 17, by, "Nie", st);
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

    private static readonly (string Section, (string Key, string Text)[] Items)[] Groups =
    [
        ("Ogólne",
        [
            ("1 … 5", "Szukaj · Subskrypcje · Historia · Do obejrzenia · Ustawienia"),
            ("/", "Przejdź do wyszukiwarki"),
            ("Esc / Backspace", "Wstecz"),
            ("q", "Wstecz / wyjście z programu"),
            ("Ctrl+C", "Wyjście"),
            ("Ctrl+L", "Odśwież ekran"),
            ("F1 / ?", "Ta pomoc"),
        ]),
        ("Listy",
        [
            ("↑ ↓  PgUp PgDn  Home End", "Nawigacja (kółko myszy też działa)"),
            ("Enter", "Otwórz film / kanał / playlistę"),
            ("p / Spacja", "Odtwórz od razu"),
            ("a", "Odtwórz tylko dźwięk"),
            ("c", "Kanał autora"),
            ("w", "Dodaj/usuń z „Do obejrzenia”"),
            ("s", "Subskrybuj / anuluj subskrypcję"),
            ("y / o", "Kopiuj link / otwórz w przeglądarce"),
            ("Tab", "Filtr wyników (Wszystko/Filmy/Kanały/Playlisty) lub zakładki"),
            ("Shift+Tab", "Sortowanie"),
        ]),
        ("Odtwarzacz",
        [
            ("Spacja / k", "Pauza / wznów"),
            ("← →", "Przewiń 5 s"),
            ("j l", "Przewiń 10 s"),
            ("PgUp PgDn / Shift+← →", "Przewiń 60 s"),
            ("0 … 9", "Skocz do 0%–90%"),
            ("Home End", "Początek / koniec"),
            ("↑ ↓ / + -", "Głośność"),
            ("m", "Wycisz"),
            (", .", "Poprzedni / następny rozdział"),
            ("n / N", "Następny / poprzedni film z kolejki"),
            ("f", "Tryb pełnoekranowy"),
            ("v", "Sixel ↔ półbloki"),
            ("d", "Paleta adaptacyjna ↔ stała z ditheringiem"),
            ("[ ]", "Rozmiar obrazu"),
            ("a", "Tylko dźwięk"),
            ("i", "Statystyki"),
            ("q / Esc", "Zamknij odtwarzacz"),
        ]),
    ];

    public override void Render(ScreenBuffer s, Rect screen)
    {
        var r = Centered(screen, 84, screen.H - 2);
        Frame(s, r, "Pomoc — skróty klawiszowe");
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
        s.TextRight(r.Right - 3, r.Bottom - 1, " Esc — zamknij ", new Style(Theme.Dim, Theme.Raised));
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
