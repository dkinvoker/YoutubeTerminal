using YoutubeTerminal.Playback;
using YoutubeTerminal.Storage;
using YoutubeTerminal.Tui;

namespace YoutubeTerminal.UI;

/// <summary>Ustawienia aplikacji (zapisywane w folderze danych aplikacji, settings.json).</summary>
internal sealed class SettingsView : View
{
    /// <summary>Opcja: etykieta i opis są funkcjami, bo zależą od bieżącego języka.</summary>
    private sealed record Option(Func<string> Label, Func<string> Value, Action<int> Change, Func<string> Help);

    private readonly List<Option> _options = new();
    private int _sel;
    private string _ytDlpVersion = "…";
    private bool _updating;

    public override string Title => L.T("Ustawienia", "Settings");

    public SettingsView()
    {
        var s = App.Settings;
        _options.Add(new(() => L.T("Język interfejsu", "Interface language"),
            () => s.UiLanguage switch
            {
                "pl" => "Polski",
                "en" => "English",
                _ => L.T($"Automatycznie ({LangName(L.Detect())})", $"Automatic ({LangName(L.Detect())})"),
            },
            d => { s.UiLanguage = Cycle(["auto", "pl", "en"], s.UiLanguage, d); App.ApplyLanguage(); },
            () => L.T("„Automatycznie” wybiera język systemu. Przy wynikach YouTube ustawionych na „auto” zmienia się też ich język.",
                "“Automatic” follows the system language. When YouTube results are set to “auto”, their language changes too.")));
        _options.Add(new(() => L.T("Tryb grafiki", "Graphics mode"),
            () => s.RenderMode switch
            {
                "sixel" => "Sixel",
                "octant" => L.T("Oktanty 𜴀 (2×4 na komórkę)", "Octants 𜴀 (2×4 per cell)"),
                "halfblock" => L.T("Półbloki ▀ (1×2 na komórkę)", "Half blocks ▀ (1×2 per cell)"),
                _ => L.T($"Automatycznie ({(App.SixelAvailable ? "Sixel" : "półbloki")})", $"Automatic ({(App.SixelAvailable ? "Sixel" : "half blocks")})"),
            },
            d => { s.RenderMode = Cycle(["auto", "sixel", "octant", "halfblock"], s.RenderMode, d); App.ApplyGraphicsSettings(); },
            () => L.T("Sixel daje pełną grafikę (np. Windows Terminal 1.22+, WezTerm, foot). Oktanty to 4× więcej szczegółów niż półbloki; wymagają terminala ze znakami Unicode 16 (np. Windows Terminal 1.25+). Półbloki działają wszędzie.",
                "Sixel gives full graphics (e.g. Windows Terminal 1.22+, WezTerm, foot). Octants have 4× the detail of half blocks and need a terminal with Unicode 16 glyphs (e.g. Windows Terminal 1.25+). Half blocks work everywhere.")));
        _options.Add(new(() => L.T("Miniatury na listach", "List thumbnails"), () => YesNo(s.ListThumbnails),
            _ => { s.ListThumbnails = !s.ListThumbnails; App.Invalidate(); },
            () => L.T("Małe miniatury Sixel obok każdego wyniku (tylko w trybie Sixel).", "Small Sixel thumbnails next to each result (Sixel mode only).")));
        _options.Add(new(() => L.T("Jakość strumienia", "Stream quality"),
            () => s.StreamQuality == 0 ? L.T("Automatycznie (wg rozmiaru okna)", "Automatic (by window size)") : s.StreamQuality + "p",
            d => s.StreamQuality = Cycle([0, 360, 480, 720, 1080], s.StreamQuality, d),
            () => L.T("Wyższa jakość = ostrzejszy obraz, ale więcej danych i obliczeń. Automatycznie dobiera do rozmiaru terminala.",
                "Higher quality means a sharper picture but more data and CPU. Automatic picks it from the terminal size.")));
        _options.Add(new(() => L.T("Limit klatek na sekundę", "Frame rate limit"), () => s.MaxFps + " fps",
            d => s.MaxFps = Cycle([12, 15, 20, 24, 30, 60], s.MaxFps, d),
            () => L.T("Ogranicza liczbę klatek wysyłanych do terminala — zmniejsz, jeśli obraz się zacina.",
                "Limits how many frames are sent to the terminal — lower it if playback stutters.")));
        _options.Add(new(() => L.T("Rozmiar obrazu wideo", "Video size"), () => s.VideoScale + "%",
            d => s.VideoScale = Cycle([30, 40, 50, 60, 70, 80, 90, 100], s.VideoScale, d),
            () => L.T("Mniejszy obraz to mniej danych na klatkę — płynniejsze odtwarzanie w wolniejszych terminalach.",
                "A smaller picture means less data per frame — smoother playback in slower terminals.")));
        _options.Add(new(() => L.T("Maks. szerokość obrazu wideo", "Max video width"),
            () => s.MaxVideoWidth == 0 ? L.T("Bez limitu", "No limit") : s.MaxVideoWidth + " px",
            d => s.MaxVideoWidth = Cycle([480, 640, 800, 960, 1280, 1600, 1920, 0], s.MaxVideoWidth, d),
            () => L.T("Ogranicza rozmiar klatek Sixel w dużych oknach. Większy obraz wymaga szybszego terminala.",
                "Caps the Sixel frame size in large windows. A bigger picture needs a faster terminal.")));
        _options.Add(new(() => L.T("Paleta kolorów", "Color palette"),
            () => s.Dither ? L.T("Stała 252 kolory + dithering", "Fixed 252 colors + dithering") : L.T("Adaptacyjna (median-cut)", "Adaptive (median cut)"),
            _ => s.Dither = !s.Dither,
            () => L.T("Paleta adaptacyjna daje najlepszą jakość; stała z ditheringiem jest stabilniejsza przy gradientach.",
                "The adaptive palette gives the best quality; the fixed one with dithering is steadier on gradients.")));
        _options.Add(new(() => L.T("Kolory w klatce wideo", "Colors per video frame"),
            () => s.VideoColors + (s.VideoColors >= 255 ? L.T(" (maks.)", " (max)") : ""),
            d => s.VideoColors = Cycle([32, 64, 96, 128, 192, 255], s.VideoColors, d),
            () => L.T("Liczba kolorów palety adaptacyjnej dla wideo Sixel. Mniej kolorów = mniejsze klatki i płynniejszy obraz.",
                "Adaptive palette size for Sixel video. Fewer colors mean smaller frames and smoother playback.")));
        _options.Add(new(() => L.T("Autoodtwarzanie", "Autoplay"), () => YesNo(s.Autoplay), _ => s.Autoplay = !s.Autoplay,
            () => L.T("Po zakończeniu filmu odtwarza następny z kolejki lub pierwszy z podobnych.",
                "When a video ends, plays the next one in the queue or the first related video.")));
        _options.Add(new(() => L.T("Wznawianie od miejsca przerwania", "Resume where you left off"), () => YesNo(s.ResumePlayback),
            _ => s.ResumePlayback = !s.ResumePlayback,
            () => L.T("Rozpoczyna odtwarzanie od zapamiętanej pozycji z historii.", "Starts playback from the position saved in your history.")));
        _options.Add(new(() => L.T("Zapisuj historię oglądania", "Save watch history"), () => YesNo(s.SaveHistory),
            _ => s.SaveHistory = !s.SaveHistory,
            () => L.T("Historia jest przechowywana tylko lokalnie.", "History is stored locally only.")));
        _options.Add(new(() => L.T("Synchronizowane wyjście (DEC 2026)", "Synchronized output (DEC 2026)"), () => YesNo(s.SyncOutput),
            _ => s.SyncOutput = !s.SyncOutput,
            () => L.T("Zapobiega rwaniu obrazu w terminalach obsługujących tryb 2026.", "Prevents tearing in terminals that support mode 2026.")));
        _options.Add(new(() => L.T("Język i region wyników", "Results language and region"),
            () => s.Language is "" or "auto"
                ? L.T($"Automatycznie ({s.EffectiveLanguage}-{s.EffectiveRegion})", $"Automatic ({s.EffectiveLanguage}-{s.EffectiveRegion})")
                : $"{s.Language}-{s.Region}",
            d =>
            {
                string cur = s.Language is "" or "auto" ? "auto" : $"{s.Language}-{s.Region}";
                var v = Cycle(["auto", "pl-PL", "en-US", "en-GB", "de-DE", "uk-UA", "cs-CZ", "fr-FR", "es-ES"], cur, d);
                if (v == "auto") { s.Language = "auto"; s.Region = "auto"; }
                else { s.Language = v[..2]; s.Region = v[3..]; }
                App.YT.Language = s.EffectiveLanguage;
                App.YT.Region = s.EffectiveRegion;
            },
            () => L.T("Wpływa na język danych z YouTube (daty, liczby wyświetleń) i lokalne wyniki.",
                "Affects the language of YouTube data (dates, view counts) and local results.")));
        _options.Add(new(() => L.T("Rozmiar komórki (px)", "Cell size (px)"),
            () => s.CellWidth > 0 ? $"{s.CellWidth}×{s.CellHeight}" : L.T($"Automatycznie ({App.CellWidth}×{App.CellHeight})", $"Automatic ({App.CellWidth}×{App.CellHeight})"),
            d =>
            {
                var cur = s.CellWidth > 0 ? $"{s.CellWidth}x{s.CellHeight}" : "auto";
                var v = Cycle(["auto", "8x16", "9x18", "10x20", "11x22", "12x24", "14x28"], cur, d);
                if (v == "auto") { s.CellWidth = 0; s.CellHeight = 0; }
                else { var p = v.Split('x'); s.CellWidth = int.Parse(p[0]); s.CellHeight = int.Parse(p[1]); }
                App.ApplyGraphicsSettings();
            },
            () => L.T("Rozmiar komórki w pikselach dla Sixel. Zmień, jeśli obrazy mają złe proporcje lub nachodzą na tekst.",
                "Cell size in pixels for Sixel. Change it if pictures have the wrong aspect ratio or overlap text.")));
        _options.Add(new(() => "yt-dlp",
            () => _updating ? App.Spinner + L.T(" aktualizacja…", " updating…") : $"{_ytDlpVersion}  " + L.T("(Enter — aktualizuj)", "(Enter — update)"),
            _ => UpdateYtDlp(),
            () => L.T($"Wbudowany yt-dlp pobiera adresy strumieni. Wypakowany do: {Installer.YtDlpPath}",
                $"The bundled yt-dlp resolves stream addresses. Extracted to: {Installer.YtDlpPath}")));
    }

    private static string LangName(Lang l) => l == Lang.Pl ? "polski" : "English";

    public override void OnShow()
    {
        App.RunAsync(ct => YtDlp.VersionAsync(ct), v => _ytDlpVersion = v, _ => _ytDlpVersion = L.T("brak", "missing"), quiet: true);
    }

    public override void OnHide() => App.Settings.Save();

    private void UpdateYtDlp()
    {
        if (_updating) return;
        _updating = true;
        App.RunAsync(ct => YtDlp.UpdateAsync(ct), r =>
        {
            _updating = false;
            App.ShowMessage("yt-dlp: " + r, 8);
            OnShow();
        }, ex => { _updating = false; App.ShowError(L.T("Aktualizacja yt-dlp nie powiodła się: ", "yt-dlp update failed: ") + ex.Message); });
    }

    private static string YesNo(bool b) => b ? L.T("Tak", "Yes") : L.T("Nie", "No");

    private static T Cycle<T>(T[] values, T current, int dir)
    {
        int i = Array.IndexOf(values, current);
        if (i < 0) i = 0;
        else i = (i + (dir < 0 ? -1 : 1) + values.Length) % values.Length;
        return values[i];
    }

    public override IEnumerable<(string Key, string Label)> Hints
    {
        get
        {
            yield return ("↑↓", L.T("Wybór", "Select"));
            yield return ("← → Enter", L.T("Zmień", "Change"));
        }
    }

    public override bool HandleKey(KeyEvent k)
    {
        switch (k.Key)
        {
            case Key.Up: _sel = Math.Max(0, _sel - 1); return true;
            case Key.Down: _sel = Math.Min(_options.Count - 1, _sel + 1); return true;
            case Key.Left: _options[_sel].Change(-1); App.Settings.Save(); return true;
            case Key.Right:
            case Key.Enter: _options[_sel].Change(1); App.Settings.Save(); return true;
        }
        if (k.Is(' ')) { _options[_sel].Change(1); App.Settings.Save(); return true; }
        return false;
    }

    public override bool HandleMouse(MouseEvent m)
    {
        if (m.Kind == MouseKind.Press && m.Y >= _listY && m.Y < _listY + _options.Count)
        {
            int idx = m.Y - _listY;
            if (idx == _sel) { _options[_sel].Change(1); App.Settings.Save(); }
            _sel = idx;
            return true;
        }
        return false;
    }

    private int _listY;

    public override void Render(ScreenBuffer s, Rect area)
    {
        Layouts.SectionHeader(s, area, L.T("Ustawienia", "Settings"), L.T("zmiany są zapisywane od razu", "changes are saved immediately"));
        int y = area.Y + 4;
        _listY = y;
        int labelW = 36;
        int w = Math.Min(area.W - 4, 116);
        for (int i = 0; i < _options.Count && y < area.Bottom - 6; i++, y++)
        {
            var o = _options[i];
            bool sel = i == _sel;
            var st = new Style(Theme.Text, sel ? Theme.SelectionFocus : Theme.Bg, sel ? Attr.Bold : Attr.None);
            s.Fill(new Rect(area.X + 2, y, w, 1), st);
            if (sel) s.Put(area.X + 2, y, "▌", new Style(Theme.Accent, st.Bg));
            s.TextTrunc(area.X + 4, y, o.Label(), st, labelW);
            s.TextTrunc(area.X + 4 + labelW, y, (sel ? "◀ " : "  ") + o.Value() + (sel ? " ▶" : ""), new Style(sel ? Theme.Text : Theme.Dim, st.Bg), w - labelW - 4);
        }

        y += 1;
        if (y < area.Bottom - 1)
            y += s.Paragraph(new Rect(area.X + 4, y, w - 4, 3), _options[_sel].Help(), Theme.DimS) + 1;

        string cellSource = App.Term.CellSizeFromTerminal ? L.T(" (zgłoszona przez terminal)", " (reported by the terminal)") : L.T(" (domyślna)", " (default)");
        var info = new List<string>
        {
            L.T($"Wersja: {AppInfo.Title}", $"Version: {AppInfo.Title}"),
            L.T($"Terminal: {App.Term.Width}×{App.Term.Height} komórek, komórka {App.CellWidth}×{App.CellHeight} px{cellSource}, Sixel: {(App.SixelAvailable ? "tak" : "nie")}",
                $"Terminal: {App.Term.Width}×{App.Term.Height} cells, cell {App.CellWidth}×{App.CellHeight} px{cellSource}, Sixel: {(App.SixelAvailable ? "yes" : "no")}"),
            L.T($"Dane aplikacji: {AppPaths.Root}", $"App data: {AppPaths.Root}"),
            L.T($"Deinstalator: {Path.Combine(Path.GetDirectoryName(AppPaths.ExePath) ?? "", Installer.UninstallerName)}",
                $"Uninstaller: {Path.Combine(Path.GetDirectoryName(AppPaths.ExePath) ?? "", Installer.UninstallerName)}"),
        };
        foreach (var line in info)
        {
            if (y >= area.Bottom) break;
            s.TextTrunc(area.X + 4, y++, line, Theme.MutedS, area.W - 6);
        }
    }
}
